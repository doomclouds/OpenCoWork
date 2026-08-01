using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Capabilities;
using OpenCoWork.Core.Configuration;

namespace OpenCoWork.Core.Gateway;

internal enum GatewayOutboxFaultPoint
{
    OutboxCommitted,
    SendCompleted,
    SentCommitted,
}

internal sealed class GatewayReconciler
{
    private const int MaximumEnvelopeBytes = 256 * 1024;
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(1);
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
    private readonly IReadOnlyDictionary<string, GatewayChannelConfig> _channels;
    private readonly ChannelCredentialService _credentials;
    private readonly IChannelSender _sender;
    private readonly GatewayService? _gateway;
    private readonly GatewayChannelRuntime? _channelRuntime;
    private readonly GatewayMediaStore? _media;
    private readonly TimeProvider _timeProvider;
    private readonly Action<GatewayOutboxFaultPoint>? _faultInjector;
    private readonly ConcurrentDictionary<string, ChannelSendState> _sendStates = [];
    private readonly IModuleHealthReporter? _health;
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly Guid _runtimeInstanceId = Guid.CreateVersion7();
    private CancellationTokenSource? _lifetime;
    private Task? _worker;
    private int _degraded;
    private long _lastSuccessfulReconcileMilliseconds = long.MinValue;

    public GatewayReconciler(
        IWorkspaceStateStore state,
        GatewayConfig config,
        ChannelCredentialService credentials,
        IChannelSender sender,
        GatewayService gateway,
        GatewayChannelRuntime channelRuntime,
        GatewayMediaStore media,
        TimeProvider timeProvider,
        IModuleHealthReporter? health = null)
        : this(
            state,
            config,
            credentials,
            sender,
            gateway,
            timeProvider,
            null,
            health,
            channelRuntime,
            media)
    {
    }

    internal GatewayReconciler(
        IWorkspaceStateStore state,
        GatewayConfig config,
        ChannelCredentialService credentials,
        IChannelSender sender,
        GatewayService? gateway,
        TimeProvider timeProvider,
        Action<GatewayOutboxFaultPoint>? faultInjector,
        IModuleHealthReporter? health = null,
        GatewayChannelRuntime? channelRuntime = null,
        GatewayMediaStore? media = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _state = state;
        _channels = config.Channels.ToDictionary(channel => channel.Id, StringComparer.Ordinal);
        _credentials = credentials;
        _sender = sender;
        _gateway = gateway;
        _channelRuntime = channelRuntime;
        _media = media;
        _timeProvider = timeProvider;
        _faultInjector = faultInjector;
        _health = health;
    }

    internal bool IsRunning => Volatile.Read(ref _lifetime) is not null;

    internal bool HasEnabledChannels => _channelRuntime?.HasEnabledChannels == true;

    internal DateTimeOffset? LastSuccessfulReconcileAtUtc
    {
        get
        {
            var value = Volatile.Read(ref _lastSuccessfulReconcileMilliseconds);
            return value == long.MinValue
                ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(value);
        }
    }

    internal byte[]? AcquireInboundSecret(string channelId) =>
        _channelRuntime?.AcquireInboundSecret(channelId);

    internal async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        if (_lifetime is not null)
        {
            return;
        }

        _gateway?.Changed += Wake;
        try
        {
            await ReconcileOnceAsync(cancellationToken);
            MarkHealthy();
            var lifetime = new CancellationTokenSource();
            _lifetime = lifetime;
            _worker = RunAsync(lifetime.Token);
        }
        catch (Exception startupError)
        {
            _gateway?.Changed -= Wake;
            if (_channelRuntime is null)
            {
                throw;
            }

            try
            {
                await _channelRuntime.StopAsync(CancellationToken.None);
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException(
                    "Gateway startup failed and channel cleanup reported an error.",
                    startupError,
                    cleanupError);
            }

            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(startupError)
                .Throw();
            throw;
        }
    }

    internal async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        var lifetime = Interlocked.Exchange(ref _lifetime, null);
        if (lifetime is null)
        {
            return;
        }

        _gateway?.Changed -= Wake;
        await lifetime.CancelAsync();
        var worker = Interlocked.Exchange(ref _worker, null);
        if (worker is not null)
        {
            try
            {
                await worker.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
        }

        Exception? releaseError = null;
        try
        {
            await ReleaseOwnedLeasesAsync(cancellationToken);
        }
        catch (Exception error)
        {
            releaseError = error;
        }

        try
        {
            if (_channelRuntime is not null)
            {
                await _channelRuntime.StopAsync(cancellationToken);
            }
        }
        catch (Exception channelError) when (releaseError is not null)
        {
            throw new AggregateException(
                "Gateway leases and channel runtime failed to stop.",
                releaseError,
                channelError);
        }
        finally
        {
            lifetime.Dispose();
        }

        if (releaseError is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(releaseError)
                .Throw();
        }
    }

    internal void Wake() => _wake.Writer.TryWrite(true);

    public async Task<int> ReconcileAsync(
        Guid runtimeInstanceId,
        int maxConcurrency,
        CancellationToken cancellationToken = default)
    {
        var processed = _gateway is null
            ? 0
            : await _gateway.DispatchPendingAsync(
                runtimeInstanceId,
                maxConcurrency,
                cancellationToken);
        processed += await ReconcileOutboxAsync(
            runtimeInstanceId,
            maxConcurrency,
            cancellationToken);
        if (_media is not null)
        {
            processed += await _media.CleanupOrphansAsync(
                _timeProvider.GetUtcNow().AddHours(-1),
                cancellationToken);
        }

        return processed;
    }

    private async Task ReconcileOnceAsync(CancellationToken cancellationToken)
    {
        await _reconcileGate.WaitAsync(cancellationToken);
        try
        {
            if (_channelRuntime is not null)
            {
                await _channelRuntime.ReconcileAsync(cancellationToken);
            }
            _ = await ReconcileAsync(
                _runtimeInstanceId,
                Math.Max(1, Math.Min(64, _channels.Values.Sum(channel =>
                    channel.MaxConcurrentSends))),
                cancellationToken);
            Volatile.Write(
                ref _lastSuccessfulReconcileMilliseconds,
                _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await WaitForWakeAsync(cancellationToken);
                await ReconcileOnceAsync(cancellationToken);
                MarkHealthy();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                await MarkDegradedAsync(error);
            }
        }
    }

    private async Task WaitForWakeAsync(CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var wake = _wake.Reader.WaitToReadAsync(wait.Token).AsTask();
        var timer = Task.Delay(ReconcileInterval, _timeProvider, wait.Token);
        _ = await Task.WhenAny(wake, timer);
        await wait.CancelAsync();
        while (_wake.Reader.TryRead(out _))
        {
        }
    }

    private async Task ReleaseOwnedLeasesAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await _state.WriteAsync(
            async (connection, transaction, token) =>
            {
                await using var inbound = connection.CreateCommand();
                inbound.Transaction = transaction;
                inbound.CommandText =
                    """
                    UPDATE channel_inbound_messages
                    SET status = 'failed', next_attempt_utc = $now,
                        lease_owner_instance_id = NULL, lease_expires_utc = NULL,
                        error_code = $errorCode, diagnostic = 'Gateway stopped.',
                        revision = revision + 1, updated_utc = $now
                    WHERE status = 'dispatching' AND lease_owner_instance_id = $owner;
                    """;
                Add(inbound, "$now", now);
                Add(inbound, "$errorCode", ChannelErrorCodes.Unavailable);
                Add(inbound, "$owner", Wire(_runtimeInstanceId));
                await inbound.ExecuteNonQueryAsync(token);

                await using var outbox = connection.CreateCommand();
                outbox.Transaction = transaction;
                outbox.CommandText =
                    """
                    UPDATE channel_outbox
                    SET status = 'failed', next_attempt_utc = $now,
                        lease_owner_instance_id = NULL, lease_expires_utc = NULL,
                        error_code = $errorCode, diagnostic = 'Gateway stopped.',
                        revision = revision + 1, updated_utc = $now
                    WHERE status = 'sending' AND lease_owner_instance_id = $owner;
                    """;
                Add(outbox, "$now", now);
                Add(outbox, "$errorCode", ChannelErrorCodes.Unavailable);
                Add(outbox, "$owner", Wire(_runtimeInstanceId));
                await outbox.ExecuteNonQueryAsync(token);
                return true;
            },
            cancellationToken);
    }

    private async Task MarkDegradedAsync(Exception error)
    {
        if (_channelRuntime is not null)
        {
            try
            {
                await _channelRuntime.SetUnavailableAsync(CancellationToken.None);
            }
            catch
            {
            }
        }
        Interlocked.Exchange(ref _degraded, 1);
        _health?.ReportDegraded(
            "gateway",
            $"Gateway reconciliation is unavailable: {error.GetType().Name}.");
    }

    private void MarkHealthy()
    {
        if (Interlocked.Exchange(ref _degraded, 0) != 0)
        {
            _health?.ClearDegraded("gateway");
        }
    }

    internal async Task<int> ReconcileOutboxAsync(
        Guid runtimeInstanceId,
        int maxConcurrency,
        CancellationToken cancellationToken = default)
    {
        RequireVersionSeven(runtimeInstanceId, nameof(runtimeInstanceId));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxConcurrency, 64);
        var removed = await DeadLetterRemovedTurnsAsync(cancellationToken);
        var created = await CaptureTerminalTurnsAsync(cancellationToken);
        if (created != 0)
        {
            _faultInjector?.Invoke(GatewayOutboxFaultPoint.OutboxCommitted);
        }

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var candidates = await ReadSendCandidatesAsync(now, maxConcurrency, cancellationToken);
        var sent = 0;
        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = maxConcurrency,
                CancellationToken = cancellationToken,
            },
            async (candidate, token) =>
            {
                if (await SendOneAsync(runtimeInstanceId, candidate, token))
                {
                    Interlocked.Increment(ref sent);
                }
            });
        return removed + created + sent;
    }

    internal async Task<long> RetryOutboxAsync(
        Guid outboxMessageId,
        long expectedRevision,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        RequireVersionSeven(outboxMessageId, nameof(outboxMessageId));
        RequireVersionSeven(idempotencyKey, nameof(idempotencyKey));
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        return await _state.WriteAsync(
            async (connection, transaction, token) =>
            {
                await using var read = connection.CreateCommand();
                read.Transaction = transaction;
                read.CommandText =
                    """
                    SELECT status, revision, retry_idempotency_key
                    FROM channel_outbox WHERE outbox_message_id = $id;
                    """;
                Add(read, "$id", Wire(outboxMessageId));
                await using var reader = await read.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token))
                {
                    throw new ChannelServiceException(
                        ChannelErrorCodes.NotFound,
                        "Outbox message was not found.");
                }

                var status = reader.GetString(0);
                var revision = reader.GetInt64(1);
                var retryKey = reader.IsDBNull(2) ? null : reader.GetString(2);
                await reader.DisposeAsync();
                if (revision >= expectedRevision + 1 &&
                    string.Equals(retryKey, Wire(idempotencyKey), StringComparison.Ordinal))
                {
                    return expectedRevision + 1;
                }

                if (status != "deadLettered" || revision != expectedRevision)
                {
                    throw new ChannelServiceException(
                        ChannelErrorCodes.RevisionConflict,
                        "Outbox revision does not match.",
                        currentRevision: revision);
                }

                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText =
                    """
                    UPDATE channel_outbox
                    SET status = 'pending', attempt_count = 0, next_attempt_utc = $now,
                        lease_owner_instance_id = NULL, lease_expires_utc = NULL,
                        error_code = NULL, diagnostic = NULL,
                        retry_idempotency_key = $idempotencyKey,
                        revision = revision + 1, updated_utc = $now
                    WHERE outbox_message_id = $id AND status = 'deadLettered'
                      AND revision = $expectedRevision;
                    """;
                Add(update, "$now", now);
                Add(update, "$id", Wire(outboxMessageId));
                Add(update, "$idempotencyKey", Wire(idempotencyKey));
                Add(update, "$expectedRevision", expectedRevision);
                if (await update.ExecuteNonQueryAsync(token) != 1)
                {
                    throw new ChannelServiceException(
                        ChannelErrorCodes.RevisionConflict,
                        "Outbox revision does not match.");
                }

                await using var operations = connection.CreateCommand();
                operations.Transaction = transaction;
                operations.CommandText =
                    """
                    UPDATE operations_state
                    SET current_revision = current_revision + 1, updated_utc = $now
                    WHERE id = 1;
                    """;
                Add(operations, "$now", now);
                await operations.ExecuteNonQueryAsync(token);

                return revision + 1;
            },
            cancellationToken);
    }

    private async Task<int> CaptureTerminalTurnsAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        return await _state.WriteAsync(
            async (connection, transaction, token) =>
            {
                var terminals = new List<TerminalTurn>();
                await using (var read = connection.CreateCommand())
                {
                    read.Transaction = transaction;
                    read.CommandText =
                        """
                        SELECT i.inbound_message_id, i.channel_id, i.external_conversation_id,
                               i.external_message_id, i.partition_sequence,
                               i.thread_id, t.turn_id, t.status, t.error_code,
                               t.updated_utc, i.correlation_id,
                               (SELECT content_text FROM items item
                                WHERE item.turn_id = t.turn_id
                                  AND item.item_type = 'agentMessage'
                                  AND item.status = 'completed'
                                ORDER BY item.sequence DESC, item.item_id DESC LIMIT 1)
                        FROM channel_inbound_messages i
                        JOIN turns t ON t.turn_id = i.turn_id OR (
                            i.turn_id IS NULL AND t.thread_id = i.thread_id
                            AND t.correlation_id = i.correlation_id)
                        WHERE i.status = 'delivered'
                          AND t.status IN ('completed', 'failed', 'cancelled')
                          AND NOT EXISTS (
                              SELECT 1 FROM channel_outbox o
                              WHERE o.channel_id = i.channel_id
                                AND o.external_conversation_id = i.external_conversation_id
                                AND o.partition_sequence = i.partition_sequence)
                        ORDER BY i.created_utc, i.inbound_message_id
                        LIMIT 256;
                        """;
                    await using var reader = await read.ExecuteReaderAsync(token);
                    while (await reader.ReadAsync(token))
                    {
                        terminals.Add(new TerminalTurn(
                            Guid.Parse(reader.GetString(0)),
                            reader.GetString(1),
                            reader.GetString(2),
                            reader.GetString(3),
                            reader.GetInt64(4),
                            Guid.Parse(reader.GetString(5)),
                            Guid.Parse(reader.GetString(6)),
                            reader.GetString(7),
                            reader.IsDBNull(8) ? null : reader.GetString(8),
                            reader.GetInt64(9),
                            Guid.Parse(reader.GetString(10)),
                            reader.IsDBNull(11) ? null : reader.GetString(11)));
                    }
                }

                foreach (var terminal in terminals)
                {
                    var outboxId = Guid.CreateVersion7();
                    var deliveryId = Guid.CreateVersion7();
                    var envelope = BoundEnvelope(new ChannelOutboundEnvelope(
                        1,
                        deliveryId,
                        terminal.SourceMessageId,
                        terminal.ConversationId,
                        terminal.ThreadId,
                        terminal.TurnId,
                        terminal.Status,
                        terminal.Status == "completed" ? terminal.Text : null,
                        terminal.Status == "completed" ? null : terminal.ErrorCode,
                        terminal.CorrelationId,
                        DateTimeOffset.FromUnixTimeMilliseconds(terminal.UpdatedUtc)));
                    var body = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
                    await using var insert = connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText =
                        """
                        INSERT INTO channel_outbox (
                            outbox_message_id, delivery_id, channel_id,
                            external_conversation_id, source_message_id, thread_id,
                            turn_id, correlation_id, partition_sequence, envelope_json,
                            body_sha256, status, attempt_count, next_attempt_utc,
                            error_code, diagnostic, revision, created_utc, updated_utc)
                        VALUES ($outboxId, $deliveryId, $channelId, $conversationId,
                                $sourceMessageId, $threadId, $turnId, $correlationId,
                                $partitionSequence, $envelopeJson, $bodySha256,
                                'pending', 0, $now, NULL, NULL, 1, $now, $now);
                        """;
                    Add(insert, "$outboxId", Wire(outboxId));
                    Add(insert, "$deliveryId", Wire(deliveryId));
                    Add(insert, "$channelId", terminal.ChannelId);
                    Add(insert, "$conversationId", terminal.ConversationId);
                    Add(insert, "$sourceMessageId", terminal.SourceMessageId);
                    Add(insert, "$threadId", Wire(terminal.ThreadId));
                    Add(insert, "$turnId", Wire(terminal.TurnId));
                    Add(insert, "$correlationId", Wire(terminal.CorrelationId));
                    Add(insert, "$partitionSequence", terminal.PartitionSequence);
                    Add(insert, "$envelopeJson", Encoding.UTF8.GetString(body));
                    Add(
                        insert,
                        "$bodySha256",
                        Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant());
                    Add(insert, "$now", now);
                    await insert.ExecuteNonQueryAsync(token);

                    await using var updateInbound = connection.CreateCommand();
                    updateInbound.Transaction = transaction;
                    updateInbound.CommandText =
                        """
                        UPDATE channel_inbound_messages
                        SET turn_id = $turnId, revision = revision + 1, updated_utc = $now
                        WHERE inbound_message_id = $inboundId AND turn_id IS NULL;
                        """;
                    Add(updateInbound, "$turnId", Wire(terminal.TurnId));
                    Add(updateInbound, "$now", now);
                    Add(updateInbound, "$inboundId", Wire(terminal.InboundMessageId));
                    await updateInbound.ExecuteNonQueryAsync(token);
                }

                return terminals.Count;
            },
            cancellationToken);
    }

    private async Task<int> DeadLetterRemovedTurnsAsync(
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        return await _state.WriteAsync(
            async (connection, transaction, token) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    UPDATE channel_inbound_messages AS inbound
                    SET status = 'deadLettered', error_code = $errorCode,
                        diagnostic = 'Session queue item was removed.',
                        revision = revision + 1, updated_utc = $now
                    WHERE status = 'delivered' AND turn_id IS NULL
                      AND session_queue_item_id IS NOT NULL
                      AND NOT EXISTS (
                          SELECT 1 FROM turn_queue queued
                          WHERE queued.queue_item_id = inbound.session_queue_item_id)
                      AND NOT EXISTS (
                          SELECT 1 FROM turns turn
                          WHERE turn.thread_id = inbound.thread_id
                            AND turn.correlation_id = inbound.correlation_id);
                    """;
                Add(command, "$errorCode", ChannelErrorCodes.TurnRemoved);
                Add(command, "$now", now);
                return await command.ExecuteNonQueryAsync(token);
            },
            cancellationToken);
    }

    private async Task<bool> SendOneAsync(
        Guid runtimeInstanceId,
        OutboxDispatch candidate,
        CancellationToken cancellationToken)
    {
        if (!_channels.TryGetValue(candidate.ChannelId, out var channel) ||
            !channel.Enabled ||
            !string.Equals(
                GatewayConfig.ComputeChannelSha256(channel),
                candidate.DefinitionSha256,
                StringComparison.Ordinal))
        {
            return false;
        }

        var sendState = _sendStates.GetOrAdd(
            channel.Id,
            _ => new ChannelSendState(channel.MaxConcurrentSends));
        await sendState.Concurrency.WaitAsync(cancellationToken);
        try
        {
            await WaitForSendIntervalAsync(sendState, channel, cancellationToken);
            var claimTime = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            if (!await ClaimAsync(
                    runtimeInstanceId,
                    candidate.OutboxMessageId,
                    claimTime,
                    cancellationToken))
            {
                return false;
            }

            return await SendClaimedAsync(
                runtimeInstanceId,
                candidate,
                channel,
                claimTime,
                cancellationToken);
        }
        finally
        {
            sendState.Concurrency.Release();
        }
    }

    private async Task<bool> SendClaimedAsync(
        Guid runtimeInstanceId,
        OutboxDispatch candidate,
        GatewayChannelConfig channel,
        long now,
        CancellationToken cancellationToken)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<ChannelOutboundEnvelope>(
                               candidate.EnvelopeJson,
                               JsonOptions)
                           ?? throw new ChannelServiceException(
                               ChannelErrorCodes.DeliveryFailed,
                               "Outbox envelope is invalid.");
            using var activity = OpenCoWorkTelemetry.StartActivity(
                OpenCoWorkTelemetry.GatewayOutboxSend,
                System.Diagnostics.ActivityKind.Producer,
                envelope.CorrelationId,
                envelope.ThreadId,
                envelope.TurnId,
                channelId: channel.Id);
            using var lease = _credentials.Acquire(channel);
            var secret = Encoding.UTF8.GetBytes(lease.Secret!);
            ChannelSendResult result;
            try
            {
                using var renewalLifetime = new CancellationTokenSource();
                var renewal = RenewLeaseWhileSendingAsync(
                    runtimeInstanceId,
                    candidate.OutboxMessageId,
                    renewalLifetime.Token);
                try
                {
                    result = await _sender.SendAsync(
                        new ChannelSendRequest(
                            channel.Id,
                            new Uri(channel.CallbackUrl, UriKind.Absolute),
                            envelope,
                            candidate.BodySha256),
                        secret,
                        cancellationToken);
                }
                finally
                {
                    await renewalLifetime.CancelAsync();
                    await renewal;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secret);
            }

            _faultInjector?.Invoke(GatewayOutboxFaultPoint.SendCompleted);
            if (!result.Succeeded)
            {
                activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error);
                activity?.SetTag(
                    OpenCoWorkTelemetry.ErrorCodeTag,
                    result.ErrorCode ?? ChannelErrorCodes.DeliveryFailed);
                await MarkFailedAsync(
                    runtimeInstanceId,
                    candidate.OutboxMessageId,
                    result.ErrorCode ?? ChannelErrorCodes.DeliveryFailed,
                    result.Retryable,
                    result.RetryAfter,
                    candidate.AttemptCount + 1,
                    now,
                    cancellationToken);
                return true;
            }

            await MarkSentAsync(
                runtimeInstanceId,
                candidate.OutboxMessageId,
                now,
                cancellationToken);
            _faultInjector?.Invoke(GatewayOutboxFaultPoint.SentCommitted);
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            var retryable = error is not ChannelServiceException channelError ||
                            channelError.Retryable ||
                            channelError.Code == ChannelErrorCodes.CredentialUnavailable;
            await MarkFailedAsync(
                runtimeInstanceId,
                candidate.OutboxMessageId,
                error is ChannelServiceException serviceError
                    ? serviceError.Code
                    : ChannelErrorCodes.DeliveryFailed,
                retryable,
                retryAfter: null,
                candidate.AttemptCount + 1,
                now,
                CancellationToken.None);
            return true;
        }
    }

    private async Task RenewLeaseWhileSendingAsync(
        Guid runtimeInstanceId,
        Guid outboxMessageId,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), _timeProvider, cancellationToken);
                var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
                var renewed = await _state.WriteAsync(
                    async (connection, transaction, token) =>
                    {
                        await using var command = connection.CreateCommand();
                        command.Transaction = transaction;
                        command.CommandText =
                            """
                            UPDATE channel_outbox
                            SET lease_expires_utc = $expires, updated_utc = $now
                            WHERE outbox_message_id = $id AND status = 'sending'
                              AND lease_owner_instance_id = $owner;
                            """;
                        Add(
                            command,
                            "$expires",
                            now + (long)TimeSpan.FromMinutes(2).TotalMilliseconds);
                        Add(command, "$now", now);
                        Add(command, "$id", Wire(outboxMessageId));
                        Add(command, "$owner", Wire(runtimeInstanceId));
                        return await command.ExecuteNonQueryAsync(token);
                    },
                    cancellationToken);
                if (renewed == 0)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task WaitForSendIntervalAsync(
        ChannelSendState state,
        GatewayChannelConfig channel,
        CancellationToken cancellationToken)
    {
        if (channel.MinimumSendIntervalMs == 0)
        {
            return;
        }

        await state.Interval.WaitAsync(cancellationToken);
        try
        {
            var now = _timeProvider.GetUtcNow();
            if (state.NextSendAt > now)
            {
                await Task.Delay(state.NextSendAt - now, _timeProvider, cancellationToken);
            }
            state.NextSendAt = _timeProvider.GetUtcNow()
                .AddMilliseconds(channel.MinimumSendIntervalMs);
        }
        finally
        {
            state.Interval.Release();
        }
    }

    private async Task<IReadOnlyList<OutboxDispatch>> ReadSendCandidatesAsync(
        long now,
        int limit,
        CancellationToken cancellationToken) =>
        await _state.ReadAsync<IReadOnlyList<OutboxDispatch>>(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    WITH eligible AS (
                        SELECT current.outbox_message_id, current.channel_id,
                               current.envelope_json, current.body_sha256,
                               current.attempt_count, current.created_utc,
                               ch.definition_sha256,
                               row_number() OVER (
                                   PARTITION BY current.channel_id
                                   ORDER BY current.created_utc, current.outbox_message_id) AS channel_rank
                        FROM channel_outbox current
                        JOIN channels ch ON ch.channel_id = current.channel_id
                    WHERE (
                            (status IN ('pending', 'failed') AND next_attempt_utc <= $now) OR
                            (status = 'sending' AND lease_expires_utc <= $now))
                      AND ch.enabled = 1 AND ch.trust_status = 'trusted'
                      AND ch.runtime_status = 'ready'
                      AND NOT EXISTS (
                          SELECT 1 FROM channel_outbox older
                          WHERE older.channel_id = current.channel_id
                            AND older.external_conversation_id = current.external_conversation_id
                            AND older.partition_sequence < current.partition_sequence
                            AND older.status NOT IN ('sent', 'deadLettered')))
                    SELECT outbox_message_id, channel_id, envelope_json, body_sha256,
                           attempt_count, definition_sha256
                    FROM eligible
                    WHERE channel_rank <= $limit
                    ORDER BY channel_rank, created_utc, outbox_message_id
                    LIMIT $totalLimit;
                    """;
                Add(command, "$now", now);
                Add(command, "$limit", limit);
                Add(command, "$totalLimit", Math.Min(1024, limit * Math.Max(1, _channels.Count)));
                await using var reader = await command.ExecuteReaderAsync(token);
                var result = new List<OutboxDispatch>();
                while (await reader.ReadAsync(token))
                {
                    result.Add(new OutboxDispatch(
                        Guid.Parse(reader.GetString(0)),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetInt32(4),
                        reader.GetString(5)));
                }
                return result;
            },
            cancellationToken);

    private async Task<bool> ClaimAsync(
        Guid runtimeInstanceId,
        Guid outboxMessageId,
        long now,
        CancellationToken cancellationToken) =>
        await _state.WriteAsync(
            async (connection, transaction, token) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    UPDATE channel_outbox
                    SET status = 'sending', attempt_count = attempt_count + 1,
                        lease_owner_instance_id = $owner, lease_expires_utc = $expires,
                        error_code = NULL, diagnostic = NULL,
                        revision = revision + 1, updated_utc = $now
                    WHERE outbox_message_id = $id AND (
                        (status IN ('pending', 'failed') AND next_attempt_utc <= $now) OR
                        (status = 'sending' AND lease_expires_utc <= $now));
                    """;
                Add(command, "$owner", Wire(runtimeInstanceId));
                Add(command, "$expires", now + (long)TimeSpan.FromMinutes(2).TotalMilliseconds);
                Add(command, "$now", now);
                Add(command, "$id", Wire(outboxMessageId));
                return await command.ExecuteNonQueryAsync(token) == 1;
            },
            cancellationToken);

    private async Task MarkSentAsync(
        Guid runtimeInstanceId,
        Guid outboxMessageId,
        long now,
        CancellationToken cancellationToken) =>
        _ = await _state.WriteAsync(
            async (connection, transaction, token) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    UPDATE channel_outbox
                    SET status = 'sent', lease_owner_instance_id = NULL,
                        lease_expires_utc = NULL, error_code = NULL, diagnostic = NULL,
                        revision = revision + 1, updated_utc = $now, sent_utc = $now
                    WHERE outbox_message_id = $id AND status = 'sending'
                      AND lease_owner_instance_id = $owner;
                    """;
                Add(command, "$now", now);
                Add(command, "$id", Wire(outboxMessageId));
                Add(command, "$owner", Wire(runtimeInstanceId));
                await command.ExecuteNonQueryAsync(token);
                return true;
            },
            cancellationToken);

    private async Task MarkFailedAsync(
        Guid runtimeInstanceId,
        Guid outboxMessageId,
        string errorCode,
        bool retryable,
        TimeSpan? retryAfter,
        int attemptCount,
        long now,
        CancellationToken cancellationToken) =>
        _ = await _state.WriteAsync(
            async (connection, transaction, token) =>
            {
                var deadLettered = !retryable || attemptCount >= 5;
                var delay = RetryDelayMilliseconds[Math.Min(attemptCount - 1, 4)];
                if (retryAfter is { } requested)
                {
                    delay = Math.Max(
                        delay,
                        (long)Math.Min(
                            requested.TotalMilliseconds,
                            TimeSpan.FromMinutes(10).TotalMilliseconds));
                }

                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    UPDATE channel_outbox
                    SET status = $status, next_attempt_utc = $nextAttempt,
                        lease_owner_instance_id = NULL, lease_expires_utc = NULL,
                        error_code = $errorCode, diagnostic = 'Outbox delivery failed.',
                        revision = revision + 1, updated_utc = $now
                    WHERE outbox_message_id = $id AND status = 'sending'
                      AND lease_owner_instance_id = $owner;
                    """;
                Add(command, "$status", deadLettered ? "deadLettered" : "failed");
                Add(command, "$nextAttempt", deadLettered ? now : now + delay);
                Add(command, "$errorCode", errorCode);
                Add(command, "$now", now);
                Add(command, "$id", Wire(outboxMessageId));
                Add(command, "$owner", Wire(runtimeInstanceId));
                await command.ExecuteNonQueryAsync(token);
                return true;
            },
            cancellationToken);

    private static ChannelOutboundEnvelope BoundEnvelope(ChannelOutboundEnvelope envelope)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        if (bytes.Length <= MaximumEnvelopeBytes)
        {
            return envelope;
        }

        var text = envelope.Text ?? string.Empty;
        var elements = StringInfo.ParseCombiningCharacters(text);
        var low = 0;
        var high = elements.Length;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            var end = middle == elements.Length ? text.Length : elements[middle];
            var candidate = envelope with
            {
                Text = text[..end],
                Truncated = true,
            };
            if (JsonSerializer.SerializeToUtf8Bytes(candidate, JsonOptions).Length <=
                MaximumEnvelopeBytes)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        var boundedEnd = low == elements.Length ? text.Length : elements[low];
        var bounded = envelope with { Text = text[..boundedEnd], Truncated = true };
        if (JsonSerializer.SerializeToUtf8Bytes(bounded, JsonOptions).Length > MaximumEnvelopeBytes)
        {
            throw new ChannelServiceException(
                ChannelErrorCodes.CapacityExceeded,
                "Outbound envelope exceeds its limit.");
        }
        return bounded;
    }

    private static void RequireVersionSeven(Guid value, string parameterName)
    {
        if (value.Version != 7)
        {
            throw new ArgumentException("Value must be a UUIDv7.", parameterName);
        }
    }

    private static string Wire(Guid value) =>
        value.ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant();

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed class ChannelSendState(int maxConcurrency)
    {
        public SemaphoreSlim Concurrency { get; } = new(maxConcurrency, maxConcurrency);
        public SemaphoreSlim Interval { get; } = new(1, 1);
        public DateTimeOffset NextSendAt { get; set; }
    }

    private sealed record TerminalTurn(
        Guid InboundMessageId,
        string ChannelId,
        string ConversationId,
        string SourceMessageId,
        long PartitionSequence,
        Guid ThreadId,
        Guid TurnId,
        string Status,
        string? ErrorCode,
        long UpdatedUtc,
        Guid CorrelationId,
        string? Text);

    private sealed record OutboxDispatch(
        Guid OutboxMessageId,
        string ChannelId,
        string EnvelopeJson,
        string BodySha256,
        int AttemptCount,
        string DefinitionSha256);
}
