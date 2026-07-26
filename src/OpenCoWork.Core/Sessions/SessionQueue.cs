using System.Collections.Concurrent;
using OpenCoWork.Abstractions;

namespace OpenCoWork.Core.Sessions;

internal sealed partial class SessionService
{
    private const int MaximumQueueLength = 128;
    private readonly ConcurrentDictionary<Guid, QueueIdempotency> _queueIdempotency = [];

    public async Task<SessionCommandResult<QueuedTurnInputSnapshot>> EnqueueInputAsync(
        EnqueueInputRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
        RequireId(request.ThreadId, nameof(request.ThreadId), "Thread ID");
        RequireId(request.IdempotencyKey, nameof(request.IdempotencyKey), "Idempotency key");
        ArgumentOutOfRangeException.ThrowIfNegative(request.ExpectedSequence);
        var operation = Wire(SessionEventType.TurnQueued);
        var requestSha256 = RequestHash(
            operation,
            new
            {
                ThreadId = Wire(request.ThreadId),
                IdempotencyKey = Wire(request.IdempotencyKey),
                request.ExpectedSequence,
                request.Text,
            });
        var keyGate = GetIdempotencyGate(request.IdempotencyKey);
        await keyGate.WaitAsync(cancellationToken);
        SessionCommandResult<QueuedTurnInputSnapshot> result;
        try
        {
            var replay = await TryReplayQueueCommandAsync(
                request.IdempotencyKey,
                operation,
                requestSha256,
                cancellationToken);
            if (replay is not null)
            {
                return replay;
            }

            if (!CanAcceptNewWork)
            {
                return NewWorkUnavailable<QueuedTurnInputSnapshot>();
            }

            var threadGate = GetThreadGate(request.ThreadId);
            await threadGate.WaitAsync(cancellationToken);
            try
            {
                var thread = await GetSnapshotAsync(request.ThreadId, cancellationToken);
                if (thread is null)
                {
                    return Rejected<QueuedTurnInputSnapshot>(
                        SessionErrorCodes.NotFound,
                        "Thread was not found.");
                }

                if (thread.CurrentSequence != request.ExpectedSequence)
                {
                    return Rejected<QueuedTurnInputSnapshot>(
                        SessionErrorCodes.SequenceConflict,
                        "Thread sequence does not match.",
                        thread.CurrentSequence);
                }

                if (thread.Status == ThreadStatus.Archived ||
                    thread.Availability != ThreadAvailability.Available)
                {
                    return Rejected<QueuedTurnInputSnapshot>(
                        SessionErrorCodes.InvalidState,
                        "Thread cannot accept queued input in its current state.",
                        thread.CurrentSequence);
                }

                if (thread.Queue.Count >= MaximumQueueLength)
                {
                    return Rejected<QueuedTurnInputSnapshot>(
                        SessionErrorCodes.QueueFull,
                        "Thread queue already contains 128 inputs.",
                        thread.CurrentSequence);
                }

                var shouldAutoTitle = string.Equals(
                                          thread.DisplayName,
                                          "New thread",
                                          StringComparison.Ordinal) &&
                                      !await HasRenameFactAsync(
                                          thread,
                                          cancellationToken);
                var timestamp = _timeProvider.GetUtcNow();
                var queueItem = new QueuedTurnInputSnapshot(
                    Guid.CreateVersion7(),
                    request.ThreadId,
                    request.Text,
                    thread.Queue.Count,
                    timestamp);
                var queue = thread.Queue.Append(queueItem).ToArray();
                var nextThread = ExecutionThread(
                    thread,
                    thread.CurrentSequence + 1,
                    timestamp,
                    thread.ActiveTurnId,
                    Array.AsReadOnly(queue));
                var committed = await CommitAsync(
                    request.IdempotencyKey,
                    operation,
                    requestSha256,
                    nextThread,
                    new TurnQueuedFact(
                        queueItem.QueueItemId,
                        queueItem.Text,
                        queueItem.Position,
                        requestSha256),
                    SessionEventType.TurnQueued,
                    cancellationToken,
                    new SessionEventPayload(QueueItem: queueItem));
                result = ConvertResult(committed, queueItem);
                if (committed.Status != SessionCommandStatus.Rejected)
                {
                    _queueIdempotency[request.IdempotencyKey] =
                        new QueueIdempotency(operation, requestSha256, result);
                    if (shouldAutoTitle)
                    {
                        var title = AutoTitle(request.Text);
                        if (title.Length != 0)
                        {
                            var titleOperation = InternalRequestHash(
                                SessionEventType.ThreadRenamed,
                                new
                                {
                                    ThreadId = Wire(request.ThreadId),
                                    title,
                                    Automatic = true,
                                });
                            var titledThread = CopySnapshot(
                                nextThread,
                                displayName: title,
                                currentSequence: nextThread.CurrentSequence + 1,
                                updatedAt: timestamp);
                            await CommitAsync(
                                titleOperation.IdempotencyKey,
                                Wire(SessionEventType.ThreadRenamed),
                                titleOperation.RequestSha256,
                                titledThread,
                                new ThreadRenamedFact(
                                    title,
                                    titleOperation.RequestSha256),
                                SessionEventType.ThreadRenamed,
                                CancellationToken.None);
                        }
                    }
                }
            }
            finally
            {
                threadGate.Release();
            }
        }
        finally
        {
            keyGate.Release();
        }

        if (result.Status != SessionCommandStatus.Rejected)
        {
            await TryScheduleNextAsync(request.ThreadId, CancellationToken.None);
        }

        return result;
    }

    private async Task<bool> HasRenameFactAsync(
        ThreadSnapshot thread,
        CancellationToken cancellationToken)
    {
        var replay = await _journal.ReplayAsync(
            thread.Status == ThreadStatus.Archived
                ? ThreadJournalLocation.Archived
                : ThreadJournalLocation.Active,
            thread.ThreadId,
            cancellationToken);
        return replay.Entries.Any(
            entry => entry.EntryType == SessionEventType.ThreadRenamed);
    }

    private static string AutoTitle(string text)
    {
        var value = text.Trim();
        var elements = System.Globalization.StringInfo.ParseCombiningCharacters(value);
        return elements.Length <= 50
            ? value
            : value[..elements[50]];
    }

    public Task<SessionCommandResult<ThreadSnapshot>> RemoveQueuedInputAsync(
        RemoveQueuedInputRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireId(request.QueueItemId, nameof(request.QueueItemId), "Queue item ID");
        return ChangeQueueAsync(
            request.ThreadId,
            request.IdempotencyKey,
            request.ExpectedSequence,
            SessionEventType.TurnQueueChanged,
            new
            {
                QueueItemId = Wire(request.QueueItemId),
            },
            snapshot =>
            {
                var removed = snapshot.Queue.FirstOrDefault(
                    item => item.QueueItemId == request.QueueItemId);
                if (removed is null)
                {
                    return (
                        Error: new SessionError(
                            SessionErrorCodes.QueueItemNotFound,
                            "Queued input was not found.",
                            IsRetryable: false),
                        Queue: snapshot.Queue,
                        Removed: (QueuedTurnInputSnapshot?)null);
                }

                var queue = snapshot.Queue
                    .Where(item => item.QueueItemId != request.QueueItemId)
                    .Select((item, position) => item with { Position = position })
                    .ToArray();
                return (
                    Error: (SessionError?)null,
                    Queue: (IReadOnlyList<QueuedTurnInputSnapshot>)Array.AsReadOnly(queue),
                    Removed: removed);
            },
            cancellationToken);
    }

    public Task<SessionCommandResult<ThreadSnapshot>> ReorderQueuedInputsAsync(
        ReorderQueuedInputsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.QueueItemIds);
        foreach (var queueItemId in request.QueueItemIds)
        {
            RequireId(queueItemId, nameof(request.QueueItemIds), "Queue item ID");
        }

        return ChangeQueueAsync(
            request.ThreadId,
            request.IdempotencyKey,
            request.ExpectedSequence,
            SessionEventType.TurnQueueChanged,
            new
            {
                QueueItemIds = request.QueueItemIds.Select(Wire).ToArray(),
            },
            snapshot =>
            {
                if (request.QueueItemIds.Count != snapshot.Queue.Count ||
                    request.QueueItemIds.Distinct().Count() !=
                    request.QueueItemIds.Count ||
                    !request.QueueItemIds
                        .Order()
                        .SequenceEqual(snapshot.Queue
                            .Select(item => item.QueueItemId)
                            .Order()))
                {
                    return (
                        Error: new SessionError(
                            SessionErrorCodes.InvalidState,
                            "Queue order must contain every current item exactly once.",
                            IsRetryable: false),
                        Queue: snapshot.Queue,
                        Removed: (QueuedTurnInputSnapshot?)null);
                }

                var byId = snapshot.Queue.ToDictionary(item => item.QueueItemId);
                var queue = request.QueueItemIds
                    .Select((itemId, position) =>
                        byId[itemId] with { Position = position })
                    .ToArray();
                return (
                    Error: (SessionError?)null,
                    Queue: (IReadOnlyList<QueuedTurnInputSnapshot>)Array.AsReadOnly(queue),
                    Removed: (QueuedTurnInputSnapshot?)null);
            },
            cancellationToken);
    }

    public async Task<SessionCommandResult<ThreadSnapshot>> SteerTurnAsync(
        SteerTurnRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireId(request.ThreadId, nameof(request.ThreadId), "Thread ID");
        RequireId(request.ExpectedTurnId, nameof(request.ExpectedTurnId), "Turn ID");
        RequireId(request.QueueItemId, nameof(request.QueueItemId), "Queue item ID");
        RequireId(request.IdempotencyKey, nameof(request.IdempotencyKey), "Idempotency key");
        ArgumentOutOfRangeException.ThrowIfNegative(request.ExpectedSequence);
        var operation = Wire(SessionEventType.TurnSteered);
        var requestSha256 = RequestHash(
            operation,
            new
            {
                ThreadId = Wire(request.ThreadId),
                ExpectedTurnId = Wire(request.ExpectedTurnId),
                QueueItemId = Wire(request.QueueItemId),
                IdempotencyKey = Wire(request.IdempotencyKey),
                request.ExpectedSequence,
            });
        var keyGate = GetIdempotencyGate(request.IdempotencyKey);
        await keyGate.WaitAsync(cancellationToken);
        SessionCommandResult<ThreadSnapshot> result;
        SessionItemSnapshot? input = null;
        try
        {
            var replay = await TryReplayThreadCommandAsync(
                request.IdempotencyKey,
                operation,
                requestSha256,
                cancellationToken);
            if (replay is not null)
            {
                return replay;
            }

            if (!CanAcceptNewWork)
            {
                return NewWorkUnavailable<ThreadSnapshot>();
            }

            var threadGate = GetThreadGate(request.ThreadId);
            await threadGate.WaitAsync(cancellationToken);
            try
            {
                await EnsureExecutionStateLoadedAsync(
                    request.ThreadId,
                    cancellationToken);
                var thread = await GetSnapshotAsync(request.ThreadId, cancellationToken);
                var queueItem = thread?.Queue.FirstOrDefault(
                    item => item.QueueItemId == request.QueueItemId);
                if (thread is null ||
                    !_turns.TryGetValue(request.ExpectedTurnId, out var turn))
                {
                    return Rejected<ThreadSnapshot>(
                        SessionErrorCodes.NotFound,
                        "Active turn was not found.");
                }

                if (thread.CurrentSequence != request.ExpectedSequence)
                {
                    return Rejected<ThreadSnapshot>(
                        SessionErrorCodes.SequenceConflict,
                        "Thread sequence does not match.",
                        thread.CurrentSequence);
                }

                if (thread.ActiveTurnId != request.ExpectedTurnId ||
                    turn.Status != TurnStatus.Running)
                {
                    return Rejected<ThreadSnapshot>(
                        SessionErrorCodes.InvalidState,
                        "Expected turn is not running.",
                        thread.CurrentSequence);
                }

                if (queueItem is null)
                {
                    return Rejected<ThreadSnapshot>(
                        SessionErrorCodes.QueueItemNotFound,
                        "Queued input was not found.",
                        thread.CurrentSequence);
                }

                var timestamp = _timeProvider.GetUtcNow();
                input = new SessionItemSnapshot(
                    Guid.CreateVersion7(),
                    turn.TurnId,
                    SessionItemType.UserMessage,
                    SessionItemStatus.Completed,
                    new TextItemContent(queueItem.Text),
                    thread.CurrentSequence + 1,
                    timestamp,
                    timestamp);
                var queue = thread.Queue
                    .Where(item => item.QueueItemId != queueItem.QueueItemId)
                    .Select((item, position) => item with { Position = position })
                    .ToArray();
                var nextThread = ExecutionThread(
                    thread,
                    thread.CurrentSequence + 1,
                    timestamp,
                    thread.ActiveTurnId,
                    Array.AsReadOnly(queue));
                result = await CommitAsync(
                    request.IdempotencyKey,
                    operation,
                    requestSha256,
                    nextThread,
                    new TurnSteeredFact(
                        turn.TurnId,
                        queueItem.QueueItemId,
                        input.ItemId,
                        queueItem.Text,
                        requestSha256),
                    SessionEventType.TurnSteered,
                    cancellationToken,
                    new SessionEventPayload(
                        Turn: turn,
                        Item: input,
                        QueueItem: queueItem));
                if (result.Status != SessionCommandStatus.Rejected)
                {
                    _items[input.ItemId] = input;
                    _itemText[input.ItemId] = queueItem.Text;
                }
            }
            finally
            {
                threadGate.Release();
            }
        }
        finally
        {
            keyGate.Release();
        }

        if (result.Status != SessionCommandStatus.Rejected)
        {
            try
            {
                if (_executor is not ISessionSteerReceiver receiver)
                {
                    throw new InvalidOperationException(
                        "Session executor does not accept steer input.");
                }

                await receiver.SteerAsync(
                    request.ExpectedTurnId,
                    input!,
                    CancellationToken.None);
            }
            catch (Exception exception)
            {
                await StopExecutionWithErrorAsync(
                    request.ThreadId,
                    request.ExpectedTurnId,
                    new SessionError(
                        SessionErrorCodes.RuntimeExecutorUnavailable,
                        $"Executor rejected steer input: {exception.Message}",
                        IsRetryable: true));
            }
        }

        return result;
    }

    internal async Task TryScheduleNextAsync(
        Guid threadId,
        CancellationToken cancellationToken)
    {
        if (_executor is null || string.IsNullOrWhiteSpace(_executorKind))
        {
            return;
        }

        var thread = await GetSnapshotAsync(threadId, cancellationToken);
        if (thread is null ||
            thread.Status != ThreadStatus.Active ||
            thread.Availability != ThreadAvailability.Available ||
            thread.ActiveTurnId is not null ||
            thread.Queue.Count == 0)
        {
            return;
        }

        await StartTurnAsync(
            threadId,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            thread.CurrentSequence,
            cancellationToken,
            queuedInput: thread.Queue[0]);
    }

    private async Task<SessionCommandResult<ThreadSnapshot>> ChangeQueueAsync(
        Guid threadId,
        Guid idempotencyKey,
        long expectedSequence,
        SessionEventType eventType,
        object requestPayload,
        Func<ThreadSnapshot, (
            SessionError? Error,
            IReadOnlyList<QueuedTurnInputSnapshot> Queue,
            QueuedTurnInputSnapshot? Removed)> change,
        CancellationToken cancellationToken)
    {
        RequireId(threadId, nameof(threadId), "Thread ID");
        RequireId(idempotencyKey, nameof(idempotencyKey), "Idempotency key");
        ArgumentOutOfRangeException.ThrowIfNegative(expectedSequence);
        var operation = Wire(eventType);
        var requestSha256 = RequestHash(
            operation,
            new
            {
                ThreadId = Wire(threadId),
                IdempotencyKey = Wire(idempotencyKey),
                ExpectedSequence = expectedSequence,
                Request = requestPayload,
            });
        var keyGate = GetIdempotencyGate(idempotencyKey);
        await keyGate.WaitAsync(cancellationToken);
        try
        {
            var replay = await TryReplayThreadCommandAsync(
                idempotencyKey,
                operation,
                requestSha256,
                cancellationToken);
            if (replay is not null)
            {
                return replay;
            }

            if (!CanAcceptNewWork)
            {
                return NewWorkUnavailable<ThreadSnapshot>();
            }

            var threadGate = GetThreadGate(threadId);
            await threadGate.WaitAsync(cancellationToken);
            try
            {
                var thread = await GetSnapshotAsync(threadId, cancellationToken);
                if (thread is null)
                {
                    return Rejected<ThreadSnapshot>(
                        SessionErrorCodes.NotFound,
                        "Thread was not found.");
                }

                if (thread.CurrentSequence != expectedSequence)
                {
                    return Rejected<ThreadSnapshot>(
                        SessionErrorCodes.SequenceConflict,
                        "Thread sequence does not match.",
                        thread.CurrentSequence);
                }

                if (thread.Availability != ThreadAvailability.Available)
                {
                    return Rejected<ThreadSnapshot>(
                        SessionErrorCodes.RecoveryRequired,
                        "Thread requires recovery.",
                        thread.CurrentSequence);
                }

                var changed = change(thread);
                if (changed.Error is not null)
                {
                    return new SessionCommandResult<ThreadSnapshot>(
                        SessionCommandStatus.Rejected,
                        null,
                        null,
                        thread.CurrentSequence,
                        changed.Error);
                }

                var timestamp = _timeProvider.GetUtcNow();
                var nextThread = ExecutionThread(
                    thread,
                    thread.CurrentSequence + 1,
                    timestamp,
                    thread.ActiveTurnId,
                    changed.Queue);
                return await CommitAsync(
                    idempotencyKey,
                    operation,
                    requestSha256,
                    nextThread,
                    new TurnQueueChangedFact(
                        changed.Queue.Select(item => item.QueueItemId).ToArray(),
                        changed.Removed?.QueueItemId,
                        requestSha256),
                    eventType,
                    cancellationToken,
                    new SessionEventPayload(QueueItem: changed.Removed));
            }
            finally
            {
                threadGate.Release();
            }
        }
        finally
        {
            keyGate.Release();
        }
    }

    private async Task<SessionCommandResult<QueuedTurnInputSnapshot>?>
        TryReplayQueueCommandAsync(
            Guid idempotencyKey,
            string operation,
            string requestSha256,
            CancellationToken cancellationToken)
    {
        if (_queueIdempotency.TryGetValue(idempotencyKey, out var memory))
        {
            return memory.Operation == operation &&
                   memory.RequestSha256 == requestSha256
                ? memory.Result
                : Rejected<QueuedTurnInputSnapshot>(
                    SessionErrorCodes.IdempotencyConflict,
                    "Idempotency key is bound to another request.");
        }

        var match = await _journal.FindByIdempotencyKeyAsync(
            idempotencyKey,
            cancellationToken);
        if (match is null)
        {
            return null;
        }

        if (match.Entry.EntryType != SessionEventType.TurnQueued)
        {
            return Rejected<QueuedTurnInputSnapshot>(
                SessionErrorCodes.IdempotencyConflict,
                "Idempotency key is bound to another request.");
        }

        var fact = ReadFact<TurnQueuedFact>(match.Entry);
        if (!string.Equals(operation, Wire(match.Entry.EntryType), StringComparison.Ordinal) ||
            !string.Equals(requestSha256, fact.RequestSha256, StringComparison.Ordinal))
        {
            return Rejected<QueuedTurnInputSnapshot>(
                SessionErrorCodes.IdempotencyConflict,
                "Idempotency key is bound to another request.");
        }

        var queueItem = new QueuedTurnInputSnapshot(
            fact.QueueItemId,
            match.Entry.ThreadId,
            fact.Text,
            fact.Position,
            match.Entry.Timestamp);
        var result = new SessionCommandResult<QueuedTurnInputSnapshot>(
            SessionCommandStatus.Committed,
            queueItem,
            match.Entry.Sequence,
            null,
            null);
        _queueIdempotency[idempotencyKey] =
            new QueueIdempotency(operation, requestSha256, result);
        return result;
    }

    private async Task StopExecutionWithErrorAsync(
        Guid threadId,
        Guid turnId,
        SessionError error)
    {
        if (_executionRuns.TryGetValue(turnId, out var run))
        {
            await run.StopAsync(
                async deltas =>
                {
                    await FailTurnWithBufferedDeltasAsync(
                        threadId,
                        turnId,
                        deltas,
                        error);
                    return (true, true);
                });
            return;
        }

        await FailTurnAsync(turnId, error, CancellationToken.None);
    }

    private async Task FailTurnWithBufferedDeltasAsync(
        Guid threadId,
        Guid turnId,
        IReadOnlyList<AppendItemDeltaIntent> deltas,
        SessionError error)
    {
        var threadGate = GetThreadGate(threadId);
        await threadGate.WaitAsync();
        try
        {
            var thread = await GetSnapshotAsync(threadId, CancellationToken.None);
            if (thread is null ||
                !_turns.TryGetValue(turnId, out var turn) ||
                turn.Status is TurnStatus.Completed or TurnStatus.Failed or TurnStatus.Cancelled)
            {
                return;
            }

            foreach (var delta in deltas)
            {
                await AppendDeltaAsync(
                    thread,
                    turn,
                    delta,
                    CancellationToken.None);
                thread = _snapshots[threadId];
            }

            await CommitTerminalTurnAsync(
                thread,
                turn,
                SessionEventType.TurnFailed,
                error,
                CancellationToken.None);
        }
        finally
        {
            threadGate.Release();
        }

        await TryScheduleNextAsync(threadId, CancellationToken.None);
    }

    private sealed record QueueIdempotency(
        string Operation,
        string RequestSha256,
        SessionCommandResult<QueuedTurnInputSnapshot> Result);
}
