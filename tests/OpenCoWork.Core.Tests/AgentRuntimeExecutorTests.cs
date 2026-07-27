using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Agents;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Workspaces;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class AgentRuntimeExecutorTests
{
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
        AgentErrorCodes.ProviderUnsupportedToolCall)]
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
        TimeProvider? timeProvider = null)
    {
        var paths = new OpenCoWorkPaths(directory);
        var models = Models();
        var credentials = FrozenProviderCredentials.Capture(
            models,
            name => name == "TOKEN_PLAN_KEY" ? secret : null);
        return new AgentRuntimeExecutor(
            new AgentFactory(
                new ProviderRegistry(
                    models,
                    credentials,
                    AppContext.BaseDirectory,
                    directory),
                paths),
            paths,
            clients,
            timeProvider);
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

    private static AgentSession Session()
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
                AgentMode.Agent),
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
