using Microsoft.Data.Sqlite;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Sessions;
using OpenCoWork.Core.State;
using OpenCoWork.Core.Workspaces;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class SessionRuntimeTests
{
    [Fact]
    public async Task Startup_isolates_corrupt_thread_and_recovers_interrupted_turn()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var state = new StateRuntime(files.Paths, TimeSpan.FromSeconds(2));
        await state.InitializeAsync(cancellationToken);
        var journal = new ThreadJournal(files.Paths);
        var projection = new SessionProjection(state);
        var setup = new SessionService(
            state,
            journal,
            projection,
            new SessionConfig());
        var corrupt = (await setup.CreateThreadAsync(
            new CreateThreadRequest(
                Guid.CreateVersion7(),
                ExpectedSequence: 0,
                DisplayName: "corrupt"),
            cancellationToken)).Value!;
        var interrupted = (await setup.CreateThreadAsync(
            new CreateThreadRequest(
                Guid.CreateVersion7(),
                ExpectedSequence: 0,
                DisplayName: "interrupted"),
            cancellationToken)).Value!;
        var turnId = Guid.CreateVersion7();
        await journal.AppendAsync(
            ThreadJournalLocation.Active,
            new ThreadJournalDraft(
                interrupted.ThreadId,
                interrupted.CurrentSequence + 1,
                Guid.CreateVersion7(),
                DateTimeOffset.UtcNow,
                SessionEventType.TurnStarted,
                Guid.CreateVersion7(),
                new TurnStartedFact(
                    turnId,
                    QueueItemId: null,
                    UserItemId: null,
                    Text: null,
                    RequestSha256: new string('a', 64))),
            cancellationToken);
        var corruptPath = Path.Combine(
            files.Paths.ActiveThreadsDirectory,
            $"{corrupt.ThreadId:D}.jsonl");
        var content = await File.ReadAllTextAsync(corruptPath, cancellationToken);
        await File.WriteAllTextAsync(
            corruptPath,
            content.Replace(
                "\"displayName\":\"corrupt\"",
                "\"displayName\":\"damaged\"",
                StringComparison.Ordinal),
            cancellationToken);

        var recoveredProjection = new SessionProjection(state);
        var recoveredService = new SessionService(
            state,
            journal,
            recoveredProjection,
            new SessionConfig());
        var runtime = new SessionRuntime(
            state,
            recoveredService,
            recoveredProjection);

        await runtime.StartAsync(cancellationToken);

        Assert.Contains(corrupt.ThreadId, runtime.RecoveryRequiredThreadIds);
        var corruptSnapshot = await recoveredService.GetThreadAsync(
            corrupt.ThreadId,
            cancellationToken);
        Assert.Equal(
            ThreadAvailability.RecoveryRequired,
            corruptSnapshot.Value!.Availability);
        var interruptedSnapshot = await recoveredService.GetThreadAsync(
            interrupted.ThreadId,
            cancellationToken);
        Assert.Null(interruptedSnapshot.Value!.ActiveTurnId);
        var history = await recoveredService.ReadHistoryAsync(
            new ReadHistoryRequest(
                interrupted.ThreadId,
                AfterSequence: 0,
                PageSize: 100),
            cancellationToken);
        Assert.Equal(SessionEventType.TurnFailed, history.Value!.Items[^1].Type);
        Assert.False(runtime.IsDegraded);
        await runtime.StopAsync(cancellationToken);
    }

    [Fact]
    public async Task Stop_flushes_buffered_delta_terminalizes_turn_and_rejects_new_work()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var state = new StateRuntime(files.Paths, TimeSpan.FromSeconds(2));
        var journal = new ThreadJournal(files.Paths);
        var projection = new SessionProjection(state);
        var executor = new BlockingExecutor();
        var service = new SessionService(
            state,
            journal,
            projection,
            new SessionConfig(),
            executor: executor,
            executorKind: "blocking");
        var runtime = new SessionRuntime(state, service, projection);
        await runtime.StartAsync(cancellationToken);
        var thread = (await service.CreateThreadAsync(
            new CreateThreadRequest(
                Guid.CreateVersion7(),
                ExpectedSequence: 0,
                DisplayName: "shutdown"),
            cancellationToken)).Value!;
        await service.EnqueueInputAsync(
            new EnqueueInputRequest(
                thread.ThreadId,
                Guid.CreateVersion7(),
                thread.CurrentSequence,
                "input"),
            cancellationToken);
        await executor.Started.Task.WaitAsync(cancellationToken);

        await runtime.StopAsync(cancellationToken);

        var stopped = await service.GetThreadAsync(thread.ThreadId, cancellationToken);
        Assert.Null(stopped.Value!.ActiveTurnId);
        var history = await service.ReadHistoryAsync(
            new ReadHistoryRequest(thread.ThreadId, AfterSequence: 0, PageSize: 100),
            cancellationToken);
        Assert.Contains(
            history.Value!.Items,
            item => item.Type == SessionEventType.ItemDeltaAppended);
        Assert.Equal(SessionEventType.TurnFailed, history.Value.Items[^1].Type);
        var rejected = await service.CreateThreadAsync(
            new CreateThreadRequest(
                Guid.CreateVersion7(),
                ExpectedSequence: 0,
                DisplayName: "after stop"),
            cancellationToken);
        Assert.Equal(SessionErrorCodes.RuntimeShuttingDown, rejected.Error?.Code);
    }

    private sealed class BlockingExecutor : ISessionExecutor
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask ExecuteAsync(
            AgentSession context,
            ISessionExecutionSink sink,
            CancellationToken cancellationToken)
        {
            var itemId = Guid.CreateVersion7();
            await sink.EmitAsync(
                new StartItemIntent(
                    itemId,
                    SessionItemType.AgentMessage,
                    new TextItemContent(string.Empty)),
                cancellationToken);
            await sink.EmitAsync(
                new AppendItemDeltaIntent(itemId, "buffered"),
                cancellationToken);
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class TempWorkspace : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-session-runtime-{Guid.NewGuid():N}");

        public TempWorkspace()
        {
            Directory.CreateDirectory(_root);
            Paths = new OpenCoWorkPaths(_root);
        }

        public OpenCoWorkPaths Paths { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
