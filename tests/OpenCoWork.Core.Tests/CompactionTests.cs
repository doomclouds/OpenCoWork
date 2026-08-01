using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
                    ProviderInvocationPurpose.Compaction,
                    ProviderInvocationPurpose.Response,
                ],
                client.Requests.Select(request => request.Purpose));
            Assert.Equal([1, 2], client.Requests.Select(request => request.AttemptNumber));
            Assert.All(client.Requests, request => Assert.Equal("high", request.ReasoningEffort));
            Assert.Empty(client.Requests[0].Tools);
            Assert.Equal(21, client.Requests[1].Tools.Count);
            var checkpoint = Assert.Single(
                    sink.Intents.OfType<RecordCompactionCheckpointIntent>())
                .Checkpoint;
            Assert.Equal(2, checkpoint.SchemaVersion);
            Assert.Equal("opencowork.compaction.v2", checkpoint.SummaryPromptVersion);
            Assert.Equal(2, checkpoint.SourceStartSequence);
            Assert.Equal(8, checkpoint.SourceEndSequence);
            Assert.Equal(CompactionClient.Summary, checkpoint.Summary);
            Assert.Equal(
                CompactionCheckpointIntegrity.SourceMessagesSha256(
                    session.ModelHistory,
                    2,
                    8,
                    schemaVersion: 2),
                checkpoint.SourceMessagesSha256);
            Assert.Equal(
                [
                    ProviderInvocationPurpose.Compaction,
                    ProviderInvocationPurpose.Response,
                ],
                sink.Intents
                    .OfType<RecordProviderUsageIntent>()
                    .Select(intent => intent.Usage.Purpose));
            Assert.DoesNotContain(
                client.Requests[^1].Input,
                item => item.TryGetProperty("content", out var content) &&
                        content.GetString()!.Contains(
                    "first-old-history",
                    StringComparison.Ordinal));
            Assert.Contains(
                client.Requests[^1].Input,
                item => item.TryGetProperty("content", out var content) &&
                        content.GetString()!.Contains(
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
                "deepseek-v4-flash",
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
                item => item.Purpose == ProviderInvocationPurpose.Compaction);
            Assert.Contains(
                "Previous authoritative summary:",
                request.Input[^1].GetProperty("content").GetString()!,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "first-old-history",
                request.Input[^1].GetProperty("content").GetString()!,
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
    public async Task Compaction_keeps_assistant_tool_call_and_result_in_one_source_group()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-tool-compaction-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var client = new CompactionClient();
            var sink = new RecordingSink();
            var session = WithToolGroup(
                Session(priorTurnCount: 1, historyRepeats: 400_000));

            await Executor(directory, client)
                .ExecuteAsync(session, sink, cancellationToken);

            var compactionRequest = Assert.Single(
                client.Requests,
                request =>
                    request.Purpose == ProviderInvocationPurpose.Compaction);
            Assert.Contains(
                "ToolCall call-1 file__list",
                compactionRequest.Input[^1].GetProperty("content").GetString()!,
                StringComparison.Ordinal);
            Assert.Contains(
                "ToolCallOutput call-1",
                compactionRequest.Input[^1].GetProperty("content").GetString()!,
                StringComparison.Ordinal);
            var checkpoint = Assert.Single(
                    sink.Intents.OfType<RecordCompactionCheckpointIntent>())
                .Checkpoint;
            Assert.Equal(2, checkpoint.SchemaVersion);
            Assert.Equal(5, checkpoint.SourceEndSequence);
            Assert.Equal(
                CompactionCheckpointIntegrity.SourceMessagesSha256(
                    session.ModelHistory,
                    checkpoint.SourceStartSequence,
                    checkpoint.SourceEndSequence,
                    schemaVersion: 2),
                checkpoint.SourceMessagesSha256);
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
    public async Task Generic_context_400_does_not_trigger_reactive_compaction()
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
                [ProviderInvocationPurpose.Response],
                client.Requests.Select(request => request.Purpose));
            Assert.Empty(sink.Intents.OfType<RecordCompactionCheckpointIntent>());
            var failure = Assert.IsType<FailTurnIntent>(sink.Intents[^1]);
            Assert.Equal(AgentErrorCodes.ProviderInvalidRequest, failure.Error.Code);
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
    public async Task Compaction_retry_and_response_stay_inside_three_call_budget()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-compaction-budget-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var client = new CompactionClient(
                transientOnFirstCompaction: true);
            var sink = new RecordingSink();

            await Executor(directory, client)
                .ExecuteAsync(
                    Session(),
                    sink,
                    cancellationToken);

            Assert.Equal(3, client.Requests.Count);
            Assert.Equal(
                [
                    ProviderInvocationPurpose.Compaction,
                    ProviderInvocationPurpose.Compaction,
                    ProviderInvocationPurpose.Response,
                ],
                client.Requests.Select(request => request.Purpose));
            Assert.Single(sink.Intents.OfType<RecordCompactionCheckpointIntent>());
            Assert.IsType<CompleteTurnIntent>(sink.Intents[^1]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Compaction_v2_hashes_complete_tool_groups_and_v1_rejects_them()
    {
        var timestamp = new DateTimeOffset(
            2026,
            7,
            28,
            8,
            0,
            0,
            TimeSpan.Zero);
        var turnId = Guid.CreateVersion7(timestamp);
        var agent = new SessionItemSnapshot(
            Guid.CreateVersion7(timestamp.AddTicks(1)),
            turnId,
            SessionItemType.AgentMessage,
            SessionItemStatus.Completed,
            new TextItemContent("checking"),
            Sequence: 1,
            timestamp,
            timestamp);
        using var arguments = JsonDocument.Parse("""{"path":"src"}""");
        using var output = JsonDocument.Parse("""{"entries":[]}""");
        var call = new SessionItemSnapshot(
            Guid.CreateVersion7(timestamp.AddTicks(2)),
            turnId,
            SessionItemType.ToolCall,
            SessionItemStatus.Completed,
            new ToolCallItemContent(
                providerRound: 1,
                agent.ItemId,
                [
                    new ToolCallItemEntry(
                        "call-1",
                        "file__list",
                        arguments.RootElement,
                        new string('a', 64),
                        sensitiveInputDetected: false),
                ]),
            Sequence: 2,
            timestamp,
            timestamp);
        var result = new SessionItemSnapshot(
            Guid.CreateVersion7(timestamp.AddTicks(3)),
            turnId,
            SessionItemType.ToolResult,
            SessionItemStatus.Completed,
            new ToolResultItemContent(new ToolResultSnapshot(
                Guid.CreateVersion7(timestamp.AddTicks(4)),
                "call-1",
                ToolInvocationStatus.Completed,
                output.RootElement,
                Error: null,
                IsTruncated: false,
                OriginalByteCount: 14,
                new string('b', 64),
                AttemptCount: 1)),
            Sequence: 3,
            timestamp,
            timestamp);
        SessionItemSnapshot[] history = [agent, call, result];

        var hash = CompactionCheckpointIntegrity.SourceMessagesSha256(
            history,
            1,
            3,
            schemaVersion: 2);
        var resultEnvelope = ProviderResponsesHistory.ToolResultEnvelope(
            Assert.IsType<ToolResultItemContent>(result.Content).Result);
        var oldV2Canonical =
            "9:assistant|8:checking|0:|6:call-1|10:file__list|14:{\"path\":\"src\"}|\n" +
            $"4:tool|{Encoding.UTF8.GetByteCount(resultEnvelope)}:{resultEnvelope}|6:call-1|\n";
        Assert.Equal(Sha256(oldV2Canonical), hash);
        Assert.Throws<InvalidDataException>(() =>
            CompactionCheckpointIntegrity.SourceMessagesSha256(
                history,
                1,
                3,
                schemaVersion: 1));
        Assert.Throws<AgentPreparationException>(() =>
            ProviderResponsesHistory.Build([agent, call]));
    }

    private static AgentRuntimeExecutor Executor(
        string directory,
        IResponsesTestClient client)
    {
        var models = new ModelsConfig();
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
            _ => client.StreamAsync);
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
                ModelsConfig.ProviderId,
                ModelsConfig.FlashModelId,
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
                    ModelsConfig.FlashModelId,
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

    private static AgentSession WithToolGroup(AgentSession session)
    {
        var agent = session.ModelHistory.Single(item =>
            item.Type == SessionItemType.AgentMessage);
        var timestamp = agent.CreatedAt;
        var toolCall = new SessionItemSnapshot(
            Guid.CreateVersion7(timestamp.AddTicks(4)),
            agent.TurnId,
            SessionItemType.ToolCall,
            SessionItemStatus.Completed,
            new ToolCallItemContent(
                providerRound: 1,
                agent.ItemId,
                [
                    new ToolCallItemEntry(
                        "call-1",
                        "file__list",
                        JsonSerializer.SerializeToElement(new { path = "src" }),
                        new string('a', 64),
                        sensitiveInputDetected: false),
                ]),
            Sequence: 4,
            timestamp,
            timestamp);
        var toolResult = new SessionItemSnapshot(
            Guid.CreateVersion7(timestamp.AddTicks(5)),
            agent.TurnId,
            SessionItemType.ToolResult,
            SessionItemStatus.Completed,
            new ToolResultItemContent(new ToolResultSnapshot(
                Guid.CreateVersion7(timestamp.AddTicks(6)),
                "call-1",
                ToolInvocationStatus.Completed,
                JsonSerializer.SerializeToElement(new { entries = Array.Empty<string>() }),
                Error: null,
                IsTruncated: false,
                OriginalByteCount: 14,
                new string('b', 64),
                AttemptCount: 1)),
            Sequence: 5,
            timestamp,
            timestamp);
        return new AgentSession(
            session.Thread,
            session.Turn,
            [.. session.ModelHistory, toolCall, toolResult],
            session.Checkpoint,
            session.CompactionCheckpoint);
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private interface IResponsesTestClient
    {
        IAsyncEnumerable<DeepSeekResponseEvent> StreamAsync(
            DeepSeekResponsesRequest request,
            CancellationToken cancellationToken = default);
    }

    private static DeepSeekTextDeltaEvent Output(string value) =>
        new("0:message-1", DeepSeekTextKind.Output, value);

    private sealed class CompactionClient(
        bool promptTooLongOnFirstResponse = false,
        bool invalidSummary = false,
        bool transientOnFirstCompaction = false) : IResponsesTestClient
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

        public List<DeepSeekResponsesRequest> Requests { get; } = [];

        public async IAsyncEnumerable<DeepSeekResponseEvent> StreamAsync(
            DeepSeekResponsesRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Purpose == ProviderInvocationPurpose.Response &&
                promptTooLongOnFirstResponse &&
                Requests.Count(item =>
                    item.Purpose == ProviderInvocationPurpose.Response) == 1)
            {
                throw new ProviderException(
                    AgentErrorCodes.ProviderInvalidRequest,
                    "prompt too long");
            }

            if (request.Purpose == ProviderInvocationPurpose.Compaction)
            {
                if (transientOnFirstCompaction &&
                    Requests.Count(item =>
                        item.Purpose == ProviderInvocationPurpose.Compaction) == 1)
                {
                    throw new ProviderException(
                        AgentErrorCodes.ProviderServerUnavailable,
                        "transient",
                        retryAfter: TimeSpan.Zero,
                        isTransient: true);
                }

                yield return Output(
                    invalidSummary ? "preamble\n" + Summary : Summary);
                yield return new DeepSeekTerminalEvent(
                    DeepSeekTerminalStatus.Completed,
                    new DeepSeekResponsesUsage(900, 0, 80, 0, 980));
            }
            else
            {
                yield return Output("answer");
                yield return new DeepSeekTerminalEvent(
                    DeepSeekTerminalStatus.Completed,
                    new DeepSeekResponsesUsage(900, 0, 1, 0, 901));
            }
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
