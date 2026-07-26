using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Sessions;
using OpenCoWork.Core.State;
using OpenCoWork.Core.Workspaces;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class SessionRecoveryTests
{
    [Theory]
    [InlineData((int)SessionRecoveryFaultPoint.AfterFactFlushed)]
    [InlineData((int)SessionRecoveryFaultPoint.AfterJournalMoved)]
    [InlineData((int)SessionRecoveryFaultPoint.AfterProjectionApplied)]
    public async Task Archive_reconciles_from_each_committed_phase(
        int faultValue)
    {
        var faultPoint = (SessionRecoveryFaultPoint)faultValue;
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var runtime = await CreateRuntimeAsync(files, cancellationToken);
        var journal = new ThreadJournal(files.Paths);
        var projection = new SessionProjection(runtime);
        var service = new SessionService(
            runtime,
            journal,
            projection,
            new SessionConfig(),
            recoveryFaultInjector: point =>
            {
                if (point == faultPoint)
                {
                    throw new InjectedRecoveryFaultException();
                }
            });
        var thread = await CreateThreadAsync(service, cancellationToken);
        var request = new ThreadMutationRequest(
            thread.ThreadId,
            Guid.CreateVersion7(),
            thread.CurrentSequence);

        await Assert.ThrowsAsync<InjectedRecoveryFaultException>(
            () => service.ArchiveThreadAsync(request, cancellationToken));
        var retried = await service.ArchiveThreadAsync(
            request,
            cancellationToken);
        Assert.Equal(SessionCommandStatus.Committed, retried.Status);
        var recovered = new SessionService(
            runtime,
            journal,
            new SessionProjection(runtime),
            new SessionConfig());
        var failures = await recovered.RecoverSessionStateAsync(cancellationToken);
        var snapshot = await recovered.GetThreadAsync(
            thread.ThreadId,
            cancellationToken);

        Assert.Empty(failures);
        Assert.Equal(ThreadStatus.Archived, snapshot.Value!.Status);
        Assert.False(journal.Exists(ThreadJournalLocation.Active, thread.ThreadId));
        Assert.True(journal.Exists(ThreadJournalLocation.Archived, thread.ThreadId));

        var unarchived = await recovered.UnarchiveThreadAsync(
            new ThreadMutationRequest(
                thread.ThreadId,
                Guid.CreateVersion7(),
                snapshot.Value.CurrentSequence),
            cancellationToken);
        Assert.Equal(ThreadStatus.Active, unarchived.Value!.Status);
        Assert.True(journal.Exists(ThreadJournalLocation.Active, thread.ThreadId));
        Assert.False(journal.Exists(ThreadJournalLocation.Archived, thread.ThreadId));
    }

    [Fact]
    public async Task Delete_token_is_bound_expires_and_replays_from_minimal_receipt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var clock = new MutableTimeProvider(
            new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero));
        var runtime = await CreateRuntimeAsync(files, cancellationToken);
        var journal = new ThreadJournal(files.Paths);
        var projection = new SessionProjection(runtime);
        var service = new SessionService(
            runtime,
            journal,
            projection,
            new SessionConfig(),
            clock);
        var thread = await CreateThreadAsync(service, cancellationToken);
        var archived = await ArchiveAsync(service, thread, cancellationToken);
        var prepared = await service.PrepareDeleteAsync(
            new PrepareDeleteRequest(
                thread.ThreadId,
                archived.CurrentSequence),
            cancellationToken);
        var conflictingKey = Guid.CreateVersion7();
        await service.CreateThreadAsync(
            new CreateThreadRequest(
                conflictingKey,
                ExpectedSequence: 0,
                DisplayName: "idempotency-owner"),
            cancellationToken);
        var foreignOperation = await service.DeleteThreadAsync(
            new DeleteThreadRequest(
                thread.ThreadId,
                conflictingKey,
                archived.CurrentSequence,
                prepared.Value!.Token),
            cancellationToken);
        var idempotencyKey = Guid.CreateVersion7();
        var other = await CreateThreadAsync(service, cancellationToken);
        var otherArchived = await ArchiveAsync(service, other, cancellationToken);
        var wrongThread = await service.DeleteThreadAsync(
            new DeleteThreadRequest(
                other.ThreadId,
                Guid.CreateVersion7(),
                otherArchived.CurrentSequence,
                prepared.Value.Token),
            cancellationToken);
        var wrong = await service.DeleteThreadAsync(
            new DeleteThreadRequest(
                thread.ThreadId,
                idempotencyKey,
                archived.CurrentSequence,
                "wrong"),
            cancellationToken);
        var deleted = await service.DeleteThreadAsync(
            new DeleteThreadRequest(
                thread.ThreadId,
                idempotencyKey,
                archived.CurrentSequence,
                prepared.Value.Token),
            cancellationToken);
        var replayed = await service.DeleteThreadAsync(
            new DeleteThreadRequest(
                thread.ThreadId,
                idempotencyKey,
                archived.CurrentSequence,
                "already-consumed"),
            cancellationToken);
        var conflict = await service.DeleteThreadAsync(
            new DeleteThreadRequest(
                Guid.CreateVersion7(),
                idempotencyKey,
                archived.CurrentSequence,
                "unrelated"),
            cancellationToken);

        Assert.Equal(SessionErrorCodes.DeleteTokenInvalid, wrongThread.Error?.Code);
        Assert.Equal(SessionErrorCodes.DeleteTokenInvalid, wrong.Error?.Code);
        Assert.Equal(SessionErrorCodes.IdempotencyConflict, foreignOperation.Error?.Code);
        Assert.Equal(SessionCommandStatus.Committed, deleted.Status);
        Assert.Equal(deleted, replayed);
        Assert.Equal(SessionErrorCodes.IdempotencyConflict, conflict.Error?.Code);
        Assert.False(journal.Exists(ThreadJournalLocation.Deleting, thread.ThreadId));
        Assert.False((await service.GetThreadAsync(
            thread.ThreadId,
            cancellationToken)).IsSuccess);

        var expiring = await CreateThreadAsync(service, cancellationToken);
        var expiringArchived = await ArchiveAsync(
            service,
            expiring,
            cancellationToken);
        var expiringToken = await service.PrepareDeleteAsync(
            new PrepareDeleteRequest(
                expiring.ThreadId,
                expiringArchived.CurrentSequence),
            cancellationToken);
        clock.Advance(TimeSpan.FromMinutes(3));
        var expired = await service.DeleteThreadAsync(
            new DeleteThreadRequest(
                expiring.ThreadId,
                Guid.CreateVersion7(),
                expiringArchived.CurrentSequence,
                expiringToken.Value!.Token),
            cancellationToken);
        Assert.Equal(SessionErrorCodes.DeleteTokenExpired, expired.Error?.Code);
    }

    [Theory]
    [InlineData((int)SessionRecoveryFaultPoint.AfterFactFlushed)]
    [InlineData((int)SessionRecoveryFaultPoint.AfterJournalMoved)]
    [InlineData((int)SessionRecoveryFaultPoint.AfterProjectionApplied)]
    public async Task Unarchive_reconciles_from_each_committed_phase(
        int faultValue)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var runtime = await CreateRuntimeAsync(files, cancellationToken);
        var journal = new ThreadJournal(files.Paths);
        var setup = new SessionService(
            runtime,
            journal,
            new SessionProjection(runtime),
            new SessionConfig());
        var thread = await CreateThreadAsync(setup, cancellationToken);
        var archived = await ArchiveAsync(setup, thread, cancellationToken);
        var faultPoint = (SessionRecoveryFaultPoint)faultValue;
        var service = new SessionService(
            runtime,
            journal,
            new SessionProjection(runtime),
            new SessionConfig(),
            recoveryFaultInjector: point =>
            {
                if (point == faultPoint)
                {
                    throw new InjectedRecoveryFaultException();
                }
            });

        await Assert.ThrowsAsync<InjectedRecoveryFaultException>(
            () => service.UnarchiveThreadAsync(
                new ThreadMutationRequest(
                    thread.ThreadId,
                    Guid.CreateVersion7(),
                    archived.CurrentSequence),
                cancellationToken));
        var recovered = new SessionService(
            runtime,
            journal,
            new SessionProjection(runtime),
            new SessionConfig());
        Assert.Empty(await recovered.RecoverSessionStateAsync(cancellationToken));
        var snapshot = await recovered.GetThreadAsync(
            thread.ThreadId,
            cancellationToken);
        Assert.Equal(ThreadStatus.Active, snapshot.Value!.Status);
        Assert.True(journal.Exists(ThreadJournalLocation.Active, thread.ThreadId));
    }

    [Theory]
    [InlineData((int)SessionRecoveryFaultPoint.AfterFactFlushed)]
    [InlineData((int)SessionRecoveryFaultPoint.AfterJournalMoved)]
    [InlineData((int)SessionRecoveryFaultPoint.AfterProjectionApplied)]
    [InlineData((int)SessionRecoveryFaultPoint.AfterDeletionMarked)]
    [InlineData((int)SessionRecoveryFaultPoint.AfterOwnedFilesDeleted)]
    [InlineData((int)SessionRecoveryFaultPoint.AfterProjectionDeleted)]
    [InlineData((int)SessionRecoveryFaultPoint.AfterJournalDeleted)]
    [InlineData((int)SessionRecoveryFaultPoint.AfterReceiptWritten)]
    public async Task Delete_reconciles_from_each_committed_phase(int faultValue)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var runtime = await CreateRuntimeAsync(files, cancellationToken);
        var journal = new ThreadJournal(files.Paths);
        var setup = new SessionService(
            runtime,
            journal,
            new SessionProjection(runtime),
            new SessionConfig());
        var thread = await CreateThreadAsync(setup, cancellationToken);
        var archived = await ArchiveAsync(setup, thread, cancellationToken);
        var faultPoint = (SessionRecoveryFaultPoint)faultValue;
        var deleting = new SessionService(
            runtime,
            journal,
            new SessionProjection(runtime),
            new SessionConfig(),
            recoveryFaultInjector: point =>
            {
                if (point == faultPoint)
                {
                    throw new InjectedRecoveryFaultException();
                }
            });
        var prepared = await deleting.PrepareDeleteAsync(
            new PrepareDeleteRequest(
                thread.ThreadId,
                archived.CurrentSequence),
            cancellationToken);
        var idempotencyKey = Guid.CreateVersion7();
        try
        {
            await deleting.DeleteThreadAsync(
                new DeleteThreadRequest(
                    thread.ThreadId,
                    idempotencyKey,
                    archived.CurrentSequence,
                    prepared.Value!.Token),
                cancellationToken);
        }
        catch (InjectedRecoveryFaultException)
        {
        }

        await deleting.DeleteThreadAsync(
            new DeleteThreadRequest(
                thread.ThreadId,
                idempotencyKey,
                archived.CurrentSequence,
                "token-no-longer-required"),
            cancellationToken);
        var recovered = new SessionService(
            runtime,
            journal,
            new SessionProjection(runtime),
            new SessionConfig());
        Assert.Empty(await recovered.RecoverSessionStateAsync(cancellationToken));
        var replayed = await recovered.DeleteThreadAsync(
            new DeleteThreadRequest(
                thread.ThreadId,
                idempotencyKey,
                archived.CurrentSequence,
                "no-longer-required"),
            cancellationToken);
        Assert.Equal(SessionCommandStatus.Committed, replayed.Status);
        Assert.False(journal.Exists(ThreadJournalLocation.Deleting, thread.ThreadId));
    }

    [Fact]
    public async Task Fork_survives_source_delete_and_rollback_replaces_model_history()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var runtime = await CreateRuntimeAsync(files, cancellationToken);
        var journal = new ThreadJournal(files.Paths);
        var executor = new RecordingExecutor();
        var service = new SessionService(
            runtime,
            journal,
            new SessionProjection(runtime),
            new SessionConfig(),
            executor: executor,
            executorKind: "test");
        var source = await CreateThreadAsync(service, cancellationToken);
        var afterFirst = await RunInputAsync(
            service,
            executor,
            source,
            "first",
            cancellationToken);
        var afterSecond = await RunInputAsync(
            service,
            executor,
            afterFirst,
            "second",
            cancellationToken);

        var forked = await service.ForkThreadAsync(
            new ForkThreadRequest(
                source.ThreadId,
                afterFirst.CurrentSequence,
                afterSecond.CurrentSequence,
                Guid.CreateVersion7(),
                "fork"),
            cancellationToken);
        var rolledBack = await service.RollbackThreadAsync(
            new RollbackThreadRequest(
                source.ThreadId,
                afterFirst.CurrentSequence,
                afterSecond.CurrentSequence,
                Guid.CreateVersion7()),
            cancellationToken);
        var afterRollback = await RunInputAsync(
            service,
            executor,
            rolledBack.Value!.Thread,
            "third",
            cancellationToken);
        var rollbackContext = executor.Last!;

        Assert.False(rolledBack.Value.ExternalSideEffectsReverted);
        Assert.Contains(
            rollbackContext.ModelHistory,
            item => item.Content is TextItemContent { Text: "first" });
        Assert.DoesNotContain(
            rollbackContext.ModelHistory,
            item => item.Content is TextItemContent { Text: "second" });

        var archived = await ArchiveAsync(
            service,
            afterRollback,
            cancellationToken);
        var prepared = await service.PrepareDeleteAsync(
            new PrepareDeleteRequest(
                source.ThreadId,
                archived.CurrentSequence),
            cancellationToken);
        await service.DeleteThreadAsync(
            new DeleteThreadRequest(
                source.ThreadId,
                Guid.CreateVersion7(),
                archived.CurrentSequence,
                prepared.Value!.Token),
            cancellationToken);

        var restartedExecutor = new RecordingExecutor();
        var restarted = new SessionService(
            runtime,
            journal,
            new SessionProjection(runtime),
            new SessionConfig(),
            executor: restartedExecutor,
            executorKind: "test");
        Assert.Empty(await restarted.RecoverSessionStateAsync(cancellationToken));
        var forkSnapshot = (await restarted.GetThreadAsync(
            forked.Value!.ThreadId,
            cancellationToken)).Value!;
        await RunInputAsync(
            restarted,
            restartedExecutor,
            forkSnapshot,
            "fork-next",
            cancellationToken);
        var forkContext = restartedExecutor.Last!;
        Assert.Contains(
            forkContext.ModelHistory,
            item => item.Content is TextItemContent { Text: "first" });
        Assert.DoesNotContain(
            forkContext.ModelHistory,
            item => item.Content is TextItemContent { Text: "second" });
    }

    [Fact]
    public async Task Delete_refuses_reparse_escape_and_reconciles_after_it_is_removed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var outside = Path.Combine(files.Root, "outside");
        Directory.CreateDirectory(outside);
        var protectedFile = Path.Combine(outside, "keep.txt");
        await File.WriteAllTextAsync(protectedFile, "keep", cancellationToken);
        var runtime = await CreateRuntimeAsync(files, cancellationToken);
        var journal = new ThreadJournal(files.Paths);
        var service = new SessionService(
            runtime,
            journal,
            new SessionProjection(runtime),
            new SessionConfig());
        var thread = await CreateThreadAsync(service, cancellationToken);
        var archived = await ArchiveAsync(service, thread, cancellationToken);
        var recoveryDirectory = Path.Combine(
            files.Paths.ThreadRecoveryDirectory,
            thread.ThreadId.ToString("D").ToLowerInvariant());
        Directory.CreateDirectory(recoveryDirectory);
        var link = Path.Combine(recoveryDirectory, "escape");
        CreateDirectoryLink(link, outside);
        var prepared = await service.PrepareDeleteAsync(
            new PrepareDeleteRequest(
                thread.ThreadId,
                archived.CurrentSequence),
            cancellationToken);

        var pending = await service.DeleteThreadAsync(
            new DeleteThreadRequest(
                thread.ThreadId,
                Guid.CreateVersion7(),
                archived.CurrentSequence,
                prepared.Value!.Token),
            cancellationToken);

        Assert.Equal(
            SessionCommandStatus.CommittedPendingProjection,
            pending.Status);
        Assert.True(File.Exists(protectedFile));
        Assert.True(journal.Exists(ThreadJournalLocation.Deleting, thread.ThreadId));

        Directory.Delete(link);
        var recovered = new SessionService(
            runtime,
            journal,
            new SessionProjection(runtime),
            new SessionConfig());
        Assert.Empty(await recovered.RecoverSessionStateAsync(cancellationToken));
        Assert.True(File.Exists(protectedFile));
        Directory.Delete(outside, recursive: true);
    }

    private static async Task<StateRuntime> CreateRuntimeAsync(
        TempWorkspace files,
        CancellationToken cancellationToken)
    {
        var runtime = new StateRuntime(files.Paths, TimeSpan.FromSeconds(2));
        await runtime.InitializeAsync(cancellationToken);
        return runtime;
    }

    private static async Task<ThreadSnapshot> CreateThreadAsync(
        SessionService service,
        CancellationToken cancellationToken)
    {
        var result = await service.CreateThreadAsync(
            new CreateThreadRequest(
                Guid.CreateVersion7(),
                ExpectedSequence: 0,
                DisplayName: "lifecycle"),
            cancellationToken);
        return Assert.IsType<ThreadSnapshot>(result.Value);
    }

    private static async Task<ThreadSnapshot> ArchiveAsync(
        SessionService service,
        ThreadSnapshot thread,
        CancellationToken cancellationToken)
    {
        var result = await service.ArchiveThreadAsync(
            new ThreadMutationRequest(
                thread.ThreadId,
                Guid.CreateVersion7(),
                thread.CurrentSequence),
            cancellationToken);
        return Assert.IsType<ThreadSnapshot>(result.Value);
    }

    private static async Task<ThreadSnapshot> RunInputAsync(
        SessionService service,
        RecordingExecutor executor,
        ThreadSnapshot thread,
        string text,
        CancellationToken cancellationToken)
    {
        var result = await service.EnqueueInputAsync(
            new EnqueueInputRequest(
                thread.ThreadId,
                Guid.CreateVersion7(),
                thread.CurrentSequence,
                text),
            cancellationToken);
        Assert.NotEqual(SessionCommandStatus.Rejected, result.Status);
        var context = await executor.Contexts.Reader.ReadAsync(cancellationToken);
        await service.WaitForExecutionAsync(context.Turn.TurnId, cancellationToken);
        return (await service.GetThreadAsync(
            thread.ThreadId,
            cancellationToken)).Value!;
    }

    private static void CreateDirectoryLink(string path, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(path, target);
            return;
        }

        using var process = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/d /c mklink /J \"{path}\" \"{target}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }) ?? throw new InvalidOperationException("Could not start mklink.");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new IOException(process.StandardError.ReadToEnd());
        }
    }

    private sealed class RecordingExecutor : ISessionExecutor
    {
        public Channel<AgentSession> Contexts { get; } =
            Channel.CreateUnbounded<AgentSession>();

        public AgentSession? Last { get; private set; }

        public async ValueTask ExecuteAsync(
            AgentSession context,
            ISessionExecutionSink sink,
            CancellationToken cancellationToken)
        {
            Last = context;
            await Contexts.Writer.WriteAsync(context, cancellationToken);
            await sink.EmitAsync(new CompleteTurnIntent(), cancellationToken);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; private set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;

        public void Advance(TimeSpan value) => UtcNow += value;
    }

    private sealed class InjectedRecoveryFaultException : Exception;

    private sealed class TempWorkspace : IDisposable
    {
        public TempWorkspace()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"opencowork-recovery-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            Paths = new OpenCoWorkPaths(Root);
        }

        public string Root { get; }

        public OpenCoWorkPaths Paths { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
