using Microsoft.Data.Sqlite;
using OpenCoWork.Core.Workspaces;

namespace OpenCoWork.Core.State;

internal sealed record StateMigration(int Version, string Sql);

internal enum StateMigrationFaultPoint
{
    Checkpoint,
    Backup,
    Ddl,
    Commit,
}

internal static class StateMigrations
{
    internal const int CurrentVersion = 4;
    internal const string VersionTwoTables =
        "items,pending_interactions,session_idempotency," +
        "session_operation_receipts,state_info,threads,turn_queue,turns";
    internal const string VersionThreeTables =
        "agent_invocations,compaction_checkpoints,items,pending_interactions," +
        "provider_usage,session_idempotency,session_operation_receipts," +
        "state_info,threads,turn_queue,turns";
    internal const string CurrentTables =
        "agent_invocations,compaction_checkpoints,items,pending_interactions," +
        "provider_usage,session_idempotency,session_operation_receipts," +
        "state_info,threads,tool_invocations,turn_queue,turns";
    internal const string CurrentIndexes =
        "ix_agent_invocations_thread,ix_items_thread_sequence,ix_items_turn_sequence," +
        "ix_pending_interactions_thread,ix_provider_usage_thread," +
        "ix_session_idempotency_thread," +
        "ix_session_operation_receipts_expiry,ix_threads_status," +
        "ix_threads_updated,ix_tool_invocations_thread_call," +
        "ix_tool_invocations_thread_status,ix_turns_thread";
    internal const string CurrentForeignKeys =
        "agent_invocations:thread_id->threads.thread_id:cascade," +
        "agent_invocations:turn_id->turns.turn_id:cascade," +
        "compaction_checkpoints:thread_id->threads.thread_id:cascade," +
        "compaction_checkpoints:turn_id->turns.turn_id:cascade," +
        "items:thread_id->threads.thread_id:cascade," +
        "items:turn_id->turns.turn_id:cascade," +
        "pending_interactions:item_id->items.item_id:cascade," +
        "pending_interactions:thread_id->threads.thread_id:cascade," +
        "pending_interactions:turn_id->turns.turn_id:cascade," +
        "provider_usage:thread_id->threads.thread_id:cascade," +
        "provider_usage:turn_id->turns.turn_id:cascade," +
        "session_idempotency:thread_id->threads.thread_id:cascade," +
        "threads:active_turn_id->turns.turn_id:set null," +
        "tool_invocations:thread_id->threads.thread_id:cascade," +
        "tool_invocations:turn_id->turns.turn_id:cascade," +
        "turn_queue:thread_id->threads.thread_id:cascade," +
        "turns:thread_id->threads.thread_id:cascade";

    private const string VersionOneSql =
        """
        CREATE TABLE state_info (
            id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
            schema_version INTEGER NOT NULL,
            migration_status TEXT NOT NULL,
            target_version INTEGER NOT NULL,
            error TEXT NULL,
            updated_utc TEXT NOT NULL
        );
        INSERT INTO state_info (
            id,
            schema_version,
            migration_status,
            target_version,
            error,
            updated_utc
        ) VALUES (
            1,
            1,
            'Completed',
            1,
            NULL,
            strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
        );
        """;

    private const string VersionTwoSql =
        """
        CREATE TABLE threads (
            thread_id TEXT NOT NULL PRIMARY KEY,
            display_name TEXT NOT NULL,
            display_name_search TEXT NOT NULL,
            status TEXT NOT NULL CHECK (status IN ('active', 'paused', 'archived')),
            availability TEXT NOT NULL CHECK (availability IN ('available', 'recoveryRequired')),
            history_mode TEXT NOT NULL CHECK (history_mode IN ('server', 'client')),
            current_sequence INTEGER NOT NULL CHECK (current_sequence >= 0),
            last_applied_sequence INTEGER NOT NULL CHECK (last_applied_sequence >= 0),
            active_turn_id TEXT NULL,
            first_user_message TEXT NULL,
            first_user_message_search TEXT NULL,
            fork_source_thread_id TEXT NULL,
            fork_source_sequence INTEGER NULL,
            diagnostic TEXT NULL,
            created_utc INTEGER NOT NULL,
            updated_utc INTEGER NOT NULL,
            FOREIGN KEY (active_turn_id) REFERENCES turns (turn_id) ON DELETE SET NULL
        );
        CREATE INDEX ix_threads_updated
            ON threads (updated_utc DESC, thread_id DESC);
        CREATE INDEX ix_threads_status
            ON threads (status, updated_utc DESC, thread_id DESC);

        CREATE TABLE turns (
            turn_id TEXT NOT NULL PRIMARY KEY,
            thread_id TEXT NOT NULL,
            status TEXT NOT NULL CHECK (
                status IN (
                    'running',
                    'waitingApproval',
                    'waitingInput',
                    'completed',
                    'failed',
                    'cancelled')),
            error_code TEXT NULL,
            error_message TEXT NULL,
            created_utc INTEGER NOT NULL,
            updated_utc INTEGER NOT NULL,
            completed_utc INTEGER NULL,
            FOREIGN KEY (thread_id) REFERENCES threads (thread_id) ON DELETE CASCADE
        );
        CREATE INDEX ix_turns_thread
            ON turns (thread_id, created_utc, turn_id);

        CREATE TABLE items (
            item_id TEXT NOT NULL PRIMARY KEY,
            thread_id TEXT NOT NULL,
            turn_id TEXT NOT NULL,
            sequence INTEGER NOT NULL CHECK (sequence > 0),
            item_type TEXT NOT NULL CHECK (
                item_type IN (
                    'userMessage',
                    'agentMessage',
                    'reasoning',
                    'approvalRequest',
                    'approvalResponse',
                    'userInputRequest',
                    'userInputResponse',
                    'error',
                    'systemNotice')),
            status TEXT NOT NULL CHECK (
                status IN ('started', 'streaming', 'completed', 'failed', 'cancelled')),
            payload_json TEXT NOT NULL,
            content_text TEXT NULL,
            content_length INTEGER NULL,
            content_sha256 TEXT NULL,
            created_utc INTEGER NOT NULL,
            updated_utc INTEGER NOT NULL,
            FOREIGN KEY (thread_id) REFERENCES threads (thread_id) ON DELETE CASCADE,
            FOREIGN KEY (turn_id) REFERENCES turns (turn_id) ON DELETE CASCADE
        );
        CREATE INDEX ix_items_thread_sequence
            ON items (thread_id, sequence, item_id);
        CREATE INDEX ix_items_turn_sequence
            ON items (turn_id, sequence, item_id);

        CREATE TABLE turn_queue (
            queue_item_id TEXT NOT NULL PRIMARY KEY,
            thread_id TEXT NOT NULL,
            position INTEGER NOT NULL CHECK (position >= 0),
            payload_json TEXT NOT NULL,
            created_utc INTEGER NOT NULL,
            FOREIGN KEY (thread_id) REFERENCES threads (thread_id) ON DELETE CASCADE,
            UNIQUE (thread_id, position)
        );

        CREATE TABLE pending_interactions (
            interaction_id TEXT NOT NULL PRIMARY KEY,
            thread_id TEXT NOT NULL,
            turn_id TEXT NOT NULL,
            item_id TEXT NOT NULL,
            interaction_type TEXT NOT NULL CHECK (
                interaction_type IN ('approval', 'userInput')),
            status TEXT NOT NULL CHECK (status IN ('pending', 'resolved')),
            request_json TEXT NOT NULL,
            resolution_json TEXT NULL,
            checkpoint_json TEXT NOT NULL,
            timeout_utc INTEGER NULL,
            created_utc INTEGER NOT NULL,
            updated_utc INTEGER NOT NULL,
            FOREIGN KEY (thread_id) REFERENCES threads (thread_id) ON DELETE CASCADE,
            FOREIGN KEY (turn_id) REFERENCES turns (turn_id) ON DELETE CASCADE,
            FOREIGN KEY (item_id) REFERENCES items (item_id) ON DELETE CASCADE
        );
        CREATE INDEX ix_pending_interactions_thread
            ON pending_interactions (thread_id, status, created_utc);

        CREATE TABLE session_idempotency (
            idempotency_key TEXT NOT NULL PRIMARY KEY,
            operation TEXT NOT NULL,
            thread_id TEXT NULL,
            request_sha256 TEXT NOT NULL,
            status TEXT NOT NULL,
            result_json TEXT NULL,
            committed_sequence INTEGER NULL,
            created_utc INTEGER NOT NULL,
            updated_utc INTEGER NOT NULL,
            FOREIGN KEY (thread_id) REFERENCES threads (thread_id) ON DELETE CASCADE
        );
        CREATE INDEX ix_session_idempotency_thread
            ON session_idempotency (thread_id, created_utc);

        CREATE TABLE session_operation_receipts (
            thread_id_sha256 TEXT NOT NULL,
            idempotency_key_sha256 TEXT NOT NULL,
            result_json TEXT NOT NULL,
            completed_utc INTEGER NOT NULL,
            expires_utc INTEGER NOT NULL,
            PRIMARY KEY (thread_id_sha256, idempotency_key_sha256)
        );
        CREATE INDEX ix_session_operation_receipts_expiry
            ON session_operation_receipts (expires_utc);
        """;

    private const string VersionThreeSql =
        """
        ALTER TABLE threads
            ADD COLUMN provider_id TEXT NULL;
        ALTER TABLE threads
            ADD COLUMN model_id TEXT NULL;
        ALTER TABLE threads
            ADD COLUMN agent_mode TEXT NOT NULL DEFAULT 'agent'
                CHECK (agent_mode IN ('agent', 'plan'));

        ALTER TABLE turns
            ADD COLUMN effective_agent_mode TEXT NOT NULL DEFAULT 'agent'
                CHECK (effective_agent_mode IN ('agent', 'plan'));

        ALTER TABLE turn_queue
            ADD COLUMN effective_agent_mode TEXT NOT NULL DEFAULT 'agent'
                CHECK (effective_agent_mode IN ('agent', 'plan'));

        CREATE TABLE agent_invocations (
            invocation_id TEXT NOT NULL PRIMARY KEY,
            thread_id TEXT NOT NULL,
            turn_id TEXT NOT NULL UNIQUE,
            snapshot_json TEXT NOT NULL,
            recorded_sequence INTEGER NOT NULL CHECK (recorded_sequence > 0),
            created_utc INTEGER NOT NULL,
            FOREIGN KEY (thread_id) REFERENCES threads (thread_id) ON DELETE CASCADE,
            FOREIGN KEY (turn_id) REFERENCES turns (turn_id) ON DELETE CASCADE
        );
        CREATE INDEX ix_agent_invocations_thread
            ON agent_invocations (thread_id, recorded_sequence);

        CREATE TABLE provider_usage (
            invocation_id TEXT NOT NULL,
            attempt_number INTEGER NOT NULL CHECK (attempt_number > 0),
            purpose TEXT NOT NULL CHECK (purpose IN ('response', 'compaction')),
            thread_id TEXT NOT NULL,
            turn_id TEXT NOT NULL,
            usage_json TEXT NOT NULL,
            recorded_sequence INTEGER NOT NULL CHECK (recorded_sequence > 0),
            created_utc INTEGER NOT NULL,
            PRIMARY KEY (invocation_id, attempt_number, purpose),
            FOREIGN KEY (thread_id) REFERENCES threads (thread_id) ON DELETE CASCADE,
            FOREIGN KEY (turn_id) REFERENCES turns (turn_id) ON DELETE CASCADE
        );
        CREATE INDEX ix_provider_usage_thread
            ON provider_usage (thread_id, recorded_sequence);

        CREATE TABLE compaction_checkpoints (
            thread_id TEXT NOT NULL PRIMARY KEY,
            turn_id TEXT NOT NULL,
            checkpoint_json TEXT NOT NULL,
            recorded_sequence INTEGER NOT NULL CHECK (recorded_sequence > 0),
            created_utc INTEGER NOT NULL,
            FOREIGN KEY (thread_id) REFERENCES threads (thread_id) ON DELETE CASCADE,
            FOREIGN KEY (turn_id) REFERENCES turns (turn_id) ON DELETE CASCADE
        );
        """;

    private const string VersionFourSql =
        """
        ALTER TABLE pending_interactions RENAME TO pending_interactions_v3;
        ALTER TABLE items RENAME TO items_v3;
        DROP INDEX ix_pending_interactions_thread;
        DROP INDEX ix_items_thread_sequence;
        DROP INDEX ix_items_turn_sequence;
        CREATE TABLE items (
            item_id TEXT NOT NULL PRIMARY KEY,
            thread_id TEXT NOT NULL,
            turn_id TEXT NOT NULL,
            sequence INTEGER NOT NULL CHECK (sequence > 0),
            item_type TEXT NOT NULL CHECK (
                item_type IN (
                    'userMessage',
                    'agentMessage',
                    'reasoning',
                    'approvalRequest',
                    'approvalResponse',
                    'userInputRequest',
                    'userInputResponse',
                    'error',
                    'systemNotice',
                    'toolCall',
                    'toolResult')),
            status TEXT NOT NULL CHECK (
                status IN ('started', 'streaming', 'completed', 'failed', 'cancelled')),
            payload_json TEXT NOT NULL,
            content_text TEXT NULL,
            content_length INTEGER NULL,
            content_sha256 TEXT NULL,
            created_utc INTEGER NOT NULL,
            updated_utc INTEGER NOT NULL,
            FOREIGN KEY (thread_id) REFERENCES threads (thread_id) ON DELETE CASCADE,
            FOREIGN KEY (turn_id) REFERENCES turns (turn_id) ON DELETE CASCADE
        );
        INSERT INTO items (
            item_id,
            thread_id,
            turn_id,
            sequence,
            item_type,
            status,
            payload_json,
            content_text,
            content_length,
            content_sha256,
            created_utc,
            updated_utc)
        SELECT
            item_id,
            thread_id,
            turn_id,
            sequence,
            item_type,
            status,
            payload_json,
            content_text,
            content_length,
            content_sha256,
            created_utc,
            updated_utc
        FROM items_v3;
        CREATE INDEX ix_items_thread_sequence
            ON items (thread_id, sequence, item_id);
        CREATE INDEX ix_items_turn_sequence
            ON items (turn_id, sequence, item_id);

        CREATE TABLE pending_interactions (
            interaction_id TEXT NOT NULL PRIMARY KEY,
            thread_id TEXT NOT NULL,
            turn_id TEXT NOT NULL,
            item_id TEXT NOT NULL,
            interaction_type TEXT NOT NULL CHECK (
                interaction_type IN ('approval', 'userInput')),
            status TEXT NOT NULL CHECK (status IN ('pending', 'resolved')),
            request_json TEXT NOT NULL,
            resolution_json TEXT NULL,
            checkpoint_json TEXT NOT NULL,
            timeout_utc INTEGER NULL,
            created_utc INTEGER NOT NULL,
            updated_utc INTEGER NOT NULL,
            FOREIGN KEY (thread_id) REFERENCES threads (thread_id) ON DELETE CASCADE,
            FOREIGN KEY (turn_id) REFERENCES turns (turn_id) ON DELETE CASCADE,
            FOREIGN KEY (item_id) REFERENCES items (item_id) ON DELETE CASCADE
        );
        INSERT INTO pending_interactions (
            interaction_id,
            thread_id,
            turn_id,
            item_id,
            interaction_type,
            status,
            request_json,
            resolution_json,
            checkpoint_json,
            timeout_utc,
            created_utc,
            updated_utc)
        SELECT
            interaction_id,
            thread_id,
            turn_id,
            item_id,
            interaction_type,
            status,
            request_json,
            resolution_json,
            checkpoint_json,
            timeout_utc,
            created_utc,
            updated_utc
        FROM pending_interactions_v3;
        CREATE INDEX ix_pending_interactions_thread
            ON pending_interactions (thread_id, status, created_utc);
        DROP TABLE pending_interactions_v3;
        DROP TABLE items_v3;

        CREATE TABLE tool_invocations (
            tool_invocation_id TEXT NOT NULL PRIMARY KEY,
            thread_id TEXT NOT NULL,
            turn_id TEXT NOT NULL,
            provider_tool_call_id TEXT NOT NULL,
            provider_tool_name TEXT NOT NULL,
            tool_definition_id TEXT NULL,
            runtime_binding_id TEXT NULL,
            snapshot_sha256 TEXT NOT NULL,
            arguments_sha256 TEXT NOT NULL,
            status TEXT NOT NULL CHECK (
                status IN (
                    'started',
                    'waitingApproval',
                    'completed',
                    'rejected',
                    'failed',
                    'cancelled',
                    'timedOut',
                    'outcomeUnknown')),
            attempt_count INTEGER NOT NULL CHECK (
                attempt_count >= 0 AND attempt_count <= 2),
            result_item_id TEXT NULL,
            error_code TEXT NULL,
            started_at INTEGER NOT NULL,
            updated_at INTEGER NOT NULL,
            completed_at INTEGER NULL,
            FOREIGN KEY (thread_id) REFERENCES threads (thread_id) ON DELETE CASCADE,
            FOREIGN KEY (turn_id) REFERENCES turns (turn_id) ON DELETE CASCADE
        );
        CREATE INDEX ix_tool_invocations_thread_call
            ON tool_invocations (
                thread_id,
                turn_id,
                provider_tool_call_id);
        CREATE INDEX ix_tool_invocations_thread_status
            ON tool_invocations (thread_id, status);
        """;

    internal static readonly IReadOnlyList<StateMigration> VersionOneOnly =
    [
        new(1, VersionOneSql),
    ];

    internal static readonly IReadOnlyList<StateMigration> VersionTwoOnly =
    [
        new(1, VersionOneSql),
        new(2, VersionTwoSql),
    ];

    internal static readonly IReadOnlyList<StateMigration> VersionThreeOnly =
    [
        new(1, VersionOneSql),
        new(2, VersionTwoSql),
        new(3, VersionThreeSql),
    ];

    internal static readonly IReadOnlyList<StateMigration> Current =
    [
        new(1, VersionOneSql),
        new(2, VersionTwoSql),
        new(3, VersionThreeSql),
        new(4, VersionFourSql),
    ];
}

public sealed class StateMigrationException : InvalidOperationException
{
    public StateMigrationException(string message)
        : base(message)
    {
    }

    public StateMigrationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed record StateInspection(
    int SchemaVersion,
    string MigrationStatus,
    int TargetVersion,
    string? Error,
    string JournalMode,
    int Synchronous,
    bool ForeignKeys,
    bool SecureDelete,
    int BusyTimeoutMilliseconds,
    bool QueryOnly,
    string Tables);

public sealed class StateWriteCoordinator
{
    private readonly Func<CancellationToken, Task<SqliteConnection>> _openConnection;
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal StateWriteCoordinator(
        Func<CancellationToken, Task<SqliteConnection>> openConnection)
    {
        _openConnection = openConnection;
    }

    public async Task ExecuteAsync(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        await _gate.WaitAsync(cancellationToken);

        try
        {
            await using var connection = await _openConnection(cancellationToken);
            await using var transaction = connection.BeginTransaction(deferred: false);

            try
            {
                await action(connection, transaction, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}

public sealed class StateRuntime
{
    private readonly OpenCoWorkPaths _paths;
    private readonly int _busyTimeoutMilliseconds;
    private readonly IReadOnlyList<StateMigration> _migrations;
    private readonly Action<StateMigrationFaultPoint>? _faultInjector;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);

    public StateRuntime(OpenCoWorkPaths paths, TimeSpan busyTimeout)
        : this(paths, busyTimeout, StateMigrations.Current, faultInjector: null)
    {
    }

    internal StateRuntime(
        OpenCoWorkPaths paths,
        TimeSpan busyTimeout,
        IReadOnlyList<StateMigration> migrations,
        Action<StateMigrationFaultPoint>? faultInjector)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(migrations);
        if (busyTimeout < TimeSpan.Zero ||
            busyTimeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(busyTimeout));
        }

        _paths = paths;
        _busyTimeoutMilliseconds = (int)busyTimeout.TotalMilliseconds;
        _migrations = migrations.ToArray();
        _faultInjector = faultInjector;
        WriteCoordinator = new StateWriteCoordinator(OpenReadWriteConnectionAsync);
    }

    public StateWriteCoordinator WriteCoordinator { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            ValidateMigrationChain();
            GuardPath(_paths.RuntimeDirectory);
            Directory.CreateDirectory(_paths.RuntimeDirectory);
            GuardPath(_paths.StateDatabasePath);

            if (!File.Exists(_paths.StateDatabasePath))
            {
                await CreateInitialDatabaseAsync(cancellationToken);
                return;
            }

            await MigrateExistingDatabaseAsync(cancellationToken);
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    public async Task<SqliteConnection> OpenReadWriteConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        GuardPath(_paths.StateDatabasePath);
        var connection = CreateConnection(SqliteOpenMode.ReadWrite);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await ApplyConnectionPolicyAsync(
                connection,
                enableWal: false,
                readOnly: false,
                cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<SqliteConnection> OpenReadOnlyConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        GuardPath(_paths.StateDatabasePath);
        var connection = CreateConnection(SqliteOpenMode.ReadOnly);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await ApplyConnectionPolicyAsync(
                connection,
                enableWal: false,
                readOnly: true,
                cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<StateInspection> InspectAsync(
        CancellationToken cancellationToken = default)
    {
        RejectUncheckpointedJournal(_paths.StateDatabasePath + "-wal");
        RejectUncheckpointedJournal(_paths.StateDatabasePath + "-journal");
        var journalMode = ReadJournalMode(_paths.StateDatabasePath);
        await using var connection = await OpenImmutableConnectionAsync(cancellationToken);
        var synchronous = await ReadPragmaIntAsync(
            connection,
            "synchronous",
            cancellationToken);
        var foreignKeys = await ReadPragmaIntAsync(
            connection,
            "foreign_keys",
            cancellationToken);
        var secureDelete = await ReadPragmaIntAsync(
            connection,
            "secure_delete",
            cancellationToken);
        var busyTimeout = await ReadPragmaIntAsync(
            connection,
            "busy_timeout",
            cancellationToken);
        var queryOnly = await ReadPragmaIntAsync(
            connection,
            "query_only",
            cancellationToken);

        string tables;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT group_concat(name, ',')
                FROM (
                    SELECT name
                    FROM sqlite_schema
                    WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
                    ORDER BY name
                );
                """;
            tables = Convert.ToString(
                await command.ExecuteScalarAsync(cancellationToken))
                ?? string.Empty;
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT schema_version, migration_status, target_version, error
                FROM state_info
                WHERE id = 1;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new StateMigrationException(
                    "state_info does not contain the singleton row.");
            }

            return new StateInspection(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                journalMode,
                synchronous,
                foreignKeys != 0,
                secureDelete != 0,
                busyTimeout,
                queryOnly != 0,
                tables);
        }
    }

    private static void RejectUncheckpointedJournal(string path)
    {
        if (File.Exists(path) && new FileInfo(path).Length > 0)
        {
            throw new StateMigrationException(
                $"Read-only inspection cannot validate an active SQLite journal: {path}");
        }
    }

    private static string ReadJournalMode(string databasePath)
    {
        using var stream = new FileStream(
            databasePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        stream.Position = 18;
        Span<byte> versions = stackalloc byte[2];
        stream.ReadExactly(versions);
        return versions[0] == 2 && versions[1] == 2
            ? "wal"
            : "delete";
    }

    private async Task<SqliteConnection> OpenImmutableConnectionAsync(
        CancellationToken cancellationToken)
    {
        GuardPath(_paths.StateDatabasePath);
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource =
                    new Uri(_paths.StateDatabasePath).AbsoluteUri + "?immutable=1",
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                DefaultTimeout = Math.Max(1, (int)Math.Ceiling(
                    _busyTimeoutMilliseconds / 1_000d)),
            }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken);
            await ApplyConnectionPolicyAsync(
                connection,
                enableWal: false,
                readOnly: true,
                cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task CheckpointAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenReadWriteConnectionAsync(cancellationToken);
        await ExecuteAsync(
            connection,
            "PRAGMA wal_checkpoint(TRUNCATE);",
            transaction: null,
            cancellationToken);
    }

    public async Task BackupAsync(
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        GuardPath(destinationPath);
        cancellationToken.ThrowIfCancellationRequested();
        await using var source = await OpenReadWriteConnectionAsync(cancellationToken);
        await using var destination = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(destinationPath),
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString());
        GuardPath(destinationPath);
        await destination.OpenAsync(cancellationToken);
        source.BackupDatabase(destination);
    }

    private async Task CreateInitialDatabaseAsync(CancellationToken cancellationToken)
    {
        var temporaryPath =
            $"{_paths.StateDatabasePath}.init-{Guid.NewGuid():N}.tmp";
        GuardPath(temporaryPath);

        try
        {
            await using (var connection = await OpenConnectionAsync(
                             temporaryPath,
                             SqliteOpenMode.ReadWriteCreate,
                             enableWal: true,
                             readOnly: false,
                             cancellationToken))
            {
                foreach (var migration in _migrations)
                {
                    await ExecuteAsync(
                        connection,
                        migration.Sql,
                        transaction: null,
                        cancellationToken);
                }

                if (_migrations.Count > 1)
                {
                    await UpdateStateAsync(
                        connection,
                        transaction: null,
                        _migrations[^1].Version,
                        "Completed",
                        _migrations[^1].Version,
                        error: null,
                        cancellationToken);
                }

                await ValidateSchemaAsync(
                    connection,
                    _migrations[^1].Version,
                    cancellationToken);
                await ExecuteAsync(
                    connection,
                    "PRAGMA wal_checkpoint(TRUNCATE);",
                    transaction: null,
                    cancellationToken);
            }

            SqliteConnection.ClearAllPools();
            File.Move(temporaryPath, _paths.StateDatabasePath);
        }
        catch
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(temporaryPath);
            throw;
        }
    }

    private async Task MigrateExistingDatabaseAsync(CancellationToken cancellationToken)
    {
        int currentVersion;
        string currentStatus;
        await using (var connection = await OpenReadWriteConnectionAsync(cancellationToken))
        {
            (currentVersion, currentStatus) = await ReadStateAsync(connection, cancellationToken);
        }

        var targetVersion = _migrations[^1].Version;
        if (currentVersion > targetVersion)
        {
            throw new StateMigrationException(
                $"State database schema {currentVersion} is newer than supported schema {targetVersion}.");
        }

        if (currentVersion == targetVersion)
        {
            if (!string.Equals(currentStatus, "Completed", StringComparison.Ordinal))
            {
                throw new StateMigrationException(
                    $"State database schema {currentVersion} is in '{currentStatus}' state.");
            }

            await using var validation =
                await OpenReadWriteConnectionAsync(cancellationToken);
            await ValidateSchemaAsync(validation, targetVersion, cancellationToken);
            return;
        }

        var backupPath =
            $"{_paths.StateDatabasePath}.v{currentVersion}-to-v{targetVersion}.backup";

        try
        {
            await CheckpointAsync(cancellationToken);
            _faultInjector?.Invoke(StateMigrationFaultPoint.Checkpoint);
            await BackupAsync(backupPath, cancellationToken);
            _faultInjector?.Invoke(StateMigrationFaultPoint.Backup);

            await WriteCoordinator.ExecuteAsync(
                (connection, transaction, token) => UpdateStateAsync(
                    connection,
                    transaction,
                    currentVersion,
                    "Started",
                    targetVersion,
                    error: null,
                    token),
                cancellationToken);

            await WriteCoordinator.ExecuteAsync(
                async (connection, transaction, token) =>
                {
                    foreach (var migration in _migrations.Where(
                                 item => item.Version > currentVersion))
                    {
                        await ExecuteAsync(connection, migration.Sql, transaction, token);
                        _faultInjector?.Invoke(StateMigrationFaultPoint.Ddl);
                    }

                    await UpdateStateAsync(
                        connection,
                        transaction,
                        targetVersion,
                        "Completed",
                        targetVersion,
                        error: null,
                        token);
                },
                cancellationToken);
            _faultInjector?.Invoke(StateMigrationFaultPoint.Commit);

            await using var validation = await OpenReadWriteConnectionAsync(cancellationToken);
            await ValidateSchemaAsync(validation, targetVersion, cancellationToken);
            DeleteDatabaseFiles(backupPath);
        }
        catch (Exception exception)
        {
            try
            {
                if (File.Exists(backupPath))
                {
                    await RestoreBackupAsync(backupPath, CancellationToken.None);
                }

                await MarkFailedAsync(
                    currentVersion,
                    targetVersion,
                    exception.GetType().Name,
                    CancellationToken.None);
            }
            catch (Exception recoveryException)
            {
                throw new StateMigrationException(
                    "State migration failed and backup recovery also failed.",
                    new AggregateException(exception, recoveryException));
            }
            finally
            {
                DeleteDatabaseFiles(backupPath);
            }

            if (exception is OperationCanceledException)
            {
                throw;
            }

            throw new StateMigrationException(
                $"State migration from {currentVersion} to {targetVersion} failed and was restored.",
                exception);
        }
    }

    private async Task RestoreBackupAsync(
        string backupPath,
        CancellationToken cancellationToken)
    {
        SqliteConnection.ClearAllPools();
        await using var source = await OpenConnectionAsync(
            backupPath,
            SqliteOpenMode.ReadOnly,
            enableWal: false,
            readOnly: true,
            cancellationToken);
        await using var destination = await OpenConnectionAsync(
            _paths.StateDatabasePath,
            SqliteOpenMode.ReadWrite,
            enableWal: false,
            readOnly: false,
            cancellationToken);
        source.BackupDatabase(destination);
    }

    private Task MarkFailedAsync(
        int currentVersion,
        int targetVersion,
        string error,
        CancellationToken cancellationToken) =>
        WriteCoordinator.ExecuteAsync(
            (connection, transaction, token) => UpdateStateAsync(
                connection,
                transaction,
                currentVersion,
                "Failed",
                targetVersion,
                error,
                token),
            cancellationToken);

    private void ValidateMigrationChain()
    {
        if (_migrations.Count == 0)
        {
            throw new StateMigrationException("State migration chain is empty.");
        }

        var ordered = _migrations.OrderBy(item => item.Version).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var expected = index + 1;
            if (ordered[index].Version != expected ||
                _migrations[index].Version != expected)
            {
                throw new StateMigrationException(
                    $"State migration chain must contain each ordered version from 1; expected {expected}.");
            }
        }
    }

    private SqliteConnection CreateConnection(SqliteOpenMode mode) =>
        new(
            new SqliteConnectionStringBuilder
            {
                DataSource = _paths.StateDatabasePath,
                Mode = mode,
                Cache = SqliteCacheMode.Private,
                Pooling = mode != SqliteOpenMode.ReadOnly,
                DefaultTimeout = Math.Max(1, (int)Math.Ceiling(
                    _busyTimeoutMilliseconds / 1_000d)),
            }.ToString());

    private async Task<SqliteConnection> OpenConnectionAsync(
        string path,
        SqliteOpenMode mode,
        bool enableWal,
        bool readOnly,
        CancellationToken cancellationToken)
    {
        GuardPath(path);
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = mode,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                DefaultTimeout = Math.Max(1, (int)Math.Ceiling(
                    _busyTimeoutMilliseconds / 1_000d)),
            }.ToString());
        await connection.OpenAsync(cancellationToken);
        await ApplyConnectionPolicyAsync(
            connection,
            enableWal,
            readOnly,
            cancellationToken);
        return connection;
    }

    private async Task ApplyConnectionPolicyAsync(
        SqliteConnection connection,
        bool enableWal,
        bool readOnly,
        CancellationToken cancellationToken)
    {
        var sql =
            $"""
             {(enableWal ? "PRAGMA journal_mode=WAL;" : string.Empty)}
             PRAGMA synchronous=FULL;
             PRAGMA foreign_keys=ON;
             PRAGMA secure_delete=ON;
             PRAGMA busy_timeout={_busyTimeoutMilliseconds};
             {(readOnly ? "PRAGMA query_only=ON;" : string.Empty)}
             """;
        await ExecuteAsync(connection, sql, transaction: null, cancellationToken);
    }

    private static async Task<(int Version, string Status)> ReadStateAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT schema_version, migration_status
            FROM state_info
            WHERE id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new StateMigrationException("state_info does not contain the singleton row.");
        }

        return (reader.GetInt32(0), reader.GetString(1));
    }

    private static async Task<int> ReadPragmaIntAsync(
        SqliteConnection connection,
        string name,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {name};";
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ValidateSchemaAsync(
        SqliteConnection connection,
        int expectedVersion,
        CancellationToken cancellationToken)
    {
        await using (var tables = connection.CreateCommand())
        {
            tables.CommandText =
                """
                SELECT group_concat(name, ',')
                FROM (
                    SELECT name
                    FROM sqlite_schema
                    WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
                    ORDER BY name
                );
                """;
            var actual = Convert.ToString(
                await tables.ExecuteScalarAsync(cancellationToken));
            var expected = expectedVersion switch
            {
                1 => "state_info",
                2 => StateMigrations.VersionTwoTables,
                3 => StateMigrations.VersionThreeTables,
                StateMigrations.CurrentVersion => StateMigrations.CurrentTables,
                _ => throw new StateMigrationException(
                    $"State schema version {expectedVersion} has no validation contract."),
            };
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                throw new StateMigrationException(
                    $"Unexpected state schema tables: {actual ?? "<none>"}.");
            }
        }

        var (version, _) = await ReadStateAsync(connection, cancellationToken);
        if (version != expectedVersion)
        {
            throw new StateMigrationException(
                $"State schema version is {version}; expected {expectedVersion}.");
        }

        if (expectedVersion != StateMigrations.CurrentVersion)
        {
            return;
        }

        await using (var indexes = connection.CreateCommand())
        {
            indexes.CommandText =
                """
                SELECT group_concat(name, ',')
                FROM (
                    SELECT name
                    FROM sqlite_schema
                    WHERE type = 'index' AND name NOT LIKE 'sqlite_%'
                    ORDER BY name
                );
                """;
            var actual = Convert.ToString(
                await indexes.ExecuteScalarAsync(cancellationToken));
            if (!string.Equals(
                    actual,
                    StateMigrations.CurrentIndexes,
                    StringComparison.Ordinal))
            {
                throw new StateMigrationException(
                    $"Unexpected state schema indexes: {actual ?? "<none>"}.");
            }
        }

        await using (var foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText =
                """
                SELECT group_concat(signature, ',')
                FROM (
                    SELECT source || ':' || "from" || '->' || "table" || '.' ||
                           "to" || ':' || lower(on_delete) AS signature
                    FROM (
                        SELECT 'threads' AS source, * FROM pragma_foreign_key_list('threads')
                        UNION ALL
                        SELECT 'turns' AS source, * FROM pragma_foreign_key_list('turns')
                        UNION ALL
                        SELECT 'items' AS source, * FROM pragma_foreign_key_list('items')
                        UNION ALL
                        SELECT 'turn_queue' AS source, * FROM pragma_foreign_key_list('turn_queue')
                        UNION ALL
                        SELECT 'pending_interactions' AS source, * FROM pragma_foreign_key_list('pending_interactions')
                        UNION ALL
                        SELECT 'session_idempotency' AS source, * FROM pragma_foreign_key_list('session_idempotency')
                        UNION ALL
                        SELECT 'agent_invocations' AS source, * FROM pragma_foreign_key_list('agent_invocations')
                        UNION ALL
                        SELECT 'provider_usage' AS source, * FROM pragma_foreign_key_list('provider_usage')
                        UNION ALL
                        SELECT 'compaction_checkpoints' AS source, * FROM pragma_foreign_key_list('compaction_checkpoints')
                        UNION ALL
                        SELECT 'tool_invocations' AS source, * FROM pragma_foreign_key_list('tool_invocations')
                    )
                    ORDER BY signature
                );
                """;
            var actual = Convert.ToString(
                await foreignKeys.ExecuteScalarAsync(cancellationToken));
            if (!string.Equals(
                    actual,
                    StateMigrations.CurrentForeignKeys,
                    StringComparison.Ordinal))
            {
                throw new StateMigrationException(
                    $"Unexpected state schema foreign keys: {actual ?? "<none>"}.");
            }
        }
    }

    private static Task UpdateStateAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        int version,
        string status,
        int targetVersion,
        string? error,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE state_info
            SET schema_version = $version,
                migration_status = $status,
                target_version = $targetVersion,
                error = $error,
                updated_utc = $updatedUtc
            WHERE id = 1;
            """;
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$targetVersion", targetVersion);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$updatedUtc",
            DateTimeOffset.UtcNow.ToString("O"));
        return ExecuteAndDisposeAsync(command, cancellationToken);
    }

    private static async Task ExecuteAndDisposeAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        await using (command)
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void DeleteDatabaseFiles(string path)
    {
        foreach (var candidate in new[] { path, $"{path}-wal", $"{path}-shm" })
        {
            if (File.Exists(candidate))
            {
                File.Delete(candidate);
            }
        }
    }

    private void GuardPath(string path)
    {
        var declaration = Path.Combine(
            _paths.WorkspaceRoot,
            ".opencowork-state-anchor");
        var relative = Path.GetRelativePath(_paths.WorkspaceRoot, path);
        var resolved = WorkspacePathGuard.ResolveContained(
            _paths.WorkspaceRoot,
            declaration,
            relative);
        WorkspacePathGuard.RevalidateForWrite(resolved);
    }
}
