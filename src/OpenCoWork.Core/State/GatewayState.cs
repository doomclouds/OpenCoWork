using System.Data.Common;
using OpenCoWork.Abstractions;

namespace OpenCoWork.Core.State;

public static class GatewayStateMigrationContributors
{
    public static IReadOnlyList<IWorkspaceStateMigrationContributor> Create() =>
        [new GatewayStateMigrationContributor()];
}

internal sealed class GatewayStateMigrationContributor
    : IWorkspaceStateMigrationContributor
{
    private static readonly string[] Tables =
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

    private static readonly string[] Indexes =
    [
        "ix_channel_inbound_claim",
        "ix_channel_media_sha",
        "ix_channel_outbox_claim",
        "ix_channels_runtime",
        "ix_improvement_proposals_status",
        "ix_insight_runs_status",
        "ix_trace_spans_correlation",
        "ix_trace_spans_started",
        "ix_workspace_heartbeat_expiry",
    ];

    private static readonly IReadOnlyDictionary<string, string[]> Columns =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["operations_state"] =
                ["id", "workspace_id", "current_revision", "updated_utc"],
            ["channels"] =
            [
                "channel_id", "kind", "enabled", "definition_sha256",
                "trust_status", "runtime_status", "diagnostic", "revision",
                "created_utc", "updated_utc",
            ],
            ["channel_thread_mappings"] =
            [
                "channel_id", "external_conversation_id", "thread_id",
                "thread_create_idempotency_key", "revision", "created_utc",
                "updated_utc",
            ],
            ["channel_inbound_messages"] =
            [
                "inbound_message_id", "channel_id", "external_message_id",
                "external_conversation_id", "partition_sequence", "payload_json",
                "body_sha256", "session_create_idempotency_key",
                "session_submit_idempotency_key", "correlation_id", "thread_id",
                "turn_id", "status", "attempt_count", "next_attempt_utc",
                "lease_owner_instance_id", "lease_expires_utc", "error_code",
                "diagnostic", "revision", "created_utc", "updated_utc",
                "delivered_utc",
            ],
            ["channel_media"] =
            [
                "media_id", "inbound_message_id", "ordinal", "relative_path",
                "media_type", "content_length", "content_sha256", "display_name",
                "created_utc",
            ],
            ["channel_outbox"] =
            [
                "outbox_message_id", "delivery_id", "channel_id",
                "external_conversation_id", "source_message_id", "thread_id",
                "turn_id", "correlation_id", "partition_sequence", "envelope_json",
                "body_sha256", "status", "attempt_count", "next_attempt_utc",
                "lease_owner_instance_id", "lease_expires_utc", "error_code",
                "diagnostic", "revision", "created_utc", "updated_utc", "sent_utc",
            ],
            ["workspace_heartbeat"] =
            [
                "id", "runtime_instance_id", "primary_host", "status",
                "snapshot_json", "observed_utc", "expires_utc", "stopped_utc",
                "revision",
            ],
            ["trace_spans"] =
            [
                "trace_id", "span_id", "parent_span_id", "name", "kind", "status",
                "correlation_id", "thread_id", "turn_id", "automation_run_id",
                "agent_run_id", "channel_id", "duration_ms", "tags_json",
                "error_code", "started_utc", "ended_utc",
            ],
            ["insight_runs"] =
            [
                "insight_run_id", "trigger_kind", "status", "high_watermark_utc",
                "diagnostic", "revision", "created_utc", "updated_utc",
                "completed_utc",
            ],
            ["improvement_proposals"] =
            [
                "proposal_id", "fingerprint_sha256", "proposal_type", "severity",
                "summary", "evidence_json", "status", "revision", "created_utc",
                "updated_utc", "reviewed_utc",
            ],
        };

    private const string Sql =
        """
        ALTER TABLE turns
            ADD COLUMN correlation_id TEXT NULL CHECK (
                correlation_id IS NULL OR (
                    length(correlation_id) = 36 AND
                    correlation_id = lower(correlation_id) AND
                    correlation_id GLOB '????????-????-7???-[89ab]???-????????????' AND
                    length(replace(correlation_id, '-', '')) = 32 AND
                    replace(correlation_id, '-', '') NOT GLOB '*[^0-9a-f]*'));

        CREATE TABLE operations_state (
            id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
            workspace_id TEXT NOT NULL UNIQUE CHECK (
                length(workspace_id) = 36 AND workspace_id = lower(workspace_id) AND
                workspace_id GLOB '????????-????-7???-[89ab]???-????????????' AND
                length(replace(workspace_id, '-', '')) = 32 AND
                replace(workspace_id, '-', '') NOT GLOB '*[^0-9a-f]*'),
            current_revision INTEGER NOT NULL CHECK (current_revision >= 0),
            updated_utc INTEGER NOT NULL
        );

        CREATE TABLE channels (
            channel_id TEXT NOT NULL PRIMARY KEY CHECK (
                length(channel_id) BETWEEN 1 AND 64 AND
                channel_id = lower(channel_id) AND
                channel_id NOT GLOB '*[^a-z0-9-]*' AND
                channel_id NOT LIKE '-%' AND channel_id NOT LIKE '%-' AND
                channel_id NOT LIKE '%--%'),
            kind TEXT NOT NULL CHECK (kind = 'webhook'),
            enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
            definition_sha256 TEXT NOT NULL CHECK (
                length(definition_sha256) = 64 AND
                definition_sha256 = lower(definition_sha256) AND
                definition_sha256 NOT GLOB '*[^0-9a-f]*'),
            trust_status TEXT NOT NULL CHECK (
                trust_status IN ('pending', 'trusted', 'denied')),
            runtime_status TEXT NOT NULL CHECK (
                runtime_status IN (
                    'disabled', 'pendingTrust', 'unavailable', 'ready', 'faulted',
                    'degraded', 'stopping', 'stopped')),
            diagnostic TEXT NULL CHECK (
                diagnostic IS NULL OR length(CAST(diagnostic AS BLOB)) <= 4096),
            revision INTEGER NOT NULL CHECK (revision > 0),
            created_utc INTEGER NOT NULL,
            updated_utc INTEGER NOT NULL
        );
        CREATE INDEX ix_channels_runtime
            ON channels (runtime_status, enabled, channel_id);

        CREATE TABLE channel_thread_mappings (
            channel_id TEXT NOT NULL,
            external_conversation_id TEXT NOT NULL CHECK (
                length(CAST(external_conversation_id AS BLOB)) BETWEEN 1 AND 1024),
            thread_id TEXT NULL UNIQUE,
            thread_create_idempotency_key TEXT NOT NULL UNIQUE CHECK (
                length(thread_create_idempotency_key) = 36 AND
                thread_create_idempotency_key = lower(thread_create_idempotency_key) AND
                thread_create_idempotency_key GLOB
                    '????????-????-7???-[89ab]???-????????????' AND
                length(replace(thread_create_idempotency_key, '-', '')) = 32 AND
                replace(thread_create_idempotency_key, '-', '')
                    NOT GLOB '*[^0-9a-f]*'),
            revision INTEGER NOT NULL CHECK (revision > 0),
            created_utc INTEGER NOT NULL,
            updated_utc INTEGER NOT NULL,
            PRIMARY KEY (channel_id, external_conversation_id),
            FOREIGN KEY (channel_id) REFERENCES channels (channel_id) ON DELETE CASCADE,
            FOREIGN KEY (thread_id) REFERENCES threads (thread_id) ON DELETE RESTRICT
        );

        CREATE TABLE channel_inbound_messages (
            inbound_message_id TEXT NOT NULL PRIMARY KEY CHECK (
                length(inbound_message_id) = 36 AND
                inbound_message_id = lower(inbound_message_id) AND
                inbound_message_id GLOB '????????-????-7???-[89ab]???-????????????' AND
                length(replace(inbound_message_id, '-', '')) = 32 AND
                replace(inbound_message_id, '-', '') NOT GLOB '*[^0-9a-f]*'),
            channel_id TEXT NOT NULL,
            external_message_id TEXT NOT NULL CHECK (
                length(CAST(external_message_id AS BLOB)) BETWEEN 1 AND 1024),
            external_conversation_id TEXT NOT NULL CHECK (
                length(CAST(external_conversation_id AS BLOB)) BETWEEN 1 AND 1024),
            partition_sequence INTEGER NOT NULL CHECK (partition_sequence > 0),
            payload_json TEXT NOT NULL CHECK (
                json_valid(payload_json) AND
                length(CAST(payload_json AS BLOB)) <= 25165824),
            body_sha256 TEXT NOT NULL CHECK (
                length(body_sha256) = 64 AND body_sha256 = lower(body_sha256) AND
                body_sha256 NOT GLOB '*[^0-9a-f]*'),
            session_create_idempotency_key TEXT NOT NULL UNIQUE CHECK (
                length(session_create_idempotency_key) = 36 AND
                session_create_idempotency_key = lower(session_create_idempotency_key) AND
                session_create_idempotency_key GLOB
                    '????????-????-7???-[89ab]???-????????????' AND
                length(replace(session_create_idempotency_key, '-', '')) = 32 AND
                replace(session_create_idempotency_key, '-', '')
                    NOT GLOB '*[^0-9a-f]*'),
            session_submit_idempotency_key TEXT NOT NULL UNIQUE CHECK (
                length(session_submit_idempotency_key) = 36 AND
                session_submit_idempotency_key = lower(session_submit_idempotency_key) AND
                session_submit_idempotency_key GLOB
                    '????????-????-7???-[89ab]???-????????????' AND
                length(replace(session_submit_idempotency_key, '-', '')) = 32 AND
                replace(session_submit_idempotency_key, '-', '')
                    NOT GLOB '*[^0-9a-f]*'),
            correlation_id TEXT NOT NULL CHECK (
                length(correlation_id) = 36 AND correlation_id = lower(correlation_id) AND
                correlation_id GLOB '????????-????-7???-[89ab]???-????????????' AND
                length(replace(correlation_id, '-', '')) = 32 AND
                replace(correlation_id, '-', '') NOT GLOB '*[^0-9a-f]*'),
            thread_id TEXT NULL,
            turn_id TEXT NULL,
            status TEXT NOT NULL CHECK (
                status IN ('pending', 'dispatching', 'delivered', 'failed', 'deadLettered')),
            attempt_count INTEGER NOT NULL CHECK (attempt_count >= 0),
            next_attempt_utc INTEGER NOT NULL,
            lease_owner_instance_id TEXT NULL CHECK (
                lease_owner_instance_id IS NULL OR (
                    length(lease_owner_instance_id) = 36 AND
                    lease_owner_instance_id = lower(lease_owner_instance_id) AND
                    lease_owner_instance_id GLOB
                        '????????-????-7???-[89ab]???-????????????' AND
                    length(replace(lease_owner_instance_id, '-', '')) = 32 AND
                    replace(lease_owner_instance_id, '-', '')
                        NOT GLOB '*[^0-9a-f]*')),
            lease_expires_utc INTEGER NULL,
            error_code TEXT NULL,
            diagnostic TEXT NULL CHECK (
                diagnostic IS NULL OR length(CAST(diagnostic AS BLOB)) <= 4096),
            revision INTEGER NOT NULL CHECK (revision > 0),
            created_utc INTEGER NOT NULL,
            updated_utc INTEGER NOT NULL,
            delivered_utc INTEGER NULL,
            FOREIGN KEY (channel_id) REFERENCES channels (channel_id) ON DELETE CASCADE,
            FOREIGN KEY (thread_id) REFERENCES threads (thread_id) ON DELETE RESTRICT,
            FOREIGN KEY (turn_id) REFERENCES turns (turn_id) ON DELETE RESTRICT,
            UNIQUE (channel_id, external_message_id),
            UNIQUE (channel_id, external_conversation_id, partition_sequence),
            CHECK ((lease_owner_instance_id IS NULL) = (lease_expires_utc IS NULL))
        );
        CREATE INDEX ix_channel_inbound_claim
            ON channel_inbound_messages (
                status, next_attempt_utc, lease_expires_utc,
                channel_id, external_conversation_id, partition_sequence);

        CREATE TABLE channel_media (
            media_id TEXT NOT NULL PRIMARY KEY CHECK (
                length(media_id) = 36 AND media_id = lower(media_id) AND
                media_id GLOB '????????-????-7???-[89ab]???-????????????' AND
                length(replace(media_id, '-', '')) = 32 AND
                replace(media_id, '-', '') NOT GLOB '*[^0-9a-f]*'),
            inbound_message_id TEXT NOT NULL,
            ordinal INTEGER NOT NULL CHECK (ordinal BETWEEN 0 AND 7),
            relative_path TEXT NOT NULL CHECK (
                length(CAST(relative_path AS BLOB)) BETWEEN 1 AND 512 AND
                relative_path NOT LIKE '/%' AND substr(relative_path, 1, 1) <> char(92) AND
                instr(relative_path, '..') = 0 AND instr(relative_path, ':') = 0),
            media_type TEXT NOT NULL CHECK (
                media_type IN (
                    'text/plain', 'application/pdf', 'image/png', 'image/jpeg',
                    'image/gif', 'image/webp')),
            content_length INTEGER NOT NULL CHECK (
                content_length BETWEEN 0 AND 8388608),
            content_sha256 TEXT NOT NULL CHECK (
                length(content_sha256) = 64 AND
                content_sha256 = lower(content_sha256) AND
                content_sha256 NOT GLOB '*[^0-9a-f]*'),
            display_name TEXT NOT NULL CHECK (
                length(CAST(display_name AS BLOB)) BETWEEN 1 AND 1024),
            created_utc INTEGER NOT NULL,
            FOREIGN KEY (inbound_message_id)
                REFERENCES channel_inbound_messages (inbound_message_id) ON DELETE CASCADE,
            UNIQUE (inbound_message_id, ordinal)
        );
        CREATE INDEX ix_channel_media_sha
            ON channel_media (content_sha256, media_id);

        CREATE TABLE channel_outbox (
            outbox_message_id TEXT NOT NULL PRIMARY KEY CHECK (
                length(outbox_message_id) = 36 AND
                outbox_message_id = lower(outbox_message_id) AND
                outbox_message_id GLOB '????????-????-7???-[89ab]???-????????????' AND
                length(replace(outbox_message_id, '-', '')) = 32 AND
                replace(outbox_message_id, '-', '') NOT GLOB '*[^0-9a-f]*'),
            delivery_id TEXT NOT NULL UNIQUE CHECK (
                length(delivery_id) = 36 AND delivery_id = lower(delivery_id) AND
                delivery_id GLOB '????????-????-7???-[89ab]???-????????????' AND
                length(replace(delivery_id, '-', '')) = 32 AND
                replace(delivery_id, '-', '') NOT GLOB '*[^0-9a-f]*'),
            channel_id TEXT NOT NULL,
            external_conversation_id TEXT NOT NULL CHECK (
                length(CAST(external_conversation_id AS BLOB)) BETWEEN 1 AND 1024),
            source_message_id TEXT NOT NULL CHECK (
                length(CAST(source_message_id AS BLOB)) BETWEEN 1 AND 1024),
            thread_id TEXT NOT NULL,
            turn_id TEXT NOT NULL,
            correlation_id TEXT NOT NULL CHECK (
                length(correlation_id) = 36 AND correlation_id = lower(correlation_id) AND
                correlation_id GLOB '????????-????-7???-[89ab]???-????????????' AND
                length(replace(correlation_id, '-', '')) = 32 AND
                replace(correlation_id, '-', '') NOT GLOB '*[^0-9a-f]*'),
            partition_sequence INTEGER NOT NULL CHECK (partition_sequence > 0),
            envelope_json TEXT NOT NULL CHECK (
                json_valid(envelope_json) AND
                length(CAST(envelope_json AS BLOB)) <= 262144),
            body_sha256 TEXT NOT NULL CHECK (
                length(body_sha256) = 64 AND body_sha256 = lower(body_sha256) AND
                body_sha256 NOT GLOB '*[^0-9a-f]*'),
            status TEXT NOT NULL CHECK (
                status IN ('pending', 'sending', 'sent', 'failed', 'deadLettered')),
            attempt_count INTEGER NOT NULL CHECK (attempt_count >= 0),
            next_attempt_utc INTEGER NOT NULL,
            lease_owner_instance_id TEXT NULL CHECK (
                lease_owner_instance_id IS NULL OR (
                    length(lease_owner_instance_id) = 36 AND
                    lease_owner_instance_id = lower(lease_owner_instance_id) AND
                    lease_owner_instance_id GLOB
                        '????????-????-7???-[89ab]???-????????????' AND
                    length(replace(lease_owner_instance_id, '-', '')) = 32 AND
                    replace(lease_owner_instance_id, '-', '')
                        NOT GLOB '*[^0-9a-f]*')),
            lease_expires_utc INTEGER NULL,
            error_code TEXT NULL,
            diagnostic TEXT NULL CHECK (
                diagnostic IS NULL OR length(CAST(diagnostic AS BLOB)) <= 4096),
            revision INTEGER NOT NULL CHECK (revision > 0),
            created_utc INTEGER NOT NULL,
            updated_utc INTEGER NOT NULL,
            sent_utc INTEGER NULL,
            FOREIGN KEY (channel_id) REFERENCES channels (channel_id) ON DELETE CASCADE,
            FOREIGN KEY (thread_id) REFERENCES threads (thread_id) ON DELETE RESTRICT,
            FOREIGN KEY (turn_id) REFERENCES turns (turn_id) ON DELETE RESTRICT,
            UNIQUE (channel_id, external_conversation_id, partition_sequence),
            CHECK ((lease_owner_instance_id IS NULL) = (lease_expires_utc IS NULL))
        );
        CREATE INDEX ix_channel_outbox_claim
            ON channel_outbox (
                status, next_attempt_utc, lease_expires_utc,
                channel_id, external_conversation_id, partition_sequence);

        CREATE TABLE workspace_heartbeat (
            id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
            runtime_instance_id TEXT NOT NULL CHECK (
                length(runtime_instance_id) = 36 AND
                runtime_instance_id = lower(runtime_instance_id) AND
                runtime_instance_id GLOB '????????-????-7???-[89ab]???-????????????' AND
                length(replace(runtime_instance_id, '-', '')) = 32 AND
                replace(runtime_instance_id, '-', '') NOT GLOB '*[^0-9a-f]*'),
            primary_host TEXT NOT NULL CHECK (primary_host IN ('app-server', 'gateway')),
            status TEXT NOT NULL CHECK (
                status IN ('healthy', 'degraded', 'unhealthy', 'stopping', 'stopped')),
            snapshot_json TEXT NOT NULL CHECK (
                json_valid(snapshot_json) AND
                length(CAST(snapshot_json AS BLOB)) <= 65536),
            observed_utc INTEGER NOT NULL,
            expires_utc INTEGER NOT NULL,
            stopped_utc INTEGER NULL,
            revision INTEGER NOT NULL CHECK (revision > 0)
        );
        CREATE INDEX ix_workspace_heartbeat_expiry
            ON workspace_heartbeat (expires_utc, status);

        CREATE TABLE trace_spans (
            trace_id TEXT NOT NULL CHECK (
                length(trace_id) = 32 AND trace_id = lower(trace_id) AND
                trace_id NOT GLOB '*[^0-9a-f]*'),
            span_id TEXT NOT NULL CHECK (
                length(span_id) = 16 AND span_id = lower(span_id) AND
                span_id NOT GLOB '*[^0-9a-f]*'),
            parent_span_id TEXT NULL CHECK (
                parent_span_id IS NULL OR (
                    length(parent_span_id) = 16 AND
                    parent_span_id = lower(parent_span_id) AND
                    parent_span_id NOT GLOB '*[^0-9a-f]*')),
            name TEXT NOT NULL CHECK (length(CAST(name AS BLOB)) BETWEEN 1 AND 256),
            kind TEXT NOT NULL CHECK (
                kind IN ('internal', 'server', 'client', 'producer', 'consumer')),
            status TEXT NOT NULL CHECK (status IN ('unset', 'ok', 'error')),
            correlation_id TEXT NULL CHECK (
                correlation_id IS NULL OR (
                    length(correlation_id) = 36 AND
                    correlation_id = lower(correlation_id) AND
                    correlation_id GLOB '????????-????-7???-[89ab]???-????????????' AND
                    length(replace(correlation_id, '-', '')) = 32 AND
                    replace(correlation_id, '-', '') NOT GLOB '*[^0-9a-f]*')),
            thread_id TEXT NULL,
            turn_id TEXT NULL,
            automation_run_id TEXT NULL,
            agent_run_id TEXT NULL,
            channel_id TEXT NULL,
            duration_ms REAL NOT NULL CHECK (duration_ms >= 0),
            tags_json TEXT NOT NULL CHECK (
                json_valid(tags_json) AND length(CAST(tags_json AS BLOB)) <= 16384),
            error_code TEXT NULL,
            started_utc INTEGER NOT NULL,
            ended_utc INTEGER NOT NULL,
            PRIMARY KEY (trace_id, span_id),
            FOREIGN KEY (thread_id) REFERENCES threads (thread_id) ON DELETE SET NULL,
            FOREIGN KEY (turn_id) REFERENCES turns (turn_id) ON DELETE SET NULL
        );
        CREATE INDEX ix_trace_spans_correlation
            ON trace_spans (correlation_id, started_utc DESC, trace_id, span_id);
        CREATE INDEX ix_trace_spans_started
            ON trace_spans (started_utc DESC, trace_id, span_id);

        CREATE TABLE insight_runs (
            insight_run_id TEXT NOT NULL PRIMARY KEY CHECK (
                length(insight_run_id) = 36 AND
                insight_run_id = lower(insight_run_id) AND
                insight_run_id GLOB '????????-????-7???-[89ab]???-????????????' AND
                length(replace(insight_run_id, '-', '')) = 32 AND
                replace(insight_run_id, '-', '') NOT GLOB '*[^0-9a-f]*'),
            trigger_kind TEXT NOT NULL CHECK (trigger_kind IN ('manual', 'scheduled')),
            status TEXT NOT NULL CHECK (status IN ('running', 'completed', 'failed')),
            high_watermark_utc INTEGER NOT NULL,
            diagnostic TEXT NULL CHECK (
                diagnostic IS NULL OR length(CAST(diagnostic AS BLOB)) <= 4096),
            revision INTEGER NOT NULL CHECK (revision > 0),
            created_utc INTEGER NOT NULL,
            updated_utc INTEGER NOT NULL,
            completed_utc INTEGER NULL
        );
        CREATE INDEX ix_insight_runs_status
            ON insight_runs (status, created_utc DESC, insight_run_id);

        CREATE TABLE improvement_proposals (
            proposal_id TEXT NOT NULL PRIMARY KEY CHECK (
                length(proposal_id) = 36 AND proposal_id = lower(proposal_id) AND
                proposal_id GLOB '????????-????-7???-[89ab]???-????????????' AND
                length(replace(proposal_id, '-', '')) = 32 AND
                replace(proposal_id, '-', '') NOT GLOB '*[^0-9a-f]*'),
            fingerprint_sha256 TEXT NOT NULL UNIQUE CHECK (
                length(fingerprint_sha256) = 64 AND
                fingerprint_sha256 = lower(fingerprint_sha256) AND
                fingerprint_sha256 NOT GLOB '*[^0-9a-f]*'),
            proposal_type TEXT NOT NULL CHECK (
                proposal_type IN (
                    'reliability', 'performance', 'configuration', 'maintenance')),
            severity TEXT NOT NULL CHECK (severity IN ('info', 'warning', 'critical')),
            summary TEXT NOT NULL CHECK (
                length(CAST(summary AS BLOB)) BETWEEN 1 AND 4096),
            evidence_json TEXT NOT NULL CHECK (
                json_valid(evidence_json) AND
                length(CAST(evidence_json AS BLOB)) <= 65536),
            status TEXT NOT NULL CHECK (
                status IN ('open', 'accepted', 'dismissed', 'archived')),
            revision INTEGER NOT NULL CHECK (revision > 0),
            created_utc INTEGER NOT NULL,
            updated_utc INTEGER NOT NULL,
            reviewed_utc INTEGER NULL
        );
        CREATE INDEX ix_improvement_proposals_status
            ON improvement_proposals (status, severity, updated_utc DESC, proposal_id);
        """;

    public int TargetVersion => 9;

    public async ValueTask ApplyAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, Sql, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO operations_state (
                id, workspace_id, current_revision, updated_utc)
            VALUES (1, $workspace_id, 0, $updated_utc);
            """;
        Add(command, "$workspace_id", Guid.CreateVersion7().ToString("D"));
        Add(command, "$updated_utc", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask ValidateAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        var tables = await ReadNamesAsync(connection, "table", Tables, cancellationToken);
        if (!tables.SequenceEqual(Tables, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Gateway state tables are incomplete.");
        }

        var indexes = await ReadNamesAsync(connection, "index", Indexes, cancellationToken);
        if (!indexes.SequenceEqual(Indexes, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Gateway state indexes are incomplete.");
        }

        foreach (var (table, expected) in Columns)
        {
            var actual = await ReadColumnsAsync(connection, table, cancellationToken);
            if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Gateway state table '{table}' has unexpected columns.");
            }
        }

        var workspaceId = await ScalarAsync<string>(
            connection,
            "SELECT workspace_id FROM operations_state WHERE id = 1;",
            cancellationToken);
        if (!Guid.TryParse(workspaceId, out var parsed) ||
            parsed.Version != 7 ||
            !string.Equals(parsed.ToString("D"), workspaceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Gateway Workspace ID is invalid.");
        }

        var turnColumns = await ReadColumnsAsync(connection, "turns", cancellationToken);
        if (!turnColumns.Contains("correlation_id", StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Turn correlation column is missing.");
        }

        var foreignKeys = await ReadForeignKeysAsync(connection, cancellationToken);
        string[] expectedForeignKeys =
        [
            "channel_inbound_messages:channel_id->channels.channel_id:cascade",
            "channel_inbound_messages:thread_id->threads.thread_id:restrict",
            "channel_inbound_messages:turn_id->turns.turn_id:restrict",
            "channel_media:inbound_message_id->channel_inbound_messages.inbound_message_id:cascade",
            "channel_outbox:channel_id->channels.channel_id:cascade",
            "channel_outbox:thread_id->threads.thread_id:restrict",
            "channel_outbox:turn_id->turns.turn_id:restrict",
            "channel_thread_mappings:channel_id->channels.channel_id:cascade",
            "channel_thread_mappings:thread_id->threads.thread_id:restrict",
            "trace_spans:thread_id->threads.thread_id:set null",
            "trace_spans:turn_id->turns.turn_id:set null",
        ];
        if (!foreignKeys.SequenceEqual(expectedForeignKeys, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Gateway state foreign keys are incomplete.");
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
            $"SELECT name FROM sqlite_schema WHERE type = '{type}' " +
            $"AND name IN ({string.Join(',', names.Select(name => $"'{name}'"))}) " +
            "ORDER BY name;";
        return await ReadStringsAsync(command, cancellationToken);
    }

    private static async ValueTask<string[]> ReadColumnsAsync(
        DbConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM pragma_table_info('{table}') ORDER BY cid;";
        return await ReadStringsAsync(command, cancellationToken);
    }

    private static async ValueTask<string[]> ReadForeignKeysAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT source || ':' || "from" || '->' || "table" || '.' ||
                   "to" || ':' || lower(on_delete)
            FROM (
                SELECT 'channel_thread_mappings' AS source, *
                FROM pragma_foreign_key_list('channel_thread_mappings')
                UNION ALL
                SELECT 'channel_inbound_messages' AS source, *
                FROM pragma_foreign_key_list('channel_inbound_messages')
                UNION ALL
                SELECT 'channel_media' AS source, *
                FROM pragma_foreign_key_list('channel_media')
                UNION ALL
                SELECT 'channel_outbox' AS source, *
                FROM pragma_foreign_key_list('channel_outbox')
                UNION ALL
                SELECT 'trace_spans' AS source, *
                FROM pragma_foreign_key_list('trace_spans'))
            ORDER BY 1;
            """;
        return await ReadStringsAsync(command, cancellationToken);
    }

    private static async ValueTask<T> ScalarAsync<T>(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("Gateway state singleton is missing.");
        return (T)Convert.ChangeType(
            value,
            typeof(T),
            System.Globalization.CultureInfo.InvariantCulture)!;
    }

    private static async ValueTask<string[]> ReadStringsAsync(
        DbCommand command,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var values = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(reader.GetString(0));
        }

        return values.ToArray();
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
