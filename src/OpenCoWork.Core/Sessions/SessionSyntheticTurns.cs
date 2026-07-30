using System.Security.Cryptography;
using System.Text;
using OpenCoWork.Abstractions;

namespace OpenCoWork.Core.Sessions;

internal sealed partial class SessionService
{
    private const int MaximumSyntheticTurnBytes = 1024 * 1024;

    public async Task<SessionCommandResult<ThreadSnapshot>>
        AppendCompletedAgentTurnAsync(
            AppendCompletedAgentTurnRequest request,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireId(request.ThreadId, nameof(request.ThreadId), "Thread ID");
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DeliveryId);
        ArgumentNullException.ThrowIfNull(request.Text);
        if (Encoding.UTF8.GetByteCount(request.Text) > MaximumSyntheticTurnBytes)
        {
            return Rejected<ThreadSnapshot>(
                SessionErrorCodes.InvalidState,
                "Synthetic Agent Turn exceeds 1 MiB.");
        }

        var observed = await GetThreadAsync(request.ThreadId, cancellationToken);
        if (observed.Value is null)
        {
            return Rejected<ThreadSnapshot>(
                observed.Error?.Code ?? SessionErrorCodes.NotFound,
                observed.Error?.Message ?? "Thread was not found.");
        }

        if (observed.Value.Status == ThreadStatus.Archived)
        {
            var unarchived = await UnarchiveThreadAsync(
                new ThreadMutationRequest(
                    request.ThreadId,
                    SyntheticId(request.DeliveryId, "unarchive"),
                    observed.Value.CurrentSequence),
                cancellationToken);
            if (unarchived.Value is null)
            {
                return unarchived;
            }
        }

        var threadGate = GetThreadGate(request.ThreadId);
        await threadGate.WaitAsync(cancellationToken);
        try
        {
            var thread = await GetSnapshotAsync(request.ThreadId, cancellationToken);
            if (thread is null)
            {
                return Rejected<ThreadSnapshot>(
                    SessionErrorCodes.NotFound,
                    "Thread was not found.");
            }

            await EnsureExecutionStateLoadedAsync(request.ThreadId, cancellationToken);
            if (thread.Status != ThreadStatus.Active ||
                thread.Availability != ThreadAvailability.Available)
            {
                return Rejected<ThreadSnapshot>(
                    SessionErrorCodes.InvalidState,
                    "Thread cannot accept a completed Agent Turn.",
                    thread.CurrentSequence);
            }

            var turnId = SyntheticId(request.DeliveryId, "turn");
            var itemId = SyntheticId(request.DeliveryId, "item");
            if (_turns.TryGetValue(turnId, out var existing) &&
                existing.Status == TurnStatus.Completed)
            {
                return new SessionCommandResult<ThreadSnapshot>(
                    SessionCommandStatus.Committed,
                    thread,
                    thread.CurrentSequence,
                    null,
                    null);
            }

            if (thread.ActiveTurnId is not null)
            {
                return new SessionCommandResult<ThreadSnapshot>(
                    SessionCommandStatus.Rejected,
                    null,
                    null,
                    thread.CurrentSequence,
                    new SessionError(
                        SessionErrorCodes.ThreadBusy,
                        "Thread is busy.",
                        IsRetryable: true));
            }

            if (!_turns.TryGetValue(turnId, out var turn))
            {
                var timestamp = _timeProvider.GetUtcNow();
                turn = new TurnSnapshot(
                    turnId,
                    request.ThreadId,
                    TurnStatus.Running,
                    timestamp,
                    timestamp,
                    CompletedAt: null,
                    Error: null);
                var operation = SyntheticOperation(
                    request,
                    "turn-started",
                    SessionEventType.TurnStarted);
                var committed = await CommitAsync(
                    operation.IdempotencyKey,
                    operation.Name,
                    operation.RequestSha256,
                    ExecutionThread(
                        thread,
                        thread.CurrentSequence + 1,
                        timestamp,
                        turnId),
                    new TurnStartedFact(
                        turnId,
                        QueueItemId: null,
                        UserItemId: null,
                        Text: null,
                        operation.RequestSha256),
                    SessionEventType.TurnStarted,
                    cancellationToken,
                    new SessionEventPayload(Turn: turn));
                if (committed.Value is null)
                {
                    return committed;
                }

                thread = committed.Value;
                _turns[turnId] = turn;
            }

            if (!_items.TryGetValue(itemId, out var item))
            {
                var timestamp = _timeProvider.GetUtcNow();
                var content = new TextItemContent(request.Text);
                item = new SessionItemSnapshot(
                    itemId,
                    turnId,
                    SessionItemType.AgentMessage,
                    SessionItemStatus.Started,
                    content,
                    thread.CurrentSequence + 1,
                    timestamp,
                    timestamp);
                var operation = SyntheticOperation(
                    request,
                    "item-started",
                    SessionEventType.ItemStarted);
                var committed = await CommitAsync(
                    operation.IdempotencyKey,
                    operation.Name,
                    operation.RequestSha256,
                    ExecutionThread(
                        thread,
                        thread.CurrentSequence + 1,
                        timestamp,
                        turnId),
                    new ItemStartedFact(
                        itemId,
                        turnId,
                        SessionItemType.AgentMessage,
                        SerializeContent(content),
                        request.Text,
                        operation.RequestSha256),
                    SessionEventType.ItemStarted,
                    cancellationToken,
                    new SessionEventPayload(Turn: turn, Item: item));
                if (committed.Value is null)
                {
                    return committed;
                }

                thread = committed.Value;
                _items[itemId] = item;
                _itemText[itemId] = request.Text;
            }

            if (item.Status != SessionItemStatus.Completed)
            {
                var timestamp = _timeProvider.GetUtcNow();
                var bytes = Encoding.UTF8.GetBytes(request.Text);
                item = item with
                {
                    Status = SessionItemStatus.Completed,
                    UpdatedAt = timestamp,
                };
                var operation = SyntheticOperation(
                    request,
                    "item-completed",
                    SessionEventType.ItemCompleted);
                var committed = await CommitAsync(
                    operation.IdempotencyKey,
                    operation.Name,
                    operation.RequestSha256,
                    ExecutionThread(
                        thread,
                        thread.CurrentSequence + 1,
                        timestamp,
                        turnId),
                    new ItemCompletedFact(
                        itemId,
                        bytes.Length,
                        Hash(bytes),
                        operation.RequestSha256),
                    SessionEventType.ItemCompleted,
                    cancellationToken,
                    new SessionEventPayload(Turn: turn, Item: item));
                if (committed.Value is null)
                {
                    return committed;
                }

                thread = committed.Value;
                _items[itemId] = item;
            }

            var completedAt = _timeProvider.GetUtcNow();
            var completedTurn = turn with
            {
                Status = TurnStatus.Completed,
                UpdatedAt = completedAt,
                CompletedAt = completedAt,
            };
            var terminalOperation = SyntheticOperation(
                request,
                "turn-completed",
                SessionEventType.TurnCompleted);
            var terminal = await CommitAsync(
                terminalOperation.IdempotencyKey,
                terminalOperation.Name,
                terminalOperation.RequestSha256,
                ExecutionThread(
                    thread,
                    thread.CurrentSequence + 1,
                    completedAt,
                    activeTurnId: null),
                new TurnTerminalFact(
                    turnId,
                    Error: null,
                    terminalOperation.RequestSha256),
                SessionEventType.TurnCompleted,
                cancellationToken,
                new SessionEventPayload(Turn: completedTurn));
            if (terminal.Value is not null)
            {
                _turns[turnId] = completedTurn;
            }

            return terminal;
        }
        finally
        {
            threadGate.Release();
        }
    }

    private static SyntheticTurnOperation SyntheticOperation(
        AppendCompletedAgentTurnRequest request,
        string stage,
        SessionEventType eventType)
    {
        var name = $"synthetic:{stage}";
        return new SyntheticTurnOperation(
            SyntheticId(request.DeliveryId, stage),
            name,
            RequestHash(
                name,
                new
                {
                    request.ThreadId,
                    request.DeliveryId,
                    request.Text,
                    EventType = eventType,
                }));
    }

    private static Guid SyntheticId(string deliveryId, string stage)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{stage}:{deliveryId}"));
        hash[6] = (byte)((hash[6] & 0x0f) | 0x70);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }

    private sealed record SyntheticTurnOperation(
        Guid IdempotencyKey,
        string Name,
        string RequestSha256);
}
