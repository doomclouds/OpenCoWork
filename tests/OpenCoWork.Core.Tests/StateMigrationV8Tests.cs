using Microsoft.Data.Sqlite;
using OpenCoWork.Abstractions;
using OpenCoWork.Automations;
using OpenCoWork.Core.State;
using OpenCoWork.Core.Workspaces;
using OpenCoWork.Teams;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class StateMigrationV8Tests
{
    [Fact]
    public async Task New_database_reaches_version_eight_and_restarts()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var current = CreateCurrent(files.Paths);

        await current.InitializeAsync(cancellationToken);
        await current.InitializeAsync(cancellationToken);

        await using var connection =
            await current.OpenReadWriteConnectionAsync(cancellationToken);
        Assert.Equal(
            8L,
            await ScalarAsync<long>(
                connection,
                "SELECT schema_version FROM state_info WHERE id = 1;",
                cancellationToken));
        Assert.Empty(Directory.EnumerateFiles(files.Paths.RuntimeDirectory, "*.backup"));
    }

    [Fact]
    public async Task Version_seven_rebuilds_only_items_and_preserves_rows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var versionSeven = CreateVersionSeven(files.Paths);
        await versionSeven.InitializeAsync(cancellationToken);
        await using (var connection =
                     await versionSeven.OpenReadWriteConnectionAsync(cancellationToken))
        {
            await InsertSeedAsync(connection, cancellationToken);
        }

        string[] oldObjects;
        string[] oldColumns;
        await using (var connection =
                     await versionSeven.OpenReadWriteConnectionAsync(cancellationToken))
        {
            oldObjects = await ReadStringsAsync(
                connection,
                """
                SELECT type || ':' || name
                FROM sqlite_schema
                WHERE name NOT LIKE 'sqlite_%'
                ORDER BY type, name;
                """,
                cancellationToken);
            oldColumns = await ReadStringsAsync(
                connection,
                "SELECT name FROM pragma_table_info('items') ORDER BY cid;",
                cancellationToken);
        }

        var current = CreateCurrent(files.Paths);
        await current.InitializeAsync(cancellationToken);
        await current.InitializeAsync(cancellationToken);

        await using var migrated =
            await current.OpenReadWriteConnectionAsync(cancellationToken);
        Assert.Equal(
            8L,
            await ScalarAsync<long>(
                migrated,
                "SELECT schema_version FROM state_info WHERE id = 1;",
                cancellationToken));
        Assert.Equal(
            "{\"message\":\"kept\"}",
            await ScalarAsync<string>(
                migrated,
                "SELECT payload_json FROM items WHERE sequence = 1;",
                cancellationToken));
        Assert.Equal(
            oldObjects,
            await ReadStringsAsync(
                migrated,
                """
                SELECT type || ':' || name
                FROM sqlite_schema
                WHERE name NOT LIKE 'sqlite_%'
                ORDER BY type, name;
                """,
                cancellationToken));
        Assert.Equal(
            oldColumns,
            await ReadStringsAsync(
                migrated,
                "SELECT name FROM pragma_table_info('items') ORDER BY cid;",
                cancellationToken));

        await ExecuteAsync(
            migrated,
            ItemInsert(
                Guid.CreateVersion7().ToString("D"),
                sequence: 2,
                itemType: "providerAction"),
            cancellationToken);
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            migrated,
            ItemInsert(
                Guid.CreateVersion7().ToString("D"),
                sequence: 3,
                itemType: "unknown"),
            cancellationToken));
        Assert.Empty(Directory.EnumerateFiles(files.Paths.RuntimeDirectory, "*.backup"));
    }

    [Theory]
    [InlineData((int)StateMigrationFaultPoint.Ddl)]
    [InlineData((int)StateMigrationFaultPoint.Commit)]
    public async Task Failed_v8_migration_restores_v7_and_can_retry(int faultPointValue)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var versionSeven = CreateVersionSeven(files.Paths);
        await versionSeven.InitializeAsync(cancellationToken);
        await using (var connection =
                     await versionSeven.OpenReadWriteConnectionAsync(cancellationToken))
        {
            await InsertSeedAsync(connection, cancellationToken);
        }

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
                     await versionSeven.OpenReadWriteConnectionAsync(cancellationToken))
        {
            Assert.Equal(
                7L,
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
                "{\"message\":\"kept\"}",
                await ScalarAsync<string>(
                    restored,
                    "SELECT payload_json FROM items WHERE sequence = 1;",
                    cancellationToken));
        }

        var retry = CreateCurrent(files.Paths);
        await retry.InitializeAsync(cancellationToken);
        await using var migrated =
            await retry.OpenReadWriteConnectionAsync(cancellationToken);
        Assert.Equal(
            8L,
            await ScalarAsync<long>(
                migrated,
                "SELECT schema_version FROM state_info WHERE id = 1;",
                cancellationToken));
        Assert.Empty(Directory.EnumerateFiles(files.Paths.RuntimeDirectory, "*.backup"));
    }

    private static async Task InsertSeedAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
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
                '01991f55-0f32-7d8f-86d8-2efb8f48f18c',
                'existing', 'EXISTING',
                'active', 'available', 'server',
                1, 1, 1, 1, 'agent');
            INSERT INTO turns (
                turn_id, thread_id, status, created_utc, updated_utc)
            VALUES (
                '01991f55-0f32-7d8f-86d8-2efb8f48f18d',
                '01991f55-0f32-7d8f-86d8-2efb8f48f18c',
                'completed', 1, 1);
            """,
            cancellationToken);
        await ExecuteAsync(
            connection,
            ItemInsert(
                "01991f55-0f32-7d8f-86d8-2efb8f48f18e",
                sequence: 1,
                itemType: "systemNotice"),
            cancellationToken);
    }

    private static string ItemInsert(string itemId, int sequence, string itemType) =>
        $$"""
          INSERT INTO items (
              item_id, thread_id, turn_id, sequence, item_type, status,
              payload_json, content_text, content_length, content_sha256,
              created_utc, updated_utc)
          VALUES (
              '{{itemId}}',
              '01991f55-0f32-7d8f-86d8-2efb8f48f18c',
              '01991f55-0f32-7d8f-86d8-2efb8f48f18d',
              {{sequence}}, '{{itemType}}', 'completed',
              '{"message":"kept"}', 'kept', 4,
              'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
              1, 1);
          """;

    private static StateRuntime CreateVersionSeven(OpenCoWorkPaths paths) =>
        new(
            paths,
            TimeSpan.FromSeconds(2),
            StateMigrations.VersionSevenOnly,
            Contributors(),
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
                $"opencowork-state-v8-{Guid.NewGuid():N}");
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
