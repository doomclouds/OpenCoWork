using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Operations;

namespace OpenCoWork.Core.Gateway;

internal enum GatewayInboundFaultPoint
{
    ThreadCreated,
    MappingCommitted,
    QueueCommitted,
    DeliveredCommitted,
}

public sealed class GatewayService : IChannelInboundSink
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly long[] RetryDelayMilliseconds =
    [
        1_000,
        5_000,
        30_000,
        120_000,
        600_000,
    ];
    private readonly IWorkspaceStateStore _state;
    private readonly GatewayMediaStore _media;
    private readonly ISessionService _sessions;
    private readonly TimeProvider _timeProvider;
    private readonly Action<GatewayInboundFaultPoint>? _faultInjector;
    private readonly OperationsChangeHub? _changes;

    internal event Action? Changed;

    public GatewayService(
        IWorkspaceStateStore state,
        GatewayMediaStore media,
        ISessionService sessions,
        TimeProvider timeProvider)
        : this(state, media, sessions, timeProvider, null, null)
    {
    }

    internal GatewayService(
        IWorkspaceStateStore state,
        GatewayMediaStore media,
        ISessionService sessions,
        TimeProvider timeProvider,
        OperationsChangeHub changes)
        : this(state, media, sessions, timeProvider, null, changes)
    {
    }

    internal GatewayService(
        IWorkspaceStateStore state,
        GatewayMediaStore media,
        ISessionService sessions,
        TimeProvider timeProvider,
        Action<GatewayInboundFaultPoint>? faultInjector,
        OperationsChangeHub? changes = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _state = state;
        _media = media;
        _sessions = sessions;
        _timeProvider = timeProvider;
        _faultInjector = faultInjector;
        _changes = changes;
    }

    public async ValueTask<ChannelInboundReceipt> AcceptAsync(
        ChannelInboundRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var activity = OpenCoWorkTelemetry.StartActivity(
            OpenCoWorkTelemetry.GatewayReceive,
            System.Diagnostics.ActivityKind.Server,
            channelId: request.ChannelId);
        Validate(request);
        var duplicate = await FindReceiptAsync(
            request.ChannelId,
            request.Envelope.MessageId,
            request.BodySha256,
            cancellationToken);
        if (duplicate is not null)
        {
            activity?.SetTag(
                OpenCoWorkTelemetry.CorrelationIdTag,
                duplicate.CorrelationId.ToString("D"));
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
            return duplicate;
        }

        var media = await _media.StoreFilesAsync(
            request.ChannelId,
            request.Envelope.Attachments,
            cancellationToken);
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var receipt = await _state.WriteAsync(
            async (connection, transaction, token) =>
            {
                var existing = await FindReceiptAsync(
                    connection,
                    transaction,
                    request.ChannelId,
                    request.Envelope.MessageId,
                    request.BodySha256,
                    token);
                if (existing is not null)
                {
                    return existing;
                }

                await RequireReadyChannelAsync(
                    connection,
                    transaction,
                    request.ChannelId,
                    token);
                var inboundId = Guid.CreateVersion7();
                var correlationId = Guid.CreateVersion7();
                var createKey = Guid.CreateVersion7();
                var submitKey = Guid.CreateVersion7();
                var sequence = await NextPartitionSequenceAsync(
                    connection,
                    transaction,
                    request.ChannelId,
                    request.Envelope.ConversationId,
                    token);
                var payload = JsonSerializer.Serialize(
                    new StoredInboundPayload(request.Envelope.Text, media),
                    JsonOptions);
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO channel_inbound_messages (
                        inbound_message_id, channel_id, external_message_id,
                        external_conversation_id, partition_sequence, payload_json,
                        body_sha256, session_create_idempotency_key,
                        session_submit_idempotency_key, session_expected_sequence,
                        session_queue_item_id, correlation_id, thread_id, turn_id,
                        status, attempt_count,
                        next_attempt_utc, lease_owner_instance_id, lease_expires_utc,
                        error_code, diagnostic, revision, created_utc, updated_utc,
                        delivered_utc)
                    VALUES (
                        $inboundId, $channelId, $messageId, $conversationId,
                        $partitionSequence, $payload, $bodySha256, $createKey,
                        $submitKey, NULL, NULL, $correlationId, NULL, NULL, 'pending', 0,
                        $now, NULL, NULL, NULL, NULL, 1, $now, $now, NULL);
                    """;
                Add(command, "$inboundId", Wire(inboundId));
                Add(command, "$channelId", request.ChannelId);
                Add(command, "$messageId", request.Envelope.MessageId);
                Add(command, "$conversationId", request.Envelope.ConversationId);
                Add(command, "$partitionSequence", sequence);
                Add(command, "$payload", payload);
                Add(command, "$bodySha256", request.BodySha256);
                Add(command, "$createKey", Wire(createKey));
                Add(command, "$submitKey", Wire(submitKey));
                Add(command, "$correlationId", Wire(correlationId));
                Add(command, "$now", now);
                await command.ExecuteNonQueryAsync(token);
                await GatewayMediaStore.InsertMetadataAsync(
                    connection,
                    transaction,
                    inboundId,
                    media,
                    now,
                    token);
                await BumpRevisionAsync(connection, transaction, now, token);
                return new ChannelInboundReceipt(inboundId, correlationId, false);
            },
            cancellationToken);
        if (!receipt.Duplicate)
        {
            Changed?.Invoke();
            _changes?.Publish(
                OperationsChangeKind.Channel,
                "inboundAccepted",
                receipt.ReceiptId.ToString("D"));
        }

        activity?.SetTag(
            OpenCoWorkTelemetry.CorrelationIdTag,
            receipt.CorrelationId.ToString("D"));
        activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
        return receipt;
    }

    public async Task<int> DispatchPendingAsync(
        Guid runtimeInstanceId,
        int maxConcurrency,
        CancellationToken cancellationToken = default)
    {
        RequireVersionSeven(runtimeInstanceId, nameof(runtimeInstanceId));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxConcurrency, 64);
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var candidates = await ReadCandidatesAsync(now, maxConcurrency, cancellationToken);
        var processed = 0;
        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = maxConcurrency,
                CancellationToken = cancellationToken,
            },
            async (candidate, token) =>
            {
                if (await DispatchOneAsync(runtimeInstanceId, candidate, now, token))
                {
                    Interlocked.Increment(ref processed);
                }
            });
        return processed;
    }

    private async Task<bool> DispatchOneAsync(
        Guid runtimeInstanceId,
        InboundDispatch candidate,
        long now,
        CancellationToken cancellationToken)
    {
        if (!await ClaimAsync(runtimeInstanceId, candidate.InboundMessageId, now, cancellationToken))
        {
            return false;
        }

        using var activity = OpenCoWorkTelemetry.StartActivity(
            OpenCoWorkTelemetry.GatewayDispatch,
            System.Diagnostics.ActivityKind.Consumer,
            candidate.CorrelationId,
            threadId: candidate.ThreadId,
            channelId: candidate.ChannelId);
        try
        {
            var threadId = candidate.ThreadId ?? await EnsureThreadAsync(candidate, now, cancellationToken);
            var expectedSequence = candidate.SessionExpectedSequence ??
                                   await EnsureExpectedSequenceAsync(
                                       candidate.InboundMessageId,
                                       threadId,
                                       now,
                                       cancellationToken);
            var payload = JsonSerializer.Deserialize<StoredInboundPayload>(
                              candidate.PayloadJson,
                              JsonOptions)
                          ?? throw Unavailable("Inbound payload is invalid.");
            var submitted = await _sessions.EnqueueInputAsync(
                new EnqueueInputRequest(
                    threadId,
                    candidate.SubmitIdempotencyKey,
                    expectedSequence,
                    FormatSessionText(payload),
                    TurnAdmission.QueueIfBusy,
                    candidate.CorrelationId),
                cancellationToken);
            if (submitted.Status == SessionCommandStatus.Rejected || submitted.Value is null)
            {
                throw Unavailable("Session rejected inbound work.", submitted.Error?.IsRetryable ?? true);
            }

            _faultInjector?.Invoke(GatewayInboundFaultPoint.QueueCommitted);
            await MarkDeliveredAsync(
                candidate.InboundMessageId,
                threadId,
                submitted.Value.QueueItem.QueueItemId,
                submitted.Value.TurnId,
                now,
                cancellationToken);
            _faultInjector?.Invoke(GatewayInboundFaultPoint.DeliveredCommitted);
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            var errorCode = error is ChannelServiceException channelError
                ? channelError.Code
                : ChannelErrorCodes.Unavailable;
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error);
            activity?.SetTag(OpenCoWorkTelemetry.ErrorCodeTag, errorCode);
            await MarkFailedAsync(
                candidate.InboundMessageId,
                errorCode,
                error is not ChannelServiceException serviceError || serviceError.Retryable,
                candidate.AttemptCount + 1,
                now,
                CancellationToken.None);
            return true;
        }
    }

    private async Task<Guid> EnsureThreadAsync(
        InboundDispatch candidate,
        long now,
        CancellationToken cancellationToken)
    {
        var mapping = await ReadMappedThreadAsync(
            candidate.ChannelId,
            candidate.ConversationId,
            cancellationToken);
        if (mapping is not null)
        {
            await SetInboundThreadAsync(candidate.InboundMessageId, mapping.Value, now, cancellationToken);
            return mapping.Value;
        }

        var created = await _sessions.CreateThreadAsync(
            new CreateThreadRequest(
                candidate.CreateIdempotencyKey,
                0,
                $"{candidate.ChannelId}: {candidate.ConversationId}",
                HistoryMode.Server),
            cancellationToken);
        if (created.Status == SessionCommandStatus.Rejected || created.Value is null)
        {
            throw Unavailable("Session rejected thread creation.", created.Error?.IsRetryable ?? true);
        }

        _faultInjector?.Invoke(GatewayInboundFaultPoint.ThreadCreated);
        var threadId = created.Value.ThreadId;
        await _state.WriteAsync(
            async (connection, transaction, token) =>
            {
                await using var mappingCommand = connection.CreateCommand();
                mappingCommand.Transaction = transaction;
                mappingCommand.CommandText =
                    """
                    INSERT INTO channel_thread_mappings (
                        channel_id, external_conversation_id, thread_id,
                        thread_create_idempotency_key, revision, created_utc, updated_utc)
                    VALUES ($channelId, $conversationId, $threadId, $createKey, 1, $now, $now)
                    ON CONFLICT (channel_id, external_conversation_id) DO NOTHING;
                    """;
                Add(mappingCommand, "$channelId", candidate.ChannelId);
                Add(mappingCommand, "$conversationId", candidate.ConversationId);
                Add(mappingCommand, "$threadId", Wire(threadId));
                Add(mappingCommand, "$createKey", Wire(candidate.CreateIdempotencyKey));
                Add(mappingCommand, "$now", now);
                await mappingCommand.ExecuteNonQueryAsync(token);

                await using var inboundCommand = connection.CreateCommand();
                inboundCommand.Transaction = transaction;
                inboundCommand.CommandText =
                    """
                    UPDATE channel_inbound_messages
                    SET thread_id = (
                            SELECT thread_id FROM channel_thread_mappings
                            WHERE channel_id = $channelId
                              AND external_conversation_id = $conversationId),
                        revision = revision + 1,
                        updated_utc = $now
                    WHERE inbound_message_id = $inboundId;
                    """;
                Add(inboundCommand, "$channelId", candidate.ChannelId);
                Add(inboundCommand, "$conversationId", candidate.ConversationId);
                Add(inboundCommand, "$inboundId", Wire(candidate.InboundMessageId));
                Add(inboundCommand, "$now", now);
                await inboundCommand.ExecuteNonQueryAsync(token);
                return true;
            },
            cancellationToken);
        _faultInjector?.Invoke(GatewayInboundFaultPoint.MappingCommitted);
        return await ReadMappedThreadAsync(
                   candidate.ChannelId,
                   candidate.ConversationId,
                   cancellationToken)
               ?? throw Unavailable("Thread mapping is unavailable.");
    }

    private async Task<long> EnsureExpectedSequenceAsync(
        Guid inboundMessageId,
        Guid threadId,
        long now,
        CancellationToken cancellationToken)
    {
        var thread = await _sessions.GetThreadAsync(threadId, cancellationToken);
        if (!thread.IsSuccess || thread.Value is null)
        {
            throw Unavailable("Mapped session thread is unavailable.");
        }

        return await _state.WriteAsync(
            async (connection, transaction, token) =>
            {
                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText =
                    """
                    UPDATE channel_inbound_messages
                    SET session_expected_sequence = $sequence,
                        revision = revision + 1,
                        updated_utc = $now
                    WHERE inbound_message_id = $inboundId
                      AND session_expected_sequence IS NULL;
                    """;
                Add(update, "$sequence", thread.Value.CurrentSequence);
                Add(update, "$now", now);
                Add(update, "$inboundId", Wire(inboundMessageId));
                await update.ExecuteNonQueryAsync(token);

                await using var read = connection.CreateCommand();
                read.Transaction = transaction;
                read.CommandText =
                    "SELECT session_expected_sequence FROM channel_inbound_messages WHERE inbound_message_id = $inboundId;";
                Add(read, "$inboundId", Wire(inboundMessageId));
                return Convert.ToInt64(
                    await read.ExecuteScalarAsync(token),
                    CultureInfo.InvariantCulture);
            },
            cancellationToken);
    }

    private async Task<IReadOnlyList<InboundDispatch>> ReadCandidatesAsync(
        long now,
        int limit,
        CancellationToken cancellationToken) =>
        await _state.ReadAsync<IReadOnlyList<InboundDispatch>>(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT inbound_message_id, channel_id, external_conversation_id,
                           payload_json, session_create_idempotency_key,
                           session_submit_idempotency_key, session_expected_sequence,
                           correlation_id, thread_id, attempt_count
                    FROM channel_inbound_messages current
                    WHERE (
                            (status IN ('pending', 'failed') AND next_attempt_utc <= $now) OR
                            (status = 'dispatching' AND lease_expires_utc <= $now))
                      AND NOT EXISTS (
                          SELECT 1 FROM channel_inbound_messages older
                          WHERE older.channel_id = current.channel_id
                            AND older.external_conversation_id = current.external_conversation_id
                            AND older.partition_sequence < current.partition_sequence
                            AND older.status NOT IN ('delivered', 'deadLettered'))
                    ORDER BY created_utc, inbound_message_id
                    LIMIT $limit;
                    """;
                Add(command, "$now", now);
                Add(command, "$limit", limit);
                await using var reader = await command.ExecuteReaderAsync(token);
                var result = new List<InboundDispatch>();
                while (await reader.ReadAsync(token))
                {
                    result.Add(new InboundDispatch(
                        Guid.Parse(reader.GetString(0)),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        Guid.Parse(reader.GetString(4)),
                        Guid.Parse(reader.GetString(5)),
                        reader.IsDBNull(6) ? null : reader.GetInt64(6),
                        Guid.Parse(reader.GetString(7)),
                        reader.IsDBNull(8) ? null : Guid.Parse(reader.GetString(8)),
                        reader.GetInt32(9)));
                }

                return result;
            },
            cancellationToken);

    private async Task<bool> ClaimAsync(
        Guid runtimeInstanceId,
        Guid inboundMessageId,
        long now,
        CancellationToken cancellationToken) =>
        await _state.WriteAsync(
            async (connection, transaction, token) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    UPDATE channel_inbound_messages
                    SET status = 'dispatching', attempt_count = attempt_count + 1,
                        lease_owner_instance_id = $owner,
                        lease_expires_utc = $expires, error_code = NULL,
                        diagnostic = NULL, revision = revision + 1, updated_utc = $now
                    WHERE inbound_message_id = $inboundId AND (
                        (status IN ('pending', 'failed') AND next_attempt_utc <= $now) OR
                        (status = 'dispatching' AND lease_expires_utc <= $now));
                    """;
                Add(command, "$owner", Wire(runtimeInstanceId));
                Add(command, "$expires", now + (long)TimeSpan.FromMinutes(2).TotalMilliseconds);
                Add(command, "$now", now);
                Add(command, "$inboundId", Wire(inboundMessageId));
                return await command.ExecuteNonQueryAsync(token) == 1;
            },
            cancellationToken);

    private async Task SetInboundThreadAsync(
        Guid inboundMessageId,
        Guid threadId,
        long now,
        CancellationToken cancellationToken) =>
        _ = await _state.WriteAsync(
            async (connection, transaction, token) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    UPDATE channel_inbound_messages
                    SET thread_id = $threadId, revision = revision + 1, updated_utc = $now
                    WHERE inbound_message_id = $inboundId;
                    """;
                Add(command, "$threadId", Wire(threadId));
                Add(command, "$now", now);
                Add(command, "$inboundId", Wire(inboundMessageId));
                await command.ExecuteNonQueryAsync(token);
                return true;
            },
            cancellationToken);

    private async Task MarkDeliveredAsync(
        Guid inboundMessageId,
        Guid threadId,
        Guid queueItemId,
        Guid? turnId,
        long now,
        CancellationToken cancellationToken) =>
        _ = await _state.WriteAsync(
            async (connection, transaction, token) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    UPDATE channel_inbound_messages
                    SET thread_id = $threadId, session_queue_item_id = $queueItemId,
                        turn_id = $turnId, status = 'delivered',
                        lease_owner_instance_id = NULL, lease_expires_utc = NULL,
                        error_code = NULL, diagnostic = NULL,
                        revision = revision + 1, updated_utc = $now, delivered_utc = $now
                    WHERE inbound_message_id = $inboundId;
                    """;
                Add(command, "$threadId", Wire(threadId));
                Add(command, "$queueItemId", Wire(queueItemId));
                Add(command, "$turnId", turnId is null ? DBNull.Value : Wire(turnId.Value));
                Add(command, "$now", now);
                Add(command, "$inboundId", Wire(inboundMessageId));
                await command.ExecuteNonQueryAsync(token);
                return true;
            },
            cancellationToken);

    private async Task MarkFailedAsync(
        Guid inboundMessageId,
        string errorCode,
        bool retryable,
        int attemptCount,
        long now,
        CancellationToken cancellationToken) =>
        _ = await _state.WriteAsync(
            async (connection, transaction, token) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                var deadLettered = !retryable || attemptCount >= 5;
                command.CommandText =
                    """
                    UPDATE channel_inbound_messages
                    SET status = $status, next_attempt_utc = $nextAttempt,
                        lease_owner_instance_id = NULL, lease_expires_utc = NULL,
                        error_code = $errorCode, diagnostic = 'Inbound dispatch failed.',
                        revision = revision + 1, updated_utc = $now
                    WHERE inbound_message_id = $inboundId AND status <> 'delivered';
                    """;
                Add(command, "$now", now);
                Add(command, "$status", deadLettered ? "deadLettered" : "failed");
                Add(
                    command,
                    "$nextAttempt",
                    deadLettered
                        ? now
                        : now + RetryDelayMilliseconds[attemptCount - 1]);
                Add(command, "$errorCode", errorCode);
                Add(command, "$inboundId", Wire(inboundMessageId));
                await command.ExecuteNonQueryAsync(token);
                return true;
            },
            cancellationToken);

    private async Task<Guid?> ReadMappedThreadAsync(
        string channelId,
        string conversationId,
        CancellationToken cancellationToken) =>
        await _state.ReadAsync<Guid?>(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT thread_id FROM channel_thread_mappings
                    WHERE channel_id = $channelId AND external_conversation_id = $conversationId;
                    """;
                Add(command, "$channelId", channelId);
                Add(command, "$conversationId", conversationId);
                var value = await command.ExecuteScalarAsync(token);
                return value is string wire ? Guid.Parse(wire) : null;
            },
            cancellationToken);

    private async Task<ChannelInboundReceipt?> FindReceiptAsync(
        string channelId,
        string messageId,
        string bodySha256,
        CancellationToken cancellationToken) =>
        await _state.ReadAsync(
            (connection, token) => FindReceiptAsync(
                connection,
                null,
                channelId,
                messageId,
                bodySha256,
                token),
            cancellationToken);

    private static async ValueTask<ChannelInboundReceipt?> FindReceiptAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string channelId,
        string messageId,
        string bodySha256,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT inbound_message_id, body_sha256, correlation_id
            FROM channel_inbound_messages
            WHERE channel_id = $channelId AND external_message_id = $messageId;
            """;
        Add(command, "$channelId", channelId);
        Add(command, "$messageId", messageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        if (!string.Equals(reader.GetString(1), bodySha256, StringComparison.Ordinal))
        {
            throw new ChannelServiceException(
                ChannelErrorCodes.IdempotencyConflict,
                "External message ID is bound to different content.");
        }

        return new ChannelInboundReceipt(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(2)),
            true);
    }

    private static async ValueTask RequireReadyChannelAsync(
        DbConnection connection,
        DbTransaction transaction,
        string channelId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT enabled, trust_status, runtime_status FROM channels WHERE channel_id = $channelId;";
        Add(command, "$channelId", channelId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new ChannelServiceException(ChannelErrorCodes.NotFound, "Channel was not found.");
        }

        if (reader.GetInt32(0) != 1 ||
            !string.Equals(reader.GetString(1), "trusted", StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(2), "ready", StringComparison.Ordinal))
        {
            throw Unavailable("Channel is not ready.");
        }
    }

    private static async ValueTask<long> NextPartitionSequenceAsync(
        DbConnection connection,
        DbTransaction transaction,
        string channelId,
        string conversationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT coalesce(max(partition_sequence), 0) + 1
            FROM channel_inbound_messages
            WHERE channel_id = $channelId AND external_conversation_id = $conversationId;
            """;
        Add(command, "$channelId", channelId);
        Add(command, "$conversationId", conversationId);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    private static async ValueTask BumpRevisionAsync(
        DbConnection connection,
        DbTransaction transaction,
        long now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "UPDATE operations_state SET current_revision = current_revision + 1, updated_utc = $now WHERE id = 1;";
        Add(command, "$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string FormatSessionText(StoredInboundPayload payload)
    {
        var builder = new StringBuilder("[OpenCoWork external channel message]");
        if (!string.IsNullOrEmpty(payload.Text))
        {
            builder.Append("\n\n").Append(payload.Text);
        }

        if (payload.Media.Count != 0)
        {
            builder.Append("\n\n");

            foreach (var item in payload.Media)
            {
                builder.Append("[media: ")
                    .Append(item.DisplayName)
                    .Append("; type=").Append(item.MediaType)
                    .Append("; bytes=").Append(item.ContentLength)
                    .Append("; sha256=").Append(item.ContentSha256)
                    .Append("; id=").Append(Wire(item.MediaId))
                    .Append("]\n");
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static void Validate(ChannelInboundRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Envelope);
        ArgumentNullException.ThrowIfNull(request.Envelope.Attachments);
        if (request.Envelope.SchemaVersion != 1 ||
            !ValidBoundedText(request.ChannelId, 64) ||
            !ValidBoundedText(request.Envelope.MessageId, 1024) ||
            !ValidBoundedText(request.Envelope.ConversationId, 1024) ||
            request.BodySha256 is not { Length: 64 } ||
            request.BodySha256.Any(character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
            request.Envelope.Text is { } text && Encoding.UTF8.GetByteCount(text) > 256 * 1024 ||
            (string.IsNullOrEmpty(request.Envelope.Text) && request.Envelope.Attachments.Count == 0))
        {
            throw new ChannelServiceException(ChannelErrorCodes.SchemaInvalid, "Inbound request is invalid.");
        }
    }

    private static bool ValidBoundedText(string? value, int maximumBytes) =>
        !string.IsNullOrWhiteSpace(value) && Encoding.UTF8.GetByteCount(value) <= maximumBytes;

    private static void RequireVersionSeven(Guid value, string parameterName)
    {
        if (value.Version != 7)
        {
            throw new ArgumentException("Value must be a UUIDv7.", parameterName);
        }
    }

    private static ChannelServiceException Unavailable(string message, bool retryable = true) =>
        new(ChannelErrorCodes.Unavailable, message, retryable);

    private static string Wire(Guid value) =>
        value.ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant();

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record StoredInboundPayload(
        string? Text,
        IReadOnlyList<ChannelMediaReference> Media);

    private sealed record InboundDispatch(
        Guid InboundMessageId,
        string ChannelId,
        string ConversationId,
        string PayloadJson,
        Guid CreateIdempotencyKey,
        Guid SubmitIdempotencyKey,
        long? SessionExpectedSequence,
        Guid CorrelationId,
        Guid? ThreadId,
        int AttemptCount);
}
