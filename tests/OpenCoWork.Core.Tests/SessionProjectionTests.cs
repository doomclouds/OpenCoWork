using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Sessions;
using OpenCoWork.Core.State;
using OpenCoWork.Core.Workspaces;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class SessionProjectionTests
{
    [Fact]
    public async Task Projection_applies_all_session_rows_in_sequence_and_skips_exact_replay()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var runtime = new StateRuntime(files.Paths, TimeSpan.FromSeconds(2));
        await runtime.InitializeAsync(cancellationToken);
        var journal = new ThreadJournal(files.Paths);
        var projection = new SessionProjection(runtime);
        var threadId = Guid.CreateVersion7();
        var turnId = Guid.CreateVersion7();
        var queueItemId = Guid.CreateVersion7();
        var requestItemId = Guid.CreateVersion7();
        var interactionId = Guid.CreateVersion7();
        var agentItemId = Guid.CreateVersion7();
        var entries = new List<ThreadJournalEntry>
        {
            await AppendAsync(
                journal,
                threadId,
                1,
                SessionEventType.ThreadCreated,
                new ThreadCreatedFact("Projection", HistoryMode.Server, null, RequestHash(1)),
                cancellationToken),
            await AppendAsync(
                journal,
                threadId,
                2,
                SessionEventType.TurnQueued,
                new TurnQueuedFact(queueItemId, "queued", 0, RequestHash(2)),
                cancellationToken),
            await AppendAsync(
                journal,
                threadId,
                3,
                SessionEventType.TurnStarted,
                new TurnStartedFact(turnId, RequestHash(3)),
                cancellationToken),
            await AppendAsync(
                journal,
                threadId,
                4,
                SessionEventType.ItemStarted,
                new ItemStartedFact(
                    requestItemId,
                    turnId,
                    SessionItemType.ApprovalRequest,
                    JsonSerializer.SerializeToElement(new { Prompt = "approve?" }),
                    "approve?",
                    RequestHash(4)),
                cancellationToken),
            await AppendAsync(
                journal,
                threadId,
                5,
                SessionEventType.TurnWaitingApproval,
                new TurnWaitingFact(
                    turnId,
                    interactionId,
                    requestItemId,
                    SessionInteractionType.Approval,
                    JsonSerializer.SerializeToElement(new { Prompt = "approve?" }),
                    new SessionExecutionCheckpoint(
                        "test",
                        1,
                        "{}",
                        new string('a', 64)),
                    null,
                    RequestHash(5)),
                cancellationToken),
            await AppendAsync(
                journal,
                threadId,
                6,
                SessionEventType.InteractionResolved,
                new InteractionResolvedFact(
                    interactionId,
                    JsonSerializer.SerializeToElement(new { Approved = true }),
                    RequestHash(6)),
                cancellationToken),
            await AppendAsync(
                journal,
                threadId,
                7,
                SessionEventType.TurnExecutionResumed,
                new TurnExecutionResumedFact(turnId, interactionId, RequestHash(7)),
                cancellationToken),
            await AppendAsync(
                journal,
                threadId,
                8,
                SessionEventType.ItemStarted,
                new ItemStartedFact(
                    agentItemId,
                    turnId,
                    SessionItemType.AgentMessage,
                    JsonSerializer.SerializeToElement(new { Text = "" }),
                    string.Empty,
                    RequestHash(8)),
                cancellationToken),
            await AppendAsync(
                journal,
                threadId,
                9,
                SessionEventType.ItemDeltaAppended,
                new ItemDeltaFact(agentItemId, "done", RequestHash(9)),
                cancellationToken),
            await AppendAsync(
                journal,
                threadId,
                10,
                SessionEventType.ItemCompleted,
                new ItemCompletedFact(
                    agentItemId,
                    4,
                    RequestHashText("done"),
                    RequestHash(10)),
                cancellationToken),
            await AppendAsync(
                journal,
                threadId,
                11,
                SessionEventType.TurnCompleted,
                new TurnTerminalFact(turnId, null, RequestHash(11)),
                cancellationToken),
        };

        foreach (var entry in entries)
        {
            Assert.Equal(
                ProjectionApplyDisposition.Applied,
                await projection.ApplyAsync(entry, cancellationToken));
        }

        Assert.Equal(
            ProjectionApplyDisposition.AlreadyApplied,
            await projection.ApplyAsync(entries[^1], cancellationToken));
        var snapshot = await projection.ReadNormalizedSnapshotAsync(cancellationToken);
        var conflict = await Assert.ThrowsAsync<SessionProjectionException>(
            () => projection.ApplyAsync(
                entries[^1] with { Checksum = new string('f', 64) },
                cancellationToken));
        Assert.Equal(SessionErrorCodes.IdempotencyConflict, conflict.Code);
        Assert.Equal(1, snapshot.ThreadCount);
        Assert.Equal(1, snapshot.TurnCount);
        Assert.Equal(2, snapshot.ItemCount);
        Assert.Equal(1, snapshot.QueueCount);
        Assert.Equal(1, snapshot.InteractionCount);
        Assert.Equal(11, snapshot.IdempotencyCount);

        await using (var connection =
                     await runtime.OpenReadOnlyConnectionAsync(cancellationToken))
        {
            Assert.Equal(
                "completed",
                await ScalarAsync<string>(
                    connection,
                    "SELECT status FROM turns WHERE turn_id = $id;",
                    turnId,
                    cancellationToken));
            Assert.Equal(
                "done",
                await ScalarAsync<string>(
                    connection,
                    "SELECT content_text FROM items WHERE item_id = $id;",
                    agentItemId,
                    cancellationToken));
            Assert.Equal(
                "resolved",
                await ScalarAsync<string>(
                    connection,
                    "SELECT status FROM pending_interactions WHERE interaction_id = $id;",
                    interactionId,
                    cancellationToken));
        }

        var gap = await AppendAsync(
            journal,
            threadId,
            13,
            SessionEventType.ThreadRenamed,
            new ThreadRenamedFact("gap", RequestHash(13)),
            cancellationToken);
        var error = await Assert.ThrowsAsync<SessionProjectionException>(
            () => projection.ApplyAsync(gap, cancellationToken));
        Assert.Equal(SessionErrorCodes.SequenceConflict, error.Code);
        Assert.Equal(
            snapshot,
            await projection.ReadNormalizedSnapshotAsync(cancellationToken));
    }

    [Fact]
    public async Task Item_completion_digest_mismatch_rolls_back_without_advancing_water()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var runtime = new StateRuntime(files.Paths, TimeSpan.FromSeconds(2));
        await runtime.InitializeAsync(cancellationToken);
        var journal = new ThreadJournal(files.Paths);
        var projection = new SessionProjection(runtime);
        var threadId = Guid.CreateVersion7();
        var turnId = Guid.CreateVersion7();
        var itemId = Guid.CreateVersion7();
        var entries = new[]
        {
            await AppendAsync(
                journal,
                threadId,
                1,
                SessionEventType.ThreadCreated,
                new ThreadCreatedFact("Digest", HistoryMode.Server, null, RequestHash(1)),
                cancellationToken),
            await AppendAsync(
                journal,
                threadId,
                2,
                SessionEventType.TurnStarted,
                new TurnStartedFact(turnId, RequestHash(2)),
                cancellationToken),
            await AppendAsync(
                journal,
                threadId,
                3,
                SessionEventType.ItemStarted,
                new ItemStartedFact(
                    itemId,
                    turnId,
                    SessionItemType.AgentMessage,
                    JsonSerializer.SerializeToElement(new { Text = "" }),
                    string.Empty,
                    RequestHash(3)),
                cancellationToken),
            await AppendAsync(
                journal,
                threadId,
                4,
                SessionEventType.ItemDeltaAppended,
                new ItemDeltaFact(itemId, "actual", RequestHash(4)),
                cancellationToken),
        };
        foreach (var entry in entries)
        {
            await projection.ApplyAsync(entry, cancellationToken);
        }

        var invalid = await AppendAsync(
            journal,
            threadId,
            5,
            SessionEventType.ItemCompleted,
            new ItemCompletedFact(
                itemId,
                6,
                RequestHashText("other!"),
                RequestHash(5)),
            cancellationToken);
        var error = await Assert.ThrowsAsync<SessionProjectionException>(
            () => projection.ApplyAsync(invalid, cancellationToken));

        Assert.Equal(SessionErrorCodes.JournalCorrupt, error.Code);
        await using var connection =
            await runtime.OpenReadOnlyConnectionAsync(cancellationToken);
        Assert.Equal(
            4L,
            await ScalarAsync<long>(
                connection,
                "SELECT last_applied_sequence FROM threads WHERE thread_id = $id;",
                threadId,
                cancellationToken));
        Assert.Equal(
            "streaming",
            await ScalarAsync<string>(
                connection,
                "SELECT status FROM items WHERE item_id = $id;",
                itemId,
                cancellationToken));
    }

    [Theory]
    [InlineData((int)SessionProjectionFaultPoint.BeforeCommit, false)]
    [InlineData((int)SessionProjectionFaultPoint.AfterCommit, true)]
    public async Task Committed_journal_fact_degrades_then_catches_up_after_projection_fault(
        int faultPointValue,
        bool rowExistsAfterFault)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var runtime = new StateRuntime(files.Paths, TimeSpan.FromSeconds(2));
        await runtime.InitializeAsync(cancellationToken);
        var journal = new ThreadJournal(files.Paths);
        var threadId = Guid.CreateVersion7();
        var entry = await AppendAsync(
            journal,
            threadId,
            1,
            SessionEventType.ThreadCreated,
            new ThreadCreatedFact("Fault", HistoryMode.Server, null, RequestHash(1)),
            cancellationToken);
        var faultPoint = (SessionProjectionFaultPoint)faultPointValue;
        var inject = true;
        var projection = new SessionProjection(
            runtime,
            point =>
            {
                if (inject && point == faultPoint)
                {
                    inject = false;
                    throw new InjectedProjectionFaultException();
                }
            });

        var result = await projection.ApplyCommittedAsync(entry, cancellationToken);

        Assert.Equal(SessionCommandStatus.CommittedPendingProjection, result.Status);
        Assert.Equal(SessionProjectionState.Degraded, projection.State);
        Assert.False(projection.CanAcceptNewWork);
        Assert.Single(projection.PendingEntries);
        Assert.Equal(
            rowExistsAfterFault ? 1L : 0L,
            await CountThreadsAsync(runtime, cancellationToken));

        await projection.CatchUpAsync([entry], cancellationToken);

        Assert.Equal(SessionProjectionState.Ready, projection.State);
        Assert.True(projection.CanAcceptNewWork);
        Assert.Empty(projection.PendingEntries);
        Assert.Equal(1L, await CountThreadsAsync(runtime, cancellationToken));
    }

    [Fact]
    public async Task Full_rebuild_removes_orphans_preserves_delete_receipts_and_matches_snapshot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var runtime = new StateRuntime(files.Paths, TimeSpan.FromSeconds(2));
        await runtime.InitializeAsync(cancellationToken);
        var journal = new ThreadJournal(files.Paths);
        var projection = new SessionProjection(runtime);
        var threadId = Guid.CreateVersion7();
        var entry = await AppendAsync(
            journal,
            threadId,
            1,
            SessionEventType.ThreadCreated,
            new ThreadCreatedFact("Keep", HistoryMode.Server, null, RequestHash(1)),
            cancellationToken);
        await projection.ApplyAsync(entry, cancellationToken);
        var expected = await projection.ReadNormalizedSnapshotAsync(cancellationToken);
        var deletedThreadId = Guid.CreateVersion7();
        var deleteKey = Guid.CreateVersion7();
        var now = new DateTimeOffset(2026, 7, 26, 9, 0, 0, TimeSpan.Zero);
        await projection.SaveDeleteReceiptAsync(
            deletedThreadId,
            deleteKey,
            now,
            cancellationToken);

        var duplicateSourceError = await Assert.ThrowsAsync<SessionProjectionException>(
            () => projection.RebuildAsync(
                [
                    new ThreadJournalSource(
                        ThreadJournalLocation.Active,
                        threadId,
                        [entry]),
                    new ThreadJournalSource(
                        ThreadJournalLocation.Archived,
                        threadId,
                        [entry]),
                ],
                cancellationToken));
        Assert.Equal(SessionErrorCodes.JournalCorrupt, duplicateSourceError.Code);
        Assert.Equal(
            expected,
            await projection.ReadNormalizedSnapshotAsync(cancellationToken));

        var orphanId = Guid.CreateVersion7();
        await runtime.WriteCoordinator.ExecuteAsync(
            async (connection, transaction, token) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO threads (
                        thread_id, display_name, display_name_search,
                        status, availability, history_mode,
                        current_sequence, last_applied_sequence,
                        created_utc, updated_utc)
                    VALUES (
                        $id, 'orphan', 'ORPHAN',
                        'active', 'available', 'server',
                        0, 0, 0, 0);
                    """;
                command.Parameters.AddWithValue("$id", orphanId.ToString("D"));
                await command.ExecuteNonQueryAsync(token);
            },
            cancellationToken);

        var rebuild = await projection.RebuildAsync(
            [
                new ThreadJournalSource(
                    ThreadJournalLocation.Active,
                    threadId,
                    [entry]),
            ],
            cancellationToken);

        Assert.Equal([orphanId], rebuild.RemovedOrphanThreadIds);
        Assert.Equal(
            expected,
            await projection.ReadNormalizedSnapshotAsync(cancellationToken));
        Assert.True(await projection.HasDeleteReceiptAsync(
            deletedThreadId,
            deleteKey,
            now.AddDays(6),
            cancellationToken));
        Assert.False(await projection.HasDeleteReceiptAsync(
            deletedThreadId,
            deleteKey,
            now.AddDays(8),
            cancellationToken));

        await using var connection =
            await runtime.OpenReadOnlyConnectionAsync(cancellationToken);
        var receiptText = await ScalarAsync<string>(
            connection,
            """
            SELECT thread_id_sha256 || idempotency_key_sha256 || result_json
            FROM session_operation_receipts;
            """,
            parameter: null,
            cancellationToken);
        Assert.DoesNotContain(
            deletedThreadId.ToString("D"),
            receiptText,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            deleteKey.ToString("D"),
            receiptText,
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<ThreadJournalEntry> AppendAsync(
        ThreadJournal journal,
        Guid threadId,
        long sequence,
        SessionEventType type,
        object payload,
        CancellationToken cancellationToken) =>
        await journal.AppendAsync(
            ThreadJournalLocation.Active,
            new ThreadJournalDraft(
                threadId,
                sequence,
                Guid.CreateVersion7(),
                new DateTimeOffset(2026, 7, 26, 8, 30, 0, TimeSpan.Zero)
                    .AddSeconds(sequence),
                type,
                Guid.CreateVersion7(),
                payload),
            cancellationToken);

    private static string RequestHash(int sequence) =>
        RequestHashText($"request-{sequence}");

    private static string RequestHashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static async Task<long> CountThreadsAsync(
        StateRuntime runtime,
        CancellationToken cancellationToken)
    {
        await using var connection =
            await runtime.OpenReadOnlyConnectionAsync(cancellationToken);
        return await ScalarAsync<long>(
            connection,
            "SELECT count(*) FROM threads;",
            parameter: null,
            cancellationToken);
    }

    private static async Task<T> ScalarAsync<T>(
        SqliteConnection connection,
        string sql,
        object? parameter,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (parameter is not null)
        {
            command.Parameters.AddWithValue(
                "$id",
                parameter is Guid id ? id.ToString("D") : parameter);
        }

        return (T)Convert.ChangeType(
            await command.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("Expected a scalar value."),
            typeof(T),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class TempWorkspace : IDisposable
    {
        public TempWorkspace()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"opencowork-projection-{Guid.NewGuid():N}");
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
