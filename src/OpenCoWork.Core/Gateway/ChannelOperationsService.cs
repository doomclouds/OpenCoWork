using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Operations;

namespace OpenCoWork.Core.Gateway;

internal sealed class ChannelOperationsService(
    IWorkspaceStateStore state,
    GatewayMediaStore media,
    GatewayReconciler reconciler,
    OperationsChangeHub? changes = null) : IChannelService
{
    private const int MaximumPageSize = 200;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<ChannelPage<ChannelSnapshot>> ListChannelsAsync(
        ChannelListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidatePageSize(query.PageSize);
        var shape = Shape("channels", Wire(query.Status));
        var after = DecodeCursor(query.Cursor, shape);
        return state.ReadAsync(
            async (connection, token) =>
            {
                var revision = await ReadRevisionAsync(connection, token);
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT channel_id, kind, enabled, definition_sha256, trust_status,
                           runtime_status, diagnostic, revision, created_utc, updated_utc
                    FROM channels
                    WHERE ($status IS NULL OR runtime_status = $status)
                      AND ($updated IS NULL OR updated_utc < $updated OR
                           (updated_utc = $updated AND channel_id < $id))
                    ORDER BY updated_utc DESC, channel_id DESC LIMIT $limit;
                    """;
                Add(command, "$status", Wire(query.Status));
                Add(command, "$updated", after?.UpdatedUtc);
                Add(command, "$id", after?.Id);
                Add(command, "$limit", query.PageSize + 1);
                var items = new List<ChannelSnapshot>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    items.Add(ReadChannel(reader));
                }
                return Page(revision, items, query.PageSize, shape,
                    item => (item.UpdatedAtUtc, item.ChannelId));
            },
            cancellationToken).AsTask();
    }

    public Task<ChannelSnapshot?> GetChannelAsync(
        string channelId,
        CancellationToken cancellationToken = default)
    {
        ValidateChannelId(channelId);
        return state.ReadAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT channel_id, kind, enabled, definition_sha256, trust_status,
                           runtime_status, diagnostic, revision, created_utc, updated_utc
                    FROM channels WHERE channel_id = $channelId;
                    """;
                Add(command, "$channelId", channelId);
                await using var reader = await command.ExecuteReaderAsync(token);
                return await reader.ReadAsync(token) ? ReadChannel(reader) : null;
            },
            cancellationToken).AsTask();
    }

    public Task<ChannelPage<ChannelInboundSummary>> ListInboundAsync(
        ChannelInboundQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidatePageSize(query.PageSize);
        if (query.ChannelId is not null)
        {
            ValidateChannelId(query.ChannelId);
        }
        var shape = Shape("inbound", query.ChannelId, Wire(query.Status));
        var after = DecodeCursor(query.Cursor, shape);
        return state.ReadAsync(
            async (connection, token) =>
            {
                var revision = await ReadRevisionAsync(connection, token);
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT inbound_message_id, channel_id, external_message_id,
                           external_conversation_id, partition_sequence, body_sha256,
                           correlation_id, thread_id, turn_id, status, attempt_count,
                           error_code, revision, created_utc, updated_utc, delivered_utc
                    FROM channel_inbound_messages
                    WHERE ($channelId IS NULL OR channel_id = $channelId)
                      AND ($status IS NULL OR status = $status)
                      AND ($updated IS NULL OR updated_utc < $updated OR
                           (updated_utc = $updated AND inbound_message_id < $id))
                    ORDER BY updated_utc DESC, inbound_message_id DESC LIMIT $limit;
                    """;
                Add(command, "$channelId", query.ChannelId);
                Add(command, "$status", Wire(query.Status));
                Add(command, "$updated", after?.UpdatedUtc);
                Add(command, "$id", after?.Id);
                Add(command, "$limit", query.PageSize + 1);
                var items = new List<ChannelInboundSummary>();
                await using (var reader = await command.ExecuteReaderAsync(token))
                {
                    while (await reader.ReadAsync(token))
                    {
                        items.Add(ReadInbound(reader));
                    }
                }
                var mediaByInbound = await ReadMediaAsync(
                    connection,
                    items.Select(item => item.InboundMessageId).ToArray(),
                    token);
                for (var index = 0; index < items.Count; index++)
                {
                    items[index] = items[index] with
                    {
                        Media = mediaByInbound.GetValueOrDefault(
                            items[index].InboundMessageId,
                            []),
                    };
                }
                return Page(revision, items, query.PageSize, shape,
                    item => (item.UpdatedAtUtc, item.InboundMessageId.ToString("D")));
            },
            cancellationToken).AsTask();
    }

    public Task<ChannelPage<ChannelOutboxSummary>> ListOutboxAsync(
        ChannelOutboxQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidatePageSize(query.PageSize);
        if (query.ChannelId is not null)
        {
            ValidateChannelId(query.ChannelId);
        }
        var shape = Shape("outbox", query.ChannelId, Wire(query.Status));
        var after = DecodeCursor(query.Cursor, shape);
        return state.ReadAsync(
            async (connection, token) =>
            {
                var revision = await ReadRevisionAsync(connection, token);
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT outbox_message_id, delivery_id, channel_id,
                           external_conversation_id, source_message_id, thread_id,
                           turn_id, correlation_id, partition_sequence, body_sha256,
                           status, attempt_count, error_code, revision, created_utc,
                           updated_utc, sent_utc
                    FROM channel_outbox
                    WHERE ($channelId IS NULL OR channel_id = $channelId)
                      AND ($status IS NULL OR status = $status)
                      AND ($updated IS NULL OR updated_utc < $updated OR
                           (updated_utc = $updated AND outbox_message_id < $id))
                    ORDER BY updated_utc DESC, outbox_message_id DESC LIMIT $limit;
                    """;
                Add(command, "$channelId", query.ChannelId);
                Add(command, "$status", Wire(query.Status));
                Add(command, "$updated", after?.UpdatedUtc);
                Add(command, "$id", after?.Id);
                Add(command, "$limit", query.PageSize + 1);
                var items = new List<ChannelOutboxSummary>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    items.Add(ReadOutbox(reader));
                }
                return Page(revision, items, query.PageSize, shape,
                    item => (item.UpdatedAtUtc, item.OutboxMessageId.ToString("D")));
            },
            cancellationToken).AsTask();
    }

    public Task<ChannelMediaChunk> ReadMediaAsync(
        ChannelMediaReadRequest request,
        CancellationToken cancellationToken = default) =>
        media.ReadAsync(request, cancellationToken);

    public async Task<ChannelOutboxSummary> RetryDeadLetterAsync(
        ChannelDeadLetterRetryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = await reconciler.RetryOutboxAsync(
            request.OutboxMessageId,
            request.ExpectedRevision,
            request.IdempotencyKey,
            cancellationToken);
        reconciler.Wake();
        var result = await ReadOutboxAsync(request.OutboxMessageId, cancellationToken)
                     ?? throw new ChannelServiceException(
                         ChannelErrorCodes.NotFound,
                         "Outbox message was not found.");
        changes?.Publish(
            OperationsChangeKind.Channel,
            "deadLetterRetried",
            request.OutboxMessageId.ToString("D"));
        return result;
    }

    private Task<ChannelOutboxSummary?> ReadOutboxAsync(
        Guid outboxMessageId,
        CancellationToken cancellationToken) =>
        state.ReadAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT outbox_message_id, delivery_id, channel_id,
                           external_conversation_id, source_message_id, thread_id,
                           turn_id, correlation_id, partition_sequence, body_sha256,
                           status, attempt_count, error_code, revision, created_utc,
                           updated_utc, sent_utc
                    FROM channel_outbox WHERE outbox_message_id = $id;
                    """;
                Add(command, "$id", outboxMessageId.ToString("D"));
                await using var reader = await command.ExecuteReaderAsync(token);
                return await reader.ReadAsync(token) ? ReadOutbox(reader) : null;
            },
            cancellationToken).AsTask();

    private static async Task<Dictionary<Guid, IReadOnlyList<ChannelMediaSummary>>>
        ReadMediaAsync(
            DbConnection connection,
            IReadOnlyList<Guid> inboundIds,
            CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, IReadOnlyList<ChannelMediaSummary>>();
        if (inboundIds.Count == 0)
        {
            return result;
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT inbound_message_id, media_id, media_type, display_name,
                   content_length, content_sha256
            FROM channel_media
            WHERE inbound_message_id IN (SELECT value FROM json_each($ids))
            ORDER BY inbound_message_id, ordinal;
            """;
        Add(command, "$ids", JsonSerializer.Serialize(
            inboundIds.Select(id => id.ToString("D"))));
        var mutable = new Dictionary<Guid, List<ChannelMediaSummary>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var inboundId = Guid.Parse(reader.GetString(0));
            if (!mutable.TryGetValue(inboundId, out var items))
            {
                items = [];
                mutable.Add(inboundId, items);
            }
            items.Add(new ChannelMediaSummary(
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetString(5)));
        }
        foreach (var (key, value) in mutable)
        {
            result.Add(key, value);
        }
        return result;
    }

    private static async Task<long> ReadRevisionAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT current_revision FROM operations_state WHERE id = 1;";
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static ChannelSnapshot ReadChannel(DbDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetInt32(2) != 0,
        reader.GetString(3), reader.GetString(4),
        Parse<ChannelRuntimeStatus>(reader.GetString(5)),
        reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetInt64(7),
        Time(reader, 8), Time(reader, 9));

    private static ChannelInboundSummary ReadInbound(DbDataReader reader) => new(
        Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
        reader.GetString(3), reader.GetInt64(4), reader.GetString(5),
        Guid.Parse(reader.GetString(6)), ReadGuid(reader, 7), ReadGuid(reader, 8),
        Parse<ChannelInboundStatus>(reader.GetString(9)), reader.GetInt32(10),
        reader.IsDBNull(11) ? null : reader.GetString(11), [], reader.GetInt64(12),
        Time(reader, 13), Time(reader, 14), ReadTime(reader, 15));

    private static ChannelOutboxSummary ReadOutbox(DbDataReader reader) => new(
        Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
        reader.GetString(2), reader.GetString(3), reader.GetString(4),
        Guid.Parse(reader.GetString(5)), Guid.Parse(reader.GetString(6)),
        Guid.Parse(reader.GetString(7)), reader.GetInt64(8), reader.GetString(9),
        Parse<ChannelOutboxStatus>(reader.GetString(10)), reader.GetInt32(11),
        reader.IsDBNull(12) ? null : reader.GetString(12), reader.GetInt64(13),
        Time(reader, 14), Time(reader, 15), ReadTime(reader, 16));

    private static ChannelPage<T> Page<T>(
        long revision,
        List<T> items,
        int pageSize,
        string shape,
        Func<T, (DateTimeOffset UpdatedAtUtc, string Id)> key)
    {
        var hasMore = items.Count > pageSize;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }
        var last = items.LastOrDefault();
        return new ChannelPage<T>(
            revision,
            items,
            hasMore && last is not null
                ? EncodeCursor(shape, key(last).UpdatedAtUtc, key(last).Id)
                : null);
    }

    private static string Shape(params string?[] values) =>
        Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(string.Join('|', values))))
            .ToLowerInvariant();

    private static string EncodeCursor(
        string shape,
        DateTimeOffset updated,
        string id) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
            new Cursor(shape, updated.ToUnixTimeMilliseconds(), id),
            JsonOptions));

    private static Cursor? DecodeCursor(string? cursor, string shape)
    {
        if (cursor is null)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<Cursor>(
                       Convert.FromBase64String(cursor),
                       JsonOptions) is { } value &&
                   string.Equals(value.Shape, shape, StringComparison.Ordinal) &&
                   !string.IsNullOrWhiteSpace(value.Id)
                ? value
                : throw InvalidCursor();
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw InvalidCursor();
        }
    }

    private static ChannelServiceException InvalidCursor() =>
        new(ChannelErrorCodes.CursorInvalid, "Channel cursor is invalid.");

    private static T Parse<T>(string value) where T : struct, Enum =>
        Enum.Parse<T>(value, ignoreCase: true);

    private static string? Wire<T>(T? value) where T : struct, Enum =>
        value is null ? null : Lower(value.Value.ToString());

    private static string Lower(string value) =>
        char.ToLowerInvariant(value[0]) + value[1..];

    private static Guid? ReadGuid(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Guid.Parse(reader.GetString(ordinal));

    private static DateTimeOffset Time(DbDataReader reader, int ordinal) =>
        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(ordinal));

    private static DateTimeOffset? ReadTime(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Time(reader, ordinal);

    private static void ValidatePageSize(int pageSize)
    {
        if (pageSize is < 1 or > MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }
    }

    private static void ValidateChannelId(string channelId)
    {
        if (string.IsNullOrWhiteSpace(channelId) ||
            channelId.Length > 64 ||
            channelId != channelId.ToLowerInvariant() ||
            channelId[0] == '-' || channelId[^1] == '-' ||
            channelId.Contains("--", StringComparison.Ordinal) ||
            channelId.Any(character =>
                character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
        {
            throw new ArgumentException("Channel id is invalid.", nameof(channelId));
        }
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private sealed record Cursor(string Shape, long UpdatedUtc, string Id);
}
