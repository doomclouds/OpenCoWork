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

    internal static readonly IReadOnlyList<StateMigration> VersionOneOnly =
    [
        new(1, VersionOneSql),
    ];

    internal static readonly IReadOnlyList<StateMigration> WithTestVersionTwo =
    [
        new(1, VersionOneSql),
        new(2, "ALTER TABLE state_info ADD COLUMN future_value TEXT NULL;"),
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
        : this(paths, busyTimeout, StateMigrations.VersionOneOnly, faultInjector: null)
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
            if (!string.Equals(actual, "state_info", StringComparison.Ordinal))
            {
                throw new StateMigrationException(
                    $"Unexpected M1 state schema tables: {actual ?? "<none>"}.");
            }
        }

        var (version, _) = await ReadStateAsync(connection, cancellationToken);
        if (version != expectedVersion)
        {
            throw new StateMigrationException(
                $"State schema version is {version}; expected {expectedVersion}.");
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
