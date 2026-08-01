using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.State;

namespace OpenCoWork.Core.Operations;

internal sealed class WorkspaceInsightService(
    StateRuntime state,
    TimeProvider timeProvider,
    OperationsChangeHub? changes = null) : IWorkspaceInsightService
{
    private const int MaximumPageSize = 200;
    private const string RuleVersion = "m10-insight-v1";
    private static readonly TimeSpan Lookback = TimeSpan.FromHours(24);

    public Task<InsightRunSnapshot> RunAsync(
        InsightRunTrigger trigger,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            new InsightRunRequest(
                Guid.CreateVersion7(timeProvider.GetUtcNow()),
                trigger),
            cancellationToken);

    public async Task<InsightRunSnapshot> RunAsync(
        InsightRunRequest request,
        CancellationToken cancellationToken = default)
    {
        RequireVersionSeven(request.CommandId, nameof(request.CommandId));
        if (await ReadRunAsync(request.CommandId, cancellationToken) is { } replay)
        {
            return replay;
        }

        var trigger = request.Trigger;
        var now = timeProvider.GetUtcNow();
        var watermark = await ReadWatermarkAsync(cancellationToken);
        if (trigger == InsightRunTrigger.Scheduled &&
            await ReadLatestRunAsync(cancellationToken) is { } previous &&
            previous.Status == InsightRunStatus.Completed &&
            previous.HighWatermarkUtc >= watermark)
        {
            return previous;
        }

        var runId = request.CommandId;
        var inserted = await state.WriteAsync(
            async (connection, transaction, token) =>
            {
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText =
                    """
                    INSERT OR IGNORE INTO insight_runs (
                        insight_run_id, trigger_kind, status, high_watermark_utc,
                        diagnostic, revision, created_utc, updated_utc, completed_utc)
                    VALUES ($runId, $trigger, 'running', $watermark,
                            NULL, 1, $now, $now, NULL);
                    SELECT changes();
                    """;
                Add(insert, "$runId", runId.ToString("D"));
                Add(insert, "$trigger", trigger == InsightRunTrigger.Manual ? "manual" : "scheduled");
                Add(insert, "$watermark", watermark.ToUnixTimeMilliseconds());
                Add(insert, "$now", now.ToUnixTimeMilliseconds());
                if (Convert.ToInt32(
                        await insert.ExecuteScalarAsync(token),
                        System.Globalization.CultureInfo.InvariantCulture) == 0)
                {
                    return false;
                }

                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    UPDATE insight_runs
                    SET status = 'failed', diagnostic = 'insight.interrupted',
                        revision = revision + 1, updated_utc = $now,
                        completed_utc = $now
                    WHERE status = 'running' AND insight_run_id <> $runId;
                    """,
                    token,
                    ("$runId", runId.ToString("D")),
                    ("$now", now.ToUnixTimeMilliseconds()));
                return true;
            },
            cancellationToken);
        if (!inserted)
        {
            return await ReadRunAsync(runId, cancellationToken)
                   ?? throw new InvalidDataException("Insight run disappeared after replay.");
        }

        try
        {
            var proposalCount = await state.WriteAsync(
                async (connection, transaction, token) =>
                {
                    var signals = await ReadSignalsAsync(
                        connection,
                        transaction,
                        now,
                        token);
                    foreach (var signal in signals)
                    {
                        await UpsertProposalAsync(
                            connection,
                            transaction,
                            signal,
                            now,
                            token);
                    }

                    await ExecuteAsync(
                        connection,
                        transaction,
                        """
                        UPDATE insight_runs
                        SET status = 'completed', diagnostic = $diagnostic,
                            revision = revision + 1, updated_utc = $now,
                            completed_utc = $now
                        WHERE insight_run_id = $runId AND status = 'running';
                        UPDATE operations_state
                        SET current_revision = current_revision + 1, updated_utc = $now
                        WHERE id = 1;
                        """,
                        token,
                        ("$diagnostic", $"rules:{RuleVersion};proposals:{signals.Count}"),
                        ("$now", now.ToUnixTimeMilliseconds()),
                        ("$runId", runId.ToString("D")));
                    return signals.Count;
                },
                cancellationToken);
            var result = new InsightRunSnapshot(
                runId,
                trigger,
                InsightRunStatus.Completed,
                watermark,
                proposalCount,
                now,
                now,
                Revision: 2);
            changes?.Publish(
                OperationsChangeKind.Insight,
                "runCompleted",
                runId.ToString("D"));
            return result;
        }
        catch
        {
            await MarkFailedAsync(runId, now, CancellationToken.None);
            changes?.Publish(
                OperationsChangeKind.Insight,
                "runFailed",
                runId.ToString("D"));
            throw;
        }
    }

    public async Task<OperationsPage<ImprovementProposalSnapshot>> ListAsync(
        int pageSize = 100,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        if (pageSize is < 1 or > MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }
        var after = DecodeCursor(cursor);
        return await state.ReadAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT proposal_id, fingerprint_sha256, proposal_type, severity,
                           summary, evidence_json, status, revision, created_utc,
                           updated_utc, reviewed_utc
                    FROM improvement_proposals
                    WHERE ($updated IS NULL OR updated_utc < $updated OR
                           (updated_utc = $updated AND proposal_id < $proposalId))
                    ORDER BY updated_utc DESC, proposal_id DESC
                    LIMIT $limit;
                    """;
                Add(command, "$updated", after?.UpdatedUtc);
                Add(command, "$proposalId", after?.ProposalId);
                Add(command, "$limit", pageSize + 1);
                var items = new List<ImprovementProposalSnapshot>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    items.Add(ReadProposal(reader));
                }

                var hasMore = items.Count > pageSize;
                if (hasMore)
                {
                    items.RemoveAt(items.Count - 1);
                }
                var last = items.LastOrDefault();
                return new OperationsPage<ImprovementProposalSnapshot>(
                    items,
                    hasMore && last is not null
                        ? EncodeCursor(last.UpdatedAtUtc.ToUnixTimeMilliseconds(), last.ProposalId)
                        : null);
            },
            cancellationToken);
    }

    public Task<ImprovementProposalSnapshot?> GetAsync(
        Guid proposalId,
        CancellationToken cancellationToken = default)
    {
        RequireVersionSeven(proposalId, nameof(proposalId));
        return state.ReadAsync(
            (connection, token) => ReadProposalAsync(connection, proposalId, token),
            cancellationToken).AsTask();
    }

    public async Task<OperationsPage<InsightRunSnapshot>> ListRunsAsync(
        int pageSize = 100,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        if (pageSize is < 1 or > MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }
        var after = DecodeCursor(cursor);
        return await state.ReadAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT insight_run_id, trigger_kind, status, high_watermark_utc,
                           diagnostic, revision, created_utc, completed_utc
                    FROM insight_runs
                    WHERE ($created IS NULL OR created_utc < $created OR
                           (created_utc = $created AND insight_run_id < $runId))
                    ORDER BY created_utc DESC, insight_run_id DESC
                    LIMIT $limit;
                    """;
                Add(command, "$created", after?.UpdatedUtc);
                Add(command, "$runId", after?.ProposalId);
                Add(command, "$limit", pageSize + 1);
                var items = new List<InsightRunSnapshot>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    items.Add(ReadRun(reader));
                }

                var hasMore = items.Count > pageSize;
                if (hasMore)
                {
                    items.RemoveAt(items.Count - 1);
                }
                var last = items.LastOrDefault();
                return new OperationsPage<InsightRunSnapshot>(
                    items,
                    hasMore && last is not null
                        ? EncodeCursor(
                            last.CreatedAtUtc.ToUnixTimeMilliseconds(),
                            last.InsightRunId)
                        : null);
            },
            cancellationToken);
    }

    public async Task<ImprovementProposalSnapshot> ArchiveAsync(
        Guid proposalId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        RequireVersionSeven(proposalId, nameof(proposalId));
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        var now = timeProvider.GetUtcNow();
        var result = await state.WriteAsync(
            async (connection, transaction, token) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    UPDATE improvement_proposals
                    SET status = 'archived', revision = revision + 1,
                        updated_utc = $now, reviewed_utc = $now
                    WHERE proposal_id = $proposalId AND status = 'open'
                      AND revision = $expectedRevision;
                    """;
                Add(command, "$now", now.ToUnixTimeMilliseconds());
                Add(command, "$proposalId", proposalId.ToString("D"));
                Add(command, "$expectedRevision", expectedRevision);
                if (await command.ExecuteNonQueryAsync(token) != 1)
                {
                    var replay = await ReadProposalAsync(
                        connection,
                        transaction,
                        proposalId,
                        token);
                    if (replay is
                        {
                            Status: ImprovementProposalStatus.Archived,
                            Revision: var revision,
                        } && revision == expectedRevision + 1)
                    {
                        return (Snapshot: replay, Changed: false);
                    }
                    throw new OperationsServiceException(
                        replay is null
                            ? OperationsErrorCodes.InsightNotFound
                            : OperationsErrorCodes.InsightRevisionConflict,
                        replay is null
                            ? "Improvement Proposal was not found."
                            : "Improvement Proposal revision changed.",
                        currentRevision: replay?.Revision);
                }

                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    UPDATE operations_state
                    SET current_revision = current_revision + 1, updated_utc = $now
                    WHERE id = 1;
                    """,
                    token,
                    ("$now", now.ToUnixTimeMilliseconds()));
                return (
                    Snapshot: await ReadProposalAsync(
                        connection,
                        transaction,
                        proposalId,
                        token) ?? throw new InvalidDataException(
                            "Improvement Proposal disappeared after archive."),
                    Changed: true);
            },
            cancellationToken);
        if (result.Changed)
        {
            changes?.Publish(
                OperationsChangeKind.Insight,
                "proposalArchived",
                proposalId.ToString("D"));
        }
        return result.Snapshot;
    }

    private async Task<DateTimeOffset> ReadWatermarkAsync(
        CancellationToken cancellationToken) =>
        await state.ReadAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT max(value) FROM (
                        SELECT coalesce(max(updated_utc), 0) AS value FROM channel_inbound_messages
                        UNION ALL SELECT coalesce(max(updated_utc), 0) FROM channel_outbox
                        UNION ALL SELECT coalesce(max(ended_utc), 0) FROM trace_spans
                        UNION ALL SELECT coalesce(max(observed_utc), 0) FROM workspace_heartbeat
                        UNION ALL SELECT coalesce(max(created_utc), 0) FROM provider_usage);
                    """;
                return DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(
                    await command.ExecuteScalarAsync(token),
                    System.Globalization.CultureInfo.InvariantCulture));
            },
            cancellationToken);

    private async Task<InsightRunSnapshot?> ReadLatestRunAsync(
        CancellationToken cancellationToken) =>
        await state.ReadAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT insight_run_id, trigger_kind, status, high_watermark_utc,
                           diagnostic, revision, created_utc, completed_utc
                    FROM insight_runs
                    ORDER BY created_utc DESC, insight_run_id DESC
                    LIMIT 1;
                    """;
                await using var reader = await command.ExecuteReaderAsync(token);
                return await reader.ReadAsync(token) ? ReadRun(reader) : null;
            },
            cancellationToken);

    private async Task<InsightRunSnapshot?> ReadRunAsync(
        Guid runId,
        CancellationToken cancellationToken) =>
        await state.ReadAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT insight_run_id, trigger_kind, status, high_watermark_utc,
                           diagnostic, revision, created_utc, completed_utc
                    FROM insight_runs WHERE insight_run_id = $runId;
                    """;
                Add(command, "$runId", runId.ToString("D"));
                await using var reader = await command.ExecuteReaderAsync(token);
                return await reader.ReadAsync(token) ? ReadRun(reader) : null;
            },
            cancellationToken);

    private static async Task<IReadOnlyList<InsightSignal>> ReadSignalsAsync(
        DbConnection connection,
        DbTransaction transaction,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var signals = new List<InsightSignal>();
        var since = now.Subtract(Lookback).ToUnixTimeMilliseconds();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT channel_id, error_code, count(*) FROM (
                    SELECT channel_id, error_code FROM channel_inbound_messages
                    WHERE status = 'deadLettered' AND updated_utc >= $since
                    UNION ALL
                    SELECT channel_id, error_code FROM channel_outbox
                    WHERE status = 'deadLettered' AND updated_utc >= $since)
                WHERE error_code IS NOT NULL
                GROUP BY channel_id, error_code
                HAVING count(*) >= 2;
                """;
            Add(command, "$since", since);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                signals.Add(Signal(
                    "deadLetter",
                    $"{reader.GetString(0)}|{reader.GetString(1)}",
                    ImprovementProposalType.Reliability,
                    ImprovementProposalSeverity.Warning,
                    "Repeated channel dead letters need review.",
                    new
                    {
                        rule = "deadLetter",
                        channelId = reader.GetString(0),
                        errorCode = reader.GetString(1),
                        count = reader.GetInt32(2),
                    }));
            }
        }

        await AddCountSignalsAsync(
            connection,
            transaction,
            """
            SELECT error_code, count(*) FROM trace_spans
            WHERE status = 'error' AND error_code IS NOT NULL AND started_utc >= $since
            GROUP BY error_code HAVING count(*) >= 3;
            """,
            since,
            (reader, count) => Signal(
                "traceError",
                reader.GetString(0),
                ImprovementProposalType.Reliability,
                ImprovementProposalSeverity.Warning,
                "Repeated traced errors need review.",
                new { rule = "traceError", errorCode = reader.GetString(0), count }),
            signals,
            cancellationToken);

        await AddCountSignalsAsync(
            connection,
            transaction,
            """
            SELECT channel_id, count(*) FROM channel_outbox
            WHERE status IN ('pending', 'failed') AND created_utc <= $since
            GROUP BY channel_id HAVING count(*) >= 10;
            """,
            now.AddMinutes(-10).ToUnixTimeMilliseconds(),
            (reader, count) => Signal(
                "outboxBacklog",
                reader.GetString(0),
                ImprovementProposalType.Performance,
                ImprovementProposalSeverity.Warning,
                "Channel outbox backlog needs review.",
                new { rule = "outboxBacklog", channelId = reader.GetString(0), count }),
            signals,
            cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT status, json_extract(snapshot_json, '$.traceDroppedCount')
                FROM workspace_heartbeat
                WHERE id = 1 AND (status IN ('degraded', 'unhealthy') OR
                                  json_extract(snapshot_json, '$.traceDroppedCount') > 0);
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var dropped = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                signals.Add(Signal(
                    "heartbeat",
                    reader.GetString(0),
                    ImprovementProposalType.Configuration,
                    ImprovementProposalSeverity.Warning,
                    "Workspace control-plane health needs review.",
                    new { rule = "heartbeat", status = reader.GetString(0), traceDropped = dropped }));
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT coalesce(sum(json_extract(usage_json, '$.totalTokens')), 0)
                FROM provider_usage WHERE created_utc >= $since;
                """;
            Add(command, "$since", now.AddHours(-1).ToUnixTimeMilliseconds());
            var tokens = Convert.ToInt64(
                await command.ExecuteScalarAsync(cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
            if (tokens >= 100_000)
            {
                signals.Add(Signal(
                    "usageConcentration",
                    "workspace",
                    ImprovementProposalType.Performance,
                    ImprovementProposalSeverity.Info,
                    "Provider usage is concentrated in the recent window.",
                    new { rule = "usageConcentration", totalTokens = tokens, windowMinutes = 60 }));
            }
        }

        return signals;
    }

    private static async Task AddCountSignalsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        long since,
        Func<DbDataReader, int, InsightSignal> create,
        List<InsightSignal> target,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        Add(command, "$since", since);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            target.Add(create(reader, reader.GetInt32(1)));
        }
    }

    private static InsightSignal Signal(
        string rule,
        string key,
        ImprovementProposalType type,
        ImprovementProposalSeverity severity,
        string summary,
        object evidence)
    {
        var fingerprint = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes($"{RuleVersion}|{rule}|{key}")))
            .ToLowerInvariant();
        return new InsightSignal(
            fingerprint,
            type,
            severity,
            summary,
            JsonSerializer.Serialize(evidence));
    }

    private static async Task UpsertProposalAsync(
        DbConnection connection,
        DbTransaction transaction,
        InsightSignal signal,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO improvement_proposals (
                proposal_id, fingerprint_sha256, proposal_type, severity,
                summary, evidence_json, status, revision, created_utc,
                updated_utc, reviewed_utc)
            VALUES ($proposalId, $fingerprint, $type, $severity,
                    $summary, $evidence, 'open', 1, $now, $now, NULL)
            ON CONFLICT (fingerprint_sha256) DO UPDATE SET
                severity = excluded.severity,
                summary = excluded.summary,
                evidence_json = excluded.evidence_json,
                revision = improvement_proposals.revision + 1,
                updated_utc = excluded.updated_utc
            WHERE improvement_proposals.status = 'open';
            """;
        Add(command, "$proposalId", Guid.CreateVersion7(now).ToString("D"));
        Add(command, "$fingerprint", signal.Fingerprint);
        Add(command, "$type", Wire(signal.Type));
        Add(command, "$severity", Wire(signal.Severity));
        Add(command, "$summary", signal.Summary);
        Add(command, "$evidence", signal.EvidenceJson);
        Add(command, "$now", now.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task MarkFailedAsync(
        Guid runId,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        _ = await state.WriteAsync(
            async (connection, transaction, token) =>
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    UPDATE insight_runs
                    SET status = 'failed', diagnostic = 'insight.failed',
                        revision = revision + 1, updated_utc = $now,
                        completed_utc = $now
                    WHERE insight_run_id = $runId AND status = 'running';
                    """,
                    token,
                    ("$now", now.ToUnixTimeMilliseconds()),
                    ("$runId", runId.ToString("D")));
                return true;
            },
            cancellationToken);

    private static async ValueTask<ImprovementProposalSnapshot?> ReadProposalAsync(
        DbConnection connection,
        Guid proposalId,
        CancellationToken cancellationToken) =>
        await ReadProposalAsync(connection, null, proposalId, cancellationToken);

    private static async ValueTask<ImprovementProposalSnapshot?> ReadProposalAsync(
        DbConnection connection,
        DbTransaction? transaction,
        Guid proposalId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT proposal_id, fingerprint_sha256, proposal_type, severity,
                   summary, evidence_json, status, revision, created_utc,
                   updated_utc, reviewed_utc
            FROM improvement_proposals WHERE proposal_id = $proposalId;
            """;
        Add(command, "$proposalId", proposalId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProposal(reader) : null;
    }

    private static ImprovementProposalSnapshot ReadProposal(DbDataReader reader)
    {
        using var evidence = JsonDocument.Parse(reader.GetString(5));
        return new ImprovementProposalSnapshot(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            ParseType(reader.GetString(2)),
            ParseSeverity(reader.GetString(3)),
            reader.GetString(4),
            evidence.RootElement.Clone(),
            reader.GetString(6) switch
            {
                "open" => ImprovementProposalStatus.Open,
                "archived" => ImprovementProposalStatus.Archived,
                _ => throw new InvalidDataException("Improvement Proposal status is invalid."),
            },
            reader.GetInt64(7),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8)),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(9)),
            reader.IsDBNull(10)
                ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(10)));
    }

    private static InsightRunSnapshot ReadRun(DbDataReader reader)
    {
        var diagnostic = reader.IsDBNull(4) ? null : reader.GetString(4);
        var countPrefix = $"rules:{RuleVersion};proposals:";
        var count = diagnostic?.StartsWith(countPrefix, StringComparison.Ordinal) == true &&
                    int.TryParse(diagnostic.AsSpan(countPrefix.Length), out var parsed)
            ? parsed
            : 0;
        return new InsightRunSnapshot(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1) == "manual" ? InsightRunTrigger.Manual : InsightRunTrigger.Scheduled,
            reader.GetString(2) switch
            {
                "running" => InsightRunStatus.Running,
                "completed" => InsightRunStatus.Completed,
                "failed" => InsightRunStatus.Failed,
                _ => throw new InvalidDataException("Insight Run status is invalid."),
            },
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
            count,
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)),
            reader.IsDBNull(7)
                ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)),
            reader.GetInt64(5));
    }

    private static Cursor? DecodeCursor(string? cursor)
    {
        if (cursor is null)
        {
            return null;
        }
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var parts = decoded.Split('|');
            return parts.Length == 2 &&
                   long.TryParse(parts[0], out var updated) &&
                   Guid.TryParse(parts[1], out var proposalId) &&
                   proposalId.Version == 7
                ? new Cursor(updated, proposalId.ToString("D"))
                : throw new ArgumentException("Insight cursor is invalid.", nameof(cursor));
        }
        catch (FormatException)
        {
            throw new ArgumentException("Insight cursor is invalid.", nameof(cursor));
        }
    }

    private static string EncodeCursor(long updated, Guid proposalId) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{updated}|{proposalId:D}"));

    private static string Wire(ImprovementProposalType type) =>
        type.ToString().ToLowerInvariant();

    private static string Wire(ImprovementProposalSeverity severity) =>
        severity.ToString().ToLowerInvariant();

    private static ImprovementProposalType ParseType(string value) =>
        Enum.Parse<ImprovementProposalType>(value, ignoreCase: true);

    private static ImprovementProposalSeverity ParseSeverity(string value) =>
        Enum.Parse<ImprovementProposalSeverity>(value, ignoreCase: true);

    private static void RequireVersionSeven(Guid value, string parameterName)
    {
        if (value.Version != 7)
        {
            throw new ArgumentException("ID must be a UUIDv7.", parameterName);
        }
    }

    private static async Task ExecuteAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            Add(command, name, value);
        }
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private sealed record InsightSignal(
        string Fingerprint,
        ImprovementProposalType Type,
        ImprovementProposalSeverity Severity,
        string Summary,
        string EvidenceJson);

    private sealed record Cursor(long UpdatedUtc, string ProposalId);
}
