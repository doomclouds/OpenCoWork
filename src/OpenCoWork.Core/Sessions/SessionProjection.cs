using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.State;

namespace OpenCoWork.Core.Sessions;

internal enum ProjectionApplyDisposition
{
    Applied,
    AlreadyApplied,
}

internal enum SessionProjectionFaultPoint
{
    BeforeTransaction,
    BeforeCommit,
    AfterCommit,
}

internal sealed record SessionProjectionCommitResult(
    SessionCommandStatus Status,
    SessionError? Error);

internal sealed record SessionProjectionSnapshot(
    string Sha256,
    int ThreadCount,
    int TurnCount,
    int ItemCount,
    int QueueCount,
    int InteractionCount,
    int IdempotencyCount,
    int ToolInvocationCount);

internal sealed record ThreadJournalSource(
    ThreadJournalLocation Location,
    Guid ThreadId,
    IReadOnlyList<ThreadJournalEntry> Entries);

internal sealed record SessionProjectionRebuildResult(
    IReadOnlyList<Guid> RemovedOrphanThreadIds);

internal sealed record ProjectedIdempotency(
    Guid IdempotencyKey,
    string Operation,
    Guid ThreadId,
    string RequestSha256,
    long CommittedSequence,
    ThreadSnapshot Thread);

internal sealed record SessionDeletionReceipt(
    string ThreadIdSha256,
    string IdempotencyKeySha256,
    long Sequence,
    DateTimeOffset ExpiresAt);

internal sealed class SessionProjectionException : InvalidOperationException
{
    public SessionProjectionException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

internal sealed class SessionProjection
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    private readonly StateRuntime _stateRuntime;
    private readonly Action<SessionProjectionFaultPoint>? _faultInjector;
    private readonly object _statusGate = new();
    private readonly List<ThreadJournalEntry> _pendingEntries = [];
    private SessionProjectionState _state = SessionProjectionState.Ready;

    public SessionProjection(
        StateRuntime stateRuntime,
        Action<SessionProjectionFaultPoint>? faultInjector = null)
    {
        ArgumentNullException.ThrowIfNull(stateRuntime);
        _stateRuntime = stateRuntime;
        _faultInjector = faultInjector;
    }

    public SessionProjectionState State
    {
        get
        {
            lock (_statusGate)
            {
                return _state;
            }
        }
    }

    public bool CanAcceptNewWork => State == SessionProjectionState.Ready;

    public IReadOnlyList<ThreadJournalEntry> PendingEntries
    {
        get
        {
            lock (_statusGate)
            {
                return Array.AsReadOnly(_pendingEntries.ToArray());
            }
        }
    }

    public async Task<ProjectionApplyDisposition> ApplyAsync(
        ThreadJournalEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var disposition = ProjectionApplyDisposition.Applied;
        _faultInjector?.Invoke(SessionProjectionFaultPoint.BeforeTransaction);
        await _stateRuntime.WriteCoordinator.ExecuteAsync(
            async (connection, transaction, token) =>
            {
                disposition = await ApplyCoreAsync(
                    connection,
                    transaction,
                    entry,
                    token);
                _faultInjector?.Invoke(SessionProjectionFaultPoint.BeforeCommit);
            },
            cancellationToken);
        _faultInjector?.Invoke(SessionProjectionFaultPoint.AfterCommit);
        return disposition;
    }

    public async Task<SessionProjectionCommitResult> ApplyCommittedAsync(
        ThreadJournalEntry entry,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await ApplyAsync(entry, cancellationToken);
            return new SessionProjectionCommitResult(
                SessionCommandStatus.Committed,
                null);
        }
        catch
        {
            lock (_statusGate)
            {
                _state = SessionProjectionState.Degraded;
                if (!_pendingEntries.Any(candidate =>
                        candidate.ThreadId == entry.ThreadId &&
                        candidate.Sequence == entry.Sequence))
                {
                    _pendingEntries.Add(entry);
                }
            }

            return new SessionProjectionCommitResult(
                SessionCommandStatus.CommittedPendingProjection,
                new SessionError(
                    SessionErrorCodes.ProjectionUnavailable,
                    "The committed session fact is waiting for projection.",
                    IsRetryable: true));
        }
    }

    public async Task CatchUpAsync(
        IEnumerable<ThreadJournalEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var applied = new HashSet<(Guid ThreadId, long Sequence)>();
        try
        {
            foreach (var entry in entries
                         .OrderBy(candidate => candidate.ThreadId.ToString("D"), StringComparer.Ordinal)
                         .ThenBy(candidate => candidate.Sequence))
            {
                await ApplyAsync(entry, cancellationToken);
                applied.Add((entry.ThreadId, entry.Sequence));
            }

            lock (_statusGate)
            {
                _pendingEntries.RemoveAll(entry =>
                    applied.Contains((entry.ThreadId, entry.Sequence)));
                _state = _pendingEntries.Count == 0
                    ? SessionProjectionState.Ready
                    : SessionProjectionState.Degraded;
            }
        }
        catch
        {
            lock (_statusGate)
            {
                _state = SessionProjectionState.Degraded;
            }

            throw;
        }
    }

    public async Task<SessionProjectionRebuildResult> RebuildAsync(
        IEnumerable<ThreadJournalSource> sources,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var ordered = sources
            .OrderBy(source => source.Location)
            .ThenBy(source => source.ThreadId.ToString("D"), StringComparer.Ordinal)
            .ToArray();
        if (ordered
            .GroupBy(source => source.ThreadId)
            .Any(group => group.Count() != 1))
        {
            throw ProjectionError(
                SessionErrorCodes.JournalCorrupt,
                "A Thread journal exists in more than one lifecycle directory.");
        }

        var sourceIds = ordered.Select(source => source.ThreadId).ToHashSet();
        var existingIds = await ReadThreadIdsAsync(cancellationToken);
        var orphanIds = existingIds
            .Where(threadId => !sourceIds.Contains(threadId))
            .OrderBy(threadId => threadId.ToString("D"), StringComparer.Ordinal)
            .ToArray();

        lock (_statusGate)
        {
            _state = SessionProjectionState.Degraded;
        }

        await _stateRuntime.WriteCoordinator.ExecuteAsync(
            async (connection, transaction, token) =>
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    "DELETE FROM session_idempotency; DELETE FROM threads;",
                    token);
            },
            cancellationToken);

        try
        {
            foreach (var source in ordered)
            {
                foreach (var entry in source.Entries.OrderBy(entry => entry.Sequence))
                {
                    if (entry.ThreadId != source.ThreadId)
                    {
                        throw ProjectionError(
                            SessionErrorCodes.JournalCorrupt,
                            "Projection source contains another Thread ID.");
                    }

                    await ApplyAsync(entry, cancellationToken);
                }
            }

            lock (_statusGate)
            {
                _pendingEntries.Clear();
                _state = SessionProjectionState.Ready;
            }

            return new SessionProjectionRebuildResult(
                Array.AsReadOnly(orphanIds));
        }
        catch
        {
            lock (_statusGate)
            {
                _state = SessionProjectionState.Degraded;
            }

            throw;
        }
    }

    public async Task<SessionProjectionSnapshot> ReadNormalizedSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            await _stateRuntime.OpenReadOnlyConnectionAsync(cancellationToken);
        var tables = new[]
        {
            await ReadRowsAsync(
                connection,
                """
                SELECT quote(thread_id) || '|' || quote(display_name) || '|' ||
                       quote(display_name_search) || '|' || quote(status) || '|' ||
                       quote(availability) || '|' || quote(history_mode) || '|' ||
                       quote(current_sequence) || '|' || quote(last_applied_sequence) || '|' ||
                       quote(active_turn_id) || '|' || quote(first_user_message) || '|' ||
                       quote(first_user_message_search) || '|' || quote(fork_source_thread_id) || '|' ||
                       quote(fork_source_sequence) || '|' || quote(diagnostic) || '|' ||
                       quote(created_utc) || '|' || quote(updated_utc) || '|' ||
                       quote(provider_id) || '|' || quote(model_id) || '|' ||
                       quote(agent_mode)
                FROM threads ORDER BY thread_id;
                """,
                cancellationToken),
            await ReadRowsAsync(
                connection,
                """
                SELECT quote(turn_id) || '|' || quote(thread_id) || '|' ||
                       quote(status) || '|' || quote(error_code) || '|' ||
                       quote(error_message) || '|' || quote(created_utc) || '|' ||
                       quote(updated_utc) || '|' || quote(completed_utc) || '|' ||
                       quote(effective_agent_mode)
                FROM turns ORDER BY turn_id;
                """,
                cancellationToken),
            await ReadRowsAsync(
                connection,
                """
                SELECT quote(item_id) || '|' || quote(thread_id) || '|' ||
                       quote(turn_id) || '|' || quote(sequence) || '|' ||
                       quote(item_type) || '|' || quote(status) || '|' ||
                       quote(payload_json) || '|' || quote(content_text) || '|' ||
                       quote(content_length) || '|' || quote(content_sha256) || '|' ||
                       quote(created_utc) || '|' || quote(updated_utc)
                FROM items ORDER BY item_id;
                """,
                cancellationToken),
            await ReadRowsAsync(
                connection,
                """
                SELECT quote(queue_item_id) || '|' || quote(thread_id) || '|' ||
                       quote(position) || '|' || quote(payload_json) || '|' ||
                       quote(created_utc) || '|' || quote(effective_agent_mode)
                FROM turn_queue ORDER BY thread_id, position, queue_item_id;
                """,
                cancellationToken),
            await ReadRowsAsync(
                connection,
                """
                SELECT quote(interaction_id) || '|' || quote(thread_id) || '|' ||
                       quote(turn_id) || '|' || quote(item_id) || '|' ||
                       quote(interaction_type) || '|' || quote(status) || '|' ||
                       quote(request_json) || '|' || quote(resolution_json) || '|' ||
                       quote(checkpoint_json) || '|' || quote(timeout_utc) || '|' ||
                       quote(created_utc) || '|' || quote(updated_utc)
                FROM pending_interactions ORDER BY interaction_id;
                """,
                cancellationToken),
            await ReadRowsAsync(
                connection,
                """
                SELECT quote(idempotency_key) || '|' || quote(operation) || '|' ||
                       quote(thread_id) || '|' || quote(request_sha256) || '|' ||
                       quote(status) || '|' || quote(result_json) || '|' ||
                       quote(committed_sequence) || '|' || quote(created_utc) || '|' ||
                       quote(updated_utc)
                FROM session_idempotency ORDER BY idempotency_key;
                """,
                cancellationToken),
            await ReadRowsAsync(
                connection,
                """
                SELECT quote(invocation_id) || '|' || quote(thread_id) || '|' ||
                       quote(turn_id) || '|' || quote(snapshot_json) || '|' ||
                       quote(recorded_sequence) || '|' || quote(created_utc)
                FROM agent_invocations ORDER BY invocation_id;
                """,
                cancellationToken),
            await ReadRowsAsync(
                connection,
                """
                SELECT quote(invocation_id) || '|' || quote(attempt_number) || '|' ||
                       quote(purpose) || '|' || quote(thread_id) || '|' ||
                       quote(turn_id) || '|' || quote(usage_json) || '|' ||
                       quote(recorded_sequence) || '|' || quote(created_utc)
                FROM provider_usage
                ORDER BY invocation_id, attempt_number, purpose;
                """,
                cancellationToken),
            await ReadRowsAsync(
                connection,
                """
                SELECT quote(thread_id) || '|' || quote(turn_id) || '|' ||
                       quote(checkpoint_json) || '|' || quote(recorded_sequence) || '|' ||
                       quote(created_utc)
                FROM compaction_checkpoints ORDER BY thread_id;
                """,
                cancellationToken),
            await ReadRowsAsync(
                connection,
                """
                SELECT quote(tool_invocation_id) || '|' || quote(thread_id) || '|' ||
                       quote(turn_id) || '|' || quote(provider_tool_call_id) || '|' ||
                       quote(provider_tool_name) || '|' || quote(tool_definition_id) || '|' ||
                       quote(runtime_binding_id) || '|' || quote(snapshot_sha256) || '|' ||
                       quote(arguments_sha256) || '|' || quote(status) || '|' ||
                       quote(attempt_count) || '|' || quote(result_item_id) || '|' ||
                       quote(error_code) || '|' || quote(started_at) || '|' ||
                       quote(updated_at) || '|' || quote(completed_at)
                FROM tool_invocations ORDER BY tool_invocation_id;
                """,
                cancellationToken),
        };
        var canonical = new StringBuilder();
        for (var index = 0; index < tables.Length; index++)
        {
            canonical.Append(index).Append('\n');
            foreach (var row in tables[index])
            {
                canonical.Append(row).Append('\n');
            }
        }

        return new SessionProjectionSnapshot(
            Hash(Encoding.UTF8.GetBytes(canonical.ToString())),
            tables[0].Count,
            tables[1].Count,
            tables[2].Count,
            tables[3].Count,
            tables[4].Count,
            tables[5].Count,
            tables[9].Count);
    }

    public async Task<ThreadSnapshot?> ReadThreadSnapshotAsync(
        Guid threadId,
        CancellationToken cancellationToken = default)
    {
        SessionIds.RequireVersion7(threadId, nameof(threadId), "Thread ID");
        await using var connection =
            await _stateRuntime.OpenReadOnlyConnectionAsync(cancellationToken);
        return await ReadThreadSnapshotCoreAsync(
            connection,
            transaction: null,
            threadId,
            SessionProjectionState.Ready,
            cancellationToken);
    }

    public async Task<ProjectedIdempotency?> ReadIdempotencyAsync(
        Guid idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        SessionIds.RequireVersion7(
            idempotencyKey,
            nameof(idempotencyKey),
            "Idempotency key");
        await using var connection =
            await _stateRuntime.OpenReadOnlyConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT operation, thread_id, request_sha256,
                   committed_sequence, result_json
            FROM session_idempotency
            WHERE idempotency_key = $key;
            """;
        command.Parameters.AddWithValue("$key", Wire(idempotencyKey));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var operation = reader.GetString(0);
        var threadId = Guid.ParseExact(reader.GetString(1), "D");
        var requestSha256 = reader.GetString(2);
        var sequence = reader.GetInt64(3);
        var receipt = JsonSerializer.Deserialize<ProjectionReceipt>(
            reader.GetString(4),
            JsonOptions);
        if (receipt?.Thread is null)
        {
            throw ProjectionError(
                SessionErrorCodes.ProjectionUnavailable,
                "Projected idempotency result is incomplete.");
        }

        return new ProjectedIdempotency(
            idempotencyKey,
            operation,
            threadId,
            requestSha256,
            sequence,
            receipt.Thread.ToSnapshot());
    }

    public Task MarkDeletingAsync(
        Guid threadId,
        CancellationToken cancellationToken = default) =>
        _stateRuntime.WriteCoordinator.ExecuteAsync(
            async (connection, transaction, token) =>
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    UPDATE threads
                    SET diagnostic = 'session.deleting'
                    WHERE thread_id = $threadId
                      AND status = 'archived'
                      AND active_turn_id IS NULL;
                    """,
                    token,
                    ("$threadId", Wire(threadId)));
            },
            cancellationToken);

    public Task DeleteThreadProjectionAsync(
        Guid threadId,
        CancellationToken cancellationToken = default) =>
        _stateRuntime.WriteCoordinator.ExecuteAsync(
            async (connection, transaction, token) =>
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    "DELETE FROM threads WHERE thread_id = $threadId;",
                    token,
                    ("$threadId", Wire(threadId)));
            },
            cancellationToken);

    public Task WriteDeletionReceiptAsync(
        ThreadDeletionRecoveryIntent intent,
        CancellationToken cancellationToken = default) =>
        _stateRuntime.WriteCoordinator.ExecuteAsync(
            async (connection, transaction, token) =>
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO session_operation_receipts (
                        thread_id_sha256, idempotency_key_sha256,
                        result_json, completed_utc, expires_utc)
                    VALUES (
                        $thread, $key, $result, $completed, $expires)
                    ON CONFLICT (thread_id_sha256, idempotency_key_sha256)
                    DO UPDATE SET
                        result_json = excluded.result_json,
                        completed_utc = excluded.completed_utc,
                        expires_utc = excluded.expires_utc;
                    """,
                    token,
                    ("$thread", intent.ThreadIdSha256),
                    ("$key", intent.IdempotencyKeySha256),
                    ("$result", JsonSerializer.Serialize(
                        new { Deleted = true, intent.Sequence },
                        JsonOptions)),
                    ("$completed", UnixMilliseconds(intent.CompletedAt)),
                    ("$expires", UnixMilliseconds(intent.ExpiresAt)));
            },
            cancellationToken);

    public async Task<SessionDeletionReceipt?> ReadDeletionReceiptAsync(
        string idempotencyKeySha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKeySha256);
        await using var connection =
            await _stateRuntime.OpenReadOnlyConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT thread_id_sha256, idempotency_key_sha256,
                   result_json, expires_utc
            FROM session_operation_receipts
            WHERE idempotency_key_sha256 = $key;
            """;
        command.Parameters.AddWithValue("$key", idempotencyKeySha256);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        using var result = JsonDocument.Parse(reader.GetString(2));
        if (!result.RootElement.TryGetProperty("sequence", out var sequence) ||
            !sequence.TryGetInt64(out var committedSequence))
        {
            throw ProjectionError(
                SessionErrorCodes.ProjectionUnavailable,
                "Deletion receipt result is invalid.");
        }

        return new SessionDeletionReceipt(
            reader.GetString(0),
            reader.GetString(1),
            committedSequence,
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)));
    }

    public Task SaveDeleteReceiptAsync(
        Guid threadId,
        Guid idempotencyKey,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        SessionIds.RequireVersion7(threadId, nameof(threadId), "Thread ID");
        SessionIds.RequireVersion7(
            idempotencyKey,
            nameof(idempotencyKey),
            "Idempotency key");
        var completed = completedAt.ToUniversalTime().ToUnixTimeMilliseconds();
        var expires = completedAt.AddDays(7).ToUniversalTime().ToUnixTimeMilliseconds();
        return _stateRuntime.WriteCoordinator.ExecuteAsync(
            async (connection, transaction, token) =>
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    DELETE FROM session_operation_receipts
                    WHERE expires_utc <= $completed;
                    INSERT OR REPLACE INTO session_operation_receipts (
                        thread_id_sha256, idempotency_key_sha256, result_json,
                        completed_utc, expires_utc)
                    VALUES (
                        $threadHash, $keyHash, '{"deleted":true}',
                        $completed, $expires);
                    """,
                    token,
                    ("$completed", completed),
                    ("$expires", expires),
                    ("$threadHash", HashId(threadId)),
                    ("$keyHash", HashId(idempotencyKey)));
            },
            cancellationToken);
    }

    public async Task<bool> HasDeleteReceiptAsync(
        Guid threadId,
        Guid idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        SessionIds.RequireVersion7(threadId, nameof(threadId), "Thread ID");
        SessionIds.RequireVersion7(
            idempotencyKey,
            nameof(idempotencyKey),
            "Idempotency key");
        await using var connection =
            await _stateRuntime.OpenReadOnlyConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT count(*)
            FROM session_operation_receipts
            WHERE thread_id_sha256 = $threadHash
              AND idempotency_key_sha256 = $keyHash
              AND expires_utc > $now;
            """;
        command.Parameters.AddWithValue("$threadHash", HashId(threadId));
        command.Parameters.AddWithValue("$keyHash", HashId(idempotencyKey));
        command.Parameters.AddWithValue(
            "$now",
            now.ToUniversalTime().ToUnixTimeMilliseconds());
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture) == 1;
    }

    private async Task<ProjectionApplyDisposition> ApplyCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ThreadJournalEntry entry,
        CancellationToken cancellationToken)
    {
        var requestSha256 = ReadRequestSha256(entry);
        var water = await ReadWaterAsync(
            connection,
            transaction,
            entry.ThreadId,
            cancellationToken);
        if (water is null)
        {
            if (entry.Sequence != 1 ||
                entry.EntryType is not (
                    SessionEventType.ThreadCreated or
                    SessionEventType.ThreadForked))
            {
                throw SequenceConflict(entry.Sequence, 0);
            }
        }
        else if (water.Value.CurrentSequence != water.Value.LastAppliedSequence)
        {
            throw ProjectionError(
                SessionErrorCodes.ProjectionUnavailable,
                "Projection sequence watermarks disagree.");
        }
        else if (entry.Sequence <= water.Value.LastAppliedSequence)
        {
            await ValidateAppliedReceiptAsync(
                connection,
                transaction,
                entry,
                requestSha256,
                cancellationToken);
            return ProjectionApplyDisposition.AlreadyApplied;
        }
        else if (entry.Sequence != water.Value.LastAppliedSequence + 1)
        {
            throw SequenceConflict(
                entry.Sequence,
                water.Value.LastAppliedSequence);
        }

        await EnsureIdempotencyKeyAvailableAsync(
            connection,
            transaction,
            entry.IdempotencyKey,
            cancellationToken);
        await ApplyFactAsync(
            connection,
            transaction,
            entry,
            cancellationToken);
        var updated = await ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE threads
            SET current_sequence = $sequence,
                last_applied_sequence = $sequence,
                updated_utc = $updated
            WHERE thread_id = $threadId;
            """,
            cancellationToken,
            ("$sequence", entry.Sequence),
            ("$updated", UnixMilliseconds(entry.Timestamp)),
            ("$threadId", Wire(entry.ThreadId)));
        if (updated != 1)
        {
            throw ProjectionError(
                SessionErrorCodes.InvalidState,
                "Projection did not retain the target thread.");
        }

        var snapshot = await ReadThreadSnapshotCoreAsync(
            connection,
            transaction,
            entry.ThreadId,
            SessionProjectionState.Ready,
            cancellationToken)
            ?? throw ProjectionError(
                SessionErrorCodes.InvalidState,
                "Projection did not retain a readable thread snapshot.");
        await InsertReceiptAsync(
            connection,
            transaction,
            entry,
            requestSha256,
            snapshot,
            cancellationToken);
        return ProjectionApplyDisposition.Applied;
    }

    private static async Task ApplyFactAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ThreadJournalEntry entry,
        CancellationToken cancellationToken)
    {
        switch (entry.EntryType)
        {
            case SessionEventType.ThreadCreated:
                await ApplyThreadCreatedAsync(connection, transaction, entry, cancellationToken);
                return;
            case SessionEventType.ThreadRenamed:
                var renamed = ReadFact<ThreadRenamedFact>(entry);
                ArgumentException.ThrowIfNullOrWhiteSpace(renamed.DisplayName);
                await ExecuteRequiredAsync(
                    connection,
                    transaction,
                    """
                    UPDATE threads
                    SET display_name = $name,
                        display_name_search = $search
                    WHERE thread_id = $threadId AND availability = 'available';
                    """,
                    cancellationToken,
                    ("$name", renamed.DisplayName),
                    ("$search", SearchText(renamed.DisplayName)),
                    ("$threadId", Wire(entry.ThreadId)));
                return;
            case SessionEventType.ThreadModelChanged:
                var model = ReadFact<ThreadModelChangedFact>(entry);
                ArgumentException.ThrowIfNullOrWhiteSpace(model.ProviderId);
                ArgumentException.ThrowIfNullOrWhiteSpace(model.ModelId);
                await ExecuteRequiredAsync(
                    connection,
                    transaction,
                    """
                    UPDATE threads
                    SET provider_id = $providerId,
                        model_id = $modelId
                    WHERE thread_id = $threadId
                      AND status <> 'archived'
                      AND availability = 'available';
                    """,
                    cancellationToken,
                    ("$providerId", model.ProviderId),
                    ("$modelId", model.ModelId),
                    ("$threadId", Wire(entry.ThreadId)));
                return;
            case SessionEventType.ThreadModeChanged:
                var mode = ReadFact<ThreadModeChangedFact>(entry);
                await ExecuteRequiredAsync(
                    connection,
                    transaction,
                    """
                    UPDATE threads
                    SET agent_mode = $mode
                    WHERE thread_id = $threadId
                      AND status <> 'archived'
                      AND availability = 'available';
                    """,
                    cancellationToken,
                    ("$mode", Wire(mode.AgentMode)),
                    ("$threadId", Wire(entry.ThreadId)));
                return;
            case SessionEventType.ThreadPaused:
                await ApplyThreadStatusAsync(
                    connection, transaction, entry, "active", "paused", cancellationToken);
                return;
            case SessionEventType.ThreadResumed:
                await ApplyThreadStatusAsync(
                    connection, transaction, entry, "paused", "active", cancellationToken);
                return;
            case SessionEventType.ThreadArchived:
                await ApplyArchiveAsync(connection, transaction, entry, cancellationToken);
                return;
            case SessionEventType.ThreadUnarchived:
                await ApplyThreadStatusAsync(
                    connection, transaction, entry, "archived", "active", cancellationToken);
                return;
            case SessionEventType.ThreadDeletionRequested:
                await ApplyDeletionRequestedAsync(
                    connection, transaction, entry, cancellationToken);
                return;
            case SessionEventType.ThreadForked:
                await ApplyThreadForkedAsync(connection, transaction, entry, cancellationToken);
                return;
            case SessionEventType.ThreadRolledBack:
                await ApplyThreadRolledBackAsync(
                    connection, transaction, entry, cancellationToken);
                return;
            case SessionEventType.TurnQueued:
                await ApplyTurnQueuedAsync(connection, transaction, entry, cancellationToken);
                return;
            case SessionEventType.TurnQueueChanged:
                await ApplyQueueChangedAsync(connection, transaction, entry, cancellationToken);
                return;
            case SessionEventType.TurnSteered:
                await ApplyTurnSteeredAsync(connection, transaction, entry, cancellationToken);
                return;
            case SessionEventType.TurnStarted:
                await ApplyTurnStartedAsync(connection, transaction, entry, cancellationToken);
                return;
            case SessionEventType.TurnWaitingApproval:
            case SessionEventType.TurnWaitingInput:
                await ApplyTurnWaitingAsync(connection, transaction, entry, cancellationToken);
                return;
            case SessionEventType.InteractionResolved:
                await ApplyInteractionResolvedAsync(
                    connection, transaction, entry, cancellationToken);
                return;
            case SessionEventType.TurnExecutionResumed:
                await ApplyTurnResumedAsync(connection, transaction, entry, cancellationToken);
                return;
            case SessionEventType.TurnCompleted:
            case SessionEventType.TurnFailed:
            case SessionEventType.TurnCancelled:
                await ApplyTurnTerminalAsync(connection, transaction, entry, cancellationToken);
                return;
            case SessionEventType.ItemStarted:
                await ApplyItemStartedAsync(connection, transaction, entry, cancellationToken);
                return;
            case SessionEventType.ItemDeltaAppended:
                await ApplyItemDeltaAsync(connection, transaction, entry, cancellationToken);
                return;
            case SessionEventType.ItemCompleted:
                await ApplyItemCompletedAsync(connection, transaction, entry, cancellationToken);
                return;
            case SessionEventType.ItemFailed:
            case SessionEventType.ItemCancelled:
                await ApplyItemTerminalAsync(connection, transaction, entry, cancellationToken);
                return;
            case SessionEventType.ToolCallRecorded:
                await ApplyToolCallRecordedAsync(
                    connection, transaction, entry, cancellationToken);
                return;
            case SessionEventType.ToolInvocationStarted:
                await ApplyToolInvocationStartedAsync(
                    connection, transaction, entry, cancellationToken);
                return;
            case SessionEventType.ToolInvocationAttemptStarted:
                await ApplyToolInvocationAttemptStartedAsync(
                    connection, transaction, entry, cancellationToken);
                return;
            case SessionEventType.ToolInvocationTerminal:
                await ApplyToolInvocationTerminalAsync(
                    connection, transaction, entry, cancellationToken);
                return;
            case SessionEventType.AgentInvocationSnapshotRecorded:
                await ApplyAgentInvocationAsync(
                    connection, transaction, entry, cancellationToken);
                return;
            case SessionEventType.ProviderUsageRecorded:
                await ApplyProviderUsageAsync(
                    connection, transaction, entry, cancellationToken);
                return;
            case SessionEventType.CompactionCheckpointRecorded:
                await ApplyCompactionCheckpointAsync(
                    connection, transaction, entry, cancellationToken);
                return;
            case SessionEventType.DeferredToolsActivated:
                await ApplyDeferredToolsActivatedAsync(
                    connection, transaction, entry, cancellationToken);
                return;
            case SessionEventType.ThreadJournalRecovered:
                return;
            default:
                throw ProjectionError(
                    SessionErrorCodes.InvalidState,
                    $"Projection for {entry.EntryType} is not implemented yet.");
        }
    }

    private static async Task ApplyThreadCreatedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ThreadJournalEntry entry,
        CancellationToken cancellationToken)
    {
        var fact = ReadFact<ThreadCreatedFact>(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(fact.DisplayName);
        if (fact.HistoryMode != HistoryMode.Server)
        {
            throw ProjectionError(
                SessionErrorCodes.UnsupportedHistoryMode,
                "M2 only supports server-managed history.");
        }
        if (string.IsNullOrWhiteSpace(fact.ProviderId) !=
            string.IsNullOrWhiteSpace(fact.ModelId))
        {
            throw ProjectionError(
                SessionErrorCodes.InvalidState,
                "Thread provider and model must be configured together.");
        }

        await ExecuteRequiredAsync(
            connection,
            transaction,
            """
            INSERT INTO threads (
                thread_id, display_name, display_name_search,
                status, availability, history_mode,
                current_sequence, last_applied_sequence,
                active_turn_id, first_user_message, first_user_message_search,
                fork_source_thread_id, fork_source_sequence, diagnostic,
                created_utc, updated_utc, provider_id, model_id, agent_mode)
            VALUES (
                $threadId, $name, $search,
                'active', 'available', 'server',
                0, 0,
                NULL, $firstMessage, $firstMessageSearch,
                NULL, NULL, NULL,
                $timestamp, $timestamp, $providerId, $modelId, $agentMode);
            """,
            cancellationToken,
            ("$threadId", Wire(entry.ThreadId)),
            ("$name", fact.DisplayName),
            ("$search", SearchText(fact.DisplayName)),
            ("$firstMessage", fact.FirstUserMessage),
            ("$firstMessageSearch", SearchText(fact.FirstUserMessage)),
            ("$providerId", fact.ProviderId),
            ("$modelId", fact.ModelId),
            ("$agentMode", Wire(fact.AgentMode)),
            ("$timestamp", UnixMilliseconds(entry.Timestamp)));
        await UpdateThreadExecutionContextAsync(
            connection,
            transaction,
            entry.ThreadId,
            fact.ExecutionWorkspace,
            fact.CoWorkProvenance,
            fact.AutomationProvenance,
            cancellationToken);
    }

    private static Task ApplyThreadStatusAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ThreadJournalEntry entry,
        string current,
        string next,
        CancellationToken cancellationToken)
    {
        ReadFact<ThreadStateFact>(entry);
        return ExecuteRequiredAsync(
            connection,
            transaction,
            """
            UPDATE threads
            SET status = $next
            WHERE thread_id = $threadId
              AND status = $current
              AND active_turn_id IS NULL
              AND availability = 'available';
            """,
            cancellationToken,
            ("$next", next),
            ("$threadId", Wire(entry.ThreadId)),
            ("$current", current));
    }

    private static async Task ApplyArchiveAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ThreadJournalEntry entry,
        CancellationToken cancellationToken)
    {
        ReadFact<ThreadStateFact>(entry);
        await ExecuteRequiredAsync(
            connection,
            transaction,
            """
            UPDATE threads
            SET status = 'archived'
            WHERE thread_id = $threadId
              AND status IN ('active', 'paused')
              AND active_turn_id IS NULL
              AND availability = 'available'
              AND NOT EXISTS (
                  SELECT 1 FROM turn_queue WHERE thread_id = $threadId);
            """,
            cancellationToken,
            ("$threadId", Wire(entry.ThreadId)));
    }

    private static async Task ApplyDeletionRequestedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ThreadJournalEntry entry,
        CancellationToken cancellationToken)
    {
        var fact = ReadFact<ThreadDeletionRequestedFact>(entry);
        if (!IsLowerSha256(fact.ThreadIdSha256) ||
            !IsLowerSha256(fact.IdempotencyKeySha256) ||
            fact.ExpectedSequence != entry.Sequence - 1)
        {
            throw ProjectionError(
                SessionErrorCodes.InvalidState,
                "Thread deletion fact is invalid.");
        }

        await ExecuteRequiredAsync(
            connection,
            transaction,
            """
            UPDATE threads
            SET diagnostic = 'session.deleting'
            WHERE thread_id = $threadId
              AND status = 'archived'
              AND active_turn_id IS NULL
              AND availability = 'available'
              AND NOT EXISTS (
                  SELECT 1 FROM turn_queue WHERE thread_id = $threadId);
            """,
            cancellationToken,
            ("$threadId", Wire(entry.ThreadId)));
    }

    private static async Task ApplyThreadForkedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ThreadJournalEntry entry,
        CancellationToken cancellationToken)
    {
        var fact = ReadFact<ThreadForkedFact>(entry);
        SessionIds.RequireVersion7(
            fact.SourceThreadId,
            nameof(fact.SourceThreadId),
            "Source thread ID");
        ArgumentException.ThrowIfNullOrWhiteSpace(fact.DisplayName);
        if (fact.SourceSequence < 1 || fact.HistoryMode != HistoryMode.Server)
        {
            throw ProjectionError(
                SessionErrorCodes.InvalidState,
                "Thread fork fact is invalid.");
        }
        if (string.IsNullOrWhiteSpace(fact.ProviderId) !=
            string.IsNullOrWhiteSpace(fact.ModelId))
        {
            throw ProjectionError(
                SessionErrorCodes.InvalidState,
                "Forked thread provider and model must be configured together.");
        }

        await ExecuteRequiredAsync(
            connection,
            transaction,
            """
            INSERT INTO threads (
                thread_id, display_name, display_name_search,
                status, availability, history_mode,
                current_sequence, last_applied_sequence,
                active_turn_id, first_user_message, first_user_message_search,
                fork_source_thread_id, fork_source_sequence, diagnostic,
                created_utc, updated_utc, provider_id, model_id, agent_mode)
            VALUES (
                $threadId, $name, $search,
                'active', 'available', 'server',
                0, 0,
                NULL, NULL, NULL,
                $sourceThreadId, $sourceSequence, NULL,
                $timestamp, $timestamp, $providerId, $modelId, $agentMode);
            """,
            cancellationToken,
            ("$threadId", Wire(entry.ThreadId)),
            ("$name", fact.DisplayName),
            ("$search", SearchText(fact.DisplayName)),
            ("$sourceThreadId", Wire(fact.SourceThreadId)),
            ("$sourceSequence", fact.SourceSequence),
            ("$providerId", fact.ProviderId),
            ("$modelId", fact.ModelId),
            ("$agentMode", Wire(fact.AgentMode)),
            ("$timestamp", UnixMilliseconds(entry.Timestamp)));
        await UpdateThreadExecutionContextAsync(
            connection,
            transaction,
            entry.ThreadId,
            fact.ExecutionWorkspace,
            fact.CoWorkProvenance,
            automationProvenance: null,
            cancellationToken);
        await InsertHistoryCheckpointAsync(
            connection,
            transaction,
            entry.ThreadId,
            fact.History,
            cancellationToken);
    }

    private static async Task ApplyThreadRolledBackAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ThreadJournalEntry entry,
        CancellationToken cancellationToken)
    {
        var fact = ReadFact<ThreadRolledBackFact>(entry);
        if (fact.TargetSequence < 1)
        {
            throw ProjectionError(
                SessionErrorCodes.InvalidState,
                "Thread rollback fact is invalid.");
        }

        await ExecuteRequiredAsync(
            connection,
            transaction,
            """
            UPDATE threads
            SET active_turn_id = NULL
            WHERE thread_id = $threadId
              AND status IN ('active', 'paused')
              AND active_turn_id IS NULL
              AND availability = 'available'
              AND NOT EXISTS (
                  SELECT 1 FROM turn_queue WHERE thread_id = $threadId);
            """,
            cancellationToken,
            ("$threadId", Wire(entry.ThreadId)));
        await ExecuteAsync(
            connection,
            transaction,
            "DELETE FROM turns WHERE thread_id = $threadId;",
            cancellationToken,
            ("$threadId", Wire(entry.ThreadId)));
        await InsertHistoryCheckpointAsync(
            connection,
            transaction,
            entry.ThreadId,
            fact.History,
            cancellationToken);
    }

    private static async Task InsertHistoryCheckpointAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid threadId,
        HistoryCheckpointFact checkpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var turnIds = new HashSet<Guid>();
        foreach (var turn in checkpoint.Turns)
        {
            SessionIds.RequireVersion7(turn.TurnId, nameof(turn.TurnId), "Turn ID");
            if (turn.ThreadId != threadId ||
                turn.Status is not (
                    TurnStatus.Completed or
                    TurnStatus.Failed or
                    TurnStatus.Cancelled) ||
                !turnIds.Add(turn.TurnId))
            {
                throw ProjectionError(
                    SessionErrorCodes.InvalidState,
                    "History checkpoint contains an invalid turn.");
            }

            await ExecuteRequiredAsync(
                connection,
                transaction,
                """
                INSERT INTO turns (
                    turn_id, thread_id, status,
                    error_code, error_message,
                    created_utc, updated_utc, completed_utc,
                    effective_agent_mode)
                VALUES (
                    $turnId, $threadId, $status,
                    $errorCode, $errorMessage,
                    $created, $updated, $completed, $agentMode);
                """,
                cancellationToken,
                ("$turnId", Wire(turn.TurnId)),
                ("$threadId", Wire(threadId)),
                ("$status", Wire(turn.Status)),
                ("$errorCode", turn.Error?.Code),
                ("$errorMessage", turn.Error?.Message),
                ("$agentMode", Wire(turn.EffectiveAgentMode)),
                ("$created", UnixMilliseconds(turn.CreatedAt)),
                ("$updated", UnixMilliseconds(turn.UpdatedAt)),
                ("$completed", turn.CompletedAt is { } completed
                    ? UnixMilliseconds(completed)
                    : (long?)null));
        }

        var itemIds = new HashSet<Guid>();
        foreach (var item in checkpoint.Items)
        {
            SessionIds.RequireVersion7(item.ItemId, nameof(item.ItemId), "Item ID");
            SessionIds.RequireVersion7(item.TurnId, nameof(item.TurnId), "Turn ID");
            if (!turnIds.Contains(item.TurnId) ||
                !itemIds.Add(item.ItemId) ||
                item.Sequence < 1)
            {
                throw ProjectionError(
                    SessionErrorCodes.InvalidState,
                    "History checkpoint contains an invalid item.");
            }

            var bytes = Encoding.UTF8.GetBytes(item.ContentText ?? string.Empty);
            await ExecuteRequiredAsync(
                connection,
                transaction,
                """
                INSERT INTO items (
                    item_id, thread_id, turn_id, sequence,
                    item_type, status, payload_json, content_text,
                    content_length, content_sha256, created_utc, updated_utc)
                VALUES (
                    $itemId, $threadId, $turnId, $sequence,
                    $itemType, $status, $payload, $contentText,
                    $length, $sha256, $created, $updated);
                """,
                cancellationToken,
                ("$itemId", Wire(item.ItemId)),
                ("$threadId", Wire(threadId)),
                ("$turnId", Wire(item.TurnId)),
                ("$sequence", item.Sequence),
                ("$itemType", Wire(item.ItemType)),
                ("$status", Wire(item.Status)),
                ("$payload", item.Content.GetRawText()),
                ("$contentText", item.ContentText),
                ("$length", bytes.Length),
                ("$sha256", Hash(bytes)),
                ("$created", UnixMilliseconds(item.CreatedAt)),
                ("$updated", UnixMilliseconds(item.UpdatedAt)));
        }

        var itemsById = checkpoint.Items.ToDictionary(item => item.ItemId);
        foreach (var item in checkpoint.Items.Where(item =>
                     item.ItemType is
                         SessionItemType.ToolCall or
                         SessionItemType.ToolResult))
        {
            var valid = item.Status == SessionItemStatus.Completed &&
                        (item.ItemType == SessionItemType.ToolCall &&
                         item.Content.Deserialize<ToolCallItemContent>(JsonOptions)
                             is { } toolCall &&
                         IsValidToolCallContent(toolCall) &&
                         HasValidAgentMessageReference(
                             itemsById,
                             item.TurnId,
                             toolCall) ||
                         item.ItemType == SessionItemType.ToolResult &&
                         item.Content.Deserialize<ToolResultItemContent>(JsonOptions)
                             is { } toolResult &&
                         IsValidToolResultContent(toolResult.Result));
            if (!valid)
            {
                throw ProjectionError(
                    SessionErrorCodes.InvalidState,
                    "History checkpoint contains an invalid Tool Item.");
            }
        }

        var invocationCalls = new HashSet<(Guid ItemId, int CallIndex)>();
        var invocationResults = new HashSet<Guid>();
        foreach (var invocation in checkpoint.ToolInvocations ?? [])
        {
            var snapshot = invocation.Snapshot;
            SessionIds.RequireVersion7(
                snapshot.ToolInvocationId,
                nameof(snapshot.ToolInvocationId),
                "Tool Invocation ID");
            if (snapshot.ThreadId != threadId ||
                !turnIds.Contains(snapshot.TurnId) ||
                snapshot.CompletedAt is null ||
                !IsTerminalToolStatus(snapshot.Status) ||
                snapshot.AttemptCount is < 0 or > 2 ||
                !IsLowerSha256(snapshot.SnapshotSha256) ||
                !IsLowerSha256(snapshot.ArgumentsSha256) ||
                (snapshot.ToolDefinitionId is null) !=
                (snapshot.RuntimeBindingId is null) ||
                snapshot.ResultItemId is not { } resultItemId ||
                !itemsById.TryGetValue(
                    invocation.ToolCallItemId,
                    out var callItem) ||
                callItem.ItemType != SessionItemType.ToolCall ||
                callItem.Status != SessionItemStatus.Completed ||
                callItem.TurnId != snapshot.TurnId ||
                callItem.Content.Deserialize<ToolCallItemContent>(JsonOptions)
                    is not { } callContent ||
                !IsValidToolCallContent(callContent) ||
                invocation.CallIndex < 0 ||
                invocation.CallIndex >= callContent.Calls.Count ||
                !string.Equals(
                    callContent.Calls[invocation.CallIndex].ProviderToolCallId,
                    snapshot.ProviderToolCallId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    callContent.Calls[invocation.CallIndex].ProviderToolName,
                    snapshot.ProviderToolName,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    callContent.Calls[invocation.CallIndex].ArgumentsSha256,
                    snapshot.ArgumentsSha256,
                    StringComparison.Ordinal) ||
                !invocationCalls.Add((
                    invocation.ToolCallItemId,
                    invocation.CallIndex)) ||
                !itemsById.TryGetValue(resultItemId, out var resultItem) ||
                resultItem.ItemType != SessionItemType.ToolResult ||
                resultItem.Status != SessionItemStatus.Completed ||
                resultItem.TurnId != snapshot.TurnId ||
                resultItem.Content.Deserialize<ToolResultItemContent>(JsonOptions)
                    is not { } resultContent ||
                !IsValidToolResultContent(resultContent.Result) ||
                resultContent.Result.ToolInvocationId != snapshot.ToolInvocationId ||
                resultContent.Result.Status != snapshot.Status ||
                resultContent.Result.AttemptCount != snapshot.AttemptCount ||
                !string.Equals(
                    resultContent.Result.Error?.Code,
                    snapshot.ErrorCode,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    resultContent.Result.ProviderToolCallId,
                    snapshot.ProviderToolCallId,
                    StringComparison.Ordinal) ||
                !invocationResults.Add(resultItemId))
            {
                throw ProjectionError(
                    SessionErrorCodes.InvalidState,
                    "History checkpoint contains an invalid Tool Invocation.");
            }

            await ExecuteRequiredAsync(
                connection,
                transaction,
                """
                INSERT INTO tool_invocations (
                    tool_invocation_id, thread_id, turn_id,
                    provider_tool_call_id, provider_tool_name,
                    tool_definition_id, runtime_binding_id,
                    snapshot_sha256, arguments_sha256,
                    status, attempt_count, result_item_id, error_code,
                    started_at, updated_at, completed_at)
                VALUES (
                    $invocationId, $threadId, $turnId,
                    $providerCallId, $providerName,
                    $definitionId, $bindingId,
                    $snapshotSha256, $argumentsSha256,
                    $status, $attemptCount, $resultItemId, $errorCode,
                    $startedAt, $updatedAt, $completedAt);
                """,
                cancellationToken,
                ("$invocationId", Wire(snapshot.ToolInvocationId)),
                ("$threadId", Wire(threadId)),
                ("$turnId", Wire(snapshot.TurnId)),
                ("$providerCallId", snapshot.ProviderToolCallId),
                ("$providerName", snapshot.ProviderToolName),
                ("$definitionId", snapshot.ToolDefinitionId is null
                    ? null
                    : JsonSerializer.Serialize(
                        snapshot.ToolDefinitionId,
                        JsonOptions)),
                ("$bindingId", snapshot.RuntimeBindingId?.Value),
                ("$snapshotSha256", snapshot.SnapshotSha256),
                ("$argumentsSha256", snapshot.ArgumentsSha256),
                ("$status", Wire(snapshot.Status)),
                ("$attemptCount", snapshot.AttemptCount),
                ("$resultItemId", Wire(resultItemId)),
                ("$errorCode", snapshot.ErrorCode),
                ("$startedAt", UnixMilliseconds(snapshot.StartedAt)),
                ("$updatedAt", UnixMilliseconds(snapshot.UpdatedAt)),
                ("$completedAt", UnixMilliseconds(snapshot.CompletedAt.Value)));
        }

        if (checkpoint.Items.Any(item =>
                item.ItemType == SessionItemType.ToolResult &&
                !invocationResults.Contains(item.ItemId)))
        {
            throw ProjectionError(
                SessionErrorCodes.InvalidState,
                "History checkpoint contains an orphan Tool Result Item.");
        }
    }

    private static async Task ApplyTurnQueuedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ThreadJournalEntry entry,
        CancellationToken cancellationToken)
    {
        var fact = ReadFact<TurnQueuedFact>(entry);
        SessionIds.RequireVersion7(fact.QueueItemId, nameof(fact.QueueItemId), "Queue item ID");
        ArgumentException.ThrowIfNullOrWhiteSpace(fact.Text);
        await ExecuteRequiredAsync(
            connection,
            transaction,
            """
            INSERT INTO turn_queue (
                queue_item_id, thread_id, position, payload_json, created_utc,
                effective_agent_mode)
            SELECT $itemId, $threadId, $position, $payload, $timestamp, $agentMode
            WHERE EXISTS (
                SELECT 1 FROM threads
                WHERE thread_id = $threadId
                  AND status IN ('active', 'paused')
                  AND availability = 'available')
              AND $position = (
                  SELECT count(*) FROM turn_queue WHERE thread_id = $threadId)
              AND $position < 128;
            """,
            cancellationToken,
            ("$itemId", Wire(fact.QueueItemId)),
            ("$threadId", Wire(entry.ThreadId)),
            ("$position", fact.Position),
            ("$payload", entry.Payload.GetRawText()),
            ("$agentMode", Wire(fact.EffectiveAgentMode)),
            ("$timestamp", UnixMilliseconds(entry.Timestamp)));
        await ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE threads
            SET first_user_message = COALESCE(first_user_message, $text),
                first_user_message_search = COALESCE(first_user_message_search, $search)
            WHERE thread_id = $threadId;
            """,
            cancellationToken,
            ("$text", fact.Text),
            ("$search", SearchText(fact.Text)),
            ("$threadId", Wire(entry.ThreadId)));
    }

    private static async Task ApplyQueueChangedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ThreadJournalEntry entry,
        CancellationToken cancellationToken)
    {
        var fact = ReadFact<TurnQueueChangedFact>(entry);
        foreach (var queueItemId in fact.QueueItemIds)
        {
            SessionIds.RequireVersion7(
                queueItemId,
                nameof(fact.QueueItemIds),
                "Queue item ID");
        }

        if (fact.QueueItemIds.Count != fact.QueueItemIds.Distinct().Count())
        {
            throw ProjectionError(
                SessionErrorCodes.InvalidState,
                "Queue order contains duplicate items.");
        }

        var existing = await ReadStringsAsync(
            connection,
            transaction,
            "SELECT queue_item_id FROM turn_queue WHERE thread_id = $threadId ORDER BY queue_item_id;",
            cancellationToken,
            ("$threadId", Wire(entry.ThreadId)));
        var expected = fact.QueueItemIds
            .Select(Wire)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var expectedBeforeChange = fact.RemovedQueueItemId is { } removedQueueItemId
            ? expected
                .Append(Wire(removedQueueItemId))
                .Order(StringComparer.Ordinal)
                .ToArray()
            : expected;
        if (!existing.SequenceEqual(expectedBeforeChange, StringComparer.Ordinal))
        {
            throw ProjectionError(
                SessionErrorCodes.InvalidState,
                "Queue order does not contain the current queue membership.");
        }

        if (fact.RemovedQueueItemId is { } removed)
        {
            SessionIds.RequireVersion7(
                removed,
                nameof(fact.RemovedQueueItemId),
                "Removed queue item ID");
            await ExecuteRequiredAsync(
                connection,
                transaction,
                """
                DELETE FROM turn_queue
                WHERE thread_id = $threadId AND queue_item_id = $itemId;
                """,
                cancellationToken,
                ("$threadId", Wire(entry.ThreadId)),
                ("$itemId", Wire(removed)));
        }

        await ExecuteAsync(
            connection,
            transaction,
            "UPDATE turn_queue SET position = position + 1000 WHERE thread_id = $threadId;",
            cancellationToken,
            ("$threadId", Wire(entry.ThreadId)));
        for (var index = 0; index < fact.QueueItemIds.Count; index++)
        {
            await ExecuteRequiredAsync(
                connection,
                transaction,
                """
                UPDATE turn_queue
                SET position = $position
                WHERE thread_id = $threadId AND queue_item_id = $itemId;
                """,
                cancellationToken,
                ("$position", index),
                ("$threadId", Wire(entry.ThreadId)),
                ("$itemId", Wire(fact.QueueItemIds[index])));
        }
    }

    private static async Task ApplyTurnStartedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ThreadJournalEntry entry,
        CancellationToken cancellationToken)
    {
        var fact = ReadFact<TurnStartedFact>(entry);
        SessionIds.RequireVersion7(fact.TurnId, nameof(fact.TurnId), "Turn ID");
        var scheduled = fact.QueueItemId is not null ||
                        fact.UserItemId is not null ||
                        fact.Text is not null;
        Guid? correlationId = null;
        if (scheduled &&
            (fact.QueueItemId is null ||
             fact.UserItemId is null ||
             string.IsNullOrWhiteSpace(fact.Text)))
        {
            throw ProjectionError(
                SessionErrorCodes.InvalidState,
                "Scheduled turn input is incomplete.");
        }

        if (scheduled)
        {
            SessionIds.RequireVersion7(
                fact.QueueItemId!.Value,
                nameof(fact.QueueItemId),
                "Queue item ID");
            SessionIds.RequireVersion7(
                fact.UserItemId!.Value,
                nameof(fact.UserItemId),
                "User item ID");
            var queuedInput = await ReadQueueInputAsync(
                connection,
                transaction,
                entry.ThreadId,
                fact.QueueItemId.Value,
                requireHead: true,
                cancellationToken);
            if (!string.Equals(queuedInput.Text, fact.Text, StringComparison.Ordinal))
            {
                throw ProjectionError(
                    SessionErrorCodes.JournalCorrupt,
                    "Scheduled turn text does not match its queued input.");
            }
            if (queuedInput.EffectiveAgentMode != fact.EffectiveAgentMode)
            {
                throw ProjectionError(
                    SessionErrorCodes.JournalCorrupt,
                    "Scheduled turn mode does not match its queued input.");
            }
            correlationId = queuedInput.CorrelationId;

            await ExecuteRequiredAsync(
                connection,
                transaction,
                """
                DELETE FROM turn_queue
                WHERE queue_item_id = $queueItemId
                  AND thread_id = $threadId
                  AND position = 0
                  AND EXISTS (
                      SELECT 1 FROM threads
                      WHERE thread_id = $threadId
                        AND status = 'active'
                        AND availability = 'available'
                        AND active_turn_id IS NULL);
                """,
                cancellationToken,
                ("$queueItemId", Wire(fact.QueueItemId.Value)),
                ("$threadId", Wire(entry.ThreadId)));
            await NormalizeQueueAsync(
                connection,
                transaction,
                entry.ThreadId,
                cancellationToken);
        }

        var timestamp = UnixMilliseconds(entry.Timestamp);
        await ExecuteRequiredAsync(
            connection,
            transaction,
            """
            INSERT INTO turns (
                turn_id, thread_id, status, error_code, error_message,
                created_utc, updated_utc, completed_utc, effective_agent_mode)
            SELECT
                $turnId, $threadId, 'running', NULL, NULL,
                $timestamp, $timestamp, NULL, $agentMode
            WHERE EXISTS (
                SELECT 1 FROM threads
                WHERE thread_id = $threadId
                  AND status = 'active'
                  AND availability = 'available'
                  AND active_turn_id IS NULL
                  AND ($scheduled = 1 OR agent_mode = $agentMode));
            """,
            cancellationToken,
            ("$turnId", Wire(fact.TurnId)),
            ("$threadId", Wire(entry.ThreadId)),
            ("$agentMode", Wire(fact.EffectiveAgentMode)),
            ("$scheduled", scheduled ? 1 : 0),
            ("$timestamp", timestamp));
        if (correlationId is not null)
        {
            await ExecuteRequiredAsync(
                connection,
                transaction,
                "UPDATE turns SET correlation_id = $correlationId WHERE turn_id = $turnId;",
                cancellationToken,
                ("$correlationId", Wire(correlationId.Value)),
                ("$turnId", Wire(fact.TurnId)));
        }
        await ExecuteRequiredAsync(
            connection,
            transaction,
            """
            UPDATE threads
            SET active_turn_id = $turnId
            WHERE thread_id = $threadId AND active_turn_id IS NULL;
            """,
            cancellationToken,
            ("$turnId", Wire(fact.TurnId)),
            ("$threadId", Wire(entry.ThreadId)));
        if (scheduled)
        {
            var text = fact.Text!;
            var bytes = Encoding.UTF8.GetBytes(text);
            await ExecuteRequiredAsync(
                connection,
                transaction,
                """
                INSERT INTO items (
                    item_id, thread_id, turn_id, sequence,
                    item_type, status, payload_json, content_text,
                    content_length, content_sha256, created_utc, updated_utc)
                VALUES (
                    $itemId, $threadId, $turnId, $sequence,
                    'userMessage', 'completed', $payload, $text,
                    $length, $sha256, $timestamp, $timestamp);
                """,
                cancellationToken,
                ("$itemId", Wire(fact.UserItemId!.Value)),
                ("$threadId", Wire(entry.ThreadId)),
                ("$turnId", Wire(fact.TurnId)),
                ("$sequence", entry.Sequence),
                ("$payload", JsonSerializer.Serialize(
                    new TextItemContent(text),
                    JsonOptions)),
                ("$text", text),
                ("$length", bytes.Length),
                ("$sha256", Hash(bytes)),
                ("$timestamp", timestamp));
        }
    }

    private static async Task ApplyTurnSteeredAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ThreadJournalEntry entry,
        CancellationToken cancellationToken)
    {
        var fact = ReadFact<TurnSteeredFact>(entry);
        SessionIds.RequireVersion7(fact.TurnId, nameof(fact.TurnId), "Turn ID");
        SessionIds.RequireVersion7(
            fact.QueueItemId,
            nameof(fact.QueueItemId),
            "Queue item ID");
        SessionIds.RequireVersion7(
            fact.UserItemId,
            nameof(fact.UserItemId),
            "User item ID");
        ArgumentException.ThrowIfNullOrWhiteSpace(fact.Text);
        var queuedInput = await ReadQueueInputAsync(
            connection,
            transaction,
            entry.ThreadId,
            fact.QueueItemId,
            requireHead: false,
            cancellationToken);
        if (!string.Equals(queuedInput.Text, fact.Text, StringComparison.Ordinal))
        {
            throw ProjectionError(
                SessionErrorCodes.JournalCorrupt,
                "Steer text does not match its queued input.");
        }

        await ExecuteRequiredAsync(
            connection,
            transaction,
            """
            DELETE FROM turn_queue
            WHERE queue_item_id = $queueItemId
              AND thread_id = $threadId
              AND EXISTS (
                  SELECT 1 FROM threads
                  WHERE thread_id = $threadId AND active_turn_id = $turnId)
              AND EXISTS (
                  SELECT 1 FROM turns
                  WHERE turn_id = $turnId
                    AND thread_id = $threadId
                    AND status = 'running');
            """,
            cancellationToken,
            ("$queueItemId", Wire(fact.QueueItemId)),
            ("$threadId", Wire(entry.ThreadId)),
            ("$turnId", Wire(fact.TurnId)));
        await NormalizeQueueAsync(
            connection,
            transaction,
            entry.ThreadId,
            cancellationToken);
        var timestamp = UnixMilliseconds(entry.Timestamp);
        var bytes = Encoding.UTF8.GetBytes(fact.Text);
        await ExecuteRequiredAsync(
            connection,
            transaction,
            """
            INSERT INTO items (
                item_id, thread_id, turn_id, sequence,
                item_type, status, payload_json, content_text,
                content_length, content_sha256, created_utc, updated_utc)
            VALUES (
                $itemId, $threadId, $turnId, $sequence,
                'userMessage', 'completed', $payload, $text,
                $length, $sha256, $timestamp, $timestamp);
            """,
            cancellationToken,
            ("$itemId", Wire(fact.UserItemId)),
            ("$threadId", Wire(entry.ThreadId)),
            ("$turnId", Wire(fact.TurnId)),
            ("$sequence", entry.Sequence),
            ("$payload", JsonSerializer.Serialize(
                new TextItemContent(fact.Text),
                JsonOptions)),
            ("$text", fact.Text),
            ("$length", bytes.Length),
            ("$sha256", Hash(bytes)),
            ("$timestamp", timestamp));
    }

    private static async Task NormalizeQueueAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid threadId,
        CancellationToken cancellationToken)
    {
        var queueItemIds = await ReadStringsAsync(
            connection,
            transaction,
            """
            SELECT queue_item_id FROM turn_queue
            WHERE thread_id = $threadId
            ORDER BY position, queue_item_id;
            """,
            cancellationToken,
            ("$threadId", Wire(threadId)));
        await ExecuteAsync(
            connection,
            transaction,
            "UPDATE turn_queue SET position = position + 1000 WHERE thread_id = $threadId;",
            cancellationToken,
            ("$threadId", Wire(threadId)));
        for (var index = 0; index < queueItemIds.Count; index++)
        {
            await ExecuteRequiredAsync(
                connection,
                transaction,
                """
                UPDATE turn_queue SET position = $position
                WHERE thread_id = $threadId AND queue_item_id = $itemId;
                """,
                cancellationToken,
                ("$position", index),
                ("$threadId", Wire(threadId)),
                ("$itemId", queueItemIds[index]));
        }
    }

    private static async Task<TurnQueuedFact> ReadQueueInputAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid threadId,
        Guid queueItemId,
        bool requireHead,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT payload_json FROM turn_queue
            WHERE thread_id = $threadId
              AND queue_item_id = $itemId
              AND ($requireHead = 0 OR position = 0);
            """;
        command.Parameters.AddWithValue("$threadId", Wire(threadId));
        command.Parameters.AddWithValue("$itemId", Wire(queueItemId));
        command.Parameters.AddWithValue("$requireHead", requireHead ? 1 : 0);
        var payload = await command.ExecuteScalarAsync(cancellationToken) as string;
        return payload is null
            ? throw ProjectionError(
                SessionErrorCodes.InvalidState,
                "Queued input is unavailable for this operation.")
            : JsonSerializer.Deserialize<TurnQueuedFact>(payload, JsonOptions)
              ?? throw ProjectionError(
                  SessionErrorCodes.JournalCorrupt,
                  "Queued input payload is invalid.");
    }

    private static async Task ApplyTurnWaitingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ThreadJournalEntry entry,
        CancellationToken cancellationToken)
    {
        var fact = ReadFact<TurnWaitingFact>(entry);
        SessionIds.RequireVersion7(fact.TurnId, nameof(fact.TurnId), "Turn ID");
        SessionIds.RequireVersion7(
            fact.InteractionId,
            nameof(fact.InteractionId),
            "Interaction ID");
        SessionIds.RequireVersion7(fact.ItemId, nameof(fact.ItemId), "Item ID");
        var expectedType = entry.EntryType == SessionEventType.TurnWaitingApproval
            ? SessionInteractionType.Approval
            : SessionInteractionType.UserInput;
        if (fact.InteractionType != expectedType)
        {
            throw ProjectionError(
                SessionErrorCodes.InvalidState,
                "Waiting fact type does not match its event.");
        }
        if (fact.ToolInvocationId is { } toolInvocationId)
        {
            SessionIds.RequireVersion7(
                toolInvocationId,
                nameof(fact.ToolInvocationId),
                "Tool Invocation ID");
            if (expectedType != SessionInteractionType.Approval)
            {
                throw ProjectionError(
                    SessionErrorCodes.InvalidState,
                    "Only an approval can reference a Tool Invocation.");
            }
        }

        var status = expectedType == SessionInteractionType.Approval
            ? "waitingApproval"
            : "waitingInput";
        var expectedItemType = expectedType == SessionInteractionType.Approval
            ? SessionItemType.ApprovalRequest
            : SessionItemType.UserInputRequest;
        if (fact.RequestItemType != expectedItemType ||
            fact.ContentLength < 0 ||
            !IsLowerSha256(fact.ContentSha256) ||
            !ContentMetadataMatches(
                fact.ContentText,
                fact.ContentLength,
                fact.ContentSha256))
        {
            throw ProjectionError(
                SessionErrorCodes.InvalidState,
                "Waiting request item metadata is invalid.");
        }

        var timestamp = UnixMilliseconds(entry.Timestamp);
        await ExecuteRequiredAsync(
            connection,
            transaction,
            """
            UPDATE turns
            SET status = $status, updated_utc = $timestamp
            WHERE turn_id = $turnId
              AND thread_id = $threadId
              AND status = 'running'
              AND ($toolInvocationId IS NULL OR EXISTS (
                SELECT 1 FROM tool_invocations
                WHERE tool_invocation_id = $toolInvocationId
                  AND thread_id = $threadId
                  AND turn_id = $turnId
                  AND completed_at IS NULL));
            """,
            cancellationToken,
            ("$status", status),
            ("$timestamp", timestamp),
            ("$turnId", Wire(fact.TurnId)),
            ("$threadId", Wire(entry.ThreadId)),
            ("$toolInvocationId", fact.ToolInvocationId is null
                ? null
                : Wire(fact.ToolInvocationId.Value)));
        if (fact.ToolInvocationId is { } waitingInvocationId)
        {
            await ExecuteRequiredAsync(
                connection,
                transaction,
                """
                UPDATE tool_invocations
                SET status = 'waitingApproval',
                    updated_at = $timestamp
                WHERE tool_invocation_id = $invocationId
                  AND thread_id = $threadId
                  AND turn_id = $turnId
                  AND status = 'started'
                  AND completed_at IS NULL;
                """,
                cancellationToken,
                ("$timestamp", timestamp),
                ("$invocationId", Wire(waitingInvocationId)),
                ("$threadId", Wire(entry.ThreadId)),
                ("$turnId", Wire(fact.TurnId)));
        }

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT OR IGNORE INTO items (
                item_id, thread_id, turn_id, sequence,
                item_type, status, payload_json, content_text,
                content_length, content_sha256, created_utc, updated_utc)
            VALUES (
                $itemId, $threadId, $turnId, $sequence,
                $itemType, 'completed', $request, $contentText,
                $contentLength, $contentSha256, $timestamp, $timestamp);
            """,
            cancellationToken,
            ("$itemId", Wire(fact.ItemId)),
            ("$threadId", Wire(entry.ThreadId)),
            ("$turnId", Wire(fact.TurnId)),
            ("$sequence", entry.Sequence),
            ("$itemType", Wire(fact.RequestItemType)),
            ("$request", fact.Request.GetRawText()),
            ("$contentText", fact.ContentText),
            ("$contentLength", fact.ContentLength),
            ("$contentSha256", fact.ContentSha256),
            ("$timestamp", timestamp));
        await ExecuteRequiredAsync(
            connection,
            transaction,
            """
            UPDATE items
            SET status = 'completed',
                content_length = $contentLength,
                content_sha256 = $contentSha256,
                updated_utc = $timestamp
            WHERE item_id = $itemId
              AND thread_id = $threadId
              AND turn_id = $turnId
              AND item_type = $itemType
              AND payload_json = $request
              AND COALESCE(content_text, '') = COALESCE($contentText, '')
              AND status IN ('started', 'completed');
            """,
            cancellationToken,
            ("$contentLength", fact.ContentLength),
            ("$contentSha256", fact.ContentSha256),
            ("$timestamp", timestamp),
            ("$itemId", Wire(fact.ItemId)),
            ("$threadId", Wire(entry.ThreadId)),
            ("$turnId", Wire(fact.TurnId)),
            ("$itemType", Wire(fact.RequestItemType)),
            ("$request", fact.Request.GetRawText()),
            ("$contentText", fact.ContentText));
        await ExecuteRequiredAsync(
            connection,
            transaction,
            """
            INSERT INTO pending_interactions (
                interaction_id, thread_id, turn_id, item_id,
                interaction_type, status, request_json, resolution_json,
                checkpoint_json, timeout_utc, created_utc, updated_utc)
            VALUES (
                $interactionId, $threadId, $turnId, $itemId,
                $type, 'pending', $request, NULL,
                $checkpoint, $timeout, $timestamp, $timestamp);
            """,
            cancellationToken,
            ("$interactionId", Wire(fact.InteractionId)),
            ("$threadId", Wire(entry.ThreadId)),
            ("$turnId", Wire(fact.TurnId)),
            ("$itemId", Wire(fact.ItemId)),
            ("$type", Wire(fact.InteractionType)),
            ("$request", fact.Request.GetRawText()),
            ("$checkpoint", JsonSerializer.Serialize(fact.Checkpoint, JsonOptions)),
            ("$timeout", fact.TimeoutAt?.ToUniversalTime().ToUnixTimeMilliseconds()),
            ("$timestamp", timestamp));
    }

    private static async Task ApplyInteractionResolvedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ThreadJournalEntry entry,
        CancellationToken cancellationToken)
    {
        var fact = ReadFact<InteractionResolvedFact>(entry);
        SessionIds.RequireVersion7(
            fact.InteractionId,
            nameof(fact.InteractionId),
            "Interaction ID");
        SessionIds.RequireVersion7(
            fact.ResponseItemId,
            nameof(fact.ResponseItemId),
            "Response item ID");
        if (fact.ContentLength < 0 || !IsLowerSha256(fact.ContentSha256))
        {
            throw ProjectionError(
                SessionErrorCodes.InvalidState,
                "Interaction response content metadata is invalid.");
        }
        if (!ContentMetadataMatches(
                fact.ContentText,
                fact.ContentLength,
                fact.ContentSha256))
        {
            throw ProjectionError(
                SessionErrorCodes.JournalCorrupt,
                "Interaction response content metadata does not match its content.");
        }

        var timestamp = UnixMilliseconds(entry.Timestamp);
        await ExecuteRequiredAsync(
            connection,
            transaction,
            """
            INSERT INTO items (
                item_id, thread_id, turn_id, sequence,
                item_type, status, payload_json, content_text,
                content_length, content_sha256, created_utc, updated_utc)
            SELECT
                $itemId, $threadId, turn_id, $sequence,
                $itemType, 'completed', $resolution, $contentText,
                $contentLength, $contentSha256, $timestamp, $timestamp
            FROM pending_interactions
            WHERE interaction_id = $interactionId
              AND thread_id = $threadId
              AND status = 'pending'
              AND (
                  (interaction_type = 'approval' AND $itemType = 'approvalResponse')
                  OR
                  (interaction_type = 'userInput' AND $itemType = 'userInputResponse'));
            """,
            cancellationToken,
            ("$itemId", Wire(fact.ResponseItemId)),
            ("$threadId", Wire(entry.ThreadId)),
            ("$sequence", entry.Sequence),
            ("$itemType", Wire(fact.ResponseItemType)),
            ("$resolution", fact.Resolution.GetRawText()),
            ("$contentText", fact.ContentText),
            ("$contentLength", fact.ContentLength),
            ("$contentSha256", fact.ContentSha256),
            ("$timestamp", timestamp),
            ("$interactionId", Wire(fact.InteractionId)));
        await ExecuteRequiredAsync(
            connection,
            transaction,
            """
            UPDATE pending_interactions
            SET status = 'resolved',
                resolution_json = $resolution,
                updated_utc = $timestamp
            WHERE interaction_id = $interactionId
              AND thread_id = $threadId
              AND status = 'pending';
            """,
            cancellationToken,
            ("$resolution", fact.Resolution.GetRawText()),
            ("$timestamp", timestamp),
            ("$interactionId", Wire(fact.InteractionId)),
            ("$threadId", Wire(entry.ThreadId)));
    }

    private static Task ApplyTurnResumedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ThreadJournalEntry entry,
        CancellationToken cancellationToken)
    {
        var fact = ReadFact<TurnExecutionResumedFact>(entry);
        SessionIds.RequireVersion7(fact.TurnId, nameof(fact.TurnId), "Turn ID");
        SessionIds.RequireVersion7(
            fact.InteractionId,
            nameof(fact.InteractionId),
            "Interaction ID");
        return ExecuteRequiredAsync(
            connection,
            transaction,
            """
            UPDATE turns
            SET status = 'running', updated_utc = $timestamp
            WHERE turn_id = $turnId
              AND thread_id = $threadId
              AND status IN ('waitingApproval', 'waitingInput')
              AND EXISTS (
                  SELECT 1 FROM pending_interactions
                  WHERE interaction_id = $interactionId
                    AND turn_id = $turnId
                    AND status = 'resolved');
            """,
            cancellationToken,
            ("$timestamp", UnixMilliseconds(entry.Timestamp)),
            ("$turnId", Wire(fact.TurnId)),
            ("$threadId", Wire(entry.ThreadId)),
            ("$interactionId", Wire(fact.InteractionId)));
    }

    private static async Task ApplyTurnTerminalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ThreadJournalEntry entry,
        CancellationToken cancellationToken)
    {
        var fact = ReadFact<TurnTerminalFact>(entry);
        SessionIds.RequireVersion7(fact.TurnId, nameof(fact.TurnId), "Turn ID");
        var status = entry.EntryType switch
        {
            SessionEventType.TurnCompleted => "completed",
            SessionEventType.TurnFailed => "failed",
            _ => "cancelled",
        };
        var timestamp = UnixMilliseconds(entry.Timestamp);
        await ExecuteRequiredAsync(
            connection,
            transaction,
            """
            UPDATE turns
            SET status = $status,
                error_code = $errorCode,
                error_message = $errorMessage,
                updated_utc = $timestamp,
                completed_utc = $timestamp
            WHERE turn_id = $turnId
              AND thread_id = $threadId
              AND status IN ('running', 'waitingApproval', 'waitingInput');
            """,
            cancellationToken,
            ("$status", status),
            ("$errorCode", fact.Error?.Code),
            ("$errorMessage", fact.Error?.Message),
            ("$timestamp", timestamp),
            ("$turnId", Wire(fact.TurnId)),
            ("$threadId", Wire(entry.ThreadId)));
        await ExecuteRequiredAsync(
            connection,
            transaction,
            """
            UPDATE threads
            SET active_turn_id = NULL
            WHERE thread_id = $threadId AND active_turn_id = $turnId;
            """,
            cancellationToken,
            ("$threadId", Wire(entry.ThreadId)),
            ("$turnId", Wire(fact.TurnId)));
    }

    private static Task ApplyItemStartedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ThreadJournalEntry entry,
        CancellationToken cancellationToken)
    {
        var fact = ReadFact<ItemStartedFact>(entry);
        SessionIds.RequireVersion7(fact.ItemId, nameof(fact.ItemId), "Item ID");
        SessionIds.RequireVersion7(fact.TurnId, nameof(fact.TurnId), "Turn ID");
        var timestamp = UnixMilliseconds(entry.Timestamp);
        return ExecuteRequiredAsync(
            connection,
            transaction,
            """
            INSERT INTO items (
                item_id, thread_id, turn_id, sequence,
                item_type, status, payload_json, content_text,
                content_length, content_sha256, created_utc, updated_utc)
            VALUES (
                $itemId, $threadId, $turnId, $sequence,
                $itemType, 'started', $payload, $contentText,
                NULL, NULL, $timestamp, $timestamp);
            """,
            cancellationToken,
            ("$itemId", Wire(fact.ItemId)),
            ("$threadId", Wire(entry.ThreadId)),
            ("$turnId", Wire(fact.TurnId)),
            ("$sequence", entry.Sequence),
            ("$itemType", Wire(fact.ItemType)),
            ("$payload", fact.Content.GetRawText()),
            ("$contentText", fact.ContentText),
            ("$timestamp", timestamp));
    }

    private static Task ApplyItemDeltaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ThreadJournalEntry entry,
        CancellationToken cancellationToken)
    {
        var fact = ReadFact<ItemDeltaFact>(entry);
        SessionIds.RequireVersion7(fact.ItemId, nameof(fact.ItemId), "Item ID");
        return ExecuteRequiredAsync(
            connection,
            transaction,
            """
            UPDATE items
            SET status = 'streaming',
                content_text = COALESCE(content_text, '') || $delta,
                updated_utc = $timestamp
            WHERE item_id = $itemId
              AND thread_id = $threadId
              AND item_type IN ('agentMessage', 'reasoning')
              AND status IN ('started', 'streaming');
            """,
            cancellationToken,
            ("$delta", fact.Delta),
            ("$timestamp", UnixMilliseconds(entry.Timestamp)),
            ("$itemId", Wire(fact.ItemId)),
            ("$threadId", Wire(entry.ThreadId)));
    }

    private static async Task ApplyItemCompletedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ThreadJournalEntry entry,
        CancellationToken cancellationToken)
    {
        var fact = ReadFact<ItemCompletedFact>(entry);
        SessionIds.RequireVersion7(fact.ItemId, nameof(fact.ItemId), "Item ID");
        if (fact.ContentLength < 0 || !IsLowerSha256(fact.ContentSha256))
        {
            throw ProjectionError(
                SessionErrorCodes.InvalidState,
                "Completed item content metadata is invalid.");
        }

        await ValidateCompletedItemAsync(
            connection,
            transaction,
            entry.ThreadId,
            fact,
            cancellationToken);
        await ExecuteRequiredAsync(
            connection,
            transaction,
            """
            UPDATE items
            SET status = 'completed',
                content_length = $length,
                content_sha256 = $sha256,
                updated_utc = $timestamp
            WHERE item_id = $itemId
              AND thread_id = $threadId
              AND status IN ('started', 'streaming');
            """,
            cancellationToken,
            ("$length", fact.ContentLength),
            ("$sha256", fact.ContentSha256),
            ("$timestamp", UnixMilliseconds(entry.Timestamp)),
            ("$itemId", Wire(fact.ItemId)),
            ("$threadId", Wire(entry.ThreadId)));
    }

    private static async Task ValidateCompletedItemAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid threadId,
        ItemCompletedFact fact,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT content_text
            FROM items
            WHERE item_id = $itemId
              AND thread_id = $threadId
              AND status IN ('started', 'streaming');
            """;
        command.Parameters.AddWithValue("$itemId", Wire(fact.ItemId));
        command.Parameters.AddWithValue("$threadId", Wire(threadId));
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null)
        {
            throw ProjectionError(
                SessionErrorCodes.InvalidState,
                "Completed item does not match an active projected item.");
        }

        var text = value == DBNull.Value ? string.Empty : Convert.ToString(value) ?? string.Empty;
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length != fact.ContentLength ||
            !string.Equals(
                Hash(bytes),
                fact.ContentSha256,
                StringComparison.Ordinal))
        {
            throw ProjectionError(
                SessionErrorCodes.JournalCorrupt,
                "Completed item content does not match its final length and digest.");
        }
    }

    private static Task ApplyItemTerminalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ThreadJournalEntry entry,
        CancellationToken cancellationToken)
    {
        var fact = ReadFact<ItemTerminalFact>(entry);
        SessionIds.RequireVersion7(fact.ItemId, nameof(fact.ItemId), "Item ID");
        var status = entry.EntryType == SessionEventType.ItemFailed
            ? "failed"
            : "cancelled";
        return ExecuteRequiredAsync(
            connection,
            transaction,
            """
            UPDATE items
            SET status = $status, updated_utc = $timestamp
            WHERE item_id = $itemId
              AND thread_id = $threadId
              AND status IN ('started', 'streaming');
            """,
            cancellationToken,
            ("$status", status),
            ("$timestamp", UnixMilliseconds(entry.Timestamp)),
            ("$itemId", Wire(fact.ItemId)),
            ("$threadId", Wire(entry.ThreadId)));
    }

    private static Task ApplyToolCallRecordedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ThreadJournalEntry entry,
        CancellationToken cancellationToken)
    {
        var fact = ReadFact<ToolCallRecordedFact>(entry);
        var content = fact.Content.Deserialize<ToolCallItemContent>(JsonOptions);
        if (content is null || !IsValidToolCallContent(content))
        {
            throw ProjectionError(
                SessionErrorCodes.JournalCorrupt,
                "Tool Call Item content is invalid.");
        }

        return InsertCompletedToolItemAsync(
            connection,
            transaction,
            entry,
            fact.ItemId,
            fact.TurnId,
            SessionItemType.ToolCall,
            fact.Content,
            fact.ContentLength,
            fact.ContentSha256,
            cancellationToken,
            content.AgentMessageItemId);
    }

    private static async Task ApplyToolInvocationStartedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ThreadJournalEntry entry,
        CancellationToken cancellationToken)
    {
        var fact = ReadFact<ToolInvocationStartedFact>(entry);
        SessionIds.RequireVersion7(
            fact.ToolInvocationId,
            nameof(fact.ToolInvocationId),
            "Tool Invocation ID");
        SessionIds.RequireVersion7(fact.TurnId, nameof(fact.TurnId), "Turn ID");
        SessionIds.RequireVersion7(
            fact.ToolCallItemId,
            nameof(fact.ToolCallItemId),
            "Tool Call Item ID");
        if (fact.CallIndex < 0 ||
            string.IsNullOrWhiteSpace(fact.ProviderToolCallId) ||
            string.IsNullOrWhiteSpace(fact.ProviderToolName) ||
            (fact.ToolDefinitionId is null) != (fact.RuntimeBindingId is null) ||
            !IsLowerSha256(fact.SnapshotSha256) ||
            !IsLowerSha256(fact.ArgumentsSha256))
        {
            throw ProjectionError(
                SessionErrorCodes.InvalidState,
                "Tool Invocation start is invalid.");
        }

        var toolCall = await ReadToolCallAsync(
            connection,
            transaction,
            entry.ThreadId,
            fact.TurnId,
            fact.ToolCallItemId,
            cancellationToken);
        if (toolCall is null ||
            fact.CallIndex >= toolCall.Calls.Count ||
            !string.Equals(
                toolCall.Calls[fact.CallIndex].ProviderToolCallId,
                fact.ProviderToolCallId,
                StringComparison.Ordinal) ||
            !string.Equals(
                toolCall.Calls[fact.CallIndex].ProviderToolName,
                fact.ProviderToolName,
                StringComparison.Ordinal) ||
            !string.Equals(
                toolCall.Calls[fact.CallIndex].ArgumentsSha256,
                fact.ArgumentsSha256,
                StringComparison.Ordinal))
        {
            throw ProjectionError(
                SessionErrorCodes.JournalCorrupt,
                "Tool Invocation start does not match its Tool Call Item.");
        }

        await ExecuteRequiredAsync(
            connection,
            transaction,
            """
            INSERT INTO tool_invocations (
                tool_invocation_id, thread_id, turn_id,
                provider_tool_call_id, provider_tool_name,
                tool_definition_id, runtime_binding_id,
                snapshot_sha256, arguments_sha256,
                status, attempt_count, result_item_id, error_code,
                started_at, updated_at, completed_at)
            SELECT
                $invocationId, $threadId, $turnId,
                $providerCallId, $providerName,
                $definitionId, $bindingId,
                $snapshotSha256, $argumentsSha256,
                'started', 0, NULL, NULL,
                $timestamp, $timestamp, NULL
            WHERE EXISTS (
                SELECT 1
                FROM items
                WHERE item_id = $toolCallItemId
                  AND thread_id = $threadId
                  AND turn_id = $turnId
                  AND item_type = 'toolCall'
                  AND status = 'completed')
              AND EXISTS (
                SELECT 1 FROM turns
                WHERE turn_id = $turnId
                  AND thread_id = $threadId
                  AND status = 'running');
            """,
            cancellationToken,
            ("$invocationId", Wire(fact.ToolInvocationId)),
            ("$threadId", Wire(entry.ThreadId)),
            ("$turnId", Wire(fact.TurnId)),
            ("$providerCallId", fact.ProviderToolCallId),
            ("$providerName", fact.ProviderToolName),
            ("$definitionId", fact.ToolDefinitionId is null
                ? null
                : JsonSerializer.Serialize(fact.ToolDefinitionId, JsonOptions)),
            ("$bindingId", fact.RuntimeBindingId?.Value),
            ("$snapshotSha256", fact.SnapshotSha256),
            ("$argumentsSha256", fact.ArgumentsSha256),
            ("$toolCallItemId", Wire(fact.ToolCallItemId)),
            ("$timestamp", UnixMilliseconds(entry.Timestamp)));
    }

    private static async Task<ToolCallItemContent?> ReadToolCallAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid threadId,
        Guid turnId,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT payload_json
            FROM items
            WHERE item_id = $itemId
              AND thread_id = $threadId
              AND turn_id = $turnId
              AND item_type = 'toolCall'
              AND status = 'completed';
            """;
        command.Parameters.AddWithValue("$itemId", Wire(itemId));
        command.Parameters.AddWithValue("$threadId", Wire(threadId));
        command.Parameters.AddWithValue("$turnId", Wire(turnId));
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string json
            ? JsonSerializer.Deserialize<ToolCallItemContent>(json, JsonOptions)
            : null;
    }

    private static Task ApplyToolInvocationAttemptStartedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ThreadJournalEntry entry,
        CancellationToken cancellationToken)
    {
        var fact = ReadFact<ToolInvocationAttemptStartedFact>(entry);
        SessionIds.RequireVersion7(
            fact.ToolInvocationId,
            nameof(fact.ToolInvocationId),
            "Tool Invocation ID");
        if (fact.AttemptNumber is < 1 or > 2)
        {
            throw ProjectionError(
                SessionErrorCodes.InvalidState,
                "Tool Invocation attempt is invalid.");
        }

        return ExecuteRequiredAsync(
            connection,
            transaction,
            """
            UPDATE tool_invocations
            SET status = 'started',
                attempt_count = $attempt,
                updated_at = $timestamp
            WHERE tool_invocation_id = $invocationId
              AND thread_id = $threadId
              AND status IN ('started', 'waitingApproval')
              AND completed_at IS NULL
              AND attempt_count = $previousAttempt;
            """,
            cancellationToken,
            ("$attempt", fact.AttemptNumber),
            ("$previousAttempt", fact.AttemptNumber - 1),
            ("$timestamp", UnixMilliseconds(entry.Timestamp)),
            ("$invocationId", Wire(fact.ToolInvocationId)),
            ("$threadId", Wire(entry.ThreadId)));
    }

    private static async Task ApplyToolInvocationTerminalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ThreadJournalEntry entry,
        CancellationToken cancellationToken)
    {
        var fact = ReadFact<ToolInvocationTerminalJournalFact>(entry);
        var terminal = fact.Invocation;
        var item = fact.ResultItem;
        SessionIds.RequireVersion7(
            terminal.ToolInvocationId,
            nameof(terminal.ToolInvocationId),
            "Tool Invocation ID");
        if (terminal.ResultItemId != item.ItemId ||
            !IsTerminalToolStatus(terminal.Status) ||
            !IsLowerSha256(terminal.ResultSha256))
        {
            throw ProjectionError(
                SessionErrorCodes.InvalidState,
                "Tool Invocation terminal is invalid.");
        }

        var content = item.Content.Deserialize<ToolResultItemContent>(JsonOptions);
        if (content?.Result is not { } result ||
            !IsValidToolResultContent(result) ||
            result.ToolInvocationId != terminal.ToolInvocationId ||
            result.Status != terminal.Status ||
            !string.Equals(result.Error?.Code, terminal.ErrorCode, StringComparison.Ordinal) ||
            !string.Equals(
                result.ResultSha256,
                terminal.ResultSha256,
                StringComparison.Ordinal))
        {
            throw ProjectionError(
                SessionErrorCodes.JournalCorrupt,
                "Tool Result Item does not match its terminal summary.");
        }

        await InsertCompletedToolItemAsync(
            connection,
            transaction,
            entry,
            item.ItemId,
            item.TurnId,
            SessionItemType.ToolResult,
            item.Content,
            item.ContentLength,
            item.ContentSha256,
            cancellationToken);
        await ExecuteRequiredAsync(
            connection,
            transaction,
            """
            UPDATE tool_invocations
            SET status = $status,
                result_item_id = $resultItemId,
                error_code = $errorCode,
                updated_at = $timestamp,
                completed_at = $timestamp
            WHERE tool_invocation_id = $invocationId
              AND thread_id = $threadId
              AND turn_id = $turnId
              AND status IN ('started', 'waitingApproval')
              AND provider_tool_call_id = $providerCallId
              AND attempt_count = $attemptCount
              AND result_item_id IS NULL
              AND completed_at IS NULL;
            """,
            cancellationToken,
            ("$status", Wire(terminal.Status)),
            ("$resultItemId", Wire(item.ItemId)),
            ("$errorCode", terminal.ErrorCode),
            ("$providerCallId", result.ProviderToolCallId),
            ("$attemptCount", result.AttemptCount),
            ("$timestamp", UnixMilliseconds(entry.Timestamp)),
            ("$invocationId", Wire(terminal.ToolInvocationId)),
            ("$threadId", Wire(entry.ThreadId)),
            ("$turnId", Wire(item.TurnId)));
    }

    private static Task InsertCompletedToolItemAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ThreadJournalEntry entry,
        Guid itemId,
        Guid turnId,
        SessionItemType itemType,
        JsonElement content,
        int contentLength,
        string contentSha256,
        CancellationToken cancellationToken,
        Guid? agentMessageItemId = null)
    {
        SessionIds.RequireVersion7(itemId, nameof(itemId), "Item ID");
        SessionIds.RequireVersion7(turnId, nameof(turnId), "Turn ID");
        var bytes = ThreadJournal.Canonicalize(content);
        if (itemType is not (SessionItemType.ToolCall or SessionItemType.ToolResult) ||
            contentLength != bytes.Length ||
            !string.Equals(Hash(bytes), contentSha256, StringComparison.Ordinal))
        {
            throw ProjectionError(
                SessionErrorCodes.JournalCorrupt,
                "Completed Tool Item content metadata is invalid.");
        }

        var timestamp = UnixMilliseconds(entry.Timestamp);
        var canonicalContent = Encoding.UTF8.GetString(bytes);
        return ExecuteRequiredAsync(
            connection,
            transaction,
            """
            INSERT INTO items (
                item_id, thread_id, turn_id, sequence,
                item_type, status, payload_json, content_text,
                content_length, content_sha256, created_utc, updated_utc)
            SELECT
                $itemId, $threadId, $turnId, $sequence,
                $itemType, 'completed', $payload, NULL,
                $contentLength, $contentSha256, $timestamp, $timestamp
            WHERE EXISTS (
                SELECT 1 FROM turns
                WHERE turn_id = $turnId
                  AND thread_id = $threadId
                  AND status = 'running')
              AND ($agentMessageItemId IS NULL OR EXISTS (
                SELECT 1 FROM items
                WHERE item_id = $agentMessageItemId
                  AND thread_id = $threadId
                  AND turn_id = $turnId
                  AND item_type = 'agentMessage'
                  AND status = 'completed'));
            """,
            cancellationToken,
            ("$itemId", Wire(itemId)),
            ("$threadId", Wire(entry.ThreadId)),
            ("$turnId", Wire(turnId)),
            ("$sequence", entry.Sequence),
            ("$itemType", Wire(itemType)),
            ("$payload", canonicalContent),
            ("$contentLength", contentLength),
            ("$contentSha256", contentSha256),
            ("$agentMessageItemId", agentMessageItemId is null
                ? null
                : Wire(agentMessageItemId.Value)),
            ("$timestamp", timestamp));
    }

    private static Task ApplyAgentInvocationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ThreadJournalEntry entry,
        CancellationToken cancellationToken)
    {
        var fact = ReadFact<AgentInvocationSnapshotRecordedFact>(entry);
        var snapshot = fact.Snapshot;
        SessionIds.RequireVersion7(fact.TurnId, nameof(fact.TurnId), "Turn ID");
        SessionIds.RequireVersion7(
            snapshot.InvocationId,
            nameof(snapshot.InvocationId),
            "Invocation ID");
        if (string.IsNullOrWhiteSpace(snapshot.ProviderId) ||
            string.IsNullOrWhiteSpace(snapshot.ModelId) ||
            string.IsNullOrWhiteSpace(snapshot.TokenizerProfileId) ||
            string.IsNullOrWhiteSpace(snapshot.TokenizerProfileVersion) ||
            snapshot.ContextWindowTokens <= 0 ||
            snapshot.MaxOutputTokens <= 0 ||
            !IsLowerSha256(snapshot.ConfigurationSha256) ||
            snapshot.CapabilityRevision < 0 ||
            (snapshot.CapabilityRevision > 0 && snapshot.Skills is null) ||
            (snapshot.Skills is { } skills &&
             !IsLowerSha256(skills.SnapshotSha256)))
        {
            throw ProjectionError(
                SessionErrorCodes.InvalidState,
                "Agent invocation snapshot is invalid.");
        }

        return ExecuteRequiredAsync(
            connection,
            transaction,
            """
            INSERT INTO agent_invocations (
                invocation_id, thread_id, turn_id, snapshot_json,
                recorded_sequence, created_utc)
            SELECT
                $invocationId, $threadId, $turnId, $snapshot,
                $sequence, $timestamp
            WHERE EXISTS (
                SELECT 1
                FROM turns
                JOIN threads USING (thread_id)
                WHERE turns.turn_id = $turnId
                  AND turns.thread_id = $threadId
                  AND turns.status = 'running'
                  AND turns.effective_agent_mode = $agentMode
                  AND threads.active_turn_id = $turnId);
            """,
            cancellationToken,
            ("$invocationId", Wire(snapshot.InvocationId)),
            ("$threadId", Wire(entry.ThreadId)),
            ("$turnId", Wire(fact.TurnId)),
            ("$snapshot", JsonSerializer.Serialize(snapshot, JsonOptions)),
            ("$sequence", entry.Sequence),
            ("$timestamp", UnixMilliseconds(entry.Timestamp)),
            ("$agentMode", Wire(snapshot.EffectiveAgentMode)));
    }

    private static Task ApplyProviderUsageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ThreadJournalEntry entry,
        CancellationToken cancellationToken)
    {
        var fact = ReadFact<ProviderUsageRecordedFact>(entry);
        var usage = fact.Usage;
        SessionIds.RequireVersion7(fact.TurnId, nameof(fact.TurnId), "Turn ID");
        SessionIds.RequireVersion7(
            usage.InvocationId,
            nameof(usage.InvocationId),
            "Invocation ID");
        if (usage.AttemptNumber <= 0 ||
            usage.PromptTokens < 0 ||
            usage.CompletionTokens < 0 ||
            usage.TotalTokens < usage.PromptTokens + usage.CompletionTokens ||
            usage.IsEstimate != (usage.Source == ProviderUsageSource.LocalEstimate))
        {
            throw ProjectionError(
                SessionErrorCodes.InvalidState,
                "Provider usage is invalid.");
        }

        return ExecuteRequiredAsync(
            connection,
            transaction,
            """
            INSERT INTO provider_usage (
                invocation_id, attempt_number, purpose, thread_id, turn_id,
                usage_json, recorded_sequence, created_utc)
            SELECT
                $invocationId, $attempt, $purpose, $threadId, $turnId,
                $usage, $sequence, $timestamp
            WHERE EXISTS (
                SELECT 1
                FROM agent_invocations
                JOIN turns USING (turn_id)
                JOIN threads ON threads.thread_id = turns.thread_id
                WHERE agent_invocations.invocation_id = $invocationId
                  AND agent_invocations.turn_id = $turnId
                  AND agent_invocations.thread_id = $threadId
                  AND turns.status = 'running'
                  AND threads.active_turn_id = $turnId);
            """,
            cancellationToken,
            ("$invocationId", Wire(usage.InvocationId)),
            ("$attempt", usage.AttemptNumber),
            ("$purpose", Wire(usage.Purpose)),
            ("$threadId", Wire(entry.ThreadId)),
            ("$turnId", Wire(fact.TurnId)),
            ("$usage", JsonSerializer.Serialize(usage, JsonOptions)),
            ("$sequence", entry.Sequence),
            ("$timestamp", UnixMilliseconds(entry.Timestamp)));
    }

    private static async Task ApplyDeferredToolsActivatedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ThreadJournalEntry entry,
        CancellationToken cancellationToken)
    {
        var fact = ReadFact<DeferredToolsActivatedFact>(entry);
        SessionIds.RequireVersion7(fact.TurnId, nameof(fact.TurnId), "Turn ID");
        if (fact.ToolDefinitionIds.Count is 0 or > 8 ||
            fact.ToolDefinitionIds.Distinct().Count() != fact.ToolDefinitionIds.Count ||
            fact.ToolDefinitionIds.Any(id =>
                !Enum.IsDefined(id.SourceKind) ||
                !IsCleanIdentity(id.SourceId) ||
                !IsCleanIdentity(id.SourceToolId)))
        {
            throw ProjectionError(
                SessionErrorCodes.InvalidState,
                "Deferred Tool activation is invalid.");
        }

        foreach (var definitionId in fact.ToolDefinitionIds)
        {
            await ExecuteRequiredAsync(
                connection,
                transaction,
                """
                INSERT INTO deferred_tool_activations (
                    thread_id, turn_id, tool_definition_id,
                    activated_sequence, activated_utc)
                SELECT
                    $threadId, $turnId, $definitionId,
                    $sequence, $timestamp
                WHERE EXISTS (
                    SELECT 1
                    FROM turns
                    JOIN threads USING (thread_id)
                    WHERE turns.turn_id = $turnId
                      AND turns.thread_id = $threadId
                      AND turns.status = 'running'
                      AND threads.active_turn_id = $turnId);
                """,
                cancellationToken,
                ("$threadId", Wire(entry.ThreadId)),
                ("$turnId", Wire(fact.TurnId)),
                ("$definitionId", JsonSerializer.Serialize(definitionId, JsonOptions)),
                ("$sequence", entry.Sequence),
                ("$timestamp", UnixMilliseconds(entry.Timestamp)));
        }
    }

    private static async Task ApplyCompactionCheckpointAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ThreadJournalEntry entry,
        CancellationToken cancellationToken)
    {
        var fact = ReadFact<CompactionCheckpointRecordedFact>(entry);
        var checkpoint = fact.Checkpoint;
        SessionIds.RequireVersion7(fact.TurnId, nameof(fact.TurnId), "Turn ID");
        if (checkpoint.SchemaVersion <= 0 ||
            string.IsNullOrWhiteSpace(checkpoint.SummaryPromptVersion) ||
            string.IsNullOrWhiteSpace(checkpoint.TokenizerProfileId) ||
            string.IsNullOrWhiteSpace(checkpoint.TokenizerProfileVersion) ||
            checkpoint.SourceStartSequence <= 0 ||
            checkpoint.SourceEndSequence < checkpoint.SourceStartSequence ||
            checkpoint.SummaryTokenCount < 0 ||
            !IsLowerSha256(checkpoint.SourceMessagesSha256) ||
            !string.Equals(
                checkpoint.SummarySha256,
                Hash(Encoding.UTF8.GetBytes(checkpoint.Summary)),
                StringComparison.Ordinal))
        {
            throw ProjectionError(
                SessionErrorCodes.InvalidState,
                "Compaction checkpoint is invalid.");
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT checkpoint_json
                FROM compaction_checkpoints
                WHERE thread_id = $threadId;
                """;
            command.Parameters.AddWithValue("$threadId", Wire(entry.ThreadId));
            if (await command.ExecuteScalarAsync(cancellationToken) is string json)
            {
                var current = JsonSerializer.Deserialize<CompactionCheckpointSnapshot>(
                    json,
                    JsonOptions)
                    ?? throw ProjectionError(
                        SessionErrorCodes.JournalCorrupt,
                        "Projected compaction checkpoint is invalid.");
                if (checkpoint.SourceStartSequence != current.SourceStartSequence ||
                    checkpoint.SourceEndSequence <= current.SourceEndSequence)
                {
                    throw ProjectionError(
                        SessionErrorCodes.InvalidState,
                        "Compaction checkpoint does not extend the authoritative range.");
                }
            }
        }

        await ExecuteRequiredAsync(
            connection,
            transaction,
            """
            INSERT INTO compaction_checkpoints (
                thread_id, turn_id, checkpoint_json, recorded_sequence, created_utc)
            SELECT
                $threadId, $turnId, $checkpoint, $sequence, $timestamp
            WHERE EXISTS (
                SELECT 1
                FROM agent_invocations
                JOIN turns USING (turn_id)
                JOIN threads ON threads.thread_id = turns.thread_id
                WHERE agent_invocations.turn_id = $turnId
                  AND agent_invocations.thread_id = $threadId
                  AND turns.status = 'running'
                  AND threads.active_turn_id = $turnId)
            ON CONFLICT (thread_id) DO UPDATE SET
                turn_id = excluded.turn_id,
                checkpoint_json = excluded.checkpoint_json,
                recorded_sequence = excluded.recorded_sequence,
                created_utc = excluded.created_utc;
            """,
            cancellationToken,
            ("$threadId", Wire(entry.ThreadId)),
            ("$turnId", Wire(fact.TurnId)),
            ("$checkpoint", JsonSerializer.Serialize(checkpoint, JsonOptions)),
            ("$sequence", entry.Sequence),
            ("$timestamp", UnixMilliseconds(entry.Timestamp)));
    }

    private static async Task<(long CurrentSequence, long LastAppliedSequence)?> ReadWaterAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid threadId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT current_sequence, last_applied_sequence
            FROM threads
            WHERE thread_id = $threadId;
            """;
        command.Parameters.AddWithValue("$threadId", Wire(threadId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetInt64(0), reader.GetInt64(1))
            : null;
    }

    private static async Task ValidateAppliedReceiptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ThreadJournalEntry entry,
        string requestSha256,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT operation, thread_id, request_sha256, committed_sequence, result_json
            FROM session_idempotency
            WHERE idempotency_key = $key;
            """;
        command.Parameters.AddWithValue("$key", Wire(entry.IdempotencyKey));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) ||
            !string.Equals(reader.GetString(0), Wire(entry.EntryType), StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(1), Wire(entry.ThreadId), StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(2), requestSha256, StringComparison.Ordinal) ||
            reader.GetInt64(3) != entry.Sequence ||
            !ReceiptMatches(reader.GetString(4), entry))
        {
            throw ProjectionError(
                SessionErrorCodes.IdempotencyConflict,
                "Applied projection receipt does not match the journal entry.");
        }
    }

    private static async Task EnsureIdempotencyKeyAvailableAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid key,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT count(*) FROM session_idempotency
            WHERE idempotency_key = $key;
            """;
        command.Parameters.AddWithValue("$key", Wire(key));
        if (Convert.ToInt64(
                await command.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture) != 0)
        {
            throw ProjectionError(
                SessionErrorCodes.IdempotencyConflict,
                "Idempotency key is already bound to another journal entry.");
        }
    }

    private static Task InsertReceiptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ThreadJournalEntry entry,
        string requestSha256,
        ThreadSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var timestamp = UnixMilliseconds(entry.Timestamp);
        return ExecuteRequiredAsync(
            connection,
            transaction,
            """
            INSERT INTO session_idempotency (
                idempotency_key, operation, thread_id, request_sha256,
                status, result_json, committed_sequence, created_utc, updated_utc)
            VALUES (
                $key, $operation, $threadId, $requestSha256,
                'committed', $result, $sequence, $timestamp, $timestamp);
            """,
            cancellationToken,
            ("$key", Wire(entry.IdempotencyKey)),
            ("$operation", Wire(entry.EntryType)),
            ("$threadId", Wire(entry.ThreadId)),
            ("$requestSha256", requestSha256),
            ("$result", ReceiptJson(entry, snapshot)),
            ("$sequence", entry.Sequence),
            ("$timestamp", timestamp));
    }

    private async Task<IReadOnlyList<Guid>> ReadThreadIdsAsync(
        CancellationToken cancellationToken)
    {
        await using var connection =
            await _stateRuntime.OpenReadOnlyConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT thread_id FROM threads ORDER BY thread_id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<Guid>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(Guid.ParseExact(reader.GetString(0), "D"));
        }

        return result.AsReadOnly();
    }

    private static async Task<IReadOnlyList<string>> ReadRowsAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(reader.GetString(0));
        }

        return rows.AsReadOnly();
    }

    private static async Task<ThreadSnapshot?> ReadThreadSnapshotCoreAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid threadId,
        SessionProjectionState projectionState,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var hasExecutionContext = await HasThreadExecutionContextAsync(
            connection,
            transaction,
            cancellationToken);
        var hasAutomationProvenance = await HasAutomationProvenanceAsync(
            connection,
            transaction,
            cancellationToken);
        command.CommandText =
            $"""
             SELECT display_name, status, availability, history_mode,
                    current_sequence, active_turn_id,
                    created_utc, updated_utc, diagnostic,
                    provider_id, model_id, agent_mode
                    {(hasExecutionContext
                        ? ", execution_workspace_json, cowork_provenance_json"
                        : string.Empty)}
                    {(hasAutomationProvenance
                        ? ", automation_provenance_json"
                        : string.Empty)}
             FROM threads
             WHERE thread_id = $threadId;
             """;
        command.Parameters.AddWithValue("$threadId", Wire(threadId));
        string displayName;
        ThreadStatus status;
        ThreadAvailability availability;
        HistoryMode historyMode;
        long currentSequence;
        Guid? activeTurnId;
        DateTimeOffset createdAt;
        DateTimeOffset updatedAt;
        string? diagnostic;
        string? providerId;
        string? modelId;
        AgentMode agentMode;
        ExecutionWorkspaceDescriptor? executionWorkspace;
        CoWorkThreadProvenance? coWorkProvenance;
        AutomationThreadProvenance? automationProvenance;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            displayName = reader.GetString(0);
            status = ParseWire<ThreadStatus>(reader.GetString(1));
            availability = ParseWire<ThreadAvailability>(reader.GetString(2));
            historyMode = ParseWire<HistoryMode>(reader.GetString(3));
            currentSequence = reader.GetInt64(4);
            activeTurnId = reader.IsDBNull(5)
                ? null
                : Guid.ParseExact(reader.GetString(5), "D");
            createdAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6));
            updatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7));
            diagnostic = reader.IsDBNull(8) ? null : reader.GetString(8);
            providerId = reader.IsDBNull(9) ? null : reader.GetString(9);
            modelId = reader.IsDBNull(10) ? null : reader.GetString(10);
            agentMode = ParseWire<AgentMode>(reader.GetString(11));
            executionWorkspace = hasExecutionContext && !reader.IsDBNull(12)
                ? JsonSerializer.Deserialize<ExecutionWorkspaceDescriptor>(
                    reader.GetString(12),
                    JsonOptions)
                : null;
            coWorkProvenance = hasExecutionContext && !reader.IsDBNull(13)
                ? JsonSerializer.Deserialize<CoWorkThreadProvenance>(
                    reader.GetString(13),
                    JsonOptions)
                : null;
            var automationIndex = hasExecutionContext ? 14 : 12;
            automationProvenance =
                hasAutomationProvenance && !reader.IsDBNull(automationIndex)
                    ? JsonSerializer.Deserialize<AutomationThreadProvenance>(
                        reader.GetString(automationIndex),
                        JsonOptions)
                    : null;
        }

        await using var queueCommand = connection.CreateCommand();
        queueCommand.Transaction = transaction;
        queueCommand.CommandText =
            """
            SELECT queue_item_id, payload_json, position, created_utc,
                   effective_agent_mode
            FROM turn_queue
            WHERE thread_id = $threadId
            ORDER BY position, queue_item_id;
            """;
        queueCommand.Parameters.AddWithValue("$threadId", Wire(threadId));
        await using var queueReader =
            await queueCommand.ExecuteReaderAsync(cancellationToken);
        var queue = new List<QueuedTurnInputSnapshot>();
        while (await queueReader.ReadAsync(cancellationToken))
        {
            var fact = JsonSerializer.Deserialize<TurnQueuedFact>(
                queueReader.GetString(1),
                JsonOptions)
                ?? throw ProjectionError(
                    SessionErrorCodes.ProjectionUnavailable,
                    "Projected queue payload is invalid.");
            queue.Add(new QueuedTurnInputSnapshot(
                Guid.ParseExact(queueReader.GetString(0), "D"),
                threadId,
                fact.Text,
                queueReader.GetInt32(2),
                DateTimeOffset.FromUnixTimeMilliseconds(queueReader.GetInt64(3)),
                ParseWire<AgentMode>(queueReader.GetString(4)),
                fact.CorrelationId));
        }

        return new ThreadSnapshot(
            threadId,
            displayName,
            status,
            availability,
            historyMode,
            currentSequence,
            activeTurnId,
            queue,
            createdAt,
            updatedAt,
            projectionState,
            diagnostic,
            providerId,
            modelId,
            agentMode,
            executionWorkspace,
            coWorkProvenance,
            automationProvenance);
    }

    private static async Task UpdateThreadExecutionContextAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid threadId,
        ExecutionWorkspaceDescriptor? executionWorkspace,
        CoWorkThreadProvenance? coWorkProvenance,
        AutomationThreadProvenance? automationProvenance,
        CancellationToken cancellationToken)
    {
        if (!await HasThreadExecutionContextAsync(
                connection,
                transaction,
                cancellationToken))
        {
            return;
        }

        await ExecuteRequiredAsync(
            connection,
            transaction,
            """
            UPDATE threads
            SET execution_workspace_json = $workspace,
                cowork_provenance_json = $provenance
            WHERE thread_id = $threadId;
            """,
            cancellationToken,
            ("$workspace", executionWorkspace is null
                ? null
                : JsonSerializer.Serialize(executionWorkspace, JsonOptions)),
            ("$provenance", coWorkProvenance is null
                ? null
                : JsonSerializer.Serialize(coWorkProvenance, JsonOptions)),
            ("$threadId", Wire(threadId)));

        if (await HasAutomationProvenanceAsync(
                connection,
                transaction,
                cancellationToken))
        {
            await ExecuteRequiredAsync(
                connection,
                transaction,
                """
                UPDATE threads
                SET automation_provenance_json = $provenance
                WHERE thread_id = $threadId;
                """,
                cancellationToken,
                ("$provenance", automationProvenance is null
                    ? null
                    : JsonSerializer.Serialize(automationProvenance, JsonOptions)),
                ("$threadId", Wire(threadId)));
        }
    }

    private static async Task<bool> HasThreadExecutionContextAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM pragma_table_info('threads')
            WHERE name IN ('execution_workspace_json', 'cowork_provenance_json');
            """;
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture) == 2;
    }

    private static async Task<bool> HasAutomationProvenanceAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM pragma_table_info('threads')
            WHERE name = 'automation_provenance_json';
            """;
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<IReadOnlyList<string>> ReadStringsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        AddParameters(command, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(0));
        }

        return result.AsReadOnly();
    }

    private static async Task ExecuteRequiredAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        if (await ExecuteAsync(
                connection,
                transaction,
                sql,
                cancellationToken,
                parameters) != 1)
        {
            throw ProjectionError(
                SessionErrorCodes.InvalidState,
                "Journal fact does not match the projected state.");
        }
    }

    private static async Task<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        AddParameters(command, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameters(
        SqliteCommand command,
        IEnumerable<(string Name, object? Value)> parameters)
    {
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
    }

    private static T ReadFact<T>(ThreadJournalEntry entry)
    {
        try
        {
            return entry.Payload.Deserialize<T>(JsonOptions)
                ?? throw new JsonException("Journal fact is null.");
        }
        catch (JsonException exception)
        {
            throw new SessionProjectionException(
                SessionErrorCodes.JournalCorrupt,
                $"Journal payload for {entry.EntryType} is invalid: {exception.Message}");
        }
    }

    private static string ReadRequestSha256(ThreadJournalEntry entry)
    {
        if (entry.EntryType == SessionEventType.ThreadJournalRecovered)
        {
            return entry.Checksum;
        }

        if (entry.Payload.ValueKind != JsonValueKind.Object ||
            !entry.Payload.TryGetProperty("requestSha256", out var property))
        {
            throw ProjectionError(
                SessionErrorCodes.JournalCorrupt,
                "Journal fact does not contain a request fingerprint.");
        }

        var value = property.GetString() ?? string.Empty;
        if (!IsLowerSha256(value))
        {
            throw ProjectionError(
                SessionErrorCodes.JournalCorrupt,
                "Journal request fingerprint is invalid.");
        }

        return value;
    }

    private static string ReceiptJson(
        ThreadJournalEntry entry,
        ThreadSnapshot snapshot) =>
        JsonSerializer.Serialize(
            new ProjectionReceipt(
                Wire(entry.EntryId),
                entry.Checksum,
                StoredThreadSnapshot.From(snapshot)),
            JsonOptions);

    private static bool ReceiptMatches(
        string receiptJson,
        ThreadJournalEntry entry)
    {
        try
        {
            using var document = JsonDocument.Parse(receiptJson);
            var root = document.RootElement;
            return string.Equals(
                       root.GetProperty("entryId").GetString(),
                       Wire(entry.EntryId),
                       StringComparison.Ordinal) &&
                   string.Equals(
                       root.GetProperty("checksum").GetString(),
                       entry.Checksum,
                       StringComparison.Ordinal);
        }
        catch (Exception exception) when (
            exception is JsonException
                or KeyNotFoundException
                or InvalidOperationException)
        {
            return false;
        }
    }

    private static long UnixMilliseconds(DateTimeOffset value) =>
        value.ToUniversalTime().ToUnixTimeMilliseconds();

    private static string? SearchText(string? value) =>
        value is null
            ? null
            : value.Normalize(NormalizationForm.FormC).ToUpperInvariant();

    private static T ParseWire<T>(string value)
        where T : struct, Enum
    {
        foreach (var candidate in Enum.GetValues<T>())
        {
            if (string.Equals(Wire(candidate), value, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        throw ProjectionError(
            SessionErrorCodes.ProjectionUnavailable,
            $"Projected {typeof(T).Name} value is invalid.");
    }

    private static string Wire(Guid value) =>
        value.ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant();

    private static string Wire<T>(T value)
        where T : struct, Enum
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static string HashId(Guid value) =>
        Hash(Encoding.UTF8.GetBytes(Wire(value)));

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool IsLowerSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsCleanIdentity(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Any(char.IsControl);

    private static bool IsTerminalToolStatus(ToolInvocationStatus status) =>
        status is
            ToolInvocationStatus.Completed or
            ToolInvocationStatus.Rejected or
            ToolInvocationStatus.Failed or
            ToolInvocationStatus.Cancelled or
            ToolInvocationStatus.TimedOut or
            ToolInvocationStatus.OutcomeUnknown;

    private static bool IsValidToolCallContent(ToolCallItemContent content) =>
        content.Calls.Count > 0 &&
        content.Calls.All(call =>
            !string.IsNullOrWhiteSpace(call.ProviderToolCallId) &&
            !string.IsNullOrWhiteSpace(call.ProviderToolName) &&
            call.Arguments.ValueKind == JsonValueKind.Object &&
            IsLowerSha256(call.ArgumentsSha256)) &&
        content.Calls
            .Select(call => call.ProviderToolCallId)
            .Distinct(StringComparer.Ordinal)
            .Count() == content.Calls.Count;

    private static bool HasValidAgentMessageReference(
        IReadOnlyDictionary<Guid, HistoryCheckpointItemFact> items,
        Guid turnId,
        ToolCallItemContent content) =>
        content.AgentMessageItemId is not { } itemId ||
        items.TryGetValue(itemId, out var item) &&
        item.TurnId == turnId &&
        item.ItemType == SessionItemType.AgentMessage &&
        item.Status == SessionItemStatus.Completed;

    private static bool IsValidToolResultContent(ToolResultSnapshot result) =>
        IsLowerSha256(result.ResultSha256) &&
        (result.Status == ToolInvocationStatus.Completed
            ? result.Output is { ValueKind: not JsonValueKind.Undefined } &&
              result.Error is null
            : IsTerminalToolStatus(result.Status) &&
              result.Output is null &&
              result.Error is { Code.Length: > 0 });

    private static bool ContentMetadataMatches(
        string? content,
        int length,
        string sha256)
    {
        var bytes = Encoding.UTF8.GetBytes(content ?? string.Empty);
        return bytes.Length == length &&
               string.Equals(Hash(bytes), sha256, StringComparison.Ordinal);
    }

    private static SessionProjectionException SequenceConflict(
        long actual,
        long current) =>
        ProjectionError(
            SessionErrorCodes.SequenceConflict,
            $"Projection sequence {actual} does not follow {current}.");

    private static SessionProjectionException ProjectionError(
        string code,
        string message) =>
        new(code, message);

    private sealed record ProjectionReceipt(
        string EntryId,
        string Checksum,
        StoredThreadSnapshot Thread);

    private sealed record StoredThreadSnapshot(
        Guid ThreadId,
        string DisplayName,
        ThreadStatus Status,
        ThreadAvailability Availability,
        HistoryMode HistoryMode,
        long CurrentSequence,
        Guid? ActiveTurnId,
        QueuedTurnInputSnapshot[] Queue,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        SessionProjectionState ProjectionState,
        string? Diagnostic,
        string? ProviderId = null,
        string? ModelId = null,
        AgentMode AgentMode = AgentMode.Agent)
    {
        public static StoredThreadSnapshot From(ThreadSnapshot snapshot) =>
            new(
                snapshot.ThreadId,
                snapshot.DisplayName,
                snapshot.Status,
                snapshot.Availability,
                snapshot.HistoryMode,
                snapshot.CurrentSequence,
                snapshot.ActiveTurnId,
                snapshot.Queue.ToArray(),
                snapshot.CreatedAt,
                snapshot.UpdatedAt,
                snapshot.ProjectionState,
                snapshot.Diagnostic,
                snapshot.ProviderId,
                snapshot.ModelId,
                snapshot.AgentMode);

        public ThreadSnapshot ToSnapshot() =>
            new(
                ThreadId,
                DisplayName,
                Status,
                Availability,
                HistoryMode,
                CurrentSequence,
                ActiveTurnId,
                Queue,
                CreatedAt,
                UpdatedAt,
                ProjectionState,
                Diagnostic,
                ProviderId,
                ModelId,
                AgentMode);
    }
}
