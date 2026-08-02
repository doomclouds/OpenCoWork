using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenCoWork.Abstractions;
using OpenCoWork.Automations;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Gateway;
using OpenCoWork.Core.Sessions;
using OpenCoWork.Core.State;
using OpenCoWork.Core.Workspaces;
using OpenCoWork.Teams;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class HistoricalRecoveryCorpusTests
{
    [Fact]
    public async Task Historical_state_corpora_restore_and_migrate_to_v9_without_mutation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var manifest = LoadManifest();
        Assert.Equal(1, manifest.SchemaVersion);
        Assert.Equal([7, 8], manifest.State.Select(corpus => corpus.SchemaVersion));

        foreach (var corpus in manifest.State)
        {
            var source = CorpusPath(corpus.File);
            Assert.Equal(corpus.Sha256, Sha256(source));
            Assert.Matches("^[0-9a-f]{40}$", corpus.SourceCommit);
            Assert.False(string.IsNullOrWhiteSpace(corpus.SourceTest));

            foreach (var faultPoint in new[]
                     {
                         StateMigrationFaultPoint.Ddl,
                         StateMigrationFaultPoint.Commit,
                     })
            {
                using var files = new TempWorkspace($"state-{corpus.SchemaVersion}");
                Directory.CreateDirectory(files.Paths.RuntimeDirectory);
                File.Copy(source, files.Paths.StateDatabasePath);
                await AssertStateCorpusAsync(
                    files.Paths.StateDatabasePath,
                    corpus,
                    migrationStatus: "Completed",
                    cancellationToken);

                var faulted = new StateRuntime(
                    files.Paths,
                    TimeSpan.FromSeconds(2),
                    StateMigrations.Current,
                    CurrentContributors(),
                    point =>
                    {
                        if (point == faultPoint)
                        {
                            throw new InjectedMigrationException();
                        }
                    });
                await Assert.ThrowsAsync<StateMigrationException>(
                    () => faulted.InitializeAsync(cancellationToken));
                await AssertStateCorpusAsync(
                    files.Paths.StateDatabasePath,
                    corpus,
                    migrationStatus: "Failed",
                    cancellationToken);

                var retry = CreateCurrent(files.Paths);
                await retry.InitializeAsync(cancellationToken);
                await retry.InitializeAsync(cancellationToken);
                await using var migrated =
                    await retry.OpenReadWriteConnectionAsync(cancellationToken);
                Assert.Equal("9", await ScalarAsync(
                    migrated,
                    "SELECT schema_version FROM state_info WHERE id = 1;",
                    cancellationToken));
                Assert.Equal("Completed", await ScalarAsync(
                    migrated,
                    "SELECT migration_status FROM state_info WHERE id = 1;",
                    cancellationToken));
                Assert.Equal("ok", await ScalarAsync(
                    migrated,
                    "PRAGMA integrity_check;",
                    cancellationToken));
                await AssertNoRowsAsync(
                    migrated,
                    "PRAGMA foreign_key_check;",
                    cancellationToken);
                Assert.Equal(7, Guid.Parse(await ScalarAsync(
                    migrated,
                    "SELECT workspace_id FROM operations_state WHERE id = 1;",
                    cancellationToken)).Version);
                await AssertExpectationsAsync(
                    migrated,
                    corpus.Expectations,
                    cancellationToken);
                Assert.Empty(Directory.EnumerateFiles(
                    files.Paths.RuntimeDirectory,
                    "*.backup"));
            }

            Assert.Equal(corpus.Sha256, Sha256(source));
        }
    }

    [Fact]
    public async Task Historical_state_v7_refuses_nonempty_legacy_automation_runs()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var corpus = Assert.Single(
            LoadManifest().State,
            candidate => candidate.SchemaVersion == 7);
        var source = CorpusPath(corpus.File);
        using var files = new TempWorkspace("state-v7-legacy-run");
        Directory.CreateDirectory(files.Paths.RuntimeDirectory);
        File.Copy(source, files.Paths.StateDatabasePath);

        await using (var connection = new SqliteConnection(
                         new SqliteConnectionStringBuilder
                         {
                             DataSource = files.Paths.StateDatabasePath,
                             Pooling = false,
                         }.ToString()))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO automation_runs (
                    automation_run_id, automation_id, trigger_kind,
                    trigger_idempotency_key, status, definition_snapshot_json,
                    inputs_json, workspace_mode, workspace_access, provider_id,
                    model_id, permission_snapshot_json, capability_snapshot_json,
                    run_deadline_utc, revision, created_utc, updated_utc)
                VALUES (
                    $run_id, 'historical-daily', 'manual', $idempotency_key,
                    'completed', '{}', '{}', 'project', 'readWrite', 'deepseek',
                    'deepseek-v4-flash', '{}', '{}', 1, 1, 1, 1);
                """;
            command.Parameters.AddWithValue("$run_id", Guid.CreateVersion7().ToString());
            command.Parameters.AddWithValue(
                "$idempotency_key",
                Guid.CreateVersion7().ToString());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var runtime = CreateCurrent(files.Paths);
        var exception = await Assert.ThrowsAsync<StateMigrationException>(
            () => runtime.InitializeAsync(cancellationToken));
        Assert.Contains("cannot be upgraded safely", exception.ToString());
        await AssertStateCorpusAsync(
            files.Paths.StateDatabasePath,
            corpus with
            {
                Expectations =
                [
                    .. corpus.Expectations,
                    new QueryExpectation(
                        "SELECT count(*) FROM automation_runs;",
                        "1"),
                ],
            },
            migrationStatus: "Failed",
            cancellationToken);
        Assert.Equal(corpus.Sha256, Sha256(source));
    }

    [Fact]
    public async Task Historical_journal_v1_corpora_replay_rebuild_and_reject_unknown_schema()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var manifest = LoadManifest();
        Assert.Equal(2, manifest.Journals.Length);

        foreach (var corpus in manifest.Journals)
        {
            var source = CorpusPath(corpus.File);
            Assert.Equal(corpus.Sha256, Sha256(source));
            Assert.Equal(1, corpus.SchemaVersion);
            Assert.Matches("^[0-9a-f]{40}$", corpus.SourceCommit);
            Assert.False(string.IsNullOrWhiteSpace(corpus.SourceTest));

            using var files = new TempWorkspace(corpus.Id);
            var journal = new ThreadJournal(files.Paths);
            var threadId = Guid.Parse(corpus.ThreadId);
            Directory.CreateDirectory(files.Paths.ActiveThreadsDirectory);
            File.Copy(
                source,
                journal.GetPath(ThreadJournalLocation.Active, threadId));

            var replay = await journal.ReplayAsync(
                ThreadJournalLocation.Active,
                threadId,
                cancellationToken);
            Assert.Equal(ThreadJournalHealth.Healthy, replay.Health);
            Assert.Equal(corpus.EntryCount, replay.Entries.Count);
            Assert.Equal(
                Enumerable.Range(1, corpus.EntryCount).Select(value => (long)value),
                replay.Entries.Select(entry => entry.Sequence));
            Assert.All(replay.Entries, entry =>
            {
                Assert.Equal(7, entry.ThreadId.Version);
                Assert.Equal(7, entry.EntryId.Version);
                Assert.Equal(7, entry.IdempotencyKey.Version);
            });

            var runtime = CreateCurrent(files.Paths);
            await runtime.InitializeAsync(cancellationToken);
            var projection = new SessionProjection(runtime);
            var service = new SessionService(
                runtime,
                journal,
                projection,
                new SessionConfig());
            Assert.Empty(await service.RecoverSessionStateAsync(cancellationToken));
            var expected = await projection.ReadNormalizedSnapshotAsync(cancellationToken);
            Assert.Equal(1, expected.ThreadCount);
            Assert.Equal(1, expected.TurnCount);
            Assert.Equal(3, expected.ItemCount);

            var history = Assert.IsType<SessionPage<SessionEvent>>(
                (await service.ReadHistoryAsync(
                    new ReadHistoryRequest(threadId),
                    cancellationToken)).Value);
            var agent = Assert.Single(history.Items, item =>
                item.Type == SessionEventType.ItemCompleted &&
                item.Payload.Item?.Type == SessionItemType.AgentMessage);
            Assert.Equal(
                corpus.ModelVisibleText,
                Assert.IsType<TextItemContent>(agent.Payload.Item!.Content).Text);

            await DeleteSessionProjectionAsync(runtime, cancellationToken);
            Assert.Equal(
                0,
                (await projection.ReadNormalizedSnapshotAsync(cancellationToken)).ThreadCount);
            var rebuiltProjection = new SessionProjection(runtime);
            var rebuilt = new SessionService(
                runtime,
                journal,
                rebuiltProjection,
                new SessionConfig());
            Assert.Empty(await rebuilt.RecoverSessionStateAsync(cancellationToken));
            Assert.Equal(
                expected,
                await rebuiltProjection.ReadNormalizedSnapshotAsync(cancellationToken));

            if (corpus.Id == "journal-m4-v1")
            {
                await AssertHistoricalLifecycleRecoveryAsync(
                    runtime,
                    journal,
                    rebuilt,
                    threadId,
                    cancellationToken);
            }

            using var unknownFiles = new TempWorkspace($"{corpus.Id}-unknown");
            var unknownJournal = new ThreadJournal(unknownFiles.Paths);
            Directory.CreateDirectory(unknownFiles.Paths.ActiveThreadsDirectory);
            var unknown = (await File.ReadAllTextAsync(source, cancellationToken))
                .Replace("\"schemaVersion\":1", "\"schemaVersion\":9", StringComparison.Ordinal);
            await File.WriteAllTextAsync(
                unknownJournal.GetPath(ThreadJournalLocation.Active, threadId),
                unknown,
                new UTF8Encoding(false),
                cancellationToken);
            var rejected = await unknownJournal.ReplayAsync(
                ThreadJournalLocation.Active,
                threadId,
                cancellationToken);
            Assert.Equal(ThreadJournalHealth.RecoveryRequired, rejected.Health);
            Assert.Equal(SessionErrorCodes.JournalUnsupportedSchema, rejected.DiagnosticCode);
            Assert.Equal(corpus.Sha256, Sha256(source));
        }
    }

    private static async Task AssertHistoricalLifecycleRecoveryAsync(
        StateRuntime runtime,
        ThreadJournal journal,
        SessionService service,
        Guid threadId,
        CancellationToken cancellationToken)
    {
        var thread = Assert.IsType<ThreadSnapshot>(
            (await service.GetThreadAsync(threadId, cancellationToken)).Value);
        var queued = Assert.Single(thread.Queue);
        var removed = await service.RemoveQueuedInputAsync(
            new RemoveQueuedInputRequest(
                threadId,
                queued.QueueItemId,
                Guid.CreateVersion7(),
                thread.CurrentSequence),
            cancellationToken);
        Assert.Equal(SessionCommandStatus.Committed, removed.Status);
        thread = Assert.IsType<ThreadSnapshot>(removed.Value);
        var archiveFault = true;
        var archiving = new SessionService(
            runtime,
            journal,
            new SessionProjection(runtime),
            new SessionConfig(),
            recoveryFaultInjector: point =>
            {
                if (archiveFault && point == SessionRecoveryFaultPoint.AfterJournalMoved)
                {
                    archiveFault = false;
                    throw new InjectedRecoveryException();
                }
            });
        await Assert.ThrowsAsync<InjectedRecoveryException>(() =>
            archiving.ArchiveThreadAsync(
                new ThreadMutationRequest(
                    threadId,
                    Guid.CreateVersion7(),
                    thread.CurrentSequence),
                cancellationToken));
        var archived = new SessionService(
            runtime,
            journal,
            new SessionProjection(runtime),
            new SessionConfig());
        Assert.Empty(await archived.RecoverSessionStateAsync(cancellationToken));
        var archivedThread = Assert.IsType<ThreadSnapshot>(
            (await archived.GetThreadAsync(threadId, cancellationToken)).Value);
        Assert.Equal(ThreadStatus.Archived, archivedThread.Status);

        var unarchiveFault = true;
        var unarchiving = new SessionService(
            runtime,
            journal,
            new SessionProjection(runtime),
            new SessionConfig(),
            recoveryFaultInjector: point =>
            {
                if (unarchiveFault && point == SessionRecoveryFaultPoint.AfterProjectionApplied)
                {
                    unarchiveFault = false;
                    throw new InjectedRecoveryException();
                }
            });
        await Assert.ThrowsAsync<InjectedRecoveryException>(() =>
            unarchiving.UnarchiveThreadAsync(
                new ThreadMutationRequest(
                    threadId,
                    Guid.CreateVersion7(),
                    archivedThread.CurrentSequence),
                cancellationToken));
        var recovered = new SessionService(
            runtime,
            journal,
            new SessionProjection(runtime),
            new SessionConfig());
        Assert.Empty(await recovered.RecoverSessionStateAsync(cancellationToken));
        Assert.Equal(
            ThreadStatus.Active,
            (await recovered.GetThreadAsync(threadId, cancellationToken)).Value!.Status);
    }

    private static async Task AssertStateCorpusAsync(
        string path,
        StateCorpus corpus,
        string migrationStatus,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
        await connection.OpenAsync(cancellationToken);
        Assert.Equal(
            corpus.SchemaVersion.ToString(CultureInfo.InvariantCulture),
            await ScalarAsync(
                connection,
                "SELECT schema_version FROM state_info WHERE id = 1;",
                cancellationToken));
        Assert.Equal(migrationStatus, await ScalarAsync(
            connection,
            "SELECT migration_status FROM state_info WHERE id = 1;",
            cancellationToken));
        Assert.Equal("ok", await ScalarAsync(
            connection,
            "PRAGMA integrity_check;",
            cancellationToken));
        await AssertNoRowsAsync(connection, "PRAGMA foreign_key_check;", cancellationToken);
        await AssertExpectationsAsync(connection, corpus.Expectations, cancellationToken);
    }

    private static async Task AssertExpectationsAsync(
        SqliteConnection connection,
        IReadOnlyList<QueryExpectation> expectations,
        CancellationToken cancellationToken)
    {
        foreach (var expectation in expectations)
        {
            Assert.Equal(
                expectation.Value,
                await ScalarAsync(connection, expectation.Sql, cancellationToken));
        }
    }

    private static async Task<string> ScalarAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidDataException("Corpus query returned null.");
        return Convert.ToString(value, CultureInfo.InvariantCulture)!;
    }

    private static async Task AssertNoRowsAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.False(await reader.ReadAsync(cancellationToken));
    }

    private static async Task DeleteSessionProjectionAsync(
        StateRuntime runtime,
        CancellationToken cancellationToken)
    {
        await runtime.WriteCoordinator.ExecuteAsync(
            async (connection, transaction, token) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM threads;";
                await command.ExecuteNonQueryAsync(token);
            },
            cancellationToken);
    }

    private static StateRuntime CreateCurrent(OpenCoWorkPaths paths) =>
        new(paths, TimeSpan.FromSeconds(2), CurrentContributors());

    private static IWorkspaceStateMigrationContributor[] CurrentContributors() =>
        [
            .. GatewayStateMigrationContributors.Create(),
            .. TeamsStateMigrationContributors.Create(),
            .. AutomationsStateMigrationContributors.Create(),
        ];

    private static CorpusManifest LoadManifest() =>
        JsonSerializer.Deserialize<CorpusManifest>(
            File.ReadAllText(CorpusPath("manifest.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidDataException("Historical corpus manifest is invalid.");

    private static string CorpusPath(
        string file,
        [CallerFilePath] string sourcePath = "") =>
        Path.Combine(Path.GetDirectoryName(sourcePath)!, "Corpora", "M11", file);

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private sealed record CorpusManifest(
        int SchemaVersion,
        StateCorpus[] State,
        JournalCorpus[] Journals);

    private sealed record StateCorpus(
        string Id,
        string File,
        string Sha256,
        string SourceCommit,
        string SourceTest,
        int SchemaVersion,
        QueryExpectation[] Expectations);

    private sealed record QueryExpectation(string Sql, string Value);

    private sealed record JournalCorpus(
        string Id,
        string File,
        string Sha256,
        string SourceCommit,
        string SourceTest,
        int SchemaVersion,
        string ThreadId,
        int EntryCount,
        string ModelVisibleText);

    private sealed class TempWorkspace : IDisposable
    {
        public TempWorkspace(string name)
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"opencowork-m11-{name}-{Guid.NewGuid():N}");
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

    private sealed class InjectedMigrationException : Exception;

    private sealed class InjectedRecoveryException : Exception;
}
