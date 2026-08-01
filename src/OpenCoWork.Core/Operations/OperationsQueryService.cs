using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.State;

namespace OpenCoWork.Core.Operations;

internal sealed class OperationsQueryService : IOperationsQueryService
{
    private const int MaximumPageSize = 200;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    private readonly StateRuntime _state;
    private readonly TimeProvider _timeProvider;

    public OperationsQueryService(
        StateRuntime state,
        TimeProvider? timeProvider = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<UsageAggregate>> QueryUsageAsync(
        UsageQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateRange(query.FromUtc, query.ToUtc);
        var bucketMilliseconds = query.Bucket switch
        {
            OperationsTimeBucket.Hour => 3_600_000L,
            OperationsTimeBucket.Day => 86_400_000L,
            _ => throw new ArgumentOutOfRangeException(nameof(query)),
        };

        return await _state.ReadAsync<IReadOnlyList<UsageAggregate>>(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    WITH usage_rows AS (
                        SELECT
                            (u.created_utc / $bucketMs) * $bucketMs AS bucket_utc,
                            json_extract(i.snapshot_json, '$.providerId') AS provider_id,
                            json_extract(i.snapshot_json, '$.modelId') AS model_id,
                            u.purpose,
                            u.thread_id,
                            (SELECT c.channel_id
                             FROM channel_inbound_messages c
                             WHERE c.turn_id = u.turn_id
                             ORDER BY c.created_utc
                             LIMIT 1) AS channel_id,
                            json_extract(u.usage_json, '$.source') AS source,
                            json_extract(u.usage_json, '$.promptTokens') AS prompt_tokens,
                            json_extract(u.usage_json, '$.cachedPromptTokens') AS cached_prompt_tokens,
                            json_extract(u.usage_json, '$.completionTokens') AS completion_tokens,
                            json_extract(u.usage_json, '$.reasoningCompletionTokens') AS reasoning_tokens,
                            json_extract(u.usage_json, '$.totalTokens') AS total_tokens
                        FROM provider_usage u
                        JOIN agent_invocations i ON i.invocation_id = u.invocation_id
                        WHERE u.created_utc >= $from AND u.created_utc < $to
                    )
                    SELECT bucket_utc, provider_id, model_id, purpose, thread_id,
                           channel_id, source,
                           sum(prompt_tokens), sum(cached_prompt_tokens),
                           sum(completion_tokens), sum(reasoning_tokens), sum(total_tokens)
                    FROM usage_rows
                    WHERE ($providerId IS NULL OR provider_id = $providerId)
                      AND ($modelId IS NULL OR model_id = $modelId)
                      AND ($channelId IS NULL OR channel_id = $channelId)
                      AND ($purpose IS NULL OR purpose = $purpose)
                      AND ($threadId IS NULL OR thread_id = $threadId)
                    GROUP BY bucket_utc, provider_id, model_id, purpose, thread_id,
                             channel_id, source
                    ORDER BY bucket_utc, provider_id, model_id, purpose, thread_id,
                             channel_id, source;
                    """;
                Add(command, "$bucketMs", bucketMilliseconds);
                Add(command, "$from", query.FromUtc.ToUnixTimeMilliseconds());
                Add(command, "$to", query.ToUtc.ToUnixTimeMilliseconds());
                Add(command, "$providerId", query.ProviderId);
                Add(command, "$modelId", query.ModelId);
                Add(command, "$channelId", query.ChannelId);
                Add(command, "$purpose", query.Purpose?.ToString().ToLowerInvariant());
                Add(command, "$threadId", query.ThreadId?.ToString("D"));
                var items = new List<UsageAggregate>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    items.Add(new UsageAggregate(
                        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
                        reader.GetString(1),
                        reader.GetString(2),
                        Enum.Parse<ProviderInvocationPurpose>(reader.GetString(3), true),
                        Guid.Parse(reader.GetString(4)),
                        reader.IsDBNull(5) ? null : reader.GetString(5),
                        Enum.Parse<ProviderUsageSource>(reader.GetString(6), true),
                        reader.GetInt64(7),
                        reader.GetInt64(8),
                        reader.GetInt64(9),
                        reader.GetInt64(10),
                        reader.GetInt64(11)));
                }
                return items;
            },
            cancellationToken);
    }

    public async Task<OperationsPage<TraceSummary>> ListTracesAsync(
        TraceListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.PageSize is < 1 or > MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(query));
        }
        if (query.FromUtc is not null && query.ToUtc is not null)
        {
            ValidateRange(query.FromUtc.Value, query.ToUtc.Value);
        }

        var shape = CursorShape(query);
        var cursor = DecodeCursor(query.Cursor, shape);
        return await _state.ReadAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    WITH traces AS (
                        SELECT trace_id, max(correlation_id) AS correlation_id,
                               min(started_utc) AS started_utc,
                               max(ended_utc) AS ended_utc,
                               count(*) AS span_count,
                               max(CASE WHEN status = 'error' THEN 1 ELSE 0 END) AS has_error
                        FROM trace_spans
                        WHERE ($correlationId IS NULL OR correlation_id = $correlationId)
                          AND ($from IS NULL OR started_utc >= $from)
                          AND ($to IS NULL OR started_utc < $to)
                        GROUP BY trace_id
                    )
                    SELECT trace_id, correlation_id, started_utc, ended_utc,
                           span_count, has_error
                    FROM traces
                    WHERE ($cursorStarted IS NULL OR started_utc < $cursorStarted OR
                           (started_utc = $cursorStarted AND trace_id < $cursorTraceId))
                    ORDER BY started_utc DESC, trace_id DESC
                    LIMIT $limit;
                    """;
                Add(command, "$correlationId", query.CorrelationId?.ToString("D"));
                Add(command, "$from", query.FromUtc?.ToUnixTimeMilliseconds());
                Add(command, "$to", query.ToUtc?.ToUnixTimeMilliseconds());
                Add(command, "$cursorStarted", cursor?.StartedUtc);
                Add(command, "$cursorTraceId", cursor?.TraceId);
                Add(command, "$limit", query.PageSize + 1);
                var items = new List<TraceSummary>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    var started = reader.GetInt64(2);
                    var ended = reader.GetInt64(3);
                    items.Add(new TraceSummary(
                        reader.GetString(0),
                        ReadGuid(reader, 1),
                        DateTimeOffset.FromUnixTimeMilliseconds(started),
                        DateTimeOffset.FromUnixTimeMilliseconds(ended),
                        Math.Max(0, ended - started),
                        reader.GetInt32(4),
                        reader.GetInt32(5) != 0));
                }

                var hasMore = items.Count > query.PageSize;
                if (hasMore)
                {
                    items.RemoveAt(items.Count - 1);
                }
                var last = items.LastOrDefault();
                return new OperationsPage<TraceSummary>(
                    items,
                    hasMore && last is not null
                        ? EncodeCursor(last.StartedAtUtc.ToUnixTimeMilliseconds(), last.TraceId, shape)
                        : null);
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<TraceSpanSnapshot>> GetTraceAsync(
        string traceId,
        CancellationToken cancellationToken = default)
    {
        if (!IsTraceId(traceId))
        {
            throw new ArgumentException("Trace id is invalid.", nameof(traceId));
        }

        return await _state.ReadAsync<IReadOnlyList<TraceSpanSnapshot>>(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT trace_id, span_id, parent_span_id, name, kind, status,
                           correlation_id, thread_id, turn_id, automation_run_id,
                           agent_run_id, channel_id, duration_ms, tags_json, error_code,
                           started_utc, ended_utc
                    FROM trace_spans
                    WHERE trace_id = $traceId
                    ORDER BY started_utc, span_id;
                    """;
                Add(command, "$traceId", traceId);
                var items = new List<TraceSpanSnapshot>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    items.Add(new TraceSpanSnapshot(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetString(5),
                        ReadGuid(reader, 6),
                        ReadGuid(reader, 7),
                        ReadGuid(reader, 8),
                        ReadGuid(reader, 9),
                        ReadGuid(reader, 10),
                        reader.IsDBNull(11) ? null : reader.GetString(11),
                        reader.GetDouble(12),
                        JsonSerializer.Deserialize<Dictionary<string, string>>(
                            reader.GetString(13)) ?? [],
                        reader.IsDBNull(14) ? null : reader.GetString(14),
                        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(15)),
                        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(16))));
                }
                return items;
            },
            cancellationToken);
    }

    public Task<OperationsHeartbeatSnapshot?> GetHeartbeatAsync(
        CancellationToken cancellationToken = default) =>
        _state.ReadAsync(
            (connection, token) => ReadHeartbeatAsync(
                connection,
                _timeProvider.GetUtcNow(),
                token),
            cancellationToken).AsTask();

    public Task<OperationsDashboardSnapshot> GetDashboardAsync(
        CancellationToken cancellationToken = default) =>
        _state.ReadAsync(
            (connection, token) => ReadDashboardAsync(
                connection,
                _timeProvider.GetUtcNow(),
                token),
            cancellationToken).AsTask();

    internal static async ValueTask<OperationsHeartbeatSnapshot?> ReadHeartbeatAsync(
        DbConnection connection,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT runtime_instance_id, primary_host, status, snapshot_json,
                   observed_utc, expires_utc, stopped_utc, revision
            FROM workspace_heartbeat
            WHERE id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var payload = JsonSerializer.Deserialize<OperationsHeartbeatPayload>(
                          reader.GetString(3),
                          JsonOptions)
                      ?? throw new InvalidDataException(
                          "Workspace Heartbeat snapshot is invalid.");
        var status = ParseHealthStatus(reader.GetString(2));
        var expires = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5));
        if (status is not OperationsHealthStatus.Stopped && now > expires)
        {
            status = OperationsHealthStatus.Stale;
        }

        return new OperationsHeartbeatSnapshot(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            status,
            payload.RuntimeStatus,
            payload.Modules,
            payload.ReadyChannels,
            payload.FaultedChannels,
            payload.PendingInbound,
            payload.FailedInbound,
            payload.DeadLetterInbound,
            payload.PendingOutbox,
            payload.FailedOutbox,
            payload.DeadLetterOutbox,
            payload.TraceDroppedCount,
            payload.SqliteWritable,
            payload.ReconcilerLastSuccessAtUtc,
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
            expires,
            reader.IsDBNull(6)
                ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)),
            reader.GetInt64(7));
    }

    internal static async ValueTask<OperationsDashboardSnapshot> ReadDashboardAsync(
        DbConnection connection,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var heartbeat = await ReadHeartbeatAsync(connection, now, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                (SELECT workspace_id FROM operations_state WHERE id = 1),
                (SELECT count(*) FROM channels WHERE runtime_status = 'ready'),
                (SELECT count(*) FROM channels WHERE runtime_status = 'faulted'),
                (SELECT count(*) FROM channel_inbound_messages WHERE status = 'pending'),
                (SELECT count(*) FROM channel_inbound_messages WHERE status = 'failed'),
                (SELECT count(*) FROM channel_inbound_messages WHERE status = 'deadLettered'),
                (SELECT count(*) FROM channel_outbox WHERE status = 'pending'),
                (SELECT count(*) FROM channel_outbox WHERE status = 'failed'),
                (SELECT count(*) FROM channel_outbox WHERE status = 'deadLettered'),
                (SELECT coalesce(sum(json_extract(usage_json, '$.totalTokens')), 0)
                 FROM provider_usage WHERE created_utc >= $since),
                (SELECT count(*) FROM trace_spans
                 WHERE status = 'error' AND started_utc >= $since),
                (SELECT count(*) FROM improvement_proposals WHERE status = 'open');
            """;
        Add(command, "$since", now.AddHours(-24).ToUnixTimeMilliseconds());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException("Operations Dashboard is unavailable.");
        }

        return new OperationsDashboardSnapshot(
            Guid.Parse(reader.GetString(0)),
            heartbeat,
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.GetInt64(9),
            reader.GetInt32(10),
            reader.GetInt32(11),
            now);
    }

    private static OperationsHealthStatus ParseHealthStatus(string value) => value switch
    {
        "healthy" => OperationsHealthStatus.Healthy,
        "degraded" => OperationsHealthStatus.Degraded,
        "unhealthy" => OperationsHealthStatus.Unhealthy,
        "stopping" => OperationsHealthStatus.Stopping,
        "stopped" => OperationsHealthStatus.Stopped,
        _ => throw new InvalidDataException("Workspace Heartbeat status is invalid."),
    };

    private static void ValidateRange(DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        if (fromUtc.Offset != TimeSpan.Zero || toUtc.Offset != TimeSpan.Zero || fromUtc >= toUtc)
        {
            throw new ArgumentException("Operations query range must be an increasing UTC range.");
        }
    }

    private static string CursorShape(TraceListQuery query)
    {
        var text = string.Join('|',
            query.CorrelationId?.ToString("D"),
            query.FromUtc?.ToUnixTimeMilliseconds(),
            query.ToUtc?.ToUnixTimeMilliseconds());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))
            .ToLowerInvariant();
    }

    private static CursorPayload? DecodeCursor(string? encoded, string shape)
    {
        if (encoded is null)
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(encoded.Replace('-', '+').Replace('_', '/') +
                new string('=', (4 - encoded.Length % 4) % 4));
            var cursor = JsonSerializer.Deserialize<CursorPayload>(bytes);
            return cursor is not null &&
                   IsTraceId(cursor.TraceId) &&
                   string.Equals(cursor.Shape, shape, StringComparison.Ordinal)
                ? cursor
                : throw new ArgumentException("Trace cursor is invalid.", nameof(encoded));
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new ArgumentException("Trace cursor is invalid.", nameof(encoded));
        }
    }

    private static string EncodeCursor(long startedUtc, string traceId, string shape) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
                new CursorPayload(startedUtc, traceId, shape)))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static bool IsTraceId(string? value) =>
        value is { Length: 32 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static Guid? ReadGuid(DbDataReader reader, int ordinal) =>
        !reader.IsDBNull(ordinal) && Guid.TryParse(reader.GetString(ordinal), out var value)
            ? value
            : null;

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private sealed record CursorPayload(long StartedUtc, string TraceId, string Shape);
}

internal sealed record OperationsHeartbeatPayload(
    string RuntimeStatus,
    IReadOnlyList<OperationsModuleHealth> Modules,
    int ReadyChannels,
    int FaultedChannels,
    int PendingInbound,
    int FailedInbound,
    int DeadLetterInbound,
    int PendingOutbox,
    int FailedOutbox,
    int DeadLetterOutbox,
    long TraceDroppedCount,
    bool SqliteWritable,
    DateTimeOffset? ReconcilerLastSuccessAtUtc);
