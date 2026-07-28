using System.Threading;
using Microsoft.Data.Sqlite;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Sessions;
using OpenCoWork.Core.State;
using OpenCoWork.Core.Workspaces;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class SessionServiceTests
{
    [Fact]
    public async Task Provider_model_validation_rejects_create_and_change_before_commit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var (_, journal, _, service) = await CreateServiceAsync(
            files,
            cancellationToken,
            providerModelValidator: (providerId, modelId) =>
                providerId == "allowed" && modelId == "model"
                    ? null
                    : new SessionError(
                        AgentErrorCodes.ContextInputInvalid,
                        "Provider/model unavailable.",
                        IsRetryable: false));

        var rejectedCreate = await service.CreateThreadAsync(
            new CreateThreadRequest(
                Guid.CreateVersion7(),
                ExpectedSequence: 0,
                ProviderId: "missing",
                ModelId: "model"),
            cancellationToken);
        var created = await service.CreateThreadAsync(
            new CreateThreadRequest(
                Guid.CreateVersion7(),
                ExpectedSequence: 0,
                ProviderId: "allowed",
                ModelId: "model"),
            cancellationToken);
        var rejectedChange = await service.SetThreadModelAsync(
            new SetThreadModelRequest(
                created.Value!.ThreadId,
                Guid.CreateVersion7(),
                created.Value.CurrentSequence,
                ProviderId: "missing",
                ModelId: "model"),
            cancellationToken);

        Assert.Equal(SessionCommandStatus.Rejected, rejectedCreate.Status);
        Assert.Equal(AgentErrorCodes.ContextInputInvalid, rejectedCreate.Error?.Code);
        Assert.Equal(SessionCommandStatus.Rejected, rejectedChange.Status);
        Assert.Equal(AgentErrorCodes.ContextInputInvalid, rejectedChange.Error?.Code);
        Assert.Equal(
            1,
            (await service.GetThreadAsync(
                created.Value.ThreadId,
                cancellationToken)).Value!.CurrentSequence);
        Assert.Single((await journal.ReplayAsync(
            ThreadJournalLocation.Active,
            created.Value.ThreadId,
            cancellationToken)).Entries);
    }

    [Fact]
    public async Task Model_and_mode_changes_persist_while_queued_inputs_keep_their_mode()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var (runtime, journal, projection, service) =
            await CreateServiceAsync(files, cancellationToken);
        var thread = Assert.IsType<ThreadSnapshot>((await service.CreateThreadAsync(
            new CreateThreadRequest(
                Guid.CreateVersion7(),
                ExpectedSequence: 0,
                DisplayName: "configured",
                ProviderId: "qwen",
                ModelId: "qwen3.8",
                AgentMode: AgentMode.Plan),
            cancellationToken)).Value);
        var modelChanged = await service.SetThreadModelAsync(
            new SetThreadModelRequest(
                thread.ThreadId,
                Guid.CreateVersion7(),
                ExpectedSequence: 1,
                ProviderId: "deepseek",
                ModelId: "deepseek-v4-pro"),
            cancellationToken);
        var paused = await service.PauseThreadAsync(
            new ThreadMutationRequest(
                thread.ThreadId,
                Guid.CreateVersion7(),
                ExpectedSequence: 2),
            cancellationToken);
        var queued = await service.EnqueueInputAsync(
            new EnqueueInputRequest(
                thread.ThreadId,
                Guid.CreateVersion7(),
                ExpectedSequence: 3,
                Text: "keep plan"),
            cancellationToken);
        var modeChanged = await service.SetAgentModeAsync(
            new SetAgentModeRequest(
                thread.ThreadId,
                Guid.CreateVersion7(),
                ExpectedSequence: 4,
                AgentMode.Agent),
            cancellationToken);

        Assert.Equal(SessionCommandStatus.Committed, modelChanged.Status);
        Assert.Equal(SessionCommandStatus.Committed, paused.Status);
        Assert.Equal(SessionCommandStatus.Committed, queued.Status);
        Assert.Equal(SessionCommandStatus.Committed, modeChanged.Status);
        var restarted = new SessionService(
            runtime,
            journal,
            projection,
            new SessionConfig());
        var current = Assert.IsType<ThreadSnapshot>(
            (await restarted.GetThreadAsync(thread.ThreadId, cancellationToken)).Value);
        Assert.Equal("deepseek", current.ProviderId);
        Assert.Equal("deepseek-v4-pro", current.ModelId);
        Assert.Equal(AgentMode.Agent, current.AgentMode);
        Assert.Equal(
            AgentMode.Plan,
            Assert.Single(current.Queue).EffectiveAgentMode);
    }

    [Fact]
    public async Task Thread_management_is_sequenced_globally_idempotent_and_restart_stable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var (runtime, journal, projection, service) =
            await CreateServiceAsync(files, cancellationToken);
        var createKey = Guid.CreateVersion7();
        var createRequest = new CreateThreadRequest(createKey, 0, "Alpha");

        var created = await service.CreateThreadAsync(createRequest, cancellationToken);
        var replayed = await service.CreateThreadAsync(createRequest, cancellationToken);

        Assert.Equal(SessionCommandStatus.Committed, created.Status);
        Assert.Equal(1, created.Sequence);
        Assert.Equal(created.Value, replayed.Value);
        Assert.Equal(created.Sequence, replayed.Sequence);
        var threadId = Assert.IsType<ThreadSnapshot>(created.Value).ThreadId;
        Assert.Single((await journal.ReplayAsync(
            ThreadJournalLocation.Active,
            threadId,
            cancellationToken)).Entries);

        var reusedKey = await service.RenameThreadAsync(
            new RenameThreadRequest(threadId, createKey, 1, "Conflict"),
            cancellationToken);
        Assert.Equal(SessionCommandStatus.Rejected, reusedKey.Status);
        Assert.Equal(SessionErrorCodes.IdempotencyConflict, reusedKey.Error?.Code);

        var stale = await service.RenameThreadAsync(
            new RenameThreadRequest(
                threadId,
                Guid.CreateVersion7(),
                0,
                "Stale"),
            cancellationToken);
        Assert.Equal(SessionCommandStatus.Rejected, stale.Status);
        Assert.Equal(SessionErrorCodes.SequenceConflict, stale.Error?.Code);
        Assert.Equal(1, stale.CurrentSequence);

        var renamed = await service.RenameThreadAsync(
            new RenameThreadRequest(
                threadId,
                Guid.CreateVersion7(),
                1,
                "Renamed"),
            cancellationToken);
        var paused = await service.PauseThreadAsync(
            new ThreadMutationRequest(
                threadId,
                Guid.CreateVersion7(),
                2),
            cancellationToken);
        var resumed = await service.ResumeThreadAsync(
            new ThreadMutationRequest(
                threadId,
                Guid.CreateVersion7(),
                3),
            cancellationToken);
        Assert.Equal(2, renamed.Sequence);
        Assert.Equal(3, paused.Sequence);
        Assert.Equal(4, resumed.Sequence);

        var restarted = new SessionService(
            runtime,
            journal,
            projection,
            new SessionConfig());
        var replayedAfterRestart = await restarted.CreateThreadAsync(
            createRequest,
            cancellationToken);

        Assert.Equal(SessionCommandStatus.Committed, replayedAfterRestart.Status);
        Assert.Equal(1, replayedAfterRestart.Sequence);
        Assert.Equal("Alpha", replayedAfterRestart.Value?.DisplayName);
        Assert.Equal(1, replayedAfterRestart.Value?.CurrentSequence);

        var current = await service.GetThreadAsync(threadId, cancellationToken);
        Assert.Equal("Renamed", current.Value?.DisplayName);
        Assert.Equal(ThreadStatus.Active, current.Value?.Status);
        Assert.Equal(4, current.Value?.CurrentSequence);
    }

    [Fact]
    public async Task Queries_use_stable_cursors_unicode_search_and_journal_history()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var (_, _, _, service) = await CreateServiceAsync(files, cancellationToken);
        foreach (var name in new[] { "Alpha", "Beta", "Café" })
        {
            await service.CreateThreadAsync(
                new CreateThreadRequest(Guid.CreateVersion7(), 0, name),
                cancellationToken);
        }

        var first = await service.ListThreadsAsync(
            new ListThreadsRequest(PageSize: 2),
            cancellationToken);
        Assert.True(first.IsSuccess);
        Assert.Equal(2, first.Value?.Items.Count);
        Assert.NotNull(first.Value?.NextCursor);
        var second = await service.ListThreadsAsync(
            new ListThreadsRequest(Cursor: first.Value!.NextCursor, PageSize: 2),
            cancellationToken);
        Assert.Single(second.Value!.Items);
        Assert.Empty(first.Value.Items.Select(item => item.ThreadId)
            .Intersect(second.Value.Items.Select(item => item.ThreadId)));

        var search = await service.SearchThreadsAsync(
            new SearchThreadsRequest("Cafe\u0301"),
            cancellationToken);
        Assert.Equal("Café", Assert.Single(search.Value!.Items).DisplayName);

        var statistics = await service.GetSessionStatisticsAsync(cancellationToken);
        Assert.Equal(3, statistics.Value?.ThreadCount);
        Assert.Equal(3, statistics.Value?.ActiveThreadCount);

        var alpha = (await service.SearchThreadsAsync(
            new SearchThreadsRequest("alpha"),
            cancellationToken)).Value!.Items[0];
        var history = await service.ReadHistoryAsync(
            new ReadHistoryRequest(alpha.ThreadId),
            cancellationToken);
        var created = Assert.Single(history.Value!.Items);
        Assert.Equal(SessionEventType.ThreadCreated, created.Type);
        Assert.Equal(1, created.Sequence);

        var invalidCursor = await service.ListThreadsAsync(
            new ListThreadsRequest(Cursor: "not-a-cursor"),
            cancellationToken);
        Assert.Equal(SessionErrorCodes.InvalidCursor, invalidCursor.Error?.Code);
    }

    [Fact]
    public async Task Different_threads_write_concurrently_and_same_thread_conflicts_are_serialized()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var (runtime, _, projection, initial) =
            await CreateServiceAsync(files, cancellationToken);
        var first = (await initial.CreateThreadAsync(
            new CreateThreadRequest(Guid.CreateVersion7(), 0, "One"),
            cancellationToken)).Value!;
        var second = (await initial.CreateThreadAsync(
            new CreateThreadRequest(Guid.CreateVersion7(), 0, "Two"),
            cancellationToken)).Value!;
        using var barrier = new Barrier(2);
        var concurrentJournal = new ThreadJournal(
            files.Paths,
            point =>
            {
                if (point == ThreadJournalFaultPoint.BeforeWrite)
                {
                    Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(5)));
                }
            });
        var concurrent = new SessionService(
            runtime,
            concurrentJournal,
            projection,
            new SessionConfig());

        var differentThreadResults = await Task.WhenAll(
            Task.Run(
                () => concurrent.RenameThreadAsync(
                    new RenameThreadRequest(
                        first.ThreadId,
                        Guid.CreateVersion7(),
                        1,
                        "One updated"),
                    cancellationToken),
                cancellationToken),
            Task.Run(
                () => concurrent.RenameThreadAsync(
                    new RenameThreadRequest(
                        second.ThreadId,
                        Guid.CreateVersion7(),
                        1,
                        "Two updated"),
                    cancellationToken),
                cancellationToken));
        Assert.All(
            differentThreadResults,
            result => Assert.Equal(SessionCommandStatus.Committed, result.Status));

        var serialized = new SessionService(
            runtime,
            new ThreadJournal(files.Paths),
            projection,
            new SessionConfig());
        var sameThreadResults = await Task.WhenAll(
            serialized.RenameThreadAsync(
                new RenameThreadRequest(
                    first.ThreadId,
                    Guid.CreateVersion7(),
                    2,
                    "Winner A"),
                cancellationToken),
            serialized.RenameThreadAsync(
                new RenameThreadRequest(
                    first.ThreadId,
                    Guid.CreateVersion7(),
                    2,
                    "Winner B"),
                cancellationToken));
        Assert.Single(
            sameThreadResults,
            result => result.Status == SessionCommandStatus.Committed);
        var rejected = Assert.Single(
            sameThreadResults,
            result => result.Status == SessionCommandStatus.Rejected);
        Assert.Equal(SessionErrorCodes.SequenceConflict, rejected.Error?.Code);
        Assert.Equal(3, rejected.CurrentSequence);
    }

    [Fact]
    public async Task Subscriptions_resume_without_gaps_and_disconnect_only_the_slow_consumer()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var (_, _, _, service) = await CreateServiceAsync(
            files,
            cancellationToken,
            eventBufferCapacity: 1);
        var thread = (await service.CreateThreadAsync(
            new CreateThreadRequest(Guid.CreateVersion7(), 0, "Live"),
            cancellationToken)).Value!;
        var slow = await service.SubscribeAsync(
            new SessionSubscriptionRequest(
                thread.ThreadId,
                SessionSubscriptionMode.SnapshotThenLive),
            cancellationToken);
        await service.RenameThreadAsync(
            new RenameThreadRequest(
                thread.ThreadId,
                Guid.CreateVersion7(),
                1,
                "Live 2"),
            cancellationToken);
        await service.PauseThreadAsync(
            new ThreadMutationRequest(
                thread.ThreadId,
                Guid.CreateVersion7(),
                2),
            cancellationToken);

        await using (var enumerator =
                     slow.Events.GetAsyncEnumerator(cancellationToken))
        {
            Assert.True(await enumerator.MoveNextAsync());
            var lagged = await Assert.ThrowsAsync<SessionSubscriptionException>(
                async () => await enumerator.MoveNextAsync().AsTask());
            Assert.Equal(SessionErrorCodes.SubscriberLagged, lagged.Error.Code);
        }

        var resumed = await service.SubscribeAsync(
            new SessionSubscriptionRequest(
                thread.ThreadId,
                SessionSubscriptionMode.ResumeAfterSequence,
                AfterSequence: 0),
            cancellationToken);
        Assert.Equal(SessionSubscriptionDisposition.Ready, resumed.Disposition);
        var sequences = new List<long>();
        await using (var enumerator =
                     resumed.Events.GetAsyncEnumerator(cancellationToken))
        {
            while (sequences.Count < 3 && await enumerator.MoveNextAsync())
            {
                sequences.Add(enumerator.Current.Sequence);
            }
        }

        Assert.Equal([1L, 2L, 3L], sequences);
        var reset = await service.SubscribeAsync(
            new SessionSubscriptionRequest(
                thread.ThreadId,
                SessionSubscriptionMode.ResumeAfterSequence,
                AfterSequence: 99),
            cancellationToken);
        Assert.Equal(SessionSubscriptionDisposition.ResetRequired, reset.Disposition);
        Assert.Equal(3, reset.CurrentSequence);
    }

    [Fact]
    public async Task Projection_failure_returns_committed_pending_blocks_queries_and_recovers()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var runtime = new StateRuntime(files.Paths, TimeSpan.FromSeconds(2));
        await runtime.InitializeAsync(cancellationToken);
        var inject = true;
        var projection = new SessionProjection(
            runtime,
            point =>
            {
                if (inject && point == SessionProjectionFaultPoint.BeforeCommit)
                {
                    inject = false;
                    throw new InjectedProjectionFaultException();
                }
            });
        var service = new SessionService(
            runtime,
            new ThreadJournal(files.Paths),
            projection,
            new SessionConfig());

        var created = await service.CreateThreadAsync(
            new CreateThreadRequest(Guid.CreateVersion7(), 0, "Pending"),
            cancellationToken);

        Assert.Equal(
            SessionCommandStatus.CommittedPendingProjection,
            created.Status);
        Assert.Equal(SessionErrorCodes.ProjectionUnavailable, created.Error?.Code);
        Assert.Equal(
            SessionProjectionState.Degraded,
            created.Value?.ProjectionState);
        var listed = await service.ListThreadsAsync(
            new ListThreadsRequest(),
            cancellationToken);
        Assert.Equal(SessionErrorCodes.ProjectionUnavailable, listed.Error?.Code);
        var threadId = created.Value!.ThreadId;
        Assert.True(await service.RecoverProjectionAsync(threadId, cancellationToken));
        Assert.True((await service.ListThreadsAsync(
            new ListThreadsRequest(),
            cancellationToken)).IsSuccess);
        Assert.Equal(
            SessionProjectionState.Ready,
            (await service.GetThreadAsync(threadId, cancellationToken))
            .Value?.ProjectionState);
    }

    [Fact]
    public async Task Create_idempotency_rebuilds_a_missing_projection_from_the_journal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var runtime = new StateRuntime(files.Paths, TimeSpan.FromSeconds(2));
        await runtime.InitializeAsync(cancellationToken);
        var inject = true;
        var projection = new SessionProjection(
            runtime,
            point =>
            {
                if (inject && point == SessionProjectionFaultPoint.BeforeCommit)
                {
                    inject = false;
                    throw new InjectedProjectionFaultException();
                }
            });
        var journal = new ThreadJournal(files.Paths);
        var request = new CreateThreadRequest(
            Guid.CreateVersion7(),
            0,
            "Recover key");
        var first = await new SessionService(
                runtime,
                journal,
                projection,
                new SessionConfig())
            .CreateThreadAsync(request, cancellationToken);
        Assert.Equal(
            SessionCommandStatus.CommittedPendingProjection,
            first.Status);

        var replayed = await new SessionService(
                runtime,
                journal,
                projection,
                new SessionConfig())
            .CreateThreadAsync(request, cancellationToken);

        Assert.Equal(SessionCommandStatus.Committed, replayed.Status);
        Assert.Equal(first.Value?.ThreadId, replayed.Value?.ThreadId);
        Assert.Equal(1, replayed.Sequence);
        Assert.Single((await journal.ReplayAsync(
            ThreadJournalLocation.Active,
            replayed.Value!.ThreadId,
            cancellationToken)).Entries);
    }

    private static async Task<(
        StateRuntime Runtime,
        ThreadJournal Journal,
        SessionProjection Projection,
        SessionService Service)> CreateServiceAsync(
        TempWorkspace files,
        CancellationToken cancellationToken,
        int eventBufferCapacity = 256,
        Func<string, string, SessionError?>? providerModelValidator = null)
    {
        var runtime = new StateRuntime(files.Paths, TimeSpan.FromSeconds(2));
        await runtime.InitializeAsync(cancellationToken);
        var journal = new ThreadJournal(files.Paths);
        var projection = new SessionProjection(runtime);
        var service = new SessionService(
            runtime,
            journal,
            projection,
            new SessionConfig
            {
                EventBufferCapacity = eventBufferCapacity,
            },
            providerModelValidator: providerModelValidator);
        return (runtime, journal, projection, service);
    }

    private sealed class TempWorkspace : IDisposable
    {
        public TempWorkspace()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"opencowork-service-{Guid.NewGuid():N}");
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

    private sealed class InjectedProjectionFaultException : Exception;
}
