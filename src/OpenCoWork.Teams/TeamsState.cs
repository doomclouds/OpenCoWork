using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using OpenCoWork.Abstractions;

namespace OpenCoWork.Teams;

[ConfigSection("teams")]
public sealed record CoWorkConfig : IValidatableObject
{
    [Range(1, 4)]
    public int MaxDepth { get; init; } = CoWorkRuntimeLimits.DefaultMaxDepth;

    [Range(1, 64)]
    public int MaxConcurrentAgentRuns { get; init; } =
        CoWorkRuntimeLimits.DefaultMaximumConcurrentAgentRuns;

    [Range(1, 16)]
    public int MaxConcurrentAgentRunsPerMission { get; init; } =
        CoWorkRuntimeLimits.DefaultMaximumConcurrentAgentRunsPerMission;

    [Range(1, 16)]
    public int MaximumMembersPerMission { get; init; } =
        CoWorkRuntimeLimits.MaximumMissionMembers;

    [Range(1, 256)]
    public int MaximumTasksPerMission { get; init; } =
        CoWorkRuntimeLimits.MaximumMissionTasks;

    [Range(1, 65_536)]
    public int MaximumMailboxMessageBytes { get; init; } =
        CoWorkRuntimeLimits.MaximumMailboxMessageBytes;

    [Range(1, 67_108_864)]
    public int MaximumArtifactBytes { get; init; } =
        (int)CoWorkRuntimeLimits.MaximumArtifactBytes;

    [Range(1, 536_870_912)]
    public int MaximumOwnedFileBytes { get; init; } =
        (int)CoWorkRuntimeLimits.MaximumOwnedFileBytes;

    [Range(
        CoWorkRuntimeLimits.DispatchAttempts,
        CoWorkRuntimeLimits.DispatchAttempts)]
    public int MaximumDispatchAttempts { get; init; } =
        CoWorkRuntimeLimits.DispatchAttempts;

    public TimeSpan DispatchLease { get; init; } = CoWorkRuntimeLimits.DispatchLease;

    public TimeSpan LeaseRenewalInterval { get; init; } =
        CoWorkRuntimeLimits.LeaseRenewal;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (MaxConcurrentAgentRunsPerMission > MaxConcurrentAgentRuns)
        {
            yield return new ValidationResult(
                "MaxConcurrentAgentRunsPerMission cannot exceed MaxConcurrentAgentRuns.",
                [nameof(MaxConcurrentAgentRunsPerMission)]);
        }

        if (DispatchLease != CoWorkRuntimeLimits.DispatchLease)
        {
            yield return new ValidationResult(
                "DispatchLease is fixed at two minutes.",
                [nameof(DispatchLease)]);
        }

        if (LeaseRenewalInterval != CoWorkRuntimeLimits.LeaseRenewal)
        {
            yield return new ValidationResult(
                "LeaseRenewalInterval is fixed at thirty seconds.",
                [nameof(LeaseRenewalInterval)]);
        }
    }
}

public static class TeamsStateMigrationContributors
{
    public static IReadOnlyList<IWorkspaceStateMigrationContributor> Create() =>
        [new TeamsStateMigrationContributor()];
}

internal sealed class TeamsStateMigrationContributor
    : IWorkspaceStateMigrationContributor
{
    private static readonly string[] Tables =
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

    private static readonly string[] Indexes =
    [
        "ix_agent_runs_active_member",
        "ix_agent_runs_mission_status",
        "ix_agent_runs_project_writer",
        "ix_cowork_command_receipts_created",
        "ix_cowork_dispatch_intents_lease",
        "ix_cowork_files_mission",
        "ix_cowork_files_mission_sha",
        "ix_cowork_worktrees_status",
        "ix_mailbox_messages_delivery",
        "ix_mission_members_leader",
        "ix_mission_tasks_mission_status",
        "ix_missions_status",
        "ix_team_members_leader",
    ];

    private static readonly (string Table, string[] Columns)[] RequiredColumns =
    [
        ("agent_profiles", ["description"]),
        ("mission_members", ["description"]),
        ("mission_tasks", ["objective", "instructions"]),
    ];

    private const string Sql =
        """
        CREATE TABLE cowork_state (
            id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
            current_revision INTEGER NOT NULL DEFAULT 0 CHECK (current_revision >= 0),
            updated_utc INTEGER NOT NULL
        );
        INSERT INTO cowork_state (id, current_revision, updated_utc) VALUES (1, 0, 0);

        CREATE TABLE agent_profiles (
            agent_profile_id TEXT NOT NULL PRIMARY KEY,
            name TEXT NOT NULL,
            normalized_name TEXT NOT NULL UNIQUE,
            description TEXT NOT NULL,
            instructions TEXT NOT NULL,
            model_json TEXT NOT NULL CHECK (json_valid(model_json)),
            tools_json TEXT NOT NULL CHECK (json_valid(tools_json)),
            permission_json TEXT NOT NULL CHECK (json_valid(permission_json)),
            enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
            revision INTEGER NOT NULL CHECK (revision >= 0),
            created_utc INTEGER NOT NULL,
            updated_utc INTEGER NOT NULL
        );

        CREATE TABLE teams (
            team_id TEXT NOT NULL PRIMARY KEY,
            name TEXT NOT NULL,
            normalized_name TEXT NOT NULL UNIQUE,
            description TEXT NULL,
            enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
            revision INTEGER NOT NULL CHECK (revision >= 0),
            created_utc INTEGER NOT NULL,
            updated_utc INTEGER NOT NULL
        );

        CREATE TABLE team_members (
            team_member_id TEXT NOT NULL PRIMARY KEY,
            team_id TEXT NOT NULL,
            agent_profile_id TEXT NOT NULL,
            alias TEXT NOT NULL,
            normalized_alias TEXT NOT NULL,
            role TEXT NOT NULL CHECK (role IN ('leader', 'member')),
            description TEXT NULL,
            ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
            FOREIGN KEY (team_id) REFERENCES teams (team_id) ON DELETE CASCADE,
            FOREIGN KEY (agent_profile_id)
                REFERENCES agent_profiles (agent_profile_id) ON DELETE RESTRICT,
            UNIQUE (team_id, normalized_alias),
            UNIQUE (team_id, ordinal)
        );
        CREATE UNIQUE INDEX ix_team_members_leader
            ON team_members (team_id)
            WHERE role = 'leader';

        CREATE TABLE missions (
            mission_id TEXT NOT NULL PRIMARY KEY,
            origin_thread_id TEXT NOT NULL,
            leader_thread_id TEXT NULL,
            origin_delivery_id TEXT NOT NULL UNIQUE,
            team_id TEXT NULL,
            objective TEXT NOT NULL,
            status TEXT NOT NULL CHECK (
                status IN (
                    'planning', 'active', 'awaitingLeaderReview',
                    'completed', 'failed', 'cancelled')),
            workspace_mode TEXT NOT NULL CHECK (
                workspace_mode IN ('project', 'worktree')),
            planning_team_revision INTEGER NULL CHECK (
                planning_team_revision IS NULL OR planning_team_revision >= 0),
            team_snapshot_json TEXT NOT NULL CHECK (json_valid(team_snapshot_json)),
            base_commit_sha TEXT NULL,
            budget_limit_tokens INTEGER NOT NULL CHECK (budget_limit_tokens >= 0),
            final_summary TEXT NULL,
            provenance_json TEXT NULL CHECK (
                provenance_json IS NULL OR json_valid(provenance_json)),
            origin_delivered_utc INTEGER NULL,
            revision INTEGER NOT NULL CHECK (revision >= 0),
            created_utc INTEGER NOT NULL,
            updated_utc INTEGER NOT NULL,
            completed_utc INTEGER NULL,
            FOREIGN KEY (origin_thread_id)
                REFERENCES threads (thread_id) ON DELETE RESTRICT,
            FOREIGN KEY (leader_thread_id)
                REFERENCES threads (thread_id) ON DELETE RESTRICT,
            FOREIGN KEY (team_id) REFERENCES teams (team_id) ON DELETE RESTRICT
        );
        CREATE INDEX ix_missions_status
            ON missions (status, updated_utc DESC);

        CREATE TABLE mission_members (
            mission_member_id TEXT NOT NULL PRIMARY KEY,
            mission_id TEXT NOT NULL,
            agent_profile_id TEXT NOT NULL,
            alias TEXT NOT NULL,
            normalized_alias TEXT NOT NULL,
            role TEXT NOT NULL CHECK (role IN ('leader', 'member')),
            description TEXT NOT NULL,
            ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
            profile_snapshot_json TEXT NOT NULL CHECK (json_valid(profile_snapshot_json)),
            FOREIGN KEY (mission_id) REFERENCES missions (mission_id) ON DELETE CASCADE,
            FOREIGN KEY (agent_profile_id)
                REFERENCES agent_profiles (agent_profile_id) ON DELETE RESTRICT,
            UNIQUE (mission_id, normalized_alias),
            UNIQUE (mission_id, ordinal)
        );
        CREATE UNIQUE INDEX ix_mission_members_leader
            ON mission_members (mission_id)
            WHERE role = 'leader';

        CREATE TABLE mission_tasks (
            mission_task_id TEXT NOT NULL PRIMARY KEY,
            mission_id TEXT NOT NULL,
            assigned_member_id TEXT NULL,
            alias TEXT NOT NULL,
            normalized_alias TEXT NOT NULL,
            objective TEXT NOT NULL,
            instructions TEXT NOT NULL,
            is_required INTEGER NOT NULL CHECK (is_required IN (0, 1)),
            review_required INTEGER NOT NULL CHECK (review_required IN (0, 1)),
            waived INTEGER NOT NULL DEFAULT 0 CHECK (waived IN (0, 1)),
            dependency_ids_json TEXT NOT NULL CHECK (json_valid(dependency_ids_json)),
            status TEXT NOT NULL CHECK (
                status IN (
                    'pending', 'waitingDependencies', 'ready', 'running',
                    'blocked', 'review', 'completed', 'failed', 'cancelled')),
            attempt_count INTEGER NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
            blocker TEXT NULL,
            output_summary TEXT NULL,
            artifact_ids_json TEXT NULL CHECK (
                artifact_ids_json IS NULL OR json_valid(artifact_ids_json)),
            error_code TEXT NULL,
            revision INTEGER NOT NULL CHECK (revision >= 0),
            created_utc INTEGER NOT NULL,
            updated_utc INTEGER NOT NULL,
            completed_utc INTEGER NULL,
            FOREIGN KEY (mission_id) REFERENCES missions (mission_id) ON DELETE CASCADE,
            FOREIGN KEY (assigned_member_id)
                REFERENCES mission_members (mission_member_id) ON DELETE SET NULL,
            UNIQUE (mission_id, normalized_alias)
        );
        CREATE INDEX ix_mission_tasks_mission_status
            ON mission_tasks (mission_id, status, updated_utc);

        CREATE TABLE cowork_budget_scopes (
            scope_id TEXT NOT NULL PRIMARY KEY,
            owner_kind TEXT NOT NULL CHECK (
                owner_kind IN ('mission', 'task', 'agentRun')),
            owner_id TEXT NOT NULL,
            limit_tokens INTEGER NOT NULL CHECK (limit_tokens > 0),
            reserved_tokens INTEGER NOT NULL CHECK (reserved_tokens >= 0),
            used_tokens INTEGER NOT NULL CHECK (used_tokens >= 0),
            revision INTEGER NOT NULL CHECK (revision >= 0),
            CHECK (reserved_tokens + used_tokens <= limit_tokens),
            UNIQUE (owner_kind, owner_id)
        );

        CREATE TABLE agent_runs (
            agent_run_id TEXT NOT NULL PRIMARY KEY,
            mission_id TEXT NULL,
            mission_task_id TEXT NULL,
            member_id TEXT NULL,
            thread_id TEXT NULL,
            parent_agent_run_id TEXT NULL,
            parent_thread_id TEXT NULL,
            run_kind TEXT NOT NULL CHECK (
                run_kind IN (
                    'direct', 'leaderPlanning', 'leaderReview',
                    'leaderSynthesis', 'missionTask')),
            status TEXT NOT NULL CHECK (
                status IN (
                    'pending', 'starting', 'running',
                    'completed', 'failed', 'cancelled')),
            profile_snapshot_json TEXT NOT NULL DEFAULT '{}' CHECK (
                json_valid(profile_snapshot_json)),
            workspace_mode TEXT NOT NULL CHECK (
                workspace_mode IN ('scratchpad', 'project', 'worktree')),
            workspace_access TEXT NOT NULL CHECK (
                workspace_access IN ('readOnly', 'readWrite')),
            workspace_json TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(workspace_json)),
            budget_limit_tokens INTEGER NOT NULL CHECK (budget_limit_tokens >= 0),
            budget_reserved_tokens INTEGER NOT NULL CHECK (budget_reserved_tokens >= 0),
            budget_used_tokens INTEGER NOT NULL CHECK (budget_used_tokens >= 0),
            attempt INTEGER NOT NULL CHECK (attempt > 0),
            lease_owner TEXT NULL,
            lease_expires_utc INTEGER NULL,
            error_code TEXT NULL,
            diagnostic TEXT NULL,
            created_utc INTEGER NOT NULL,
            updated_utc INTEGER NOT NULL,
            completed_utc INTEGER NULL,
            CHECK (budget_reserved_tokens + budget_used_tokens <= budget_limit_tokens),
            FOREIGN KEY (mission_id) REFERENCES missions (mission_id) ON DELETE RESTRICT,
            FOREIGN KEY (mission_task_id)
                REFERENCES mission_tasks (mission_task_id) ON DELETE RESTRICT,
            FOREIGN KEY (member_id)
                REFERENCES mission_members (mission_member_id) ON DELETE RESTRICT,
            FOREIGN KEY (thread_id) REFERENCES threads (thread_id) ON DELETE RESTRICT,
            FOREIGN KEY (parent_thread_id)
                REFERENCES threads (thread_id) ON DELETE RESTRICT,
            FOREIGN KEY (parent_agent_run_id)
                REFERENCES agent_runs (agent_run_id) ON DELETE RESTRICT,
            UNIQUE (mission_task_id, attempt)
        );
        CREATE INDEX ix_agent_runs_mission_status
            ON agent_runs (mission_id, status, updated_utc);
        CREATE UNIQUE INDEX ix_agent_runs_active_member
            ON agent_runs (mission_id, member_id)
            WHERE member_id IS NOT NULL
              AND status IN ('pending', 'starting', 'running');
        CREATE UNIQUE INDEX ix_agent_runs_project_writer
            ON agent_runs ((1))
            WHERE workspace_mode = 'project'
              AND workspace_access = 'readWrite'
              AND status IN ('pending', 'starting', 'running');

        CREATE TABLE mailbox_messages (
            mailbox_message_id TEXT NOT NULL PRIMARY KEY,
            mission_id TEXT NULL,
            scope TEXT NOT NULL CHECK (scope IN ('mission', 'direct')),
            sender_member_id TEXT NULL,
            recipient_member_id TEXT NULL,
            sender_thread_id TEXT NULL,
            recipient_thread_id TEXT NULL,
            mission_task_id TEXT NULL,
            artifact_id TEXT NULL,
            message_kind TEXT NOT NULL CHECK (
                message_kind IN (
                    'info', 'request', 'handoff', 'blocker', 'review', 'rework')),
            content TEXT NOT NULL,
            content_length INTEGER NOT NULL CHECK (
                content_length >= 0 AND content_length <= 65536),
            status TEXT NOT NULL CHECK (
                status IN ('pending', 'delivered', 'acknowledged', 'deadLettered')),
            attempt_count INTEGER NOT NULL DEFAULT 0 CHECK (
                attempt_count >= 0 AND attempt_count <= 5),
            lease_owner TEXT NULL,
            lease_expires_utc INTEGER NULL,
            error_code TEXT NULL,
            diagnostic TEXT NULL,
            created_utc INTEGER NOT NULL,
            delivered_utc INTEGER NULL,
            acknowledged_utc INTEGER NULL,
            FOREIGN KEY (mission_id) REFERENCES missions (mission_id) ON DELETE CASCADE,
            FOREIGN KEY (sender_member_id)
                REFERENCES mission_members (mission_member_id) ON DELETE RESTRICT,
            FOREIGN KEY (recipient_member_id)
                REFERENCES mission_members (mission_member_id) ON DELETE RESTRICT,
            FOREIGN KEY (sender_thread_id)
                REFERENCES threads (thread_id) ON DELETE RESTRICT,
            FOREIGN KEY (recipient_thread_id)
                REFERENCES threads (thread_id) ON DELETE RESTRICT,
            FOREIGN KEY (mission_task_id)
                REFERENCES mission_tasks (mission_task_id) ON DELETE RESTRICT,
            FOREIGN KEY (artifact_id)
                REFERENCES cowork_files (cowork_file_id) ON DELETE RESTRICT
        );
        CREATE INDEX ix_mailbox_messages_delivery
            ON mailbox_messages (mission_id, recipient_member_id, status, created_utc);

        CREATE TABLE cowork_files (
            cowork_file_id TEXT NOT NULL PRIMARY KEY,
            mission_id TEXT NULL,
            agent_run_id TEXT NOT NULL,
            area TEXT NOT NULL CHECK (area IN ('workspace', 'scratchpad')),
            kind TEXT NOT NULL CHECK (kind IN ('scratchpad', 'artifact')),
            relative_path TEXT NOT NULL,
            sha256 TEXT NULL CHECK (
                sha256 IS NULL OR length(sha256) = 64),
            size_bytes INTEGER NOT NULL CHECK (
                size_bytes >= 0 AND size_bytes <= 67108864),
            media_type TEXT NULL,
            display_name TEXT NULL,
            visibility TEXT NOT NULL CHECK (
                visibility IN ('private', 'mission', 'origin')),
            status TEXT NOT NULL CHECK (
                status IN ('available', 'unavailable')),
            created_utc INTEGER NOT NULL,
            updated_utc INTEGER NOT NULL,
            FOREIGN KEY (mission_id) REFERENCES missions (mission_id) ON DELETE RESTRICT,
            FOREIGN KEY (agent_run_id)
                REFERENCES agent_runs (agent_run_id) ON DELETE RESTRICT,
            CHECK (kind = 'scratchpad' OR sha256 IS NOT NULL),
            CHECK (
                (kind = 'scratchpad' AND visibility = 'private') OR
                (kind = 'artifact' AND visibility IN ('mission', 'origin'))),
            UNIQUE (agent_run_id, area, relative_path)
        );
        CREATE INDEX ix_cowork_files_mission
            ON cowork_files (mission_id, kind, created_utc);
        CREATE UNIQUE INDEX ix_cowork_files_mission_sha
            ON cowork_files (mission_id, sha256)
            WHERE kind = 'artifact';

        CREATE TABLE cowork_worktrees (
            cowork_worktree_id TEXT NOT NULL PRIMARY KEY,
            mission_id TEXT NULL,
            agent_run_id TEXT NOT NULL UNIQUE,
            relative_path TEXT NOT NULL UNIQUE,
            base_commit_sha TEXT NOT NULL,
            status TEXT NOT NULL CHECK (
                status IN (
                    'creating', 'ready', 'removing', 'removed',
                    'retainedDirty', 'faulted')),
            is_dirty INTEGER NOT NULL CHECK (is_dirty IN (0, 1)),
            trust_json TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(trust_json)),
            diagnostic TEXT NULL,
            created_utc INTEGER NOT NULL,
            updated_utc INTEGER NOT NULL,
            FOREIGN KEY (mission_id) REFERENCES missions (mission_id) ON DELETE RESTRICT,
            FOREIGN KEY (agent_run_id)
                REFERENCES agent_runs (agent_run_id) ON DELETE RESTRICT
        );
        CREATE INDEX ix_cowork_worktrees_status
            ON cowork_worktrees (status, updated_utc);

        CREATE TABLE cowork_dispatch_intents (
            intent_id TEXT NOT NULL PRIMARY KEY,
            idempotency_key TEXT NOT NULL UNIQUE,
            command_id TEXT NULL,
            dispatch_kind TEXT NOT NULL CHECK (
                dispatch_kind IN (
                    'createThread', 'createWorktree', 'submitTurn',
                    'deliverMessage', 'finalizeArtifact', 'synthesizeMission',
                    'deliverOrigin', 'cleanup')),
            entity_kind TEXT NOT NULL,
            entity_id TEXT NOT NULL,
            status TEXT NOT NULL CHECK (
                status IN ('pending', 'leased', 'completed', 'deadLettered')),
            attempt_count INTEGER NOT NULL CHECK (
                attempt_count >= 0 AND attempt_count <= 5),
            lease_owner TEXT NULL,
            lease_expires_utc INTEGER NULL,
            error_code TEXT NULL,
            diagnostic TEXT NULL,
            created_utc INTEGER NOT NULL,
            updated_utc INTEGER NOT NULL
        );
        CREATE INDEX ix_cowork_dispatch_intents_lease
            ON cowork_dispatch_intents (status, lease_expires_utc, created_utc);

        CREATE TABLE cowork_command_receipts (
            command_id TEXT NOT NULL PRIMARY KEY,
            actor_id TEXT NOT NULL,
            command_kind TEXT NOT NULL,
            target_id TEXT NULL,
            request_sha256 TEXT NOT NULL,
            result_json TEXT NOT NULL CHECK (json_valid(result_json)),
            revision INTEGER NOT NULL CHECK (revision >= 0),
            created_utc INTEGER NOT NULL
        );
        CREATE INDEX ix_cowork_command_receipts_created
            ON cowork_command_receipts (created_utc);
        """;

    public int TargetVersion => 6;

    public ValueTask ApplyAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, Sql, cancellationToken);

    public async ValueTask ValidateAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        var tables = await ReadNamesAsync(connection, "table", Tables, cancellationToken);
        if (!tables.SequenceEqual(Tables, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unexpected CoWork state tables: {string.Join(',', tables)}.");
        }

        var indexes = await ReadNamesAsync(connection, "index", Indexes, cancellationToken);
        if (!indexes.SequenceEqual(Indexes, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unexpected CoWork state indexes: {string.Join(',', indexes)}.");
        }

        foreach (var (table, required) in RequiredColumns)
        {
            var columns = await ReadColumnsAsync(
                connection,
                table,
                cancellationToken);
            if (!required.All(columns.Contains))
            {
                throw new InvalidOperationException(
                    $"CoWork state table '{table}' is missing required columns.");
            }
        }
    }

    private static async ValueTask ExecuteAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask<string[]> ReadNamesAsync(
        DbConnection connection,
        string type,
        IReadOnlyList<string> names,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
             SELECT name
             FROM sqlite_schema
             WHERE type = '{type}'
               AND name IN ({string.Join(',', names.Select(name => $"'{name}'"))})
             ORDER BY name;
             """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(0));
        }

        return result.ToArray();
    }

    private static async ValueTask<HashSet<string>> ReadColumnsAsync(
        DbConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info('{table}');";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(1));
        }

        return result;
    }
}
