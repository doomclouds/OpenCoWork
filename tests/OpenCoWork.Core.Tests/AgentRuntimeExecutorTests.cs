using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Agents;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Logging;
using OpenCoWork.Core.Tools;
using OpenCoWork.Core.Workspaces;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class AgentRuntimeExecutorTests
{
    [Fact]
    public async Task Workspace_instructions_come_from_the_calling_thread_execution_root()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var origin = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-instructions-origin-{Guid.NewGuid():N}");
        var worker = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-instructions-worker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(origin);
        Directory.CreateDirectory(worker);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(origin, "AGENTS.md"),
                "origin\n",
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(worker, "AGENTS.md"),
                "worker\n",
                cancellationToken);
            var workspace = new ExecutionWorkspaceDescriptor(
                CoWorkWorkspaceMode.Project,
                worker,
                Path.Combine(worker, "scratchpad"),
                WorktreeId: null,
                WorktreeRoot: null,
                BaseCommitSha: null);
            var sink = new RecordingSink();

            await Executor(origin, "secret", _ => new ScriptedClient())
                .ExecuteAsync(Session(workspace), sink, cancellationToken);

            var snapshot = Assert.Single(
                sink.Intents.OfType<RecordAgentInvocationSnapshotIntent>()).Snapshot;
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("worker\n")))
                    .ToLowerInvariant(),
                snapshot.WorkspaceInstructions!.ContentSha256);
        }
        finally
        {
            Directory.Delete(origin, recursive: true);
            Directory.Delete(worker, recursive: true);
        }
    }

    [Fact]
    public async Task Commits_snapshot_first_flushes_first_visible_delta_and_finishes_once()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string secret = "executor-secret-71d85a";
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-executor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var executor = Executor(
                directory,
                secret,
                _ => new ScriptedClient());
            var sink = new RecordingSink();

            await executor.ExecuteAsync(Session(), sink, cancellationToken);

            Assert.IsType<RecordAgentInvocationSnapshotIntent>(sink.Intents[0]);
            var contentStart = Assert.IsType<StartItemIntent>(sink.Intents[1]);
            Assert.Equal(SessionItemType.AgentMessage, contentStart.Type);
            var firstDelta = Assert.IsType<AppendItemDeltaIntent>(sink.Intents[2]);
            Assert.True(firstDelta.Flush);
            var reasoningStart = Assert.IsType<StartItemIntent>(sink.Intents[3]);
            Assert.Equal(SessionItemType.Reasoning, reasoningStart.Type);
            Assert.False(
                Assert.IsType<AppendItemDeltaIntent>(sink.Intents[4]).Flush);
            Assert.Contains(
                sink.Intents,
                intent => intent is RecordProviderUsageIntent);
            Assert.IsType<CompleteTurnIntent>(sink.Intents[^1]);
            Assert.Single(
                sink.Intents,
                intent => intent is CompleteTurnIntent or FailTurnIntent);
            Assert.DoesNotContain(
                secret,
                JsonSerializer.Serialize(sink.Intents),
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Retries_only_before_the_first_visible_delta_is_committed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-retry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var beforeVisible = new TransientClient(failAfterDelta: false);
            var beforeVisibleSink = new RecordingSink();
            await Executor(
                    directory,
                    "secret",
                    _ => beforeVisible)
                .ExecuteAsync(Session(), beforeVisibleSink, cancellationToken);

            Assert.Equal(2, beforeVisible.Attempts);
            Assert.IsType<CompleteTurnIntent>(beforeVisibleSink.Intents[^1]);
            var estimatedUsage = Assert.Single(
                beforeVisibleSink.Intents,
                intent => intent is RecordProviderUsageIntent);
            Assert.True(
                Assert.IsType<RecordProviderUsageIntent>(estimatedUsage)
                    .Usage.IsEstimate);

            var afterVisible = new TransientClient(failAfterDelta: true);
            var afterVisibleSink = new RecordingSink();
            await Executor(
                    directory,
                    "secret",
                    _ => afterVisible)
                .ExecuteAsync(Session(), afterVisibleSink, cancellationToken);

            Assert.Equal(1, afterVisible.Attempts);
            Assert.IsType<FailTurnIntent>(afterVisibleSink.Intents[^1]);
            Assert.Single(
                afterVisibleSink.Intents,
                intent => intent is RecordAgentInvocationSnapshotIntent);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Executes_each_tool_frame_in_order_and_continues_the_same_invocation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-tool-loop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var client = new ToolLoopClient();
            var sink = new RecordingSink();

            await Executor(
                    directory,
                    "secret",
                    _ => client)
                .ExecuteAsync(Session(), sink, cancellationToken);

            Assert.Equal(2, client.Requests.Count);
            Assert.Equal(
                client.Requests[0].InvocationId,
                client.Requests[1].InvocationId);
            Assert.Equal([1, 2], client.Requests.Select(item => item.AttemptNumber));
            Assert.Equal(
                [
                    ChatCompletionMessageRole.Assistant,
                    ChatCompletionMessageRole.Tool,
                    ChatCompletionMessageRole.Tool,
                ],
                client.Requests[1].Messages.TakeLast(3).Select(message => message.Role));
            Assert.Equal(
                ["call-1", "call-2"],
                client.Requests[1].Messages.TakeLast(2)
                    .Select(message => message.ToolCallId));

            var toolCall = Assert.Single(
                sink.Intents.OfType<RecordToolCallIntent>());
            Assert.Equal(1, toolCall.Content.ProviderRound);
            Assert.NotNull(toolCall.Content.AgentMessageItemId);
            Assert.Equal(
                ["call-1", "call-2"],
                toolCall.Content.Calls.Select(call => call.ProviderToolCallId));
            Assert.Equal(
                ["call-1", "call-2"],
                sink.Intents
                    .OfType<RecordToolInvocationTerminalIntent>()
                    .Select(intent => intent.Result.ProviderToolCallId));
            Assert.IsType<CompleteTurnIntent>(sink.Intents[^1]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Deferred_tool_activation_changes_the_next_provider_round()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-deferred-loop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var runtime = new ToolRuntime(new OpenCoWorkPaths(directory));
            using var schema = JsonDocument.Parse(
                """{"type":"object","additionalProperties":false}""");
            var registration = new ToolRegistration(
                new ToolDefinition(
                    new ToolDefinitionId(
                        ToolSourceKind.PluginNative,
                        "acme/tools",
                        "echo"),
                    new ToolName("plugin_acme_tools", "echo"),
                    "Deferred echo tool.",
                    schema.RootElement,
                    ToolEffect.None,
                    ToolReplaySafety.Safe),
                new RuntimeBindingId("plugin.acme.tools.echo"),
                ToolExposure.Deferred,
                ToolInvocationAudience.Model);
            runtime.PublishPlugin(
                "acme/tools",
                [registration],
                [new ToolRuntimeBinding(
                    registration.RuntimeBindingId,
                    ToolBindingAvailability.Available,
                    Lease: null,
                    TimeSpan.FromSeconds(30),
                    static (_, _) => ValueTask.FromResult(
                        ToolBindingResult.Success(
                            JsonSerializer.SerializeToElement(new { ok = true }))))]);
            var client = new DeferredToolClient();
            var sink = new RecordingSink();

            await Executor(
                    directory,
                    "secret",
                    _ => client,
                    toolPipeline: new ToolInvocationPipeline(
                        runtime,
                        new SecretRedactor([])),
                    toolRuntime: runtime)
                .ExecuteAsync(Session(), sink, cancellationToken);

            Assert.Equal(2, client.Requests.Count);
            Assert.DoesNotContain(
                client.Requests[0].Tools,
                tool => tool.ProviderName == "plugin_acme_tools__echo");
            Assert.Contains(
                client.Requests[1].Tools,
                tool => tool.ProviderName == "plugin_acme_tools__echo");
            Assert.Single(
                sink.Intents.OfType<RecordDeferredToolsActivatedIntent>());
            Assert.IsType<CompleteTurnIntent>(sink.Intents[^1]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Replays_duplicate_provider_call_ids_without_repeating_the_binding()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-tool-dedupe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var client = new DuplicateToolCallClient();
            var pipeline = new CountingToolPipeline();
            var sink = new RecordingSink();

            await Executor(
                    directory,
                    "secret",
                    _ => client,
                    toolPipeline: pipeline)
                .ExecuteAsync(Session(), sink, cancellationToken);

            Assert.Equal(3, client.Requests.Count);
            Assert.Equal(1, pipeline.BindingCalls);
            Assert.Equal(1, pipeline.Replays);
            Assert.Equal(
                2,
                sink.Intents.OfType<RecordToolCallIntent>().Count());
            Assert.IsType<CompleteTurnIntent>(sink.Intents[^1]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Resumes_waiting_approval_before_requesting_provider_again()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-tool-approval-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var clock = new ManualTimerTimeProvider(
                new DateTimeOffset(2026, 7, 27, 13, 0, 0, TimeSpan.Zero));
            var pipeline = new ApprovalToolPipeline();
            var firstSink = new RecordingSink();
            await Executor(
                    directory,
                    "secret",
                    _ => new SingleToolCallClient(),
                    clock,
                    pipeline)
                .ExecuteAsync(Session(), firstSink, cancellationToken);

            var resumedSession = ApprovedSession(Session(), firstSink);
            clock.Advance(TimeSpan.FromDays(1));
            var resumedClient = new FinalAfterToolClient();
            var resumedSink = new RecordingSink();
            await Executor(
                    directory,
                    "secret",
                    _ => resumedClient,
                    clock,
                    pipeline)
                .ExecuteAsync(resumedSession, resumedSink, cancellationToken);

            Assert.Equal(2, pipeline.Calls.Count);
            Assert.Equal(
                pipeline.Calls[0].ToolInvocationId,
                pipeline.Calls[1].ToolInvocationId);
            Assert.Null(pipeline.Calls[0].ApprovalGranted);
            Assert.True(pipeline.Calls[1].ApprovalGranted);
            Assert.Single(resumedClient.Requests);
            Assert.Equal(2, resumedClient.Requests[0].AttemptNumber);
            Assert.Equal(
                [ChatCompletionMessageRole.Assistant, ChatCompletionMessageRole.Tool],
                resumedClient.Requests[0].Messages.TakeLast(2)
                    .Select(message => message.Role));
            Assert.DoesNotContain(
                resumedSink.Intents,
                intent => intent is RecordAgentInvocationSnapshotIntent);
            Assert.IsType<CompleteTurnIntent>(resumedSink.Intents[^1]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Resumes_interrupted_tool_attempt_before_requesting_provider()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-tool-interrupted-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var crashingPipeline = new CrashingToolPipeline();
            var firstSink = new RecordingSink();
            await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await Executor(
                        directory,
                        "secret",
                        _ => new SingleToolCallClient(),
                        toolPipeline: crashingPipeline)
                    .ExecuteAsync(Session(), firstSink, cancellationToken));

            var resumedClient = new FinalAfterToolClient();
            var resumedPipeline = new InterruptedRecoveryToolPipeline();
            var resumedSink = new RecordingSink();
            await Executor(
                    directory,
                    "secret",
                    _ => resumedClient,
                    toolPipeline: resumedPipeline)
                .ExecuteAsync(
                    InterruptedSession(Session(), firstSink),
                    resumedSink,
                    cancellationToken);

            var recovered = Assert.Single(resumedPipeline.Calls);
            Assert.Equal(
                crashingPipeline.ToolInvocationId,
                recovered.ToolInvocationId);
            Assert.Equal(1, recovered.PriorAttemptCount);
            Assert.Single(resumedClient.Requests);
            Assert.Equal(2, resumedClient.Requests[0].AttemptNumber);
            Assert.IsType<CompleteTurnIntent>(resumedSink.Intents[^1]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Stops_after_the_sixty_fourth_tool_round()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-tool-limit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var client = new IterationToolClient();
            var pipeline = new CountingToolPipeline();
            var sink = new RecordingSink();

            await Executor(
                    directory,
                    "secret",
                    _ => client,
                    toolPipeline: pipeline)
                .ExecuteAsync(Session(), sink, cancellationToken);

            Assert.Equal(64, client.Requests);
            Assert.Equal(64, pipeline.BindingCalls);
            var failure = Assert.IsType<FailTurnIntent>(sink.Intents[^1]);
            Assert.Equal(
                ToolErrorCodes.IterationLimitExceeded,
                failure.Error.Code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Compacts_completed_tool_history_after_the_third_attempt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-tool-compaction-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var original = Session();
            var invocation = Factory(directory, "secret")
                .Create(
                    original,
                    Guid.CreateVersion7(),
                    instructions: null)
                .Snapshot;
            var client = new CompactionResumeClient();
            var resumedSink = new RecordingSink();
            await Executor(
                    directory,
                    "secret",
                    _ => client)
                .ExecuteAsync(
                    CompletedToolSession(original, invocation),
                    resumedSink,
                    cancellationToken);

            Assert.Equal(
                [
                    ProviderInvocationPurpose.Compaction,
                    ProviderInvocationPurpose.Response,
                ],
                client.Requests.Select(request => request.Purpose));
            Assert.Equal(
                [5, 6],
                client.Requests.Select(request => request.AttemptNumber));
            Assert.IsType<CompleteTurnIntent>(resumedSink.Intents[^1]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Invocation_deadline_fails_with_provider_timeout()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-deadline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        using var callerCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var clock = new ManualTimerTimeProvider(
                new DateTimeOffset(2026, 7, 27, 13, 0, 0, TimeSpan.Zero));
            var client = new BlockingClient();
            var sink = new RecordingSink();
            var execution = Executor(
                    directory,
                    "secret",
                    _ => client,
                    clock)
                .ExecuteAsync(Session(), sink, callerCancellation.Token)
                .AsTask();

            await client.Started.Task.WaitAsync(cancellationToken);
            clock.Advance(TimeSpan.FromMinutes(30));
            await execution.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);

            Assert.Equal(1, client.Attempts);
            var failed = Assert.IsType<FailTurnIntent>(sink.Intents[^1]);
            Assert.Equal(AgentErrorCodes.ProviderTimeout, failed.Error.Code);
            Assert.Single(
                sink.Intents,
                intent => intent is CompleteTurnIntent or FailTurnIntent);
        }
        finally
        {
            callerCancellation.Cancel();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Caller_cancellation_is_not_converted_to_provider_timeout()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-cancellation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        using var callerCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var client = new BlockingClient();
            var sink = new RecordingSink();
            var execution = Executor(
                    directory,
                    "secret",
                    _ => client)
                .ExecuteAsync(Session(), sink, callerCancellation.Token)
                .AsTask();

            await client.Started.Task.WaitAsync(cancellationToken);
            callerCancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => execution);
            Assert.DoesNotContain(
                sink.Intents,
                intent => intent is CompleteTurnIntent or FailTurnIntent);
        }
        finally
        {
            callerCancellation.Cancel();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Registers_as_the_single_production_session_executor()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-agent-services-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton(new OpenCoWorkPaths(directory));
            services.AddSingleton(Models());
            services.AddOpenCoWorkAgentRuntime();
            using var provider = services.BuildServiceProvider();

            Assert.IsType<AgentRuntimeExecutor>(
                provider.GetRequiredService<ISessionExecutor>());
            Assert.Same(
                provider.GetRequiredService<ISessionExecutor>(),
                provider.GetRequiredService<ISessionExecutor>());
            Assert.Same(
                provider.GetRequiredService<HttpClient>(),
                provider.GetRequiredService<HttpClient>());
            Assert.Single(
                services,
                descriptor =>
                    descriptor.ServiceType == typeof(ISessionExecutor));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Length_completes_with_a_truncation_notice()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-length-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var sink = new RecordingSink();
            await Executor(
                    directory,
                    "secret",
                    _ => new FinishClient(
                        ChatCompletionFinishReason.Length,
                        content: "partial"))
                .ExecuteAsync(Session(), sink, cancellationToken);

            var notice = Assert.Single(
                sink.Intents.OfType<StartItemIntent>(),
                intent => intent.Type == SessionItemType.SystemNotice);
            Assert.Equal(
                new SystemNoticeContent("response.truncated"),
                notice.Content);
            Assert.IsType<CompleteTurnIntent>(sink.Intents[^1]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(
        ChatCompletionFinishReason.ContentFilter,
        "partial",
        null,
        AgentErrorCodes.ProviderContentFiltered)]
    [InlineData(
        ChatCompletionFinishReason.ToolCall,
        "partial",
        null,
        AgentErrorCodes.ProviderInvalidStream)]
    [InlineData(
        ChatCompletionFinishReason.Unknown,
        "partial",
        null,
        AgentErrorCodes.ProviderInvalidStream)]
    [InlineData(
        ChatCompletionFinishReason.Stop,
        null,
        "thought",
        AgentErrorCodes.ProviderEmptyResponse)]
    public async Task Invalid_finish_states_fail_once(
        ChatCompletionFinishReason finishReason,
        string? content,
        string? reasoning,
        string expectedCode)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-finish-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var sink = new RecordingSink();
            await Executor(
                    directory,
                    "secret",
                    _ => new FinishClient(finishReason, content, reasoning))
                .ExecuteAsync(Session(), sink, cancellationToken);

            var failed = Assert.IsType<FailTurnIntent>(sink.Intents[^1]);
            Assert.Equal(expectedCode, failed.Error.Code);
            Assert.Single(
                sink.Intents,
                intent => intent is CompleteTurnIntent or FailTurnIntent);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static AgentRuntimeExecutor Executor(
        string directory,
        string secret,
        Func<ProviderModelRegistration, IChatCompletionClient> clients,
        TimeProvider? timeProvider = null,
        IToolInvocationPipeline? toolPipeline = null,
        ToolRuntime? toolRuntime = null)
    {
        var paths = new OpenCoWorkPaths(directory);
        return new AgentRuntimeExecutor(
            Factory(directory, secret, toolRuntime),
            paths,
            clients,
            timeProvider,
            toolPipeline);
    }

    private static AgentFactory Factory(
        string directory,
        string secret,
        ToolRuntime? toolRuntime = null)
    {
        var paths = new OpenCoWorkPaths(directory);
        var models = Models();
        var credentials = FrozenProviderCredentials.Capture(
            models,
            name => name == "TOKEN_PLAN_KEY" ? secret : null);
        return new AgentFactory(
            new ProviderRegistry(
                models,
                credentials,
                AppContext.BaseDirectory,
                directory),
            paths,
            toolRuntime);
    }

    private static ModelsConfig Models() =>
        new()
        {
            Providers = new Dictionary<string, ProviderConfig>(StringComparer.Ordinal)
            {
                ["token-plan"] = new()
                {
                    BaseUrl = "https://example.test/v1",
                    ApiKey = new ProviderApiKeyConfig
                    {
                        Environment = "TOKEN_PLAN_KEY",
                    },
                    Models = new Dictionary<string, ModelConfig>(StringComparer.Ordinal)
                    {
                        ["qwen3.8-max-preview"] = new()
                        {
                            TokenizerProfileId = "qwen-o200k",
                            TokenizerProfileVersion = "1",
                            ContextWindowTokens = 983_616,
                            MaxOutputTokens = 131_072,
                        },
                    },
                },
            },
        };

    private static AgentSession Session(
        ExecutionWorkspaceDescriptor? executionWorkspace = null)
    {
        var threadId =
            Guid.Parse("019f2fb7-f514-7389-a79b-8af5d3f2c827");
        var turnId =
            Guid.Parse("019f2fb7-f514-7fc4-89c5-3446e04b98a5");
        var timestamp = new DateTimeOffset(
            2026,
            7,
            27,
            13,
            0,
            0,
            TimeSpan.Zero);
        return new AgentSession(
            new ThreadSnapshot(
                threadId,
                "executor",
                ThreadStatus.Active,
                ThreadAvailability.Available,
                HistoryMode.Server,
                2,
                turnId,
                [],
                timestamp,
                timestamp,
                SessionProjectionState.Ready,
                diagnostic: null,
                "token-plan",
                "qwen3.8-max-preview",
                AgentMode.Agent,
                executionWorkspace),
            new TurnSnapshot(
                turnId,
                threadId,
                TurnStatus.Running,
                timestamp,
                timestamp,
                CompletedAt: null,
                Error: null),
            [
                new SessionItemSnapshot(
                    Guid.Parse("019f2fb7-f514-71fb-b634-539b66e99c30"),
                    turnId,
                    SessionItemType.UserMessage,
                    SessionItemStatus.Completed,
                    new TextItemContent("hello"),
                    2,
                    timestamp,
                    timestamp),
            ]);
    }

    private static AgentSession ApprovedSession(
        AgentSession original,
        RecordingSink sink)
    {
        var invocation = Assert.Single(
            sink.Intents.OfType<RecordAgentInvocationSnapshotIntent>()).Snapshot;
        var toolCall = Assert.Single(
            sink.Intents.OfType<RecordToolCallIntent>());
        var started = Assert.Single(
            sink.Intents.OfType<RecordToolInvocationStartedIntent>());
        var waiting = Assert.Single(
            sink.Intents.OfType<WaitForInteractionIntent>());
        var requested = Assert.IsType<ToolApprovalRequestContent>(waiting.Request);
        var timestamp = original.Turn.UpdatedAt.AddMinutes(1);
        var history = original.ModelHistory.Concat(
        [
            new SessionItemSnapshot(
                toolCall.ItemId,
                original.Turn.TurnId,
                SessionItemType.ToolCall,
                SessionItemStatus.Completed,
                toolCall.Content,
                3,
                timestamp,
                timestamp),
            new SessionItemSnapshot(
                waiting.InteractionId,
                original.Turn.TurnId,
                SessionItemType.ApprovalRequest,
                SessionItemStatus.Completed,
                requested,
                4,
                timestamp,
                timestamp),
            new SessionItemSnapshot(
                Guid.CreateVersion7(timestamp),
                original.Turn.TurnId,
                SessionItemType.ApprovalResponse,
                SessionItemStatus.Completed,
                new ApprovalResponseContent(true, Comment: null),
                5,
                timestamp,
                timestamp),
        ]);
        var state = new ToolInvocationSnapshot(
            started.ToolInvocationId,
            original.Thread.ThreadId,
            original.Turn.TurnId,
            started.ProviderToolCallId,
            started.ProviderToolName,
            started.ToolDefinitionId,
            started.RuntimeBindingId,
            started.SnapshotSha256,
            started.ArgumentsSha256,
            ToolInvocationStatus.WaitingApproval,
            AttemptCount: 0,
            ResultItemId: null,
            ErrorCode: null,
            timestamp,
            timestamp,
            CompletedAt: null);
        return new AgentSession(
            original.Thread,
            original.Turn,
            history,
            waiting.Checkpoint,
            invocation: invocation,
            toolInvocations:
            [
                new AgentToolInvocationSnapshot(
                    state,
                    started.ToolCallItemId,
                    started.CallIndex),
            ],
            providerUsage: sink.Intents
                .OfType<RecordProviderUsageIntent>()
                .Select(intent => intent.Usage));
    }

    private static AgentSession InterruptedSession(
        AgentSession original,
        RecordingSink sink)
    {
        var invocation = Assert.Single(
            sink.Intents.OfType<RecordAgentInvocationSnapshotIntent>()).Snapshot;
        var toolCall = Assert.Single(
            sink.Intents.OfType<RecordToolCallIntent>());
        var started = Assert.Single(
            sink.Intents.OfType<RecordToolInvocationStartedIntent>());
        var timestamp = original.Turn.UpdatedAt.AddMinutes(1);
        return new AgentSession(
            original.Thread,
            original.Turn,
            original.ModelHistory.Append(
                new SessionItemSnapshot(
                    toolCall.ItemId,
                    original.Turn.TurnId,
                    SessionItemType.ToolCall,
                    SessionItemStatus.Completed,
                    toolCall.Content,
                    3,
                    timestamp,
                    timestamp)),
            invocation: invocation,
            toolInvocations:
            [
                new AgentToolInvocationSnapshot(
                    new ToolInvocationSnapshot(
                        started.ToolInvocationId,
                        original.Thread.ThreadId,
                        original.Turn.TurnId,
                        started.ProviderToolCallId,
                        started.ProviderToolName,
                        started.ToolDefinitionId,
                        started.RuntimeBindingId,
                        started.SnapshotSha256,
                        started.ArgumentsSha256,
                        ToolInvocationStatus.Started,
                        AttemptCount: 1,
                        ResultItemId: null,
                        ErrorCode: null,
                        timestamp,
                        timestamp,
                        CompletedAt: null),
                    started.ToolCallItemId,
                    started.CallIndex),
            ],
            providerUsage: sink.Intents
                .OfType<RecordProviderUsageIntent>()
                .Select(intent => intent.Usage));
    }

    private static AgentSession CompletedToolSession(
        AgentSession original,
        AgentInvocationSnapshot invocation)
    {
        var timestamp = original.Turn.UpdatedAt.AddMinutes(1);
        var priorTurnId = Guid.CreateVersion7(timestamp.AddTicks(-2));
        var currentUser = Assert.Single(original.ModelHistory) with
        {
            Sequence = 3,
        };
        var history = string.Join(
            ' ',
            Enumerable.Repeat("history", 800_000));
        using var arguments = JsonDocument.Parse("""{"path":"src"}""");
        using var output = JsonDocument.Parse("""{"status":"ok"}""");
        var toolCallItemId = Guid.CreateVersion7(timestamp.AddTicks(1));
        var toolInvocationId = Guid.CreateVersion7(timestamp.AddTicks(2));
        var resultItemId = Guid.CreateVersion7(timestamp.AddTicks(3));
        var argumentsSha256 = new string('a', 64);
        var result = new ToolResultSnapshot(
            toolInvocationId,
            "call-1",
            ToolInvocationStatus.Completed,
            output.RootElement,
            Error: null,
            IsTruncated: false,
            OriginalByteCount: 15,
            new string('d', 64),
            AttemptCount: 1);
        return new AgentSession(
            original.Thread,
            original.Turn,
            [
                new SessionItemSnapshot(
                    Guid.CreateVersion7(timestamp.AddTicks(-2)),
                    priorTurnId,
                    SessionItemType.UserMessage,
                    SessionItemStatus.Completed,
                    new TextItemContent(history),
                    1,
                    timestamp,
                    timestamp),
                new SessionItemSnapshot(
                    Guid.CreateVersion7(timestamp.AddTicks(-1)),
                    priorTurnId,
                    SessionItemType.AgentMessage,
                    SessionItemStatus.Completed,
                    new TextItemContent("prior answer"),
                    2,
                    timestamp,
                    timestamp),
                currentUser,
                new SessionItemSnapshot(
                    toolCallItemId,
                    original.Turn.TurnId,
                    SessionItemType.ToolCall,
                    SessionItemStatus.Completed,
                    new ToolCallItemContent(
                        providerRound: 1,
                        agentMessageItemId: null,
                        [
                            new ToolCallItemEntry(
                                "call-1",
                                "file__list",
                                arguments.RootElement,
                                argumentsSha256,
                                sensitiveInputDetected: false),
                        ]),
                    4,
                    timestamp,
                    timestamp),
                new SessionItemSnapshot(
                    resultItemId,
                    original.Turn.TurnId,
                    SessionItemType.ToolResult,
                    SessionItemStatus.Completed,
                    new ToolResultItemContent(result),
                    5,
                    timestamp,
                    timestamp),
            ],
            invocation: invocation,
            toolInvocations:
            [
                new AgentToolInvocationSnapshot(
                    new ToolInvocationSnapshot(
                        toolInvocationId,
                        original.Thread.ThreadId,
                        original.Turn.TurnId,
                        "call-1",
                        "file__list",
                        ToolDefinitionId: null,
                        RuntimeBindingId: null,
                        invocation.Tools!.SnapshotSha256,
                        argumentsSha256,
                        result.Status,
                        AttemptCount: 1,
                        resultItemId,
                        result.Error?.Code,
                        timestamp,
                        timestamp,
                        timestamp),
                    toolCallItemId,
                    CallIndex: 0),
            ],
            providerUsage: Enumerable.Range(1, 4)
                .Select(attempt => new ProviderUsageSnapshot(
                    invocation.InvocationId,
                    attempt,
                    ProviderInvocationPurpose.Response,
                    PromptTokens: 0,
                    CompletionTokens: 0,
                    TotalTokens: 0,
                    ProviderUsageSource.LocalEstimate,
                    IsEstimate: true)));
    }

    private sealed class ScriptedClient : IChatCompletionClient
    {
        public async IAsyncEnumerable<ChatCompletionEvent> StreamAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatCompletionContentDeltaEvent("answer");
            yield return new ChatCompletionReasoningDeltaEvent("thought");
            yield return new ChatCompletionUsageEvent(
                new ChatCompletionUsage(20, 4, 24));
            yield return new ChatCompletionCompletedEvent(
                ChatCompletionFinishReason.Stop);
        }
    }

    private sealed class TransientClient(bool failAfterDelta)
        : IChatCompletionClient
    {
        public int Attempts { get; private set; }

        public async IAsyncEnumerable<ChatCompletionEvent> StreamAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Attempts++;
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (failAfterDelta)
            {
                yield return new ChatCompletionContentDeltaEvent("visible");
                throw Transient();
            }

            if (request.AttemptNumber == 1)
            {
                throw Transient();
            }

            yield return new ChatCompletionContentDeltaEvent("recovered");
            yield return new ChatCompletionCompletedEvent(
                ChatCompletionFinishReason.Stop);
        }

        private static ChatCompletionException Transient() =>
            new(
                AgentErrorCodes.ProviderServerUnavailable,
                "transient",
                retryAfter: TimeSpan.Zero,
                isTransient: true);
    }

    private sealed class BlockingClient : IChatCompletionClient
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Attempts { get; private set; }

        public async IAsyncEnumerable<ChatCompletionEvent> StreamAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Attempts++;
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private sealed class ToolLoopClient : IChatCompletionClient
    {
        public List<ChatCompletionRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ChatCompletionEvent> StreamAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (Requests.Count == 1)
            {
                yield return new ChatCompletionContentDeltaEvent("checking");
                yield return new ChatCompletionToolCallCompletedEvent(
                    0,
                    "call-1",
                    "file__list",
                    """{"path":"src"}""");
                yield return new ChatCompletionToolCallCompletedEvent(
                    1,
                    "call-2",
                    "file__list",
                    """{"path":"tests"}""");
                yield return new ChatCompletionCompletedEvent(
                    ChatCompletionFinishReason.ToolCall);
                yield break;
            }

            yield return new ChatCompletionContentDeltaEvent("done");
            yield return new ChatCompletionCompletedEvent(
                ChatCompletionFinishReason.Stop);
        }
    }

    private sealed class DeferredToolClient : IChatCompletionClient
    {
        public List<ChatCompletionRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ChatCompletionEvent> StreamAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (Requests.Count == 1)
            {
                yield return new ChatCompletionToolCallCompletedEvent(
                    0,
                    "call-search",
                    "tool__search",
                    """{"query":"echo"}""");
                yield return new ChatCompletionCompletedEvent(
                    ChatCompletionFinishReason.ToolCall);
                yield break;
            }

            yield return new ChatCompletionContentDeltaEvent("done");
            yield return new ChatCompletionCompletedEvent(
                ChatCompletionFinishReason.Stop);
        }
    }

    private sealed class DuplicateToolCallClient : IChatCompletionClient
    {
        public List<ChatCompletionRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ChatCompletionEvent> StreamAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (Requests.Count <= 2)
            {
                yield return new ChatCompletionToolCallCompletedEvent(
                    0,
                    "call-1",
                    "file__list",
                    """{"path":"src"}""");
                yield return new ChatCompletionCompletedEvent(
                    ChatCompletionFinishReason.ToolCall);
                yield break;
            }

            yield return new ChatCompletionContentDeltaEvent("done");
            yield return new ChatCompletionCompletedEvent(
                ChatCompletionFinishReason.Stop);
        }
    }

    private sealed class SingleToolCallClient : IChatCompletionClient
    {
        public async IAsyncEnumerable<ChatCompletionEvent> StreamAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatCompletionToolCallCompletedEvent(
                0,
                "approval-call",
                "file__list",
                """{"path":"src"}""");
            yield return new ChatCompletionCompletedEvent(
                ChatCompletionFinishReason.ToolCall);
        }
    }

    private sealed class FinalAfterToolClient : IChatCompletionClient
    {
        public List<ChatCompletionRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ChatCompletionEvent> StreamAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatCompletionContentDeltaEvent("done");
            yield return new ChatCompletionCompletedEvent(
                ChatCompletionFinishReason.Stop);
        }
    }

    private sealed class IterationToolClient : IChatCompletionClient
    {
        public int Requests { get; private set; }

        public async IAsyncEnumerable<ChatCompletionEvent> StreamAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests++;
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatCompletionToolCallCompletedEvent(
                0,
                $"call-{Requests}",
                "file__list",
                """{"path":"src"}""");
            yield return new ChatCompletionCompletedEvent(
                ChatCompletionFinishReason.ToolCall);
        }
    }

    private sealed class CompactionResumeClient : IChatCompletionClient
    {
        private const string Summary =
            """
            ## 目标与上下文
            - Preserve the earlier goal.
            ## 已确认的决策与约束
            - Keep provider-neutral behavior.
            ## 已完成结果
            - The earlier turn completed.
            ## 关键标识、路径与错误
            - None.
            ## 待办与下一步
            - Continue the current turn.
            """;

        public List<ChatCompletionRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ChatCompletionEvent> StreamAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatCompletionContentDeltaEvent(
                request.Purpose == ProviderInvocationPurpose.Compaction
                    ? Summary
                    : "done");
            yield return new ChatCompletionCompletedEvent(
                ChatCompletionFinishReason.Stop);
        }
    }

    private sealed class ApprovalToolPipeline : IToolInvocationPipeline
    {
        public List<ToolInvocationContext> Calls { get; } = [];

        public async ValueTask<ToolResultSnapshot> InvokeAsync(
            ToolInvocationContext context,
            ISessionExecutionSink sink,
            CancellationToken cancellationToken)
        {
            Calls.Add(context);
            var registration = Assert.Single(
                context.Snapshot.Registrations,
                item => string.Equals(
                    context.Snapshot.CanonicalToProviderNames[
                        $"{item.Definition.Name.Namespace}.{item.Definition.Name.Name}"],
                    context.ProviderToolName,
                    StringComparison.Ordinal));
            await sink.EmitAsync(
                new RecordToolInvocationStartedIntent(
                    context.ToolInvocationId,
                    context.ToolCallItemId,
                    context.CallIndex,
                    context.ProviderToolCallId,
                    context.ProviderToolName,
                    registration.Definition.Id,
                    registration.RuntimeBindingId,
                    context.Snapshot.SnapshotSha256,
                    context.ArgumentsSha256),
                cancellationToken);
            if (context.ApprovalGranted is null)
            {
                await sink.EmitAsync(
                    new WaitForInteractionIntent(
                        Guid.CreateVersion7(),
                        SessionInteractionType.Approval,
                        new ToolApprovalRequestContent(
                            context.ToolInvocationId,
                            registration.Definition.Id,
                            context.Snapshot.SnapshotSha256,
                            context.ArgumentsSha256,
                            "Approve?"),
                        context.ApprovalCheckpoint!,
                        TimeoutAt: null,
                        context.ToolInvocationId),
                    cancellationToken);
                throw new ToolInvocationSuspendedException(
                    context.ToolInvocationId);
            }

            using var output = JsonDocument.Parse("""{"status":"ok"}""");
            var result = new ToolResultSnapshot(
                context.ToolInvocationId,
                context.ProviderToolCallId,
                ToolInvocationStatus.Completed,
                output.RootElement,
                Error: null,
                IsTruncated: false,
                OriginalByteCount: 15,
                new string('b', 64),
                AttemptCount: 1);
            await sink.EmitAsync(
                new RecordToolInvocationTerminalIntent(
                    Guid.CreateVersion7(),
                    result),
                cancellationToken);
            return result;
        }
    }

    private sealed class CrashingToolPipeline : IToolInvocationPipeline
    {
        public Guid ToolInvocationId { get; private set; }

        public async ValueTask<ToolResultSnapshot> InvokeAsync(
            ToolInvocationContext context,
            ISessionExecutionSink sink,
            CancellationToken cancellationToken)
        {
            ToolInvocationId = context.ToolInvocationId;
            await sink.EmitAsync(
                new RecordToolInvocationStartedIntent(
                    context.ToolInvocationId,
                    context.ToolCallItemId,
                    context.CallIndex,
                    context.ProviderToolCallId,
                    context.ProviderToolName,
                    ToolDefinitionId: null,
                    RuntimeBindingId: null,
                    context.Snapshot.SnapshotSha256,
                    context.ArgumentsSha256),
                cancellationToken);
            await sink.EmitAsync(
                new RecordToolInvocationAttemptStartedIntent(
                    context.ToolInvocationId,
                    AttemptNumber: 1),
                cancellationToken);
            throw new InvalidOperationException("simulated crash");
        }
    }

    private sealed class InterruptedRecoveryToolPipeline
        : IToolInvocationPipeline
    {
        public List<ToolInvocationContext> Calls { get; } = [];

        public async ValueTask<ToolResultSnapshot> InvokeAsync(
            ToolInvocationContext context,
            ISessionExecutionSink sink,
            CancellationToken cancellationToken)
        {
            Calls.Add(context);
            var result = new ToolResultSnapshot(
                context.ToolInvocationId,
                context.ProviderToolCallId,
                ToolInvocationStatus.OutcomeUnknown,
                Output: null,
                new SessionError(
                    ToolErrorCodes.OutcomeUnknown,
                    "Tool result is unknown.",
                    IsRetryable: false),
                IsTruncated: false,
                OriginalByteCount: 0,
                new string('c', 64),
                context.PriorAttemptCount);
            await sink.EmitAsync(
                new RecordToolInvocationTerminalIntent(
                    Guid.CreateVersion7(),
                    result),
                cancellationToken);
            return result;
        }
    }

    private sealed class CountingToolPipeline : IToolInvocationPipeline
    {
        public int BindingCalls { get; private set; }

        public int Replays { get; private set; }

        public async ValueTask<ToolResultSnapshot> InvokeAsync(
            ToolInvocationContext context,
            ISessionExecutionSink sink,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.ReplayResult is null)
            {
                BindingCalls++;
            }
            else
            {
                Replays++;
            }

            using var output = JsonDocument.Parse("""{"status":"ok"}""");
            var result = new ToolResultSnapshot(
                context.ToolInvocationId,
                context.ProviderToolCallId,
                ToolInvocationStatus.Completed,
                output.RootElement,
                Error: null,
                IsTruncated: false,
                OriginalByteCount: 15,
                new string('a', 64),
                AttemptCount: context.ReplayResult is null ? 1 : 0);
            await sink.EmitAsync(
                new RecordToolInvocationTerminalIntent(
                    Guid.CreateVersion7(),
                    result),
                cancellationToken);
            return result;
        }
    }

    private sealed class FinishClient(
        ChatCompletionFinishReason finishReason,
        string? content = null,
        string? reasoning = null) : IChatCompletionClient
    {
        public async IAsyncEnumerable<ChatCompletionEvent> StreamAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (content is not null)
            {
                yield return new ChatCompletionContentDeltaEvent(content);
            }

            if (reasoning is not null)
            {
                yield return new ChatCompletionReasoningDeltaEvent(reasoning);
            }

            yield return new ChatCompletionCompletedEvent(finishReason);
        }
    }

    private sealed class RecordingSink : ISessionExecutionSink
    {
        public List<SessionExecutionIntent> Intents { get; } = [];

        public ValueTask EmitAsync(
            SessionExecutionIntent intent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Intents.Add(intent);
            return ValueTask.CompletedTask;
        }
    }

}
