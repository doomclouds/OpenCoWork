using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Sessions;
using OpenCoWork.Core.State;
using OpenCoWork.Core.Workspaces;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class SessionExecutionTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    [Fact]
    public async Task Agent_snapshots_usage_and_compaction_are_durable_across_restart()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var invocationId = Guid.CreateVersion7();
        var summary =
            """
            ## 目标与上下文
            - Preserve the earlier goal.
            ## 已确认的决策与约束
            - Keep the durable session contract.
            ## 已完成结果
            - The earlier turn completed.
            ## 关键标识、路径与错误
            - None.
            ## 待办与下一步
            - Continue.
            """;
        var checkpoint = new CompactionCheckpointSnapshot(
            SchemaVersion: 1,
            Summary: summary,
            SummarySha256: Sha256(summary),
            SourceStartSequence: 1,
            SourceEndSequence: 1,
            SourceMessagesSha256: Sha256(string.Empty),
            SummaryPromptVersion: "compaction-v1",
            TokenizerProfileId: "qwen-tokenizer",
            TokenizerProfileVersion: "1",
            SummaryTokenCount: 3);
        var invocation = new AgentInvocationSnapshot(
            invocationId,
            "qwen",
            "qwen3.8",
            "qwen-tokenizer",
            "1",
            AgentMode.Plan,
            new AgentPromptSnapshot("response-v1", new string('a', 64), 10),
            new AgentPromptSnapshot("compaction-v1", new string('b', 64), 8),
            WorkspaceInstructions: null,
            ContextWindowTokens: 32_768,
            MaxOutputTokens: 2_048,
            ConfigurationSha256: new string('c', 64));
        var firstExecutor = new ScriptedExecutor(
            async (context, sink, token) =>
            {
                Assert.Equal(AgentMode.Plan, context.Turn.EffectiveAgentMode);
                await sink.EmitAsync(
                    new RecordAgentInvocationSnapshotIntent(invocation),
                    token);
                await sink.EmitAsync(
                    new RecordProviderUsageIntent(
                        new ProviderUsageSnapshot(
                            invocationId,
                            AttemptNumber: 1,
                            ChatCompletionInvocationPurpose.Response,
                            PromptTokens: 10,
                            CompletionTokens: 4,
                            TotalTokens: 14,
                            ProviderUsageSource.Provider,
                            IsEstimate: false)),
                    token);
                await sink.EmitAsync(
                    new RecordCompactionCheckpointIntent(checkpoint),
                    token);
                await sink.EmitAsync(new CompleteTurnIntent(), token);
            });
        var (runtime, journal, firstService) = await CreateServiceAsync(
            files,
            cancellationToken,
            firstExecutor);
        var thread = Assert.IsType<ThreadSnapshot>((await firstService.CreateThreadAsync(
            new CreateThreadRequest(
                Guid.CreateVersion7(),
                ExpectedSequence: 0,
                DisplayName: "agent",
                ProviderId: "qwen",
                ModelId: "qwen3.8",
                AgentMode: AgentMode.Plan),
            cancellationToken)).Value);
        var firstTurnId = Guid.CreateVersion7();
        await firstService.StartTurnAsync(
            thread.ThreadId,
            firstTurnId,
            Guid.CreateVersion7(),
            thread.CurrentSequence,
            cancellationToken);
        await firstService.WaitForExecutionAsync(firstTurnId, cancellationToken);

        var restartedExecutor = new ScriptedExecutor(
            async (context, sink, token) =>
            {
                Assert.Equal(checkpoint, context.CompactionCheckpoint);
                await sink.EmitAsync(new CompleteTurnIntent(), token);
            });
        var restarted = new SessionService(
            runtime,
            journal,
            new SessionProjection(runtime),
            new SessionConfig(),
            executor: restartedExecutor,
            executorKind: "scripted");
        var current = Assert.IsType<ThreadSnapshot>(
            (await restarted.GetThreadAsync(thread.ThreadId, cancellationToken)).Value);
        var secondTurnId = Guid.CreateVersion7();
        await restarted.StartTurnAsync(
            thread.ThreadId,
            secondTurnId,
            Guid.CreateVersion7(),
            current.CurrentSequence,
            cancellationToken);
        await restarted.WaitForExecutionAsync(secondTurnId, cancellationToken);

        var history = Assert.IsType<SessionPage<SessionEvent>>(
            (await restarted.ReadHistoryAsync(
                new ReadHistoryRequest(thread.ThreadId),
                cancellationToken)).Value);
        Assert.Single(
            history.Items,
            item => item.Type == SessionEventType.AgentInvocationSnapshotRecorded);
        Assert.Single(
            history.Items,
            item => item.Type == SessionEventType.ProviderUsageRecorded);
        Assert.Single(
            history.Items,
            item => item.Type == SessionEventType.CompactionCheckpointRecorded);
    }

    [Fact]
    public async Task Starting_a_turn_without_an_executor_is_rejected_without_a_journal_write()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var (_, journal, service) = await CreateServiceAsync(files, cancellationToken);
        var thread = await CreateThreadAsync(service, cancellationToken);

        var result = await service.StartTurnAsync(
            thread.ThreadId,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            thread.CurrentSequence,
            cancellationToken);

        Assert.Equal(SessionCommandStatus.Rejected, result.Status);
        Assert.Equal(SessionErrorCodes.RuntimeExecutorUnavailable, result.Error?.Code);
        var replay = await journal.ReplayAsync(
            ThreadJournalLocation.Active,
            thread.ThreadId,
            cancellationToken);
        Assert.Single(replay.Entries);
    }

    [Fact]
    public async Task Streaming_deltas_flush_at_the_byte_limit_and_before_terminal_facts()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var itemId = Guid.CreateVersion7();
        var executor = new ScriptedExecutor(
            async (_, sink, token) =>
            {
                await sink.EmitAsync(
                    new StartItemIntent(
                        itemId,
                        SessionItemType.AgentMessage,
                        new TextItemContent(string.Empty)),
                    token);
                await sink.EmitAsync(
                    new AppendItemDeltaIntent(itemId, new string('a', 4096)),
                    token);
                await sink.EmitAsync(
                    new AppendItemDeltaIntent(itemId, new string('b', 4096)),
                    token);
                await sink.EmitAsync(new AppendItemDeltaIntent(itemId, "尾"), token);
                await sink.EmitAsync(new CompleteItemIntent(itemId), token);
                await sink.EmitAsync(new CompleteTurnIntent(), token);
            });
        var (_, journal, service) = await CreateServiceAsync(
            files,
            cancellationToken,
            executor);
        var thread = await CreateThreadAsync(service, cancellationToken);
        var turnId = Guid.CreateVersion7();

        var started = await service.StartTurnAsync(
            thread.ThreadId,
            turnId,
            Guid.CreateVersion7(),
            thread.CurrentSequence,
            cancellationToken);
        await service.WaitForExecutionAsync(turnId, cancellationToken);

        Assert.Equal(SessionCommandStatus.Committed, started.Status);
        var replay = await journal.ReplayAsync(
            ThreadJournalLocation.Active,
            thread.ThreadId,
            cancellationToken);
        Assert.Equal(
            [
                SessionEventType.ThreadCreated,
                SessionEventType.TurnStarted,
                SessionEventType.ItemStarted,
                SessionEventType.ItemDeltaAppended,
                SessionEventType.ItemDeltaAppended,
                SessionEventType.ItemCompleted,
                SessionEventType.TurnCompleted,
            ],
            replay.Entries.Select(entry => entry.EntryType));
        var completed = replay.Entries[^2].Payload.Deserialize<ItemCompletedFact>(JsonOptions);
        Assert.NotNull(completed);
        Assert.Equal(8195, completed.ContentLength);
    }

    [Fact]
    public async Task Resolution_is_durable_once_and_resumes_from_the_checkpoint()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var interactionId = Guid.CreateVersion7();
        var checkpoint = SessionExecutionCheckpointCodec.Create(
            "scripted",
            schemaVersion: 1,
            payload: """{"step":1}""");
        var executor = new ScriptedExecutor(
            async (_, sink, token) =>
            {
                await sink.EmitAsync(
                    new WaitForInteractionIntent(
                        interactionId,
                        SessionInteractionType.Approval,
                        new ApprovalRequestContent("Proceed?"),
                        checkpoint,
                        TimeoutAt: null),
                    token);
            },
            async (context, sink, token) =>
            {
                Assert.Equal(checkpoint, context.Checkpoint);
                Assert.Contains(
                    context.ModelHistory,
                    item => item.Content is ApprovalResponseContent { Approved: true });
                await sink.EmitAsync(new CompleteTurnIntent(), token);
            });
        var (_, journal, service) = await CreateServiceAsync(
            files,
            cancellationToken,
            executor);
        var thread = await CreateThreadAsync(service, cancellationToken);
        var turnId = Guid.CreateVersion7();
        await service.StartTurnAsync(
            thread.ThreadId,
            turnId,
            Guid.CreateVersion7(),
            thread.CurrentSequence,
            cancellationToken);
        await service.WaitForExecutionAsync(turnId, cancellationToken);
        var waiting = await service.GetThreadAsync(thread.ThreadId, cancellationToken);
        var key = Guid.CreateVersion7();
        var request = new ResolveInteractionRequest(
            thread.ThreadId,
            turnId,
            interactionId,
            new ApprovalResponseContent(true, "ok"),
            key,
            waiting.Value!.CurrentSequence);

        var resolved = await service.ResolveInteractionAsync(request, cancellationToken);
        var replayed = await service.ResolveInteractionAsync(request, cancellationToken);
        await service.WaitForExecutionAsync(turnId, cancellationToken);

        Assert.Equal(SessionCommandStatus.Committed, resolved.Status);
        Assert.Equal(resolved, replayed);
        var replay = await journal.ReplayAsync(
            ThreadJournalLocation.Active,
            thread.ThreadId,
            cancellationToken);
        Assert.Single(
            replay.Entries,
            entry => entry.EntryType == SessionEventType.InteractionResolved);
        Assert.Contains(
            replay.Entries,
            entry => entry.EntryType == SessionEventType.TurnExecutionResumed);
        Assert.Equal(SessionEventType.TurnCompleted, replay.Entries[^1].EntryType);
        var history = await service.ReadHistoryAsync(
            new ReadHistoryRequest(thread.ThreadId),
            cancellationToken);
        var resolutionEvent = Assert.Single(
            history.Value!.Items,
            sessionEvent =>
                sessionEvent.Type == SessionEventType.InteractionResolved);
        Assert.IsType<ApprovalResponseContent>(resolutionEvent.Payload.Item?.Content);
        Assert.True(resolutionEvent.Payload.Interaction?.IsResolved);
    }

    [Fact]
    public async Task A_committed_resolution_recovers_after_restart_before_resume()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var interactionId = Guid.CreateVersion7();
        var checkpoint = SessionExecutionCheckpointCodec.Create(
            "scripted",
            schemaVersion: 1,
            payload: "resume-me");
        var faulted = false;
        var firstExecutor = new ScriptedExecutor(
            async (_, sink, token) =>
            {
                await sink.EmitAsync(
                    new WaitForInteractionIntent(
                        interactionId,
                        SessionInteractionType.UserInput,
                        new UserInputRequestContent("Value?"),
                        checkpoint,
                        TimeoutAt: null),
                    token);
            });
        var (runtime, journal, firstService) = await CreateServiceAsync(
            files,
            cancellationToken,
            firstExecutor,
            faultInjector: point =>
            {
                if (!faulted &&
                    point == SessionExecutionFaultPoint.AfterResolutionCommittedBeforeResume)
                {
                    faulted = true;
                    throw new InjectedExecutionFaultException();
                }
            });
        var thread = await CreateThreadAsync(firstService, cancellationToken);
        var turnId = Guid.CreateVersion7();
        await firstService.StartTurnAsync(
            thread.ThreadId,
            turnId,
            Guid.CreateVersion7(),
            thread.CurrentSequence,
            cancellationToken);
        await firstService.WaitForExecutionAsync(turnId, cancellationToken);
        var waiting = await firstService.GetThreadAsync(thread.ThreadId, cancellationToken);
        var resolutionRequest = new ResolveInteractionRequest(
            thread.ThreadId,
            turnId,
            interactionId,
            new UserInputResponseContent("42"),
            Guid.CreateVersion7(),
            waiting.Value!.CurrentSequence);

        await Assert.ThrowsAsync<InjectedExecutionFaultException>(() =>
            firstService.ResolveInteractionAsync(
                resolutionRequest,
                cancellationToken));

        var resumedExecutor = new ScriptedExecutor(
            async (context, sink, token) =>
            {
                Assert.Equal(checkpoint, context.Checkpoint);
                await sink.EmitAsync(new CompleteTurnIntent(), token);
            });
        var restarted = new SessionService(
            runtime,
            journal,
            new SessionProjection(runtime),
            new SessionConfig(),
            executor: resumedExecutor,
            executorKind: "scripted");

        var replayedResolution = await restarted.ResolveInteractionAsync(
            resolutionRequest,
            cancellationToken);
        await restarted.RecoverExecutionAsync(thread.ThreadId, cancellationToken);
        await restarted.WaitForExecutionAsync(turnId, cancellationToken);

        Assert.Equal(SessionCommandStatus.Committed, replayedResolution.Status);
        var replay = await journal.ReplayAsync(
            ThreadJournalLocation.Active,
            thread.ThreadId,
            cancellationToken);
        Assert.Contains(
            replay.Entries,
            entry => entry.EntryType == SessionEventType.TurnExecutionResumed);
        Assert.Equal(SessionEventType.TurnCompleted, replay.Entries[^1].EntryType);
    }

    [Fact]
    public async Task Interaction_timeout_uses_the_injected_clock_and_persists_failure()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var clock = new MutableTimeProvider(
            new DateTimeOffset(2026, 7, 26, 0, 0, 0, TimeSpan.Zero));
        var interactionId = Guid.CreateVersion7();
        var checkpoint = SessionExecutionCheckpointCodec.Create("scripted", 1, "timeout");
        var executor = new ScriptedExecutor(
            async (_, sink, token) =>
            {
                await sink.EmitAsync(
                    new WaitForInteractionIntent(
                        interactionId,
                        SessionInteractionType.UserInput,
                        new UserInputRequestContent("Value?"),
                        checkpoint,
                        clock.GetUtcNow().AddMinutes(5)),
                    token);
            });
        var (_, journal, service) = await CreateServiceAsync(
            files,
            cancellationToken,
            executor,
            clock);
        var thread = await CreateThreadAsync(service, cancellationToken);
        var turnId = Guid.CreateVersion7();
        await service.StartTurnAsync(
            thread.ThreadId,
            turnId,
            Guid.CreateVersion7(),
            thread.CurrentSequence,
            cancellationToken);
        await service.WaitForExecutionAsync(turnId, cancellationToken);

        clock.Advance(TimeSpan.FromMinutes(6));
        await service.ProcessInteractionTimeoutsAsync(cancellationToken);

        var replay = await journal.ReplayAsync(
            ThreadJournalLocation.Active,
            thread.ThreadId,
            cancellationToken);
        Assert.Equal(SessionEventType.TurnFailed, replay.Entries[^1].EntryType);
        var failure = replay.Entries[^1].Payload.Deserialize<TurnTerminalFact>(JsonOptions);
        Assert.Equal(SessionErrorCodes.RuntimeInterrupted, failure?.Error?.Code);
    }

    [Fact]
    public async Task Resolution_and_cancel_with_the_same_sequence_have_one_winner()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var interactionId = Guid.CreateVersion7();
        var checkpoint = SessionExecutionCheckpointCodec.Create("scripted", 1, "race");
        var executor = new ScriptedExecutor(
            async (_, sink, token) =>
            {
                await sink.EmitAsync(
                    new WaitForInteractionIntent(
                        interactionId,
                        SessionInteractionType.Approval,
                        new ApprovalRequestContent("Proceed?"),
                        checkpoint,
                        TimeoutAt: null),
                    token);
            },
            async (_, sink, token) =>
            {
                await sink.EmitAsync(new CompleteTurnIntent(), token);
            });
        var (_, journal, service) = await CreateServiceAsync(
            files,
            cancellationToken,
            executor);
        var thread = await CreateThreadAsync(service, cancellationToken);
        var turnId = Guid.CreateVersion7();
        await service.StartTurnAsync(
            thread.ThreadId,
            turnId,
            Guid.CreateVersion7(),
            thread.CurrentSequence,
            cancellationToken);
        await service.WaitForExecutionAsync(turnId, cancellationToken);
        var water = (await service.GetThreadAsync(
            thread.ThreadId,
            cancellationToken)).Value!.CurrentSequence;

        var cancel = service.CancelTurnAsync(
            new CancelTurnRequest(
                thread.ThreadId,
                turnId,
                Guid.CreateVersion7(),
                water),
            cancellationToken);
        var resolve = service.ResolveInteractionAsync(
            new ResolveInteractionRequest(
                thread.ThreadId,
                turnId,
                interactionId,
                new ApprovalResponseContent(true, null),
                Guid.CreateVersion7(),
                water),
            cancellationToken);
        await Task.WhenAll(cancel, resolve);
        var cancelResult = await cancel;
        var resolveResult = await resolve;
        await service.WaitForExecutionAsync(turnId, cancellationToken);

        Assert.Equal(
            1,
            new[] { cancelResult.Status, resolveResult.Status }
                .Count(status => status != SessionCommandStatus.Rejected));
        var replay = await journal.ReplayAsync(
            ThreadJournalLocation.Active,
            thread.ThreadId,
            cancellationToken);
        Assert.Equal(
            1,
            replay.Entries.Count(entry =>
                entry.EntryType is SessionEventType.TurnCancelled
                    or SessionEventType.InteractionResolved));
    }

    [Fact]
    public async Task Executor_failure_becomes_a_durable_runtime_error()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var executor = new ScriptedExecutor(
            (_, _, _) => ValueTask.FromException(
                new InvalidOperationException("provider gone")));
        var (_, journal, service) = await CreateServiceAsync(
            files,
            cancellationToken,
            executor);
        var thread = await CreateThreadAsync(service, cancellationToken);
        var turnId = Guid.CreateVersion7();

        await service.StartTurnAsync(
            thread.ThreadId,
            turnId,
            Guid.CreateVersion7(),
            thread.CurrentSequence,
            cancellationToken);
        await service.WaitForExecutionAsync(turnId, cancellationToken);

        var replay = await journal.ReplayAsync(
            ThreadJournalLocation.Active,
            thread.ThreadId,
            cancellationToken);
        Assert.Equal(SessionEventType.TurnFailed, replay.Entries[^1].EntryType);
        var failure = replay.Entries[^1].Payload.Deserialize<TurnTerminalFact>(JsonOptions);
        Assert.Equal(SessionErrorCodes.RuntimeExecutorUnavailable, failure?.Error?.Code);
    }

    [Fact]
    public async Task Invalid_checkpoint_fails_without_entering_an_unrecoverable_wait()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var executor = new ScriptedExecutor(
            async (_, sink, token) =>
            {
                await sink.EmitAsync(
                    new WaitForInteractionIntent(
                        Guid.CreateVersion7(),
                        SessionInteractionType.UserInput,
                        new UserInputRequestContent("Value?"),
                        new SessionExecutionCheckpoint(
                            "scripted",
                            1,
                            "broken",
                            new string('0', 64)),
                        TimeoutAt: null),
                    token);
            });
        var (_, journal, service) = await CreateServiceAsync(
            files,
            cancellationToken,
            executor);
        var thread = await CreateThreadAsync(service, cancellationToken);
        var turnId = Guid.CreateVersion7();

        await service.StartTurnAsync(
            thread.ThreadId,
            turnId,
            Guid.CreateVersion7(),
            thread.CurrentSequence,
            cancellationToken);
        await service.WaitForExecutionAsync(turnId, cancellationToken);

        var replay = await journal.ReplayAsync(
            ThreadJournalLocation.Active,
            thread.ThreadId,
            cancellationToken);
        Assert.DoesNotContain(
            replay.Entries,
            entry => entry.EntryType is SessionEventType.TurnWaitingApproval
                or SessionEventType.TurnWaitingInput);
        var failure = replay.Entries[^1].Payload.Deserialize<TurnTerminalFact>(JsonOptions);
        Assert.Equal(SessionErrorCodes.RuntimeContinuationMissing, failure?.Error?.Code);
    }

    [Fact]
    public async Task Streaming_delta_flushes_when_the_injected_clock_reaches_the_interval()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var clock = new ManualTimerTimeProvider(
            new DateTimeOffset(2026, 7, 26, 0, 0, 0, TimeSpan.Zero));
        var buffered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var itemId = Guid.CreateVersion7();
        var executor = new ScriptedExecutor(
            async (_, sink, token) =>
            {
                await sink.EmitAsync(
                    new StartItemIntent(
                        itemId,
                        SessionItemType.AgentMessage,
                        new TextItemContent(string.Empty)),
                    token);
                await sink.EmitAsync(new AppendItemDeltaIntent(itemId, "small"), token);
                buffered.SetResult();
                await finish.Task.WaitAsync(token);
                await sink.EmitAsync(new CompleteItemIntent(itemId), token);
                await sink.EmitAsync(new CompleteTurnIntent(), token);
            });
        var (_, journal, service) = await CreateServiceAsync(
            files,
            cancellationToken,
            executor,
            clock);
        var thread = await CreateThreadAsync(service, cancellationToken);
        var turnId = Guid.CreateVersion7();
        await service.StartTurnAsync(
            thread.ThreadId,
            turnId,
            Guid.CreateVersion7(),
            thread.CurrentSequence,
            cancellationToken);
        await buffered.Task.WaitAsync(cancellationToken);
        Assert.DoesNotContain(
            (await journal.ReplayAsync(
                ThreadJournalLocation.Active,
                thread.ThreadId,
                cancellationToken)).Entries,
            entry => entry.EntryType == SessionEventType.ItemDeltaAppended);

        clock.Advance(TimeSpan.FromMilliseconds(50));
        await WaitUntilAsync(
            async () => (await journal.ReplayAsync(
                    ThreadJournalLocation.Active,
                    thread.ThreadId,
                    cancellationToken)).Entries
                .Any(entry => entry.EntryType == SessionEventType.ItemDeltaAppended),
            cancellationToken);
        finish.SetResult();
        await service.WaitForExecutionAsync(turnId, cancellationToken);
    }

    [Fact]
    public async Task Cancel_flushes_buffered_content_before_the_cancel_fact()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var buffered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var itemId = Guid.CreateVersion7();
        var executor = new ScriptedExecutor(
            async (_, sink, token) =>
            {
                await sink.EmitAsync(
                    new StartItemIntent(
                        itemId,
                        SessionItemType.AgentMessage,
                        new TextItemContent(string.Empty)),
                    token);
                await sink.EmitAsync(new AppendItemDeltaIntent(itemId, "keep"), token);
                buffered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            });
        var (_, journal, service) = await CreateServiceAsync(
            files,
            cancellationToken,
            executor);
        var thread = await CreateThreadAsync(service, cancellationToken);
        var turnId = Guid.CreateVersion7();
        await service.StartTurnAsync(
            thread.ThreadId,
            turnId,
            Guid.CreateVersion7(),
            thread.CurrentSequence,
            cancellationToken);
        await buffered.Task.WaitAsync(cancellationToken);
        var water = (await service.GetThreadAsync(
            thread.ThreadId,
            cancellationToken)).Value!.CurrentSequence;

        var cancelled = await service.CancelTurnAsync(
            new CancelTurnRequest(
                thread.ThreadId,
                turnId,
                Guid.CreateVersion7(),
                water),
            cancellationToken);
        await service.WaitForExecutionAsync(turnId, cancellationToken);

        Assert.NotEqual(SessionCommandStatus.Rejected, cancelled.Status);
        var replay = await journal.ReplayAsync(
            ThreadJournalLocation.Active,
            thread.ThreadId,
            cancellationToken);
        Assert.Equal(
            [
                SessionEventType.ItemDeltaAppended,
                SessionEventType.ItemCancelled,
                SessionEventType.TurnCancelled,
            ],
            replay.Entries.TakeLast(3).Select(entry => entry.EntryType));
    }

    private static async Task<ThreadSnapshot> CreateThreadAsync(
        SessionService service,
        CancellationToken cancellationToken)
    {
        var result = await service.CreateThreadAsync(
            new CreateThreadRequest(
                Guid.CreateVersion7(),
                ExpectedSequence: 0,
                DisplayName: "execution"),
            cancellationToken);
        return Assert.IsType<ThreadSnapshot>(result.Value);
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static async Task WaitUntilAsync(
        Func<Task<bool>> condition,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken);
        }

        throw new TimeoutException("Condition was not observed.");
    }

    private static async Task<(StateRuntime Runtime, ThreadJournal Journal, SessionService Service)>
        CreateServiceAsync(
            TempWorkspace files,
            CancellationToken cancellationToken,
            ISessionExecutor? executor = null,
            TimeProvider? timeProvider = null,
            Action<SessionExecutionFaultPoint>? faultInjector = null)
    {
        var runtime = new StateRuntime(files.Paths, TimeSpan.FromSeconds(2));
        await runtime.InitializeAsync(cancellationToken);
        var journal = new ThreadJournal(files.Paths);
        var service = new SessionService(
            runtime,
            journal,
            new SessionProjection(runtime),
            new SessionConfig(),
            timeProvider,
            executor,
            executorKind: executor is null ? null : "scripted",
            executionFaultInjector: faultInjector);
        return (runtime, journal, service);
    }

    private sealed class ScriptedExecutor(
        params Func<AgentSession, ISessionExecutionSink, CancellationToken, ValueTask>[] scripts)
        : ISessionExecutor
    {
        private readonly ConcurrentQueue<
            Func<AgentSession, ISessionExecutionSink, CancellationToken, ValueTask>> _scripts =
            new(scripts);

        public ValueTask ExecuteAsync(
            AgentSession context,
            ISessionExecutionSink sink,
            CancellationToken cancellationToken) =>
            _scripts.TryDequeue(out var script)
                ? script(context, sink, cancellationToken)
                : ValueTask.FromException(
                    new InvalidOperationException("No scripted execution remains."));
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan value) => _utcNow += value;
    }

    private sealed class TempWorkspace : IDisposable
    {
        public TempWorkspace()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"opencowork-execution-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            Paths = new OpenCoWorkPaths(Root);
        }

        public string Root { get; }

        public OpenCoWorkPaths Paths { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class InjectedExecutionFaultException : Exception;
}
