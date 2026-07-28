using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.State;

namespace OpenCoWork.Core.Sessions;

internal sealed partial class SessionService : ISessionService
{
    private const int MaximumPageSize = 100;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    private readonly StateRuntime _stateRuntime;
    private readonly ThreadJournal _journal;
    private readonly SessionProjection _projection;
    private readonly SessionEventChannel _eventChannel;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string, string, SessionError?>? _providerModelValidator;
    private readonly ConcurrentDictionary<Guid, ThreadSnapshot> _snapshots = [];
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _threadGates = [];
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _idempotencyGates = [];
    private readonly ConcurrentDictionary<Guid, MemoryIdempotency> _idempotency = [];
    private readonly object _pendingEventGate = new();
    private readonly List<SessionEvent> _pendingEvents = [];
    private readonly SemaphoreSlim _projectionRecoveryGate = new(1, 1);
    private int _recoveryPublishing;
    private int _acceptingWork = 1;

    public SessionService(
        StateRuntime stateRuntime,
        ThreadJournal journal,
        SessionProjection projection,
        SessionConfig config,
        TimeProvider? timeProvider = null,
        ISessionExecutor? executor = null,
        string? executorKind = null,
        Action<SessionExecutionFaultPoint>? executionFaultInjector = null,
        Action<SessionRecoveryFaultPoint>? recoveryFaultInjector = null,
        Func<string, string, SessionError?>? providerModelValidator = null)
    {
        ArgumentNullException.ThrowIfNull(stateRuntime);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(config);
        _stateRuntime = stateRuntime;
        _journal = journal;
        _projection = projection;
        _eventChannel = new SessionEventChannel(config.EventBufferCapacity);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _sessionConfig = config;
        _executor = executor;
        _executorKind = executorKind;
        _executionFaultInjector = executionFaultInjector;
        _recoveryFaultInjector = recoveryFaultInjector;
        _providerModelValidator = providerModelValidator;
    }

    public async Task<SessionCommandResult<ThreadSnapshot>> CreateThreadAsync(
        CreateThreadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireId(request.IdempotencyKey, nameof(request.IdempotencyKey), "Idempotency key");
        if (request.ExpectedSequence != 0)
        {
            return Rejected<ThreadSnapshot>(
                SessionErrorCodes.SequenceConflict,
                "A new thread must start at sequence zero.",
                currentSequence: 0);
        }

        if (string.IsNullOrWhiteSpace(request.ProviderId) !=
            string.IsNullOrWhiteSpace(request.ModelId))
        {
            return Rejected<ThreadSnapshot>(
                SessionErrorCodes.InvalidState,
                "Provider and model must be specified together.");
        }

        var operation = Wire(SessionEventType.ThreadCreated);
        var requestSha256 = RequestHash(
            operation,
            new
            {
                request.ExpectedSequence,
                request.DisplayName,
                request.HistoryMode,
                request.ProviderId,
                request.ModelId,
                request.AgentMode,
            });
        var keyGate = GetIdempotencyGate(request.IdempotencyKey);
        await keyGate.WaitAsync(cancellationToken);
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

            if (request.HistoryMode != HistoryMode.Server)
            {
                return Rejected<ThreadSnapshot>(
                    SessionErrorCodes.UnsupportedHistoryMode,
                    "M2 only supports server-managed history.");
            }

            var providerModelError = ValidateProviderModel(
                request.ProviderId,
                request.ModelId);
            if (providerModelError is not null)
            {
                return new SessionCommandResult<ThreadSnapshot>(
                    SessionCommandStatus.Rejected,
                    null,
                    null,
                    null,
                    providerModelError);
            }

            if (!CanAcceptNewWork)
            {
                return NewWorkUnavailable<ThreadSnapshot>();
            }

            var displayName = string.IsNullOrWhiteSpace(request.DisplayName)
                ? "New thread"
                : request.DisplayName;
            var threadId = Guid.CreateVersion7();
            var threadGate = GetThreadGate(threadId);
            await threadGate.WaitAsync(cancellationToken);
            try
            {
                var timestamp = _timeProvider.GetUtcNow();
                var snapshot = new ThreadSnapshot(
                    threadId,
                    displayName,
                    ThreadStatus.Active,
                    ThreadAvailability.Available,
                    HistoryMode.Server,
                    currentSequence: 1,
                    activeTurnId: null,
                    queue: [],
                    timestamp,
                    timestamp,
                    SessionProjectionState.Ready,
                    diagnostic: null,
                    request.ProviderId,
                    request.ModelId,
                    request.AgentMode);
                return await CommitAsync(
                    request.IdempotencyKey,
                    operation,
                    requestSha256,
                    snapshot,
                    new ThreadCreatedFact(
                        displayName,
                        HistoryMode.Server,
                        FirstUserMessage: null,
                        requestSha256,
                        request.ProviderId,
                        request.ModelId,
                        request.AgentMode),
                    SessionEventType.ThreadCreated,
                    cancellationToken);
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

    public async Task<SessionQueryResult<ThreadSnapshot>> GetThreadAsync(
        Guid threadId,
        CancellationToken cancellationToken = default)
    {
        RequireId(threadId, nameof(threadId), "Thread ID");
        var snapshot = await GetSnapshotAsync(threadId, cancellationToken);
        return snapshot is null && _projection.State == SessionProjectionState.Degraded
            ? QueryError<ThreadSnapshot>(
                SessionErrorCodes.ProjectionUnavailable,
                "Session projection is unavailable.",
                retryable: true)
            : snapshot is null
            ? QueryError<ThreadSnapshot>(
                SessionErrorCodes.NotFound,
                "Thread was not found.")
            : new SessionQueryResult<ThreadSnapshot>(
                WithProjectionState(snapshot, _projection.State),
                null);
    }

    public Task<SessionQueryResult<SessionPage<ThreadSnapshot>>> ListThreadsAsync(
        ListThreadsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return QueryThreadsAsync(
            query: null,
            request.Cursor,
            request.PageSize,
            request.IncludeArchived,
            cancellationToken);
    }

    public async Task<SessionQueryResult<SessionPage<SessionEvent>>> ReadHistoryAsync(
        ReadHistoryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireId(request.ThreadId, nameof(request.ThreadId), "Thread ID");
        ValidatePageSize(request.PageSize);
        ArgumentOutOfRangeException.ThrowIfNegative(request.AfterSequence);
        var snapshot = await GetSnapshotAsync(request.ThreadId, cancellationToken);
        if (snapshot is null)
        {
            return QueryError<SessionPage<SessionEvent>>(
                SessionErrorCodes.NotFound,
                "Thread was not found.");
        }

        var replay = await ReplayAsync(snapshot, cancellationToken);
        if (replay.Health == ThreadJournalHealth.RecoveryRequired)
        {
            MarkRecoveryRequired(snapshot, replay.Diagnostic);
            return QueryError<SessionPage<SessionEvent>>(
                SessionErrorCodes.RecoveryRequired,
                "Thread history requires recovery.");
        }

        if (!TryBuildHistoryEvents(replay.Entries, out var history))
        {
            MarkRecoveryRequired(snapshot, "Thread journal payload is invalid.");
            return QueryError<SessionPage<SessionEvent>>(
                SessionErrorCodes.RecoveryRequired,
                "Thread history requires recovery.");
        }

        var entries = history
            .Where(sessionEvent => sessionEvent.Sequence > request.AfterSequence)
            .Take(request.PageSize + 1)
            .ToArray();
        var hasMore = entries.Length > request.PageSize;
        var events = hasMore ? entries[..request.PageSize] : entries;
        return new SessionQueryResult<SessionPage<SessionEvent>>(
            new SessionPage<SessionEvent>(
                Array.AsReadOnly(events),
                hasMore
                    ? events[^1].Sequence.ToString(CultureInfo.InvariantCulture)
                    : null),
            null);
    }

    public async Task<SessionQueryResult<SessionStatistics>> GetSessionStatisticsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!CanAcceptNewWork)
        {
            return QueryError<SessionStatistics>(
                SessionErrorCodes.ProjectionUnavailable,
                "Session projection is unavailable.",
                retryable: true);
        }

        await using var connection =
            await _stateRuntime.OpenReadOnlyConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                (SELECT count(*) FROM threads),
                (SELECT count(*) FROM threads WHERE status = 'active'),
                (SELECT count(*) FROM turns),
                (SELECT count(*) FROM turns
                 WHERE status IN ('running', 'waitingApproval', 'waitingInput')),
                (SELECT count(*) FROM items);
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new SessionQueryResult<SessionStatistics>(
            new SessionStatistics(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4)),
            null);
    }

    public Task<SessionQueryResult<SessionPage<ThreadSnapshot>>> SearchThreadsAsync(
        SearchThreadsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Query);
        return QueryThreadsAsync(
            request.Query.Normalize(NormalizationForm.FormC).ToUpperInvariant(),
            request.Cursor,
            request.PageSize,
            request.IncludeArchived,
            cancellationToken);
    }

    public Task<SessionCommandResult<ThreadSnapshot>> RenameThreadAsync(
        RenameThreadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DisplayName);
        var operation = Wire(SessionEventType.ThreadRenamed);
        var requestSha256 = RequestHash(
            operation,
            new
            {
                ThreadId = Wire(request.ThreadId),
                IdempotencyKey = Wire(request.IdempotencyKey),
                request.ExpectedSequence,
                request.DisplayName,
            });
        return MutateThreadAsync(
            request.ThreadId,
            request.IdempotencyKey,
            request.ExpectedSequence,
            operation,
            requestSha256,
            SessionEventType.ThreadRenamed,
            (_, _) => null,
            (snapshot, timestamp) => CopySnapshot(
                snapshot,
                displayName: request.DisplayName,
                currentSequence: snapshot.CurrentSequence + 1,
                updatedAt: timestamp),
            hash => new ThreadRenamedFact(request.DisplayName, hash),
            cancellationToken);
    }

    public Task<SessionCommandResult<ThreadSnapshot>> SetThreadModelAsync(
        SetThreadModelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProviderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ModelId);
        var operation = Wire(SessionEventType.ThreadModelChanged);
        var requestSha256 = RequestHash(
            operation,
            new
            {
                ThreadId = Wire(request.ThreadId),
                IdempotencyKey = Wire(request.IdempotencyKey),
                request.ExpectedSequence,
                request.ProviderId,
                request.ModelId,
            });
        return MutateThreadAsync(
            request.ThreadId,
            request.IdempotencyKey,
            request.ExpectedSequence,
            operation,
            requestSha256,
            SessionEventType.ThreadModelChanged,
            (snapshot, _) =>
                snapshot.Status == ThreadStatus.Archived
                    ? new SessionError(
                        SessionErrorCodes.InvalidState,
                        "An archived thread cannot change model.",
                        IsRetryable: false)
                    : ValidateProviderModel(request.ProviderId, request.ModelId),
            (snapshot, timestamp) => CopySnapshot(
                snapshot,
                currentSequence: snapshot.CurrentSequence + 1,
                updatedAt: timestamp,
                providerId: request.ProviderId,
                modelId: request.ModelId),
            hash => new ThreadModelChangedFact(
                request.ProviderId,
                request.ModelId,
                hash),
            cancellationToken);
    }

    private SessionError? ValidateProviderModel(string? providerId, string? modelId) =>
        _providerModelValidator is null ||
        string.IsNullOrWhiteSpace(providerId) ||
        string.IsNullOrWhiteSpace(modelId)
            ? null
            : _providerModelValidator(providerId, modelId);

    public Task<SessionCommandResult<ThreadSnapshot>> SetAgentModeAsync(
        SetAgentModeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var operation = Wire(SessionEventType.ThreadModeChanged);
        var requestSha256 = RequestHash(
            operation,
            new
            {
                ThreadId = Wire(request.ThreadId),
                IdempotencyKey = Wire(request.IdempotencyKey),
                request.ExpectedSequence,
                request.AgentMode,
            });
        return MutateThreadAsync(
            request.ThreadId,
            request.IdempotencyKey,
            request.ExpectedSequence,
            operation,
            requestSha256,
            SessionEventType.ThreadModeChanged,
            (snapshot, _) => snapshot.Status == ThreadStatus.Archived
                ? new SessionError(
                    SessionErrorCodes.InvalidState,
                    "An archived thread cannot change agent mode.",
                    IsRetryable: false)
                : null,
            (snapshot, timestamp) => CopySnapshot(
                snapshot,
                currentSequence: snapshot.CurrentSequence + 1,
                updatedAt: timestamp,
                agentMode: request.AgentMode),
            hash => new ThreadModeChangedFact(request.AgentMode, hash),
            cancellationToken);
    }

    public Task<SessionCommandResult<ThreadSnapshot>> PauseThreadAsync(
        ThreadMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ChangeStatusAsync(
            request,
            SessionEventType.ThreadPaused,
            ThreadStatus.Active,
            ThreadStatus.Paused,
            cancellationToken);
    }

    public async Task<SessionCommandResult<ThreadSnapshot>> ResumeThreadAsync(
        ThreadMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await ChangeStatusAsync(
            request,
            SessionEventType.ThreadResumed,
            ThreadStatus.Paused,
            ThreadStatus.Active,
            cancellationToken);
        if (result.Status != SessionCommandStatus.Rejected)
        {
            await TryScheduleNextAsync(request.ThreadId, CancellationToken.None);
        }

        return result;
    }

    public async Task<SessionSubscription> SubscribeAsync(
        SessionSubscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireId(request.ThreadId, nameof(request.ThreadId), "Thread ID");
        var threadGate = GetThreadGate(request.ThreadId);
        await threadGate.WaitAsync(cancellationToken);
        try
        {
            var snapshot = await GetSnapshotAsync(request.ThreadId, cancellationToken)
                ?? throw SubscriptionError(
                    SessionErrorCodes.NotFound,
                    "Thread was not found.");
            if (snapshot.Availability == ThreadAvailability.RecoveryRequired)
            {
                throw SubscriptionError(
                    SessionErrorCodes.RecoveryRequired,
                    "Thread history requires recovery.");
            }

            var water = snapshot.CurrentSequence;
            if (request.Mode == SessionSubscriptionMode.SnapshotThenLive)
            {
                return new SessionSubscription(
                    SessionSubscriptionDisposition.Ready,
                    WithProjectionState(snapshot, _projection.State),
                    water,
                    _eventChannel.Subscribe(request.ThreadId, water));
            }

            if (request.Mode != SessionSubscriptionMode.ResumeAfterSequence ||
                request.AfterSequence is null ||
                request.AfterSequence < 0 ||
                request.AfterSequence > water)
            {
                return new SessionSubscription(
                    SessionSubscriptionDisposition.ResetRequired,
                    WithProjectionState(snapshot, _projection.State),
                    water,
                    EmptyEvents());
            }

            var live = _eventChannel.Subscribe(request.ThreadId, water);
            try
            {
                var replay = await ReplayAsync(snapshot, cancellationToken);
                if (replay.Health == ThreadJournalHealth.RecoveryRequired)
                {
                    MarkRecoveryRequired(snapshot, replay.Diagnostic);
                    await live.DisposeAsync();
                    throw SubscriptionError(
                        SessionErrorCodes.RecoveryRequired,
                        "Thread history requires recovery.");
                }

                if (!TryBuildHistoryEvents(replay.Entries, out var history))
                {
                    MarkRecoveryRequired(snapshot, "Thread journal payload is invalid.");
                    await live.DisposeAsync();
                    throw SubscriptionError(
                        SessionErrorCodes.RecoveryRequired,
                        "Thread history requires recovery.");
                }

                var catchUp = history
                    .Where(sessionEvent =>
                        sessionEvent.Sequence > request.AfterSequence &&
                        sessionEvent.Sequence <= water)
                    .ToArray();
                if (catchUp.Length != water - request.AfterSequence.Value)
                {
                    await live.DisposeAsync();
                    return new SessionSubscription(
                        SessionSubscriptionDisposition.ResetRequired,
                        WithProjectionState(snapshot, _projection.State),
                        water,
                        EmptyEvents());
                }

                return new SessionSubscription(
                    SessionSubscriptionDisposition.Ready,
                    WithProjectionState(snapshot, _projection.State),
                    water,
                    CombineEvents(catchUp, live));
            }
            catch
            {
                await live.DisposeAsync();
                throw;
            }
        }
        finally
        {
            threadGate.Release();
        }
    }

    internal async Task<bool> RecoverProjectionAsync(
        Guid threadId,
        CancellationToken cancellationToken = default)
    {
        RequireId(threadId, nameof(threadId), "Thread ID");
        await _projectionRecoveryGate.WaitAsync(cancellationToken);
        try
        {
            Volatile.Write(ref _recoveryPublishing, 1);
            var threadGate = GetThreadGate(threadId);
            await threadGate.WaitAsync(cancellationToken);
            try
            {
                var snapshot = await GetSnapshotAsync(threadId, cancellationToken);
                if (snapshot is null)
                {
                    return false;
                }

                var replay = await ReplayAsync(snapshot, cancellationToken);
                if (replay.Health == ThreadJournalHealth.RecoveryRequired)
                {
                    MarkRecoveryRequired(snapshot, replay.Diagnostic);
                    return false;
                }

                await _projection.CatchUpAsync(replay.Entries, cancellationToken);
                if (_projection.State == SessionProjectionState.Ready)
                {
                    foreach (var pair in _snapshots.ToArray())
                    {
                        _snapshots[pair.Key] = WithProjectionState(
                            pair.Value,
                            SessionProjectionState.Ready);
                    }

                    SessionEvent[] pending;
                    lock (_pendingEventGate)
                    {
                        pending = _pendingEvents
                            .OrderBy(
                                sessionEvent => sessionEvent.ThreadId.ToString("D"),
                                StringComparer.Ordinal)
                            .ThenBy(sessionEvent => sessionEvent.Sequence)
                            .ToArray();
                        _pendingEvents.Clear();
                    }

                    foreach (var sessionEvent in pending)
                    {
                        _eventChannel.Publish(sessionEvent with
                        {
                            Payload = sessionEvent.Payload.Thread is { } thread
                                ? new SessionEventPayload(
                                    Thread: WithProjectionState(
                                        thread,
                                        SessionProjectionState.Ready))
                                : sessionEvent.Payload,
                        });
                    }
                }

                return _projection.State == SessionProjectionState.Ready;
            }
            finally
            {
                threadGate.Release();
            }
        }
        finally
        {
            Volatile.Write(ref _recoveryPublishing, 0);
            _projectionRecoveryGate.Release();
        }
    }

    private Task<SessionCommandResult<ThreadSnapshot>> ChangeStatusAsync(
        ThreadMutationRequest request,
        SessionEventType eventType,
        ThreadStatus expectedStatus,
        ThreadStatus nextStatus,
        CancellationToken cancellationToken)
    {
        var operation = Wire(eventType);
        var requestSha256 = RequestHash(
            operation,
            new
            {
                ThreadId = Wire(request.ThreadId),
                IdempotencyKey = Wire(request.IdempotencyKey),
                request.ExpectedSequence,
            });
        return MutateThreadAsync(
            request.ThreadId,
            request.IdempotencyKey,
            request.ExpectedSequence,
            operation,
            requestSha256,
            eventType,
            (snapshot, _) =>
            {
                if (snapshot.Status != expectedStatus)
                {
                    return new SessionError(
                        SessionErrorCodes.InvalidState,
                        $"Thread must be {expectedStatus} for this operation.",
                        IsRetryable: false);
                }

                if (snapshot.ActiveTurnId is not null)
                {
                    return new SessionError(
                        SessionErrorCodes.ThreadBusy,
                        "Thread has an active turn.",
                        IsRetryable: true);
                }

                return null;
            },
            (snapshot, timestamp) => CopySnapshot(
                snapshot,
                status: nextStatus,
                currentSequence: snapshot.CurrentSequence + 1,
                updatedAt: timestamp),
            hash => new ThreadStateFact(hash),
            cancellationToken);
    }

    private async Task<SessionCommandResult<ThreadSnapshot>> MutateThreadAsync(
        Guid threadId,
        Guid idempotencyKey,
        long expectedSequence,
        string operation,
        string requestSha256,
        SessionEventType eventType,
        Func<ThreadSnapshot, DateTimeOffset, SessionError?> validate,
        Func<ThreadSnapshot, DateTimeOffset, ThreadSnapshot> mutate,
        Func<string, object> createFact,
        CancellationToken cancellationToken)
    {
        RequireId(threadId, nameof(threadId), "Thread ID");
        RequireId(idempotencyKey, nameof(idempotencyKey), "Idempotency key");
        ArgumentOutOfRangeException.ThrowIfNegative(expectedSequence);
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
                var snapshot = await GetSnapshotAsync(threadId, cancellationToken);
                if (snapshot is null)
                {
                    return Rejected<ThreadSnapshot>(
                        SessionErrorCodes.NotFound,
                        "Thread was not found.");
                }

                if (snapshot.CurrentSequence != expectedSequence)
                {
                    return Rejected<ThreadSnapshot>(
                        SessionErrorCodes.SequenceConflict,
                        "Expected sequence does not match the thread.",
                        currentSequence: snapshot.CurrentSequence);
                }

                if (snapshot.Availability == ThreadAvailability.RecoveryRequired)
                {
                    return Rejected<ThreadSnapshot>(
                        SessionErrorCodes.RecoveryRequired,
                        "Thread requires recovery.");
                }

                var timestamp = _timeProvider.GetUtcNow();
                var validationError = validate(snapshot, timestamp);
                if (validationError is not null)
                {
                    return new SessionCommandResult<ThreadSnapshot>(
                        SessionCommandStatus.Rejected,
                        null,
                        null,
                        null,
                        validationError);
                }

                return await CommitAsync(
                    idempotencyKey,
                    operation,
                    requestSha256,
                    mutate(snapshot, timestamp),
                    createFact(requestSha256),
                    eventType,
                    cancellationToken);
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

    private async Task<SessionCommandResult<ThreadSnapshot>> CommitAsync(
        Guid idempotencyKey,
        string operation,
        string requestSha256,
        ThreadSnapshot snapshot,
        object fact,
        SessionEventType eventType,
        CancellationToken cancellationToken,
        SessionEventPayload? eventPayload = null)
    {
        ThreadJournalEntry entry;
        try
        {
            entry = await _journal.AppendAsync(
                ThreadJournalLocation.Active,
                new ThreadJournalDraft(
                    snapshot.ThreadId,
                    snapshot.CurrentSequence,
                    Guid.CreateVersion7(),
                    snapshot.UpdatedAt,
                    eventType,
                    idempotencyKey,
                    fact),
                cancellationToken);
        }
        catch (ThreadJournalCommittedException committed)
        {
            entry = committed.Entry;
        }
        catch (ThreadJournalException exception)
        {
            return Rejected<ThreadSnapshot>(exception.Code, exception.Message);
        }

        return await CompleteCommitAsync(
            entry,
            operation,
            requestSha256,
            snapshot,
            eventPayload);
    }

    private async Task<SessionCommandResult<ThreadSnapshot>> CompleteCommitAsync(
        ThreadJournalEntry entry,
        string operation,
        string requestSha256,
        ThreadSnapshot snapshot,
        SessionEventPayload? eventPayload = null)
    {
        _snapshots[snapshot.ThreadId] = snapshot;
        var projectionResult = await _projection.ApplyCommittedAsync(
            entry,
            CancellationToken.None);
        var projectionPending =
            projectionResult.Status == SessionCommandStatus.CommittedPendingProjection ||
            _projection.State == SessionProjectionState.Degraded;
        var resultSnapshot = projectionPending
            ? WithProjectionState(snapshot, SessionProjectionState.Degraded)
            : snapshot;
        _snapshots[snapshot.ThreadId] = resultSnapshot;
        var sessionEvent = new SessionEvent(
            entry.ThreadId,
            entry.Sequence,
            entry.EntryId,
            entry.Timestamp,
            entry.EntryType,
            eventPayload is null
                ? new SessionEventPayload(Thread: resultSnapshot)
                : eventPayload with { Thread = resultSnapshot });
        if (!projectionPending)
        {
            _eventChannel.Publish(sessionEvent);
        }
        else
        {
            lock (_pendingEventGate)
            {
                _pendingEvents.Add(sessionEvent);
            }
        }

        var result = new SessionCommandResult<ThreadSnapshot>(
            projectionPending
                ? SessionCommandStatus.CommittedPendingProjection
                : SessionCommandStatus.Committed,
            resultSnapshot,
            entry.Sequence,
            null,
            projectionPending
                ? projectionResult.Error ?? new SessionError(
                    SessionErrorCodes.ProjectionUnavailable,
                    "The committed session fact is waiting for projection.",
                    IsRetryable: true)
                : null);
        _idempotency[entry.IdempotencyKey] = new MemoryIdempotency(
            operation,
            requestSha256,
            result);
        return result;
    }

    private async Task<SessionCommandResult<ThreadSnapshot>?> TryReplayThreadCommandAsync(
        Guid idempotencyKey,
        string operation,
        string requestSha256,
        CancellationToken cancellationToken)
    {
        if (_idempotency.TryGetValue(idempotencyKey, out var memory))
        {
            return memory.Operation == operation &&
                   memory.RequestSha256 == requestSha256
                ? memory.Result
                : Rejected<ThreadSnapshot>(
                    SessionErrorCodes.IdempotencyConflict,
                    "Idempotency key is bound to another request.");
        }

        var projected = await _projection.ReadIdempotencyAsync(
            idempotencyKey,
            cancellationToken);
        if (projected is null)
        {
            var journalMatch = await _journal.FindByIdempotencyKeyAsync(
                idempotencyKey,
                cancellationToken);
            if (journalMatch is not null)
            {
                await _projection.CatchUpAsync(
                    journalMatch.Replay.Entries,
                    CancellationToken.None);
                projected = await _projection.ReadIdempotencyAsync(
                    idempotencyKey,
                    CancellationToken.None);
            }
        }

        if (projected is null)
        {
            return null;
        }

        if (!string.Equals(projected.Operation, operation, StringComparison.Ordinal) ||
            !string.Equals(
                projected.RequestSha256,
                requestSha256,
                StringComparison.Ordinal))
        {
            return Rejected<ThreadSnapshot>(
                SessionErrorCodes.IdempotencyConflict,
                "Idempotency key is bound to another request.");
        }

        var result = new SessionCommandResult<ThreadSnapshot>(
            SessionCommandStatus.Committed,
            projected.Thread,
            projected.CommittedSequence,
            null,
            null);
        _idempotency.TryAdd(
            idempotencyKey,
            new MemoryIdempotency(operation, requestSha256, result));
        return result;
    }

    private async Task<ThreadSnapshot?> GetSnapshotAsync(
        Guid threadId,
        CancellationToken cancellationToken)
    {
        if (_snapshots.TryGetValue(threadId, out var snapshot))
        {
            return snapshot;
        }

        if (_projection.State == SessionProjectionState.Degraded)
        {
            return null;
        }

        snapshot = await _projection.ReadThreadSnapshotAsync(threadId, cancellationToken);
        if (snapshot is not null)
        {
            _snapshots.TryAdd(threadId, snapshot);
        }

        return snapshot;
    }

    private async Task<SessionQueryResult<SessionPage<ThreadSnapshot>>> QueryThreadsAsync(
        string? query,
        string? cursor,
        int pageSize,
        bool includeArchived,
        CancellationToken cancellationToken)
    {
        ValidatePageSize(pageSize);
        if (!CanAcceptNewWork)
        {
            return QueryError<SessionPage<ThreadSnapshot>>(
                SessionErrorCodes.ProjectionUnavailable,
                "Session projection is unavailable.",
                retryable: true);
        }

        if (!TryDecodeCursor(cursor, out var cursorValue))
        {
            return QueryError<SessionPage<ThreadSnapshot>>(
                SessionErrorCodes.InvalidCursor,
                "Thread cursor is invalid.");
        }

        await using var connection =
            await _stateRuntime.OpenReadOnlyConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT thread_id, updated_utc
            FROM threads
            WHERE ($includeArchived = 1 OR status <> 'archived')
              AND ($query IS NULL OR
                   instr(display_name_search, $query) > 0 OR
                   instr(COALESCE(first_user_message_search, ''), $query) > 0)
              AND ($cursorUpdated IS NULL OR
                   updated_utc < $cursorUpdated OR
                   (updated_utc = $cursorUpdated AND thread_id < $cursorThread))
            ORDER BY updated_utc DESC, thread_id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$includeArchived", includeArchived ? 1 : 0);
        command.Parameters.AddWithValue("$query", query is null ? DBNull.Value : query);
        command.Parameters.AddWithValue(
            "$cursorUpdated",
            cursorValue is null ? DBNull.Value : cursorValue.Value.UpdatedAt);
        command.Parameters.AddWithValue(
            "$cursorThread",
            cursorValue is null ? DBNull.Value : Wire(cursorValue.Value.ThreadId));
        command.Parameters.AddWithValue("$limit", pageSize + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<(Guid ThreadId, long UpdatedAt)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add((
                Guid.ParseExact(reader.GetString(0), "D"),
                reader.GetInt64(1)));
        }

        var hasMore = rows.Count > pageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var snapshots = new List<ThreadSnapshot>(rows.Count);
        foreach (var row in rows)
        {
            var snapshot = await GetSnapshotAsync(row.ThreadId, cancellationToken);
            if (snapshot is null)
            {
                return QueryError<SessionPage<ThreadSnapshot>>(
                    SessionErrorCodes.ProjectionUnavailable,
                    "A projected thread snapshot is unavailable.",
                    retryable: true);
            }

            snapshots.Add(snapshot);
        }

        return new SessionQueryResult<SessionPage<ThreadSnapshot>>(
            new SessionPage<ThreadSnapshot>(
                snapshots.AsReadOnly(),
                hasMore && rows.Count > 0
                    ? EncodeCursor(rows[^1].UpdatedAt, rows[^1].ThreadId)
                    : null),
            null);
    }

    private async Task<ThreadJournalReplayResult> ReplayAsync(
        ThreadSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _journal.ReplayAsync(
                snapshot.Status == ThreadStatus.Archived
                    ? ThreadJournalLocation.Archived
                    : ThreadJournalLocation.Active,
                snapshot.ThreadId,
                cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return new ThreadJournalReplayResult(
                ThreadJournalHealth.RecoveryRequired,
                [],
                SessionErrorCodes.JournalCorrupt,
                "Thread journal is missing.",
                null);
        }
    }

    private void MarkRecoveryRequired(
        ThreadSnapshot snapshot,
        string? diagnostic)
    {
        _snapshots[snapshot.ThreadId] = new ThreadSnapshot(
            snapshot.ThreadId,
            snapshot.DisplayName,
            snapshot.Status,
            ThreadAvailability.RecoveryRequired,
            snapshot.HistoryMode,
            snapshot.CurrentSequence,
            snapshot.ActiveTurnId,
            snapshot.Queue,
            snapshot.CreatedAt,
            snapshot.UpdatedAt,
            WithProjectionState(snapshot, _projection.State).ProjectionState,
            diagnostic ?? "Thread journal requires recovery.",
            snapshot.ProviderId,
            snapshot.ModelId,
            snapshot.AgentMode);
    }

    private SemaphoreSlim GetThreadGate(Guid threadId) =>
        _threadGates.GetOrAdd(threadId, static _ => new SemaphoreSlim(1, 1));

    private SemaphoreSlim GetIdempotencyGate(Guid idempotencyKey) =>
        _idempotencyGates.GetOrAdd(
            idempotencyKey,
            static _ => new SemaphoreSlim(1, 1));

    private static ThreadSnapshot CopySnapshot(
        ThreadSnapshot snapshot,
        string? displayName = null,
        ThreadStatus? status = null,
        long? currentSequence = null,
        DateTimeOffset? updatedAt = null,
        string? providerId = null,
        string? modelId = null,
        AgentMode? agentMode = null) =>
        new(
            snapshot.ThreadId,
            displayName ?? snapshot.DisplayName,
            status ?? snapshot.Status,
            snapshot.Availability,
            snapshot.HistoryMode,
            currentSequence ?? snapshot.CurrentSequence,
            snapshot.ActiveTurnId,
            snapshot.Queue,
            snapshot.CreatedAt,
            updatedAt ?? snapshot.UpdatedAt,
            snapshot.ProjectionState,
            snapshot.Diagnostic,
            providerId ?? snapshot.ProviderId,
            modelId ?? snapshot.ModelId,
            agentMode ?? snapshot.AgentMode);

    private static ThreadSnapshot WithProjectionState(
        ThreadSnapshot snapshot,
        SessionProjectionState projectionState) =>
        new(
            snapshot.ThreadId,
            snapshot.DisplayName,
            snapshot.Status,
            snapshot.Availability,
            snapshot.HistoryMode,
            snapshot.CurrentSequence,
            snapshot.ActiveTurnId,
            snapshot.Queue,
            snapshot.CreatedAt,
            snapshot.UpdatedAt,
            projectionState,
            snapshot.Diagnostic,
            snapshot.ProviderId,
            snapshot.ModelId,
            snapshot.AgentMode);

    private static bool TryBuildHistoryEvents(
        IReadOnlyList<ThreadJournalEntry> entries,
        out SessionEvent[] events)
    {
        try
        {
            events = BuildHistoryEvents(entries);
            return true;
        }
        catch (Exception exception) when (
            exception is JsonException
                or InvalidOperationException
                or ArgumentException
                or InvalidDataException)
        {
            events = [];
            return false;
        }
    }

    private static ThreadSnapshot? ApplyHistoryFact(
        ThreadSnapshot? snapshot,
        ThreadJournalEntry entry)
    {
        if (entry.EntryType == SessionEventType.ThreadCreated)
        {
            var fact = entry.Payload.Deserialize<ThreadCreatedFact>(JsonOptions)
                ?? throw new JsonException("Thread fact is missing.");
            return new ThreadSnapshot(
                entry.ThreadId,
                fact.DisplayName,
                ThreadStatus.Active,
                ThreadAvailability.Available,
                fact.HistoryMode,
                entry.Sequence,
                activeTurnId: null,
                queue: [],
                entry.Timestamp,
                entry.Timestamp,
                SessionProjectionState.Ready,
                diagnostic: null,
                fact.ProviderId,
                fact.ModelId,
                fact.AgentMode);
        }

        if (entry.EntryType == SessionEventType.ThreadForked)
        {
            var fact = entry.Payload.Deserialize<ThreadForkedFact>(JsonOptions)
                ?? throw new JsonException("Fork fact is missing.");
            return new ThreadSnapshot(
                entry.ThreadId,
                fact.DisplayName,
                ThreadStatus.Active,
                ThreadAvailability.Available,
                fact.HistoryMode,
                entry.Sequence,
                activeTurnId: null,
                queue: [],
                entry.Timestamp,
                entry.Timestamp,
                SessionProjectionState.Ready,
                diagnostic: null,
                fact.ProviderId,
                fact.ModelId,
                fact.AgentMode);
        }

        if (snapshot is null)
        {
            throw new InvalidDataException("Thread history does not start with creation.");
        }

        var displayName = snapshot.DisplayName;
        var status = snapshot.Status;
        var activeTurnId = snapshot.ActiveTurnId;
        var providerId = snapshot.ProviderId;
        var modelId = snapshot.ModelId;
        var agentMode = snapshot.AgentMode;
        IReadOnlyList<QueuedTurnInputSnapshot> queue = snapshot.Queue;
        switch (entry.EntryType)
        {
            case SessionEventType.ThreadRenamed:
                displayName = (entry.Payload.Deserialize<ThreadRenamedFact>(JsonOptions)
                    ?? throw new JsonException("Rename fact is missing.")).DisplayName;
                break;
            case SessionEventType.ThreadModelChanged:
                var model = entry.Payload
                    .Deserialize<ThreadModelChangedFact>(JsonOptions)
                    ?? throw new JsonException("Model fact is missing.");
                providerId = model.ProviderId;
                modelId = model.ModelId;
                break;
            case SessionEventType.ThreadModeChanged:
                agentMode = (entry.Payload
                    .Deserialize<ThreadModeChangedFact>(JsonOptions)
                    ?? throw new JsonException("Mode fact is missing.")).AgentMode;
                break;
            case SessionEventType.ThreadPaused:
                status = ThreadStatus.Paused;
                break;
            case SessionEventType.ThreadResumed:
            case SessionEventType.ThreadUnarchived:
                status = ThreadStatus.Active;
                break;
            case SessionEventType.ThreadArchived:
                status = ThreadStatus.Archived;
                break;
            case SessionEventType.ThreadDeletionRequested:
                status = ThreadStatus.Archived;
                break;
            case SessionEventType.ThreadRolledBack:
                activeTurnId = null;
                queue = [];
                break;
            case SessionEventType.TurnStarted:
                var started = entry.Payload
                    .Deserialize<TurnStartedFact>(JsonOptions)
                    ?? throw new JsonException("Turn start fact is missing.");
                activeTurnId = started.TurnId;
                if (started.QueueItemId is { } scheduledQueueItemId)
                {
                    queue = RepositionQueue(
                        queue.Where(item => item.QueueItemId != scheduledQueueItemId));
                }

                break;
            case SessionEventType.TurnQueued:
                var queued = entry.Payload
                    .Deserialize<TurnQueuedFact>(JsonOptions)
                    ?? throw new JsonException("Queue fact is missing.");
                queue = RepositionQueue(
                    queue.Append(new QueuedTurnInputSnapshot(
                        queued.QueueItemId,
                        entry.ThreadId,
                        queued.Text,
                        queued.Position,
                        entry.Timestamp,
                        queued.EffectiveAgentMode)));
                break;
            case SessionEventType.TurnQueueChanged:
                var changed = entry.Payload
                    .Deserialize<TurnQueueChangedFact>(JsonOptions)
                    ?? throw new JsonException("Queue change fact is missing.");
                var byId = queue.ToDictionary(item => item.QueueItemId);
                queue = changed.QueueItemIds
                    .Select((itemId, position) =>
                        byId[itemId] with { Position = position })
                    .ToArray();
                break;
            case SessionEventType.TurnSteered:
                var steered = entry.Payload
                    .Deserialize<TurnSteeredFact>(JsonOptions)
                    ?? throw new JsonException("Steer fact is missing.");
                queue = RepositionQueue(
                    queue.Where(item => item.QueueItemId != steered.QueueItemId));
                break;
            case SessionEventType.TurnCompleted:
            case SessionEventType.TurnFailed:
            case SessionEventType.TurnCancelled:
                activeTurnId = null;
                break;
        }

        return new ThreadSnapshot(
            snapshot.ThreadId,
            displayName,
            status,
            snapshot.Availability,
            snapshot.HistoryMode,
            entry.Sequence,
            activeTurnId,
            queue,
            snapshot.CreatedAt,
            entry.Timestamp,
            snapshot.ProjectionState,
            snapshot.Diagnostic,
            providerId,
            modelId,
            agentMode);
    }

    private static IReadOnlyList<QueuedTurnInputSnapshot> RepositionQueue(
        IEnumerable<QueuedTurnInputSnapshot> queue) =>
        Array.AsReadOnly(
            queue.Select((item, position) => item with { Position = position }).ToArray());

    private static async IAsyncEnumerable<SessionEvent> CombineEvents(
        IEnumerable<SessionEvent> catchUp,
        SessionEventChannel.SessionEventFeed live,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using (live)
        {
            foreach (var sessionEvent in catchUp)
            {
                yield return sessionEvent;
            }

            await foreach (var sessionEvent in live.WithCancellation(cancellationToken))
            {
                yield return sessionEvent;
            }
        }
    }

    private static async IAsyncEnumerable<SessionEvent> EmptyEvents()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static string RequestHash(string operation, object request)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new { Operation = operation, Request = request },
            JsonOptions);
        return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    }

    private static void RequireId(Guid value, string parameterName, string label) =>
        SessionIds.RequireVersion7(value, parameterName, label);

    private static void ValidatePageSize(int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, MaximumPageSize);
    }

    private static bool TryDecodeCursor(
        string? cursor,
        out (long UpdatedAt, Guid ThreadId)? value)
    {
        value = null;
        if (cursor is null)
        {
            return true;
        }

        try
        {
            var base64 = cursor.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight((base64.Length + 3) / 4 * 4, '=');
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            var separator = text.IndexOf('|');
            if (separator <= 0 ||
                !long.TryParse(
                    text.AsSpan(0, separator),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var updatedAt) ||
                !Guid.TryParseExact(text[(separator + 1)..], "D", out var threadId) ||
                threadId.Version != 7)
            {
                return false;
            }

            value = (updatedAt, threadId);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string EncodeCursor(long updatedAt, Guid threadId)
    {
        var bytes = Encoding.UTF8.GetBytes(
            $"{updatedAt.ToString(CultureInfo.InvariantCulture)}|{Wire(threadId)}");
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string Wire(Guid value) =>
        value.ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant();

    private static string Wire<T>(T value)
        where T : struct, Enum
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static SessionCommandResult<T> Rejected<T>(
        string code,
        string message,
        long? currentSequence = null) =>
        new(
            SessionCommandStatus.Rejected,
            default,
            null,
            currentSequence,
            new SessionError(code, message, IsRetryable: false));

    private SessionCommandResult<T> NewWorkUnavailable<T>() =>
        Volatile.Read(ref _acceptingWork) == 0
            ? new SessionCommandResult<T>(
                SessionCommandStatus.Rejected,
                default,
                null,
                null,
                new SessionError(
                    SessionErrorCodes.RuntimeShuttingDown,
                    "Session runtime is not accepting new work.",
                    IsRetryable: true))
            : new SessionCommandResult<T>(
                SessionCommandStatus.Rejected,
                default,
                null,
                null,
                new SessionError(
                    SessionErrorCodes.ProjectionUnavailable,
                    "Session projection is unavailable.",
                    IsRetryable: true));

    private static SessionCommandResult<T> NotAvailable<T>() =>
        Rejected<T>(
            SessionErrorCodes.InvalidState,
            "Operation is not available.");

    private static SessionQueryResult<T> QueryError<T>(
        string code,
        string message,
        bool retryable = false) =>
        new(default, new SessionError(code, message, retryable));

    private static SessionSubscriptionException SubscriptionError(
        string code,
        string message) =>
        new(new SessionError(code, message, IsRetryable: false));

    private sealed record MemoryIdempotency(
        string Operation,
        string RequestSha256,
        SessionCommandResult<ThreadSnapshot> Result);

    private bool CanAcceptNewWork =>
        Volatile.Read(ref _acceptingWork) != 0 &&
        _projection.CanAcceptNewWork &&
        Volatile.Read(ref _recoveryPublishing) == 0;
}
