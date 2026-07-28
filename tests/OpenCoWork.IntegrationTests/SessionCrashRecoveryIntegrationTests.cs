using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenCoWork.Abstractions;
using OpenCoWork.App;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Tools;
using Xunit;

namespace OpenCoWork.IntegrationTests;

public sealed class SessionCrashRecoveryIntegrationTests
{
    private const string ChildFlag = "OPENCOWORK_M2_CRASH_CHILD";
    private const string ToolChildFlag = "OPENCOWORK_M4_TOOL_CRASH_CHILD";
    private const string ChildWorkspace = "OPENCOWORK_M2_CRASH_WORKSPACE";
    private const string ToolKeyEnvironment = "OPENCOWORK_M4_TOOL_KEY";
    private const string ToolModel = "qwen3.8-max-preview";
    private const int CrashExitCode = 73;

    [Fact]
    public async Task Process_interruption_after_turn_flush_recovers_to_terminal_state()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-process-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var child = await RunCrashChildAsync(
                root,
                nameof(Child_commits_turn_then_terminates_process),
                ChildFlag,
                cancellationToken);
            Assert.True(
                child.ExitCode == CrashExitCode,
                $"Exit={child.ExitCode}{Environment.NewLine}" +
                $"stdout:{Environment.NewLine}{child.Output}" +
                $"{Environment.NewLine}stderr:{Environment.NewLine}" +
                child.Error);
            Assert.DoesNotContain(
                root,
                child.Output,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                root,
                child.Error,
                StringComparison.OrdinalIgnoreCase);

            using var host = OpenCoWorkCompositionRoot.Build([], root);
            await host.StartAsync(cancellationToken);
            var service = host.Services.GetRequiredService<ISessionService>();
            var threads = await service.ListThreadsAsync(
                new ListThreadsRequest(
                    Cursor: null,
                    PageSize: 100,
                    IncludeArchived: true),
                cancellationToken);
            var thread = Assert.Single(threads.Value!.Items);
            Assert.Null(thread.ActiveTurnId);
            var history = await service.ReadHistoryAsync(
                new ReadHistoryRequest(
                    thread.ThreadId,
                    AfterSequence: 0,
                    PageSize: 100),
                cancellationToken);
            var terminal = history.Value!.Items[^1];
            Assert.Equal(SessionEventType.TurnFailed, terminal.Type);
            Assert.Equal(
                SessionErrorCodes.RuntimeInterrupted,
                terminal.Payload.Turn!.Error?.Code);
            await host.StopAsync(cancellationToken);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Process_interruption_with_tool_cursor_resumes_the_running_turn()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-tool-process-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var child = await RunCrashChildAsync(
                root,
                nameof(Child_commits_tool_attempt_then_terminates_process),
                ToolChildFlag,
                cancellationToken);
            Assert.True(
                child.ExitCode == CrashExitCode,
                $"Exit={child.ExitCode}{Environment.NewLine}" +
                $"stdout:{Environment.NewLine}{child.Output}" +
                $"{Environment.NewLine}stderr:{Environment.NewLine}" +
                child.Error);

            using var host = OpenCoWorkCompositionRoot.Build(
                [],
                root,
                services => services.AddSingleton<
                    ISessionExecutor,
                    RecoveringToolExecutor>());
            await host.StartAsync(cancellationToken);
            var service = host.Services.GetRequiredService<ISessionService>();
            var threads = await service.ListThreadsAsync(
                new ListThreadsRequest(
                    Cursor: null,
                    PageSize: 100,
                    IncludeArchived: true),
                cancellationToken);
            Assert.Null(threads.Error);
            var thread = Assert.Single(threads.Value!.Items);
            for (var attempt = 0;
                 attempt < 100 && thread.ActiveTurnId is not null;
                 attempt++)
            {
                await Task.Delay(10, cancellationToken);
                thread = (await service.GetThreadAsync(
                    thread.ThreadId,
                    cancellationToken)).Value!;
            }

            Assert.Null(thread.ActiveTurnId);
            var history = (await service.ReadHistoryAsync(
                new ReadHistoryRequest(
                    thread.ThreadId,
                    AfterSequence: 0,
                    PageSize: 100),
                cancellationToken)).Value!.Items;
            Assert.Contains(
                history,
                item => item.Type == SessionEventType.ToolInvocationTerminal);
            Assert.Equal(SessionEventType.TurnCompleted, history[^1].Type);
            await host.StopAsync(cancellationToken);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Child_commits_turn_then_terminates_process()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(ChildFlag),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var root = Environment.GetEnvironmentVariable(ChildWorkspace)
            ?? throw new InvalidOperationException("Crash workspace is missing.");
        using var host = OpenCoWorkCompositionRoot.Build(
            [],
            root,
            services => services.AddSingleton<ISessionExecutor, CrashExecutor>());
        await host.StartAsync(TestContext.Current.CancellationToken);
        var service = host.Services.GetRequiredService<ISessionService>();
        var thread = (await service.CreateThreadAsync(
            new CreateThreadRequest(
                Guid.CreateVersion7(),
                ExpectedSequence: 0,
                DisplayName: "crash recovery"),
            TestContext.Current.CancellationToken)).Value!;
        await service.EnqueueInputAsync(
            new EnqueueInputRequest(
                thread.ThreadId,
                Guid.CreateVersion7(),
                thread.CurrentSequence,
                "interrupt after turn flush"),
            TestContext.Current.CancellationToken);
        await Task.Delay(Timeout.InfiniteTimeSpan, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Child_commits_tool_attempt_then_terminates_process()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(ToolChildFlag),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var root = Environment.GetEnvironmentVariable(ChildWorkspace)
            ?? throw new InvalidOperationException("Crash workspace is missing.");
        using var host = OpenCoWorkCompositionRoot.Build(
            [],
            root,
            services =>
            {
                services.AddSingleton(ToolCrashModels());
                services.AddSingleton<
                    ISessionExecutor,
                    ToolAttemptCrashExecutor>();
            });
        await host.StartAsync(TestContext.Current.CancellationToken);
        var service = host.Services.GetRequiredService<ISessionService>();
        var thread = (await service.CreateThreadAsync(
            new CreateThreadRequest(
                Guid.CreateVersion7(),
                ExpectedSequence: 0,
                DisplayName: "tool crash recovery",
                ProviderId: "test",
                ModelId: ToolModel),
            TestContext.Current.CancellationToken)).Value!;
        await service.EnqueueInputAsync(
            new EnqueueInputRequest(
                thread.ThreadId,
                Guid.CreateVersion7(),
                thread.CurrentSequence,
                "interrupt after tool attempt"),
            TestContext.Current.CancellationToken);
        await Task.Delay(Timeout.InfiniteTimeSpan, TestContext.Current.CancellationToken);
    }

    private static async Task<CrashChildResult> RunCrashChildAsync(
        string root,
        string method,
        string flag,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(
            typeof(SessionCrashRecoveryIntegrationTests).Assembly.Location);
        startInfo.ArgumentList.Add("-noLogo");
        startInfo.ArgumentList.Add("-noColor");
        startInfo.ArgumentList.Add("-method");
        startInfo.ArgumentList.Add(
            $"{typeof(SessionCrashRecoveryIntegrationTests).FullName}.{method}");
        startInfo.Environment[flag] = "1";
        startInfo.Environment[ChildWorkspace] = root;
        startInfo.Environment[ToolKeyEnvironment] = "test-secret";
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start crash child.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (
            timeout.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw new TimeoutException(
                "Crash child did not exit within 15 seconds.");
        }

        return new CrashChildResult(
            process.ExitCode,
            await process.StandardOutput.ReadToEndAsync(cancellationToken),
            await process.StandardError.ReadToEndAsync(cancellationToken));
    }

    private sealed class CrashExecutor : ISessionExecutor
    {
        public ValueTask ExecuteAsync(
            AgentSession context,
            ISessionExecutionSink sink,
            CancellationToken cancellationToken)
        {
            Environment.Exit(CrashExitCode);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ToolAttemptCrashExecutor : ISessionExecutor
    {
        public async ValueTask ExecuteAsync(
            AgentSession context,
            ISessionExecutionSink sink,
            CancellationToken cancellationToken)
        {
            var tools = new ToolRuntime().BuildSnapshot(
                AgentMode.Agent,
                new ToolsConfig());
            var snapshotHash = tools.SnapshotSha256;
            var invocation = new AgentInvocationSnapshot(
                Guid.CreateVersion7(),
                "test",
                ToolModel,
                "qwen-o200k",
                "1",
                AgentMode.Agent,
                new AgentPromptSnapshot("response-v1", new string('b', 64), 1),
                new AgentPromptSnapshot("compaction-v1", new string('c', 64), 1),
                WorkspaceInstructions: null,
                ContextWindowTokens: 983_616,
                MaxOutputTokens: 131_072,
                new string('d', 64),
                tools);
            await sink.EmitAsync(
                new RecordAgentInvocationSnapshotIntent(invocation),
                cancellationToken);
            using var arguments = JsonDocument.Parse("{}");
            var argumentsHash = new string('e', 64);
            var toolCallItemId = Guid.CreateVersion7();
            await sink.EmitAsync(
                new RecordToolCallIntent(
                    toolCallItemId,
                    new ToolCallItemContent(
                        1,
                        agentMessageItemId: null,
                        [
                            new ToolCallItemEntry(
                                "call-1",
                                "test__unsafe",
                                arguments.RootElement,
                                argumentsHash,
                                sensitiveInputDetected: false),
                        ])),
                cancellationToken);
            var toolInvocationId = Guid.CreateVersion7();
            await sink.EmitAsync(
                new RecordToolInvocationStartedIntent(
                    toolInvocationId,
                    toolCallItemId,
                    CallIndex: 0,
                    "call-1",
                    "test__unsafe",
                    ToolDefinitionId: null,
                    RuntimeBindingId: null,
                    snapshotHash,
                    argumentsHash),
                cancellationToken);
            await sink.EmitAsync(
                new RecordToolInvocationAttemptStartedIntent(
                    toolInvocationId,
                    AttemptNumber: 1),
                cancellationToken);
            Environment.Exit(CrashExitCode);
        }
    }

    private sealed class RecoveringToolExecutor : ISessionExecutor
    {
        public async ValueTask ExecuteAsync(
            AgentSession context,
            ISessionExecutionSink sink,
            CancellationToken cancellationToken)
        {
            var state = Assert.Single(context.ToolInvocations);
            Assert.NotNull(context.Invocation?.Tools);
            Assert.Equal(1, state.Invocation.AttemptCount);
            await sink.EmitAsync(
                new RecordToolInvocationTerminalIntent(
                    Guid.CreateVersion7(),
                    new ToolResultSnapshot(
                        state.Invocation.ToolInvocationId,
                        state.Invocation.ProviderToolCallId,
                        ToolInvocationStatus.OutcomeUnknown,
                        Output: null,
                        new SessionError(
                            ToolErrorCodes.OutcomeUnknown,
                            "Tool result is unknown.",
                            IsRetryable: false),
                        IsTruncated: false,
                        OriginalByteCount: 0,
                        new string('f', 64),
                        state.Invocation.AttemptCount)),
                cancellationToken);
            await sink.EmitAsync(
                new CompleteTurnIntent(),
                cancellationToken);
        }
    }

    private static ModelsConfig ToolCrashModels() =>
        new()
        {
            DefaultProvider = "test",
            DefaultModel = ToolModel,
            Providers = new Dictionary<string, ProviderConfig>(StringComparer.Ordinal)
            {
                ["test"] = new()
                {
                    BaseUrl = "https://example.test/v1",
                    ApiKey = new ProviderApiKeyConfig
                    {
                        Environment = ToolKeyEnvironment,
                    },
                    Models = new Dictionary<string, ModelConfig>(StringComparer.Ordinal)
                    {
                        [ToolModel] = new()
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

    private sealed record CrashChildResult(
        int ExitCode,
        string Output,
        string Error);
}
