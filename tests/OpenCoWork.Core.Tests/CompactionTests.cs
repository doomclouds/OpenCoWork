using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Agents;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Workspaces;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class CompactionTests
{
    [Fact]
    public async Task Proactive_compaction_commits_the_oldest_prefix_before_response()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-compaction-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var client = new CompactionClient();
            var sink = new RecordingSink();
            var session = Session();

            await Executor(directory, client)
                .ExecuteAsync(session, sink, cancellationToken);

            Assert.Null(sink.Intents.OfType<FailTurnIntent>().SingleOrDefault());
            Assert.Equal(
                [
                    ChatCompletionInvocationPurpose.Compaction,
                    ChatCompletionInvocationPurpose.Response,
                ],
                client.Requests.Select(request => request.Purpose));
            Assert.Equal([1, 2], client.Requests.Select(request => request.AttemptNumber));
            Assert.Empty(client.Requests[0].Tools);
            Assert.Equal(5, client.Requests[1].Tools.Count);
            var checkpoint = Assert.Single(
                    sink.Intents.OfType<RecordCompactionCheckpointIntent>())
                .Checkpoint;
            Assert.Equal(2, checkpoint.SourceStartSequence);
            Assert.Equal(4, checkpoint.SourceEndSequence);
            Assert.Equal(CompactionClient.Summary, checkpoint.Summary);
            Assert.Equal(
                CompactionCheckpointIntegrity.SourceMessagesSha256(
                    session.ModelHistory,
                    2,
                    4),
                checkpoint.SourceMessagesSha256);
            Assert.Equal(
                [
                    ChatCompletionInvocationPurpose.Compaction,
                    ChatCompletionInvocationPurpose.Response,
                ],
                sink.Intents
                    .OfType<RecordProviderUsageIntent>()
                    .Select(intent => intent.Usage.Purpose));
            Assert.DoesNotContain(
                client.Requests[^1].Messages,
                message => message.Content.Contains(
                    "first-old-history",
                    StringComparison.Ordinal));
            Assert.Contains(
                client.Requests[^1].Messages,
                message => message.Content.Contains(
                    CompactionClient.Summary,
                    StringComparison.Ordinal));
            Assert.IsType<CompleteTurnIntent>(sink.Intents[^1]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Later_compaction_extends_the_latest_checkpoint()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-extend-compaction-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var tokenizer = Tokenizer();
            var session = Session(
                priorTurnCount: 2,
                historyRepeats: 400_000);
            var previous = new CompactionCheckpointSnapshot(
                1,
                CompactionClient.Summary,
                Sha256(CompactionClient.Summary),
                2,
                4,
                CompactionCheckpointIntegrity.SourceMessagesSha256(
                    session.ModelHistory,
                    2,
                    4),
                "opencowork.compaction.v1",
                "qwen-o200k",
                "1",
                tokenizer.CountTokens(CompactionClient.Summary));
            var client = new CompactionClient();
            var sink = new RecordingSink();

            await Executor(directory, client)
                .ExecuteAsync(
                    WithCheckpoint(session, previous),
                    sink,
                    cancellationToken);

            var request = Assert.Single(
                client.Requests,
                item => item.Purpose == ChatCompletionInvocationPurpose.Compaction);
            Assert.Contains(
                "Previous authoritative summary:",
                request.Messages[^1].Content,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "first-old-history",
                request.Messages[^1].Content,
                StringComparison.Ordinal);
            var checkpoint = Assert.Single(
                    sink.Intents.OfType<RecordCompactionCheckpointIntent>())
                .Checkpoint;
            Assert.Equal(2, checkpoint.SourceStartSequence);
            Assert.Equal(8, checkpoint.SourceEndSequence);
            Assert.IsType<CompleteTurnIntent>(sink.Intents[^1]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Corrupt_checkpoint_fails_before_a_provider_call()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-corrupt-compaction-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var tokenizer = Tokenizer();
            var session = Session(priorTurnCount: 1, historyRepeats: 1);
            var checkpoint = new CompactionCheckpointSnapshot(
                1,
                CompactionClient.Summary,
                Sha256(CompactionClient.Summary),
                2,
                4,
                new string('0', 64),
                "opencowork.compaction.v1",
                "qwen-o200k",
                "1",
                tokenizer.CountTokens(CompactionClient.Summary));
            var client = new CompactionClient();
            var sink = new RecordingSink();

            await Executor(directory, client)
                .ExecuteAsync(
                    WithCheckpoint(session, checkpoint),
                    sink,
                    cancellationToken);

            Assert.Empty(client.Requests);
            var failure = Assert.IsType<FailTurnIntent>(sink.Intents[^1]);
            Assert.Equal(AgentErrorCodes.ContextInputInvalid, failure.Error.Code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Prompt_too_long_compacts_once_inside_the_three_call_budget()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-reactive-compaction-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var client = new CompactionClient(promptTooLongOnFirstResponse: true);
            var sink = new RecordingSink();

            await Executor(directory, client)
                .ExecuteAsync(
                    Session(priorTurnCount: 1, historyRepeats: 240_000),
                    sink,
                    cancellationToken);

            Assert.Equal(
                [
                    ChatCompletionInvocationPurpose.Response,
                    ChatCompletionInvocationPurpose.Compaction,
                    ChatCompletionInvocationPurpose.Response,
                ],
                client.Requests.Select(request => request.Purpose));
            Assert.Equal([1, 2, 3], client.Requests.Select(request => request.AttemptNumber));
            Assert.Single(sink.Intents.OfType<RecordCompactionCheckpointIntent>());
            Assert.IsType<CompleteTurnIntent>(sink.Intents[^1]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Invalid_summary_is_discarded_and_fails_compaction()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-invalid-compaction-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var client = new CompactionClient(invalidSummary: true);
            var sink = new RecordingSink();

            await Executor(directory, client)
                .ExecuteAsync(Session(), sink, cancellationToken);

            Assert.Single(client.Requests);
            Assert.Empty(sink.Intents.OfType<RecordCompactionCheckpointIntent>());
            var failure = Assert.IsType<FailTurnIntent>(sink.Intents[^1]);
            Assert.Equal(AgentErrorCodes.ContextCompactionFailed, failure.Error.Code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Compaction_never_creates_a_fourth_provider_call()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-compaction-budget-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var client = new CompactionClient(
                promptTooLongOnFirstResponse: true,
                transientOnFirstCompaction: true);
            var sink = new RecordingSink();

            await Executor(directory, client)
                .ExecuteAsync(
                    Session(priorTurnCount: 1, historyRepeats: 240_000),
                    sink,
                    cancellationToken);

            Assert.Equal(3, client.Requests.Count);
            Assert.Single(sink.Intents.OfType<RecordCompactionCheckpointIntent>());
            var failure = Assert.IsType<FailTurnIntent>(sink.Intents[^1]);
            Assert.Equal(AgentErrorCodes.ContextCompactionFailed, failure.Error.Code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static AgentRuntimeExecutor Executor(
        string directory,
        IChatCompletionClient client)
    {
        var models = new ModelsConfig
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
        var paths = new OpenCoWorkPaths(directory);
        return new AgentRuntimeExecutor(
            new AgentFactory(
                new ProviderRegistry(
                    models,
                    FrozenProviderCredentials.Capture(
                        models,
                        _ => "secret"),
                    AppContext.BaseDirectory,
                    directory),
                paths),
            paths,
            _ => client);
    }

    private static AgentSession Session(
        int priorTurnCount = 2,
        int historyRepeats = 200_000)
    {
        var timestamp = new DateTimeOffset(
            2026,
            7,
            27,
            14,
            0,
            0,
            TimeSpan.Zero);
        var threadId = Guid.Parse("019f2fef-3ff0-7f92-b408-7a66af6c2e15");
        var currentTurnId = Guid.Parse("019f2fef-3ff0-7f97-9a42-e9ead98c04a4");
        Guid[] priorTurnIds =
        [
            Guid.Parse("019f2fef-3ff0-70f4-805e-18340d15481b"),
            Guid.Parse("019f2fef-3ff0-7302-be3c-f19856995726"),
        ];
        var items = new List<SessionItemSnapshot>();
        for (var index = 0; index < priorTurnCount; index++)
        {
            var sequence = index * 3L + 2;
            items.Add(Item(
                priorTurnIds[index],
                SessionItemType.UserMessage,
                History($"{Ordinal(index)}-old-history", historyRepeats),
                sequence));
            items.Add(Item(
                priorTurnIds[index],
                SessionItemType.AgentMessage,
                History($"{Ordinal(index)}-old-answer", historyRepeats),
                sequence + 1));
        }

        var currentSequence = priorTurnCount * 3L + 3;
        items.Add(Item(
            currentTurnId,
            SessionItemType.UserMessage,
            "current",
            currentSequence));
        return new AgentSession(
            new ThreadSnapshot(
                threadId,
                "compaction",
                ThreadStatus.Active,
                ThreadAvailability.Available,
                HistoryMode.Server,
                currentSequence,
                currentTurnId,
                [],
                timestamp,
                timestamp,
                SessionProjectionState.Ready,
                diagnostic: null,
                "token-plan",
                "qwen3.8-max-preview",
                AgentMode.Agent),
            new TurnSnapshot(
                currentTurnId,
                threadId,
                TurnStatus.Running,
                timestamp,
                timestamp,
                CompletedAt: null,
                Error: null),
            items);

        SessionItemSnapshot Item(
            Guid turnId,
            SessionItemType type,
            string content,
            long sequence) =>
            new(
                Guid.CreateVersion7(timestamp.AddTicks(sequence)),
                turnId,
                type,
                SessionItemStatus.Completed,
                new TextItemContent(content),
                sequence,
                timestamp,
                timestamp);
    }

    private static string History(string marker, int repeats) =>
        marker + " " + string.Join(' ', Enumerable.Repeat("history", repeats));

    private static string Ordinal(int index) => index == 0 ? "first" : "second";

    private static ModelTokenizer Tokenizer() =>
        TokenizerProfiles.BuiltIn
            .Single(profile =>
                profile.ModelIds.Contains(
                    "qwen3.8-max-preview",
                    StringComparer.Ordinal))
            .CreateTokenizer(AppContext.BaseDirectory);

    private static AgentSession WithCheckpoint(
        AgentSession session,
        CompactionCheckpointSnapshot checkpoint) =>
        new(
            session.Thread,
            session.Turn,
            session.ModelHistory,
            session.Checkpoint,
            checkpoint);

    private static string Sha256(string value) =>
        Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private sealed class CompactionClient(
        bool promptTooLongOnFirstResponse = false,
        bool invalidSummary = false,
        bool transientOnFirstCompaction = false) : IChatCompletionClient
    {
        public const string Summary =
            """
            ## 目标与上下文
            - Preserve the earlier goal.
            ## 已确认的决策与约束
            - Keep provider-neutral behavior.
            ## 已完成结果
            - The first turn completed.
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
            if (request.Purpose == ChatCompletionInvocationPurpose.Response &&
                promptTooLongOnFirstResponse &&
                Requests.Count(item =>
                    item.Purpose == ChatCompletionInvocationPurpose.Response) == 1)
            {
                throw new ChatCompletionException(
                    AgentErrorCodes.ProviderInvalidRequest,
                    "prompt too long",
                    isPromptTooLong: true);
            }

            if (request.Purpose == ChatCompletionInvocationPurpose.Compaction)
            {
                if (transientOnFirstCompaction &&
                    Requests.Count(item =>
                        item.Purpose == ChatCompletionInvocationPurpose.Compaction) == 1)
                {
                    throw new ChatCompletionException(
                        AgentErrorCodes.ProviderServerUnavailable,
                        "transient",
                        retryAfter: TimeSpan.Zero,
                        isTransient: true);
                }

                yield return new ChatCompletionContentDeltaEvent(
                    invalidSummary ? "preamble\n" + Summary : Summary);
                yield return new ChatCompletionUsageEvent(
                    new ChatCompletionUsage(900, 80, 980));
            }
            else
            {
                yield return new ChatCompletionContentDeltaEvent("answer");
                yield return new ChatCompletionUsageEvent(
                    new ChatCompletionUsage(900, 1, 901));
            }

            yield return new ChatCompletionCompletedEvent(
                ChatCompletionFinishReason.Stop);
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
