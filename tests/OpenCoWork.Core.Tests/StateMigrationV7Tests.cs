using Microsoft.Data.Sqlite;
using OpenCoWork.Abstractions;
using OpenCoWork.Automations;
using OpenCoWork.Core.State;
using OpenCoWork.Core.Workspaces;
using OpenCoWork.Teams;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class StateMigrationV7Tests
{
    private static readonly string[] VersionSevenTables =
    [
        "automation_command_receipts",
        "automation_definitions",
        "automation_dispatch_intents",
        "automation_runs",
        "automation_schedules",
        "automation_state",
        "project_writer_lease",
    ];

    [Fact]
    public async Task Version_six_migrates_atomically_to_version_seven_and_restarts()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var versionSix = CreateVersionSix(files.Paths);
        await versionSix.InitializeAsync(cancellationToken);

        var current = CreateCurrent(files.Paths);
        await current.InitializeAsync(cancellationToken);
        await current.InitializeAsync(cancellationToken);

        await using var connection =
            await current.OpenReadWriteConnectionAsync(cancellationToken);
        Assert.Equal(
            7L,
            await ScalarAsync<long>(
                connection,
                "SELECT schema_version FROM state_info WHERE id = 1;",
                cancellationToken));
        Assert.Equal(
            VersionSevenTables,
            await ReadStringsAsync(
                connection,
                """
                SELECT name
                FROM sqlite_schema
                WHERE type = 'table'
                  AND name IN (
                    'automation_command_receipts', 'automation_definitions',
                    'automation_dispatch_intents', 'automation_runs',
                    'automation_schedules', 'automation_state',
                    'project_writer_lease')
                ORDER BY name;
                """,
                cancellationToken));
        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                connection,
                "SELECT count(*) FROM project_writer_lease WHERE id = 1;",
                cancellationToken));
        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                connection,
                "SELECT count(*) FROM automation_state WHERE id = 1;",
                cancellationToken));
        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                connection,
                """
                SELECT count(*) FROM sqlite_schema
                WHERE type = 'index' AND name = 'ix_agent_runs_project_writer';
                """,
                cancellationToken));
        Assert.Empty(Directory.EnumerateFiles(files.Paths.RuntimeDirectory, "*.backup"));
    }

    [Theory]
    [InlineData((int)StateMigrationFaultPoint.Ddl)]
    [InlineData((int)StateMigrationFaultPoint.Commit)]
    public async Task Failed_v7_migration_restores_v6_marks_failed_and_can_retry(
        int faultPointValue)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var versionSix = CreateVersionSix(files.Paths);
        await versionSix.InitializeAsync(cancellationToken);
        var faultPoint = (StateMigrationFaultPoint)faultPointValue;
        var faulted = new StateRuntime(
            files.Paths,
            TimeSpan.FromSeconds(2),
            StateMigrations.Current,
            Contributors(),
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
                     await versionSix.OpenReadWriteConnectionAsync(cancellationToken))
        {
            Assert.Equal(
                6L,
                await ScalarAsync<long>(
                    restored,
                    "SELECT schema_version FROM state_info WHERE id = 1;",
                    cancellationToken));
            Assert.Equal(
                "Failed",
                await ScalarAsync<string>(
                    restored,
                    "SELECT migration_status FROM state_info WHERE id = 1;",
                    cancellationToken));
            Assert.Equal(
                0L,
                await ScalarAsync<long>(
                    restored,
                    """
                    SELECT count(*) FROM sqlite_schema
                    WHERE type = 'table' AND name = 'automation_runs';
                    """,
                    cancellationToken));
        }

        var retry = CreateCurrent(files.Paths);
        await retry.InitializeAsync(cancellationToken);
        await using var migrated =
            await retry.OpenReadWriteConnectionAsync(cancellationToken);
        Assert.Equal(
            7L,
            await ScalarAsync<long>(
                migrated,
                "SELECT schema_version FROM state_info WHERE id = 1;",
                cancellationToken));
        Assert.Empty(Directory.EnumerateFiles(files.Paths.RuntimeDirectory, "*.backup"));
    }

    [Fact]
    public async Task Version_seven_enforces_json_uuid_and_active_run_constraints()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var current = CreateCurrent(files.Paths);
        await current.InitializeAsync(cancellationToken);
        await using var connection =
            await current.OpenReadWriteConnectionAsync(cancellationToken);
        await ExecuteAsync(
            connection,
            """
            INSERT INTO automation_definitions (
                automation_id, source_relative_path, source_status,
                source_sha256, definition_version, display_name, enabled,
                definition_json, diagnostics_json, has_schedule,
                revision, created_utc, updated_utc)
            VALUES (
                'daily-check', 'daily-check.yaml', 'ready',
                'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
                'Daily check', 1, '{}', '[]', 0, 1, 1, 1);
            """,
            cancellationToken);

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            """
            INSERT INTO automation_definitions (
                automation_id, source_relative_path, source_status,
                display_name, enabled, definition_json, diagnostics_json,
                has_schedule, revision, created_utc, updated_utc)
            VALUES (
                'invalid-json', 'invalid-json.yaml', 'faulted',
                'Invalid', 0, '{', '[]', 0, 1, 1, 1);
            """,
            cancellationToken));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            """
            INSERT INTO automation_runs (
                automation_run_id, automation_id, trigger_kind,
                trigger_idempotency_key, status, definition_snapshot_json,
                inputs_json, workspace_mode, workspace_access,
                provider_id, model_id, permission_snapshot_json,
                capability_snapshot_json, run_deadline_utc,
                revision, created_utc, updated_utc)
            VALUES (
                'not-a-uuid', 'daily-check', 'manual', 'manual:invalid',
                'pending', '{}', '{}', 'project', 'readOnly',
                'fake', 'fake-model', '{}', '[]', 2, 1, 1, 1);
            """,
            cancellationToken));

        var first = Guid.CreateVersion7().ToString("D");
        var second = Guid.CreateVersion7().ToString("D");
        await ExecuteAsync(
            connection,
            RunInsert(first, "manual:first"),
            cancellationToken);
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            RunInsert(second, "manual:second"),
            cancellationToken));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            """
            INSERT INTO project_writer_lease (
                id, owner_kind, owner_id, lease_id, expires_utc, updated_utc)
            VALUES (2, NULL, NULL, NULL, NULL, 1);
            """,
            cancellationToken));
    }

    private static StateRuntime CreateVersionSix(OpenCoWorkPaths paths) =>
        new(
            paths,
            TimeSpan.FromSeconds(2),
            StateMigrations.VersionSixOnly,
            TeamsStateMigrationContributors.CreateVersionSix(),
            faultInjector: null);

    private static StateRuntime CreateCurrent(OpenCoWorkPaths paths) =>
        new(paths, TimeSpan.FromSeconds(2), Contributors());

    private static IWorkspaceStateMigrationContributor[] Contributors() =>
        [
            .. TeamsStateMigrationContributors.Create(),
            .. AutomationsStateMigrationContributors.Create(),
        ];

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

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string RunInsert(string runId, string idempotencyKey) =>
        $$"""
         INSERT INTO automation_runs (
             automation_run_id, automation_id, trigger_kind,
             trigger_idempotency_key, status, definition_snapshot_json,
             inputs_json, workspace_mode, workspace_access,
             provider_id, model_id, permission_snapshot_json,
             capability_snapshot_json, run_deadline_utc,
             revision, created_utc, updated_utc)
         VALUES (
             '{{runId}}', 'daily-check', 'manual', '{{idempotencyKey}}',
             'pending', '{}', '{}', 'project', 'readOnly',
             'fake', 'fake-model', '{}', '[]', 2, 1, 1, 1);
         """;

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

    private sealed class TempWorkspace : IDisposable
    {
        public TempWorkspace()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"opencowork-state-v7-{Guid.NewGuid():N}");
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
