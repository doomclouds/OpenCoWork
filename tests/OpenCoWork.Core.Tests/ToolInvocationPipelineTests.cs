using System.Security.Cryptography;
using System.Text.Json;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Logging;
using OpenCoWork.Core.Sessions;
using OpenCoWork.Core.Tools;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class ToolInvocationPipelineTests
{
    [Fact]
    public async Task Completed_call_follows_the_fixed_pipeline_and_redacts_output()
    {
        using var schema = JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "path": { "type": "string" }
              },
              "required": [ "path" ],
              "additionalProperties": false
            }
            """);
        using var arguments = JsonDocument.Parse("""{"path":"src"}""");
        var bindingInvocations = 0;
        var definition = new ToolDefinition(
            new ToolDefinitionId(
                ToolSourceKind.CoreNative,
                "opencowork.core",
                "test.read"),
            new ToolName("test", "read"),
            "Read test data.",
            schema.RootElement,
            ToolEffect.None,
            ToolReplaySafety.Safe);
        var bindingId = new RuntimeBindingId("core.test.read.v1");
        var registration = new ToolRegistration(
            definition,
            bindingId,
            ToolExposure.Direct,
            ToolInvocationAudience.Model);
        var binding = new ToolRuntimeBinding(
            bindingId,
            ToolBindingAvailability.Available,
            Lease: null,
            TimeSpan.FromSeconds(30),
            (_, _) =>
            {
                bindingInvocations++;
                using var output = JsonDocument.Parse(
                    """
                    {
                      "items": [
                        {
                          "api_key": "raw-secret",
                          "value": "top-secret"
                        }
                      ]
                    }
                    """);
                return ValueTask.FromResult(
                    ToolBindingResult.Success(output.RootElement));
            });
        var runtime = new ToolRuntime([registration], [binding]);
        var snapshot = runtime.BuildSnapshot(AgentMode.Agent, new ToolsConfig());
        var argumentsSha256 = Sha256(ThreadJournal.Canonicalize(
            arguments.RootElement));
        var context = new ToolInvocationContext(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            CallIndex: 0,
            "call-1",
            "test__read",
            arguments.RootElement,
            argumentsSha256,
            SensitiveInputDetected: false,
            snapshot);
        var sink = new RecordingSink();
        var trace = new ToolInvocationTrace();
        ToolResultSnapshot? observedTerminal = null;
        var pipeline = new ToolInvocationPipeline(
            runtime,
            new SecretRedactor(["top-secret"]),
            trace: trace,
            terminal: (result, _) =>
            {
                observedTerminal = result;
                return ValueTask.CompletedTask;
            });

        var result = await pipeline.InvokeAsync(
            context,
            sink,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                ToolInvocationStage.SnapshotLookup,
                ToolInvocationStage.Started,
                ToolInvocationStage.AudienceExposureMode,
                ToolInvocationStage.BindingAvailabilityLease,
                ToolInvocationStage.Authority,
                ToolInvocationStage.InputSchema,
                ToolInvocationStage.Policy,
                ToolInvocationStage.PreToolUse,
                ToolInvocationStage.Approval,
                ToolInvocationStage.Invoke,
                ToolInvocationStage.ResultNormalize,
                ToolInvocationStage.Terminal,
                ToolInvocationStage.TerminalHook,
            ],
            trace.Stages);
        Assert.Equal(1, bindingInvocations);
        Assert.Same(result, observedTerminal);
        Assert.Equal(ToolInvocationStatus.Completed, result.Status);
        Assert.False(result.IsTruncated);
        Assert.Equal(
            SecretRedactor.Replacement,
            result.Output?
                .GetProperty("items")[0]
                .GetProperty("api_key")
                .GetString());
        Assert.Equal(
            SecretRedactor.Replacement,
            result.Output?
                .GetProperty("items")[0]
                .GetProperty("value")
                .GetString());
        Assert.DoesNotContain(
            "top-secret",
            result.Output?.GetRawText(),
            StringComparison.Ordinal);
        Assert.Equal(
            Sha256(ThreadJournal.Canonicalize(result.Output!.Value)),
            result.ResultSha256);
        Assert.Collection(
            sink.Intents,
            intent => Assert.IsType<RecordToolInvocationStartedIntent>(intent),
            intent => Assert.IsType<RecordToolInvocationAttemptStartedIntent>(intent),
            intent =>
            {
                var terminal =
                    Assert.IsType<RecordToolInvocationTerminalIntent>(intent);
                Assert.Same(result, terminal.Result);
            });
    }

    [Theory]
    [InlineData("not-found", ToolErrorCodes.NotFound)]
    [InlineData("audience", ToolErrorCodes.AudienceDenied)]
    [InlineData("exposure", ToolErrorCodes.ExposureDenied)]
    [InlineData("mode", ToolErrorCodes.ModeDenied)]
    [InlineData("binding", ToolErrorCodes.BindingUnavailable)]
    [InlineData("lease", ToolErrorCodes.LeaseExpired)]
    [InlineData("authority", ToolErrorCodes.AuthorityDenied)]
    [InlineData("runtime-authority", ToolErrorCodes.AuthorityDenied)]
    [InlineData("schema", ToolErrorCodes.InputInvalid)]
    [InlineData("policy", ToolErrorCodes.PolicyDenied)]
    [InlineData("hook-deny", ToolErrorCodes.HookDenied)]
    [InlineData("hook-failure", ToolErrorCodes.HookFailed)]
    [InlineData("approval", ToolErrorCodes.ApprovalDenied)]
    [InlineData("sensitive", ToolErrorCodes.SensitiveInputRejected)]
    [InlineData("input-limit", ToolErrorCodes.InputTooLarge)]
    public async Task Pre_invoke_rejections_have_stable_codes(
        string scenario,
        string expectedCode)
    {
        var policyDeniedEffects = ToolEffect.None;
        IReadOnlyList<ToolAuthorityPolicy>? runtimeAuthority = null;
        ToolPreUseHook? hook = null;
        Harness harness;
        switch (scenario)
        {
            case "audience":
                harness = CreateHarness(
                    audience: ToolInvocationAudience.Host);
                break;
            case "exposure":
                harness = CreateHarness(exposure: ToolExposure.Hidden);
                break;
            case "mode":
                harness = CreateHarness(
                    effects: ToolEffect.WorkspaceWrite,
                    mode: AgentMode.Plan);
                break;
            case "binding":
                harness = CreateHarness(
                    availability: ToolBindingAvailability.Unavailable);
                break;
            case "lease":
                harness = CreateHarness(
                    leaseExpiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
                break;
            case "authority":
                harness = CreateHarness(
                    effects: ToolEffect.WorkspaceRead,
                    authority: ToolAuthorityDecision.Deny);
                break;
            case "runtime-authority":
                harness = CreateHarness(effects: ToolEffect.WorkspaceRead);
                runtimeAuthority =
                [
                    new(
                        ToolEffect.WorkspaceRead,
                        ToolAuthorityDecision.Deny),
                ];
                break;
            case "schema":
                harness = CreateHarness(argumentsJson: """{"path":1}""");
                break;
            case "policy":
                harness = CreateHarness(effects: ToolEffect.WorkspaceRead);
                policyDeniedEffects = ToolEffect.WorkspaceRead;
                break;
            case "hook-deny":
                harness = CreateHarness();
                hook = (_, _) => ValueTask.FromResult(
                    new ToolPreUseDecision(ToolAuthorityDecision.Deny));
                break;
            case "hook-failure":
                harness = CreateHarness();
                hook = (_, _) => ValueTask.FromException<ToolPreUseDecision>(
                    new InvalidOperationException("secret exception"));
                break;
            case "approval":
                harness = CreateHarness(
                    effects: ToolEffect.WorkspaceRead,
                    authority: ToolAuthorityDecision.RequireApproval);
                harness = harness with
                {
                    Context = harness.Context with { ApprovalGranted = false },
                };
                break;
            case "sensitive":
                harness = CreateHarness();
                harness = harness with
                {
                    Context = harness.Context with
                    {
                        SensitiveInputDetected = true,
                    },
                };
                break;
            case "input-limit":
                harness = CreateHarness(
                    argumentsJson:
                    JsonSerializer.Serialize(new
                    {
                        path = new string(
                            'x',
                            ToolRuntimeLimits.MaximumArgumentsBytes),
                    }));
                break;
            default:
                harness = CreateHarness();
                harness = harness with
                {
                    Context = harness.Context with
                    {
                        ProviderToolName = "missing__tool",
                    },
                };
                break;
        }

        var sink = new RecordingSink();
        var pipeline = new ToolInvocationPipeline(
            harness.Runtime,
            new SecretRedactor([]),
            runtimeAuthority,
            policyDeniedEffects: policyDeniedEffects,
            preToolUse: hook);

        var result = await pipeline.InvokeAsync(
            harness.Context,
            sink,
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolInvocationStatus.Rejected, result.Status);
        Assert.Equal(expectedCode, result.Error?.Code);
        Assert.False(result.Error?.IsRetryable);
        Assert.Equal(0, harness.Counter.Value);
        Assert.Collection(
            sink.Intents,
            intent => Assert.IsType<RecordToolInvocationStartedIntent>(intent),
            intent => Assert.IsType<RecordToolInvocationTerminalIntent>(intent));
    }

    [Fact]
    public async Task Approval_suspends_once_and_approved_resume_invokes_the_binding()
    {
        var harness = CreateHarness(
            effects: ToolEffect.WorkspaceRead,
            authority: ToolAuthorityDecision.RequireApproval);
        var checkpoint =
            SessionExecutionCheckpointCodec.Create("agent-runtime", 1, "{}");
        var pending = harness.Context with
        {
            ApprovalCheckpoint = checkpoint,
            ApprovalTimeoutAt = DateTimeOffset.UtcNow.AddMinutes(5),
        };
        var pipeline = new ToolInvocationPipeline(
            harness.Runtime,
            new SecretRedactor([]),
            preToolUse: (_, _) => ValueTask.FromResult(
                new ToolPreUseDecision(ToolAuthorityDecision.Allow)));
        var waitingSink = new RecordingSink();

        var suspended = await Assert.ThrowsAsync<ToolInvocationSuspendedException>(
            async () => await pipeline.InvokeAsync(
                pending,
                waitingSink,
                TestContext.Current.CancellationToken));

        Assert.Equal(harness.Context.ToolInvocationId, suspended.ToolInvocationId);
        Assert.Equal(0, harness.Counter.Value);
        Assert.Collection(
            waitingSink.Intents,
            intent => Assert.IsType<RecordToolInvocationStartedIntent>(intent),
            intent =>
            {
                var waiting = Assert.IsType<WaitForInteractionIntent>(intent);
                Assert.Equal(
                    harness.Context.ToolInvocationId,
                    waiting.ToolInvocationId);
                var request =
                    Assert.IsType<ToolApprovalRequestContent>(waiting.Request);
                Assert.Equal(harness.Context.ToolInvocationId, request.ToolInvocationId);
                Assert.Equal(harness.Context.ArgumentsSha256, request.ArgumentsSha256);
            });

        var resumedSink = new RecordingSink();
        var result = await pipeline.InvokeAsync(
            pending with { ApprovalGranted = true },
            resumedSink,
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolInvocationStatus.Completed, result.Status);
        Assert.Equal(1, harness.Counter.Value);
        Assert.Collection(
            resumedSink.Intents,
            intent => Assert.IsType<RecordToolInvocationStartedIntent>(intent),
            intent => Assert.Equal(
                1,
                Assert.IsType<RecordToolInvocationAttemptStartedIntent>(intent)
                    .AttemptNumber),
            intent => Assert.IsType<RecordToolInvocationTerminalIntent>(intent));
    }

    [Fact]
    public async Task Unsafe_interrupted_attempt_becomes_outcome_unknown_without_replay()
    {
        var harness = CreateHarness(replaySafety: ToolReplaySafety.Unsafe);
        var sink = new RecordingSink();
        var pipeline = new ToolInvocationPipeline(
            harness.Runtime,
            new SecretRedactor([]));

        var result = await pipeline.InvokeAsync(
            harness.Context with { PriorAttemptCount = 1 },
            sink,
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolInvocationStatus.OutcomeUnknown, result.Status);
        Assert.Equal(ToolErrorCodes.OutcomeUnknown, result.Error?.Code);
        Assert.Equal(0, harness.Counter.Value);
        Assert.DoesNotContain(
            sink.Intents,
            intent => intent is RecordToolInvocationAttemptStartedIntent);
    }

    [Fact]
    public async Task Safe_interrupted_attempt_replays_once_with_attempt_two()
    {
        var harness = CreateHarness(replaySafety: ToolReplaySafety.Safe);
        var sink = new RecordingSink();
        var pipeline = new ToolInvocationPipeline(
            harness.Runtime,
            new SecretRedactor([]));

        var result = await pipeline.InvokeAsync(
            harness.Context with { PriorAttemptCount = 1 },
            sink,
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolInvocationStatus.Completed, result.Status);
        Assert.Equal(1, harness.Counter.Value);
        Assert.Equal(
            2,
            Assert.Single(
                sink.Intents.OfType<RecordToolInvocationAttemptStartedIntent>())
                .AttemptNumber);
    }

    [Fact]
    public async Task Safe_second_interrupted_attempt_becomes_outcome_unknown()
    {
        var harness = CreateHarness(replaySafety: ToolReplaySafety.Safe);
        var sink = new RecordingSink();
        var pipeline = new ToolInvocationPipeline(
            harness.Runtime,
            new SecretRedactor([]));

        var result = await pipeline.InvokeAsync(
            harness.Context with { PriorAttemptCount = 2 },
            sink,
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolInvocationStatus.OutcomeUnknown, result.Status);
        Assert.Equal(0, harness.Counter.Value);
    }

    [Fact]
    public async Task Caller_cancellation_wins_over_the_linked_deadline()
    {
        using var callerCancellation = new CancellationTokenSource();
        var harness = CreateHarness(
            executor: (_, token) =>
            {
                callerCancellation.Cancel();
                return ValueTask.FromCanceled<ToolBindingResult>(token);
            });
        var pipeline = new ToolInvocationPipeline(
            harness.Runtime,
            new SecretRedactor([]));

        var result = await pipeline.InvokeAsync(
            harness.Context,
            new RecordingSink(),
            callerCancellation.Token);

        Assert.Equal(ToolInvocationStatus.Cancelled, result.Status);
        Assert.Equal(ToolErrorCodes.Cancelled, result.Error?.Code);
        Assert.Equal(1, harness.Counter.Value);
    }

    [Fact]
    public async Task Binding_deadline_becomes_a_single_timed_out_terminal()
    {
        var harness = CreateHarness(
            timeout: TimeSpan.FromMilliseconds(20),
            executor: async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("unreachable");
            });
        var sink = new RecordingSink();
        var pipeline = new ToolInvocationPipeline(
            harness.Runtime,
            new SecretRedactor([]));

        var result = await pipeline.InvokeAsync(
            harness.Context,
            sink,
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolInvocationStatus.TimedOut, result.Status);
        Assert.Equal(ToolErrorCodes.Timeout, result.Error?.Code);
        Assert.Single(
            sink.Intents.OfType<RecordToolInvocationTerminalIntent>());
    }

    [Fact]
    public async Task Large_redacted_output_is_truncated_and_hard_limit_fails()
    {
        var previewPayload = new string(
            '中',
            ToolRuntimeLimits.MaximumResultEnvelopeBytes / 2);
        var previewHarness = CreateHarness(
            executor: SuccessOutput(new { value = previewPayload }));
        var pipeline = new ToolInvocationPipeline(
            previewHarness.Runtime,
            new SecretRedactor([]));

        var preview = await pipeline.InvokeAsync(
            previewHarness.Context,
            new RecordingSink(),
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolInvocationStatus.Completed, preview.Status);
        Assert.True(preview.IsTruncated);
        Assert.True(
            ThreadJournal.Canonicalize(preview.Output!.Value).Length <=
            ToolRuntimeLimits.MaximumResultEnvelopeBytes - 4 * 1024);
        Assert.True(
            JsonSerializer.SerializeToUtf8Bytes(preview).Length <=
            ToolRuntimeLimits.MaximumResultEnvelopeBytes);

        var oversizedValue = new
        {
            value = new string(
                'x',
                ToolRuntimeLimits.MaximumBindingResultBytes + 1),
        };
        var oversizedCanonical = ThreadJournal.Canonicalize(
            JsonSerializer.SerializeToElement(oversizedValue));
        var oversizedHarness = CreateHarness(
            executor: SuccessOutput(oversizedValue));
        var oversized = await new ToolInvocationPipeline(
                oversizedHarness.Runtime,
                new SecretRedactor([]))
            .InvokeAsync(
                oversizedHarness.Context,
                new RecordingSink(),
                TestContext.Current.CancellationToken);

        Assert.Equal(ToolInvocationStatus.Failed, oversized.Status);
        Assert.Equal(ToolErrorCodes.OutputLimitExceeded, oversized.Error?.Code);
        Assert.Null(oversized.Output);
        Assert.Equal(oversizedCanonical.Length, oversized.OriginalByteCount);
        Assert.Equal(Sha256(oversizedCanonical), oversized.ResultSha256);
    }

    [Fact]
    public async Task Ambiguous_binding_failure_is_terminal_outcome_unknown()
    {
        var harness = CreateHarness(
            executor: (_, _) => ValueTask.FromResult(
                ToolBindingResult.Failure(new SessionError(
                    ToolErrorCodes.OutcomeUnknown,
                    "File write outcome is unknown.",
                    IsRetryable: false))));

        var result = await new ToolInvocationPipeline(
                harness.Runtime,
                new SecretRedactor([]))
            .InvokeAsync(
                harness.Context,
                new RecordingSink(),
                TestContext.Current.CancellationToken);

        Assert.Equal(ToolInvocationStatus.OutcomeUnknown, result.Status);
        Assert.Equal(ToolErrorCodes.OutcomeUnknown, result.Error?.Code);
        Assert.Null(result.Output);
    }

    [Fact]
    public async Task Escaped_exceptions_do_not_leak_and_terminal_hook_cannot_rewrite_result()
    {
        var failedHarness = CreateHarness(
            executor: (_, _) => ValueTask.FromException<ToolBindingResult>(
                new InvalidOperationException("top-secret stack data")));
        var failed = await new ToolInvocationPipeline(
                failedHarness.Runtime,
                new SecretRedactor(["top-secret"]))
            .InvokeAsync(
                failedHarness.Context,
                new RecordingSink(),
                TestContext.Current.CancellationToken);

        Assert.Equal(ToolInvocationStatus.Failed, failed.Status);
        Assert.Equal(ToolErrorCodes.ExecutionFailed, failed.Error?.Code);
        Assert.DoesNotContain(
            "top-secret",
            JsonSerializer.Serialize(failed),
            StringComparison.Ordinal);

        var completedHarness = CreateHarness();
        var completedSink = new RecordingSink();
        var completed = await new ToolInvocationPipeline(
                completedHarness.Runtime,
                new SecretRedactor([]),
                terminal: (_, _) => ValueTask.FromException(
                    new InvalidOperationException("terminal failure")))
            .InvokeAsync(
                completedHarness.Context,
                completedSink,
                TestContext.Current.CancellationToken);

        Assert.Equal(ToolInvocationStatus.Completed, completed.Status);
        Assert.Single(
            completedSink.Intents.OfType<RecordToolInvocationTerminalIntent>());
    }

    [Fact]
    public async Task Recovery_validates_arguments_against_the_frozen_schema()
    {
        var harness = CreateHarness(runtimeSchemaRequiresNumber: true);

        var result = await new ToolInvocationPipeline(
                harness.Runtime,
                new SecretRedactor([]))
            .InvokeAsync(
                harness.Context,
                new RecordingSink(),
                TestContext.Current.CancellationToken);

        Assert.Equal(ToolInvocationStatus.Completed, result.Status);
        Assert.Equal(1, harness.Counter.Value);
    }

    [Fact]
    public async Task Duplicate_replay_and_conflict_never_invoke_the_binding()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = CreateHarness();
        var pipeline = new ToolInvocationPipeline(
            harness.Runtime,
            new SecretRedactor([]));
        using var output = JsonDocument.Parse("""{"ok":true}""");
        var replay = new ToolResultSnapshot(
            Guid.CreateVersion7(),
            "call-1",
            ToolInvocationStatus.Completed,
            output.RootElement,
            Error: null,
            IsTruncated: false,
            OriginalByteCount: 11,
            new string('b', 64),
            AttemptCount: 1);

        var replayResult = await pipeline.InvokeAsync(
            harness.Context with { ReplayResult = replay },
            new RecordingSink(),
            cancellationToken);
        var conflictResult = await pipeline.InvokeAsync(
            harness.Context with
            {
                ToolInvocationId = Guid.CreateVersion7(),
                ProviderCallIdConflict = true,
            },
            new RecordingSink(),
            cancellationToken);

        Assert.Equal(0, harness.Counter.Value);
        Assert.Equal(ToolInvocationStatus.Completed, replayResult.Status);
        Assert.Equal(replay.ResultSha256, replayResult.ResultSha256);
        Assert.Equal(ToolInvocationStatus.Rejected, conflictResult.Status);
        Assert.Equal(ToolErrorCodes.CallIdConflict, conflictResult.Error!.Code);
    }

    private static Harness CreateHarness(
        ToolEffect effects = ToolEffect.None,
        ToolReplaySafety replaySafety = ToolReplaySafety.Safe,
        ToolInvocationAudience audience = ToolInvocationAudience.Model,
        ToolExposure exposure = ToolExposure.Direct,
        AgentMode mode = AgentMode.Agent,
        ToolBindingAvailability availability = ToolBindingAvailability.Available,
        DateTimeOffset? leaseExpiresAt = null,
        ToolAuthorityDecision authority = ToolAuthorityDecision.Allow,
        string argumentsJson = """{"path":"src"}""",
        TimeSpan? timeout = null,
        ToolExecutor? executor = null,
        bool runtimeSchemaRequiresNumber = false)
    {
        using var schema = JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "path": { "type": "string" }
              },
              "required": [ "path" ],
              "additionalProperties": false
            }
            """);
        using var arguments = JsonDocument.Parse(argumentsJson);
        var definition = new ToolDefinition(
            new ToolDefinitionId(
                ToolSourceKind.CoreNative,
                "opencowork.core",
                "test.read"),
            new ToolName("test", "read"),
            "Read test data.",
            schema.RootElement,
            effects,
            replaySafety);
        var bindingId = new RuntimeBindingId("core.test.read.v1");
        var registration = new ToolRegistration(
            definition,
            bindingId,
            exposure,
            audience);
        using var runtimeSchema = runtimeSchemaRequiresNumber
            ? JsonDocument.Parse(
                schema.RootElement
                    .GetRawText()
                    .Replace(
                        "\"string\"",
                        "\"number\"",
                        StringComparison.Ordinal))
            : null;
        var runtimeDefinition = runtimeSchema is null
            ? definition
            : new ToolDefinition(
                definition.Id,
                definition.Name,
                definition.Description,
                runtimeSchema.RootElement,
                definition.Effects,
                definition.ReplaySafety);
        var runtimeRegistration = registration with
        {
            Definition = runtimeDefinition,
        };
        var counter = new InvocationCounter();
        var implementation = executor ?? SuccessOutput(new { ok = true });
        var binding = new ToolRuntimeBinding(
            bindingId,
            availability,
            leaseExpiresAt is null
                ? null
                : new ToolBindingLease("lease-1", leaseExpiresAt),
            timeout ?? TimeSpan.FromSeconds(30),
            async (value, token) =>
            {
                counter.Value++;
                return await implementation(value, token);
            });
        var runtime = new ToolRuntime([runtimeRegistration], [binding]);
        var policies = new[]
        {
            new ToolAuthorityPolicy(ToolEffect.None, authority),
            new ToolAuthorityPolicy(ToolEffect.WorkspaceRead, authority),
            new ToolAuthorityPolicy(ToolEffect.WorkspaceWrite, authority),
            new ToolAuthorityPolicy(ToolEffect.ProcessExecution, authority),
            new ToolAuthorityPolicy(ToolEffect.NetworkRead, authority),
            new ToolAuthorityPolicy(ToolEffect.ExternalMutation, authority),
        };
        var snapshot = new EffectiveToolSnapshot(
            schemaVersion: 1,
            mode,
            policies,
            [registration],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["test.read"] = "test__read",
            },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["test__read"] = "test.read",
            },
            diagnostics: [],
            new string('a', 64));
        var argumentValue = arguments.RootElement.Clone();
        var context = new ToolInvocationContext(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            CallIndex: 0,
            "call-1",
            "test__read",
            argumentValue,
            Sha256(ThreadJournal.Canonicalize(argumentValue)),
            SensitiveInputDetected: false,
            snapshot);
        return new Harness(runtime, context, counter);
    }

    private static ToolExecutor SuccessOutput<T>(T value) =>
        (_, _) => ValueTask.FromResult(
            ToolBindingResult.Success(
                JsonSerializer.SerializeToElement(value)));

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record Harness(
        ToolRuntime Runtime,
        ToolInvocationContext Context,
        InvocationCounter Counter);

    private sealed class InvocationCounter
    {
        public int Value { get; set; }
    }

    private sealed class RecordingSink : ISessionExecutionSink
    {
        public List<SessionExecutionIntent> Intents { get; } = [];

        public ValueTask EmitAsync(
            SessionExecutionIntent intent,
            CancellationToken cancellationToken = default)
        {
            Intents.Add(intent);
            return ValueTask.CompletedTask;
        }
    }
}
