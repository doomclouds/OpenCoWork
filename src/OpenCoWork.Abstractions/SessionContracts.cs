namespace OpenCoWork.Abstractions;

public enum ThreadStatus
{
    Active,
    Paused,
    Archived,
}

public enum ThreadAvailability
{
    Available,
    RecoveryRequired,
}

public enum TurnStatus
{
    Running,
    WaitingApproval,
    WaitingInput,
    Completed,
    Failed,
    Cancelled,
}

public enum SessionItemType
{
    UserMessage,
    AgentMessage,
    Reasoning,
    ApprovalRequest,
    ApprovalResponse,
    UserInputRequest,
    UserInputResponse,
    Error,
    SystemNotice,
}

public enum SessionItemStatus
{
    Started,
    Streaming,
    Completed,
    Failed,
    Cancelled,
}

public enum HistoryMode
{
    Server,
    Client,
}

public enum SessionCommandStatus
{
    Rejected,
    Committed,
    CommittedPendingProjection,
}

public enum SessionProjectionState
{
    Ready,
    Degraded,
}

public enum SessionSubscriptionMode
{
    SnapshotThenLive,
    ResumeAfterSequence,
}

public enum SessionSubscriptionDisposition
{
    Ready,
    ResetRequired,
}

public enum SessionInteractionType
{
    Approval,
    UserInput,
}

public enum SessionEventType
{
    ThreadCreated,
    ThreadRenamed,
    ThreadPaused,
    ThreadResumed,
    ThreadArchived,
    ThreadUnarchived,
    ThreadDeletionRequested,
    ThreadForked,
    ThreadRolledBack,
    TurnQueued,
    TurnQueueChanged,
    TurnSteered,
    TurnStarted,
    TurnWaitingApproval,
    TurnWaitingInput,
    TurnExecutionResumed,
    TurnCompleted,
    TurnFailed,
    TurnCancelled,
    ItemStarted,
    ItemDeltaAppended,
    ItemCompleted,
    ItemFailed,
    ItemCancelled,
    InteractionResolved,
    ThreadJournalRecovered,
}

public static class SessionErrorCodes
{
    public const string NotFound = "session.notFound";
    public const string InvalidState = "session.invalidState";
    public const string ThreadBusy = "session.threadBusy";
    public const string SequenceConflict = "session.sequenceConflict";
    public const string IdempotencyConflict = "session.idempotencyConflict";
    public const string QueueFull = "session.queueFull";
    public const string QueueItemNotFound = "session.queueItemNotFound";
    public const string InteractionAlreadyResolved = "session.interactionAlreadyResolved";
    public const string SubscriberLagged = "session.subscriberLagged";
    public const string ProjectionUnavailable = "session.projectionUnavailable";
    public const string RecoveryRequired = "session.recoveryRequired";
    public const string InvalidCursor = "session.invalidCursor";
    public const string DeleteTokenInvalid = "session.deleteTokenInvalid";
    public const string DeleteTokenExpired = "session.deleteTokenExpired";
    public const string UnsupportedHistoryMode = "session.unsupportedHistoryMode";
    public const string JournalCorrupt = "journal.corrupt";
    public const string JournalEntryTooLarge = "journal.entryTooLarge";
    public const string JournalUnsupportedSchema = "journal.unsupportedSchema";
    public const string RuntimeInterrupted = "runtime.interrupted";
    public const string RuntimeContinuationMissing = "runtime.continuationMissing";
    public const string RuntimeShuttingDown = "runtime.shuttingDown";
    public const string RuntimeExecutorUnavailable = "runtime.executorUnavailable";
}

public sealed record SessionError(string Code, string Message, bool IsRetryable);

public sealed record SessionCommandResult<T>(
    SessionCommandStatus Status,
    T? Value,
    long? Sequence,
    long? CurrentSequence,
    SessionError? Error);

public sealed record SessionQueryResult<T>(T? Value, SessionError? Error)
{
    public bool IsSuccess => Error is null;
}

public sealed record SessionPage<T>(IReadOnlyList<T> Items, string? NextCursor);

public abstract record SessionItemContent;

public sealed record TextItemContent(string Text) : SessionItemContent;

public sealed record ApprovalRequestContent(string Prompt) : SessionItemContent;

public sealed record ApprovalResponseContent(bool Approved, string? Comment) : SessionItemContent;

public sealed record UserInputRequestContent(string Prompt) : SessionItemContent;

public sealed record UserInputResponseContent(string Text) : SessionItemContent;

public sealed record ErrorItemContent(string Code, string Message) : SessionItemContent;

public sealed record SystemNoticeContent(string Message) : SessionItemContent;

public sealed record TurnSnapshot(
    Guid TurnId,
    Guid ThreadId,
    TurnStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    SessionError? Error);

public sealed record SessionItemSnapshot(
    Guid ItemId,
    Guid TurnId,
    SessionItemType Type,
    SessionItemStatus Status,
    SessionItemContent Content,
    long Sequence,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record QueuedTurnInputSnapshot(
    Guid QueueItemId,
    Guid ThreadId,
    string Text,
    int Position,
    DateTimeOffset CreatedAt);

public sealed record PendingInteractionSnapshot(
    Guid InteractionId,
    Guid ThreadId,
    Guid TurnId,
    SessionInteractionType Type,
    bool IsResolved,
    DateTimeOffset CreatedAt,
    DateTimeOffset? TimeoutAt);

public sealed record ThreadSnapshot
{
    public ThreadSnapshot(
        Guid threadId,
        string displayName,
        ThreadStatus status,
        ThreadAvailability availability,
        HistoryMode historyMode,
        long currentSequence,
        Guid? activeTurnId,
        IEnumerable<QueuedTurnInputSnapshot> queue,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        SessionProjectionState projectionState,
        string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        ArgumentNullException.ThrowIfNull(queue);
        ThreadId = threadId;
        DisplayName = displayName;
        Status = status;
        Availability = availability;
        HistoryMode = historyMode;
        CurrentSequence = currentSequence;
        ActiveTurnId = activeTurnId;
        Queue = Array.AsReadOnly(queue.ToArray());
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        ProjectionState = projectionState;
        Diagnostic = diagnostic;
    }

    public Guid ThreadId { get; }

    public string DisplayName { get; }

    public ThreadStatus Status { get; }

    public ThreadAvailability Availability { get; }

    public HistoryMode HistoryMode { get; }

    public long CurrentSequence { get; }

    public Guid? ActiveTurnId { get; }

    public IReadOnlyList<QueuedTurnInputSnapshot> Queue { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; }

    public SessionProjectionState ProjectionState { get; }

    public string? Diagnostic { get; }
}

public sealed record SessionStatistics(
    long ThreadCount,
    long ActiveThreadCount,
    long TurnCount,
    long ActiveTurnCount,
    long ItemCount);

public sealed record SessionEventPayload(
    ThreadSnapshot? Thread = null,
    TurnSnapshot? Turn = null,
    SessionItemSnapshot? Item = null,
    QueuedTurnInputSnapshot? QueueItem = null,
    PendingInteractionSnapshot? Interaction = null,
    SessionError? Error = null);

public sealed record SessionEvent(
    Guid ThreadId,
    long Sequence,
    Guid EntryId,
    DateTimeOffset Timestamp,
    SessionEventType Type,
    SessionEventPayload Payload);

public sealed class SessionSubscription
{
    public SessionSubscription(
        SessionSubscriptionDisposition disposition,
        ThreadSnapshot snapshot,
        long currentSequence,
        IAsyncEnumerable<SessionEvent> events)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(events);
        Disposition = disposition;
        Snapshot = snapshot;
        CurrentSequence = currentSequence;
        Events = events;
    }

    public SessionSubscriptionDisposition Disposition { get; }

    public ThreadSnapshot Snapshot { get; }

    public long CurrentSequence { get; }

    public IAsyncEnumerable<SessionEvent> Events { get; }
}

public sealed record CreateThreadRequest(
    Guid IdempotencyKey,
    long ExpectedSequence,
    string? DisplayName = null,
    HistoryMode HistoryMode = HistoryMode.Server);

public sealed record RenameThreadRequest(
    Guid ThreadId,
    Guid IdempotencyKey,
    long ExpectedSequence,
    string DisplayName);

public sealed record ThreadMutationRequest(
    Guid ThreadId,
    Guid IdempotencyKey,
    long ExpectedSequence);

public sealed record PrepareDeleteRequest(Guid ThreadId, long ExpectedSequence);

public sealed record DeleteThreadRequest(
    Guid ThreadId,
    Guid IdempotencyKey,
    long ExpectedSequence,
    string Token);

public sealed record ForkThreadRequest(
    Guid SourceThreadId,
    long SourceSequence,
    long ExpectedSequence,
    Guid IdempotencyKey,
    string? DisplayName = null);

public sealed record RollbackThreadRequest(
    Guid ThreadId,
    long TargetSequence,
    long ExpectedSequence,
    Guid IdempotencyKey);

public sealed record EnqueueInputRequest(
    Guid ThreadId,
    Guid IdempotencyKey,
    long ExpectedSequence,
    string Text);

public sealed record RemoveQueuedInputRequest(
    Guid ThreadId,
    Guid QueueItemId,
    Guid IdempotencyKey,
    long ExpectedSequence);

public sealed record ReorderQueuedInputsRequest(
    Guid ThreadId,
    IReadOnlyList<Guid> QueueItemIds,
    Guid IdempotencyKey,
    long ExpectedSequence);

public sealed record SteerTurnRequest(
    Guid ThreadId,
    Guid ExpectedTurnId,
    Guid QueueItemId,
    Guid IdempotencyKey,
    long ExpectedSequence);

public sealed record CancelTurnRequest(
    Guid ThreadId,
    Guid TurnId,
    Guid IdempotencyKey,
    long ExpectedSequence);

public sealed record ResolveInteractionRequest(
    Guid ThreadId,
    Guid TurnId,
    Guid InteractionId,
    SessionItemContent Response,
    Guid IdempotencyKey,
    long ExpectedSequence);

public sealed record ListThreadsRequest(
    string? Cursor = null,
    int PageSize = 100,
    bool IncludeArchived = false);

public sealed record ReadHistoryRequest(
    Guid ThreadId,
    long AfterSequence = 0,
    int PageSize = 100);

public sealed record SearchThreadsRequest(
    string Query,
    string? Cursor = null,
    int PageSize = 100,
    bool IncludeArchived = false);

public sealed record SessionSubscriptionRequest(
    Guid ThreadId,
    SessionSubscriptionMode Mode,
    long? AfterSequence = null);

public sealed record DeletePreparation(
    Guid ThreadId,
    long Sequence,
    string Token,
    DateTimeOffset ExpiresAt);

public sealed record RollbackResult(
    ThreadSnapshot Thread,
    bool ExternalSideEffectsReverted);

public interface ISessionService
{
    Task<SessionCommandResult<ThreadSnapshot>> CreateThreadAsync(
        CreateThreadRequest request,
        CancellationToken cancellationToken = default);

    Task<SessionQueryResult<ThreadSnapshot>> GetThreadAsync(
        Guid threadId,
        CancellationToken cancellationToken = default);

    Task<SessionQueryResult<SessionPage<ThreadSnapshot>>> ListThreadsAsync(
        ListThreadsRequest request,
        CancellationToken cancellationToken = default);

    Task<SessionQueryResult<SessionPage<SessionEvent>>> ReadHistoryAsync(
        ReadHistoryRequest request,
        CancellationToken cancellationToken = default);

    Task<SessionQueryResult<SessionStatistics>> GetSessionStatisticsAsync(
        CancellationToken cancellationToken = default);

    Task<SessionQueryResult<SessionPage<ThreadSnapshot>>> SearchThreadsAsync(
        SearchThreadsRequest request,
        CancellationToken cancellationToken = default);

    Task<SessionCommandResult<ThreadSnapshot>> RenameThreadAsync(
        RenameThreadRequest request,
        CancellationToken cancellationToken = default);

    Task<SessionCommandResult<ThreadSnapshot>> PauseThreadAsync(
        ThreadMutationRequest request,
        CancellationToken cancellationToken = default);

    Task<SessionCommandResult<ThreadSnapshot>> ResumeThreadAsync(
        ThreadMutationRequest request,
        CancellationToken cancellationToken = default);

    Task<SessionCommandResult<ThreadSnapshot>> ArchiveThreadAsync(
        ThreadMutationRequest request,
        CancellationToken cancellationToken = default);

    Task<SessionCommandResult<ThreadSnapshot>> UnarchiveThreadAsync(
        ThreadMutationRequest request,
        CancellationToken cancellationToken = default);

    Task<SessionQueryResult<DeletePreparation>> PrepareDeleteAsync(
        PrepareDeleteRequest request,
        CancellationToken cancellationToken = default);

    Task<SessionCommandResult<bool>> DeleteThreadAsync(
        DeleteThreadRequest request,
        CancellationToken cancellationToken = default);

    Task<SessionCommandResult<ThreadSnapshot>> ForkThreadAsync(
        ForkThreadRequest request,
        CancellationToken cancellationToken = default);

    Task<SessionCommandResult<RollbackResult>> RollbackThreadAsync(
        RollbackThreadRequest request,
        CancellationToken cancellationToken = default);

    Task<SessionCommandResult<QueuedTurnInputSnapshot>> EnqueueInputAsync(
        EnqueueInputRequest request,
        CancellationToken cancellationToken = default);

    Task<SessionCommandResult<ThreadSnapshot>> RemoveQueuedInputAsync(
        RemoveQueuedInputRequest request,
        CancellationToken cancellationToken = default);

    Task<SessionCommandResult<ThreadSnapshot>> ReorderQueuedInputsAsync(
        ReorderQueuedInputsRequest request,
        CancellationToken cancellationToken = default);

    Task<SessionCommandResult<ThreadSnapshot>> SteerTurnAsync(
        SteerTurnRequest request,
        CancellationToken cancellationToken = default);

    Task<SessionCommandResult<TurnSnapshot>> CancelTurnAsync(
        CancelTurnRequest request,
        CancellationToken cancellationToken = default);

    Task<SessionCommandResult<PendingInteractionSnapshot>> ResolveInteractionAsync(
        ResolveInteractionRequest request,
        CancellationToken cancellationToken = default);

    Task<SessionSubscription> SubscribeAsync(
        SessionSubscriptionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record SessionExecutionCheckpoint(
    string ExecutorKind,
    int SchemaVersion,
    string Payload,
    string Checksum);

public sealed class AgentSession
{
    public AgentSession(
        ThreadSnapshot thread,
        TurnSnapshot turn,
        IEnumerable<SessionItemSnapshot> modelHistory,
        SessionExecutionCheckpoint? checkpoint = null)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(modelHistory);
        Thread = thread;
        Turn = turn;
        ModelHistory = Array.AsReadOnly(modelHistory.ToArray());
        Checkpoint = checkpoint;
    }

    public ThreadSnapshot Thread { get; }

    public TurnSnapshot Turn { get; }

    public IReadOnlyList<SessionItemSnapshot> ModelHistory { get; }

    public SessionExecutionCheckpoint? Checkpoint { get; }
}

public abstract record SessionExecutionIntent;

public sealed record StartItemIntent(
    Guid ItemId,
    SessionItemType Type,
    SessionItemContent Content) : SessionExecutionIntent;

public sealed record AppendItemDeltaIntent(Guid ItemId, string Delta) : SessionExecutionIntent;

public sealed record CompleteItemIntent(Guid ItemId) : SessionExecutionIntent;

public sealed record FailItemIntent(Guid ItemId, SessionError Error) : SessionExecutionIntent;

public sealed record WaitForInteractionIntent(
    Guid InteractionId,
    SessionInteractionType Type,
    SessionItemContent Request,
    SessionExecutionCheckpoint Checkpoint,
    DateTimeOffset? TimeoutAt) : SessionExecutionIntent;

public sealed record CompleteTurnIntent : SessionExecutionIntent;

public sealed record FailTurnIntent(SessionError Error) : SessionExecutionIntent;

public interface ISessionExecutionSink
{
    ValueTask EmitAsync(
        SessionExecutionIntent intent,
        CancellationToken cancellationToken = default);
}

public interface ISessionExecutor
{
    ValueTask ExecuteAsync(
        AgentSession context,
        ISessionExecutionSink sink,
        CancellationToken cancellationToken);
}
