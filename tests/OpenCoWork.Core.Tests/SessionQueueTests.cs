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

public sealed class SessionQueueTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    [Fact]
    public async Task Paused_queue_reorders_removes_and_replays_idempotently_after_restart()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var (runtime, journal, service) = await CreateServiceAsync(
            files,
            cancellationToken);
        var thread = await CreateThreadAsync(service, cancellationToken);
        var paused = await service.PauseThreadAsync(
            new ThreadMutationRequest(
                thread.ThreadId,
                Guid.CreateVersion7(),
                thread.CurrentSequence),
            cancellationToken);
        var firstRequest = new EnqueueInputRequest(
            thread.ThreadId,
            Guid.CreateVersion7(),
            paused.Value!.CurrentSequence,
            "first");

        var first = await service.EnqueueInputAsync(firstRequest, cancellationToken);
        var firstReplay = await service.EnqueueInputAsync(firstRequest, cancellationToken);
        var second = await EnqueueAtCurrentAsync(
            service,
            thread.ThreadId,
            "second",
            cancellationToken);
        var third = await EnqueueAtCurrentAsync(
            service,
            thread.ThreadId,
            "third",
            cancellationToken);
        var water = (await service.GetThreadAsync(
            thread.ThreadId,
            cancellationToken)).Value!.CurrentSequence;
        var reordered = await service.ReorderQueuedInputsAsync(
            new ReorderQueuedInputsRequest(
                thread.ThreadId,
                [
                    third.Value!.QueueItem.QueueItemId,
                    first.Value!.QueueItem.QueueItemId,
                    second.Value!.QueueItem.QueueItemId,
                ],
                Guid.CreateVersion7(),
                water),
            cancellationToken);
        var invalid = await service.ReorderQueuedInputsAsync(
            new ReorderQueuedInputsRequest(
                thread.ThreadId,
                [
                    third.Value.QueueItem.QueueItemId,
                    second.Value.QueueItem.QueueItemId,
                ],
                Guid.CreateVersion7(),
                reordered.Value!.CurrentSequence),
            cancellationToken);
        var removed = await service.RemoveQueuedInputAsync(
            new RemoveQueuedInputRequest(
                thread.ThreadId,
                first.Value.QueueItem.QueueItemId,
                Guid.CreateVersion7(),
                reordered.Value.CurrentSequence),
            cancellationToken);

        var restarted = new SessionService(
            runtime,
            journal,
            new SessionProjection(runtime),
            new SessionConfig());
        var afterRestart = await restarted.GetThreadAsync(
            thread.ThreadId,
            cancellationToken);
        var replayAfterRestart = await restarted.EnqueueInputAsync(
            firstRequest,
            cancellationToken);

        Assert.Equal(first, firstReplay);
        Assert.Equal(first, replayAfterRestart);
        Assert.Equal(SessionCommandStatus.Rejected, invalid.Status);
        Assert.Equal(
            [
                third.Value.QueueItem.QueueItemId,
                second.Value.QueueItem.QueueItemId,
            ],
            removed.Value!.Queue.Select(item => item.QueueItemId));
        Assert.Equal(
            [
                third.Value.QueueItem.QueueItemId,
                second.Value.QueueItem.QueueItemId,
            ],
            afterRestart.Value!.Queue.Select(item => item.QueueItemId));
        Assert.Equal([0, 1], afterRestart.Value.Queue.Select(item => item.Position));
    }

    [Fact]
    public async Task Correlation_is_journaled_before_turn_start_and_reaches_executor()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var observedContext = new TaskCompletionSource<AgentSession>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new DelegateExecutor(
            async (context, sink, token) =>
            {
                observedContext.SetResult(context);
                await sink.EmitAsync(new CompleteTurnIntent(), token);
            });
        var (_, journal, service) = await CreateServiceAsync(
            files,
            cancellationToken,
            executor);
        var thread = await CreateThreadAsync(service, cancellationToken);
        var correlationId = Guid.CreateVersion7();

        var queued = await service.EnqueueInputAsync(
            new EnqueueInputRequest(
                thread.ThreadId,
                Guid.CreateVersion7(),
                thread.CurrentSequence,
                "run now",
                CorrelationId: correlationId),
            cancellationToken);
        var replay = await journal.ReplayAsync(
            ThreadJournalLocation.Active,
            thread.ThreadId,
            cancellationToken);
        var turnStarted = Assert.Single(
            replay.Entries,
            entry => entry.EntryType == SessionEventType.TurnStarted);
        var turnId = turnStarted.Payload
            .Deserialize<TurnStartedFact>(JsonOptions)!.TurnId;
        await service.WaitForExecutionAsync(turnId, cancellationToken);
        var context = await observedContext.Task.WaitAsync(cancellationToken);
        replay = await journal.ReplayAsync(
            ThreadJournalLocation.Active,
            thread.ThreadId,
            cancellationToken);

        Assert.NotEqual(SessionCommandStatus.Rejected, queued.Status);
        Assert.Equal(correlationId, context.Turn.CorrelationId);
        Assert.Contains(
            context.ModelHistory,
            item => item.Content is TextItemContent { Text: "run now" });
        var entries = replay.Entries.ToList();
        Assert.True(
            entries.FindIndex(entry =>
                entry.EntryType == SessionEventType.TurnQueued) <
            entries.FindIndex(entry =>
                entry.EntryType == SessionEventType.TurnStarted));
        if (replay.Entries[^1].EntryType != SessionEventType.TurnCompleted)
        {
            var failure = replay.Entries[^1].Payload
                .Deserialize<TurnTerminalFact>(JsonOptions);
            Assert.Fail($"{failure?.Error?.Code}: {failure?.Error?.Message}");
        }
        Assert.Empty((await service.GetThreadAsync(
            thread.ThreadId,
            cancellationToken)).Value!.Queue);
    }

    [Fact]
    public async Task Start_only_rejects_a_busy_thread_without_queueing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new DelegateExecutor(
            async (_, _, token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            });
        var (_, _, service) = await CreateServiceAsync(
            files,
            cancellationToken,
            executor);
        var thread = await CreateThreadAsync(service, cancellationToken);

        await service.EnqueueInputAsync(
            new EnqueueInputRequest(
                thread.ThreadId,
                Guid.CreateVersion7(),
                thread.CurrentSequence,
                "first"),
            cancellationToken);
        await started.Task.WaitAsync(cancellationToken);
        var busy = (await service.GetThreadAsync(
            thread.ThreadId,
            cancellationToken)).Value!;
        var result = await service.EnqueueInputAsync(
            new EnqueueInputRequest(
                thread.ThreadId,
                Guid.CreateVersion7(),
                busy.CurrentSequence,
                "must not queue",
                TurnAdmission.StartOnly),
            cancellationToken);
        var after = (await service.GetThreadAsync(
            thread.ThreadId,
            cancellationToken)).Value!;

        Assert.Equal(SessionCommandStatus.Rejected, result.Status);
        Assert.Equal(SessionErrorCodes.ThreadBusy, result.Error?.Code);
        Assert.Equal(busy.CurrentSequence, after.CurrentSequence);
        Assert.Empty(after.Queue);

        await service.CancelTurnAsync(
            new CancelTurnRequest(
                thread.ThreadId,
                busy.ActiveTurnId!.Value,
                Guid.CreateVersion7(),
                busy.CurrentSequence),
            cancellationToken);
    }

    [Fact]
    public async Task Start_only_returns_and_replays_the_started_turn_id()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new DelegateExecutor(
            async (_, _, token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            });
        var (runtime, journal, service) = await CreateServiceAsync(
            files,
            cancellationToken,
            executor);
        var thread = await CreateThreadAsync(service, cancellationToken);
        var request = new EnqueueInputRequest(
            thread.ThreadId,
            Guid.CreateVersion7(),
            thread.CurrentSequence,
            "start now",
            TurnAdmission.StartOnly);

        var result = await service.EnqueueInputAsync(request, cancellationToken);
        await started.Task.WaitAsync(cancellationToken);
        var running = (await service.GetThreadAsync(
            thread.ThreadId,
            cancellationToken)).Value!;

        Assert.NotEqual(SessionCommandStatus.Rejected, result.Status);
        Assert.Equal(running.ActiveTurnId, result.Value!.TurnId);

        await service.CancelTurnAsync(
            new CancelTurnRequest(
                thread.ThreadId,
                result.Value.TurnId!.Value,
                Guid.CreateVersion7(),
                running.CurrentSequence),
            cancellationToken);
        var restarted = new SessionService(
            runtime,
            journal,
            new SessionProjection(runtime),
            new SessionConfig());
        var replay = await restarted.EnqueueInputAsync(request, cancellationToken);

        Assert.Equal(result, replay);
    }

    [Fact]
    public async Task First_text_input_sets_grapheme_safe_title_and_manual_rename_disables_it()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var (_, journal, service) = await CreateServiceAsync(
            files,
            cancellationToken);
        var automatic = (await service.CreateThreadAsync(
            new CreateThreadRequest(
                Guid.CreateVersion7(),
                ExpectedSequence: 0,
                DisplayName: null),
            cancellationToken)).Value!;
        var input = string.Concat(Enumerable.Repeat("👨‍👩‍👧‍👦", 51)) + " searchable";

        await service.EnqueueInputAsync(
            new EnqueueInputRequest(
                automatic.ThreadId,
                Guid.CreateVersion7(),
                automatic.CurrentSequence,
                input),
            cancellationToken);

        var titled = await service.GetThreadAsync(
            automatic.ThreadId,
            cancellationToken);
        Assert.Equal(
            50,
            System.Globalization.StringInfo.ParseCombiningCharacters(
                titled.Value!.DisplayName).Length);
        var search = await service.SearchThreadsAsync(
            new SearchThreadsRequest(
                "searchable",
                Cursor: null,
                PageSize: 100,
                IncludeArchived: false),
            cancellationToken);
        Assert.Contains(
            search.Value!.Items,
            item => item.ThreadId == automatic.ThreadId);
        var replay = await journal.ReplayAsync(
            ThreadJournalLocation.Active,
            automatic.ThreadId,
            cancellationToken);
        Assert.Contains(
            replay.Entries,
            entry => entry.EntryType == SessionEventType.ThreadRenamed);

        var manual = (await service.CreateThreadAsync(
            new CreateThreadRequest(
                Guid.CreateVersion7(),
                ExpectedSequence: 0,
                DisplayName: null),
            cancellationToken)).Value!;
        var renamed = await service.RenameThreadAsync(
            new RenameThreadRequest(
                manual.ThreadId,
                Guid.CreateVersion7(),
                manual.CurrentSequence,
                "New thread"),
            cancellationToken);
        await service.EnqueueInputAsync(
            new EnqueueInputRequest(
                manual.ThreadId,
                Guid.CreateVersion7(),
                renamed.Value!.CurrentSequence,
                "must not overwrite"),
            cancellationToken);
        var unchanged = await service.GetThreadAsync(
            manual.ThreadId,
            cancellationToken);
        Assert.Equal("New thread", unchanged.Value!.DisplayName);
    }

    [Fact]
    public async Task Rejected_steer_keeps_the_committed_user_message_and_fails_the_turn()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var executor = new RejectingSteerExecutor();
        var (_, journal, service) = await CreateServiceAsync(
            files,
            cancellationToken,
            executor);
        var thread = await CreateThreadAsync(service, cancellationToken);
        await service.EnqueueInputAsync(
            new EnqueueInputRequest(
                thread.ThreadId,
                Guid.CreateVersion7(),
                thread.CurrentSequence,
                "first"),
            cancellationToken);
        await executor.Started.Task.WaitAsync(cancellationToken);
        var running = (await service.GetThreadAsync(
            thread.ThreadId,
            cancellationToken)).Value!;
        var queued = await service.EnqueueInputAsync(
            new EnqueueInputRequest(
                thread.ThreadId,
                Guid.CreateVersion7(),
                running.CurrentSequence,
                "steer me"),
            cancellationToken);
        running = (await service.GetThreadAsync(
            thread.ThreadId,
            cancellationToken)).Value!;

        var steered = await service.SteerTurnAsync(
            new SteerTurnRequest(
                thread.ThreadId,
                running.ActiveTurnId!.Value,
                queued.Value!.QueueItem.QueueItemId,
                Guid.CreateVersion7(),
                running.CurrentSequence),
            cancellationToken);
        await service.WaitForExecutionAsync(
            running.ActiveTurnId.Value,
            cancellationToken);

        var replay = await journal.ReplayAsync(
            ThreadJournalLocation.Active,
            thread.ThreadId,
            cancellationToken);
        var entries = replay.Entries.ToList();
        var steerIndex = entries.FindIndex(
            entry => entry.EntryType == SessionEventType.TurnSteered);
        var failureIndex = entries.FindIndex(
            entry => entry.EntryType == SessionEventType.TurnFailed);
        Assert.NotEqual(SessionCommandStatus.Rejected, steered.Status);
        Assert.True(steerIndex >= 0 && steerIndex < failureIndex);
        Assert.Empty((await service.GetThreadAsync(
            thread.ThreadId,
            cancellationToken)).Value!.Queue);
        var history = await service.ReadHistoryAsync(
            new ReadHistoryRequest(thread.ThreadId),
            cancellationToken);
        Assert.Contains(
            history.Value!.Items,
            sessionEvent =>
                sessionEvent.Type == SessionEventType.TurnSteered &&
                sessionEvent.Payload.Item?.Content is TextItemContent { Text: "steer me" });
    }

    [Fact]
    public async Task Queue_rejects_the_129th_pending_input()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var (_, _, service) = await CreateServiceAsync(files, cancellationToken);
        var thread = await CreateThreadAsync(service, cancellationToken);
        var paused = await service.PauseThreadAsync(
            new ThreadMutationRequest(
                thread.ThreadId,
                Guid.CreateVersion7(),
                thread.CurrentSequence),
            cancellationToken);
        var sequence = paused.Value!.CurrentSequence;
        for (var index = 0; index < 128; index++)
        {
            var result = await service.EnqueueInputAsync(
                new EnqueueInputRequest(
                    thread.ThreadId,
                    Guid.CreateVersion7(),
                    sequence,
                    $"item-{index}"),
                cancellationToken);
            Assert.NotEqual(SessionCommandStatus.Rejected, result.Status);
            sequence = result.Sequence!.Value;
        }

        var overflow = await service.EnqueueInputAsync(
            new EnqueueInputRequest(
                thread.ThreadId,
                Guid.CreateVersion7(),
                sequence,
                "overflow"),
            cancellationToken);

        Assert.Equal(SessionCommandStatus.Rejected, overflow.Status);
        Assert.Equal(SessionErrorCodes.QueueFull, overflow.Error?.Code);
    }

    [Fact]
    public async Task Deterministic_random_queue_mutations_rebuild_to_the_same_order()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var (runtime, journal, service) = await CreateServiceAsync(
            files,
            cancellationToken);
        var thread = await CreateThreadAsync(service, cancellationToken);
        var paused = await service.PauseThreadAsync(
            new ThreadMutationRequest(
                thread.ThreadId,
                Guid.CreateVersion7(),
                thread.CurrentSequence),
            cancellationToken);
        var sequence = paused.Value!.CurrentSequence;
        var expected = new List<QueuedTurnInputSnapshot>();
        var random = new Random(20260726);
        for (var operation = 0; operation < 40; operation++)
        {
            var choice = random.Next(100);
            if (expected.Count == 0 || choice < 45)
            {
                var queued = await service.EnqueueInputAsync(
                    new EnqueueInputRequest(
                        thread.ThreadId,
                        Guid.CreateVersion7(),
                        sequence,
                        $"random-{operation}"),
                    cancellationToken);
                expected.Add(queued.Value!.QueueItem);
                sequence = queued.Sequence!.Value;
            }
            else if (choice < 70)
            {
                var index = random.Next(expected.Count);
                var removed = expected[index];
                var result = await service.RemoveQueuedInputAsync(
                    new RemoveQueuedInputRequest(
                        thread.ThreadId,
                        removed.QueueItemId,
                        Guid.CreateVersion7(),
                        sequence),
                    cancellationToken);
                expected.RemoveAt(index);
                sequence = result.Sequence!.Value;
            }
            else
            {
                expected = expected
                    .OrderBy(_ => random.Next())
                    .Select((item, position) => item with { Position = position })
                    .ToList();
                var result = await service.ReorderQueuedInputsAsync(
                    new ReorderQueuedInputsRequest(
                        thread.ThreadId,
                        expected.Select(item => item.QueueItemId).ToArray(),
                        Guid.CreateVersion7(),
                        sequence),
                    cancellationToken);
                sequence = result.Sequence!.Value;
            }
        }

        var replay = await journal.ReplayAsync(
            ThreadJournalLocation.Active,
            thread.ThreadId,
            cancellationToken);
        var rebuiltProjection = new SessionProjection(runtime);
        await rebuiltProjection.RebuildAsync(
            [
                new ThreadJournalSource(
                    ThreadJournalLocation.Active,
                    thread.ThreadId,
                    replay.Entries),
            ],
            cancellationToken);
        var rebuilt = await rebuiltProjection.ReadThreadSnapshotAsync(
            thread.ThreadId,
            cancellationToken);

        Assert.Equal(
            expected.Select(item => item.QueueItemId),
            rebuilt!.Queue.Select(item => item.QueueItemId));
        Assert.Equal(
            Enumerable.Range(0, expected.Count),
            rebuilt.Queue.Select(item => item.Position));
    }

    private static async Task<SessionCommandResult<SubmittedTurnInputSnapshot>>
        EnqueueAtCurrentAsync(
            SessionService service,
            Guid threadId,
            string text,
            CancellationToken cancellationToken)
    {
        var thread = await service.GetThreadAsync(threadId, cancellationToken);
        return await service.EnqueueInputAsync(
            new EnqueueInputRequest(
                threadId,
                Guid.CreateVersion7(),
                thread.Value!.CurrentSequence,
                text),
            cancellationToken);
    }

    private static async Task<ThreadSnapshot> CreateThreadAsync(
        SessionService service,
        CancellationToken cancellationToken)
    {
        var result = await service.CreateThreadAsync(
            new CreateThreadRequest(
                Guid.CreateVersion7(),
                ExpectedSequence: 0,
                DisplayName: "queue"),
            cancellationToken);
        return Assert.IsType<ThreadSnapshot>(result.Value);
    }

    private static async Task<(StateRuntime Runtime, ThreadJournal Journal, SessionService Service)>
        CreateServiceAsync(
            TempWorkspace files,
            CancellationToken cancellationToken,
            ISessionExecutor? executor = null)
    {
        var runtime = new StateRuntime(files.Paths, TimeSpan.FromSeconds(2));
        await runtime.InitializeAsync(cancellationToken);
        var journal = new ThreadJournal(files.Paths);
        var service = new SessionService(
            runtime,
            journal,
            new SessionProjection(runtime),
            new SessionConfig(),
            executor: executor,
            executorKind: executor is null ? null : "test");
        return (runtime, journal, service);
    }

    private sealed class DelegateExecutor(
        Func<AgentSession, ISessionExecutionSink, CancellationToken, ValueTask> execute)
        : ISessionExecutor
    {
        public ValueTask ExecuteAsync(
            AgentSession context,
            ISessionExecutionSink sink,
            CancellationToken cancellationToken) =>
            execute(context, sink, cancellationToken);
    }

    private sealed class RejectingSteerExecutor : ISessionExecutor, ISessionSteerReceiver
    {
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask ExecuteAsync(
            AgentSession context,
            ISessionExecutionSink sink,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public ValueTask SteerAsync(
            Guid turnId,
            SessionItemSnapshot input,
            CancellationToken cancellationToken) =>
            ValueTask.FromException(new InvalidOperationException("rejected"));
    }

    private sealed class TempWorkspace : IDisposable
    {
        public TempWorkspace()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"opencowork-queue-{Guid.NewGuid():N}");
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
}
