using System.Diagnostics;
using Microsoft.Data.Sqlite;
using OpenCoWork.Core.State;
using OpenCoWork.Core.Workspaces;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class StateRuntimeTests
{
    private static readonly string[] SessionSchemaTables =
    [
        "items",
        "pending_interactions",
        "session_idempotency",
        "session_operation_receipts",
        "state_info",
        "threads",
        "turn_queue",
        "turns",
    ];

    private static readonly string[] SessionSchemaIndexes =
    [
        "ix_items_thread_sequence",
        "ix_items_turn_sequence",
        "ix_pending_interactions_thread",
        "ix_session_idempotency_thread",
        "ix_session_operation_receipts_expiry",
        "ix_threads_status",
        "ix_threads_updated",
        "ix_turns_thread",
    ];

    [Fact]
    public async Task Initial_database_uses_the_frozen_schema_pragmas_and_read_only_policy()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var runtime = new StateRuntime(files.Paths, TimeSpan.FromMilliseconds(750));

        await runtime.InitializeAsync(cancellationToken);

        Assert.True(File.Exists(files.Paths.StateDatabasePath));
        Assert.Empty(Directory.EnumerateFiles(files.Paths.RuntimeDirectory, "*.backup"));

        await using var write = await runtime.OpenReadWriteConnectionAsync(cancellationToken);
        Assert.Equal("wal", await ScalarAsync<string>(write, "PRAGMA journal_mode;", cancellationToken));
        Assert.Equal(2L, await ScalarAsync<long>(write, "PRAGMA synchronous;", cancellationToken));
        Assert.Equal(1L, await ScalarAsync<long>(write, "PRAGMA foreign_keys;", cancellationToken));
        Assert.Equal(1L, await ScalarAsync<long>(write, "PRAGMA secure_delete;", cancellationToken));
        Assert.Equal(750L, await ScalarAsync<long>(write, "PRAGMA busy_timeout;", cancellationToken));
        Assert.Equal(
            SessionSchemaTables,
            await ReadStringsAsync(
                write,
                """
                SELECT name
                FROM sqlite_schema
                WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
                ORDER BY name;
                """,
                cancellationToken));
        Assert.Equal(
            2L,
            await ScalarAsync<long>(
                write,
                "SELECT schema_version FROM state_info WHERE id = 1;",
                cancellationToken));

        await using var read = await runtime.OpenReadOnlyConnectionAsync(cancellationToken);
        Assert.Equal(1L, await ScalarAsync<long>(read, "PRAGMA query_only;", cancellationToken));
        await Assert.ThrowsAsync<SqliteException>(
            async () => await ExecuteAsync(
                read,
                "UPDATE state_info SET migration_status = 'tampered' WHERE id = 1;",
                cancellationToken));
    }

    [Fact]
    public async Task Session_schema_enforces_required_indexes_and_foreign_keys()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var runtime = new StateRuntime(files.Paths, TimeSpan.FromSeconds(2));

        await runtime.InitializeAsync(cancellationToken);

        await using var connection =
            await runtime.OpenReadWriteConnectionAsync(cancellationToken);
        Assert.Equal(
            SessionSchemaIndexes,
            await ReadStringsAsync(
                connection,
                """
                SELECT name
                FROM sqlite_schema
                WHERE type = 'index' AND name NOT LIKE 'sqlite_%'
                ORDER BY name;
                """,
                cancellationToken));
        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                connection,
                "SELECT count(*) FROM pragma_foreign_key_list('threads');",
                cancellationToken));
        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                connection,
                "SELECT count(*) FROM pragma_foreign_key_list('session_idempotency');",
                cancellationToken));
    }

    [Fact]
    public async Task Current_schema_is_revalidated_before_reuse()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var runtime = new StateRuntime(files.Paths, TimeSpan.FromSeconds(2));
        await runtime.InitializeAsync(cancellationToken);

        await using (var connection =
                     await runtime.OpenReadWriteConnectionAsync(cancellationToken))
        {
            await ExecuteAsync(
                connection,
                "DROP INDEX ix_threads_updated;",
                cancellationToken);
        }

        await Assert.ThrowsAsync<StateMigrationException>(
            () => runtime.InitializeAsync(cancellationToken));
    }

    [Fact]
    public async Task Coordinator_serializes_one_workspace_without_blocking_another_workspace()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var firstFiles = new TempWorkspace();
        using var secondFiles = new TempWorkspace();
        var first = new StateRuntime(firstFiles.Paths, TimeSpan.FromSeconds(2));
        var second = new StateRuntime(secondFiles.Paths, TimeSpan.FromSeconds(2));
        await first.InitializeAsync(cancellationToken);
        await second.InitializeAsync(cancellationToken);
        var firstActive = 0;
        var firstMaximum = 0;
        var allActive = 0;
        var allMaximum = 0;

        async Task WorkAsync(StateRuntime runtime, bool sameWorkspace)
        {
            await runtime.WriteCoordinator.ExecuteAsync(
                async (_, _, cancellationToken) =>
                {
                    var active = Interlocked.Increment(ref allActive);
                    UpdateMaximum(ref allMaximum, active);
                    if (sameWorkspace)
                    {
                        var current = Interlocked.Increment(ref firstActive);
                        UpdateMaximum(ref firstMaximum, current);
                    }

                    await Task.Delay(60, cancellationToken);

                    if (sameWorkspace)
                    {
                        Interlocked.Decrement(ref firstActive);
                    }

                    Interlocked.Decrement(ref allActive);
                },
                cancellationToken);
        }

        await Task.WhenAll(
            WorkAsync(first, sameWorkspace: true),
            WorkAsync(first, sameWorkspace: true),
            WorkAsync(second, sameWorkspace: false));

        Assert.Equal(1, firstMaximum);
        Assert.True(allMaximum >= 2);
    }

    [Fact]
    public async Task Failed_committed_migration_restores_schema_marks_failed_and_can_retry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var versionOne = new StateRuntime(
            files.Paths,
            TimeSpan.FromSeconds(2),
            StateMigrations.VersionOneOnly,
            faultInjector: null);
        await versionOne.InitializeAsync(cancellationToken);

        var faulted = new StateRuntime(
            files.Paths,
            TimeSpan.FromSeconds(2),
            StateMigrations.Current,
            point =>
            {
                if (point == StateMigrationFaultPoint.Commit)
                {
                    throw new InjectedMigrationException();
                }
            });

        await Assert.ThrowsAsync<StateMigrationException>(
            () => faulted.InitializeAsync(cancellationToken));

        await using (var connection = await versionOne.OpenReadWriteConnectionAsync(cancellationToken))
        {
            Assert.Equal(
                ["error", "id", "migration_status", "schema_version", "target_version", "updated_utc"],
                await ReadStringsAsync(
                    connection,
                    "SELECT name FROM pragma_table_info('state_info') ORDER BY name;",
                    cancellationToken));
            Assert.Equal(
                "Failed",
                await ScalarAsync<string>(
                    connection,
                    "SELECT migration_status FROM state_info WHERE id = 1;",
                    cancellationToken));
            Assert.Equal(
                1L,
                await ScalarAsync<long>(
                    connection,
                    "SELECT schema_version FROM state_info WHERE id = 1;",
                    cancellationToken));
        }

        var retry = new StateRuntime(
            files.Paths,
            TimeSpan.FromSeconds(2),
            StateMigrations.Current,
            faultInjector: null);
        await retry.InitializeAsync(cancellationToken);

        await using var migrated = await retry.OpenReadWriteConnectionAsync(cancellationToken);
        Assert.Equal(
            SessionSchemaTables,
            await ReadStringsAsync(
                migrated,
                """
                SELECT name
                FROM sqlite_schema
                WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
                ORDER BY name;
                """,
                cancellationToken));
        Assert.Equal(
            2L,
            await ScalarAsync<long>(
                migrated,
                "SELECT schema_version FROM state_info WHERE id = 1;",
                cancellationToken));
        Assert.Equal(
            "Completed",
            await ScalarAsync<string>(
                migrated,
                "SELECT migration_status FROM state_info WHERE id = 1;",
                cancellationToken));
    }

    [Fact]
    public async Task Invalid_or_future_migration_chains_fail_before_modifying_the_database()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var runtime = new StateRuntime(files.Paths, TimeSpan.FromSeconds(2));
        await runtime.InitializeAsync(cancellationToken);

        await Assert.ThrowsAsync<StateMigrationException>(
            () => new StateRuntime(
                    files.Paths,
                    TimeSpan.FromSeconds(2),
                    [new StateMigration(2, "SELECT 1;")],
                    faultInjector: null)
                .InitializeAsync(cancellationToken));

        await using var connection = await runtime.OpenReadWriteConnectionAsync(cancellationToken);
        await ExecuteAsync(
            connection,
            "UPDATE state_info SET schema_version = 99 WHERE id = 1;",
            cancellationToken);

        await Assert.ThrowsAsync<StateMigrationException>(
            () => runtime.InitializeAsync(cancellationToken));
    }

    [Fact]
    public async Task Direct_state_runtime_rejects_a_runtime_junction_outside_the_workspace()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        Directory.CreateDirectory(files.Paths.OpenCoWorkDirectory);
        var outside = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-state-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        var runtimeLink = files.Paths.RuntimeDirectory;
        CreateDirectoryLink(runtimeLink, outside);

        try
        {
            var runtime = new StateRuntime(files.Paths, TimeSpan.FromSeconds(2));
            await Assert.ThrowsAsync<WorkspacePathEscapeException>(
                () => runtime.InitializeAsync(cancellationToken));
            Assert.False(File.Exists(Path.Combine(outside, "state.db")));
        }
        finally
        {
            Directory.Delete(runtimeLink);
            Directory.Delete(outside, recursive: true);
        }
    }

    private static void UpdateMaximum(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }

    private static void CreateDirectoryLink(string path, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(path, target);
            return;
        }

        using var process = Process.Start(new ProcessStartInfo
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

    private static async Task<T> ScalarAsync<T>(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(
            await command.ExecuteScalarAsync(cancellationToken) ?? throw new InvalidDataException(),
            typeof(T));
    }

    private static async Task<string[]> ReadStringsAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var values = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(reader.GetString(0));
        }

        return [.. values];
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed class TempWorkspace : IDisposable
    {
        public TempWorkspace()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"opencowork-state-{Guid.NewGuid():N}");
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
}
