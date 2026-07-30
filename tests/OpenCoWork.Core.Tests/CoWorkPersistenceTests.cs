using System.Data.Common;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.State;
using OpenCoWork.Core.Workspaces;
using OpenCoWork.Teams;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class CoWorkPersistenceTests
{
    [Fact]
    public async Task Workspace_state_store_serializes_revision_compare_and_swap()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        IWorkspaceStateStore store = await CreateStoreAsync(files, cancellationToken);

        var results = await Task.WhenAll(
            CompareAndSwapAsync(store, 0, cancellationToken).AsTask(),
            CompareAndSwapAsync(store, 0, cancellationToken).AsTask());

        Assert.Equal([0, 1], results.Order());
        Assert.Equal(
            1L,
            await store.ReadAsync(
                (connection, token) =>
                    ScalarAsync<long>(
                        connection,
                        "SELECT current_revision FROM cowork_state WHERE id = 1;",
                        token),
                cancellationToken));
    }

    [Fact]
    public async Task Schema_enforces_command_intent_budget_and_active_run_invariants()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        IWorkspaceStateStore store = await CreateStoreAsync(files, cancellationToken);

        Assert.Equal(
            1L,
            await store.ReadAsync(
                (connection, token) =>
                    ScalarAsync<long>(
                        connection,
                        """
                        SELECT count(*) FROM sqlite_schema
                        WHERE type = 'index'
                          AND name = 'ix_agent_runs_project_writer';
                        """,
                        token),
                cancellationToken));
        await store.WriteAsync(
            async (connection, transaction, token) =>
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO threads (
                        thread_id, display_name, display_name_search,
                        status, availability, history_mode,
                        current_sequence, last_applied_sequence,
                        created_utc, updated_utc, agent_mode)
                    VALUES (
                        'thread-1', 'cowork', 'COWORK',
                        'active', 'available', 'server',
                        0, 0, 1, 1, 'agent');
                    INSERT INTO agent_profiles (
                        agent_profile_id, name, normalized_name, description, instructions,
                        model_json, tools_json, permission_json, enabled,
                        revision, created_utc, updated_utc)
                    VALUES (
                        'profile-1', 'worker', 'WORKER', '', '',
                        '{}', '[]', '{}', 1, 0, 1, 1);
                    INSERT INTO missions (
                        mission_id, origin_thread_id, origin_delivery_id,
                        objective, status, workspace_mode, team_snapshot_json,
                        budget_limit_tokens, revision, created_utc, updated_utc)
                    VALUES
                        ('mission-1', 'thread-1', 'delivery-1',
                         'one', 'active', 'worktree', '{}', 10, 0, 1, 1),
                        ('mission-2', 'thread-1', 'delivery-2',
                         'two', 'active', 'project', '{}', 10, 0, 1, 1),
                        ('mission-3', 'thread-1', 'delivery-3',
                         'three', 'active', 'project', '{}', 10, 0, 1, 1);
                    INSERT INTO mission_members (
                        mission_member_id, mission_id, agent_profile_id,
                        alias, normalized_alias, role, description, ordinal,
                        profile_snapshot_json)
                    VALUES (
                        'member-1', 'mission-1', 'profile-1',
                        'worker', 'WORKER', 'leader', '', 0, '{}');
                    INSERT INTO cowork_command_receipts (
                        command_id, actor_id, command_kind, target_id,
                        request_sha256, result_json, revision, created_utc)
                    VALUES ('command-1', 'actor-1', 'createMission', 'mission-1',
                            'sha', '{}', 1, 1);
                    INSERT INTO cowork_dispatch_intents (
                        intent_id, idempotency_key, command_id,
                        dispatch_kind, entity_kind, entity_id, status,
                        attempt_count, lease_expires_utc, created_utc, updated_utc)
                    VALUES ('intent-1', 'dispatch-1', 'command-1',
                            'createThread', 'mission', 'mission-1', 'pending',
                            0, NULL, 1, 1);
                    INSERT INTO cowork_budget_scopes (
                        scope_id, owner_kind, owner_id, limit_tokens,
                        reserved_tokens, used_tokens, revision)
                    VALUES ('budget-1', 'mission', 'mission-1', 10, 0, 0, 0);
                    INSERT INTO agent_runs (
                        agent_run_id, mission_id, member_id, thread_id,
                        run_kind, status, workspace_mode, workspace_access,
                        budget_limit_tokens, budget_reserved_tokens,
                        budget_used_tokens, attempt, created_utc, updated_utc)
                    VALUES
                        ('run-1', 'mission-1', 'member-1', NULL,
                         'missionTask', 'starting', 'worktree', 'readWrite',
                         10, 0, 0, 1, 1, 1),
                        ('run-2', 'mission-2', NULL, NULL,
                         'missionTask', 'starting', 'project', 'readWrite',
                         10, 0, 0, 1, 1, 1);
                    """,
                    token);
                return 0;
            },
            cancellationToken);

        await Assert.ThrowsAnyAsync<DbException>(() =>
            store.WriteAsync(
                    async (connection, transaction, token) =>
                    {
                        await ExecuteAsync(
                            connection,
                            transaction,
                            """
                            INSERT INTO cowork_command_receipts (
                                command_id, actor_id, command_kind, target_id,
                                request_sha256, result_json, revision, created_utc)
                            VALUES ('command-1', 'actor-2', 'createMission', 'mission-2',
                                    'sha-2', '{}', 2, 2);
                            """,
                            token);
                        return 0;
                    },
                    cancellationToken)
                .AsTask());

        await Assert.ThrowsAnyAsync<DbException>(() =>
            store.WriteAsync(
                    async (connection, transaction, token) =>
                    {
                        await ExecuteAsync(
                            connection,
                            transaction,
                            """
                            INSERT INTO cowork_dispatch_intents (
                                intent_id, idempotency_key, command_id,
                                dispatch_kind, entity_kind, entity_id, status,
                                attempt_count, lease_expires_utc, created_utc, updated_utc)
                            VALUES ('intent-2', 'dispatch-1', NULL,
                                    'createThread', 'mission', 'mission-2', 'pending',
                                    0, NULL, 2, 2);
                            """,
                            token);
                        return 0;
                    },
                    cancellationToken)
                .AsTask());

        Assert.Equal(
            1,
            await store.WriteAsync(
                (connection, transaction, token) =>
                    ExecuteAsync(
                        connection,
                        transaction,
                        """
                        UPDATE cowork_budget_scopes
                        SET reserved_tokens = reserved_tokens + 10,
                            revision = revision + 1
                        WHERE scope_id = 'budget-1'
                          AND used_tokens + reserved_tokens + 10 <= limit_tokens;
                        """,
                        token),
                cancellationToken));
        Assert.Equal(
            0,
            await store.WriteAsync(
                (connection, transaction, token) =>
                    ExecuteAsync(
                        connection,
                        transaction,
                        """
                        UPDATE cowork_budget_scopes
                        SET reserved_tokens = reserved_tokens + 1,
                            revision = revision + 1
                        WHERE scope_id = 'budget-1'
                          AND used_tokens + reserved_tokens + 1 <= limit_tokens;
                        """,
                        token),
                cancellationToken));

        await Assert.ThrowsAnyAsync<DbException>(() =>
            InsertRunAsync(
                    store,
                    "run-3",
                    "mission-1",
                    "member-1",
                    "worktree",
                    cancellationToken)
                .AsTask());
        await Assert.ThrowsAnyAsync<DbException>(() =>
            InsertRunAsync(
                    store,
                    "run-4",
                    "mission-3",
                    null,
                    "project",
                    cancellationToken)
                .AsTask());
    }

    private static ValueTask<int> CompareAndSwapAsync(
        IWorkspaceStateStore store,
        long expectedRevision,
        CancellationToken cancellationToken) =>
        store.WriteAsync(
            (connection, transaction, token) =>
                ExecuteAsync(
                    connection,
                    transaction,
                    $"""
                     UPDATE cowork_state
                     SET current_revision = current_revision + 1
                     WHERE id = 1 AND current_revision = {expectedRevision};
                     """,
                    token),
            cancellationToken);

    private static ValueTask<int> InsertRunAsync(
        IWorkspaceStateStore store,
        string runId,
        string missionId,
        string? memberId,
        string workspaceMode,
        CancellationToken cancellationToken) =>
        store.WriteAsync(
            (connection, transaction, token) =>
                ExecuteAsync(
                    connection,
                    transaction,
                    $"""
                     INSERT INTO agent_runs (
                         agent_run_id, mission_id, member_id, thread_id,
                         run_kind, status, workspace_mode, workspace_access,
                         budget_limit_tokens, budget_reserved_tokens,
                         budget_used_tokens, attempt, created_utc, updated_utc)
                     VALUES (
                         '{runId}', '{missionId}',
                         {(memberId is null ? "NULL" : $"'{memberId}'")}, NULL,
                         'missionTask', 'running', '{workspaceMode}', 'readWrite',
                         10, 0, 0, 1, 1, 1);
                     """,
                    token),
            cancellationToken);

    private static async Task<StateRuntime> CreateStoreAsync(
        TempWorkspace files,
        CancellationToken cancellationToken)
    {
        var runtime = new StateRuntime(
            files.Paths,
            TimeSpan.FromSeconds(2),
            TeamsStateMigrationContributors.Create());
        await runtime.InitializeAsync(cancellationToken);
        return runtime;
    }

    private static async ValueTask<int> ExecuteAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask<T> ScalarAsync<T>(
        DbConnection connection,
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

    private sealed class TempWorkspace : IDisposable
    {
        public TempWorkspace()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"opencowork-persistence-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            Paths = new OpenCoWorkPaths(Root);
        }

        public string Root { get; }

        public OpenCoWorkPaths Paths { get; }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
        }
    }
}
