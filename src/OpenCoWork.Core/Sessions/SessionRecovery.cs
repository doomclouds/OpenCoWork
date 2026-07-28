using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCoWork.Abstractions;

namespace OpenCoWork.Core.Sessions;

internal enum SessionRecoveryFaultPoint
{
    AfterFactFlushed,
    AfterJournalMoved,
    AfterProjectionApplied,
    AfterDeletionMarked,
    AfterOwnedFilesDeleted,
    AfterProjectionDeleted,
    AfterJournalDeleted,
    AfterReceiptWritten,
}

internal sealed partial class SessionService
{
    private static readonly TimeSpan DeleteTokenLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DeleteReceiptLifetime = TimeSpan.FromDays(7);
    private readonly ConcurrentDictionary<Guid, DeletePreparationState>
        _deletePreparations = [];
    private readonly Action<SessionRecoveryFaultPoint>? _recoveryFaultInjector;

    public Task<SessionCommandResult<ThreadSnapshot>> ArchiveThreadAsync(
        ThreadMutationRequest request,
        CancellationToken cancellationToken = default) =>
        RelocateThreadAsync(
            request,
            SessionEventType.ThreadArchived,
            ThreadJournalLocation.Active,
            ThreadJournalLocation.Archived,
            ThreadStatus.Archived,
            cancellationToken);

    public Task<SessionCommandResult<ThreadSnapshot>> UnarchiveThreadAsync(
        ThreadMutationRequest request,
        CancellationToken cancellationToken = default) =>
        RelocateThreadAsync(
            request,
            SessionEventType.ThreadUnarchived,
            ThreadJournalLocation.Archived,
            ThreadJournalLocation.Active,
            ThreadStatus.Active,
            cancellationToken);

    public async Task<SessionQueryResult<DeletePreparation>> PrepareDeleteAsync(
        PrepareDeleteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireId(request.ThreadId, nameof(request.ThreadId), "Thread ID");
        ArgumentOutOfRangeException.ThrowIfNegative(request.ExpectedSequence);
        var threadGate = GetThreadGate(request.ThreadId);
        await threadGate.WaitAsync(cancellationToken);
        try
        {
            if (!CanAcceptNewWork)
            {
                var unavailable = NewWorkUnavailable<DeletePreparation>();
                return new SessionQueryResult<DeletePreparation>(
                    null,
                    unavailable.Error);
            }

            var thread = await GetSnapshotAsync(request.ThreadId, cancellationToken);
            if (thread is null)
            {
                return QueryError<DeletePreparation>(
                    SessionErrorCodes.NotFound,
                    "Thread was not found.");
            }

            if (thread.CurrentSequence != request.ExpectedSequence)
            {
                return QueryError<DeletePreparation>(
                    SessionErrorCodes.SequenceConflict,
                    "Thread sequence does not match.");
            }

            if (!CanManageThread(thread, ThreadStatus.Archived))
            {
                return QueryError<DeletePreparation>(
                    SessionErrorCodes.InvalidState,
                    "Only an idle archived thread with an empty queue can be deleted.");
            }

            var tokenBytes = RandomNumberGenerator.GetBytes(32);
            var token = Convert.ToBase64String(tokenBytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            var expiresAt = _timeProvider.GetUtcNow() + DeleteTokenLifetime;
            _deletePreparations[request.ThreadId] = new DeletePreparationState(
                request.ExpectedSequence,
                SHA256.HashData(Encoding.UTF8.GetBytes(token)),
                expiresAt);
            return new SessionQueryResult<DeletePreparation>(
                new DeletePreparation(
                    request.ThreadId,
                    request.ExpectedSequence,
                    token,
                    expiresAt),
                null);
        }
        finally
        {
            threadGate.Release();
        }
    }

    public async Task<SessionCommandResult<bool>> DeleteThreadAsync(
        DeleteThreadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Token);
        RequireId(request.ThreadId, nameof(request.ThreadId), "Thread ID");
        RequireId(request.IdempotencyKey, nameof(request.IdempotencyKey), "Idempotency key");
        ArgumentOutOfRangeException.ThrowIfNegative(request.ExpectedSequence);
        var threadHash = HashId(request.ThreadId);
        var idempotencyHash = HashId(request.IdempotencyKey);
        var operation = Wire(SessionEventType.ThreadDeletionRequested);
        var requestSha256 = RequestHash(
            operation,
            new
            {
                ThreadId = Wire(request.ThreadId),
                IdempotencyKey = Wire(request.IdempotencyKey),
                request.ExpectedSequence,
            });
        var keyGate = GetIdempotencyGate(request.IdempotencyKey);
        await keyGate.WaitAsync(cancellationToken);
        try
        {
            var receipt = await _projection.ReadDeletionReceiptAsync(
                idempotencyHash,
                cancellationToken);
            if (receipt is not null && receipt.ExpiresAt > _timeProvider.GetUtcNow())
            {
                return string.Equals(
                    receipt.ThreadIdSha256,
                    threadHash,
                    StringComparison.Ordinal)
                    ? new SessionCommandResult<bool>(
                        SessionCommandStatus.Committed,
                        true,
                        receipt.Sequence,
                        null,
                        null)
                    : Rejected<bool>(
                        SessionErrorCodes.IdempotencyConflict,
                        "Idempotency key is bound to another deleted thread.");
            }

            var journalMatch = await _journal.FindByIdempotencyKeyAsync(
                request.IdempotencyKey,
                cancellationToken);
            if (journalMatch is not null)
            {
                return await ResumeCommittedDeletionAsync(
                    request,
                    requestSha256,
                    journalMatch,
                    cancellationToken);
            }

            if (!CanAcceptNewWork)
            {
                return NewWorkUnavailable<bool>();
            }

            var threadGate = GetThreadGate(request.ThreadId);
            await threadGate.WaitAsync(cancellationToken);
            try
            {
                var thread = await GetSnapshotAsync(request.ThreadId, cancellationToken);
                if (thread is null)
                {
                    return Rejected<bool>(
                        SessionErrorCodes.NotFound,
                        "Thread was not found.");
                }

                if (thread.CurrentSequence != request.ExpectedSequence)
                {
                    return Rejected<bool>(
                        SessionErrorCodes.SequenceConflict,
                        "Thread sequence does not match.",
                        thread.CurrentSequence);
                }

                if (!CanManageThread(thread, ThreadStatus.Archived))
                {
                    return Rejected<bool>(
                        SessionErrorCodes.InvalidState,
                        "Only an idle archived thread with an empty queue can be deleted.",
                        thread.CurrentSequence);
                }

                var tokenError = ConsumeDeleteToken(request, thread);
                if (tokenError is not null)
                {
                    return new SessionCommandResult<bool>(
                        SessionCommandStatus.Rejected,
                        false,
                        null,
                        thread.CurrentSequence,
                        tokenError);
                }

                var timestamp = _timeProvider.GetUtcNow();
                var nextThread = CopySnapshot(
                    thread,
                    currentSequence: thread.CurrentSequence + 1,
                    updatedAt: timestamp);
                ThreadJournalEntry entry;
                try
                {
                    entry = await _journal.AppendAsync(
                        ThreadJournalLocation.Archived,
                        new ThreadJournalDraft(
                            request.ThreadId,
                            nextThread.CurrentSequence,
                            Guid.CreateVersion7(),
                            timestamp,
                            SessionEventType.ThreadDeletionRequested,
                            request.IdempotencyKey,
                            new ThreadDeletionRequestedFact(
                                threadHash,
                                idempotencyHash,
                                request.ExpectedSequence,
                                requestSha256)),
                        cancellationToken);
                }
                catch (ThreadJournalCommittedException committed)
                {
                    entry = committed.Entry;
                }
                catch (ThreadJournalException exception)
                {
                    return Rejected<bool>(exception.Code, exception.Message);
                }

                _recoveryFaultInjector?.Invoke(
                    SessionRecoveryFaultPoint.AfterFactFlushed);
                var intent = new ThreadDeletionRecoveryIntent(
                    request.ThreadId,
                    threadHash,
                    idempotencyHash,
                    entry.Sequence,
                    requestSha256,
                    entry.Timestamp,
                    entry.Timestamp + DeleteReceiptLifetime);
                await _journal.WriteDeletionRecoveryIntentAsync(
                    intent,
                    CancellationToken.None);
                try
                {
                    await _journal.MoveAsync(
                        ThreadJournalLocation.Archived,
                        ThreadJournalLocation.Deleting,
                        request.ThreadId,
                        CancellationToken.None);
                }
                catch (Exception exception)
                {
                    return PendingDelete(entry.Sequence, exception.Message);
                }

                _recoveryFaultInjector?.Invoke(
                    SessionRecoveryFaultPoint.AfterJournalMoved);
                _snapshots[request.ThreadId] = nextThread;
                var projection = await _projection.ApplyCommittedAsync(
                    entry,
                    CancellationToken.None);
                if (projection.Status ==
                    SessionCommandStatus.CommittedPendingProjection)
                {
                    return new SessionCommandResult<bool>(
                        SessionCommandStatus.CommittedPendingProjection,
                        true,
                        entry.Sequence,
                        null,
                        projection.Error);
                }

                _recoveryFaultInjector?.Invoke(
                    SessionRecoveryFaultPoint.AfterProjectionApplied);
                _eventChannel.Publish(new SessionEvent(
                    entry.ThreadId,
                    entry.Sequence,
                    entry.EntryId,
                    entry.Timestamp,
                    entry.EntryType,
                    new SessionEventPayload(Thread: nextThread)));
                try
                {
                    await FinishDeletionAsync(intent, CancellationToken.None);
                }
                catch (Exception exception)
                {
                    return PendingDelete(entry.Sequence, exception.Message);
                }

                return new SessionCommandResult<bool>(
                    SessionCommandStatus.Committed,
                    true,
                    entry.Sequence,
                    null,
                    null);
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

    public async Task<SessionCommandResult<ThreadSnapshot>> ForkThreadAsync(
        ForkThreadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireId(
            request.SourceThreadId,
            nameof(request.SourceThreadId),
            "Source thread ID");
        RequireId(request.IdempotencyKey, nameof(request.IdempotencyKey), "Idempotency key");
        ArgumentOutOfRangeException.ThrowIfLessThan(request.SourceSequence, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(request.ExpectedSequence);
        var operation = Wire(SessionEventType.ThreadForked);
        var requestSha256 = RequestHash(
            operation,
            new
            {
                SourceThreadId = Wire(request.SourceThreadId),
                request.SourceSequence,
                request.ExpectedSequence,
                request.DisplayName,
            });
        var keyGate = GetIdempotencyGate(request.IdempotencyKey);
        await keyGate.WaitAsync(cancellationToken);
        try
        {
            var replayed = await TryReplayThreadCommandAsync(
                request.IdempotencyKey,
                operation,
                requestSha256,
                cancellationToken);
            if (replayed is not null)
            {
                return replayed;
            }

            if (!CanAcceptNewWork)
            {
                return NewWorkUnavailable<ThreadSnapshot>();
            }

            ThreadSnapshot source;
            ThreadJournalEntry[] sourceEntries;
            var sourceGate = GetThreadGate(request.SourceThreadId);
            await sourceGate.WaitAsync(cancellationToken);
            try
            {
                source = await GetSnapshotAsync(
                    request.SourceThreadId,
                    cancellationToken)
                    ?? throw new SessionStateException(
                        SessionErrorCodes.NotFound,
                        "Source thread was not found.");
                if (source.CurrentSequence != request.ExpectedSequence)
                {
                    return Rejected<ThreadSnapshot>(
                        SessionErrorCodes.SequenceConflict,
                        "Source thread sequence does not match.",
                        source.CurrentSequence);
                }

                if (source.Availability != ThreadAvailability.Available ||
                    request.SourceSequence > source.CurrentSequence)
                {
                    return Rejected<ThreadSnapshot>(
                        SessionErrorCodes.InvalidState,
                        "Source thread is not available at the requested boundary.",
                        source.CurrentSequence);
                }

                var replay = await _journal.ReplayAsync(
                    source.Status == ThreadStatus.Archived
                        ? ThreadJournalLocation.Archived
                        : ThreadJournalLocation.Active,
                    source.ThreadId,
                    cancellationToken);
                sourceEntries = replay.Entries
                    .Where(entry => entry.Sequence <= request.SourceSequence)
                    .ToArray();
                if (!IsStableBoundary(sourceEntries, request.SourceSequence))
                {
                    return Rejected<ThreadSnapshot>(
                        SessionErrorCodes.InvalidState,
                        "Fork target must be thread creation or a terminal turn boundary.",
                        source.CurrentSequence);
                }
            }
            finally
            {
                sourceGate.Release();
            }

            var targetThreadId = Guid.CreateVersion7();
            var checkpoint = BuildHistoryCheckpoint(
                sourceEntries,
                targetThreadId,
                remapIds: true);
            var timestamp = _timeProvider.GetUtcNow();
            var target = new ThreadSnapshot(
                targetThreadId,
                string.IsNullOrWhiteSpace(request.DisplayName)
                    ? source.DisplayName
                    : request.DisplayName,
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
                source.ProviderId,
                source.ModelId,
                source.AgentMode);
            var targetGate = GetThreadGate(targetThreadId);
            await targetGate.WaitAsync(cancellationToken);
            try
            {
                var result = await CommitAsync(
                    request.IdempotencyKey,
                    operation,
                    requestSha256,
                    target,
                    new ThreadForkedFact(
                        source.ThreadId,
                        request.SourceSequence,
                        target.DisplayName,
                        HistoryMode.Server,
                        checkpoint,
                        requestSha256,
                        source.ProviderId,
                        source.ModelId,
                        source.AgentMode),
                    SessionEventType.ThreadForked,
                    cancellationToken);
                if (result.Status != SessionCommandStatus.Rejected)
                {
                    RestoreHistoryCheckpoint(targetThreadId, checkpoint);
                }

                return result;
            }
            finally
            {
                targetGate.Release();
            }
        }
        catch (SessionStateException exception)
        {
            return Rejected<ThreadSnapshot>(exception.Code, exception.Message);
        }
        finally
        {
            keyGate.Release();
        }
    }

    public async Task<SessionCommandResult<RollbackResult>> RollbackThreadAsync(
        RollbackThreadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireId(request.ThreadId, nameof(request.ThreadId), "Thread ID");
        RequireId(request.IdempotencyKey, nameof(request.IdempotencyKey), "Idempotency key");
        ArgumentOutOfRangeException.ThrowIfLessThan(request.TargetSequence, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(request.ExpectedSequence);
        var operation = Wire(SessionEventType.ThreadRolledBack);
        var requestSha256 = RequestHash(
            operation,
            new
            {
                ThreadId = Wire(request.ThreadId),
                request.TargetSequence,
                request.ExpectedSequence,
            });
        var keyGate = GetIdempotencyGate(request.IdempotencyKey);
        await keyGate.WaitAsync(cancellationToken);
        try
        {
            var replayed = await TryReplayThreadCommandAsync(
                request.IdempotencyKey,
                operation,
                requestSha256,
                cancellationToken);
            if (replayed is not null)
            {
                return ConvertRollback(replayed);
            }

            if (!CanAcceptNewWork)
            {
                return NewWorkUnavailable<RollbackResult>();
            }

            var threadGate = GetThreadGate(request.ThreadId);
            await threadGate.WaitAsync(cancellationToken);
            try
            {
                var thread = await GetSnapshotAsync(request.ThreadId, cancellationToken);
                if (thread is null)
                {
                    return RejectedRollback(
                        SessionErrorCodes.NotFound,
                        "Thread was not found.");
                }

                if (thread.CurrentSequence != request.ExpectedSequence)
                {
                    return RejectedRollback(
                        SessionErrorCodes.SequenceConflict,
                        "Thread sequence does not match.",
                        thread.CurrentSequence);
                }

                if (thread.Status is not (ThreadStatus.Active or ThreadStatus.Paused) ||
                    thread.Availability != ThreadAvailability.Available ||
                    thread.ActiveTurnId is not null ||
                    thread.Queue.Count != 0 ||
                    request.TargetSequence > thread.CurrentSequence)
                {
                    return RejectedRollback(
                        SessionErrorCodes.InvalidState,
                        "Rollback requires an idle active or paused thread and an empty queue.",
                        thread.CurrentSequence);
                }

                var replay = await _journal.ReplayAsync(
                    ThreadJournalLocation.Active,
                    request.ThreadId,
                    cancellationToken);
                var entries = replay.Entries
                    .Where(entry => entry.Sequence <= request.TargetSequence)
                    .ToArray();
                if (!IsStableBoundary(entries, request.TargetSequence))
                {
                    return RejectedRollback(
                        SessionErrorCodes.InvalidState,
                        "Rollback target must be thread creation or a terminal turn boundary.",
                        thread.CurrentSequence);
                }

                var checkpoint = BuildHistoryCheckpoint(
                    entries,
                    request.ThreadId,
                    remapIds: false);
                var timestamp = _timeProvider.GetUtcNow();
                var nextThread = CopySnapshot(
                    thread,
                    currentSequence: thread.CurrentSequence + 1,
                    updatedAt: timestamp);
                var committed = await CommitAsync(
                    request.IdempotencyKey,
                    operation,
                    requestSha256,
                    nextThread,
                    new ThreadRolledBackFact(
                        request.TargetSequence,
                        checkpoint,
                        requestSha256),
                    SessionEventType.ThreadRolledBack,
                    cancellationToken);
                if (committed.Status != SessionCommandStatus.Rejected)
                {
                    RestoreHistoryCheckpoint(request.ThreadId, checkpoint);
                }

                return ConvertRollback(committed);
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

    internal async Task<IReadOnlyList<Guid>> RecoverSessionStateAsync(
        CancellationToken cancellationToken = default)
    {
        var failures = new HashSet<Guid>();
        var locations = Enum.GetValues<ThreadJournalLocation>();
        var byThread = locations
            .SelectMany(location => _journal.ListThreadIds(location)
                .Select(threadId => (threadId, location)))
            .GroupBy(item => item.threadId)
            .OrderBy(group => group.Key);
        foreach (var group in byThread)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var threadId = group.Key;
            try
            {
                var found = group.ToArray();
                if (found.Length != 1)
                {
                    failures.Add(threadId);
                    await MarkRecoveryRequiredAsync(
                        threadId,
                        "Thread journal exists in more than one lifecycle directory.",
                        cancellationToken);
                    continue;
                }

                var threadGate = GetThreadGate(threadId);
                await threadGate.WaitAsync(cancellationToken);
                try
                {
                    if (!await RecoverJournalAsync(
                            threadId,
                            found[0].location,
                            cancellationToken))
                    {
                        failures.Add(threadId);
                        await MarkRecoveryRequiredAsync(
                            threadId,
                            "Thread journal requires recovery.",
                            cancellationToken);
                    }
                }
                finally
                {
                    threadGate.Release();
                }
            }
            catch
            {
                failures.Add(threadId);
                await MarkRecoveryRequiredAsync(
                    threadId,
                    "Thread recovery failed.",
                    CancellationToken.None);
            }
        }

        foreach (var intent in await _journal.ReadDeletionRecoveryIntentsAsync(
                     cancellationToken))
        {
            if (failures.Contains(intent.ThreadId) ||
                _journal.Exists(ThreadJournalLocation.Deleting, intent.ThreadId))
            {
                continue;
            }

            try
            {
                await FinishDeletionAsync(intent, cancellationToken);
            }
            catch
            {
                failures.Add(intent.ThreadId);
            }
        }

        return failures.Order().ToArray();
    }

    private async Task MarkRecoveryRequiredAsync(
        Guid threadId,
        string diagnostic,
        CancellationToken cancellationToken)
    {
        var snapshot = await _projection.ReadThreadSnapshotAsync(
            threadId,
            cancellationToken);
        if (snapshot is not null)
        {
            MarkRecoveryRequired(snapshot, diagnostic);
        }
    }

    private async Task<bool> RecoverJournalAsync(
        Guid threadId,
        ThreadJournalLocation location,
        CancellationToken cancellationToken)
    {
        var replay = await _journal.ReplayAsync(
            location,
            threadId,
            cancellationToken);
        if (replay.Health == ThreadJournalHealth.RecoveryRequired ||
            replay.Entries.Count == 0)
        {
            return false;
        }

        var last = replay.Entries[^1];
        if (last.EntryType == SessionEventType.ThreadDeletionRequested)
        {
            var fact = last.Payload.Deserialize<ThreadDeletionRequestedFact>(
                JsonOptions)
                ?? throw new InvalidDataException(
                    "Thread deletion fact is invalid.");
            var intent = DeletionIntent(last, fact);
            await _journal.WriteDeletionRecoveryIntentAsync(
                intent,
                cancellationToken);
            if (location != ThreadJournalLocation.Deleting)
            {
                await _journal.MoveAsync(
                    location,
                    ThreadJournalLocation.Deleting,
                    threadId,
                    cancellationToken);
            }

            await _projection.CatchUpAsync(
                replay.Entries,
                cancellationToken);
            await FinishDeletionAsync(intent, cancellationToken);
            return true;
        }

        var expectedLocation = last.EntryType switch
        {
            SessionEventType.ThreadArchived =>
                ThreadJournalLocation.Archived,
            SessionEventType.ThreadUnarchived =>
                ThreadJournalLocation.Active,
            _ => location,
        };
        if (location != expectedLocation)
        {
            await _journal.MoveAsync(
                location,
                expectedLocation,
                threadId,
                cancellationToken);
        }

        await _projection.CatchUpAsync(
            replay.Entries,
            cancellationToken);
        var snapshot = await _projection.ReadThreadSnapshotAsync(
            threadId,
            cancellationToken);
        if (snapshot is not null)
        {
            _snapshots[threadId] = snapshot;
        }

        return true;
    }

    private async Task<SessionCommandResult<ThreadSnapshot>> RelocateThreadAsync(
        ThreadMutationRequest request,
        SessionEventType eventType,
        ThreadJournalLocation source,
        ThreadJournalLocation destination,
        ThreadStatus nextStatus,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireId(request.ThreadId, nameof(request.ThreadId), "Thread ID");
        RequireId(request.IdempotencyKey, nameof(request.IdempotencyKey), "Idempotency key");
        ArgumentOutOfRangeException.ThrowIfNegative(request.ExpectedSequence);
        var operation = Wire(eventType);
        var requestSha256 = RequestHash(
            operation,
            new
            {
                ThreadId = Wire(request.ThreadId),
                IdempotencyKey = Wire(request.IdempotencyKey),
                request.ExpectedSequence,
            });
        var keyGate = GetIdempotencyGate(request.IdempotencyKey);
        await keyGate.WaitAsync(cancellationToken);
        try
        {
            var journalMatch = await _journal.FindByIdempotencyKeyAsync(
                request.IdempotencyKey,
                cancellationToken);
            if (journalMatch is not null)
            {
                return await ResumeRelocationAsync(
                    request,
                    eventType,
                    destination,
                    operation,
                    requestSha256,
                    journalMatch,
                    cancellationToken);
            }

            var replayed = await TryReplayThreadCommandAsync(
                request.IdempotencyKey,
                operation,
                requestSha256,
                cancellationToken);
            if (replayed is not null)
            {
                return replayed;
            }

            if (!CanAcceptNewWork)
            {
                return NewWorkUnavailable<ThreadSnapshot>();
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

                if (thread.CurrentSequence != request.ExpectedSequence)
                {
                    return Rejected<ThreadSnapshot>(
                        SessionErrorCodes.SequenceConflict,
                        "Thread sequence does not match.",
                        thread.CurrentSequence);
                }

                var currentStatus = eventType == SessionEventType.ThreadArchived
                    ? thread.Status is ThreadStatus.Active or ThreadStatus.Paused
                    : thread.Status == ThreadStatus.Archived;
                if (!currentStatus ||
                    thread.Availability != ThreadAvailability.Available ||
                    thread.ActiveTurnId is not null ||
                    thread.Queue.Count != 0)
                {
                    return Rejected<ThreadSnapshot>(
                        SessionErrorCodes.InvalidState,
                        "Thread must be idle with an empty queue for this operation.",
                        thread.CurrentSequence);
                }

                var timestamp = _timeProvider.GetUtcNow();
                var nextThread = CopySnapshot(
                    thread,
                    status: nextStatus,
                    currentSequence: thread.CurrentSequence + 1,
                    updatedAt: timestamp);
                ThreadJournalEntry entry;
                try
                {
                    entry = await _journal.AppendAsync(
                        source,
                        new ThreadJournalDraft(
                            request.ThreadId,
                            nextThread.CurrentSequence,
                            Guid.CreateVersion7(),
                            timestamp,
                            eventType,
                            request.IdempotencyKey,
                            new ThreadStateFact(requestSha256)),
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

                _recoveryFaultInjector?.Invoke(
                    SessionRecoveryFaultPoint.AfterFactFlushed);
                try
                {
                    await _journal.MoveAsync(
                        source,
                        destination,
                        request.ThreadId,
                        CancellationToken.None);
                }
                catch (Exception exception)
                {
                    return PendingRelocation(
                        entry,
                        operation,
                        requestSha256,
                        nextThread,
                        exception.Message);
                }

                _recoveryFaultInjector?.Invoke(
                    SessionRecoveryFaultPoint.AfterJournalMoved);
                var result = await CompleteCommitAsync(
                    entry,
                    operation,
                    requestSha256,
                    nextThread);
                _recoveryFaultInjector?.Invoke(
                    SessionRecoveryFaultPoint.AfterProjectionApplied);
                return result;
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

    private SessionError? ConsumeDeleteToken(
        DeleteThreadRequest request,
        ThreadSnapshot thread)
    {
        if (!_deletePreparations.TryGetValue(request.ThreadId, out var prepared))
        {
            return new SessionError(
                SessionErrorCodes.DeleteTokenInvalid,
                "Delete token is invalid or has already been consumed.",
                IsRetryable: false);
        }

        if (prepared.ExpiresAt <= _timeProvider.GetUtcNow())
        {
            _deletePreparations.TryRemove(request.ThreadId, out _);
            return new SessionError(
                SessionErrorCodes.DeleteTokenExpired,
                "Delete token has expired.",
                IsRetryable: false);
        }

        var provided = SHA256.HashData(Encoding.UTF8.GetBytes(request.Token));
        if (prepared.Sequence != request.ExpectedSequence ||
            prepared.Sequence != thread.CurrentSequence ||
            !CryptographicOperations.FixedTimeEquals(
                provided,
                prepared.TokenSha256))
        {
            return new SessionError(
                SessionErrorCodes.DeleteTokenInvalid,
                "Delete token does not match this thread and sequence.",
                IsRetryable: false);
        }

        _deletePreparations.TryRemove(request.ThreadId, out _);
        return null;
    }

    private async Task<SessionCommandResult<bool>> ResumeCommittedDeletionAsync(
        DeleteThreadRequest request,
        string requestSha256,
        ThreadJournalMatch match,
        CancellationToken cancellationToken)
    {
        if (match.Entry.EntryType != SessionEventType.ThreadDeletionRequested)
        {
            return Rejected<bool>(
                SessionErrorCodes.IdempotencyConflict,
                "Idempotency key is bound to another request.");
        }

        var fact = ReadFact<ThreadDeletionRequestedFact>(match.Entry);
        if (match.Entry.ThreadId != request.ThreadId ||
            !string.Equals(
                fact.RequestSha256,
                requestSha256,
                StringComparison.Ordinal))
        {
            return Rejected<bool>(
                SessionErrorCodes.IdempotencyConflict,
                "Idempotency key is bound to another deletion request.");
        }

        var intent = DeletionIntent(match.Entry, fact);
        try
        {
            await _journal.WriteDeletionRecoveryIntentAsync(
                intent,
                cancellationToken);
            if (match.Location != ThreadJournalLocation.Deleting)
            {
                await _journal.MoveAsync(
                    match.Location,
                    ThreadJournalLocation.Deleting,
                    intent.ThreadId,
                    cancellationToken);
            }

            await _projection.CatchUpAsync(
                match.Replay.Entries,
                cancellationToken);
            await FinishDeletionAsync(intent, cancellationToken);
            return new SessionCommandResult<bool>(
                SessionCommandStatus.Committed,
                true,
                intent.Sequence,
                null,
                null);
        }
        catch (Exception exception)
        {
            return PendingDelete(intent.Sequence, exception.Message);
        }
    }

    private async Task<SessionCommandResult<ThreadSnapshot>> ResumeRelocationAsync(
        ThreadMutationRequest request,
        SessionEventType eventType,
        ThreadJournalLocation destination,
        string operation,
        string requestSha256,
        ThreadJournalMatch match,
        CancellationToken cancellationToken)
    {
        var fact = match.Entry.Payload.Deserialize<ThreadStateFact>(JsonOptions);
        if (match.Entry.ThreadId != request.ThreadId ||
            match.Entry.EntryType != eventType ||
            fact is null ||
            !string.Equals(
                fact.RequestSha256,
                requestSha256,
                StringComparison.Ordinal))
        {
            return Rejected<ThreadSnapshot>(
                SessionErrorCodes.IdempotencyConflict,
                "Idempotency key is bound to another request.");
        }

        try
        {
            if (match.Location != destination)
            {
                await _journal.MoveAsync(
                    match.Location,
                    destination,
                    request.ThreadId,
                    cancellationToken);
            }

            await _projection.CatchUpAsync(
                match.Replay.Entries,
                cancellationToken);
            var snapshot = await _projection.ReadThreadSnapshotAsync(
                request.ThreadId,
                cancellationToken)
                ?? throw new InvalidDataException(
                    "Relocated thread projection is unavailable.");
            _snapshots[request.ThreadId] = snapshot;
            var sessionEvent = new SessionEvent(
                match.Entry.ThreadId,
                match.Entry.Sequence,
                match.Entry.EntryId,
                match.Entry.Timestamp,
                match.Entry.EntryType,
                new SessionEventPayload(Thread: snapshot));
            _eventChannel.Publish(sessionEvent);
            var result = new SessionCommandResult<ThreadSnapshot>(
                SessionCommandStatus.Committed,
                snapshot,
                match.Entry.Sequence,
                null,
                null);
            _idempotency[match.Entry.IdempotencyKey] = new MemoryIdempotency(
                operation,
                requestSha256,
                result);
            return result;
        }
        catch (Exception exception)
        {
            var snapshot = await GetSnapshotAsync(
                request.ThreadId,
                CancellationToken.None);
            return snapshot is null
                ? Rejected<ThreadSnapshot>(
                    SessionErrorCodes.RecoveryRequired,
                    "Thread relocation requires recovery.")
                : PendingRelocation(
                    match.Entry,
                    operation,
                    requestSha256,
                    snapshot,
                    exception.Message);
        }
    }

    private async Task FinishDeletionAsync(
        ThreadDeletionRecoveryIntent intent,
        CancellationToken cancellationToken)
    {
        await _journal.WriteDeletionRecoveryIntentAsync(intent, cancellationToken);
        await _projection.MarkDeletingAsync(intent.ThreadId, cancellationToken);
        _recoveryFaultInjector?.Invoke(
            SessionRecoveryFaultPoint.AfterDeletionMarked);
        _journal.DeleteOwnedRecovery(intent.ThreadId);
        _recoveryFaultInjector?.Invoke(
            SessionRecoveryFaultPoint.AfterOwnedFilesDeleted);
        await _projection.DeleteThreadProjectionAsync(
            intent.ThreadId,
            cancellationToken);
        _recoveryFaultInjector?.Invoke(
            SessionRecoveryFaultPoint.AfterProjectionDeleted);
        await _journal.DeleteAsync(
            ThreadJournalLocation.Deleting,
            intent.ThreadId,
            cancellationToken);
        _recoveryFaultInjector?.Invoke(
            SessionRecoveryFaultPoint.AfterJournalDeleted);
        await _projection.WriteDeletionReceiptAsync(intent, cancellationToken);
        _recoveryFaultInjector?.Invoke(
            SessionRecoveryFaultPoint.AfterReceiptWritten);
        _journal.DeleteDeletionRecoveryIntent(intent.ThreadId);
        ForgetThread(intent.ThreadId);
    }

    private void ForgetThread(Guid threadId)
    {
        _snapshots.TryRemove(threadId, out _);
        _loadedExecutionThreads.TryRemove(threadId, out _);
        _deletePreparations.TryRemove(threadId, out _);
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

        _threadGates.TryRemove(threadId, out _);
    }

    private SessionCommandResult<ThreadSnapshot> PendingRelocation(
        ThreadJournalEntry entry,
        string operation,
        string requestSha256,
        ThreadSnapshot snapshot,
        string diagnostic)
    {
        var unavailable = new ThreadSnapshot(
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
            snapshot.ProjectionState,
            diagnostic,
            snapshot.ProviderId,
            snapshot.ModelId,
            snapshot.AgentMode);
        _snapshots[snapshot.ThreadId] = unavailable;
        var result = new SessionCommandResult<ThreadSnapshot>(
            SessionCommandStatus.CommittedPendingProjection,
            unavailable,
            entry.Sequence,
            null,
            new SessionError(
                SessionErrorCodes.RecoveryRequired,
                "Thread lifecycle change was committed and requires recovery.",
                IsRetryable: true));
        _idempotency[entry.IdempotencyKey] = new MemoryIdempotency(
            operation,
            requestSha256,
            result);
        return result;
    }

    private static SessionCommandResult<bool> PendingDelete(
        long sequence,
        string diagnostic) =>
        new(
            SessionCommandStatus.CommittedPendingProjection,
            true,
            sequence,
            null,
            new SessionError(
                SessionErrorCodes.RecoveryRequired,
                $"Thread deletion was committed and requires recovery: {diagnostic}",
                IsRetryable: true));

    private static bool CanManageThread(
        ThreadSnapshot thread,
        ThreadStatus status) =>
        thread.Status == status &&
        thread.Availability == ThreadAvailability.Available &&
        thread.ActiveTurnId is null &&
        thread.Queue.Count == 0;

    private static bool IsStableBoundary(
        IReadOnlyList<ThreadJournalEntry> entries,
        long sequence) =>
        entries.Count > 0 &&
        entries[^1].Sequence == sequence &&
        (sequence == 1 &&
         entries[^1].EntryType is (
             SessionEventType.ThreadCreated or
             SessionEventType.ThreadForked) ||
         entries[^1].EntryType is (
             SessionEventType.TurnCompleted or
             SessionEventType.TurnFailed or
             SessionEventType.TurnCancelled));

    private static HistoryCheckpointFact BuildHistoryCheckpoint(
        IReadOnlyList<ThreadJournalEntry> entries,
        Guid targetThreadId,
        bool remapIds)
    {
        var turns = new Dictionary<Guid, TurnSnapshot>();
        var items = new Dictionary<Guid, SessionItemSnapshot>();
        var toolInvocations = new Dictionary<Guid, ToolInvocationRecord>();
        var checkpointEntry = entries.LastOrDefault(entry =>
            entry.EntryType is (
                SessionEventType.ThreadForked or
                SessionEventType.ThreadRolledBack));
        var checkpointSequence = checkpointEntry?.Sequence ?? 0;
        if (checkpointEntry is not null)
        {
            var checkpoint = checkpointEntry.EntryType == SessionEventType.ThreadForked
                ? ReadFact<ThreadForkedFact>(checkpointEntry).History
                : ReadFact<ThreadRolledBackFact>(checkpointEntry).History;
            foreach (var turn in checkpoint.Turns)
            {
                turns[turn.TurnId] = turn;
            }

            foreach (var item in checkpoint.Items)
            {
                items[item.ItemId] = new SessionItemSnapshot(
                    item.ItemId,
                    item.TurnId,
                    item.ItemType,
                    item.Status,
                    DeserializeContent(item.ItemType, item.Content),
                    item.Sequence,
                    item.CreatedAt,
                    item.UpdatedAt);
            }

            foreach (var invocation in checkpoint.ToolInvocations ?? [])
            {
                toolInvocations[invocation.Snapshot.ToolInvocationId] =
                    new ToolInvocationRecord(
                        invocation.Snapshot,
                        invocation.ToolCallItemId,
                        invocation.CallIndex,
                        invocation.Snapshot.ResultItemId is { } resultItemId
                            ? items.GetValueOrDefault(resultItemId)?.Sequence ?? 0
                            : 0);
            }
        }

        foreach (var entry in entries.Where(entry => entry.Sequence > checkpointSequence))
        {
            switch (entry.EntryType)
            {
                case SessionEventType.ToolInvocationStarted:
                    var started = ReadFact<ToolInvocationStartedFact>(entry);
                    toolInvocations[started.ToolInvocationId] =
                        new ToolInvocationRecord(
                            new ToolInvocationSnapshot(
                                started.ToolInvocationId,
                                entry.ThreadId,
                                started.TurnId,
                                started.ProviderToolCallId,
                                started.ProviderToolName,
                                started.ToolDefinitionId,
                                started.RuntimeBindingId,
                                started.SnapshotSha256,
                                started.ArgumentsSha256,
                                ToolInvocationStatus.Started,
                                AttemptCount: 0,
                                ResultItemId: null,
                                ErrorCode: null,
                                entry.Timestamp,
                                entry.Timestamp,
                                CompletedAt: null),
                            started.ToolCallItemId,
                            started.CallIndex,
                            entry.Sequence);
                    break;
                case SessionEventType.ToolInvocationAttemptStarted:
                    var attempt = ReadFact<ToolInvocationAttemptStartedFact>(entry);
                    if (toolInvocations.TryGetValue(
                            attempt.ToolInvocationId,
                            out var attemptRecord))
                    {
                        toolInvocations[attempt.ToolInvocationId] =
                            attemptRecord with
                            {
                                Snapshot = attemptRecord.Snapshot with
                                {
                                    AttemptCount = attempt.AttemptNumber,
                                    UpdatedAt = entry.Timestamp,
                                },
                                Sequence = entry.Sequence,
                            };
                    }

                    break;
                case SessionEventType.ToolInvocationTerminal:
                    var terminal =
                        ReadFact<ToolInvocationTerminalJournalFact>(entry);
                    if (toolInvocations.TryGetValue(
                            terminal.Invocation.ToolInvocationId,
                            out var terminalRecord))
                    {
                        toolInvocations[terminal.Invocation.ToolInvocationId] =
                            terminalRecord with
                            {
                                Snapshot = terminalRecord.Snapshot with
                                {
                                    Status = terminal.Invocation.Status,
                                    ResultItemId = terminal.ResultItem.ItemId,
                                    ErrorCode = terminal.Invocation.ErrorCode,
                                    UpdatedAt = entry.Timestamp,
                                    CompletedAt = entry.Timestamp,
                                },
                                Sequence = entry.Sequence,
                            };
                    }

                    break;
            }
        }

        foreach (var sessionEvent in BuildHistoryEvents(entries)
                     .Where(sessionEvent =>
                         sessionEvent.Sequence > checkpointSequence))
        {
            if (sessionEvent.Payload.Turn is { } turn)
            {
                turns[turn.TurnId] = turn;
            }

            if (sessionEvent.Payload.Item is { } item)
            {
                items[item.ItemId] = item;
            }
        }

        if (turns.Values.Any(turn =>
                turn.Status is not (
                    TurnStatus.Completed or
                    TurnStatus.Failed or
                    TurnStatus.Cancelled)))
        {
            throw new SessionStateException(
                SessionErrorCodes.InvalidState,
                "History checkpoint contains a non-terminal turn.");
        }

        var turnMap = turns.Keys.ToDictionary(
            turnId => turnId,
            turnId => remapIds ? Guid.CreateVersion7() : turnId);
        var targetTurns = turns.Values
            .OrderBy(turn => turn.CreatedAt)
            .ThenBy(turn => turn.TurnId)
            .Select(turn => turn with
            {
                TurnId = turnMap[turn.TurnId],
                ThreadId = targetThreadId,
            })
            .ToArray();
        var completedInvocations = toolInvocations.Values
            .Where(invocation =>
                invocation.Snapshot.CompletedAt is not null &&
                invocation.Snapshot.ResultItemId is not null)
            .ToArray();
        var invocationMap = completedInvocations.ToDictionary(
            invocation => invocation.Snapshot.ToolInvocationId,
            invocation => remapIds
                ? Guid.CreateVersion7()
                : invocation.Snapshot.ToolInvocationId);
        var includedItems = items.Values
            .Where(item => turnMap.ContainsKey(item.TurnId))
            .Where(item =>
                item.Content is not ToolResultItemContent result ||
                invocationMap.ContainsKey(result.Result.ToolInvocationId))
            .ToArray();
        var itemMap = includedItems.ToDictionary(
            item => item.ItemId,
            item => remapIds ? Guid.CreateVersion7() : item.ItemId);
        var targetItems = includedItems
            .OrderBy(item => item.Sequence)
            .ThenBy(item => item.ItemId)
            .Select(item => new HistoryCheckpointItemFact(
                itemMap[item.ItemId],
                turnMap[item.TurnId],
                item.Type,
                item.Status,
                SerializeContent(RemapToolResult(item.Content, invocationMap)),
                ContentText(item.Content),
                item.Sequence,
                item.CreatedAt,
                item.UpdatedAt))
            .ToArray();
        var targetInvocations = completedInvocations
            .Where(invocation =>
                turnMap.ContainsKey(invocation.Snapshot.TurnId) &&
                itemMap.ContainsKey(invocation.ToolCallItemId) &&
                invocation.Snapshot.ResultItemId is { } resultItemId &&
                itemMap.ContainsKey(resultItemId))
            .OrderBy(invocation => invocation.Snapshot.StartedAt)
            .ThenBy(invocation => invocation.Snapshot.ToolInvocationId)
            .Select(invocation =>
            {
                var snapshot = invocation.Snapshot;
                return new HistoryCheckpointToolInvocationFact(
                    snapshot with
                    {
                        ToolInvocationId =
                            invocationMap[snapshot.ToolInvocationId],
                        ThreadId = targetThreadId,
                        TurnId = turnMap[snapshot.TurnId],
                        ResultItemId = itemMap[snapshot.ResultItemId!.Value],
                    },
                    itemMap[invocation.ToolCallItemId],
                    invocation.CallIndex);
            })
            .ToArray();
        return new HistoryCheckpointFact(
            targetTurns,
            targetItems,
            targetInvocations);
    }

    private static SessionItemContent RemapToolResult(
        SessionItemContent content,
        IReadOnlyDictionary<Guid, Guid> invocationMap)
    {
        if (content is not ToolResultItemContent toolResult ||
            !invocationMap.TryGetValue(
                toolResult.Result.ToolInvocationId,
                out var invocationId))
        {
            return content;
        }

        var result = toolResult.Result;
        return new ToolResultItemContent(
            new ToolResultSnapshot(
                invocationId,
                result.ProviderToolCallId,
                result.Status,
                result.Output,
                result.Error,
                result.IsTruncated,
                result.OriginalByteCount,
                result.ResultSha256,
                result.AttemptCount));
    }

    private static ThreadDeletionRecoveryIntent DeletionIntent(
        ThreadJournalEntry entry,
        ThreadDeletionRequestedFact fact)
    {
        if (!string.Equals(
                fact.ThreadIdSha256,
                HashId(entry.ThreadId),
                StringComparison.Ordinal) ||
            !string.Equals(
                fact.IdempotencyKeySha256,
                HashId(entry.IdempotencyKey),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Thread deletion fact identity digest is invalid.");
        }

        return new ThreadDeletionRecoveryIntent(
            entry.ThreadId,
            fact.ThreadIdSha256,
            fact.IdempotencyKeySha256,
            entry.Sequence,
            fact.RequestSha256,
            entry.Timestamp,
            entry.Timestamp + DeleteReceiptLifetime);
    }

    private static string HashId(Guid value) =>
        Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(Wire(value))))
            .ToLowerInvariant();

    private static SessionCommandResult<RollbackResult> ConvertRollback(
        SessionCommandResult<ThreadSnapshot> result) =>
        new(
            result.Status,
            result.Value is null
                ? null
                : new RollbackResult(
                    result.Value,
                    ExternalSideEffectsReverted: false),
            result.Sequence,
            result.CurrentSequence,
            result.Error);

    private static SessionCommandResult<RollbackResult> RejectedRollback(
        string code,
        string message,
        long? currentSequence = null) =>
        new(
            SessionCommandStatus.Rejected,
            null,
            null,
            currentSequence,
            new SessionError(code, message, IsRetryable: false));

    private sealed record DeletePreparationState(
        long Sequence,
        byte[] TokenSha256,
        DateTimeOffset ExpiresAt);
}
