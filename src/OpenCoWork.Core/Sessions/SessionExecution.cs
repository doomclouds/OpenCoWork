using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Agents;
using OpenCoWork.Core.Configuration;

namespace OpenCoWork.Core.Sessions;

internal enum SessionExecutionFaultPoint
{
    AfterResolutionCommittedBeforeResume,
}

internal enum SessionExecutionStop
{
    Returned,
    Waiting,
    Terminal,
}

internal interface ISessionSteerReceiver
{
    ValueTask SteerAsync(
        Guid turnId,
        SessionItemSnapshot input,
        CancellationToken cancellationToken);
}

internal static class SessionExecutionCheckpointCodec
{
    private const int MaximumPayloadBytes = 256 * 1024;

    public static SessionExecutionCheckpoint Create(
        string executorKind,
        int schemaVersion,
        string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executorKind);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentOutOfRangeException.ThrowIfLessThan(schemaVersion, 1);
        if (Encoding.UTF8.GetByteCount(payload) > MaximumPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payload),
                "Checkpoint payload exceeds 256 KiB.");
        }

        return new SessionExecutionCheckpoint(
            executorKind,
            schemaVersion,
            payload,
            Checksum(executorKind, schemaVersion, payload));
    }

    public static bool IsValid(
        SessionExecutionCheckpoint checkpoint,
        string executorKind) =>
        !string.IsNullOrWhiteSpace(checkpoint.ExecutorKind) &&
        string.Equals(
            checkpoint.ExecutorKind,
            executorKind,
            StringComparison.Ordinal) &&
        checkpoint.SchemaVersion == 1 &&
        Encoding.UTF8.GetByteCount(checkpoint.Payload) <= MaximumPayloadBytes &&
        checkpoint.Checksum.Length == 64 &&
        string.Equals(
            checkpoint.Checksum,
            checkpoint.Checksum.ToLowerInvariant(),
            StringComparison.Ordinal) &&
        string.Equals(
            checkpoint.Checksum,
            Checksum(
                checkpoint.ExecutorKind,
                checkpoint.SchemaVersion,
                checkpoint.Payload),
            StringComparison.Ordinal);

    private static string Checksum(
        string executorKind,
        int schemaVersion,
        string payload)
    {
        var canonical =
            $"{Encoding.UTF8.GetByteCount(executorKind)}:{executorKind}" +
            $"{schemaVersion}:{Encoding.UTF8.GetByteCount(payload)}:{payload}";
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }
}

internal sealed class SessionExecution
{
    private readonly ISessionExecutor _executor;
    private readonly SessionConfig _config;
    private readonly TimeProvider _timeProvider;

    public SessionExecution(
        ISessionExecutor executor,
        SessionConfig config,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _executor = executor;
        _config = config;
        _timeProvider = timeProvider;
    }

    public SessionExecutionRun Start(
        AgentSession context,
        long initialSequence,
        Func<SessionExecutionIntent, CancellationToken, ValueTask<long>> commit,
        Func<CancellationToken, ValueTask>? onAccepted = null)
    {
        var cancellation = new CancellationTokenSource();
        var sink = new ExecutionSink(
            commit,
            _config.StreamFlushBytes,
            initialSequence,
            cancellation);
        var completion = RunAsync(context, sink, onAccepted, cancellation.Token);
        return new SessionExecutionRun(sink, cancellation, completion);
    }

    private async Task<SessionExecutionStop> RunAsync(
        AgentSession context,
        ExecutionSink sink,
        Func<CancellationToken, ValueTask>? onAccepted,
        CancellationToken cancellationToken)
    {
        using var timerCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timer = FlushPeriodicallyAsync(sink, timerCancellation.Token);
        try
        {
            var start = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var execution = InvokeExecutorAsync(start.Task);
            try
            {
                if (onAccepted is not null)
                {
                    await onAccepted(cancellationToken);
                }

                start.SetResult();
            }
            catch
            {
                start.SetCanceled(cancellationToken);
                try
                {
                    await execution;
                }
                catch (OperationCanceledException)
                {
                }

                throw;
            }

            await execution;
            await sink.FlushAsync(CancellationToken.None);
            return sink.Stop;

            async Task InvokeExecutorAsync(Task startSignal)
            {
                await startSignal;
                await _executor.ExecuteAsync(context, sink, cancellationToken);
            }
        }
        finally
        {
            timerCancellation.Cancel();
            try
            {
                await timer;
            }
            catch (OperationCanceledException)
            {
            }

            await sink.FlushAsync(CancellationToken.None);
        }
    }

    private async Task FlushPeriodicallyAsync(
        ExecutionSink sink,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(
            _config.StreamFlushInterval,
            _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            await sink.FlushAsync(CancellationToken.None);
        }
    }

    internal sealed class ExecutionSink(
        Func<SessionExecutionIntent, CancellationToken, ValueTask<long>> commit,
        int flushBytes,
        long initialSequence,
        CancellationTokenSource executionCancellation) : ISessionExecutionSink
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Dictionary<Guid, StringBuilder> _deltas = [];
        private bool _externallyStopped;
        private long _lastSequence = initialSequence;

        public SessionExecutionStop Stop { get; private set; } =
            SessionExecutionStop.Returned;

        public async ValueTask EmitAsync(
            SessionExecutionIntent intent,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(intent);
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (_externallyStopped || Stop != SessionExecutionStop.Returned)
                {
                    throw new InvalidOperationException(
                        "The execution sink no longer accepts intents.");
                }

                if (intent is AppendItemDeltaIntent delta)
                {
                    if (delta.Delta.Length == 0)
                    {
                        return;
                    }

                    if (!_deltas.TryGetValue(delta.ItemId, out var buffer))
                    {
                        buffer = new StringBuilder();
                        _deltas.Add(delta.ItemId, buffer);
                    }

                    buffer.Append(delta.Delta);
                    if (delta.Flush ||
                        Encoding.UTF8.GetByteCount(buffer.ToString()) >= flushBytes)
                    {
                        await FlushItemCoreAsync(delta.ItemId, cancellationToken);
                    }

                    return;
                }

                if (intent is CompleteItemIntent complete)
                {
                    await FlushItemCoreAsync(complete.ItemId, CancellationToken.None);
                }
                else if (intent is FailItemIntent failed)
                {
                    await FlushItemCoreAsync(failed.ItemId, CancellationToken.None);
                }
                else if (intent is WaitForInteractionIntent
                         or CompleteTurnIntent
                         or FailTurnIntent)
                {
                    await FlushCoreAsync(CancellationToken.None);
                }

                _lastSequence = await commit(intent, cancellationToken);
                if (intent is WaitForInteractionIntent)
                {
                    Stop = SessionExecutionStop.Waiting;
                }
                else if (intent is CompleteTurnIntent or FailTurnIntent)
                {
                    Stop = SessionExecutionStop.Terminal;
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<T> StopAsync<T>(
            Func<IReadOnlyList<AppendItemDeltaIntent>, Task<(bool Stop, T Result)>> action)
        {
            ArgumentNullException.ThrowIfNull(action);
            await _gate.WaitAsync();
            try
            {
                var deltas = _deltas
                    .Where(pair => pair.Value.Length != 0)
                    .Select(pair => new AppendItemDeltaIntent(
                        pair.Key,
                        pair.Value.ToString()))
                    .ToArray();
                var decision = await action(Array.AsReadOnly(deltas));
                if (decision.Stop)
                {
                    _externallyStopped = true;
                    _deltas.Clear();
                    executionCancellation.Cancel();
                }

                return decision.Result;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task FlushAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                await FlushCoreAsync(cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task FlushCoreAsync(CancellationToken cancellationToken)
        {
            foreach (var itemId in _deltas.Keys.ToArray())
            {
                await FlushItemCoreAsync(itemId, cancellationToken);
            }
        }

        private async Task FlushItemCoreAsync(
            Guid itemId,
            CancellationToken cancellationToken)
        {
            if (!_deltas.Remove(itemId, out var buffer) || buffer.Length == 0)
            {
                return;
            }

            _lastSequence = await commit(
                new AppendItemDeltaIntent(itemId, buffer.ToString()),
                cancellationToken);
        }
    }
}

internal sealed class SessionExecutionRun(
    SessionExecution.ExecutionSink sink,
    CancellationTokenSource cancellation,
    Task<SessionExecutionStop> completion) : IAsyncDisposable
{
    public Task<SessionExecutionStop> Completion { get; } = completion;

    public Task<T> StopAsync<T>(
        Func<IReadOnlyList<AppendItemDeltaIntent>, Task<(bool Stop, T Result)>> action) =>
        sink.StopAsync(action);

    public ValueTask DisposeAsync()
    {
        cancellation.Cancel();
        cancellation.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed class SessionExecutionException(SessionError error) : Exception(error.Message)
{
    public SessionError Error { get; } = error;
}

internal sealed partial class SessionService
{
    private readonly SessionConfig _sessionConfig;
    private readonly ISessionExecutor? _executor;
    private readonly string? _executorKind;
    private readonly Action<SessionExecutionFaultPoint>? _executionFaultInjector;
    private readonly ConcurrentDictionary<Guid, TurnSnapshot> _turns = [];
    private readonly ConcurrentDictionary<Guid, SessionItemSnapshot> _items = [];
    private readonly ConcurrentDictionary<Guid, string> _itemText = [];
    private readonly ConcurrentDictionary<Guid, InteractionRuntime> _interactions = [];
    private readonly ConcurrentDictionary<Guid, SessionExecutionRun> _executionRuns = [];
    private readonly ConcurrentDictionary<Guid, Task> _executionTasks = [];
    private readonly ConcurrentDictionary<Guid, ExecutionIdempotency> _executionIdempotency = [];
    private readonly ConcurrentDictionary<Guid, InvocationRecord> _agentInvocations = [];
    private readonly ConcurrentDictionary<Guid, ToolInvocationRecord> _toolInvocations = [];
    private readonly ConcurrentDictionary<DeferredActivationKey, long>
        _deferredToolActivations = [];
    private readonly ConcurrentDictionary<ProviderUsageKey, UsageRecord> _providerUsage = [];
    private readonly ConcurrentDictionary<Guid, CompactionRecord> _compactionCheckpoints = [];
    private readonly ConcurrentDictionary<Guid, byte> _loadedExecutionThreads = [];

    internal async Task<SessionCommandResult<TurnSnapshot>> StartTurnAsync(
        Guid threadId,
        Guid turnId,
        Guid idempotencyKey,
        long expectedSequence,
        CancellationToken cancellationToken = default,
        QueuedTurnInputSnapshot? queuedInput = null,
        bool threadGateHeld = false)
    {
        RequireId(threadId, nameof(threadId), "Thread ID");
        RequireId(turnId, nameof(turnId), "Turn ID");
        RequireId(idempotencyKey, nameof(idempotencyKey), "Idempotency key");
        if (_executor is null || string.IsNullOrWhiteSpace(_executorKind))
        {
            return Rejected<TurnSnapshot>(
                SessionErrorCodes.RuntimeExecutorUnavailable,
                "No session executor is available.",
                currentSequence: expectedSequence);
        }

        var threadGate = GetThreadGate(threadId);
        if (!threadGateHeld)
        {
            await threadGate.WaitAsync(cancellationToken);
        }

        SessionCommandResult<TurnSnapshot> result;
        try
        {
            var thread = await GetSnapshotAsync(threadId, cancellationToken);
            if (thread is null)
            {
                return Rejected<TurnSnapshot>(
                    SessionErrorCodes.NotFound,
                    "Thread was not found.");
            }

            await EnsureExecutionStateLoadedAsync(threadId, cancellationToken);
            if (thread.CurrentSequence != expectedSequence)
            {
                return Rejected<TurnSnapshot>(
                    SessionErrorCodes.SequenceConflict,
                    "Thread sequence does not match.",
                    thread.CurrentSequence);
            }

            if (!CanAcceptNewWork)
            {
                return NewWorkUnavailable<TurnSnapshot>();
            }

            if (thread.Status != ThreadStatus.Active ||
                thread.Availability != ThreadAvailability.Available ||
                thread.ActiveTurnId is not null ||
                queuedInput is not null &&
                (thread.Queue.Count == 0 ||
                 thread.Queue[0].QueueItemId != queuedInput.QueueItemId ||
                 queuedInput.ThreadId != threadId))
            {
                return Rejected<TurnSnapshot>(
                    SessionErrorCodes.InvalidState,
                    "Thread cannot start a turn in its current state.",
                    thread.CurrentSequence);
            }

            var timestamp = _timeProvider.GetUtcNow();
            var effectiveAgentMode =
                queuedInput?.EffectiveAgentMode ?? thread.AgentMode;
            var turn = new TurnSnapshot(
                turnId,
                threadId,
                TurnStatus.Running,
                timestamp,
                timestamp,
                CompletedAt: null,
                Error: null,
                effectiveAgentMode);
            var requestSha256 = RequestHash(
                Wire(SessionEventType.TurnStarted),
                new
                {
                    ThreadId = Wire(threadId),
                    TurnId = Wire(turnId),
                    ExpectedSequence = expectedSequence,
                    QueueItemId = queuedInput is null
                        ? null
                        : Wire(queuedInput.QueueItemId),
                    queuedInput?.Text,
                    EffectiveAgentMode = effectiveAgentMode,
                });
            var userItem = queuedInput is null
                ? null
                : new SessionItemSnapshot(
                    Guid.CreateVersion7(),
                    turnId,
                    SessionItemType.UserMessage,
                    SessionItemStatus.Completed,
                    new TextItemContent(queuedInput.Text),
                    thread.CurrentSequence + 1,
                    timestamp,
                    timestamp);
            var nextQueue = queuedInput is null
                ? thread.Queue
                : thread.Queue
                    .Skip(1)
                    .Select((item, position) => item with { Position = position })
                    .ToArray();
            var nextThread = ExecutionThread(
                thread,
                thread.CurrentSequence + 1,
                timestamp,
                turnId,
                nextQueue);
            var committed = await CommitAsync(
                idempotencyKey,
                Wire(SessionEventType.TurnStarted),
                requestSha256,
                nextThread,
                new TurnStartedFact(
                    turnId,
                    queuedInput?.QueueItemId,
                    userItem?.ItemId,
                    queuedInput?.Text,
                    RequestSha256: requestSha256,
                    effectiveAgentMode),
                SessionEventType.TurnStarted,
                cancellationToken,
                new SessionEventPayload(
                    Turn: turn,
                    Item: userItem,
                    QueueItem: queuedInput));
            if (committed.Status == SessionCommandStatus.Rejected)
            {
                return ConvertResult(committed, turn);
            }

            _turns[turnId] = turn;
            if (userItem is not null)
            {
                _items[userItem.ItemId] = userItem;
                _itemText[userItem.ItemId] = queuedInput!.Text;
            }

            result = ConvertResult(committed, turn);
        }
        finally
        {
            if (!threadGateHeld)
            {
                threadGate.Release();
            }
        }

        BeginExecution(result.Value!, checkpoint: null, resumed: false);
        return result;
    }

    public async Task<SessionCommandResult<TurnSnapshot>> CancelTurnAsync(
        CancelTurnRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireId(request.ThreadId, nameof(request.ThreadId), "Thread ID");
        RequireId(request.TurnId, nameof(request.TurnId), "Turn ID");
        RequireId(request.IdempotencyKey, nameof(request.IdempotencyKey), "Idempotency key");
        var operation = Wire(SessionEventType.TurnCancelled);
        var requestSha256 = RequestHash(
            operation,
            new
            {
                ThreadId = Wire(request.ThreadId),
                TurnId = Wire(request.TurnId),
                IdempotencyKey = Wire(request.IdempotencyKey),
                request.ExpectedSequence,
            });
        var keyGate = GetIdempotencyGate(request.IdempotencyKey);
        await keyGate.WaitAsync(cancellationToken);
        try
        {
            var replay = await TryReplayExecutionCommandAsync<TurnSnapshot>(
                request.IdempotencyKey,
                operation,
                requestSha256,
                cancellationToken);
            if (replay is not null)
            {
                return replay;
            }

            if (_executionRuns.TryGetValue(request.TurnId, out var run))
            {
                return await run.StopAsync(
                    async deltas =>
                    {
                        var result = await CancelTurnCoreAsync(
                            request,
                            operation,
                            requestSha256,
                            deltas,
                            cancellationToken);
                        return (
                            result.Status != SessionCommandStatus.Rejected,
                            result);
                    });
            }

            return await CancelTurnCoreAsync(
                request,
                operation,
                requestSha256,
                [],
                cancellationToken);
        }
        finally
        {
            keyGate.Release();
        }
    }

    private async Task<SessionCommandResult<TurnSnapshot>> CancelTurnCoreAsync(
        CancelTurnRequest request,
        string operation,
        string requestSha256,
        IReadOnlyList<AppendItemDeltaIntent> bufferedDeltas,
        CancellationToken cancellationToken)
    {
        var threadGate = GetThreadGate(request.ThreadId);
        await threadGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureExecutionStateLoadedAsync(
                request.ThreadId,
                cancellationToken);
            var thread = await GetSnapshotAsync(request.ThreadId, cancellationToken);
            if (thread is null || !_turns.TryGetValue(request.TurnId, out var turn))
            {
                return Rejected<TurnSnapshot>(
                    SessionErrorCodes.NotFound,
                    "Turn was not found.");
            }

            if (thread.CurrentSequence != request.ExpectedSequence)
            {
                return Rejected<TurnSnapshot>(
                    SessionErrorCodes.SequenceConflict,
                    "Thread sequence does not match.",
                    thread.CurrentSequence);
            }

            if (turn.Status is not (
                TurnStatus.Running or
                TurnStatus.WaitingApproval or
                TurnStatus.WaitingInput))
            {
                return Rejected<TurnSnapshot>(
                    SessionErrorCodes.InvalidState,
                    "Turn is already terminal.",
                    thread.CurrentSequence);
            }

            foreach (var delta in bufferedDeltas)
            {
                await AppendDeltaAsync(
                    thread,
                    turn,
                    delta,
                    CancellationToken.None);
                thread = _snapshots[request.ThreadId];
            }

            thread = await TerminalizeActiveItemsAsync(
                thread,
                turn,
                SessionItemStatus.Cancelled,
                error: null,
                CancellationToken.None);
            var timestamp = _timeProvider.GetUtcNow();
            var cancelled = turn with
            {
                Status = TurnStatus.Cancelled,
                UpdatedAt = timestamp,
                CompletedAt = timestamp,
            };
            var nextThread = ExecutionThread(
                thread,
                thread.CurrentSequence + 1,
                timestamp,
                activeTurnId: null);
            var committed = await CommitAsync(
                request.IdempotencyKey,
                operation,
                requestSha256,
                nextThread,
                new TurnTerminalFact(request.TurnId, null, requestSha256),
                SessionEventType.TurnCancelled,
                CancellationToken.None,
                new SessionEventPayload(Turn: cancelled));
            if (committed.Status == SessionCommandStatus.Rejected)
            {
                return ConvertResult(committed, cancelled);
            }

            _turns[request.TurnId] = cancelled;
            var result = ConvertResult(committed, cancelled);
            _executionIdempotency[request.IdempotencyKey] =
                new ExecutionIdempotency(operation, requestSha256, result);
            return result;
        }
        finally
        {
            threadGate.Release();
        }
    }

    public async Task<SessionCommandResult<PendingInteractionSnapshot>>
        ResolveInteractionAsync(
            ResolveInteractionRequest request,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Response);
        RequireId(request.ThreadId, nameof(request.ThreadId), "Thread ID");
        RequireId(request.TurnId, nameof(request.TurnId), "Turn ID");
        RequireId(request.InteractionId, nameof(request.InteractionId), "Interaction ID");
        RequireId(request.IdempotencyKey, nameof(request.IdempotencyKey), "Idempotency key");
        var operation = Wire(SessionEventType.InteractionResolved);
        var response = SerializeContent(request.Response);
        var requestSha256 = RequestHash(
            operation,
            new
            {
                ThreadId = Wire(request.ThreadId),
                TurnId = Wire(request.TurnId),
                InteractionId = Wire(request.InteractionId),
                Response = response,
                IdempotencyKey = Wire(request.IdempotencyKey),
                request.ExpectedSequence,
            });
        var keyGate = GetIdempotencyGate(request.IdempotencyKey);
        await keyGate.WaitAsync(cancellationToken);
        SessionCommandResult<PendingInteractionSnapshot> result;
        SessionExecutionCheckpoint? checkpoint;
        try
        {
            var replay =
                await TryReplayExecutionCommandAsync<PendingInteractionSnapshot>(
                    request.IdempotencyKey,
                    operation,
                    requestSha256,
                    cancellationToken);
            if (replay is not null)
            {
                return replay;
            }

            var threadGate = GetThreadGate(request.ThreadId);
            await threadGate.WaitAsync(cancellationToken);
            try
            {
                await EnsureExecutionStateLoadedAsync(
                    request.ThreadId,
                    cancellationToken);
                var thread = await GetSnapshotAsync(request.ThreadId, cancellationToken);
                if (thread is null ||
                    !_turns.TryGetValue(request.TurnId, out var turn) ||
                    !_interactions.TryGetValue(
                        request.InteractionId,
                        out var interaction))
                {
                    return Rejected<PendingInteractionSnapshot>(
                        SessionErrorCodes.NotFound,
                        "Pending interaction was not found.");
                }

                if (thread.CurrentSequence != request.ExpectedSequence)
                {
                    return Rejected<PendingInteractionSnapshot>(
                        SessionErrorCodes.SequenceConflict,
                        "Thread sequence does not match.",
                        thread.CurrentSequence);
                }

                if (interaction.Snapshot.IsResolved)
                {
                    return Rejected<PendingInteractionSnapshot>(
                        SessionErrorCodes.InteractionAlreadyResolved,
                        "Interaction was already resolved.",
                        thread.CurrentSequence);
                }

                if (turn.Status != WaitingStatus(interaction.Snapshot.Type))
                {
                    return Rejected<PendingInteractionSnapshot>(
                        SessionErrorCodes.InvalidState,
                        "Turn is not waiting for this interaction.",
                        thread.CurrentSequence);
                }

                var responseItemType = ResponseItemType(
                    interaction.Snapshot.Type,
                    request.Response);
                var contentText = ContentText(request.Response);
                var contentBytes = Encoding.UTF8.GetBytes(contentText ?? string.Empty);
                var timestamp = _timeProvider.GetUtcNow();
                var responseItem = new SessionItemSnapshot(
                    Guid.CreateVersion7(),
                    request.TurnId,
                    responseItemType,
                    SessionItemStatus.Completed,
                    request.Response,
                    thread.CurrentSequence + 1,
                    timestamp,
                    timestamp);
                var resolved = interaction.Snapshot with { IsResolved = true };
                var nextThread = ExecutionThread(
                    thread,
                    thread.CurrentSequence + 1,
                    timestamp,
                    request.TurnId);
                var committed = await CommitAsync(
                    request.IdempotencyKey,
                    operation,
                    requestSha256,
                    nextThread,
                    new InteractionResolvedFact(
                        request.InteractionId,
                        responseItem.ItemId,
                        responseItem.Type,
                        response,
                        contentText,
                        contentBytes.Length,
                        Hash(contentBytes),
                        requestSha256),
                    SessionEventType.InteractionResolved,
                    cancellationToken,
                    new SessionEventPayload(
                        Turn: turn,
                        Item: responseItem,
                        Interaction: resolved));
                if (committed.Status == SessionCommandStatus.Rejected)
                {
                    return ConvertResult(committed, resolved);
                }

                _items[responseItem.ItemId] = responseItem;
                _itemText[responseItem.ItemId] = contentText ?? string.Empty;
                _interactions[request.InteractionId] =
                    interaction with
                    {
                        Snapshot = resolved,
                        Resolution = response,
                    };
                result = ConvertResult(committed, resolved);
                _executionIdempotency[request.IdempotencyKey] =
                    new ExecutionIdempotency(operation, requestSha256, result);
                checkpoint = interaction.Checkpoint;
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

        _executionFaultInjector?.Invoke(
            SessionExecutionFaultPoint.AfterResolutionCommittedBeforeResume);
        BeginResume(request.ThreadId, request.TurnId, request.InteractionId, checkpoint!);
        return result;
    }

    internal async Task WaitForExecutionAsync(
        Guid turnId,
        CancellationToken cancellationToken = default)
    {
        RequireId(turnId, nameof(turnId), "Turn ID");
        if (_executionTasks.TryGetValue(turnId, out var task))
        {
            await task.WaitAsync(cancellationToken);
        }
    }

    internal async Task ProcessInteractionTimeoutsAsync(
        CancellationToken cancellationToken = default)
    {
        var expired = _interactions.Values
            .Where(interaction =>
                !interaction.Snapshot.IsResolved &&
                interaction.Snapshot.TimeoutAt is { } timeout &&
                timeout <= _timeProvider.GetUtcNow())
            .Select(interaction => interaction.Snapshot.TurnId)
            .Distinct()
            .ToArray();
        foreach (var turnId in expired)
        {
            await FailTurnAsync(
                turnId,
                new SessionError(
                    SessionErrorCodes.RuntimeInterrupted,
                    "Pending interaction timed out.",
                    IsRetryable: false),
                cancellationToken);
        }
    }

    internal async Task RecoverExecutionAsync(
        Guid threadId,
        CancellationToken cancellationToken = default)
    {
        RequireId(threadId, nameof(threadId), "Thread ID");
        await EnsureExecutionStateLoadedAsync(threadId, cancellationToken);
        var thread = await GetSnapshotAsync(threadId, cancellationToken);
        if (thread?.ActiveTurnId is not { } turnId ||
            !_turns.TryGetValue(turnId, out var turn))
        {
            return;
        }

        if (turn.Status == TurnStatus.Running)
        {
            if (HasRecoverableToolCursor(turn))
            {
                BeginExecution(
                    turn,
                    checkpoint: null,
                    resumed: false);
                return;
            }

            await FailTurnAsync(
                turnId,
                new SessionError(
                    SessionErrorCodes.RuntimeInterrupted,
                    "Running execution was interrupted before a recoverable checkpoint.",
                    IsRetryable: false),
                cancellationToken);
            return;
        }

        var interaction = _interactions.Values
            .Where(candidate =>
                candidate.Snapshot.TurnId == turnId &&
                candidate.Snapshot.Type ==
                (turn.Status == TurnStatus.WaitingApproval
                    ? SessionInteractionType.Approval
                    : SessionInteractionType.UserInput))
            .OrderByDescending(candidate => candidate.Snapshot.CreatedAt)
            .FirstOrDefault();
        if (interaction is null ||
            string.IsNullOrWhiteSpace(_executorKind) ||
            !SessionExecutionCheckpointCodec.IsValid(
                interaction.Checkpoint,
                _executorKind))
        {
            await FailTurnAsync(
                turnId,
                new SessionError(
                    SessionErrorCodes.RuntimeContinuationMissing,
                    "Execution checkpoint is missing, unsupported, or corrupt.",
                    IsRetryable: false),
                cancellationToken);
            return;
        }

        if (interaction.Snapshot.TimeoutAt is { } timeout &&
            timeout <= _timeProvider.GetUtcNow())
        {
            await ProcessInteractionTimeoutsAsync(cancellationToken);
            return;
        }

        if (interaction.Snapshot.IsResolved)
        {
            BeginResume(
                threadId,
                turnId,
                interaction.Snapshot.InteractionId,
                interaction.Checkpoint);
        }
    }

    private bool HasRecoverableToolCursor(TurnSnapshot turn)
    {
        if (!_agentInvocations.TryGetValue(turn.TurnId, out var invocation) ||
            invocation.Snapshot.Tools is not { } tools)
        {
            return false;
        }

        var frames = _items.Values
            .Where(item =>
                item.TurnId == turn.TurnId &&
                item.Type == SessionItemType.ToolCall &&
                item.Status == SessionItemStatus.Completed &&
                item.Content is ToolCallItemContent)
            .OrderBy(item => item.Sequence)
            .ThenBy(item => item.ItemId)
            .ToArray();
        if (frames.Length == 0)
        {
            return false;
        }

        var byFrame = frames.ToDictionary(item => item.ItemId);
        var states = _toolInvocations.Values
            .Where(item => item.Snapshot.TurnId == turn.TurnId)
            .ToArray();
        if (states.Any(state =>
                !byFrame.TryGetValue(state.ToolCallItemId, out var item) ||
                item.Content is not ToolCallItemContent content ||
                state.CallIndex < 0 ||
                state.CallIndex >= content.Calls.Count ||
                !string.Equals(
                    content.Calls[state.CallIndex].ProviderToolCallId,
                    state.Snapshot.ProviderToolCallId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    content.Calls[state.CallIndex].ProviderToolName,
                    state.Snapshot.ProviderToolName,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    content.Calls[state.CallIndex].ArgumentsSha256,
                    state.Snapshot.ArgumentsSha256,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    state.Snapshot.SnapshotSha256,
                    tools.SnapshotSha256,
                    StringComparison.Ordinal)) ||
            states.GroupBy(state => (state.ToolCallItemId, state.CallIndex))
                .Any(group => group.Count() != 1))
        {
            return false;
        }

        bool IsComplete(SessionItemSnapshot frame, int callIndex)
        {
            var state = states.SingleOrDefault(candidate =>
                candidate.ToolCallItemId == frame.ItemId &&
                candidate.CallIndex == callIndex);
            return state?.Snapshot.CompletedAt is not null &&
                   state.Snapshot.ResultItemId is { } resultItemId &&
                   _items.TryGetValue(resultItemId, out var resultItem) &&
                   resultItem.TurnId == turn.TurnId &&
                   resultItem.Type == SessionItemType.ToolResult &&
                   resultItem.Status == SessionItemStatus.Completed &&
                   resultItem.Content is ToolResultItemContent result &&
                   result.Result.ToolInvocationId ==
                   state.Snapshot.ToolInvocationId;
        }

        foreach (var frame in frames[..^1])
        {
            var content = (ToolCallItemContent)frame.Content;
            if (content.Calls.Count == 0 ||
                Enumerable.Range(0, content.Calls.Count)
                    .Any(callIndex => !IsComplete(frame, callIndex)))
            {
                return false;
            }
        }

        var latest = frames[^1];
        var latestContent = (ToolCallItemContent)latest.Content;
        if (latestContent.Calls.Count == 0 ||
            _items.Values.Any(item =>
                item.TurnId == turn.TurnId &&
                item.Sequence > latest.Sequence &&
                item.Type is
                    SessionItemType.UserMessage or
                    SessionItemType.AgentMessage or
                    SessionItemType.Reasoning or
                    SessionItemType.ToolCall))
        {
            return false;
        }

        return Enumerable.Range(0, latestContent.Calls.Count)
            .All(callIndex =>
            {
                var state = states.SingleOrDefault(candidate =>
                    candidate.ToolCallItemId == latest.ItemId &&
                    candidate.CallIndex == callIndex);
                return state is null ||
                       state.Snapshot.CompletedAt is null ||
                       IsComplete(latest, callIndex);
            });
    }

    private void BeginExecution(
        TurnSnapshot turn,
        SessionExecutionCheckpoint? checkpoint,
        bool resumed,
        Guid? interactionId = null)
    {
        if (_executor is null)
        {
            _executionTasks[turn.TurnId] = FailTurnAsync(
                turn.TurnId,
                new SessionError(
                    SessionErrorCodes.RuntimeExecutorUnavailable,
                    "Session executor is unavailable.",
                    IsRetryable: true),
                CancellationToken.None);
            return;
        }

        var thread = _snapshots[turn.ThreadId];
        var contextTurn = resumed
            ? turn with { Status = TurnStatus.Running }
            : turn;
        var context = new AgentSession(
            thread,
            contextTurn,
            ModelHistory(turn.ThreadId),
            checkpoint,
            _compactionCheckpoints.GetValueOrDefault(turn.ThreadId)?.Checkpoint,
            _agentInvocations.GetValueOrDefault(turn.TurnId)?.Snapshot,
            _toolInvocations.Values
                .Where(item => item.Snapshot.TurnId == turn.TurnId)
                .OrderBy(item => item.Sequence)
                .Select(item => new AgentToolInvocationSnapshot(
                    item.Snapshot,
                    item.ToolCallItemId,
                    item.CallIndex)),
            _providerUsage.Values
                .Where(item =>
                    _agentInvocations.TryGetValue(turn.TurnId, out var invocation) &&
                    item.Usage.InvocationId == invocation.Snapshot.InvocationId)
                .OrderBy(item => item.Sequence)
                .Select(item => item.Usage),
            _deferredToolActivations
                .Where(item => item.Key.TurnId == turn.TurnId)
                .OrderBy(item => item.Value)
                .Select(item => item.Key.ToolDefinitionId));
        var execution = new SessionExecution(
            _executor,
            _sessionConfig,
            _timeProvider);
        var run = execution.Start(
            context,
            thread.CurrentSequence,
            (intent, token) => ApplyExecutionIntentAsync(
                turn.ThreadId,
                turn.TurnId,
                intent,
                token),
            resumed
                ? token => ResumeExecutionAsync(
                    turn.ThreadId,
                    turn.TurnId,
                    interactionId!.Value,
                    token)
                : null);
        if (_executionRuns.TryRemove(turn.TurnId, out var previous))
        {
            _ = previous.DisposeAsync();
        }

        _executionRuns[turn.TurnId] = run;
        _executionTasks[turn.TurnId] =
            ObserveExecutionAsync(turn.ThreadId, turn.TurnId, run);
    }

    private void BeginResume(
        Guid threadId,
        Guid turnId,
        Guid interactionId,
        SessionExecutionCheckpoint checkpoint)
    {
        if (!_turns.TryGetValue(turnId, out var turn))
        {
            return;
        }

        if (_executor is null ||
            string.IsNullOrWhiteSpace(_executorKind) ||
            !SessionExecutionCheckpointCodec.IsValid(checkpoint, _executorKind))
        {
            _executionTasks[turnId] = FailTurnAsync(
                turnId,
                new SessionError(
                    _executor is null
                        ? SessionErrorCodes.RuntimeExecutorUnavailable
                        : SessionErrorCodes.RuntimeContinuationMissing,
                    _executor is null
                        ? "Session executor is unavailable."
                        : "Execution checkpoint is unsupported or corrupt.",
                    IsRetryable: _executor is null),
                CancellationToken.None);
            return;
        }

        BeginExecution(turn, checkpoint, resumed: true, interactionId);
    }

    private async Task ObserveExecutionAsync(
        Guid threadId,
        Guid turnId,
        SessionExecutionRun run)
    {
        try
        {
            var stop = await run.Completion;
            if (stop == SessionExecutionStop.Returned)
            {
                await FailTurnAsync(
                    turnId,
                    new SessionError(
                        SessionErrorCodes.RuntimeInterrupted,
                        "Executor returned before producing a terminal or waiting intent.",
                        IsRetryable: false),
                    CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            if (_turns.TryGetValue(turnId, out var turn) &&
                turn.Status != TurnStatus.Cancelled)
            {
                await FailTurnAsync(
                    turnId,
                    new SessionError(
                        SessionErrorCodes.RuntimeInterrupted,
                        "Execution was interrupted.",
                        IsRetryable: false),
                    CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            var error = exception is SessionExecutionException execution
                ? execution.Error
                : new SessionError(
                    SessionErrorCodes.RuntimeExecutorUnavailable,
                    $"Executor failed: {exception.Message}",
                    IsRetryable: true);
            await FailTurnAsync(turnId, error, CancellationToken.None);
        }
        finally
        {
            var terminal = _turns.TryGetValue(turnId, out var turn) &&
                           turn.Status is TurnStatus.Completed
                               or TurnStatus.Failed
                               or TurnStatus.Cancelled;
            if (terminal &&
                _executionRuns.TryGetValue(turnId, out var current) &&
                ReferenceEquals(current, run) &&
                _executionRuns.TryRemove(turnId, out _))
            {
                await run.DisposeAsync();
            }

            if (terminal)
            {
                await TryScheduleNextAsync(threadId, CancellationToken.None);
            }
        }
    }

    private async ValueTask<long> ApplyExecutionIntentAsync(
        Guid threadId,
        Guid turnId,
        SessionExecutionIntent intent,
        CancellationToken cancellationToken)
    {
        var threadGate = GetThreadGate(threadId);
        await threadGate.WaitAsync(cancellationToken);
        try
        {
            var thread = await GetSnapshotAsync(threadId, cancellationToken)
                ?? throw ExecutionError(
                    SessionErrorCodes.NotFound,
                    "Thread was not found.");
            if (!_turns.TryGetValue(turnId, out var turn) ||
                thread.ActiveTurnId != turnId)
            {
                throw ExecutionError(
                    SessionErrorCodes.InvalidState,
                    "Execution does not own the active turn.");
            }

            return intent switch
            {
                StartItemIntent start =>
                    await StartItemAsync(thread, turn, start, cancellationToken),
                AppendItemDeltaIntent delta =>
                    await AppendDeltaAsync(thread, turn, delta, cancellationToken),
                CompleteItemIntent complete =>
                    await CompleteItemAsync(thread, turn, complete, cancellationToken),
                FailItemIntent failed =>
                    await FailItemAsync(thread, turn, failed, cancellationToken),
                RecordAgentInvocationSnapshotIntent invocation =>
                    await RecordAgentInvocationAsync(
                        thread,
                        turn,
                        invocation,
                        cancellationToken),
                RecordProviderUsageIntent usage =>
                    await RecordProviderUsageAsync(
                        thread,
                        turn,
                        usage,
                        cancellationToken),
                RecordCompactionCheckpointIntent compaction =>
                    await RecordCompactionCheckpointAsync(
                        thread,
                        turn,
                        compaction,
                        cancellationToken),
                RecordToolCallIntent toolCall =>
                    await RecordToolCallAsync(
                        thread,
                        turn,
                        toolCall,
                        cancellationToken),
                RecordToolInvocationStartedIntent toolStarted =>
                    await RecordToolInvocationStartedAsync(
                        thread,
                        turn,
                        toolStarted,
                        cancellationToken),
                RecordToolInvocationAttemptStartedIntent toolAttempt =>
                    await RecordToolInvocationAttemptStartedAsync(
                        thread,
                        turn,
                        toolAttempt,
                        cancellationToken),
                RecordToolInvocationTerminalIntent toolTerminal =>
                    await RecordToolInvocationTerminalAsync(
                        thread,
                        turn,
                        toolTerminal,
                        cancellationToken),
                RecordDeferredToolsActivatedIntent deferred =>
                    await RecordDeferredToolsActivatedAsync(
                        thread,
                        turn,
                        deferred,
                        cancellationToken),
                WaitForInteractionIntent waiting =>
                    await WaitForInteractionAsync(
                        thread,
                        turn,
                        waiting,
                        cancellationToken),
                CompleteTurnIntent =>
                    await CompleteTurnAsync(thread, turn, cancellationToken),
                FailTurnIntent failed =>
                    await FailTurnFromIntentAsync(
                        thread,
                        turn,
                        failed,
                        cancellationToken),
                _ => throw ExecutionError(
                    SessionErrorCodes.InvalidState,
                    "Executor emitted an unsupported intent."),
            };
        }
        finally
        {
            threadGate.Release();
        }
    }

    private async ValueTask<long> RecordAgentInvocationAsync(
        ThreadSnapshot thread,
        TurnSnapshot turn,
        RecordAgentInvocationSnapshotIntent intent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent.Snapshot);
        var snapshot = intent.Snapshot;
        RequireId(snapshot.InvocationId, nameof(snapshot.InvocationId), "Invocation ID");
        if (turn.Status != TurnStatus.Running ||
            snapshot.EffectiveAgentMode != turn.EffectiveAgentMode ||
            !string.Equals(snapshot.ProviderId, thread.ProviderId, StringComparison.Ordinal) ||
            !string.Equals(snapshot.ModelId, thread.ModelId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(snapshot.TokenizerProfileId) ||
            string.IsNullOrWhiteSpace(snapshot.TokenizerProfileVersion) ||
            string.IsNullOrWhiteSpace(snapshot.ResponsePrompt.Version) ||
            string.IsNullOrWhiteSpace(snapshot.CompactionPrompt.Version) ||
            !IsLowerSha256(snapshot.ResponsePrompt.SystemMessageSha256) ||
            !IsLowerSha256(snapshot.CompactionPrompt.SystemMessageSha256) ||
            !IsLowerSha256(snapshot.ConfigurationSha256) ||
            snapshot.CapabilityRevision < 0 ||
            (snapshot.CapabilityRevision > 0 && snapshot.Skills is null) ||
            (snapshot.Skills is { } skills &&
             !IsLowerSha256(skills.SnapshotSha256)) ||
            snapshot.ResponsePrompt.TokenCount < 0 ||
            snapshot.CompactionPrompt.TokenCount < 0 ||
            snapshot.ContextWindowTokens <= 0 ||
            snapshot.MaxOutputTokens <= 0 ||
            snapshot.ReasoningEffort is not ("low" or "high" or "max"))
        {
            throw ExecutionError(
                SessionErrorCodes.InvalidState,
                "Agent invocation snapshot does not match the active turn.");
        }

        if (_agentInvocations.TryGetValue(turn.TurnId, out var existing))
        {
            if (existing.Snapshot == snapshot)
            {
                return existing.Sequence;
            }

            throw ExecutionError(
                SessionErrorCodes.InvalidState,
                "The active turn already has a different invocation snapshot.");
        }

        var hash = InternalRequestHash(
            SessionEventType.AgentInvocationSnapshotRecorded,
            new { turn.TurnId, Snapshot = snapshot });
        var sequence = await CommitExecutionFactAsync(
            thread,
            new AgentInvocationSnapshotRecordedFact(
                turn.TurnId,
                snapshot,
                hash.RequestSha256),
            SessionEventType.AgentInvocationSnapshotRecorded,
            new SessionEventPayload(Turn: turn, Invocation: snapshot),
            hash,
            cancellationToken);
        _agentInvocations[turn.TurnId] = new InvocationRecord(snapshot, sequence);
        return sequence;
    }

    private async ValueTask<long> RecordProviderUsageAsync(
        ThreadSnapshot thread,
        TurnSnapshot turn,
        RecordProviderUsageIntent intent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent.Usage);
        var usage = intent.Usage;
        var key = new ProviderUsageKey(
            usage.InvocationId,
            usage.AttemptNumber,
            usage.Purpose);
        if (!_agentInvocations.TryGetValue(turn.TurnId, out var invocation) ||
            invocation.Snapshot.InvocationId != usage.InvocationId ||
            usage.AttemptNumber <= 0 ||
            usage.PromptTokens < 0 ||
            usage.CompletionTokens < 0 ||
            usage.CachedPromptTokens < 0 ||
            usage.CachedPromptTokens > usage.PromptTokens ||
            usage.ReasoningCompletionTokens < 0 ||
            usage.ReasoningCompletionTokens > usage.CompletionTokens ||
            usage.TotalTokens != usage.PromptTokens + usage.CompletionTokens ||
            usage.IsEstimate != (usage.Source == ProviderUsageSource.LocalEstimate))
        {
            throw ExecutionError(
                SessionErrorCodes.InvalidState,
                "Provider usage does not match the active invocation.");
        }

        if (_providerUsage.TryGetValue(key, out var existing))
        {
            if (existing.Usage == usage)
            {
                return existing.Sequence;
            }

            throw ExecutionError(
                AgentErrorCodes.ProviderInvalidStream,
                "Provider returned conflicting usage for one call.");
        }

        var hash = InternalRequestHash(
            SessionEventType.ProviderUsageRecorded,
            new { turn.TurnId, Usage = usage });
        var sequence = await CommitExecutionFactAsync(
            thread,
            new ProviderUsageRecordedFact(
                turn.TurnId,
                usage,
                hash.RequestSha256),
            SessionEventType.ProviderUsageRecorded,
            new SessionEventPayload(Turn: turn, Usage: usage),
            hash,
            cancellationToken);
        _providerUsage[key] = new UsageRecord(usage, sequence);
        return sequence;
    }

    private async ValueTask<long> RecordCompactionCheckpointAsync(
        ThreadSnapshot thread,
        TurnSnapshot turn,
        RecordCompactionCheckpointIntent intent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent.Checkpoint);
        var checkpoint = intent.Checkpoint;
        if (!_agentInvocations.TryGetValue(turn.TurnId, out var invocation) ||
            turn.Status != TurnStatus.Running ||
            checkpoint.SchemaVersion is not (1 or 2) ||
            string.IsNullOrWhiteSpace(checkpoint.Summary) ||
            checkpoint.SourceStartSequence <= 0 ||
            checkpoint.SourceEndSequence < checkpoint.SourceStartSequence ||
            !CompactionCheckpointIntegrity.IsLowerSha256(
                checkpoint.SourceMessagesSha256) ||
            checkpoint.SummaryTokenCount <= 0 ||
            !CompactionCheckpointIntegrity.IsValidSummary(checkpoint.Summary) ||
            !string.Equals(
                checkpoint.SummaryPromptVersion,
                invocation.Snapshot.CompactionPrompt.Version,
                StringComparison.Ordinal) ||
            !string.Equals(
                checkpoint.TokenizerProfileId,
                invocation.Snapshot.TokenizerProfileId,
                StringComparison.Ordinal) ||
            !string.Equals(
                checkpoint.TokenizerProfileVersion,
                invocation.Snapshot.TokenizerProfileVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                checkpoint.SummarySha256,
                CompactionCheckpointIntegrity.Sha256(checkpoint.Summary),
                StringComparison.Ordinal) ||
            !CompactionSourceMatches(checkpoint, _items.Values))
        {
            throw ExecutionError(
                SessionErrorCodes.InvalidState,
                "Compaction checkpoint does not match the active invocation.");
        }

        if (_compactionCheckpoints.TryGetValue(thread.ThreadId, out var existing))
        {
            if (existing.Checkpoint == checkpoint)
            {
                return existing.Sequence;
            }

            if (checkpoint.SourceStartSequence !=
                    existing.Checkpoint.SourceStartSequence ||
                checkpoint.SourceEndSequence <=
                    existing.Checkpoint.SourceEndSequence)
            {
                throw ExecutionError(
                    SessionErrorCodes.InvalidState,
                    "Compaction checkpoint does not extend the authoritative range.");
            }
        }

        var hash = InternalRequestHash(
            SessionEventType.CompactionCheckpointRecorded,
            new { turn.TurnId, Checkpoint = checkpoint });
        var sequence = await CommitExecutionFactAsync(
            thread,
            new CompactionCheckpointRecordedFact(
                turn.TurnId,
                checkpoint,
                hash.RequestSha256),
            SessionEventType.CompactionCheckpointRecorded,
            new SessionEventPayload(Turn: turn, Compaction: checkpoint),
            hash,
            cancellationToken);
        _compactionCheckpoints[thread.ThreadId] =
            new CompactionRecord(checkpoint, sequence);
        return sequence;
    }

    private async ValueTask<long> RecordToolCallAsync(
        ThreadSnapshot thread,
        TurnSnapshot turn,
        RecordToolCallIntent intent,
        CancellationToken cancellationToken)
    {
        RequireId(intent.ItemId, nameof(intent.ItemId), "Item ID");
        ArgumentNullException.ThrowIfNull(intent.Content);
        if (_items.TryGetValue(intent.ItemId, out var existingItem))
        {
            if (existingItem.TurnId == turn.TurnId &&
                existingItem.Type == SessionItemType.ToolCall &&
                existingItem.Status == SessionItemStatus.Completed &&
                existingItem.Content is ToolCallItemContent existingContent &&
                ThreadJournal.Canonicalize(SerializeContent(existingContent))
                    .AsSpan()
                    .SequenceEqual(
                        ThreadJournal.Canonicalize(
                            SerializeContent(intent.Content))))
            {
                return existingItem.Sequence;
            }

            throw ExecutionError(
                SessionErrorCodes.InvalidState,
                "Tool Call Item ID already has different content.");
        }

        if (turn.Status != TurnStatus.Running ||
            !IsValidToolCallContent(intent.Content) ||
            intent.Content.AgentMessageItemId is { } agentMessageItemId &&
            (!_items.TryGetValue(agentMessageItemId, out var agentMessage) ||
             agentMessage.TurnId != turn.TurnId ||
             agentMessage.Type != SessionItemType.AgentMessage ||
             agentMessage.Status != SessionItemStatus.Completed))
        {
            throw ExecutionError(
                SessionErrorCodes.InvalidState,
                "Executor emitted an invalid Tool Call frame.");
        }

        var timestamp = _timeProvider.GetUtcNow();
        var content = SerializeContent(intent.Content);
        var bytes = ThreadJournal.Canonicalize(content);
        var item = new SessionItemSnapshot(
            intent.ItemId,
            turn.TurnId,
            SessionItemType.ToolCall,
            SessionItemStatus.Completed,
            intent.Content,
            thread.CurrentSequence + 1,
            timestamp,
            timestamp);
        var hash = InternalRequestHash(
            SessionEventType.ToolCallRecorded,
            new { intent.ItemId, turn.TurnId, Content = content });
        var sequence = await CommitExecutionFactAsync(
            thread,
            new ToolCallRecordedFact(
                intent.ItemId,
                turn.TurnId,
                content,
                bytes.Length,
                Hash(bytes),
                hash.RequestSha256),
            SessionEventType.ToolCallRecorded,
            new SessionEventPayload(Turn: turn, Item: item),
            hash,
            cancellationToken);
        _items[intent.ItemId] = item;
        _itemText[intent.ItemId] = string.Empty;
        return sequence;
    }

    private async ValueTask<long> RecordToolInvocationStartedAsync(
        ThreadSnapshot thread,
        TurnSnapshot turn,
        RecordToolInvocationStartedIntent intent,
        CancellationToken cancellationToken)
    {
        RequireId(
            intent.ToolInvocationId,
            nameof(intent.ToolInvocationId),
            "Tool Invocation ID");
        if (_toolInvocations.TryGetValue(intent.ToolInvocationId, out var existing))
        {
            if (StartedIntentMatches(existing, thread, turn, intent))
            {
                return existing.Sequence;
            }

            throw ExecutionError(
                SessionErrorCodes.InvalidState,
                "Tool Invocation already has a different start.");
        }

        if (turn.Status != TurnStatus.Running ||
            !_items.TryGetValue(intent.ToolCallItemId, out var callItem) ||
            callItem.TurnId != turn.TurnId ||
            callItem.Type != SessionItemType.ToolCall ||
            callItem.Status != SessionItemStatus.Completed ||
            callItem.Content is not ToolCallItemContent callContent ||
            intent.CallIndex < 0 ||
            intent.CallIndex >= callContent.Calls.Count ||
            !CallMatches(callContent.Calls[intent.CallIndex], intent) ||
            _toolInvocations.Values.Any(invocation =>
                invocation.ToolCallItemId == intent.ToolCallItemId &&
                invocation.CallIndex == intent.CallIndex) ||
            (intent.ToolDefinitionId is null) != (intent.RuntimeBindingId is null) ||
            !IsLowerSha256(intent.SnapshotSha256) ||
            !IsLowerSha256(intent.ArgumentsSha256))
        {
            throw ExecutionError(
                SessionErrorCodes.InvalidState,
                "Tool Invocation start does not match the recorded Tool Call.");
        }

        var timestamp = _timeProvider.GetUtcNow();
        var snapshot = new ToolInvocationSnapshot(
            intent.ToolInvocationId,
            thread.ThreadId,
            turn.TurnId,
            intent.ProviderToolCallId,
            intent.ProviderToolName,
            intent.ToolDefinitionId,
            intent.RuntimeBindingId,
            intent.SnapshotSha256,
            intent.ArgumentsSha256,
            ToolInvocationStatus.Started,
            AttemptCount: 0,
            ResultItemId: null,
            ErrorCode: null,
            timestamp,
            timestamp,
            CompletedAt: null);
        var hash = InternalRequestHash(
            SessionEventType.ToolInvocationStarted,
            new { turn.TurnId, Intent = intent });
        var sequence = await CommitExecutionFactAsync(
            thread,
            new ToolInvocationStartedFact(
                intent.ToolInvocationId,
                turn.TurnId,
                intent.ToolCallItemId,
                intent.CallIndex,
                intent.ProviderToolCallId,
                intent.ProviderToolName,
                intent.ToolDefinitionId,
                intent.RuntimeBindingId,
                intent.SnapshotSha256,
                intent.ArgumentsSha256,
                hash.RequestSha256),
            SessionEventType.ToolInvocationStarted,
            new SessionEventPayload(Turn: turn, ToolInvocation: snapshot),
            hash,
            cancellationToken);
        _toolInvocations[intent.ToolInvocationId] =
            new ToolInvocationRecord(
                snapshot,
                intent.ToolCallItemId,
                intent.CallIndex,
                sequence);
        return sequence;
    }

    private async ValueTask<long> RecordToolInvocationAttemptStartedAsync(
        ThreadSnapshot thread,
        TurnSnapshot turn,
        RecordToolInvocationAttemptStartedIntent intent,
        CancellationToken cancellationToken)
    {
        if (!_toolInvocations.TryGetValue(intent.ToolInvocationId, out var existing))
        {
            throw ExecutionError(
                SessionErrorCodes.InvalidState,
                "Tool Invocation attempt has no start.");
        }

        if (existing.Snapshot.CompletedAt is null &&
            existing.Snapshot.AttemptCount == intent.AttemptNumber &&
            existing.Snapshot.Status == ToolInvocationStatus.Started)
        {
            return existing.Sequence;
        }

        if (turn.Status != TurnStatus.Running ||
            existing.Snapshot.TurnId != turn.TurnId ||
            existing.Snapshot.CompletedAt is not null ||
            intent.AttemptNumber != existing.Snapshot.AttemptCount + 1 ||
            intent.AttemptNumber is < 1 or > 2)
        {
            throw ExecutionError(
                SessionErrorCodes.InvalidState,
                "Tool Invocation attempt is out of order.");
        }

        var timestamp = _timeProvider.GetUtcNow();
        var snapshot = existing.Snapshot with
        {
            Status = ToolInvocationStatus.Started,
            AttemptCount = intent.AttemptNumber,
            UpdatedAt = timestamp,
        };
        var hash = InternalRequestHash(
            SessionEventType.ToolInvocationAttemptStarted,
            new { turn.TurnId, Intent = intent });
        var sequence = await CommitExecutionFactAsync(
            thread,
            new ToolInvocationAttemptStartedFact(
                intent.ToolInvocationId,
                intent.AttemptNumber,
                hash.RequestSha256),
            SessionEventType.ToolInvocationAttemptStarted,
            new SessionEventPayload(Turn: turn, ToolInvocation: snapshot),
            hash,
            cancellationToken);
        _toolInvocations[intent.ToolInvocationId] =
            existing with { Snapshot = snapshot, Sequence = sequence };
        return sequence;
    }

    private async ValueTask<long> RecordToolInvocationTerminalAsync(
        ThreadSnapshot thread,
        TurnSnapshot turn,
        RecordToolInvocationTerminalIntent intent,
        CancellationToken cancellationToken)
    {
        RequireId(intent.ResultItemId, nameof(intent.ResultItemId), "Result Item ID");
        ArgumentNullException.ThrowIfNull(intent.Result);
        var result = intent.Result;
        if (!_toolInvocations.TryGetValue(result.ToolInvocationId, out var existing))
        {
            throw ExecutionError(
                SessionErrorCodes.InvalidState,
                "Tool Invocation terminal has no start.");
        }

        if (existing.Snapshot.CompletedAt is not null)
        {
            if (existing.Snapshot.ResultItemId == intent.ResultItemId &&
                existing.Snapshot.Status == result.Status &&
                existing.Snapshot.AttemptCount == result.AttemptCount &&
                string.Equals(
                    existing.Snapshot.ErrorCode,
                    result.Error?.Code,
                    StringComparison.Ordinal) &&
                _items.TryGetValue(intent.ResultItemId, out var replayedItem) &&
                replayedItem.Content is ToolResultItemContent replayedResult &&
                string.Equals(
                    replayedResult.Result.ResultSha256,
                    result.ResultSha256,
                    StringComparison.Ordinal))
            {
                return existing.Sequence;
            }

            throw ExecutionError(
                SessionErrorCodes.InvalidState,
                "Tool Invocation already has a different terminal.");
        }

        if (turn.Status != TurnStatus.Running ||
            _items.ContainsKey(intent.ResultItemId) ||
            existing.Snapshot.TurnId != turn.TurnId ||
            !IsValidToolResultContent(result) ||
            !IsTerminalToolStatus(result.Status) ||
            result.AttemptCount != existing.Snapshot.AttemptCount ||
            !string.Equals(
                result.ProviderToolCallId,
                existing.Snapshot.ProviderToolCallId,
                StringComparison.Ordinal) ||
            !IsLowerSha256(result.ResultSha256) ||
            result.Status == ToolInvocationStatus.Completed && result.Error is not null)
        {
            throw ExecutionError(
                SessionErrorCodes.InvalidState,
                "Tool Invocation terminal result is invalid.");
        }

        var timestamp = _timeProvider.GetUtcNow();
        var contentValue = new ToolResultItemContent(result);
        var content = SerializeContent(contentValue);
        var bytes = ThreadJournal.Canonicalize(content);
        var item = new SessionItemSnapshot(
            intent.ResultItemId,
            turn.TurnId,
            SessionItemType.ToolResult,
            SessionItemStatus.Completed,
            contentValue,
            thread.CurrentSequence + 1,
            timestamp,
            timestamp);
        var snapshot = existing.Snapshot with
        {
            Status = result.Status,
            ResultItemId = intent.ResultItemId,
            ErrorCode = result.Error?.Code,
            UpdatedAt = timestamp,
            CompletedAt = timestamp,
        };
        var terminal = new ToolInvocationTerminalFact(
            result.ToolInvocationId,
            result.Status,
            result.Error?.Code,
            result.ResultSha256,
            intent.ResultItemId);
        var resultItem = new ToolResultItemFact(
            intent.ResultItemId,
            turn.TurnId,
            content,
            bytes.Length,
            Hash(bytes));
        var hash = InternalRequestHash(
            SessionEventType.ToolInvocationTerminal,
            new { turn.TurnId, Invocation = terminal, ResultItem = resultItem });
        var sequence = await CommitExecutionFactAsync(
            thread,
            new ToolInvocationTerminalJournalFact(
                terminal,
                resultItem,
                hash.RequestSha256),
            SessionEventType.ToolInvocationTerminal,
            new SessionEventPayload(
                Turn: turn,
                Item: item,
                ToolInvocation: snapshot,
                ToolResult: result),
            hash,
            cancellationToken);
        _items[intent.ResultItemId] = item;
        _itemText[intent.ResultItemId] = string.Empty;
        _toolInvocations[result.ToolInvocationId] =
            existing with { Snapshot = snapshot, Sequence = sequence };
        return sequence;
    }

    private async ValueTask<long> RecordDeferredToolsActivatedAsync(
        ThreadSnapshot thread,
        TurnSnapshot turn,
        RecordDeferredToolsActivatedIntent intent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent.ToolDefinitionIds);
        var requested = intent.ToolDefinitionIds.Distinct().ToArray();
        if (turn.Status != TurnStatus.Running ||
            requested.Length is 0 or > 8 ||
            requested.Length != intent.ToolDefinitionIds.Count ||
            !_agentInvocations.TryGetValue(turn.TurnId, out var invocation))
        {
            throw ExecutionError(
                SessionErrorCodes.InvalidState,
                "Deferred Tool activation is invalid.");
        }

        var existing = _deferredToolActivations.Keys
            .Where(key => key.TurnId == turn.TurnId)
            .Select(key => key.ToolDefinitionId)
            .ToHashSet();
        if (invocation.Snapshot.Tools is not { } frozenTools ||
            existing.Count + requested.Length > 32 ||
            requested.Any(existing.Contains) ||
            requested.Any(id =>
                !frozenTools.Registrations.Any(registration =>
                    registration.Exposure == ToolExposure.Deferred &&
                    registration.Definition.Id == id)))
        {
            throw ExecutionError(
                SessionErrorCodes.InvalidState,
                "Deferred Tool activation does not match the frozen snapshot.");
        }

        var hash = InternalRequestHash(
            SessionEventType.DeferredToolsActivated,
            new { turn.TurnId, ToolDefinitionIds = requested });
        var fact = new DeferredToolsActivatedFact(
            turn.TurnId,
            Array.AsReadOnly(requested),
            hash.RequestSha256);
        var sequence = await CommitExecutionFactAsync(
            thread,
            fact,
            SessionEventType.DeferredToolsActivated,
            new SessionEventPayload(Turn: turn),
            hash,
            cancellationToken);
        foreach (var id in requested)
        {
            _deferredToolActivations[
                new DeferredActivationKey(turn.TurnId, id)] = sequence;
        }

        return sequence;
    }

    private async ValueTask<long> StartItemAsync(
        ThreadSnapshot thread,
        TurnSnapshot turn,
        StartItemIntent intent,
        CancellationToken cancellationToken)
    {
        RequireId(intent.ItemId, nameof(intent.ItemId), "Item ID");
        if (turn.Status != TurnStatus.Running ||
            _items.ContainsKey(intent.ItemId) ||
            !ContentMatches(intent.Type, intent.Content))
        {
            throw ExecutionError(
                SessionErrorCodes.InvalidState,
                "Executor emitted an invalid item start.");
        }

        var timestamp = _timeProvider.GetUtcNow();
        var content = SerializeContent(intent.Content);
        var text = ContentText(intent.Content);
        var hash = InternalRequestHash(
            SessionEventType.ItemStarted,
            new { intent.ItemId, turn.TurnId, intent.Type, Content = content });
        var item = new SessionItemSnapshot(
            intent.ItemId,
            turn.TurnId,
            intent.Type,
            SessionItemStatus.Started,
            intent.Content,
            thread.CurrentSequence + 1,
            timestamp,
            timestamp);
        await CommitExecutionFactAsync(
            thread,
            new ItemStartedFact(
                intent.ItemId,
                turn.TurnId,
                intent.Type,
                content,
                text,
                hash.RequestSha256),
            SessionEventType.ItemStarted,
            new SessionEventPayload(Turn: turn, Item: item),
            hash,
            cancellationToken);
        _items[intent.ItemId] = item;
        _itemText[intent.ItemId] = text ?? string.Empty;
        return item.Sequence;
    }

    private async ValueTask<long> AppendDeltaAsync(
        ThreadSnapshot thread,
        TurnSnapshot turn,
        AppendItemDeltaIntent intent,
        CancellationToken cancellationToken)
    {
        if (turn.Status != TurnStatus.Running ||
            !_items.TryGetValue(intent.ItemId, out var item) ||
            item.TurnId != turn.TurnId ||
            item.Type is not (SessionItemType.AgentMessage or SessionItemType.Reasoning) ||
            item.Status is not (SessionItemStatus.Started or SessionItemStatus.Streaming))
        {
            throw ExecutionError(
                SessionErrorCodes.InvalidState,
                "Executor emitted a delta for an inactive item.");
        }

        var timestamp = _timeProvider.GetUtcNow();
        var text = _itemText.GetValueOrDefault(intent.ItemId) + intent.Delta;
        var streamed = item with
        {
            Status = SessionItemStatus.Streaming,
            Content = new TextItemContent(text),
            UpdatedAt = timestamp,
        };
        var hash = InternalRequestHash(
            SessionEventType.ItemDeltaAppended,
            new { intent.ItemId, intent.Delta });
        var sequence = await CommitExecutionFactAsync(
            thread,
            new ItemDeltaFact(intent.ItemId, intent.Delta, hash.RequestSha256),
            SessionEventType.ItemDeltaAppended,
            new SessionEventPayload(Turn: turn, Item: streamed),
            hash,
            cancellationToken);
        _items[intent.ItemId] = streamed;
        _itemText[intent.ItemId] = text;
        return sequence;
    }

    private async ValueTask<long> CompleteItemAsync(
        ThreadSnapshot thread,
        TurnSnapshot turn,
        CompleteItemIntent intent,
        CancellationToken cancellationToken)
    {
        if (turn.Status != TurnStatus.Running ||
            !_items.TryGetValue(intent.ItemId, out var item) ||
            item.TurnId != turn.TurnId ||
            item.Status is not (SessionItemStatus.Started or SessionItemStatus.Streaming))
        {
            throw ExecutionError(
                SessionErrorCodes.InvalidState,
                "Executor completed an inactive item.");
        }

        var timestamp = _timeProvider.GetUtcNow();
        var bytes = Encoding.UTF8.GetBytes(
            _itemText.GetValueOrDefault(intent.ItemId) ?? string.Empty);
        var completed = item with
        {
            Status = SessionItemStatus.Completed,
            UpdatedAt = timestamp,
        };
        var hash = InternalRequestHash(
            SessionEventType.ItemCompleted,
            new { intent.ItemId, Length = bytes.Length, Sha256 = Hash(bytes) });
        var sequence = await CommitExecutionFactAsync(
            thread,
            new ItemCompletedFact(
                intent.ItemId,
                bytes.Length,
                Hash(bytes),
                hash.RequestSha256),
            SessionEventType.ItemCompleted,
            new SessionEventPayload(Turn: turn, Item: completed),
            hash,
            cancellationToken);
        _items[intent.ItemId] = completed;
        return sequence;
    }

    private async ValueTask<long> FailItemAsync(
        ThreadSnapshot thread,
        TurnSnapshot turn,
        FailItemIntent intent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent.Error);
        if (turn.Status != TurnStatus.Running ||
            !_items.TryGetValue(intent.ItemId, out var item) ||
            item.TurnId != turn.TurnId ||
            item.Status is not (SessionItemStatus.Started or SessionItemStatus.Streaming))
        {
            throw ExecutionError(
                SessionErrorCodes.InvalidState,
                "Executor failed an inactive item.");
        }

        var failed = item with
        {
            Status = SessionItemStatus.Failed,
            UpdatedAt = _timeProvider.GetUtcNow(),
        };
        var hash = InternalRequestHash(
            SessionEventType.ItemFailed,
            new { intent.ItemId, intent.Error });
        var sequence = await CommitExecutionFactAsync(
            thread,
            new ItemTerminalFact(intent.ItemId, intent.Error, hash.RequestSha256),
            SessionEventType.ItemFailed,
            new SessionEventPayload(Turn: turn, Item: failed, Error: intent.Error),
            hash,
            cancellationToken);
        _items[intent.ItemId] = failed;
        return sequence;
    }

    private async ValueTask<long> WaitForInteractionAsync(
        ThreadSnapshot thread,
        TurnSnapshot turn,
        WaitForInteractionIntent intent,
        CancellationToken cancellationToken)
    {
        RequireId(intent.InteractionId, nameof(intent.InteractionId), "Interaction ID");
        if (turn.Status != TurnStatus.Running ||
            _interactions.ContainsKey(intent.InteractionId) ||
            HasActiveItems(turn.TurnId) ||
            intent.ToolInvocationId is { } toolInvocationId &&
            (intent.Type != SessionInteractionType.Approval ||
             !_toolInvocations.TryGetValue(toolInvocationId, out var toolInvocation) ||
             toolInvocation.Snapshot.TurnId != turn.TurnId ||
             toolInvocation.Snapshot.CompletedAt is not null) ||
            string.IsNullOrWhiteSpace(_executorKind) ||
            !SessionExecutionCheckpointCodec.IsValid(
                intent.Checkpoint,
                _executorKind))
        {
            throw ExecutionError(
                SessionErrorCodes.RuntimeContinuationMissing,
                "Execution checkpoint is missing, unsupported, or corrupt.");
        }

        var requestType = RequestItemType(intent.Type, intent.Request);
        var timestamp = _timeProvider.GetUtcNow();
        var waitingToolInvocation = intent.ToolInvocationId is { } invocationId
            ? _toolInvocations[invocationId]
            : null;
        var waitingToolSnapshot = waitingToolInvocation is null
            ? null
            : waitingToolInvocation.Snapshot with
            {
                Status = ToolInvocationStatus.WaitingApproval,
                UpdatedAt = timestamp,
            };
        var itemId = Guid.CreateVersion7();
        var request = SerializeContent(intent.Request);
        var contentText = ContentText(intent.Request);
        var contentBytes = Encoding.UTF8.GetBytes(contentText ?? string.Empty);
        var waitingStatus = WaitingStatus(intent.Type);
        var waitingTurn = turn with
        {
            Status = waitingStatus,
            UpdatedAt = timestamp,
        };
        var item = new SessionItemSnapshot(
            itemId,
            turn.TurnId,
            requestType,
            SessionItemStatus.Completed,
            intent.Request,
            thread.CurrentSequence + 1,
            timestamp,
            timestamp);
        var interaction = new PendingInteractionSnapshot(
            intent.InteractionId,
            thread.ThreadId,
            turn.TurnId,
            intent.Type,
            IsResolved: false,
            timestamp,
            intent.TimeoutAt?.ToUniversalTime(),
            intent.ToolInvocationId);
        var hash = InternalRequestHash(
            intent.Type == SessionInteractionType.Approval
                ? SessionEventType.TurnWaitingApproval
                : SessionEventType.TurnWaitingInput,
            new
            {
                intent.InteractionId,
                turn.TurnId,
                Request = request,
                intent.Checkpoint,
                intent.TimeoutAt,
                intent.ToolInvocationId,
            });
        var eventType = intent.Type == SessionInteractionType.Approval
            ? SessionEventType.TurnWaitingApproval
            : SessionEventType.TurnWaitingInput;
        var sequence = await CommitExecutionFactAsync(
            thread,
            new TurnWaitingFact(
                turn.TurnId,
                intent.InteractionId,
                itemId,
                intent.Type,
                requestType,
                request,
                contentText,
                contentBytes.Length,
                Hash(contentBytes),
                intent.Checkpoint,
                intent.TimeoutAt?.ToUniversalTime(),
                hash.RequestSha256,
                intent.ToolInvocationId),
            eventType,
            new SessionEventPayload(
                Turn: waitingTurn,
                Item: item,
                Interaction: interaction,
                ToolInvocation: waitingToolSnapshot),
            hash,
            cancellationToken);
        _turns[turn.TurnId] = waitingTurn;
        _items[itemId] = item;
        _itemText[itemId] = contentText ?? string.Empty;
        _interactions[intent.InteractionId] = new InteractionRuntime(
            interaction,
            itemId,
            request,
            intent.Checkpoint,
            Resolution: null);
        if (waitingToolInvocation is not null)
        {
            _toolInvocations[waitingToolInvocation.Snapshot.ToolInvocationId] =
                waitingToolInvocation with
                {
                    Snapshot = waitingToolSnapshot!,
                    Sequence = sequence,
                };
        }

        return sequence;
    }

    private async ValueTask<long> CompleteTurnAsync(
        ThreadSnapshot thread,
        TurnSnapshot turn,
        CancellationToken cancellationToken)
    {
        if (turn.Status != TurnStatus.Running)
        {
            throw ExecutionError(
                SessionErrorCodes.InvalidState,
                "Executor completed a turn that is not running.");
        }

        return await CommitTerminalTurnAsync(
            thread,
            turn,
            SessionEventType.TurnCompleted,
            error: null,
            cancellationToken);
    }

    private ValueTask<long> FailTurnFromIntentAsync(
        ThreadSnapshot thread,
        TurnSnapshot turn,
        FailTurnIntent intent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent.Error);
        return CommitTerminalTurnAsync(
            thread,
            turn,
            SessionEventType.TurnFailed,
            intent.Error,
            cancellationToken);
    }

    private async ValueTask<long> CommitTerminalTurnAsync(
        ThreadSnapshot thread,
        TurnSnapshot turn,
        SessionEventType eventType,
        SessionError? error,
        CancellationToken cancellationToken)
    {
        if (turn.Status is not (
            TurnStatus.Running or
            TurnStatus.WaitingApproval or
            TurnStatus.WaitingInput))
        {
            throw ExecutionError(
                SessionErrorCodes.InvalidState,
                "Turn is already terminal.");
        }

        if (eventType == SessionEventType.TurnCompleted && HasActiveItems(turn.TurnId))
        {
            throw ExecutionError(
                SessionErrorCodes.InvalidState,
                "Turn cannot complete while an item is active.");
        }

        var status = eventType == SessionEventType.TurnCompleted
            ? TurnStatus.Completed
            : eventType == SessionEventType.TurnCancelled
                ? TurnStatus.Cancelled
                : TurnStatus.Failed;
        if (eventType != SessionEventType.TurnCompleted)
        {
            thread = await TerminalizeActiveItemsAsync(
                thread,
                turn,
                eventType == SessionEventType.TurnCancelled
                    ? SessionItemStatus.Cancelled
                    : SessionItemStatus.Failed,
                error,
                cancellationToken);
        }

        var timestamp = _timeProvider.GetUtcNow();
        var terminal = turn with
        {
            Status = status,
            UpdatedAt = timestamp,
            CompletedAt = timestamp,
            Error = error,
        };
        var hash = InternalRequestHash(
            eventType,
            new { turn.TurnId, Error = error });
        var sequence = await CommitExecutionFactAsync(
            thread,
            new TurnTerminalFact(turn.TurnId, error, hash.RequestSha256),
            eventType,
            new SessionEventPayload(Turn: terminal, Error: error),
            hash,
            cancellationToken,
            clearActiveTurn: true);
        _turns[turn.TurnId] = terminal;
        return sequence;
    }

    private async Task<ThreadSnapshot> TerminalizeActiveItemsAsync(
        ThreadSnapshot thread,
        TurnSnapshot turn,
        SessionItemStatus status,
        SessionError? error,
        CancellationToken cancellationToken)
    {
        var eventType = status == SessionItemStatus.Cancelled
            ? SessionEventType.ItemCancelled
            : SessionEventType.ItemFailed;
        foreach (var item in _items.Values
                     .Where(item =>
                         item.TurnId == turn.TurnId &&
                         item.Status is SessionItemStatus.Started
                             or SessionItemStatus.Streaming)
                     .OrderBy(item => item.Sequence)
                     .ThenBy(item => item.ItemId))
        {
            var terminal = item with
            {
                Status = status,
                UpdatedAt = _timeProvider.GetUtcNow(),
            };
            var operation = InternalRequestHash(
                eventType,
                new { item.ItemId, Error = error });
            await CommitExecutionFactAsync(
                thread,
                new ItemTerminalFact(item.ItemId, error, operation.RequestSha256),
                eventType,
                new SessionEventPayload(Turn: turn, Item: terminal, Error: error),
                operation,
                cancellationToken);
            _items[item.ItemId] = terminal;
            thread = _snapshots[thread.ThreadId];
        }

        return thread;
    }

    private bool HasActiveItems(Guid turnId) =>
        _items.Values.Any(item =>
            item.TurnId == turnId &&
            item.Status is SessionItemStatus.Started or SessionItemStatus.Streaming);

    private async ValueTask ResumeExecutionAsync(
        Guid threadId,
        Guid turnId,
        Guid interactionId,
        CancellationToken cancellationToken)
    {
        var threadGate = GetThreadGate(threadId);
        await threadGate.WaitAsync(cancellationToken);
        try
        {
            var thread = await GetSnapshotAsync(threadId, cancellationToken)
                ?? throw ExecutionError(
                    SessionErrorCodes.NotFound,
                    "Thread was not found.");
            if (!_turns.TryGetValue(turnId, out var turn) ||
                !_interactions.TryGetValue(interactionId, out var interaction) ||
                !interaction.Snapshot.IsResolved ||
                turn.Status is not (
                    TurnStatus.WaitingApproval or TurnStatus.WaitingInput))
            {
                throw ExecutionError(
                    SessionErrorCodes.InvalidState,
                    "Execution cannot resume from the current state.");
            }

            var timestamp = _timeProvider.GetUtcNow();
            var running = turn with
            {
                Status = TurnStatus.Running,
                UpdatedAt = timestamp,
            };
            var hash = InternalRequestHash(
                SessionEventType.TurnExecutionResumed,
                new { turnId, interactionId });
            await CommitExecutionFactAsync(
                thread,
                new TurnExecutionResumedFact(
                    turnId,
                    interactionId,
                    hash.RequestSha256),
                SessionEventType.TurnExecutionResumed,
                new SessionEventPayload(Turn: running, Interaction: interaction.Snapshot),
                hash,
                cancellationToken);
            _turns[turnId] = running;
        }
        finally
        {
            threadGate.Release();
        }
    }

    private async Task FailTurnAsync(
        Guid turnId,
        SessionError error,
        CancellationToken cancellationToken)
    {
        if (!_turns.TryGetValue(turnId, out var knownTurn) ||
            knownTurn.Status is TurnStatus.Completed or TurnStatus.Failed or TurnStatus.Cancelled)
        {
            return;
        }

        var threadGate = GetThreadGate(knownTurn.ThreadId);
        await threadGate.WaitAsync(cancellationToken);
        var committed = false;
        try
        {
            var thread = await GetSnapshotAsync(knownTurn.ThreadId, cancellationToken);
            if (thread is null ||
                !_turns.TryGetValue(turnId, out var turn) ||
                turn.Status is TurnStatus.Completed or TurnStatus.Failed or TurnStatus.Cancelled)
            {
                return;
            }

            await CommitTerminalTurnAsync(
                thread,
                turn,
                SessionEventType.TurnFailed,
                error,
                cancellationToken);
            committed = true;
        }
        finally
        {
            threadGate.Release();
        }

        if (committed)
        {
            await TryScheduleNextAsync(knownTurn.ThreadId, CancellationToken.None);
        }
    }

    private async ValueTask<long> CommitExecutionFactAsync(
        ThreadSnapshot thread,
        object fact,
        SessionEventType eventType,
        SessionEventPayload payload,
        InternalOperation operation,
        CancellationToken cancellationToken,
        bool clearActiveTurn = false)
    {
        var timestamp = _timeProvider.GetUtcNow();
        var nextThread = ExecutionThread(
            thread,
            thread.CurrentSequence + 1,
            timestamp,
            clearActiveTurn ? null : thread.ActiveTurnId);
        var committed = await CommitAsync(
            operation.IdempotencyKey,
            Wire(eventType),
            operation.RequestSha256,
            nextThread,
            fact,
            eventType,
            cancellationToken,
            payload);
        if (committed.Status == SessionCommandStatus.Rejected)
        {
            throw new SessionExecutionException(
                committed.Error ?? new SessionError(
                    SessionErrorCodes.InvalidState,
                    "Session fact was rejected.",
                    IsRetryable: false));
        }

        return committed.Sequence!.Value;
    }

    private async Task EnsureExecutionStateLoadedAsync(
        Guid threadId,
        CancellationToken cancellationToken)
    {
        if (_loadedExecutionThreads.ContainsKey(threadId))
        {
            return;
        }

        var thread = await GetSnapshotAsync(threadId, cancellationToken);
        if (thread is null)
        {
            return;
        }

        var replay = await _journal.ReplayAsync(
            thread.Status == ThreadStatus.Archived
                ? ThreadJournalLocation.Archived
                : ThreadJournalLocation.Active,
            threadId,
            cancellationToken);
        RestoreExecutionState(thread, replay.Entries);
        _loadedExecutionThreads.TryAdd(threadId, 0);
    }

    private void RestoreExecutionState(
        ThreadSnapshot thread,
        IReadOnlyList<ThreadJournalEntry> entries)
    {
        foreach (var entry in entries)
        {
            switch (entry.EntryType)
            {
                case SessionEventType.ThreadForked:
                    RestoreHistoryCheckpoint(
                        entry.ThreadId,
                        ReadFact<ThreadForkedFact>(entry).History);
                    break;
                case SessionEventType.ThreadRolledBack:
                    RestoreHistoryCheckpoint(
                        entry.ThreadId,
                        ReadFact<ThreadRolledBackFact>(entry).History);
                    break;
                case SessionEventType.TurnStarted:
                    var started = ReadFact<TurnStartedFact>(entry);
                    _turns[started.TurnId] = new TurnSnapshot(
                        started.TurnId,
                        entry.ThreadId,
                        TurnStatus.Running,
                        entry.Timestamp,
                        entry.Timestamp,
                        CompletedAt: null,
                        Error: null,
                        started.EffectiveAgentMode);
                    if (started.UserItemId is { } userItemId &&
                        started.Text is { } userText)
                    {
                        _items[userItemId] = new SessionItemSnapshot(
                            userItemId,
                            started.TurnId,
                            SessionItemType.UserMessage,
                            SessionItemStatus.Completed,
                            new TextItemContent(userText),
                            entry.Sequence,
                            entry.Timestamp,
                            entry.Timestamp);
                        _itemText[userItemId] = userText;
                    }

                    break;
                case SessionEventType.TurnSteered:
                    var steered = ReadFact<TurnSteeredFact>(entry);
                    _items[steered.UserItemId] = new SessionItemSnapshot(
                        steered.UserItemId,
                        steered.TurnId,
                        SessionItemType.UserMessage,
                        SessionItemStatus.Completed,
                        new TextItemContent(steered.Text),
                        entry.Sequence,
                        entry.Timestamp,
                        entry.Timestamp);
                    _itemText[steered.UserItemId] = steered.Text;
                    break;
                case SessionEventType.ItemStarted:
                    RestoreStartedItem(entry);
                    break;
                case SessionEventType.ItemDeltaAppended:
                    RestoreItemDelta(entry);
                    break;
                case SessionEventType.ItemCompleted:
                    RestoreItemTerminal(entry, SessionItemStatus.Completed);
                    break;
                case SessionEventType.ItemFailed:
                    RestoreItemTerminal(entry, SessionItemStatus.Failed);
                    break;
                case SessionEventType.ItemCancelled:
                    RestoreItemTerminal(entry, SessionItemStatus.Cancelled);
                    break;
                case SessionEventType.ToolCallRecorded:
                    RestoreToolCall(entry);
                    break;
                case SessionEventType.ToolInvocationStarted:
                    RestoreToolInvocationStarted(entry);
                    break;
                case SessionEventType.ToolInvocationAttemptStarted:
                    RestoreToolInvocationAttemptStarted(entry);
                    break;
                case SessionEventType.ToolInvocationTerminal:
                    RestoreToolInvocationTerminal(entry);
                    break;
                case SessionEventType.DeferredToolsActivated:
                    RestoreDeferredToolsActivated(entry);
                    break;
                case SessionEventType.AgentInvocationSnapshotRecorded:
                    var invocation = ReadFact<AgentInvocationSnapshotRecordedFact>(entry);
                    _agentInvocations[invocation.TurnId] =
                        new InvocationRecord(invocation.Snapshot, entry.Sequence);
                    break;
                case SessionEventType.ProviderUsageRecorded:
                    var usage = ReadFact<ProviderUsageRecordedFact>(entry);
                    var usageKey = new ProviderUsageKey(
                        usage.Usage.InvocationId,
                        usage.Usage.AttemptNumber,
                        usage.Usage.Purpose);
                    if (_providerUsage.TryGetValue(usageKey, out var existingUsage) &&
                        existingUsage.Usage != usage.Usage)
                    {
                        throw new InvalidDataException(
                            "Journal contains conflicting provider usage.");
                    }

                    _providerUsage[usageKey] =
                        new UsageRecord(usage.Usage, entry.Sequence);
                    break;
                case SessionEventType.CompactionCheckpointRecorded:
                    var compaction = ReadFact<CompactionCheckpointRecordedFact>(entry);
                    if (!_agentInvocations.TryGetValue(
                            compaction.TurnId,
                            out var compactionInvocation) ||
                        compaction.Checkpoint.SchemaVersion is not (1 or 2) ||
                        string.IsNullOrWhiteSpace(compaction.Checkpoint.Summary) ||
                        compaction.Checkpoint.SourceStartSequence <= 0 ||
                        compaction.Checkpoint.SourceEndSequence <
                        compaction.Checkpoint.SourceStartSequence ||
                        !CompactionCheckpointIntegrity.IsLowerSha256(
                            compaction.Checkpoint.SourceMessagesSha256) ||
                        compaction.Checkpoint.SummaryTokenCount <= 0 ||
                        !CompactionCheckpointIntegrity.IsValidSummary(
                            compaction.Checkpoint.Summary) ||
                        !string.Equals(
                            compaction.Checkpoint.SummaryPromptVersion,
                            compactionInvocation.Snapshot.CompactionPrompt.Version,
                            StringComparison.Ordinal) ||
                        !string.Equals(
                            compaction.Checkpoint.TokenizerProfileId,
                            compactionInvocation.Snapshot.TokenizerProfileId,
                            StringComparison.Ordinal) ||
                        !string.Equals(
                            compaction.Checkpoint.TokenizerProfileVersion,
                            compactionInvocation.Snapshot.TokenizerProfileVersion,
                            StringComparison.Ordinal) ||
                        !string.Equals(
                            compaction.Checkpoint.SummarySha256,
                            CompactionCheckpointIntegrity.Sha256(
                                compaction.Checkpoint.Summary),
                            StringComparison.Ordinal) ||
                        !CompactionSourceMatches(
                            compaction.Checkpoint,
                            _items.Values))
                    {
                        throw new InvalidDataException(
                            "Journal contains an invalid compaction checkpoint.");
                    }

                    if (_compactionCheckpoints.TryGetValue(
                            entry.ThreadId,
                            out var existingCompaction) &&
                        (compaction.Checkpoint.SourceStartSequence !=
                         existingCompaction.Checkpoint.SourceStartSequence ||
                         compaction.Checkpoint.SourceEndSequence <=
                         existingCompaction.Checkpoint.SourceEndSequence))
                    {
                        throw new InvalidDataException(
                            "Journal contains a non-extending compaction checkpoint.");
                    }

                    _compactionCheckpoints[entry.ThreadId] =
                        new CompactionRecord(compaction.Checkpoint, entry.Sequence);
                    break;
                case SessionEventType.TurnWaitingApproval:
                case SessionEventType.TurnWaitingInput:
                    RestoreWaiting(entry);
                    break;
                case SessionEventType.InteractionResolved:
                    RestoreResolution(entry);
                    break;
                case SessionEventType.TurnExecutionResumed:
                    var resumed = ReadFact<TurnExecutionResumedFact>(entry);
                    UpdateTurn(resumed.TurnId, TurnStatus.Running, entry.Timestamp, null);
                    break;
                case SessionEventType.TurnCompleted:
                case SessionEventType.TurnFailed:
                case SessionEventType.TurnCancelled:
                    RestoreTerminalTurn(entry);
                    break;
            }
        }

        _snapshots[thread.ThreadId] = thread;
    }

    private static SessionEvent[] BuildHistoryEvents(
        IReadOnlyList<ThreadJournalEntry> entries)
    {
        var events = new List<SessionEvent>(entries.Count);
        var turns = new Dictionary<Guid, TurnSnapshot>();
        var items = new Dictionary<Guid, SessionItemSnapshot>();
        var texts = new Dictionary<Guid, string>();
        var interactions = new Dictionary<Guid, PendingInteractionSnapshot>();
        var queueItems = new Dictionary<Guid, QueuedTurnInputSnapshot>();
        var toolInvocations = new Dictionary<Guid, ToolInvocationRecord>();
        ThreadSnapshot? thread = null;
        foreach (var entry in entries)
        {
            thread = ApplyHistoryFact(thread, entry);
            TurnSnapshot? turn = null;
            SessionItemSnapshot? item = null;
            QueuedTurnInputSnapshot? queueItem = null;
            PendingInteractionSnapshot? interaction = null;
            SessionError? error = null;
            AgentInvocationSnapshot? invocation = null;
            ProviderUsageSnapshot? usage = null;
            CompactionCheckpointSnapshot? compaction = null;
            ToolInvocationSnapshot? toolInvocation = null;
            switch (entry.EntryType)
            {
                case SessionEventType.ThreadForked:
                    ReplaceHistory(
                        entry.ThreadId,
                        ReadFact<ThreadForkedFact>(entry).History,
                        turns,
                        items,
                        texts,
                        interactions,
                        toolInvocations);
                    break;
                case SessionEventType.ThreadRolledBack:
                    ReplaceHistory(
                        entry.ThreadId,
                        ReadFact<ThreadRolledBackFact>(entry).History,
                        turns,
                        items,
                        texts,
                        interactions,
                        toolInvocations);
                    break;
                case SessionEventType.TurnQueued:
                    var queued = ReadFact<TurnQueuedFact>(entry);
                    var queuedItem = new QueuedTurnInputSnapshot(
                        queued.QueueItemId,
                        entry.ThreadId,
                        queued.Text,
                        queued.Position,
                        entry.Timestamp,
                        queued.EffectiveAgentMode);
                    queueItems[queued.QueueItemId] = queuedItem;
                    events.Add(new SessionEvent(
                        entry.ThreadId,
                        entry.Sequence,
                        entry.EntryId,
                        entry.Timestamp,
                        entry.EntryType,
                        new SessionEventPayload(
                            Thread: thread,
                            QueueItem: queuedItem)));
                    continue;
                case SessionEventType.TurnQueueChanged:
                    var queueChanged = ReadFact<TurnQueueChangedFact>(entry);
                    var removedQueueItem = queueChanged.RemovedQueueItemId is { } removedId
                        ? queueItems.GetValueOrDefault(removedId)
                        : null;
                    if (removedQueueItem is not null)
                    {
                        queueItems.Remove(removedQueueItem.QueueItemId);
                    }

                    queueItem = removedQueueItem;
                    break;
                case SessionEventType.TurnStarted:
                    var started = ReadFact<TurnStartedFact>(entry);
                    turn = new TurnSnapshot(
                        started.TurnId,
                        entry.ThreadId,
                        TurnStatus.Running,
                        entry.Timestamp,
                        entry.Timestamp,
                        CompletedAt: null,
                        Error: null,
                        started.EffectiveAgentMode);
                    turns[started.TurnId] = turn;
                    if (started.QueueItemId is { } scheduledQueueItemId &&
                        started.UserItemId is { } userItemId &&
                        started.Text is { } userText)
                    {
                        var scheduledQueueItem =
                            queueItems.GetValueOrDefault(scheduledQueueItemId);
                        queueItems.Remove(scheduledQueueItemId);
                        item = new SessionItemSnapshot(
                            userItemId,
                            started.TurnId,
                            SessionItemType.UserMessage,
                            SessionItemStatus.Completed,
                            new TextItemContent(userText),
                            entry.Sequence,
                            entry.Timestamp,
                            entry.Timestamp);
                        items[item.ItemId] = item;
                        texts[item.ItemId] = userText;
                        events.Add(new SessionEvent(
                            entry.ThreadId,
                            entry.Sequence,
                            entry.EntryId,
                            entry.Timestamp,
                            entry.EntryType,
                            new SessionEventPayload(
                                Thread: thread,
                                Turn: turn,
                                Item: item,
                                QueueItem: scheduledQueueItem)));
                        continue;
                    }

                    break;
                case SessionEventType.TurnSteered:
                    var steered = ReadFact<TurnSteeredFact>(entry);
                    var steeredQueueItem =
                        queueItems.GetValueOrDefault(steered.QueueItemId);
                    queueItems.Remove(steered.QueueItemId);
                    turn = turns.GetValueOrDefault(steered.TurnId);
                    item = new SessionItemSnapshot(
                        steered.UserItemId,
                        steered.TurnId,
                        SessionItemType.UserMessage,
                        SessionItemStatus.Completed,
                        new TextItemContent(steered.Text),
                        entry.Sequence,
                        entry.Timestamp,
                        entry.Timestamp);
                    items[item.ItemId] = item;
                    texts[item.ItemId] = steered.Text;
                    events.Add(new SessionEvent(
                        entry.ThreadId,
                        entry.Sequence,
                        entry.EntryId,
                        entry.Timestamp,
                        entry.EntryType,
                        new SessionEventPayload(
                            Thread: thread,
                            Turn: turn,
                            Item: item,
                            QueueItem: steeredQueueItem)));
                    continue;
                case SessionEventType.ItemStarted:
                    var itemStarted = ReadFact<ItemStartedFact>(entry);
                    item = new SessionItemSnapshot(
                        itemStarted.ItemId,
                        itemStarted.TurnId,
                        itemStarted.ItemType,
                        SessionItemStatus.Started,
                        DeserializeContent(itemStarted.ItemType, itemStarted.Content),
                        entry.Sequence,
                        entry.Timestamp,
                        entry.Timestamp);
                    items[item.ItemId] = item;
                    texts[item.ItemId] = itemStarted.ContentText ?? string.Empty;
                    turn = turns.GetValueOrDefault(item.TurnId);
                    break;
                case SessionEventType.ItemDeltaAppended:
                    var delta = ReadFact<ItemDeltaFact>(entry);
                    item = items[delta.ItemId];
                    var text = texts.GetValueOrDefault(delta.ItemId) + delta.Delta;
                    texts[delta.ItemId] = text;
                    item = item with
                    {
                        Status = SessionItemStatus.Streaming,
                        Content = new TextItemContent(text),
                        UpdatedAt = entry.Timestamp,
                    };
                    items[item.ItemId] = item;
                    turn = turns.GetValueOrDefault(item.TurnId);
                    break;
                case SessionEventType.ItemCompleted:
                    var completed = ReadFact<ItemCompletedFact>(entry);
                    item = UpdateHistoryItem(
                        items,
                        completed.ItemId,
                        SessionItemStatus.Completed,
                        entry.Timestamp);
                    turn = turns.GetValueOrDefault(item.TurnId);
                    break;
                case SessionEventType.ItemFailed:
                case SessionEventType.ItemCancelled:
                    var itemTerminal = ReadFact<ItemTerminalFact>(entry);
                    item = UpdateHistoryItem(
                        items,
                        itemTerminal.ItemId,
                        entry.EntryType == SessionEventType.ItemFailed
                            ? SessionItemStatus.Failed
                            : SessionItemStatus.Cancelled,
                        entry.Timestamp);
                    turn = turns.GetValueOrDefault(item.TurnId);
                    error = itemTerminal.Error;
                    break;
                case SessionEventType.ToolCallRecorded:
                    var toolCall = ReadFact<ToolCallRecordedFact>(entry);
                    if (!HasValidToolItemDigest(
                            toolCall.Content,
                            toolCall.ContentLength,
                            toolCall.ContentSha256))
                    {
                        throw new InvalidDataException(
                            "Journal Tool Call Item digest is invalid.");
                    }

                    if (!turns.TryGetValue(toolCall.TurnId, out var toolCallTurn) ||
                        toolCallTurn.Status != TurnStatus.Running)
                    {
                        throw new InvalidDataException(
                            "Journal Tool Call Item has no running turn.");
                    }

                    item = new SessionItemSnapshot(
                        toolCall.ItemId,
                        toolCall.TurnId,
                        SessionItemType.ToolCall,
                        SessionItemStatus.Completed,
                        DeserializeContent(SessionItemType.ToolCall, toolCall.Content),
                        entry.Sequence,
                        entry.Timestamp,
                        entry.Timestamp);
                    if (item.Content is not ToolCallItemContent toolCallFrame ||
                        !IsValidToolCallContent(toolCallFrame) ||
                        !HasValidAgentMessageReference(
                            items,
                            item.TurnId,
                            toolCallFrame) ||
                        !items.TryAdd(item.ItemId, item))
                    {
                        throw new InvalidDataException(
                            "Journal contains a duplicate Tool Call Item.");
                    }

                    texts[item.ItemId] = string.Empty;
                    turn = turns.GetValueOrDefault(item.TurnId);
                    break;
                case SessionEventType.ToolInvocationStarted:
                    var toolStarted = ReadFact<ToolInvocationStartedFact>(entry);
                    if (toolInvocations.ContainsKey(toolStarted.ToolInvocationId) ||
                        !items.TryGetValue(toolStarted.ToolCallItemId, out var toolCallItem) ||
                        toolCallItem.Type != SessionItemType.ToolCall ||
                        toolCallItem.Status != SessionItemStatus.Completed ||
                        toolCallItem.Content is not ToolCallItemContent toolCallContent ||
                        toolCallItem.TurnId != toolStarted.TurnId ||
                        toolStarted.CallIndex < 0 ||
                        toolStarted.CallIndex >= toolCallContent.Calls.Count ||
                        !CallMatches(
                            toolCallContent.Calls[toolStarted.CallIndex],
                            toolStarted) ||
                        toolInvocations.Values.Any(invocation =>
                            invocation.ToolCallItemId ==
                            toolStarted.ToolCallItemId &&
                            invocation.CallIndex == toolStarted.CallIndex) ||
                        (toolStarted.ToolDefinitionId is null) !=
                        (toolStarted.RuntimeBindingId is null) ||
                        !IsLowerSha256(toolStarted.SnapshotSha256) ||
                        !IsLowerSha256(toolStarted.ArgumentsSha256))
                    {
                        throw new InvalidDataException(
                            "Journal contains an invalid Tool Invocation start.");
                    }

                    toolInvocation = new ToolInvocationSnapshot(
                        toolStarted.ToolInvocationId,
                        entry.ThreadId,
                        toolStarted.TurnId,
                        toolStarted.ProviderToolCallId,
                        toolStarted.ProviderToolName,
                        toolStarted.ToolDefinitionId,
                        toolStarted.RuntimeBindingId,
                        toolStarted.SnapshotSha256,
                        toolStarted.ArgumentsSha256,
                        ToolInvocationStatus.Started,
                        AttemptCount: 0,
                        ResultItemId: null,
                        ErrorCode: null,
                        entry.Timestamp,
                        entry.Timestamp,
                        CompletedAt: null);
                    toolInvocations[toolStarted.ToolInvocationId] =
                        new ToolInvocationRecord(
                            toolInvocation,
                            toolStarted.ToolCallItemId,
                            toolStarted.CallIndex,
                            entry.Sequence);
                    turn = turns.GetValueOrDefault(toolStarted.TurnId);
                    break;
                case SessionEventType.ToolInvocationAttemptStarted:
                    var toolAttempt =
                        ReadFact<ToolInvocationAttemptStartedFact>(entry);
                    if (!toolInvocations.TryGetValue(
                            toolAttempt.ToolInvocationId,
                            out var attemptRecord) ||
                        attemptRecord.Snapshot.CompletedAt is not null ||
                        toolAttempt.AttemptNumber !=
                        attemptRecord.Snapshot.AttemptCount + 1 ||
                        toolAttempt.AttemptNumber is < 1 or > 2)
                    {
                        throw new InvalidDataException(
                            "Journal contains an invalid Tool Invocation attempt.");
                    }

                    toolInvocation = attemptRecord.Snapshot with
                    {
                        Status = ToolInvocationStatus.Started,
                        AttemptCount = toolAttempt.AttemptNumber,
                        UpdatedAt = entry.Timestamp,
                    };
                    toolInvocations[toolAttempt.ToolInvocationId] =
                        attemptRecord with
                        {
                            Snapshot = toolInvocation,
                            Sequence = entry.Sequence,
                        };
                    turn = turns.GetValueOrDefault(toolInvocation.TurnId);
                    break;
                case SessionEventType.ToolInvocationTerminal:
                    var toolTerminal =
                        ReadFact<ToolInvocationTerminalJournalFact>(entry);
                    if (!toolInvocations.TryGetValue(
                            toolTerminal.Invocation.ToolInvocationId,
                            out var terminalRecord) ||
                        terminalRecord.Snapshot.CompletedAt is not null ||
                        toolTerminal.Invocation.ResultItemId !=
                        toolTerminal.ResultItem.ItemId)
                    {
                        throw new InvalidDataException(
                            "Journal contains an invalid Tool Invocation terminal.");
                    }

                    var resultContent = DeserializeContent(
                        SessionItemType.ToolResult,
                        toolTerminal.ResultItem.Content);
                    if (!HasValidToolItemDigest(
                            toolTerminal.ResultItem.Content,
                            toolTerminal.ResultItem.ContentLength,
                            toolTerminal.ResultItem.ContentSha256) ||
                        !IsTerminalToolStatus(toolTerminal.Invocation.Status) ||
                        resultContent is not ToolResultItemContent resultItemContent ||
                        toolTerminal.ResultItem.TurnId !=
                        terminalRecord.Snapshot.TurnId ||
                        resultItemContent.Result.ToolInvocationId !=
                        toolTerminal.Invocation.ToolInvocationId ||
                        resultItemContent.Result.Status !=
                        toolTerminal.Invocation.Status ||
                        resultItemContent.Result.AttemptCount !=
                        terminalRecord.Snapshot.AttemptCount ||
                        !IsValidToolResultContent(resultItemContent.Result) ||
                        !string.Equals(
                            resultItemContent.Result.ResultSha256,
                            toolTerminal.Invocation.ResultSha256,
                            StringComparison.Ordinal) ||
                        !string.Equals(
                            resultItemContent.Result.ProviderToolCallId,
                            terminalRecord.Snapshot.ProviderToolCallId,
                            StringComparison.Ordinal) ||
                        !string.Equals(
                            resultItemContent.Result.Error?.Code,
                            toolTerminal.Invocation.ErrorCode,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "Journal Tool Result does not match its terminal.");
                    }

                    item = new SessionItemSnapshot(
                        toolTerminal.ResultItem.ItemId,
                        toolTerminal.ResultItem.TurnId,
                        SessionItemType.ToolResult,
                        SessionItemStatus.Completed,
                        resultContent,
                        entry.Sequence,
                        entry.Timestamp,
                        entry.Timestamp);
                    if (!items.TryAdd(item.ItemId, item))
                    {
                        throw new InvalidDataException(
                            "Journal contains a duplicate Tool Result Item.");
                    }

                    texts[item.ItemId] = string.Empty;
                    toolInvocation = terminalRecord.Snapshot with
                    {
                        Status = toolTerminal.Invocation.Status,
                        ResultItemId = toolTerminal.ResultItem.ItemId,
                        ErrorCode = toolTerminal.Invocation.ErrorCode,
                        UpdatedAt = entry.Timestamp,
                        CompletedAt = entry.Timestamp,
                    };
                    toolInvocations[toolTerminal.Invocation.ToolInvocationId] =
                        terminalRecord with
                        {
                            Snapshot = toolInvocation,
                            Sequence = entry.Sequence,
                        };
                    turn = turns.GetValueOrDefault(toolInvocation.TurnId);
                    error = resultItemContent.Result.Error;
                    break;
                case SessionEventType.AgentInvocationSnapshotRecorded:
                    var invocationFact =
                        ReadFact<AgentInvocationSnapshotRecordedFact>(entry);
                    invocation = invocationFact.Snapshot;
                    turn = turns.GetValueOrDefault(invocationFact.TurnId);
                    break;
                case SessionEventType.ProviderUsageRecorded:
                    var usageFact = ReadFact<ProviderUsageRecordedFact>(entry);
                    usage = usageFact.Usage;
                    turn = turns.GetValueOrDefault(usageFact.TurnId);
                    break;
                case SessionEventType.CompactionCheckpointRecorded:
                    var compactionFact =
                        ReadFact<CompactionCheckpointRecordedFact>(entry);
                    compaction = compactionFact.Checkpoint;
                    turn = turns.GetValueOrDefault(compactionFact.TurnId);
                    break;
                case SessionEventType.TurnWaitingApproval:
                case SessionEventType.TurnWaitingInput:
                    var waiting = ReadFact<TurnWaitingFact>(entry);
                    if (waiting.ToolInvocationId is { } waitingInvocationId &&
                        (waiting.InteractionType != SessionInteractionType.Approval ||
                         !toolInvocations.TryGetValue(
                             waitingInvocationId,
                             out var waitingInvocation) ||
                         waitingInvocation.Snapshot.TurnId != waiting.TurnId ||
                         waitingInvocation.Snapshot.CompletedAt is not null))
                    {
                        throw new InvalidDataException(
                            "Journal waiting interaction has an invalid Tool Invocation.");
                    }

                    if (waiting.ToolInvocationId is { } referencedInvocationId)
                    {
                        var waitingRecord = toolInvocations[referencedInvocationId];
                        toolInvocation = waitingRecord.Snapshot with
                        {
                            Status = ToolInvocationStatus.WaitingApproval,
                            UpdatedAt = entry.Timestamp,
                        };
                        toolInvocations[referencedInvocationId] =
                            waitingRecord with
                            {
                                Snapshot = toolInvocation,
                                Sequence = entry.Sequence,
                            };
                    }

                    turn = UpdateHistoryTurn(
                        turns,
                        waiting.TurnId,
                        WaitingStatus(waiting.InteractionType),
                        entry.Timestamp,
                        error: null);
                    item = new SessionItemSnapshot(
                        waiting.ItemId,
                        waiting.TurnId,
                        waiting.RequestItemType,
                        SessionItemStatus.Completed,
                        DeserializeContent(waiting.RequestItemType, waiting.Request),
                        entry.Sequence,
                        entry.Timestamp,
                        entry.Timestamp);
                    items[item.ItemId] = item;
                    texts[item.ItemId] = waiting.ContentText ?? string.Empty;
                    interaction = new PendingInteractionSnapshot(
                        waiting.InteractionId,
                        entry.ThreadId,
                        waiting.TurnId,
                        waiting.InteractionType,
                        IsResolved: false,
                        entry.Timestamp,
                        waiting.TimeoutAt,
                        waiting.ToolInvocationId);
                    interactions[interaction.InteractionId] = interaction;
                    break;
                case SessionEventType.InteractionResolved:
                    var resolved = ReadFact<InteractionResolvedFact>(entry);
                    interaction = interactions[resolved.InteractionId] with
                    {
                        IsResolved = true,
                    };
                    interactions[resolved.InteractionId] = interaction;
                    item = new SessionItemSnapshot(
                        resolved.ResponseItemId,
                        interaction.TurnId,
                        resolved.ResponseItemType,
                        SessionItemStatus.Completed,
                        DeserializeContent(resolved.ResponseItemType, resolved.Resolution),
                        entry.Sequence,
                        entry.Timestamp,
                        entry.Timestamp);
                    items[item.ItemId] = item;
                    texts[item.ItemId] = resolved.ContentText ?? string.Empty;
                    turn = turns.GetValueOrDefault(interaction.TurnId);
                    break;
                case SessionEventType.TurnExecutionResumed:
                    var resumed = ReadFact<TurnExecutionResumedFact>(entry);
                    turn = UpdateHistoryTurn(
                        turns,
                        resumed.TurnId,
                        TurnStatus.Running,
                        entry.Timestamp,
                        error: null);
                    interaction = interactions.GetValueOrDefault(resumed.InteractionId);
                    break;
                case SessionEventType.TurnCompleted:
                case SessionEventType.TurnFailed:
                case SessionEventType.TurnCancelled:
                    var terminal = ReadFact<TurnTerminalFact>(entry);
                    error = terminal.Error;
                    turn = UpdateHistoryTurn(
                        turns,
                        terminal.TurnId,
                        entry.EntryType switch
                        {
                            SessionEventType.TurnCompleted => TurnStatus.Completed,
                            SessionEventType.TurnFailed => TurnStatus.Failed,
                            _ => TurnStatus.Cancelled,
                        },
                        entry.Timestamp,
                        error);
                    break;
            }

            events.Add(new SessionEvent(
                entry.ThreadId,
                entry.Sequence,
                entry.EntryId,
                entry.Timestamp,
                entry.EntryType,
                new SessionEventPayload(
                    Thread: thread,
                    Turn: turn,
                    Item: item,
                    QueueItem: queueItem,
                    Interaction: interaction,
                    Error: error,
                    Invocation: invocation,
                    Usage: usage,
                    Compaction: compaction,
                    ToolInvocation: toolInvocation,
                    ToolResult: item?.Content is ToolResultItemContent toolResult
                        ? toolResult.Result
                        : null)));
        }

        return events.ToArray();
    }

    private void RestoreHistoryCheckpoint(
        Guid threadId,
        HistoryCheckpointFact checkpoint)
    {
        var turnIds = _turns.Values
            .Where(turn => turn.ThreadId == threadId)
            .Select(turn => turn.TurnId)
            .ToHashSet();
        foreach (var interaction in _interactions
                     .Where(pair => turnIds.Contains(pair.Value.Snapshot.TurnId))
                     .Select(pair => pair.Key))
        {
            _interactions.TryRemove(interaction, out _);
        }

        foreach (var item in _items
                     .Where(pair => turnIds.Contains(pair.Value.TurnId))
                     .Select(pair => pair.Key))
        {
            _items.TryRemove(item, out _);
            _itemText.TryRemove(item, out _);
        }

        foreach (var turnId in turnIds)
        {
            _turns.TryRemove(turnId, out _);
        }

        foreach (var invocation in _toolInvocations
                     .Where(pair => pair.Value.Snapshot.ThreadId == threadId)
                     .Select(pair => pair.Key))
        {
            _toolInvocations.TryRemove(invocation, out _);
        }

        foreach (var turn in checkpoint.Turns)
        {
            if (!_turns.TryAdd(turn.TurnId, turn))
            {
                throw new InvalidDataException(
                    "History checkpoint contains a duplicate Turn.");
            }
        }

        foreach (var item in checkpoint.Items)
        {
            var snapshot = new SessionItemSnapshot(
                item.ItemId,
                item.TurnId,
                item.ItemType,
                item.Status,
                DeserializeContent(item.ItemType, item.Content),
                item.Sequence,
                item.CreatedAt,
                item.UpdatedAt);
            if (!_items.TryAdd(item.ItemId, snapshot))
            {
                throw new InvalidDataException(
                    "History checkpoint contains a duplicate Item.");
            }

            _itemText[item.ItemId] = item.ContentText ?? string.Empty;
        }

        var checkpointItemIds = checkpoint.Items.Select(item => item.ItemId).ToArray();
        ValidateCheckpointToolItems(
            threadId,
            _turns,
            _items,
            checkpointItemIds);
        foreach (var invocation in checkpoint.ToolInvocations ?? [])
        {
            RestoreCheckpointToolInvocation(
                threadId,
                invocation,
                _items,
                _toolInvocations);
        }
        ValidateCheckpointToolResults(
            threadId,
            _items,
            checkpointItemIds,
            _toolInvocations.Values);

        _loadedExecutionThreads[threadId] = 0;
    }

    private static void ReplaceHistory(
        Guid threadId,
        HistoryCheckpointFact checkpoint,
        Dictionary<Guid, TurnSnapshot> turns,
        Dictionary<Guid, SessionItemSnapshot> items,
        Dictionary<Guid, string> texts,
        Dictionary<Guid, PendingInteractionSnapshot> interactions,
        Dictionary<Guid, ToolInvocationRecord> toolInvocations)
    {
        turns.Clear();
        items.Clear();
        texts.Clear();
        interactions.Clear();
        toolInvocations.Clear();
        foreach (var turn in checkpoint.Turns)
        {
            if (!turns.TryAdd(turn.TurnId, turn))
            {
                throw new InvalidDataException(
                    "History checkpoint contains a duplicate Turn.");
            }
        }

        foreach (var item in checkpoint.Items)
        {
            var snapshot = new SessionItemSnapshot(
                item.ItemId,
                item.TurnId,
                item.ItemType,
                item.Status,
                DeserializeContent(item.ItemType, item.Content),
                item.Sequence,
                item.CreatedAt,
                item.UpdatedAt);
            if (!items.TryAdd(item.ItemId, snapshot))
            {
                throw new InvalidDataException(
                    "History checkpoint contains a duplicate Item.");
            }

            texts[item.ItemId] = item.ContentText ?? string.Empty;
        }

        var checkpointItemIds = checkpoint.Items.Select(item => item.ItemId).ToArray();
        ValidateCheckpointToolItems(
            threadId,
            turns,
            items,
            checkpointItemIds);
        foreach (var invocation in checkpoint.ToolInvocations ?? [])
        {
            RestoreCheckpointToolInvocation(
                threadId,
                invocation,
                items,
                toolInvocations);
        }
        ValidateCheckpointToolResults(
            threadId,
            items,
            checkpointItemIds,
            toolInvocations.Values);
    }

    private static void RestoreCheckpointToolInvocation(
        Guid threadId,
        HistoryCheckpointToolInvocationFact fact,
        IReadOnlyDictionary<Guid, SessionItemSnapshot> items,
        IDictionary<Guid, ToolInvocationRecord> invocations)
    {
        var snapshot = fact.Snapshot;
        if (snapshot.ThreadId != threadId ||
            snapshot.CompletedAt is null ||
            !IsTerminalToolStatus(snapshot.Status) ||
            snapshot.ResultItemId is not { } resultItemId ||
            !items.TryGetValue(fact.ToolCallItemId, out var callItem) ||
            callItem.Type != SessionItemType.ToolCall ||
            callItem.Status != SessionItemStatus.Completed ||
            callItem.TurnId != snapshot.TurnId ||
            callItem.Content is not ToolCallItemContent calls ||
            !IsValidToolCallContent(calls) ||
            fact.CallIndex < 0 ||
            fact.CallIndex >= calls.Calls.Count ||
            !string.Equals(
                calls.Calls[fact.CallIndex].ProviderToolCallId,
                snapshot.ProviderToolCallId,
                StringComparison.Ordinal) ||
            !string.Equals(
                calls.Calls[fact.CallIndex].ProviderToolName,
                snapshot.ProviderToolName,
                StringComparison.Ordinal) ||
            !string.Equals(
                calls.Calls[fact.CallIndex].ArgumentsSha256,
                snapshot.ArgumentsSha256,
                StringComparison.Ordinal) ||
            !IsLowerSha256(snapshot.SnapshotSha256) ||
            !IsLowerSha256(snapshot.ArgumentsSha256) ||
            (snapshot.ToolDefinitionId is null) !=
            (snapshot.RuntimeBindingId is null) ||
            !items.TryGetValue(resultItemId, out var resultItem) ||
            resultItem.Type != SessionItemType.ToolResult ||
            resultItem.Status != SessionItemStatus.Completed ||
            resultItem.TurnId != snapshot.TurnId ||
            resultItem.Content is not ToolResultItemContent result ||
            !IsValidToolResultContent(result.Result) ||
            result.Result.ToolInvocationId != snapshot.ToolInvocationId ||
            result.Result.Status != snapshot.Status ||
            result.Result.AttemptCount != snapshot.AttemptCount ||
            !string.Equals(
                result.Result.ProviderToolCallId,
                snapshot.ProviderToolCallId,
                StringComparison.Ordinal) ||
            !string.Equals(
                result.Result.Error?.Code,
                snapshot.ErrorCode,
                StringComparison.Ordinal) ||
            invocations.Values.Any(invocation =>
                invocation.ToolCallItemId == fact.ToolCallItemId &&
                invocation.CallIndex == fact.CallIndex) ||
            !invocations.TryAdd(
                snapshot.ToolInvocationId,
                new ToolInvocationRecord(
                    snapshot,
                    fact.ToolCallItemId,
                    fact.CallIndex,
                    resultItem.Sequence)))
        {
            throw new InvalidDataException(
                "History checkpoint contains an invalid Tool Invocation.");
        }
    }

    private static void ValidateCheckpointToolItems(
        Guid threadId,
        IReadOnlyDictionary<Guid, TurnSnapshot> turns,
        IReadOnlyDictionary<Guid, SessionItemSnapshot> items,
        IEnumerable<Guid> itemIds)
    {
        foreach (var item in itemIds
                     .Select(itemId => items[itemId])
                     .Where(item =>
                         item.Type is
                             SessionItemType.ToolCall or
                             SessionItemType.ToolResult))
        {
            if (!turns.TryGetValue(item.TurnId, out var turn) ||
                turn.ThreadId != threadId ||
                item.Status != SessionItemStatus.Completed ||
                item.Type == SessionItemType.ToolCall &&
                (item.Content is not ToolCallItemContent call ||
                 !IsValidToolCallContent(call) ||
                 !HasValidAgentMessageReference(items, item.TurnId, call)) ||
                item.Type == SessionItemType.ToolResult &&
                (item.Content is not ToolResultItemContent result ||
                 !IsValidToolResultContent(result.Result)))
            {
                throw new InvalidDataException(
                    "History checkpoint contains an invalid Tool Item.");
            }
        }
    }

    private static void ValidateCheckpointToolResults(
        Guid threadId,
        IReadOnlyDictionary<Guid, SessionItemSnapshot> items,
        IEnumerable<Guid> itemIds,
        IEnumerable<ToolInvocationRecord> invocations)
    {
        var resultItemIds = invocations
            .Where(invocation => invocation.Snapshot.ThreadId == threadId)
            .Select(invocation => invocation.Snapshot.ResultItemId)
            .ToHashSet();
        if (itemIds.Select(itemId => items[itemId]).Any(item =>
                item.Type == SessionItemType.ToolResult &&
                !resultItemIds.Contains(item.ItemId)))
        {
            throw new InvalidDataException(
                "History checkpoint contains an orphan Tool Result Item.");
        }
    }

    private static SessionItemSnapshot UpdateHistoryItem(
        Dictionary<Guid, SessionItemSnapshot> items,
        Guid itemId,
        SessionItemStatus status,
        DateTimeOffset timestamp)
    {
        var item = items[itemId] with
        {
            Status = status,
            UpdatedAt = timestamp,
        };
        items[itemId] = item;
        return item;
    }

    private static TurnSnapshot UpdateHistoryTurn(
        Dictionary<Guid, TurnSnapshot> turns,
        Guid turnId,
        TurnStatus status,
        DateTimeOffset timestamp,
        SessionError? error)
    {
        var turn = turns[turnId] with
        {
            Status = status,
            UpdatedAt = timestamp,
            CompletedAt = status is TurnStatus.Completed or TurnStatus.Failed or TurnStatus.Cancelled
                ? timestamp
                : null,
            Error = error,
        };
        turns[turnId] = turn;
        return turn;
    }

    private void RestoreStartedItem(ThreadJournalEntry entry)
    {
        var fact = ReadFact<ItemStartedFact>(entry);
        var content = DeserializeContent(fact.ItemType, fact.Content);
        _items[fact.ItemId] = new SessionItemSnapshot(
            fact.ItemId,
            fact.TurnId,
            fact.ItemType,
            SessionItemStatus.Started,
            content,
            entry.Sequence,
            entry.Timestamp,
            entry.Timestamp);
        _itemText[fact.ItemId] = fact.ContentText ?? string.Empty;
    }

    private void RestoreItemDelta(ThreadJournalEntry entry)
    {
        var fact = ReadFact<ItemDeltaFact>(entry);
        if (!_items.TryGetValue(fact.ItemId, out var item))
        {
            throw ExecutionError(
                SessionErrorCodes.RecoveryRequired,
                "Journal delta does not have a started item.");
        }

        var text = _itemText.GetValueOrDefault(fact.ItemId) + fact.Delta;
        _itemText[fact.ItemId] = text;
        _items[fact.ItemId] = item with
        {
            Status = SessionItemStatus.Streaming,
            Content = new TextItemContent(text),
            UpdatedAt = entry.Timestamp,
        };
    }

    private void RestoreItemTerminal(
        ThreadJournalEntry entry,
        SessionItemStatus status)
    {
        var itemId = entry.EntryType == SessionEventType.ItemCompleted
            ? ReadFact<ItemCompletedFact>(entry).ItemId
            : ReadFact<ItemTerminalFact>(entry).ItemId;
        if (_items.TryGetValue(itemId, out var item))
        {
            _items[itemId] = item with
            {
                Status = status,
                UpdatedAt = entry.Timestamp,
            };
        }
    }

    private void RestoreToolCall(ThreadJournalEntry entry)
    {
        var fact = ReadFact<ToolCallRecordedFact>(entry);
        if (!HasValidToolItemDigest(
                fact.Content,
                fact.ContentLength,
                fact.ContentSha256))
        {
            throw new InvalidDataException(
                "Journal Tool Call Item digest is invalid.");
        }

        var content = DeserializeContent(SessionItemType.ToolCall, fact.Content);
        if (!_turns.TryGetValue(fact.TurnId, out var turn) ||
            turn.Status != TurnStatus.Running ||
            content is not ToolCallItemContent toolCall ||
            !IsValidToolCallContent(toolCall) ||
            !HasValidAgentMessageReference(_items, fact.TurnId, toolCall) ||
            _items.ContainsKey(fact.ItemId))
        {
            throw new InvalidDataException(
                "Journal contains a duplicate Tool Call Item.");
        }

        _items[fact.ItemId] = new SessionItemSnapshot(
            fact.ItemId,
            fact.TurnId,
            SessionItemType.ToolCall,
            SessionItemStatus.Completed,
            content,
            entry.Sequence,
            entry.Timestamp,
            entry.Timestamp);
        _itemText[fact.ItemId] = string.Empty;
    }

    private void RestoreToolInvocationStarted(ThreadJournalEntry entry)
    {
        var fact = ReadFact<ToolInvocationStartedFact>(entry);
        if (_toolInvocations.ContainsKey(fact.ToolInvocationId) ||
            !_items.TryGetValue(fact.ToolCallItemId, out var item) ||
            item.TurnId != fact.TurnId ||
            item.Type != SessionItemType.ToolCall ||
            item.Status != SessionItemStatus.Completed ||
            item.Content is not ToolCallItemContent content ||
            fact.CallIndex < 0 ||
            fact.CallIndex >= content.Calls.Count ||
            !CallMatches(content.Calls[fact.CallIndex], fact) ||
            _toolInvocations.Values.Any(invocation =>
                invocation.ToolCallItemId == fact.ToolCallItemId &&
                invocation.CallIndex == fact.CallIndex) ||
            (fact.ToolDefinitionId is null) != (fact.RuntimeBindingId is null) ||
            !IsLowerSha256(fact.SnapshotSha256) ||
            !IsLowerSha256(fact.ArgumentsSha256))
        {
            throw new InvalidDataException(
                "Journal contains an invalid Tool Invocation start.");
        }

        var snapshot = new ToolInvocationSnapshot(
            fact.ToolInvocationId,
            entry.ThreadId,
            fact.TurnId,
            fact.ProviderToolCallId,
            fact.ProviderToolName,
            fact.ToolDefinitionId,
            fact.RuntimeBindingId,
            fact.SnapshotSha256,
            fact.ArgumentsSha256,
            ToolInvocationStatus.Started,
            AttemptCount: 0,
            ResultItemId: null,
            ErrorCode: null,
            entry.Timestamp,
            entry.Timestamp,
            CompletedAt: null);
        _toolInvocations[fact.ToolInvocationId] =
            new ToolInvocationRecord(
                snapshot,
                fact.ToolCallItemId,
                fact.CallIndex,
                entry.Sequence);
    }

    private void RestoreToolInvocationAttemptStarted(ThreadJournalEntry entry)
    {
        var fact = ReadFact<ToolInvocationAttemptStartedFact>(entry);
        if (!_toolInvocations.TryGetValue(fact.ToolInvocationId, out var existing) ||
            existing.Snapshot.CompletedAt is not null ||
            fact.AttemptNumber != existing.Snapshot.AttemptCount + 1 ||
            fact.AttemptNumber is < 1 or > 2)
        {
            throw new InvalidDataException(
                "Journal contains an invalid Tool Invocation attempt.");
        }

        _toolInvocations[fact.ToolInvocationId] = existing with
        {
            Snapshot = existing.Snapshot with
            {
                Status = ToolInvocationStatus.Started,
                AttemptCount = fact.AttemptNumber,
                UpdatedAt = entry.Timestamp,
            },
            Sequence = entry.Sequence,
        };
    }

    private void RestoreToolInvocationTerminal(ThreadJournalEntry entry)
    {
        var fact = ReadFact<ToolInvocationTerminalJournalFact>(entry);
        var terminal = fact.Invocation;
        var resultItem = fact.ResultItem;
        if (!HasValidToolItemDigest(
                resultItem.Content,
                resultItem.ContentLength,
                resultItem.ContentSha256))
        {
            throw new InvalidDataException(
                "Journal Tool Result Item digest is invalid.");
        }

        var content = DeserializeContent(SessionItemType.ToolResult, resultItem.Content);
        if (!_toolInvocations.TryGetValue(terminal.ToolInvocationId, out var existing) ||
            existing.Snapshot.CompletedAt is not null ||
            terminal.ResultItemId != resultItem.ItemId ||
            resultItem.TurnId != existing.Snapshot.TurnId ||
            _items.ContainsKey(resultItem.ItemId) ||
            content is not ToolResultItemContent toolResult ||
            toolResult.Result.ToolInvocationId != terminal.ToolInvocationId ||
            toolResult.Result.Status != terminal.Status ||
            toolResult.Result.AttemptCount != existing.Snapshot.AttemptCount ||
            !IsValidToolResultContent(toolResult.Result) ||
            !string.Equals(
                toolResult.Result.ProviderToolCallId,
                existing.Snapshot.ProviderToolCallId,
                StringComparison.Ordinal) ||
            !string.Equals(
                toolResult.Result.ResultSha256,
                terminal.ResultSha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                toolResult.Result.Error?.Code,
                terminal.ErrorCode,
                StringComparison.Ordinal) ||
            !IsTerminalToolStatus(terminal.Status))
        {
            throw new InvalidDataException(
                "Journal contains an invalid Tool Invocation terminal.");
        }

        _items[resultItem.ItemId] = new SessionItemSnapshot(
            resultItem.ItemId,
            resultItem.TurnId,
            SessionItemType.ToolResult,
            SessionItemStatus.Completed,
            content,
            entry.Sequence,
            entry.Timestamp,
            entry.Timestamp);
        _itemText[resultItem.ItemId] = string.Empty;
        _toolInvocations[terminal.ToolInvocationId] = existing with
        {
            Snapshot = existing.Snapshot with
            {
                Status = terminal.Status,
                ResultItemId = resultItem.ItemId,
                ErrorCode = terminal.ErrorCode,
                UpdatedAt = entry.Timestamp,
                CompletedAt = entry.Timestamp,
            },
            Sequence = entry.Sequence,
        };
    }

    private void RestoreDeferredToolsActivated(ThreadJournalEntry entry)
    {
        var fact = ReadFact<DeferredToolsActivatedFact>(entry);
        if (!_agentInvocations.TryGetValue(fact.TurnId, out var invocation) ||
            invocation.Snapshot.Tools is not { } frozenTools ||
            fact.ToolDefinitionIds.Count is 0 or > 8 ||
            fact.ToolDefinitionIds.Distinct().Count() != fact.ToolDefinitionIds.Count)
        {
            throw new InvalidDataException(
                "Journal contains invalid Deferred Tool activations.");
        }

        var existing = _deferredToolActivations.Keys
            .Where(key => key.TurnId == fact.TurnId)
            .Select(key => key.ToolDefinitionId)
            .ToHashSet();
        if (existing.Count + fact.ToolDefinitionIds.Count > 32 ||
            fact.ToolDefinitionIds.Any(existing.Contains) ||
            fact.ToolDefinitionIds.Any(id =>
                !frozenTools.Registrations.Any(registration =>
                    registration.Exposure == ToolExposure.Deferred &&
                    registration.Definition.Id == id)))
        {
            throw new InvalidDataException(
                "Journal Deferred Tool activation does not match its snapshot.");
        }

        foreach (var id in fact.ToolDefinitionIds)
        {
            _deferredToolActivations[
                new DeferredActivationKey(fact.TurnId, id)] = entry.Sequence;
        }
    }

    private void RestoreWaiting(ThreadJournalEntry entry)
    {
        var fact = ReadFact<TurnWaitingFact>(entry);
        if (fact.ToolInvocationId is { } toolInvocationId &&
            (fact.InteractionType != SessionInteractionType.Approval ||
             !_toolInvocations.TryGetValue(toolInvocationId, out var toolInvocation) ||
             toolInvocation.Snapshot.TurnId != fact.TurnId ||
             toolInvocation.Snapshot.CompletedAt is not null))
        {
            throw new InvalidDataException(
                "Journal waiting interaction has an invalid Tool Invocation.");
        }

        var content = DeserializeContent(fact.RequestItemType, fact.Request);
        var item = new SessionItemSnapshot(
            fact.ItemId,
            fact.TurnId,
            fact.RequestItemType,
            SessionItemStatus.Completed,
            content,
            entry.Sequence,
            entry.Timestamp,
            entry.Timestamp);
        _items[fact.ItemId] = item;
        _itemText[fact.ItemId] = fact.ContentText ?? string.Empty;
        var snapshot = new PendingInteractionSnapshot(
            fact.InteractionId,
            entry.ThreadId,
            fact.TurnId,
            fact.InteractionType,
            IsResolved: false,
            entry.Timestamp,
            fact.TimeoutAt,
            fact.ToolInvocationId);
        _interactions[fact.InteractionId] = new InteractionRuntime(
            snapshot,
            fact.ItemId,
            fact.Request,
            fact.Checkpoint,
            Resolution: null);
        if (fact.ToolInvocationId is { } waitingInvocationId)
        {
            var waitingInvocation = _toolInvocations[waitingInvocationId];
            _toolInvocations[waitingInvocationId] = waitingInvocation with
            {
                Snapshot = waitingInvocation.Snapshot with
                {
                    Status = ToolInvocationStatus.WaitingApproval,
                    UpdatedAt = entry.Timestamp,
                },
                Sequence = entry.Sequence,
            };
        }

        UpdateTurn(
            fact.TurnId,
            WaitingStatus(fact.InteractionType),
            entry.Timestamp,
            null);
    }

    private void RestoreResolution(ThreadJournalEntry entry)
    {
        var fact = ReadFact<InteractionResolvedFact>(entry);
        if (!_interactions.TryGetValue(fact.InteractionId, out var interaction))
        {
            throw ExecutionError(
                SessionErrorCodes.RecoveryRequired,
                "Resolved interaction does not have a waiting fact.");
        }

        var content = DeserializeContent(fact.ResponseItemType, fact.Resolution);
        _items[fact.ResponseItemId] = new SessionItemSnapshot(
            fact.ResponseItemId,
            interaction.Snapshot.TurnId,
            fact.ResponseItemType,
            SessionItemStatus.Completed,
            content,
            entry.Sequence,
            entry.Timestamp,
            entry.Timestamp);
        _itemText[fact.ResponseItemId] = fact.ContentText ?? string.Empty;
        _interactions[fact.InteractionId] = interaction with
        {
            Snapshot = interaction.Snapshot with { IsResolved = true },
            Resolution = fact.Resolution,
        };
    }

    private void RestoreTerminalTurn(ThreadJournalEntry entry)
    {
        var fact = ReadFact<TurnTerminalFact>(entry);
        var status = entry.EntryType switch
        {
            SessionEventType.TurnCompleted => TurnStatus.Completed,
            SessionEventType.TurnFailed => TurnStatus.Failed,
            _ => TurnStatus.Cancelled,
        };
        UpdateTurn(fact.TurnId, status, entry.Timestamp, fact.Error);
    }

    private void UpdateTurn(
        Guid turnId,
        TurnStatus status,
        DateTimeOffset timestamp,
        SessionError? error)
    {
        if (!_turns.TryGetValue(turnId, out var turn))
        {
            throw ExecutionError(
                SessionErrorCodes.RecoveryRequired,
                "Journal turn transition does not have a start fact.");
        }

        _turns[turnId] = turn with
        {
            Status = status,
            UpdatedAt = timestamp,
            CompletedAt = status is TurnStatus.Completed or TurnStatus.Failed or TurnStatus.Cancelled
                ? timestamp
                : null,
            Error = error,
        };
    }

    private async Task<SessionCommandResult<T>?> TryReplayExecutionCommandAsync<T>(
        Guid idempotencyKey,
        string operation,
        string requestSha256,
        CancellationToken cancellationToken)
    {
        if (_executionIdempotency.TryGetValue(idempotencyKey, out var memory))
        {
            return memory.Operation == operation &&
                   memory.RequestSha256 == requestSha256 &&
                   memory.Result is SessionCommandResult<T> typed
                ? typed
                : Rejected<T>(
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

        var storedHash = match.Entry.EntryType switch
        {
            SessionEventType.InteractionResolved =>
                ReadFact<InteractionResolvedFact>(match.Entry).RequestSha256,
            SessionEventType.TurnCancelled =>
                ReadFact<TurnTerminalFact>(match.Entry).RequestSha256,
            _ => null,
        };
        if (!string.Equals(Wire(match.Entry.EntryType), operation, StringComparison.Ordinal) ||
            !string.Equals(storedHash, requestSha256, StringComparison.Ordinal))
        {
            return Rejected<T>(
                SessionErrorCodes.IdempotencyConflict,
                "Idempotency key is bound to another request.");
        }

        var thread = await _projection.ReadThreadSnapshotAsync(
            match.Entry.ThreadId,
            cancellationToken)
            ?? throw ExecutionError(
                SessionErrorCodes.ProjectionUnavailable,
                "Projected thread is unavailable.");
        RestoreExecutionState(thread, match.Replay.Entries);
        object value = match.Entry.EntryType switch
        {
            SessionEventType.InteractionResolved =>
                _interactions[
                    ReadFact<InteractionResolvedFact>(match.Entry).InteractionId].Snapshot,
            SessionEventType.TurnCancelled =>
                _turns[ReadFact<TurnTerminalFact>(match.Entry).TurnId],
            _ => throw new InvalidOperationException(),
        };
        if (value is not T resultValue)
        {
            return Rejected<T>(
                SessionErrorCodes.IdempotencyConflict,
                "Idempotency result type does not match the request.");
        }

        var result = new SessionCommandResult<T>(
            SessionCommandStatus.Committed,
            resultValue,
            match.Entry.Sequence,
            null,
            null);
        _executionIdempotency[idempotencyKey] =
            new ExecutionIdempotency(operation, requestSha256, result);
        return result;
    }

    private IReadOnlyList<SessionItemSnapshot> ModelHistory(Guid threadId)
    {
        var activeTurnId = _snapshots[threadId].ActiveTurnId;
        var turnIds = _turns.Values
            .Where(turn =>
                turn.ThreadId == threadId &&
                (turn.Status == TurnStatus.Completed ||
                 turn.TurnId == activeTurnId))
            .Select(turn => turn.TurnId)
            .ToHashSet();
        return Array.AsReadOnly(
            _items.Values
                .Where(item => turnIds.Contains(item.TurnId))
                .OrderBy(item => item.Sequence)
                .ThenBy(item => item.ItemId)
                .ToArray());
    }

    private static ThreadSnapshot ExecutionThread(
        ThreadSnapshot thread,
        long sequence,
        DateTimeOffset timestamp,
        Guid? activeTurnId,
        IReadOnlyList<QueuedTurnInputSnapshot>? queue = null) =>
        new(
            thread.ThreadId,
            thread.DisplayName,
            thread.Status,
            thread.Availability,
            thread.HistoryMode,
            sequence,
            activeTurnId,
            queue ?? thread.Queue,
            thread.CreatedAt,
            timestamp,
            thread.ProjectionState,
            thread.Diagnostic,
            thread.ProviderId,
            thread.ModelId,
            thread.AgentMode,
            thread.ExecutionWorkspace,
            thread.CoWorkProvenance,
            thread.AutomationProvenance);

    private InternalOperation InternalRequestHash(
        SessionEventType eventType,
        object payload)
    {
        var operation = Wire(eventType);
        return new InternalOperation(
            Guid.CreateVersion7(),
            RequestHash(operation, payload));
    }

    private static SessionExecutionException ExecutionError(
        string code,
        string message,
        bool retryable = false) =>
        new(new SessionError(code, message, retryable));

    private static SessionCommandResult<T> ConvertResult<T>(
        SessionCommandResult<ThreadSnapshot> result,
        T value) =>
        new(
            result.Status,
            result.Status == SessionCommandStatus.Rejected ? default : value,
            result.Sequence,
            result.CurrentSequence,
            result.Error);

    private static JsonElement SerializeContent(SessionItemContent content) =>
        JsonSerializer.SerializeToElement(content, content.GetType(), JsonOptions);

    private static T ReadFact<T>(ThreadJournalEntry entry) =>
        entry.Payload.Deserialize<T>(JsonOptions)
        ?? throw ExecutionError(
            SessionErrorCodes.RecoveryRequired,
            $"Journal payload for {entry.EntryType} is invalid.");

    private static SessionItemContent DeserializeContent(
        SessionItemType type,
        JsonElement content)
    {
        SessionItemContent? result = type switch
        {
            SessionItemType.UserMessage or
            SessionItemType.AgentMessage or
            SessionItemType.Reasoning =>
                content.Deserialize<TextItemContent>(JsonOptions),
            SessionItemType.ApprovalRequest =>
                content.TryGetProperty("toolInvocationId", out _)
                    ? content.Deserialize<ToolApprovalRequestContent>(JsonOptions)
                    : content.Deserialize<ApprovalRequestContent>(JsonOptions),
            SessionItemType.ApprovalResponse =>
                content.Deserialize<ApprovalResponseContent>(JsonOptions),
            SessionItemType.UserInputRequest =>
                content.Deserialize<UserInputRequestContent>(JsonOptions),
            SessionItemType.UserInputResponse =>
                content.Deserialize<UserInputResponseContent>(JsonOptions),
            SessionItemType.Error =>
                content.Deserialize<ErrorItemContent>(JsonOptions),
            SessionItemType.SystemNotice =>
                content.Deserialize<SystemNoticeContent>(JsonOptions),
            SessionItemType.ToolCall =>
                content.Deserialize<ToolCallItemContent>(JsonOptions),
            SessionItemType.ToolResult =>
                content.Deserialize<ToolResultItemContent>(JsonOptions),
            SessionItemType.ProviderAction =>
                content.Deserialize<ProviderActionItemContent>(JsonOptions),
            _ => null,
        };
        return result ?? throw ExecutionError(
            SessionErrorCodes.RecoveryRequired,
            "Journal item content is invalid.");
    }

    private static bool ContentMatches(
        SessionItemType type,
        SessionItemContent content) =>
        type switch
        {
            SessionItemType.UserMessage or
            SessionItemType.AgentMessage or
            SessionItemType.Reasoning => content is TextItemContent,
            SessionItemType.ApprovalRequest => content is ApprovalRequestContent,
            SessionItemType.ApprovalResponse => content is ApprovalResponseContent,
            SessionItemType.UserInputRequest => content is UserInputRequestContent,
            SessionItemType.UserInputResponse => content is UserInputResponseContent,
            SessionItemType.Error => content is ErrorItemContent,
            SessionItemType.SystemNotice => content is SystemNoticeContent,
            SessionItemType.ProviderAction => content is ProviderActionItemContent,
            _ => false,
        };

    private static bool CompactionSourceMatches(
        CompactionCheckpointSnapshot checkpoint,
        IEnumerable<SessionItemSnapshot> items)
    {
        try
        {
            return CompactionCheckpointIntegrity.SourceRangeIsClosed(
                       items,
                       checkpoint.SourceStartSequence,
                       checkpoint.SourceEndSequence) &&
                   string.Equals(
                       checkpoint.SourceMessagesSha256,
                       CompactionCheckpointIntegrity.SourceMessagesSha256(
                           items,
                           checkpoint.SourceStartSequence,
                           checkpoint.SourceEndSequence,
                           checkpoint.SchemaVersion),
                       StringComparison.Ordinal);
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static string? ContentText(SessionItemContent content) =>
        content switch
        {
            TextItemContent text => text.Text,
            ApprovalRequestContent approval => approval.Prompt,
            UserInputRequestContent input => input.Prompt,
            UserInputResponseContent input => input.Text,
            ErrorItemContent error => error.Message,
            SystemNoticeContent notice => notice.Message,
            _ => null,
        };

    private static SessionItemType RequestItemType(
        SessionInteractionType type,
        SessionItemContent content) =>
        (type, content) switch
        {
            (SessionInteractionType.Approval, ApprovalRequestContent) =>
                SessionItemType.ApprovalRequest,
            (SessionInteractionType.UserInput, UserInputRequestContent) =>
                SessionItemType.UserInputRequest,
            _ => throw ExecutionError(
                SessionErrorCodes.InvalidState,
                "Interaction request content does not match its type."),
        };

    private static SessionItemType ResponseItemType(
        SessionInteractionType type,
        SessionItemContent content) =>
        (type, content) switch
        {
            (SessionInteractionType.Approval, ApprovalResponseContent) =>
                SessionItemType.ApprovalResponse,
            (SessionInteractionType.UserInput, UserInputResponseContent) =>
                SessionItemType.UserInputResponse,
            _ => throw ExecutionError(
                SessionErrorCodes.InvalidState,
                "Interaction response content does not match its type."),
        };

    private static TurnStatus WaitingStatus(SessionInteractionType type) =>
        type == SessionInteractionType.Approval
            ? TurnStatus.WaitingApproval
            : TurnStatus.WaitingInput;

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool IsLowerSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool CallMatches(
        ToolCallItemEntry call,
        RecordToolInvocationStartedIntent intent) =>
        string.Equals(
            call.ProviderToolCallId,
            intent.ProviderToolCallId,
            StringComparison.Ordinal) &&
        string.Equals(
            call.ProviderToolName,
            intent.ProviderToolName,
            StringComparison.Ordinal) &&
        string.Equals(
            call.ArgumentsSha256,
            intent.ArgumentsSha256,
            StringComparison.Ordinal);

    private static bool CallMatches(
        ToolCallItemEntry call,
        ToolInvocationStartedFact fact) =>
        string.Equals(
            call.ProviderToolCallId,
            fact.ProviderToolCallId,
            StringComparison.Ordinal) &&
        string.Equals(
            call.ProviderToolName,
            fact.ProviderToolName,
            StringComparison.Ordinal) &&
        string.Equals(
            call.ArgumentsSha256,
            fact.ArgumentsSha256,
            StringComparison.Ordinal);

    private static bool StartedIntentMatches(
        ToolInvocationRecord existing,
        ThreadSnapshot thread,
        TurnSnapshot turn,
        RecordToolInvocationStartedIntent intent) =>
        existing.Snapshot.ThreadId == thread.ThreadId &&
        existing.Snapshot.TurnId == turn.TurnId &&
        existing.ToolCallItemId == intent.ToolCallItemId &&
        existing.CallIndex == intent.CallIndex &&
        string.Equals(
            existing.Snapshot.ProviderToolCallId,
            intent.ProviderToolCallId,
            StringComparison.Ordinal) &&
        string.Equals(
            existing.Snapshot.ProviderToolName,
            intent.ProviderToolName,
            StringComparison.Ordinal) &&
        existing.Snapshot.ToolDefinitionId == intent.ToolDefinitionId &&
        existing.Snapshot.RuntimeBindingId == intent.RuntimeBindingId &&
        string.Equals(
            existing.Snapshot.SnapshotSha256,
            intent.SnapshotSha256,
            StringComparison.Ordinal) &&
        string.Equals(
            existing.Snapshot.ArgumentsSha256,
            intent.ArgumentsSha256,
            StringComparison.Ordinal);

    private static bool IsTerminalToolStatus(ToolInvocationStatus status) =>
        status is
            ToolInvocationStatus.Completed or
            ToolInvocationStatus.Rejected or
            ToolInvocationStatus.Failed or
            ToolInvocationStatus.Cancelled or
            ToolInvocationStatus.TimedOut or
            ToolInvocationStatus.OutcomeUnknown;

    private static bool HasValidToolItemDigest(
        JsonElement content,
        int contentLength,
        string contentSha256)
    {
        var bytes = ThreadJournal.Canonicalize(content);
        return contentLength == bytes.Length &&
               IsLowerSha256(contentSha256) &&
               string.Equals(Hash(bytes), contentSha256, StringComparison.Ordinal);
    }

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
        IReadOnlyDictionary<Guid, SessionItemSnapshot> items,
        Guid turnId,
        ToolCallItemContent content) =>
        content.AgentMessageItemId is not { } itemId ||
        items.TryGetValue(itemId, out var item) &&
        item.TurnId == turnId &&
        item.Type == SessionItemType.AgentMessage &&
        item.Status == SessionItemStatus.Completed;

    private static bool IsValidToolResultContent(ToolResultSnapshot result) =>
        IsLowerSha256(result.ResultSha256) &&
        (result.Status == ToolInvocationStatus.Completed
            ? result.Output is { ValueKind: not JsonValueKind.Undefined } &&
              result.Error is null
            : IsTerminalToolStatus(result.Status) &&
              result.Output is null &&
              result.Error is { Code.Length: > 0 });

    private sealed record InteractionRuntime(
        PendingInteractionSnapshot Snapshot,
        Guid RequestItemId,
        JsonElement Request,
        SessionExecutionCheckpoint Checkpoint,
        JsonElement? Resolution);

    private sealed record ExecutionIdempotency(
        string Operation,
        string RequestSha256,
        object Result);

    private sealed record InvocationRecord(
        AgentInvocationSnapshot Snapshot,
        long Sequence);

    private sealed record ToolInvocationRecord(
        ToolInvocationSnapshot Snapshot,
        Guid ToolCallItemId,
        int CallIndex,
        long Sequence);

    private sealed record ProviderUsageKey(
        Guid InvocationId,
        int AttemptNumber,
        ProviderInvocationPurpose Purpose);

    private sealed record UsageRecord(
        ProviderUsageSnapshot Usage,
        long Sequence);

    private sealed record DeferredActivationKey(
        Guid TurnId,
        ToolDefinitionId ToolDefinitionId);

    private sealed record CompactionRecord(
        CompactionCheckpointSnapshot Checkpoint,
        long Sequence);

    private sealed record InternalOperation(
        Guid IdempotencyKey,
        string RequestSha256);
}
