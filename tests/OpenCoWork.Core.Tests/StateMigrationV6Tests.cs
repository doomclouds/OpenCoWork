using Microsoft.Data.Sqlite;
using OpenCoWork.Core.State;
using OpenCoWork.Core.Workspaces;
using OpenCoWork.Teams;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class StateMigrationV6Tests
{
    private static readonly string[] CoWorkTables =
    [
        "agent_profiles",
        "agent_runs",
        "cowork_budget_scopes",
        "cowork_command_receipts",
        "cowork_dispatch_intents",
        "cowork_files",
        "cowork_state",
        "cowork_worktrees",
        "mailbox_messages",
        "mission_members",
        "mission_tasks",
        "missions",
        "team_members",
        "teams",
    ];

    [Fact]
    public async Task Version_five_migrates_atomically_to_global_version_six()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var versionFive = new StateRuntime(
            files.Paths,
            TimeSpan.FromSeconds(2),
            StateMigrations.VersionFiveOnly,
            [],
            faultInjector: null);
        await versionFive.InitializeAsync(cancellationToken);
        await using (var connection =
                     await versionFive.OpenReadWriteConnectionAsync(cancellationToken))
        {
            await ExecuteAsync(
                connection,
                """
                INSERT INTO threads (
                    thread_id, display_name, display_name_search,
                    status, availability, history_mode,
                    current_sequence, last_applied_sequence,
                    created_utc, updated_utc, agent_mode)
                VALUES (
                    '0197f8b1-0000-7000-8000-000000000001',
                    'existing', 'EXISTING',
                    'active', 'available', 'server',
                    0, 0, 1, 1, 'agent');
                """,
                cancellationToken);
        }

        var current = CreateCurrent(files.Paths);
        await current.InitializeAsync(cancellationToken);
        await current.InitializeAsync(cancellationToken);

        await using var migrated =
            await current.OpenReadWriteConnectionAsync(cancellationToken);
        Assert.Equal(
            6L,
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
        Assert.Equal(
            CoWorkTables,
            await ReadStringsAsync(
                migrated,
                """
                SELECT name
                FROM sqlite_schema
                WHERE type = 'table'
                  AND name IN (
                    'agent_profiles', 'agent_runs', 'cowork_budget_scopes',
                    'cowork_command_receipts', 'cowork_dispatch_intents',
                    'cowork_files', 'cowork_state', 'cowork_worktrees',
                    'mailbox_messages', 'mission_members', 'mission_tasks',
                    'missions', 'team_members', 'teams')
                ORDER BY name;
                """,
                cancellationToken));
        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                migrated,
                "SELECT count(*) FROM threads WHERE display_name = 'existing';",
                cancellationToken));
        Assert.Empty(Directory.EnumerateFiles(files.Paths.RuntimeDirectory, "*.backup"));
    }

    [Theory]
    [InlineData((int)StateMigrationFaultPoint.Ddl)]
    [InlineData((int)StateMigrationFaultPoint.Commit)]
    public async Task Failed_v6_migration_restores_v5_marks_failed_and_can_retry(
        int faultPointValue)
    {
        var faultPoint = (StateMigrationFaultPoint)faultPointValue;
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var versionFive = new StateRuntime(
            files.Paths,
            TimeSpan.FromSeconds(2),
            StateMigrations.VersionFiveOnly,
            [],
            faultInjector: null);
        await versionFive.InitializeAsync(cancellationToken);
        var faulted = new StateRuntime(
            files.Paths,
            TimeSpan.FromSeconds(2),
            StateMigrations.VersionSixOnly,
            TeamsStateMigrationContributors.CreateVersionSix(),
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
                     await versionFive.OpenReadWriteConnectionAsync(cancellationToken))
        {
            Assert.Equal(
                5L,
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
                    "SELECT count(*) FROM sqlite_schema WHERE type = 'table' AND name = 'missions';",
                    cancellationToken));
        }

        var retry = CreateCurrent(files.Paths);
        await retry.InitializeAsync(cancellationToken);
        await using var migrated =
            await retry.OpenReadWriteConnectionAsync(cancellationToken);
        Assert.Equal(
            6L,
            await ScalarAsync<long>(
                migrated,
                "SELECT schema_version FROM state_info WHERE id = 1;",
                cancellationToken));
        Assert.Empty(Directory.EnumerateFiles(files.Paths.RuntimeDirectory, "*.backup"));
    }

    private static StateRuntime CreateCurrent(OpenCoWorkPaths paths) =>
        new(
            paths,
            TimeSpan.FromSeconds(2),
            StateMigrations.VersionSixOnly,
            TeamsStateMigrationContributors.CreateVersionSix(),
            faultInjector: null);

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
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

    private sealed class TempWorkspace : IDisposable
    {
        public TempWorkspace()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"opencowork-state-v6-{Guid.NewGuid():N}");
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
