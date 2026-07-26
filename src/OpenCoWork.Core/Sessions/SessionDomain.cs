using OpenCoWork.Abstractions;

namespace OpenCoWork.Core.Sessions;

internal sealed class SessionStateException : InvalidOperationException
{
    public SessionStateException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

internal sealed class SessionThreadState
{
    private SessionThreadState(
        Guid threadId,
        string displayName,
        HistoryMode historyMode,
        DateTimeOffset createdAt)
    {
        ThreadId = threadId;
        DisplayName = displayName;
        HistoryMode = historyMode;
        Status = ThreadStatus.Active;
        Availability = ThreadAvailability.Available;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid ThreadId { get; }

    public string DisplayName { get; private set; }

    public HistoryMode HistoryMode { get; }

    public ThreadStatus Status { get; private set; }

    public ThreadAvailability Availability { get; private set; }

    public long CurrentSequence { get; private set; }

    public SessionTurnState? ActiveTurn { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static SessionThreadState Create(
        Guid threadId,
        string displayName,
        HistoryMode historyMode,
        DateTimeOffset createdAt)
    {
        if (threadId.Version != 7)
        {
            throw new ArgumentException("Thread ID must be UUIDv7.", nameof(threadId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (historyMode != HistoryMode.Server)
        {
            throw new SessionStateException(
                SessionErrorCodes.UnsupportedHistoryMode,
                "M2 only supports server-managed history.");
        }

        return new SessionThreadState(threadId, displayName, historyMode, createdAt);
    }

    public void Rename(string displayName, DateTimeOffset timestamp)
    {
        EnsureAvailable();
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        DisplayName = displayName;
        UpdatedAt = timestamp;
    }

    public void Pause(DateTimeOffset timestamp)
    {
        EnsureAvailable();
        if (Status != ThreadStatus.Active)
        {
            throw InvalidState("Only an active thread can be paused.");
        }

        EnsureIdle();
        Status = ThreadStatus.Paused;
        UpdatedAt = timestamp;
    }

    public void Resume(DateTimeOffset timestamp)
    {
        EnsureAvailable();
        if (Status != ThreadStatus.Paused)
        {
            throw InvalidState("Only a paused thread can be resumed.");
        }

        Status = ThreadStatus.Active;
        UpdatedAt = timestamp;
    }

    public void Archive(DateTimeOffset timestamp)
    {
        EnsureAvailable();
        if (Status is not (ThreadStatus.Active or ThreadStatus.Paused))
        {
            throw InvalidState("Only an active or paused thread can be archived.");
        }

        EnsureIdle();
        Status = ThreadStatus.Archived;
        UpdatedAt = timestamp;
    }

    public void Unarchive(DateTimeOffset timestamp)
    {
        EnsureAvailable();
        if (Status != ThreadStatus.Archived)
        {
            throw InvalidState("Only an archived thread can be unarchived.");
        }

        Status = ThreadStatus.Active;
        UpdatedAt = timestamp;
    }

    public SessionTurnState StartTurn(Guid turnId, DateTimeOffset timestamp)
    {
        EnsureAvailable();
        if (Status != ThreadStatus.Active)
        {
            throw InvalidState("Turns can start only on an active thread.");
        }

        EnsureIdle();
        ActiveTurn = SessionTurnState.Start(turnId, ThreadId, timestamp);
        UpdatedAt = timestamp;
        return ActiveTurn;
    }

    public void EndTurn(SessionTurnState turn, DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(turn);
        if (!ReferenceEquals(ActiveTurn, turn))
        {
            throw InvalidState("The turn is not active on this thread.");
        }

        if (!turn.IsTerminal)
        {
            throw InvalidState("An active turn must be terminal before it can be released.");
        }

        ActiveTurn = null;
        UpdatedAt = timestamp;
    }

    public void AdvanceSequence(long sequence, DateTimeOffset timestamp)
    {
        if (sequence != CurrentSequence + 1)
        {
            throw new SessionStateException(
                SessionErrorCodes.SequenceConflict,
                $"Sequence {sequence} does not follow {CurrentSequence}.");
        }

        CurrentSequence = sequence;
        UpdatedAt = timestamp;
    }

    private void EnsureIdle()
    {
        if (ActiveTurn is not null)
        {
            throw new SessionStateException(
                SessionErrorCodes.ThreadBusy,
                "The thread has an active turn.");
        }
    }

    private void EnsureAvailable()
    {
        if (Availability != ThreadAvailability.Available)
        {
            throw new SessionStateException(
                SessionErrorCodes.RecoveryRequired,
                "The thread requires recovery.");
        }
    }

    private static SessionStateException InvalidState(string message) =>
        new(SessionErrorCodes.InvalidState, message);
}

internal sealed class SessionTurnState
{
    private SessionTurnState(Guid turnId, Guid threadId, DateTimeOffset createdAt)
    {
        TurnId = turnId;
        ThreadId = threadId;
        Status = TurnStatus.Running;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid TurnId { get; }

    public Guid ThreadId { get; }

    public TurnStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public bool IsTerminal =>
        Status is TurnStatus.Completed or TurnStatus.Failed or TurnStatus.Cancelled;

    public static SessionTurnState Start(
        Guid turnId,
        Guid threadId,
        DateTimeOffset createdAt)
    {
        if (turnId.Version != 7)
        {
            throw new ArgumentException("Turn ID must be UUIDv7.", nameof(turnId));
        }

        if (threadId.Version != 7)
        {
            throw new ArgumentException("Thread ID must be UUIDv7.", nameof(threadId));
        }

        return new SessionTurnState(turnId, threadId, createdAt);
    }

    public void TransitionTo(TurnStatus next, DateTimeOffset timestamp)
    {
        var allowed = Status switch
        {
            TurnStatus.Running =>
                next is TurnStatus.WaitingApproval
                    or TurnStatus.WaitingInput
                    or TurnStatus.Completed
                    or TurnStatus.Failed
                    or TurnStatus.Cancelled,
            TurnStatus.WaitingApproval or TurnStatus.WaitingInput =>
                next is TurnStatus.Running
                    or TurnStatus.Failed
                    or TurnStatus.Cancelled,
            _ => false,
        };

        if (!allowed)
        {
            throw new SessionStateException(
                SessionErrorCodes.InvalidState,
                $"Turn cannot transition from {Status} to {next}.");
        }

        Status = next;
        UpdatedAt = timestamp;
        if (IsTerminal)
        {
            CompletedAt = timestamp;
        }
    }
}

internal sealed class SessionItemState
{
    private SessionItemState(
        Guid itemId,
        Guid turnId,
        SessionItemType type,
        DateTimeOffset createdAt)
    {
        ItemId = itemId;
        TurnId = turnId;
        Type = type;
        Status = SessionItemStatus.Started;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid ItemId { get; }

    public Guid TurnId { get; }

    public SessionItemType Type { get; }

    public SessionItemStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static SessionItemState Start(
        Guid itemId,
        Guid turnId,
        SessionItemType type,
        DateTimeOffset createdAt)
    {
        if (itemId.Version != 7)
        {
            throw new ArgumentException("Item ID must be UUIDv7.", nameof(itemId));
        }

        if (turnId.Version != 7)
        {
            throw new ArgumentException("Turn ID must be UUIDv7.", nameof(turnId));
        }

        return new SessionItemState(itemId, turnId, type, createdAt);
    }

    public void TransitionTo(SessionItemStatus next, DateTimeOffset timestamp)
    {
        var streamable = Type is SessionItemType.AgentMessage or SessionItemType.Reasoning;
        var allowed = Status switch
        {
            SessionItemStatus.Started =>
                next is SessionItemStatus.Completed
                    or SessionItemStatus.Failed
                    or SessionItemStatus.Cancelled
                || streamable && next == SessionItemStatus.Streaming,
            SessionItemStatus.Streaming =>
                next is SessionItemStatus.Completed
                    or SessionItemStatus.Failed
                    or SessionItemStatus.Cancelled,
            _ => false,
        };

        if (!allowed)
        {
            throw new SessionStateException(
                SessionErrorCodes.InvalidState,
                $"Item cannot transition from {Status} to {next}.");
        }

        Status = next;
        UpdatedAt = timestamp;
    }
}
