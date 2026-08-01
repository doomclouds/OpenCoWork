using Microsoft.Data.Sqlite;
using OpenCoWork.Abstractions;
using OpenCoWork.Automations;
using OpenCoWork.Core.State;
using OpenCoWork.Core.Workspaces;
using OpenCoWork.Teams;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class StateMigrationV9Tests
{
    private static readonly string[] VersionNineTables =
    [
        "channel_inbound_messages",
        "channel_media",
        "channel_outbox",
        "channel_thread_mappings",
        "channels",
        "improvement_proposals",
        "insight_runs",
        "operations_state",
        "trace_spans",
        "workspace_heartbeat",
    ];

    [Fact]
    public async Task New_database_reaches_version_nine_with_stable_workspace_identity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var first = CreateCurrent(files.Paths);

        await first.InitializeAsync(cancellationToken);
        var workspaceId = await ReadWorkspaceIdAsync(first, cancellationToken);
        await first.InitializeAsync(cancellationToken);

        var parsed = Guid.Parse(workspaceId);
        Assert.Equal(7, parsed.Version);
        Assert.Equal(parsed.ToString("D"), workspaceId);
        Assert.Equal(workspaceId, await ReadWorkspaceIdAsync(first, cancellationToken));

        await using var connection =
            await first.OpenReadWriteConnectionAsync(cancellationToken);
        Assert.Equal(
            9L,
            await ScalarAsync<long>(
                connection,
                "SELECT schema_version FROM state_info WHERE id = 1;",
                cancellationToken));
        Assert.Equal(
            VersionNineTables,
            await ReadStringsAsync(
                connection,
                $$"""
                  SELECT name
                  FROM sqlite_schema
                  WHERE type = 'table'
                    AND name IN ({{string.Join(',', VersionNineTables.Select(name => $"'{name}'"))}})
                  ORDER BY name;
                  """,
                cancellationToken));
        Assert.Equal("ok", await ScalarAsync<string>(
            connection,
            "PRAGMA integrity_check;",
            cancellationToken));
        Assert.Empty(await ReadStringsAsync(
            connection,
            "PRAGMA foreign_key_check;",
            cancellationToken));

        foreach (var table in new[] { "turns", "automation_runs", "agent_runs" })
        {
            Assert.Contains(
                "correlation_id",
                await ReadStringsAsync(
                    connection,
                    $"SELECT name FROM pragma_table_info('{table}');",
                    cancellationToken));
        }

        Assert.Empty(Directory.EnumerateFiles(files.Paths.RuntimeDirectory, "*.backup"));
    }

    [Fact]
    public async Task Version_eight_migrates_to_nine_and_recovers_from_faults()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        foreach (var faultPoint in new[]
                 {
                     StateMigrationFaultPoint.Ddl,
                     StateMigrationFaultPoint.Commit,
                 })
        {
            using var files = new TempWorkspace();
            var versionEight = CreateVersionEight(files.Paths);
            await versionEight.InitializeAsync(cancellationToken);
            await using (var seed =
                         await versionEight.OpenReadWriteConnectionAsync(cancellationToken))
            {
                await ExecuteAsync(
                    seed,
                    """
                    INSERT INTO threads (
                        thread_id, display_name, display_name_search, status,
                        availability, history_mode, current_sequence,
                        last_applied_sequence, created_utc, updated_utc, agent_mode)
                    VALUES (
                        '01991f55-0f32-7d8f-86d8-2efb8f48f18c',
                        'existing', 'EXISTING', 'active', 'available', 'server',
                        0, 0, 1, 1, 'agent');
                    """,
                    cancellationToken);
            }
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

            await using (var restored =
                         await versionEight.OpenReadWriteConnectionAsync(cancellationToken))
            {
                Assert.Equal(8L, await ScalarAsync<long>(
                    restored,
                    "SELECT schema_version FROM state_info WHERE id = 1;",
                    cancellationToken));
                Assert.Equal("Failed", await ScalarAsync<string>(
                    restored,
                    "SELECT migration_status FROM state_info WHERE id = 1;",
                    cancellationToken));
                Assert.Equal(0L, await ScalarAsync<long>(
                    restored,
                    "SELECT count(*) FROM sqlite_schema WHERE name = 'operations_state';",
                    cancellationToken));
            }

            var retry = CreateCurrent(files.Paths);
            await retry.InitializeAsync(cancellationToken);
            Assert.Equal(7, Guid.Parse(
                await ReadWorkspaceIdAsync(retry, cancellationToken)).Version);
            await using (var migrated =
                         await retry.OpenReadWriteConnectionAsync(cancellationToken))
            {
                Assert.Equal("existing", await ScalarAsync<string>(
                    migrated,
                    "SELECT display_name FROM threads WHERE thread_id = " +
                    "'01991f55-0f32-7d8f-86d8-2efb8f48f18c';",
                    cancellationToken));
            }
            Assert.Empty(Directory.EnumerateFiles(files.Paths.RuntimeDirectory, "*.backup"));
        }
    }

    [Fact]
    public async Task Concurrent_first_initialization_keeps_one_workspace_identity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var first = CreateCurrent(files.Paths);
        var second = CreateCurrent(files.Paths);

        await Task.WhenAll(
            first.InitializeAsync(cancellationToken),
            second.InitializeAsync(cancellationToken));

        Assert.Equal(
            await ReadWorkspaceIdAsync(first, cancellationToken),
            await ReadWorkspaceIdAsync(second, cancellationToken));
    }

    [Fact]
    public async Task Core_v9_owns_turn_correlation_without_the_gateway_contributor()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var state = new StateRuntime(
            files.Paths,
            TimeSpan.FromSeconds(2),
            [
                .. TeamsStateMigrationContributors.Create(),
                .. AutomationsStateMigrationContributors.Create(),
            ]);

        await state.InitializeAsync(cancellationToken);
        await using var connection =
            await state.OpenReadWriteConnectionAsync(cancellationToken);

        Assert.Contains(
            "correlation_id",
            await ReadStringsAsync(
                connection,
                "SELECT name FROM pragma_table_info('turns');",
                cancellationToken));
        Assert.Equal(
            0L,
            await ScalarAsync<long>(
                connection,
                "SELECT count(*) FROM sqlite_schema WHERE name = 'operations_state';",
                cancellationToken));
    }

    [Fact]
    public async Task Missing_operations_singleton_fails_restart_validation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var current = CreateCurrent(files.Paths);
        await current.InitializeAsync(cancellationToken);
        await using (var connection =
                     await current.OpenReadWriteConnectionAsync(cancellationToken))
        {
            await ExecuteAsync(
                connection,
                "DELETE FROM operations_state WHERE id = 1;",
                cancellationToken);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateCurrent(files.Paths).InitializeAsync(cancellationToken));
    }

    [Fact]
    public async Task Version_nine_rejects_invalid_uuid_and_json_values()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var current = CreateCurrent(files.Paths);
        await current.InitializeAsync(cancellationToken);
        await using var connection =
            await current.OpenReadWriteConnectionAsync(cancellationToken);

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            "UPDATE operations_state SET workspace_id = 'not-a-uuid' WHERE id = 1;",
            cancellationToken));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            "UPDATE operations_state SET workspace_id = " +
            "'gggggggg-gggg-7ggg-8ggg-gggggggggggg' WHERE id = 1;",
            cancellationToken));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            """
            INSERT INTO trace_spans (
                trace_id, span_id, name, kind, status, duration_ms,
                tags_json, started_utc, ended_utc)
            VALUES (
                'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'bbbbbbbbbbbbbbbb',
                'invalid', 'internal', 'error', 1, '{', 1, 2);
            """,
            cancellationToken));
    }

    private static StateRuntime CreateVersionEight(OpenCoWorkPaths paths) =>
        new(
            paths,
            TimeSpan.FromSeconds(2),
            StateMigrations.VersionEightOnly,
            LegacyContributors(),
            faultInjector: null);

    private static StateRuntime CreateCurrent(OpenCoWorkPaths paths) =>
        new(paths, TimeSpan.FromSeconds(2), CurrentContributors());

    private static IWorkspaceStateMigrationContributor[] LegacyContributors() =>
        [
            .. TeamsStateMigrationContributors.Create()
                .Where(contributor => contributor.TargetVersion <= 8),
            .. AutomationsStateMigrationContributors.Create()
                .Where(contributor => contributor.TargetVersion <= 8),
        ];

    private static IWorkspaceStateMigrationContributor[] CurrentContributors() =>
        [
            .. GatewayStateMigrationContributors.Create(),
            .. TeamsStateMigrationContributors.Create(),
            .. AutomationsStateMigrationContributors.Create(),
        ];

    private static async Task<string> ReadWorkspaceIdAsync(
        StateRuntime state,
        CancellationToken cancellationToken)
    {
        await using var connection =
            await state.OpenReadWriteConnectionAsync(cancellationToken);
        return await ScalarAsync<string>(
            connection,
            "SELECT workspace_id FROM operations_state WHERE id = 1;",
            cancellationToken);
    }

    private static async Task<T> ScalarAsync<T>(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidDataException("Scalar query returned null.");
        return (T)Convert.ChangeType(
            value,
            typeof(T),
            System.Globalization.CultureInfo.InvariantCulture)!;
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

        return values.ToArray();
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
                $"opencowork-state-v9-{Guid.NewGuid():N}");
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
