using System.Data.Common;
using OpenCoWork.Abstractions;

namespace OpenCoWork.Automations;

public static class AutomationsStateMigrationContributors
{
    public static IReadOnlyList<IWorkspaceStateMigrationContributor> Create() =>
        [new AutomationsStateMigrationContributor()];
}

internal sealed class AutomationsStateMigrationContributor
    : IWorkspaceStateMigrationContributor
{
    private static readonly string[] Tables =
    [
        "automation_command_receipts",
        "automation_definitions",
        "automation_dispatch_intents",
        "automation_runs",
        "automation_schedules",
        "automation_state",
    ];

    private static readonly string[] Indexes =
    [
        "ix_automation_command_receipts_created",
        "ix_automation_definitions_status",
        "ix_automation_dispatch_intents_lease",
        "ix_automation_runs_active",
        "ix_automation_runs_status",
        "ix_automation_schedules_due",
    ];

    private const string Sql =
        """
        CREATE TABLE automation_state (
            id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
            automation_revision INTEGER NOT NULL DEFAULT 0 CHECK (automation_revision >= 0),
            updated_utc INTEGER NOT NULL
        );
        INSERT INTO automation_state (id, automation_revision, updated_utc)
        VALUES (1, 0, 0);

        CREATE TABLE automation_definitions (
            automation_id TEXT NOT NULL PRIMARY KEY CHECK (
                length(automation_id) BETWEEN 1 AND 128 AND
                automation_id = lower(automation_id) AND
                automation_id NOT GLOB '*[^a-z0-9-]*' AND
                automation_id NOT LIKE '-%' AND
                automation_id NOT LIKE '%-' AND
                automation_id NOT LIKE '%--%'),
            source_relative_path TEXT NOT NULL UNIQUE,
            source_status TEXT NOT NULL CHECK (
                source_status IN ('ready', 'faulted', 'missing')),
            source_sha256 TEXT NULL CHECK (
                source_sha256 IS NULL OR length(source_sha256) = 64),
            definition_version TEXT NULL CHECK (
                definition_version IS NULL OR length(definition_version) = 64),
            display_name TEXT NOT NULL,
            enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
            definition_json TEXT NULL CHECK (
                definition_json IS NULL OR json_valid(definition_json)),
            diagnostics_json TEXT NOT NULL CHECK (json_valid(diagnostics_json)),
            has_schedule INTEGER NOT NULL CHECK (has_schedule IN (0, 1)),
            revision INTEGER NOT NULL CHECK (revision > 0),
            created_utc INTEGER NOT NULL,
            updated_utc INTEGER NOT NULL,
            missing_utc INTEGER NULL
        );
        CREATE INDEX ix_automation_definitions_status
            ON automation_definitions (source_status, enabled, automation_id);

        CREATE TABLE automation_schedules (
            automation_id TEXT NOT NULL PRIMARY KEY,
            cron TEXT NOT NULL,
            time_zone TEXT NOT NULL,
            next_occurrence_utc INTEGER NULL,
            last_occurrence_utc INTEGER NULL,
            coalesced_occurrence_utc INTEGER NULL,
            revision INTEGER NOT NULL CHECK (revision > 0),
            updated_utc INTEGER NOT NULL,
            FOREIGN KEY (automation_id)
                REFERENCES automation_definitions (automation_id) ON DELETE CASCADE
        );
        CREATE INDEX ix_automation_schedules_due
            ON automation_schedules (next_occurrence_utc, automation_id);

        CREATE TABLE automation_runs (
            automation_run_id TEXT NOT NULL PRIMARY KEY CHECK (
                length(automation_run_id) = 36 AND
                automation_run_id = lower(automation_run_id) AND
                substr(automation_run_id, 9, 1) = '-' AND
                substr(automation_run_id, 14, 1) = '-' AND
                substr(automation_run_id, 15, 1) = '7' AND
                substr(automation_run_id, 19, 1) = '-' AND
                substr(automation_run_id, 20, 1) IN ('8', '9', 'a', 'b') AND
                substr(automation_run_id, 24, 1) = '-'),
            automation_id TEXT NOT NULL,
            trigger_kind TEXT NOT NULL CHECK (trigger_kind IN ('manual', 'cron')),
            trigger_idempotency_key TEXT NOT NULL UNIQUE,
            scheduled_occurrence_utc INTEGER NULL,
            status TEXT NOT NULL CHECK (
                status IN (
                    'pending', 'running', 'needsAttention',
                    'completed', 'failed', 'cancelled', 'timedOut')),
            definition_snapshot_json TEXT NOT NULL CHECK (
                json_valid(definition_snapshot_json)),
            inputs_json TEXT NOT NULL CHECK (json_valid(inputs_json)),
            rendered_prompt TEXT NULL,
            workspace_mode TEXT NOT NULL CHECK (
                workspace_mode IN ('project', 'worktree')),
            workspace_access TEXT NOT NULL CHECK (
                workspace_access IN ('readOnly', 'readWrite')),
            provider_id TEXT NOT NULL,
            model_id TEXT NOT NULL,
            permission_snapshot_json TEXT NOT NULL CHECK (
                json_valid(permission_snapshot_json)),
            capability_snapshot_json TEXT NOT NULL CHECK (
                json_valid(capability_snapshot_json)),
            run_deadline_utc INTEGER NOT NULL,
            attention_kind TEXT NULL CHECK (
                attention_kind IS NULL OR attention_kind IN (
                    'approvalRequired', 'userInputRequired', 'outcomeUnknown')),
            attention_deadline_utc INTEGER NULL,
            thread_id TEXT NULL UNIQUE CHECK (
                thread_id IS NULL OR (
                    length(thread_id) = 36 AND substr(thread_id, 15, 1) = '7')),
            worktree_id TEXT NULL UNIQUE CHECK (
                worktree_id IS NULL OR (
                    length(worktree_id) = 36 AND substr(worktree_id, 15, 1) = '7')),
            base_commit_sha TEXT NULL,
            project_writer_lease_id TEXT NULL,
            project_writer_lease_expires_utc INTEGER NULL,
            safe_summary TEXT NULL CHECK (
                safe_summary IS NULL OR length(CAST(safe_summary AS BLOB)) <= 16384),
            error_code TEXT NULL,
            diagnostic TEXT NULL,
            revision INTEGER NOT NULL CHECK (revision > 0),
            created_utc INTEGER NOT NULL,
            started_utc INTEGER NULL,
            updated_utc INTEGER NOT NULL,
            completed_utc INTEGER NULL,
            FOREIGN KEY (automation_id)
                REFERENCES automation_definitions (automation_id) ON DELETE RESTRICT,
            FOREIGN KEY (thread_id) REFERENCES threads (thread_id) ON DELETE RESTRICT,
            CHECK (
                (project_writer_lease_id IS NULL AND
                 project_writer_lease_expires_utc IS NULL) OR
                (project_writer_lease_id IS NOT NULL AND
                 project_writer_lease_expires_utc IS NOT NULL))
        );
        CREATE UNIQUE INDEX ix_automation_runs_active
            ON automation_runs (automation_id)
            WHERE status IN ('pending', 'running', 'needsAttention');
        CREATE INDEX ix_automation_runs_status
            ON automation_runs (status, updated_utc, automation_run_id);

        CREATE TABLE automation_dispatch_intents (
            intent_id TEXT NOT NULL PRIMARY KEY CHECK (
                length(intent_id) = 36 AND intent_id = lower(intent_id) AND
                substr(intent_id, 9, 1) = '-' AND
                substr(intent_id, 14, 1) = '-' AND
                substr(intent_id, 15, 1) = '7' AND
                substr(intent_id, 19, 1) = '-' AND
                substr(intent_id, 20, 1) IN ('8', '9', 'a', 'b') AND
                substr(intent_id, 24, 1) = '-'),
            idempotency_key TEXT NOT NULL UNIQUE,
            dispatch_kind TEXT NOT NULL CHECK (
                dispatch_kind IN (
                    'createWorktree', 'createThread', 'submitTurn',
                    'archiveThread', 'cleanupWorktree')),
            entity_kind TEXT NOT NULL CHECK (
                entity_kind IN ('automationRun', 'thread', 'worktree')),
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
        CREATE INDEX ix_automation_dispatch_intents_lease
            ON automation_dispatch_intents (
                status, lease_expires_utc, created_utc, intent_id);

        CREATE TABLE automation_command_receipts (
            command_id TEXT NOT NULL PRIMARY KEY CHECK (
                length(command_id) = 36 AND command_id = lower(command_id) AND
                substr(command_id, 9, 1) = '-' AND
                substr(command_id, 14, 1) = '-' AND
                substr(command_id, 15, 1) = '7' AND
                substr(command_id, 19, 1) = '-' AND
                substr(command_id, 20, 1) IN ('8', '9', 'a', 'b') AND
                substr(command_id, 24, 1) = '-'),
            actor_kind TEXT NOT NULL CHECK (actor_kind IN ('host', 'scheduler')),
            actor_id TEXT NOT NULL,
            command_kind TEXT NOT NULL,
            target_id TEXT NULL,
            request_sha256 TEXT NOT NULL CHECK (length(request_sha256) = 64),
            result_json TEXT NOT NULL CHECK (json_valid(result_json)),
            revision INTEGER NOT NULL CHECK (revision >= 0),
            created_utc INTEGER NOT NULL
        );
        CREATE INDEX ix_automation_command_receipts_created
            ON automation_command_receipts (created_utc, command_id);
        """;

    public int TargetVersion => 7;

    public ValueTask ApplyAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, Sql, cancellationToken);

    public async ValueTask ValidateAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        var tables = await ReadNamesAsync(
            connection,
            "table",
            Tables,
            cancellationToken);
        if (!tables.SequenceEqual(Tables, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Automation state tables are incomplete.");
        }

        var indexes = await ReadNamesAsync(
            connection,
            "index",
            Indexes,
            cancellationToken);
        if (!indexes.SequenceEqual(Indexes, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Automation state indexes are incomplete.");
        }

        if (await ScalarAsync(
                connection,
                "SELECT count(*) FROM automation_state WHERE id = 1;",
                cancellationToken) != 1)
        {
            throw new InvalidOperationException("Automation state singleton is missing.");
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

    private static async ValueTask<long> ScalarAsync(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }
}
