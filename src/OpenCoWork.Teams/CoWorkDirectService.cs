using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using OpenCoWork.Abstractions;

namespace OpenCoWork.Teams;

internal enum CoWorkDispatchFaultPoint
{
    BeforeCreateThread,
    AfterCreateThread,
    BeforeSubmitTurn,
    AfterSubmitTurn,
    BeforeDeliverMessage,
    AfterDeliverMessage,
    BeforeMissionCompletion,
    AfterMissionCompletion,
    BeforeOriginDelivery,
    AfterOriginDelivery,
}

internal sealed class CoWorkDispatchCrashException(CoWorkDispatchFaultPoint faultPoint)
    : IOException($"Injected CoWork dispatch crash at {faultPoint}.")
{
}

public sealed partial class CoWorkService
{
    private readonly Channel<bool> _wakeups = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite,
        });
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);
    private readonly string _leaseOwner = Guid.NewGuid().ToString("N");
    private CancellationTokenSource? _reconcilerCancellation;
    private Task? _reconcilerTask;
    private int _acceptingLeases;

    public async Task<CoWorkResult<AgentRunSnapshot>> SpawnSubAgentAsync(
        SpawnSubAgentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_sessions is null || _workspace is null)
        {
            return await FailureAsync<AgentRunSnapshot>(
                CoWorkErrorCodes.SessionUnavailable,
                "The Session-backed CoWork runtime is unavailable.",
                cancellationToken);
        }

        if (request.TokenBudget <= 0 || string.IsNullOrWhiteSpace(request.Task))
        {
            return await FailureAsync<AgentRunSnapshot>(
                CoWorkErrorCodes.InvalidState,
                "Task and a positive Token Budget are required.",
                cancellationToken);
        }

        if (ContainsSensitiveData(request.Task))
        {
            return await FailureAsync<AgentRunSnapshot>(
                CoWorkErrorCodes.SecretDetected,
                "SubAgent task contains sensitive data.",
                cancellationToken);
        }

        if (!IsHost(request.Command.Actor) &&
            !(request.Command.Actor.Kind == CoWorkActorKind.DirectParent &&
              request.Command.Actor.ThreadId == request.ParentThreadId))
        {
            return await FailureAsync<AgentRunSnapshot>(
                CoWorkErrorCodes.PermissionDenied,
                "Only Host or the owning Direct Parent can spawn a SubAgent.",
                cancellationToken);
        }

        var parent = await _sessions.GetThreadAsync(
            request.ParentThreadId,
            cancellationToken);
        if (parent.Value is null)
        {
            return await FailureAsync<AgentRunSnapshot>(
                CoWorkErrorCodes.NotFound,
                "Parent Thread was not found.",
                cancellationToken);
        }

        var requestHash = HashRequest(request);
        try
        {
            var replay = await TryReadCommandReceiptAsync<AgentRunSnapshot>(
                request.Command.CommandId,
                requestHash,
                cancellationToken);
            if (replay is not null)
            {
                return replay;
            }

            var prepared = await PrepareSpawnAsync(
                request,
                parent.Value,
                requestHash,
                cancellationToken);
            if (prepared.Replay is not null)
            {
                return prepared.Replay;
            }

            await ReconcileAgentRunAsync(prepared.AgentRunId, cancellationToken);
            var run = await ReadAgentRunAsync(prepared.AgentRunId, cancellationToken)
                      ?? throw InvalidState("Prepared AgentRun was not found.");
            if (run.ThreadId == Guid.Empty ||
                run.Status is CoWorkAgentRunStatus.Failed or
                    CoWorkAgentRunStatus.Cancelled ||
                run.ErrorCode is not null)
            {
                return await FailureAsync<AgentRunSnapshot>(
                    run.ErrorCode ?? CoWorkErrorCodes.SessionUnavailable,
                    "SubAgent dispatch did not complete.",
                    cancellationToken);
            }

            return await StoreCommandReceiptAsync(
                request.Command,
                "spawnSubAgent",
                run.AgentRunId.ToString(),
                requestHash,
                run,
                prepared.Revision,
                cancellationToken);
        }
        catch (CoWorkDomainException exception)
        {
            return await FailureAsync<AgentRunSnapshot>(
                exception.Code,
                exception.Message,
                cancellationToken);
        }
        catch (DbException exception)
        {
            return await FailureAsync<AgentRunSnapshot>(
                CoWorkErrorCodes.Conflict,
                $"State constraint rejected the command: {exception.GetType().Name}.",
                cancellationToken);
        }
    }

    public Task<CoWorkResult<CoWorkPage<DirectSubAgentSnapshot>>>
        ListSubAgentChildrenAsync(
            SubAgentQueryRequest request,
            CancellationToken cancellationToken = default) =>
        ListDirectSubAgentsAsync(request, descendants: false, cancellationToken);

    public Task<CoWorkResult<CoWorkPage<DirectSubAgentSnapshot>>> ListSubAgentsAsync(
        SubAgentQueryRequest request,
        CancellationToken cancellationToken = default) =>
        ListDirectSubAgentsAsync(request, descendants: true, cancellationToken);

    public async Task<CoWorkResult<MailboxMessageSnapshot>> SendSubAgentMessageAsync(
        SendSubAgentMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return await FailureAsync<MailboxMessageSnapshot>(
                CoWorkErrorCodes.InvalidState,
                "Message is required.",
                cancellationToken);
        }

        if (Encoding.UTF8.GetByteCount(request.Message) >
            _config.MaximumMailboxMessageBytes)
        {
            return await FailureAsync<MailboxMessageSnapshot>(
                CoWorkErrorCodes.InvalidState,
                "Message exceeds the configured mailbox limit.",
                cancellationToken);
        }

        if (ContainsSensitiveData(request.Message))
        {
            return await FailureAsync<MailboxMessageSnapshot>(
                CoWorkErrorCodes.SecretDetected,
                "SubAgent message contains sensitive data.",
                cancellationToken);
        }

        if (!await CanManageDirectThreadAsync(
                request.Command.Actor,
                request.ChildThreadId,
                cancellationToken))
        {
            return await FailureAsync<MailboxMessageSnapshot>(
                CoWorkErrorCodes.PermissionDenied,
                "The actor does not own this Direct SubAgent.",
                cancellationToken);
        }

        var result = await ExecuteCommandAsync(
            request,
            request.Command,
            "sendSubAgentMessage",
            request.ChildThreadId.ToString(),
            async (connection, transaction, token) =>
            {
                var child = await LoadDirectSubAgentAsync(
                                connection,
                                request.ChildThreadId,
                                token)
                            ?? throw NotFound("Direct SubAgent was not found.");
                var messageId = Guid.CreateVersion7(_timeProvider.GetUtcNow());
                var now = UtcNowMilliseconds();
                var senderThreadId =
                    request.Command.Actor.ThreadId ?? child.ParentThreadId;
                await ExecuteSqlAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO mailbox_messages (
                        mailbox_message_id, mission_id, scope,
                        sender_member_id, recipient_member_id,
                        sender_thread_id, recipient_thread_id,
                        mission_task_id, artifact_id, message_kind,
                        content, content_length, status, attempt_count,
                        lease_owner, lease_expires_utc, error_code, diagnostic,
                        created_utc, delivered_utc, acknowledged_utc)
                    VALUES (
                        $id, NULL, 'direct',
                        NULL, NULL,
                        $senderThreadId, $recipientThreadId,
                        NULL, NULL, 'info',
                        $content, $length, 'pending', 0,
                        NULL, NULL, NULL, NULL,
                        $now, NULL, NULL);
                    """,
                    token,
                    ("$id", messageId),
                    ("$senderThreadId", senderThreadId),
                    ("$recipientThreadId", request.ChildThreadId),
                    ("$content", request.Message.Trim()),
                    ("$length", Encoding.UTF8.GetByteCount(request.Message.Trim())),
                    ("$now", now));
                if (child.ActiveRun is not null)
                {
                    await InsertDispatchIntentAsync(
                        connection,
                        transaction,
                        CoWorkDispatchKind.DeliverMessage,
                        "mailboxMessage",
                        messageId,
                        request.Command.CommandId,
                        now,
                        token);
                }

                return (await LoadMailboxMessageAsync(connection, messageId, token))!;
            },
            cancellationToken);
        if (result.IsSuccess)
        {
            WakeReconciler();
            await ReconcilePendingAsync(cancellationToken);
        }

        return result;
    }

    public async Task<CoWorkResult<AgentRunSnapshot>> FollowUpSubAgentAsync(
        FollowUpSubAgentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_sessions is null || string.IsNullOrWhiteSpace(request.Task))
        {
            return await FailureAsync<AgentRunSnapshot>(
                CoWorkErrorCodes.InvalidState,
                "Session runtime and follow-up task are required.",
                cancellationToken);
        }

        if (ContainsSensitiveData(request.Task))
        {
            return await FailureAsync<AgentRunSnapshot>(
                CoWorkErrorCodes.SecretDetected,
                "Follow-up task contains sensitive data.",
                cancellationToken);
        }

        if (!await CanManageDirectThreadAsync(
                request.Command.Actor,
                request.ChildThreadId,
                cancellationToken))
        {
            return await FailureAsync<AgentRunSnapshot>(
                CoWorkErrorCodes.PermissionDenied,
                "The actor does not own this Direct SubAgent.",
                cancellationToken);
        }

        var requestHash = HashRequest(request);
        try
        {
            var replay = await TryReadCommandReceiptAsync<AgentRunSnapshot>(
                request.Command.CommandId,
                requestHash,
                cancellationToken);
            if (replay is not null)
            {
                return replay;
            }

            await ObserveTerminalRunsAsync(cancellationToken);
            var prepared = await PrepareFollowUpAsync(
                request,
                requestHash,
                cancellationToken);
            if (prepared.Replay is not null)
            {
                return prepared.Replay;
            }

            await ReconcileAgentRunAsync(prepared.AgentRunId, cancellationToken);
            var run = await ReadAgentRunAsync(prepared.AgentRunId, cancellationToken)
                      ?? throw InvalidState("Prepared AgentRun was not found.");
            if (run.ThreadId == Guid.Empty ||
                run.Status is CoWorkAgentRunStatus.Failed or
                    CoWorkAgentRunStatus.Cancelled ||
                run.ErrorCode is not null)
            {
                return await FailureAsync<AgentRunSnapshot>(
                    run.ErrorCode ?? CoWorkErrorCodes.SessionUnavailable,
                    "SubAgent follow-up dispatch did not complete.",
                    cancellationToken);
            }

            return await StoreCommandReceiptAsync(
                request.Command,
                "followUpSubAgent",
                request.ChildThreadId.ToString(),
                requestHash,
                run,
                prepared.Revision,
                cancellationToken);
        }
        catch (CoWorkDomainException exception)
        {
            return await FailureAsync<AgentRunSnapshot>(
                exception.Code,
                exception.Message,
                cancellationToken);
        }
        catch (DbException exception)
        {
            return await FailureAsync<AgentRunSnapshot>(
                CoWorkErrorCodes.Conflict,
                $"State constraint rejected the command: {exception.GetType().Name}.",
                cancellationToken);
        }
    }

    public async Task<CoWorkResult<DirectSubAgentSnapshot>> CancelSubAgentAsync(
        CancelSubAgentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await CanManageDirectThreadAsync(
                request.Command.Actor,
                request.ChildThreadId,
                cancellationToken))
        {
            return await FailureAsync<DirectSubAgentSnapshot>(
                CoWorkErrorCodes.PermissionDenied,
                "The actor does not own this Direct SubAgent.",
                cancellationToken);
        }

        Guid[] threads = [];
        var result = await ExecuteCommandAsync(
            request,
            request.Command,
            "cancelSubAgent",
            request.ChildThreadId.ToString(),
            async (connection, transaction, token) =>
            {
                threads = await LoadDescendantThreadIdsAsync(
                    connection,
                    request.ChildThreadId,
                    includeRoot: true,
                    token);
                if (threads.Length == 0)
                {
                    throw NotFound("Direct SubAgent was not found.");
                }

                var now = UtcNowMilliseconds();
                foreach (var threadId in threads)
                {
                    await ReleaseRunReservationsAsync(
                        connection,
                        transaction,
                        threadId,
                        now,
                        token);
                }

                var snapshot = await LoadDirectSubAgentAsync(
                                   connection,
                                   request.ChildThreadId,
                                   token)
                               ?? throw NotFound("Direct SubAgent was not found.");
                return snapshot with { ActiveRun = null };
            },
            cancellationToken);
        if (result.IsSuccess && _sessions is not null)
        {
            foreach (var threadId in threads)
            {
                var thread = await _sessions.GetThreadAsync(threadId, cancellationToken);
                if (thread.Value?.ActiveTurnId is { } turnId)
                {
                    _ = await _sessions.CancelTurnAsync(
                        new CancelTurnRequest(
                            threadId,
                            turnId,
                            Guid.CreateVersion7(),
                            thread.Value.CurrentSequence),
                        cancellationToken);
                }
            }
        }

        return result;
    }

    internal Task StartReconcilerAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_reconcilerTask is not null)
        {
            return Task.CompletedTask;
        }

        Volatile.Write(ref _acceptingLeases, 1);
        _reconcilerCancellation = new CancellationTokenSource();
        _reconcilerTask = RunReconcilerAsync(_reconcilerCancellation.Token);
        WakeReconciler();
        return Task.CompletedTask;
    }

    internal async Task StopReconcilerAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _acceptingLeases, 0);
        if (_reconcilerCancellation is null || _reconcilerTask is null)
        {
            return;
        }

        _reconcilerCancellation.Cancel();
        try
        {
            await _reconcilerTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
            when (_reconcilerCancellation.IsCancellationRequested)
        {
        }

        await _store.WriteAsync(
            async (connection, transaction, token) =>
            {
                await ExecuteSqlAsync(
                    connection,
                    transaction,
                    """
                    UPDATE cowork_dispatch_intents
                    SET status = 'pending',
                        lease_owner = NULL,
                        lease_expires_utc = NULL,
                        updated_utc = $now
                    WHERE status = 'leased'
                      AND lease_owner = $owner;
                    """,
                    token,
                    ("$now", UtcNowMilliseconds()),
                    ("$owner", _leaseOwner));
                return 0;
            },
            CancellationToken.None);
        _reconcilerCancellation.Dispose();
        _reconcilerCancellation = null;
        _reconcilerTask = null;
    }

    internal async Task ReconcilePendingAsync(CancellationToken cancellationToken)
    {
        if (_sessions is null)
        {
            return;
        }

        await RecoverArtifactsOnceAsync(cancellationToken);
        await _reconcileGate.WaitAsync(cancellationToken);
        try
        {
            for (var cycle = 0; cycle < 1_024; cycle++)
            {
                await ObserveTerminalRunsAsync(cancellationToken);
                await MaintainProjectWriterLeasesAsync(cancellationToken);
                var prepared = await PrepareMissionRunsAsync(cancellationToken);
                var dispatched = 0;
                for (var count = 0; count < 1_024; count++)
                {
                    var intent = await ClaimIntentAsync(
                        agentRunId: null,
                        cancellationToken);
                    if (intent is null)
                    {
                        break;
                    }

                    if (!await ExecuteIntentAsync(intent, cancellationToken))
                    {
                        break;
                    }

                    dispatched++;
                }

                await ObserveTerminalRunsAsync(cancellationToken);
                if (prepared == 0 && dispatched == 0)
                {
                    break;
                }
            }
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    private async Task RunReconcilerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var iteration = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                var wake = _wakeups.Reader.WaitToReadAsync(iteration.Token).AsTask();
                var tick = Task.Delay(
                    TimeSpan.FromSeconds(1),
                    _timeProvider,
                    iteration.Token);
                await Task.WhenAny(wake, tick);
                await iteration.CancelAsync();
                try
                {
                    await Task.WhenAll(wake, tick);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                }

                while (_wakeups.Reader.TryRead(out _))
                {
                }

                await ReconcilePendingAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private void WakeReconciler() => _wakeups.Writer.TryWrite(true);

    private async Task<PreparedDirectRun> PrepareSpawnAsync(
        SpawnSubAgentRequest request,
        ThreadSnapshot parent,
        string requestHash,
        CancellationToken cancellationToken) =>
        await _store.WriteAsync(
            async (connection, transaction, token) =>
            {
                var receipt = await ReadCommandReceiptAsync<AgentRunSnapshot>(
                    connection,
                    transaction,
                    request.Command.CommandId,
                    requestHash,
                    token);
                if (receipt is not null)
                {
                    return new PreparedDirectRun(
                        receipt.Value!.AgentRunId,
                        receipt.CoWorkRevision,
                        receipt);
                }

                var existing = await FindPreparedCommandRunAsync(
                    connection,
                    transaction,
                    request.Command.CommandId,
                    requestHash,
                    token);
                if (existing is not null)
                {
                    return new PreparedDirectRun(
                        existing.Value,
                        await ReadRevisionAsync(connection, transaction, token),
                        Replay: null);
                }

                await RequireExpectedGlobalRevisionAsync(
                    connection,
                    transaction,
                    request.Command.ExpectedRevision,
                    token);
                var profile = await LoadProfileAsync(connection, request.ProfileId, token)
                              ?? throw NotFound("Agent Profile was not found.");
                if (!profile.Enabled)
                {
                    throw InvalidState("Agent Profile is disabled.");
                }

                var parentRun = await LoadLatestRunForThreadAsync(
                    connection,
                    parent.ThreadId,
                    token);
                var depth = parentRun is null
                    ? 1
                    : await ReadRunDepthAsync(
                        connection,
                        transaction,
                        parentRun.AgentRunId,
                        token) + 1;
                if (depth > _config.MaxDepth)
                {
                    throw new CoWorkDomainException(
                        CoWorkErrorCodes.DepthExceeded,
                        "Direct SubAgent depth exceeds the configured limit.");
                }

                await RequireGlobalCapacityAsync(connection, transaction, token);
                var runId = Guid.CreateVersion7(_timeProvider.GetUtcNow());
                var now = UtcNowMilliseconds();
                Guid scopeId;
                long scopeLimit;
                if (parentRun is null)
                {
                    scopeId = Guid.CreateVersion7(_timeProvider.GetUtcNow());
                    scopeLimit = request.TokenBudget;
                    await ExecuteSqlAsync(
                        connection,
                        transaction,
                        """
                        INSERT INTO cowork_budget_scopes (
                            scope_id, owner_kind, owner_id, limit_tokens,
                            reserved_tokens, used_tokens, revision)
                        VALUES ($scopeId, 'agentRun', $runId, $limit, 0, 0, 1);
                        """,
                        token,
                        ("$scopeId", scopeId),
                        ("$runId", runId),
                        ("$limit", scopeLimit));
                }
                else
                {
                    var scope = await LoadRootBudgetAsync(
                                    connection,
                                    parentRun.AgentRunId,
                                    token)
                                ?? throw InvalidState(
                                    "Direct SubAgent root Budget Scope is missing.");
                    scopeId = scope.BudgetScopeId;
                    scopeLimit = scope.TokenLimit;
                }

                var reservation = EstimateReservation(request.Task);
                await ReserveBudgetAsync(
                    connection,
                    transaction,
                    scopeId,
                    reservation,
                    token);
                var workspace = CreateDirectWorkspace(
                    request.WorkspaceMode,
                    parent.ExecutionWorkspace!,
                    runId);
                await InsertAgentRunAsync(
                    connection,
                    transaction,
                    runId,
                    threadId: null,
                    parentRun?.AgentRunId,
                    parent.ThreadId,
                    attempt: 1,
                    profile,
                    workspace,
                    InferWorkspaceAccess(profile),
                    scopeLimit,
                    reservation,
                    now,
                    token);
                await InsertDirectInputAsync(
                    connection,
                    transaction,
                    Guid.CreateVersion7(_timeProvider.GetUtcNow()),
                    parent.ThreadId,
                    recipientThreadId: null,
                    request.Task.Trim(),
                    now,
                    token);
                await InsertDispatchIntentAsync(
                    connection,
                    transaction,
                    CoWorkDispatchKind.CreateThread,
                    "agentRun",
                    runId,
                    request.Command.CommandId,
                    now,
                    token,
                    requestHash);
                var revision = await IncrementGlobalRevisionAsync(
                    connection,
                    transaction,
                    token);
                WakeReconciler();
                return new PreparedDirectRun(runId, revision, Replay: null);
            },
            cancellationToken);

    private async Task<PreparedDirectRun> PrepareFollowUpAsync(
        FollowUpSubAgentRequest request,
        string requestHash,
        CancellationToken cancellationToken) =>
        await _store.WriteAsync(
            async (connection, transaction, token) =>
            {
                var receipt = await ReadCommandReceiptAsync<AgentRunSnapshot>(
                    connection,
                    transaction,
                    request.Command.CommandId,
                    requestHash,
                    token);
                if (receipt is not null)
                {
                    return new PreparedDirectRun(
                        receipt.Value!.AgentRunId,
                        receipt.CoWorkRevision,
                        receipt);
                }

                var existing = await FindPreparedCommandRunAsync(
                    connection,
                    transaction,
                    request.Command.CommandId,
                    requestHash,
                    token);
                if (existing is not null)
                {
                    return new PreparedDirectRun(
                        existing.Value,
                        await ReadRevisionAsync(connection, transaction, token),
                        Replay: null);
                }

                await RequireExpectedGlobalRevisionAsync(
                    connection,
                    transaction,
                    request.Command.ExpectedRevision,
                    token);
                var first = await LoadFirstRunForThreadAsync(
                                connection,
                                request.ChildThreadId,
                                token)
                            ?? throw NotFound("Direct SubAgent was not found.");
                if (await HasActiveRunAsync(
                        connection,
                        transaction,
                        request.ChildThreadId,
                        token))
                {
                    throw Conflict("Direct SubAgent already has an active AgentRun.");
                }

                await RequireGlobalCapacityAsync(connection, transaction, token);
                var budget = await LoadRootBudgetAsync(
                                 connection,
                                 first.AgentRunId,
                                 token)
                             ?? throw InvalidState(
                                 "Direct SubAgent root Budget Scope is missing.");
                var reservation = EstimateReservation(request.Task);
                await ReserveBudgetAsync(
                    connection,
                    transaction,
                    budget.BudgetScopeId,
                    reservation,
                    token);
                var runId = Guid.CreateVersion7(_timeProvider.GetUtcNow());
                var now = UtcNowMilliseconds();
                var attempt = await ScalarAsync<long>(
                    connection,
                    transaction,
                    """
                    SELECT count(*) + 1
                    FROM agent_runs
                    WHERE run_kind = 'direct'
                      AND thread_id = $threadId;
                    """,
                    token,
                    ("$threadId", request.ChildThreadId));
                await InsertAgentRunAsync(
                    connection,
                    transaction,
                    runId,
                    request.ChildThreadId,
                    first.AgentRunId,
                    first.ParentThreadId,
                    checked((int)attempt),
                    first.Profile,
                    first.ExecutionWorkspace,
                    first.WorkspaceAccess,
                    budget.TokenLimit,
                    reservation,
                    now,
                    token);
                await InsertDirectInputAsync(
                    connection,
                    transaction,
                    Guid.CreateVersion7(_timeProvider.GetUtcNow()),
                    request.Command.Actor.ThreadId ?? first.ParentThreadId ??
                    request.ChildThreadId,
                    request.ChildThreadId,
                    request.Task.Trim(),
                    now,
                    token);
                await InsertDispatchIntentAsync(
                    connection,
                    transaction,
                    CoWorkDispatchKind.SubmitTurn,
                    "agentRun",
                    runId,
                    request.Command.CommandId,
                    now,
                    token,
                    requestHash);
                var revision = await IncrementGlobalRevisionAsync(
                    connection,
                    transaction,
                    token);
                WakeReconciler();
                return new PreparedDirectRun(runId, revision, Replay: null);
            },
            cancellationToken);

    private async Task ReconcileAgentRunAsync(
        Guid agentRunId,
        CancellationToken cancellationToken)
    {
        await _reconcileGate.WaitAsync(cancellationToken);
        try
        {
            for (var count = 0; count < 8; count++)
            {
                var intent = await ClaimIntentAsync(agentRunId, cancellationToken);
                if (intent is null)
                {
                    break;
                }

                if (!await ExecuteIntentAsync(intent, cancellationToken))
                {
                    break;
                }
            }

            await ObserveTerminalRunsAsync(cancellationToken);
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    private async Task<DispatchIntentSnapshot?> ClaimIntentAsync(
        Guid? agentRunId,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _acceptingLeases) == 0 && _reconcilerTask is not null)
        {
            return null;
        }

        return await _store.WriteAsync(
            async (connection, transaction, token) =>
            {
                var now = UtcNowMilliseconds();
                Guid? id = null;
                await using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText =
                        """
                        SELECT intent_id
                        FROM cowork_dispatch_intents
                        WHERE (status = 'pending' OR
                               (status = 'leased' AND lease_expires_utc <= $now))
                          AND attempt_count < $maximumAttempts
                          AND ($entityId IS NULL OR entity_id = $entityId)
                        ORDER BY created_utc, intent_id
                        LIMIT 1;
                        """;
                    AddParameter(command, "$now", now);
                    AddParameter(command, "$maximumAttempts", _config.MaximumDispatchAttempts);
                    AddParameter(command, "$entityId", agentRunId);
                    id = Guid.TryParse(
                        Convert.ToString(await command.ExecuteScalarAsync(token)),
                        out var parsed)
                        ? parsed
                        : null;
                }

                if (id is null)
                {
                    return null;
                }

                await ExecuteSqlAsync(
                    connection,
                    transaction,
                    """
                    UPDATE cowork_dispatch_intents
                    SET status = 'leased',
                        attempt_count = attempt_count + 1,
                        lease_owner = $owner,
                        lease_expires_utc = $leaseExpires,
                        updated_utc = $now
                    WHERE intent_id = $id;
                    """,
                    token,
                    ("$owner", _leaseOwner),
                    ("$leaseExpires", checked(now + (long)_config.DispatchLease.TotalMilliseconds)),
                    ("$now", now),
                    ("$id", id.Value));
                return await LoadDispatchIntentAsync(connection, id.Value, token);
            },
            cancellationToken);
    }

    private async Task<bool> ExecuteIntentAsync(
        DispatchIntentSnapshot intent,
        CancellationToken cancellationToken)
    {
        var materialized = await MaterializeMissionIntentAsync(
            intent,
            cancellationToken);
        if (materialized is null)
        {
            return false;
        }

        intent = materialized;
        using var renewalCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var renewal = RenewLeaseAsync(
            intent.DispatchIntentId,
            renewalCancellation.Token);
        try
        {
            try
            {
                switch (intent.Kind)
                {
                    case CoWorkDispatchKind.CreateThread:
                        await ExecuteCreateThreadIntentAsync(intent, cancellationToken);
                        break;
                    case CoWorkDispatchKind.SubmitTurn:
                    case CoWorkDispatchKind.SynthesizeMission:
                        if (!await ExecuteSubmitTurnIntentAsync(
                                intent,
                                cancellationToken))
                        {
                            return false;
                        }

                        break;
                    case CoWorkDispatchKind.DeliverMessage:
                        if (!await ExecuteDeliverMessageIntentAsync(
                                intent,
                                cancellationToken))
                        {
                            return false;
                        }

                        break;
                    case CoWorkDispatchKind.DeliverOrigin:
                        if (!await ExecuteDeliverOriginIntentAsync(
                                intent,
                                cancellationToken))
                        {
                            return false;
                        }

                        break;
                    default:
                        await DeadLetterIntentAsync(
                            intent,
                            CoWorkErrorCodes.InvalidState,
                            "Dispatch kind is not implemented by this runtime slice.",
                            cancellationToken);
                        break;
                }
            }
            catch (CoWorkDispatchCrashException)
            {
                throw;
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException or TimeoutException)
            {
                await RetryOrDeadLetterIntentAsync(
                    intent,
                    exception.GetType().Name,
                    cancellationToken);
            }
            catch (Exception exception)
            {
                await FailRunAndIntentAsync(
                    intent,
                    CoWorkErrorCodes.InvalidState,
                    exception.GetType().Name,
                    cancellationToken);
            }
        }
        finally
        {
            await renewalCancellation.CancelAsync();
            try
            {
                await renewal;
            }
            catch (OperationCanceledException)
                when (renewalCancellation.IsCancellationRequested)
            {
            }
        }

        return true;
    }

    private async Task RenewLeaseAsync(
        Guid intentId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(
                _config.LeaseRenewalInterval,
                _timeProvider,
                cancellationToken);
            await _store.WriteAsync(
                async (connection, transaction, token) =>
                {
                    var now = UtcNowMilliseconds();
                    await ExecuteSqlAsync(
                        connection,
                        transaction,
                        """
                        UPDATE cowork_dispatch_intents
                        SET lease_expires_utc = $leaseExpires,
                            updated_utc = $now
                        WHERE intent_id = $intentId
                          AND status = 'leased'
                          AND lease_owner = $owner;
                        """,
                        token,
                        ("$leaseExpires", checked(
                            now + (long)_config.DispatchLease.TotalMilliseconds)),
                        ("$now", now),
                        ("$intentId", intentId),
                        ("$owner", _leaseOwner));
                    return 0;
                },
                cancellationToken);
        }
    }

    private async Task ExecuteCreateThreadIntentAsync(
        DispatchIntentSnapshot intent,
        CancellationToken cancellationToken)
    {
        var run = await ReadAgentRunAsync(intent.EntityId, cancellationToken)
                  ?? throw InvalidState("Dispatch AgentRun was not found.");
        if (run.ThreadId != Guid.Empty)
        {
            await CompleteIntentAsync(intent.DispatchIntentId, cancellationToken);
            return;
        }

        var workspace = run.ExecutionWorkspace;
        if (workspace.Mode == CoWorkWorkspaceMode.Worktree)
        {
            if (_worktrees is null)
            {
                throw InvalidState("Managed Worktree service is unavailable.");
            }

            var managed = workspace.BaseCommitSha is null
                ? await _worktrees.CreateAsync(run.AgentRunId, cancellationToken)
                : await _worktrees.CreateAsync(
                    new ManagedWorktreeCreateRequest(
                        run.AgentRunId,
                        workspace.BaseCommitSha),
                    cancellationToken);
            workspace = workspace with
            {
                WorktreeId = managed.WorktreeId,
                WorktreeRoot = managed.WorktreeRoot,
                BaseCommitSha = managed.BaseCommitSha,
            };
            await PersistWorktreeAsync(run, managed, cancellationToken);
        }

        _dispatchFaultInjector?.Invoke(CoWorkDispatchFaultPoint.BeforeCreateThread);
        var created = await _sessions!.CreateThreadAsync(
            new CreateThreadRequest(
                intent.DispatchIntentId,
                ExpectedSequence: 0,
                DisplayName: run.Profile.Name,
                ProviderId: run.Profile.ProviderId,
                ModelId: run.Profile.ModelId,
                AgentMode: run.Kind is CoWorkAgentRunKind.Direct or
                    CoWorkAgentRunKind.MissionTask
                    ? AgentMode.Agent
                    : AgentMode.Plan,
                ExecutionWorkspace: workspace,
                CoWorkProvenance: new CoWorkThreadProvenance(
                    run.AgentRunId,
                    run.Kind,
                    run.MissionId,
                    run.TaskId,
                    run.MemberId,
                    ParentAgentRunId: run.ParentRunId,
                    ParentThreadId: run.ParentThreadId)),
            cancellationToken);
        if (created.Value is null)
        {
            await FailRunAndIntentAsync(
                intent,
                created.Error?.Code ?? CoWorkErrorCodes.SessionUnavailable,
                created.Error?.Message ?? "Session Thread creation failed.",
                cancellationToken);
            return;
        }

        _dispatchFaultInjector?.Invoke(CoWorkDispatchFaultPoint.AfterCreateThread);
        await _store.WriteAsync(
            async (connection, transaction, token) =>
            {
                var now = UtcNowMilliseconds();
                await ExecuteSqlAsync(
                    connection,
                    transaction,
                    """
                    UPDATE agent_runs
                    SET thread_id = $threadId,
                        workspace_json = $workspace,
                        status = 'starting',
                        lease_owner = NULL,
                        lease_expires_utc = NULL,
                        updated_utc = $now
                    WHERE agent_run_id = $runId;

                    UPDATE mailbox_messages
                    SET recipient_thread_id = $threadId
                    WHERE scope = 'direct'
                      AND recipient_thread_id IS NULL
                      AND rowid = (
                          SELECT rowid
                          FROM mailbox_messages
                          WHERE scope = 'direct'
                            AND recipient_thread_id IS NULL
                            AND sender_thread_id = $parentThreadId
                          ORDER BY created_utc DESC, mailbox_message_id DESC
                          LIMIT 1);

                    UPDATE cowork_dispatch_intents
                    SET status = 'completed',
                        lease_owner = NULL,
                        lease_expires_utc = NULL,
                        error_code = NULL,
                        diagnostic = NULL,
                        updated_utc = $now
                    WHERE intent_id = $intentId;
                    """,
                    token,
                    ("$threadId", created.Value.ThreadId),
                    ("$workspace", JsonSerializer.Serialize(workspace, JsonOptions)),
                    ("$now", now),
                    ("$runId", run.AgentRunId),
                    ("$parentThreadId", run.ParentThreadId),
                    ("$intentId", intent.DispatchIntentId));
                if (run.Kind == CoWorkAgentRunKind.LeaderPlanning &&
                    run.MissionId is { } missionId)
                {
                    await ExecuteSqlAsync(
                        connection,
                        transaction,
                        """
                        UPDATE missions
                        SET leader_thread_id = $threadId,
                            revision = revision + 1,
                            updated_utc = $now
                        WHERE mission_id = $missionId
                          AND leader_thread_id IS NULL;
                        """,
                        token,
                        ("$threadId", created.Value.ThreadId),
                        ("$now", now),
                        ("$missionId", missionId));
                }

                await InsertDispatchIntentAsync(
                    connection,
                    transaction,
                    CoWorkDispatchKind.SubmitTurn,
                    "agentRun",
                    run.AgentRunId,
                    commandId: null,
                    now,
                    token);
                return 0;
            },
            cancellationToken);
    }

    private async Task<bool> ExecuteSubmitTurnIntentAsync(
        DispatchIntentSnapshot intent,
        CancellationToken cancellationToken)
    {
        var run = await ReadAgentRunAsync(intent.EntityId, cancellationToken)
                  ?? throw InvalidState("Dispatch AgentRun was not found.");
        if (run.ThreadId == Guid.Empty)
        {
            throw InvalidState("AgentRun Thread has not been created.");
        }

        var messages = run.Kind == CoWorkAgentRunKind.Direct
            ? await ReadPendingDirectMessagesAsync(run.ThreadId, cancellationToken)
            : [];
        if (run.Kind == CoWorkAgentRunKind.Direct && messages.Length == 0)
        {
            await FailRunAndIntentAsync(
                intent,
                CoWorkErrorCodes.InvalidState,
                "AgentRun input is missing.",
                cancellationToken);
            return true;
        }

        var thread = await _sessions!.GetThreadAsync(run.ThreadId, cancellationToken);
        if (thread.Value is null)
        {
            await FailRunAndIntentAsync(
                intent,
                thread.Error?.Code ?? CoWorkErrorCodes.SessionUnavailable,
                thread.Error?.Message ?? "AgentRun Thread is unavailable.",
                cancellationToken);
            return true;
        }

        var expectedSequence = TryReadExpectedSequence(intent.Diagnostic)
            ?? thread.Value.CurrentSequence;
        if (intent.Diagnostic is null)
        {
            await PersistIntentDiagnosticAsync(
                intent.DispatchIntentId,
                $"expectedSequence:{expectedSequence}",
                cancellationToken);
        }

        var text = run.Kind == CoWorkAgentRunKind.Direct
            ? string.Join("\n\n", messages.Select(message => message.Body))
            : await BuildMissionRunInputAsync(run, cancellationToken);
        if (!await TryAcquireProjectWriterLeaseAsync(run, cancellationToken))
        {
            await ReleaseIntentAsync(intent.DispatchIntentId, cancellationToken);
            return false;
        }

        _dispatchFaultInjector?.Invoke(CoWorkDispatchFaultPoint.BeforeSubmitTurn);
        var submitted = await _sessions.EnqueueInputAsync(
            new EnqueueInputRequest(
                run.ThreadId,
                intent.DispatchIntentId,
                expectedSequence,
                text,
                TurnAdmission.QueueIfBusy),
            cancellationToken);
        if (submitted.Value is null)
        {
            await FailRunAndIntentAsync(
                intent,
                submitted.Error?.Code ?? CoWorkErrorCodes.SessionUnavailable,
                submitted.Error?.Message ?? "AgentRun Turn submission failed.",
                cancellationToken);
            return true;
        }

        _dispatchFaultInjector?.Invoke(CoWorkDispatchFaultPoint.AfterSubmitTurn);
        await _store.WriteAsync(
            async (connection, transaction, token) =>
            {
                var now = UtcNowMilliseconds();
                foreach (var message in messages)
                {
                    await ExecuteSqlAsync(
                        connection,
                        transaction,
                        """
                        UPDATE mailbox_messages
                        SET status = 'delivered',
                            attempt_count = attempt_count + 1,
                            delivered_utc = $now
                        WHERE mailbox_message_id = $messageId;
                        """,
                        token,
                        ("$now", now),
                        ("$messageId", message.MessageId));
                }

                await ExecuteSqlAsync(
                    connection,
                    transaction,
                    """
                    UPDATE agent_runs
                    SET status = 'running',
                        diagnostic = $turn,
                        lease_owner = NULL,
                        lease_expires_utc = NULL,
                        updated_utc = $now
                    WHERE agent_run_id = $runId;

                    UPDATE cowork_dispatch_intents
                    SET status = 'completed',
                        lease_owner = NULL,
                        lease_expires_utc = NULL,
                        error_code = NULL,
                        diagnostic = NULL,
                        updated_utc = $now
                    WHERE intent_id = $intentId;
                    """,
                    token,
                    ("$turn", "turn:" +
                              (submitted.Value.TurnId ?? submitted.Value.QueueItem.QueueItemId)),
                    ("$now", now),
                    ("$runId", run.AgentRunId),
                    ("$intentId", intent.DispatchIntentId));
                return 0;
            },
            cancellationToken);
        return true;
    }

    private async Task<bool> ExecuteDeliverMessageIntentAsync(
        DispatchIntentSnapshot intent,
        CancellationToken cancellationToken)
    {
        var message = await ReadMailboxMessageAsync(intent.EntityId, cancellationToken)
                      ?? throw InvalidState("Mailbox message was not found.");
        var recipientThreadId = await ResolveMailboxRecipientThreadAsync(
            message,
            cancellationToken);
        if (recipientThreadId is null)
        {
            await ReleaseIntentAsync(intent.DispatchIntentId, cancellationToken);
            return false;
        }

        _dispatchFaultInjector?.Invoke(CoWorkDispatchFaultPoint.BeforeDeliverMessage);
        var thread = await _sessions!.GetThreadAsync(
            recipientThreadId.Value,
            cancellationToken);
        if (thread.Value is null)
        {
            throw new IOException("Mailbox recipient Session is unavailable.");
        }

        var activeTurnId = thread.Value.ActiveTurnId;
        var expectedSequence = TryReadExpectedSequence(intent.Diagnostic)
            ?? thread.Value.CurrentSequence;
        if (intent.Diagnostic is null)
        {
            await PersistIntentDiagnosticAsync(
                intent.DispatchIntentId,
                $"expectedSequence:{expectedSequence}",
                cancellationToken);
        }

        var queued = await _sessions.EnqueueInputAsync(
            new EnqueueInputRequest(
                recipientThreadId.Value,
                intent.DispatchIntentId,
                expectedSequence,
                message.Body,
                TurnAdmission.QueueIfBusy),
            cancellationToken);
        if (queued.Value is null)
        {
            await FailRunAndIntentAsync(
                intent,
                queued.Error?.Code ?? CoWorkErrorCodes.SessionUnavailable,
                queued.Error?.Message ?? "Direct message delivery failed.",
                cancellationToken);
            return true;
        }

        var current = await _sessions.GetThreadAsync(
            recipientThreadId.Value,
            cancellationToken);
        if (activeTurnId is not null &&
            current.Value?.ActiveTurnId == activeTurnId &&
            queued.Value.TurnId is null)
        {
            _ = await _sessions.SteerTurnAsync(
                new SteerTurnRequest(
                    recipientThreadId.Value,
                    activeTurnId.Value,
                    queued.Value.QueueItem.QueueItemId,
                    Guid.CreateVersion7(),
                    current.Value.CurrentSequence),
                cancellationToken);
        }

        _dispatchFaultInjector?.Invoke(CoWorkDispatchFaultPoint.AfterDeliverMessage);
        await _store.WriteAsync(
            async (connection, transaction, token) =>
            {
                var now = UtcNowMilliseconds();
                await ExecuteSqlAsync(
                    connection,
                    transaction,
                    """
                    UPDATE mailbox_messages
                    SET status = 'delivered',
                        attempt_count = $attempt,
                        delivered_utc = $now,
                        error_code = NULL,
                        diagnostic = NULL
                    WHERE mailbox_message_id = $messageId;

                    UPDATE cowork_dispatch_intents
                    SET status = 'completed',
                        lease_owner = NULL,
                        lease_expires_utc = NULL,
                        updated_utc = $now
                    WHERE intent_id = $intentId;
                    """,
                    token,
                    ("$now", now),
                    ("$attempt", intent.Attempt),
                    ("$messageId", message.MessageId),
                    ("$intentId", intent.DispatchIntentId));
                return 0;
            },
            cancellationToken);
        return true;
    }

    private async Task<Guid?> ResolveMailboxRecipientThreadAsync(
        MailboxMessageSnapshot message,
        CancellationToken cancellationToken) =>
        await _store.ReadAsync(
            async (connection, token) =>
            {
                if (message.Scope == CoWorkMailboxScope.Direct)
                {
                    return await ReadOptionalGuidAsync(
                        connection,
                        """
                        SELECT recipient_thread_id
                        FROM mailbox_messages
                        WHERE mailbox_message_id = $messageId;
                        """,
                        token,
                        ("$messageId", message.MessageId));
                }

                return await ReadOptionalGuidAsync(
                    connection,
                    """
                    SELECT coalesce(
                        CASE member.role
                            WHEN 'leader' THEN mission.leader_thread_id
                        END,
                        (
                            SELECT run.thread_id
                            FROM agent_runs run
                            WHERE run.mission_id = message.mission_id
                              AND run.member_id = message.recipient_member_id
                              AND run.thread_id IS NOT NULL
                            ORDER BY
                                CASE run.status
                                    WHEN 'running' THEN 0
                                    WHEN 'starting' THEN 1
                                    WHEN 'pending' THEN 2
                                    ELSE 3
                                END,
                                run.created_utc DESC
                            LIMIT 1
                        ))
                    FROM mailbox_messages message
                    JOIN mission_members member
                      ON member.mission_member_id = message.recipient_member_id
                    JOIN missions mission ON mission.mission_id = message.mission_id
                    WHERE message.mailbox_message_id = $messageId;
                    """,
                    token,
                    ("$messageId", message.MessageId));
            },
            cancellationToken);

    private async Task ObserveTerminalRunsAsync(CancellationToken cancellationToken)
    {
        if (_sessions is null)
        {
            return;
        }

        var runs = await _store.ReadAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT agent_run_id
                    FROM agent_runs
                    WHERE status IN ('starting', 'running')
                      AND thread_id IS NOT NULL
                    ORDER BY created_utc, agent_run_id;
                    """;
                await using var reader = await command.ExecuteReaderAsync(token);
                var ids = new List<Guid>();
                while (await reader.ReadAsync(token))
                {
                    ids.Add(Guid.Parse(reader.GetString(0)));
                }

                return ids.ToArray();
            },
            cancellationToken);
        foreach (var runId in runs)
        {
            var run = await ReadAgentRunAsync(runId, cancellationToken);
            if (run is null)
            {
                continue;
            }

            var events = new List<SessionEvent>();
            long afterSequence = 0;
            while (true)
            {
                var history = await _sessions.ReadHistoryAsync(
                    new ReadHistoryRequest(
                        run.ThreadId,
                        AfterSequence: afterSequence,
                        PageSize: 100),
                    cancellationToken);
                if (history.Value is null)
                {
                    events.Clear();
                    break;
                }

                if (history.Value.Items.Count == 0)
                {
                    break;
                }

                events.AddRange(history.Value.Items);
                if (history.Value.NextCursor is null)
                {
                    break;
                }

                afterSequence = history.Value.Items[^1].Sequence;
            }

            if (events.Count == 0)
            {
                continue;
            }

            var dispatchId = await ReadRunDispatchIdAsync(
                run.AgentRunId,
                cancellationToken);
            var started = dispatchId is null
                ? null
                : events.LastOrDefault(item =>
                    item.Type == SessionEventType.TurnStarted &&
                    (item.Payload.Turn?.TurnId == dispatchId ||
                     item.Payload.QueueItem?.QueueItemId == dispatchId));
            var terminal = started?.Payload.Turn is { } startedTurn
                ? events.LastOrDefault(item =>
                    item.Payload.Turn?.TurnId == startedTurn.TurnId &&
                    item.Type is SessionEventType.TurnCompleted or
                        SessionEventType.TurnFailed or
                        SessionEventType.TurnCancelled)
                : null;
            if (terminal is null)
            {
                continue;
            }

            var terminalTurnId = terminal.Payload.Turn?.TurnId;
            var turnStartSequence = started!.Sequence;
            var usage = events
                .Where(item =>
                    item.Type == SessionEventType.ProviderUsageRecorded &&
                    item.Sequence >= turnStartSequence &&
                    item.Sequence <= terminal.Sequence)
                .Sum(item => (long)(item.Payload.Usage?.TotalTokens ?? 0));
            var status = terminal.Type switch
            {
                SessionEventType.TurnCompleted => CoWorkAgentRunStatus.Completed,
                SessionEventType.TurnCancelled => CoWorkAgentRunStatus.Cancelled,
                _ => CoWorkAgentRunStatus.Failed,
            };
            var outputSummary = events.LastOrDefault(item =>
                    item.Type == SessionEventType.ItemCompleted &&
                    item.Sequence >= turnStartSequence &&
                    item.Sequence <= terminal.Sequence &&
                    item.Payload.Item?.Type == SessionItemType.AgentMessage)
                ?.Payload.Item?.Content is TextItemContent text
                ? _sensitiveData.Redact(text.Text)
                : status == CoWorkAgentRunStatus.Completed
                    ? "Completed without a textual summary."
                    : terminal.Payload.Error?.Message;
            await SettleAgentRunAsync(
                run,
                status,
                usage == 0 ? null : usage,
                terminal.Payload.Error?.Code,
                outputSummary,
                cancellationToken);
        }
    }

    private async Task<bool> TryAcquireProjectWriterLeaseAsync(
        AgentRunSnapshot run,
        CancellationToken cancellationToken)
    {
        if (_projectWriterLeases is null ||
            run.ExecutionWorkspace.Mode != CoWorkWorkspaceMode.Project ||
            run.WorkspaceAccess != CoWorkWorkspaceAccess.ReadWrite)
        {
            return true;
        }

        var lease = await _projectWriterLeases.TryAcquireAsync(
            new ProjectWriterLeaseOwner(
                ProjectWriterLeaseOwnerKind.CoWorkAgentRun,
                run.AgentRunId),
            cancellationToken);
        if (lease is null)
        {
            return false;
        }

        await PersistProjectWriterLeaseAsync(
            run.AgentRunId,
            lease,
            cancellationToken);
        return true;
    }

    private async Task MaintainProjectWriterLeasesAsync(
        CancellationToken cancellationToken)
    {
        if (_projectWriterLeases is null || _sessions is null)
        {
            return;
        }

        var runs = await _store.ReadAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT agent_run_id, project_writer_lease_id,
                           project_writer_lease_expires_utc
                    FROM agent_runs
                    WHERE status IN ('starting', 'running')
                      AND workspace_mode = 'project'
                      AND workspace_access = 'readWrite'
                      AND project_writer_lease_id IS NOT NULL
                    ORDER BY created_utc, agent_run_id;
                    """;
                await using var reader = await command.ExecuteReaderAsync(token);
                var values = new List<(Guid RunId, Guid LeaseId, long ExpiresUtc)>();
                while (await reader.ReadAsync(token))
                {
                    values.Add((
                        Guid.Parse(reader.GetString(0)),
                        Guid.Parse(reader.GetString(1)),
                        reader.GetInt64(2)));
                }

                return values.ToArray();
            },
            cancellationToken);
        var renewAt = ProjectWriterLeaseLimits.LeaseDuration -
                      ProjectWriterLeaseLimits.RenewalInterval;
        foreach (var item in runs)
        {
            var expiresAt =
                DateTimeOffset.FromUnixTimeMilliseconds(item.ExpiresUtc);
            if (expiresAt - _timeProvider.GetUtcNow() > renewAt)
            {
                continue;
            }

            var owner = new ProjectWriterLeaseOwner(
                ProjectWriterLeaseOwnerKind.CoWorkAgentRun,
                item.RunId);
            var renewed = await _projectWriterLeases.RenewAsync(
                owner,
                item.LeaseId,
                cancellationToken);
            if (renewed is not null)
            {
                await PersistProjectWriterLeaseAsync(
                    item.RunId,
                    renewed,
                    cancellationToken);
                continue;
            }

            var run = await ReadAgentRunAsync(item.RunId, cancellationToken);
            if (run is null)
            {
                continue;
            }

            var thread = await _sessions.GetThreadAsync(
                run.ThreadId,
                cancellationToken);
            if (thread.Value?.ActiveTurnId is { } turnId)
            {
                _ = await _sessions.CancelTurnAsync(
                    new CancelTurnRequest(
                        run.ThreadId,
                        turnId,
                        Guid.CreateVersion7(),
                        thread.Value.CurrentSequence),
                    cancellationToken);
            }

            await SettleAgentRunAsync(
                run,
                CoWorkAgentRunStatus.Failed,
                actualUsage: null,
                CoWorkErrorCodes.InvalidState,
                "Project writer lease was lost.",
                cancellationToken);
        }
    }

    private async Task PersistProjectWriterLeaseAsync(
        Guid runId,
        ProjectWriterLease lease,
        CancellationToken cancellationToken) =>
        await _store.WriteAsync(
            async (connection, transaction, token) =>
            {
                await ExecuteSqlAsync(
                    connection,
                    transaction,
                    """
                    UPDATE agent_runs
                    SET project_writer_lease_id = $leaseId,
                        project_writer_lease_expires_utc = $expiresUtc,
                        updated_utc = $now
                    WHERE agent_run_id = $runId
                      AND status IN ('starting', 'running');
                    """,
                    token,
                    ("$leaseId", lease.LeaseId),
                    ("$expiresUtc", lease.ExpiresAtUtc.ToUnixTimeMilliseconds()),
                    ("$now", UtcNowMilliseconds()),
                    ("$runId", runId));
                return 0;
            },
            cancellationToken);

    private async Task ReleaseProjectWriterLeaseAsync(
        Guid runId,
        CancellationToken cancellationToken)
    {
        if (_projectWriterLeases is null)
        {
            return;
        }

        var leaseId = await _store.ReadAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT project_writer_lease_id
                    FROM agent_runs
                    WHERE agent_run_id = $runId;
                    """;
                var parameter = command.CreateParameter();
                parameter.ParameterName = "$runId";
                parameter.Value = runId.ToString("D");
                command.Parameters.Add(parameter);
                var value = await command.ExecuteScalarAsync(token);
                return value is null or DBNull
                    ? (Guid?)null
                    : Guid.Parse((string)value);
            },
            cancellationToken);
        if (leaseId is not null)
        {
            _ = await _projectWriterLeases.ReleaseAsync(
                new ProjectWriterLeaseOwner(
                    ProjectWriterLeaseOwnerKind.CoWorkAgentRun,
                    runId),
                leaseId.Value,
                cancellationToken);
        }
    }

    private async Task<bool> ExecuteDeliverOriginIntentAsync(
        DispatchIntentSnapshot intent,
        CancellationToken cancellationToken)
    {
        var state = await _store.ReadAsync(
            async (connection, token) =>
            {
                var mission = await LoadMissionAsync(connection, intent.EntityId, token)
                              ?? throw NotFound("Mission was not found.");
                var delivered = await ScalarAsync<long>(
                    connection,
                    null,
                    """
                    SELECT count(*) FROM missions
                    WHERE mission_id = $missionId
                      AND origin_delivered_utc IS NOT NULL;
                    """,
                    token,
                    ("$missionId", mission.MissionId)) != 0;
                return (Mission: mission, Delivered: delivered);
            },
            cancellationToken);
        if (state.Delivered)
        {
            await CompleteIntentAsync(intent.DispatchIntentId, cancellationToken);
            return true;
        }

        if (state.Mission.Status != CoWorkMissionStatus.Completed ||
            string.IsNullOrWhiteSpace(state.Mission.FinalSummary) ||
            string.IsNullOrWhiteSpace(state.Mission.OriginDeliveryId))
        {
            await DeadLetterIntentAsync(
                intent,
                CoWorkErrorCodes.InvalidState,
                "Mission completion is unavailable for Origin delivery.",
                cancellationToken);
            return true;
        }

        _dispatchFaultInjector?.Invoke(CoWorkDispatchFaultPoint.BeforeOriginDelivery);
        var appended = await _sessions!.AppendCompletedAgentTurnAsync(
            new AppendCompletedAgentTurnRequest(
                state.Mission.OriginThreadId,
                state.Mission.OriginDeliveryId,
                $"Mission {state.Mission.MissionId:D} completed.\n" +
                $"Leader Thread: {state.Mission.LeaderThreadId:D}\n" +
                $"Summary:\n{state.Mission.FinalSummary}"),
            cancellationToken);
        if (appended.Value is null)
        {
            if (appended.Error?.IsRetryable == true ||
                appended.Error?.Code == SessionErrorCodes.ThreadBusy)
            {
                await ReleaseIntentAsync(intent.DispatchIntentId, cancellationToken);
                return false;
            }

            await DeadLetterIntentAsync(
                intent,
                appended.Error?.Code ?? CoWorkErrorCodes.SessionUnavailable,
                appended.Error?.Message ?? "Origin delivery failed.",
                cancellationToken);
            return true;
        }

        _dispatchFaultInjector?.Invoke(CoWorkDispatchFaultPoint.AfterOriginDelivery);
        await _store.WriteAsync(
            async (connection, transaction, token) =>
            {
                var now = UtcNowMilliseconds();
                await ExecuteSqlAsync(
                    connection,
                    transaction,
                    """
                    UPDATE missions
                    SET origin_delivered_utc = coalesce(origin_delivered_utc, $now),
                        revision = CASE
                            WHEN origin_delivered_utc IS NULL THEN revision + 1
                            ELSE revision
                        END,
                        updated_utc = $now
                    WHERE mission_id = $missionId;

                    UPDATE cowork_dispatch_intents
                    SET status = 'completed',
                        lease_owner = NULL,
                        lease_expires_utc = NULL,
                        error_code = NULL,
                        diagnostic = NULL,
                        updated_utc = $now
                    WHERE intent_id = $intentId;
                    """,
                    token,
                    ("$now", (object)now),
                    ("$missionId", state.Mission.MissionId),
                    ("$intentId", intent.DispatchIntentId));
                return 0;
            },
            cancellationToken);
        return true;
    }

    private async Task SettleAgentRunAsync(
        AgentRunSnapshot run,
        CoWorkAgentRunStatus status,
        long? actualUsage,
        string? errorCode,
        string? outputSummary,
        CancellationToken cancellationToken)
    {
        if (run.Kind == CoWorkAgentRunKind.LeaderSynthesis)
        {
            _dispatchFaultInjector?.Invoke(
                CoWorkDispatchFaultPoint.BeforeMissionCompletion);
        }

        await ReleaseProjectWriterLeaseAsync(run.AgentRunId, cancellationToken);
        await _store.WriteAsync(
            async (connection, transaction, token) =>
            {
                var current = await LoadAgentRunAsync(connection, run.AgentRunId, token);
                if (current is null ||
                    current.Status is CoWorkAgentRunStatus.Completed or
                        CoWorkAgentRunStatus.Failed or
                        CoWorkAgentRunStatus.Cancelled)
                {
                    return 0;
                }

                var used = actualUsage is null
                    ? current.ReservedTokens
                    : Math.Min(actualUsage.Value, current.ReservedTokens);
                await ExecuteSqlAsync(
                    connection,
                    transaction,
                    """
                    UPDATE cowork_budget_scopes
                    SET reserved_tokens = reserved_tokens - $reserved,
                        used_tokens = used_tokens + $used,
                        revision = revision + 1
                    WHERE scope_id = $scopeId;

                    UPDATE agent_runs
                    SET status = $status,
                        budget_reserved_tokens = 0,
                        budget_used_tokens = $used,
                        error_code = $errorCode,
                        diagnostic = $diagnostic,
                        completed_utc = $now,
                        updated_utc = $now,
                        lease_owner = NULL,
                        lease_expires_utc = NULL,
                        project_writer_lease_id = NULL,
                        project_writer_lease_expires_utc = NULL
                    WHERE agent_run_id = $runId;
                    """,
                    token,
                    ("$reserved", current.ReservedTokens),
                    ("$used", used),
                    ("$scopeId", current.BudgetScopeId),
                    ("$status", EnumText(status)),
                    ("$errorCode", errorCode),
                    ("$diagnostic", actualUsage is null ? "usageUnknown" : null),
                    ("$now", UtcNowMilliseconds()),
                    ("$runId", current.AgentRunId));
                if (current.TaskId is { } taskId)
                {
                    var task = await LoadTaskAsync(connection, taskId, token)
                               ?? throw InvalidState("Mission Task is missing.");
                    var taskStatus = status switch
                    {
                        CoWorkAgentRunStatus.Completed when task.RequiresReview =>
                            CoWorkTaskStatus.Review,
                        CoWorkAgentRunStatus.Completed => CoWorkTaskStatus.Completed,
                        CoWorkAgentRunStatus.Cancelled => CoWorkTaskStatus.Cancelled,
                        _ => CoWorkTaskStatus.Failed,
                    };
                    var now = UtcNowMilliseconds();
                    await ExecuteSqlAsync(
                        connection,
                        transaction,
                        """
                        UPDATE mission_tasks
                        SET status = $status,
                            output_summary = $summary,
                            error_code = $errorCode,
                            revision = revision + 1,
                            updated_utc = $now,
                            completed_utc = CASE
                                WHEN $status = 'completed' THEN $now
                                ELSE NULL
                            END
                        WHERE mission_task_id = $taskId;

                        UPDATE missions
                        SET revision = revision + 1,
                            updated_utc = $now
                        WHERE mission_id = $missionId;
                        """,
                        token,
                        ("$status", EnumText(taskStatus)),
                        ("$summary", outputSummary),
                        ("$errorCode", errorCode),
                        ("$now", now),
                        ("$taskId", taskId),
                        ("$missionId", current.MissionId));
                }
                else if (current.Kind == CoWorkAgentRunKind.LeaderSynthesis &&
                         current.MissionId is { } missionId)
                {
                    var mission = await LoadMissionAsync(connection, missionId, token)
                                  ?? throw InvalidState("Mission is missing.");
                    var now = UtcNowMilliseconds();
                    if (status == CoWorkAgentRunStatus.Completed)
                    {
                        var provenance = JsonSerializer.Serialize(
                            new
                            {
                                schemaVersion = 1,
                                missionId,
                                leaderThreadId = current.ThreadId,
                                synthesisRunId = current.AgentRunId,
                                taskIds = mission.Tasks
                                    .OrderBy(task => task.CreatedAt)
                                    .ThenBy(task => task.TaskId)
                                    .Select(task => task.TaskId)
                                    .ToArray(),
                            },
                            JsonOptions);
                        await ExecuteSqlAsync(
                            connection,
                            transaction,
                            """
                            UPDATE missions
                            SET status = 'completed',
                                final_summary = $summary,
                                provenance_json = $provenance,
                                revision = revision + 1,
                                updated_utc = $now,
                                completed_utc = $now
                            WHERE mission_id = $missionId
                              AND status = 'awaitingLeaderReview';
                            """,
                            token,
                            ("$summary", outputSummary),
                            ("$provenance", provenance),
                            ("$now", now),
                            ("$missionId", missionId));
                        await InsertDispatchIntentAsync(
                            connection,
                            transaction,
                            CoWorkDispatchKind.DeliverOrigin,
                            "missionOrigin",
                            missionId,
                            commandId: null,
                            now,
                            token);
                    }
                    else
                    {
                        await ExecuteSqlAsync(
                            connection,
                            transaction,
                            """
                            UPDATE missions
                            SET status = 'failed',
                                revision = revision + 1,
                                updated_utc = $now,
                                completed_utc = $now
                            WHERE mission_id = $missionId
                              AND status = 'awaitingLeaderReview';
                            """,
                            token,
                            ("$now", now),
                            ("$missionId", missionId));
                    }
                }

                return 0;
            },
            cancellationToken);

        if (run.Kind == CoWorkAgentRunKind.LeaderSynthesis)
        {
            _dispatchFaultInjector?.Invoke(
                CoWorkDispatchFaultPoint.AfterMissionCompletion);
        }
    }

    private async Task<CoWorkResult<CoWorkPage<DirectSubAgentSnapshot>>>
        ListDirectSubAgentsAsync(
            SubAgentQueryRequest request,
            bool descendants,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PageSize is < 1 or > 1_000 ||
            !TryReadOffset(request.Cursor, out var offset))
        {
            return await FailureAsync<CoWorkPage<DirectSubAgentSnapshot>>(
                CoWorkErrorCodes.InvalidState,
                "Page size or cursor is invalid.",
                cancellationToken);
        }

        if (!IsHost(request.Actor) &&
            !(request.Actor.Kind == CoWorkActorKind.DirectParent &&
              request.Actor.ThreadId == request.ParentThreadId))
        {
            return await FailureAsync<CoWorkPage<DirectSubAgentSnapshot>>(
                CoWorkErrorCodes.PermissionDenied,
                "The actor cannot inspect this Direct SubAgent lineage.",
                cancellationToken);
        }

        await ObserveTerminalRunsAsync(cancellationToken);
        var page = await _store.ReadAsync(
            async (connection, token) =>
            {
                var ids = descendants
                    ? await LoadDescendantThreadIdsAsync(
                        connection,
                        request.ParentThreadId,
                        includeRoot: false,
                        token)
                    : await LoadChildThreadIdsAsync(
                        connection,
                        request.ParentThreadId,
                        token);
                if (request.ChildThreadId is { } childThreadId)
                {
                    ids = ids.Where(id => id == childThreadId).ToArray();
                }

                var selected = ids.Skip(offset).Take(request.PageSize + 1).ToArray();
                var items = new List<DirectSubAgentSnapshot>();
                foreach (var id in selected.Take(request.PageSize))
                {
                    if (await LoadDirectSubAgentAsync(connection, id, token) is { } item)
                    {
                        items.Add(item);
                    }
                }

                return new CoWorkPage<DirectSubAgentSnapshot>(
                    items,
                    selected.Length > request.PageSize
                        ? (offset + request.PageSize).ToString(
                            System.Globalization.CultureInfo.InvariantCulture)
                        : null);
            },
            cancellationToken);
        return Success(page, await ReadGlobalRevisionAsync(cancellationToken));
    }

    private async Task<bool> CanManageDirectThreadAsync(
        CoWorkActorContext actor,
        Guid childThreadId,
        CancellationToken cancellationToken)
    {
        if (IsHost(actor))
        {
            return true;
        }

        if (actor.Kind != CoWorkActorKind.DirectParent ||
            actor.ThreadId is not { } parentThreadId)
        {
            return false;
        }

        return await _store.ReadAsync(
            async (connection, token) =>
                (await LoadDescendantThreadIdsAsync(
                    connection,
                    parentThreadId,
                    includeRoot: false,
                    token)).Contains(childThreadId),
            cancellationToken);
    }

    private async Task<CoWorkResult<T>?> TryReadCommandReceiptAsync<T>(
        Guid commandId,
        string requestHash,
        CancellationToken cancellationToken) =>
        await _store.ReadAsync(
            (connection, token) => ReadCommandReceiptAsync<T>(
                connection,
                transaction: null,
                commandId,
                requestHash,
                token),
            cancellationToken);

    private static async ValueTask<CoWorkResult<T>?> ReadCommandReceiptAsync<T>(
        DbConnection connection,
        DbTransaction? transaction,
        Guid commandId,
        string requestHash,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT request_sha256, result_json, revision
            FROM cowork_command_receipts
            WHERE command_id = $commandId;
            """;
        AddParameter(command, "$commandId", commandId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        if (!string.Equals(reader.GetString(0), requestHash, StringComparison.Ordinal))
        {
            throw Conflict("Command ID was already used with a different request.");
        }

        var value = JsonSerializer.Deserialize<T>(reader.GetString(1), JsonOptions)
                    ?? throw InvalidState("Stored command result is invalid.");
        return Success(value, reader.GetInt64(2));
    }

    private async Task<CoWorkResult<T>> StoreCommandReceiptAsync<T>(
        CoWorkCommandContext commandContext,
        string commandKind,
        string targetId,
        string requestHash,
        T value,
        long revision,
        CancellationToken cancellationToken) =>
        await _store.WriteAsync(
            async (connection, transaction, token) =>
            {
                var replay = await ReadCommandReceiptAsync<T>(
                    connection,
                    transaction,
                    commandContext.CommandId,
                    requestHash,
                    token);
                if (replay is not null)
                {
                    return replay;
                }

                await ExecuteSqlAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO cowork_command_receipts (
                        command_id, actor_id, command_kind, target_id,
                        request_sha256, result_json, revision, created_utc)
                    VALUES (
                        $commandId, $actorId, $commandKind, $targetId,
                        $requestHash, $resultJson, $revision, $createdUtc);
                    """,
                    token,
                    ("$commandId", commandContext.CommandId),
                    ("$actorId", commandContext.Actor.PrincipalId),
                    ("$commandKind", commandKind),
                    ("$targetId", targetId),
                    ("$requestHash", requestHash),
                    ("$resultJson", JsonSerializer.Serialize(value, JsonOptions)),
                    ("$revision", revision),
                    ("$createdUtc", UtcNowMilliseconds()));
                return Success(value, revision);
            },
            cancellationToken);

    private static async ValueTask<Guid?> FindPreparedCommandRunAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid commandId,
        string requestHash,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT entity_id, idempotency_key
            FROM cowork_dispatch_intents
            WHERE command_id = $commandId
              AND entity_kind = 'agentRun'
            ORDER BY created_utc
            LIMIT 1;
            """;
        AddParameter(command, "$commandId", commandId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        if (!reader.GetString(1).EndsWith(requestHash, StringComparison.Ordinal))
        {
            throw Conflict("Command ID was already used with a different request.");
        }

        return Guid.Parse(reader.GetString(0));
    }

    private static string HashRequest(object request) =>
        Convert.ToHexString(
                SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions)))
            .ToLowerInvariant();

    private async ValueTask RequireExpectedGlobalRevisionAsync(
        DbConnection connection,
        DbTransaction transaction,
        long? expectedRevision,
        CancellationToken cancellationToken)
    {
        if (expectedRevision is null)
        {
            return;
        }

        RequireRevision(
            expectedRevision,
            await ReadRevisionAsync(connection, transaction, cancellationToken));
    }

    private static ValueTask<long> ReadRevisionAsync(
        DbConnection connection,
        DbTransaction? transaction,
        CancellationToken cancellationToken) =>
        ScalarAsync<long>(
            connection,
            transaction,
            "SELECT current_revision FROM cowork_state WHERE id = 1;",
            cancellationToken);

    private async ValueTask RequireGlobalCapacityAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var active = await ScalarAsync<long>(
            connection,
            transaction,
            """
            SELECT count(*)
            FROM agent_runs
            WHERE status IN ('pending', 'starting', 'running');
            """,
            cancellationToken);
        if (active >= _config.MaxConcurrentAgentRuns)
        {
            throw new CoWorkDomainException(
                CoWorkErrorCodes.ConcurrencyExceeded,
                "Workspace AgentRun concurrency is exhausted.");
        }
    }

    private static async ValueTask ReserveBudgetAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid scopeId,
        long reservation,
        CancellationToken cancellationToken)
    {
        var changed = await ExecuteSqlAsync(
            connection,
            transaction,
            """
            UPDATE cowork_budget_scopes
            SET reserved_tokens = reserved_tokens + $reservation,
                revision = revision + 1
            WHERE scope_id = $scopeId
              AND limit_tokens - reserved_tokens - used_tokens >= $reservation;
            """,
            cancellationToken,
            ("$reservation", reservation),
            ("$scopeId", scopeId));
        if (changed != 1)
        {
            throw new CoWorkDomainException(
                CoWorkErrorCodes.BudgetExceeded,
                "Direct SubAgent Token Budget is exhausted.");
        }
    }

    private static long EstimateReservation(string input) =>
        Math.Max(1_024, Encoding.UTF8.GetByteCount(input) / 4L + 1_024);

    private ExecutionWorkspaceDescriptor CreateDirectWorkspace(
        CoWorkWorkspaceMode mode,
        ExecutionWorkspaceDescriptor parent,
        Guid agentRunId) =>
        new(
            mode,
            parent.WorkspaceRoot,
            Path.Combine(_workspace!.SubAgentsRoot, agentRunId.ToString("D"), "scratchpad"),
            WorktreeId: null,
            WorktreeRoot: null,
            BaseCommitSha: null);

    private static CoWorkWorkspaceAccess InferWorkspaceAccess(
        AgentProfileSnapshot profile) =>
        profile.ToolAllowlist.Any(tool =>
            tool.Equals("file.apply_patch", StringComparison.OrdinalIgnoreCase) ||
            tool.Contains("write", StringComparison.OrdinalIgnoreCase) ||
            tool.Contains("shell", StringComparison.OrdinalIgnoreCase) ||
            tool.Contains("terminal", StringComparison.OrdinalIgnoreCase) ||
            tool.Contains("sourceControl", StringComparison.OrdinalIgnoreCase))
            ? CoWorkWorkspaceAccess.ReadWrite
            : CoWorkWorkspaceAccess.ReadOnly;

    private static async ValueTask InsertAgentRunAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid runId,
        Guid? threadId,
        Guid? parentRunId,
        Guid? parentThreadId,
        int attempt,
        AgentProfileSnapshot profile,
        ExecutionWorkspaceDescriptor workspace,
        CoWorkWorkspaceAccess access,
        long budgetLimit,
        long reservation,
        long now,
        CancellationToken cancellationToken) =>
        _ = await ExecuteSqlAsync(
            connection,
            transaction,
            """
            INSERT INTO agent_runs (
                agent_run_id, mission_id, mission_task_id, member_id,
                thread_id, parent_agent_run_id, parent_thread_id,
                run_kind, status, profile_snapshot_json,
                workspace_mode, workspace_access, workspace_json,
                budget_limit_tokens, budget_reserved_tokens, budget_used_tokens,
                attempt, lease_owner, lease_expires_utc,
                error_code, diagnostic, created_utc, updated_utc, completed_utc)
            VALUES (
                $runId, NULL, NULL, NULL,
                $threadId, $parentRunId, $parentThreadId,
                'direct', 'pending', $profile,
                $workspaceMode, $workspaceAccess, $workspace,
                $budgetLimit, $reservation, 0,
                $attempt, NULL, NULL,
                NULL, NULL, $now, $now, NULL);
            """,
            cancellationToken,
            ("$runId", runId),
            ("$threadId", threadId),
            ("$parentRunId", parentRunId),
            ("$parentThreadId", parentThreadId),
            ("$profile", JsonSerializer.Serialize(profile, JsonOptions)),
            ("$workspaceMode", EnumText(workspace.Mode)),
            ("$workspaceAccess", EnumText(access)),
            ("$workspace", JsonSerializer.Serialize(workspace, JsonOptions)),
            ("$budgetLimit", budgetLimit),
            ("$reservation", reservation),
            ("$attempt", attempt),
            ("$now", now));

    private static async ValueTask InsertDirectInputAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid messageId,
        Guid senderThreadId,
        Guid? recipientThreadId,
        string content,
        long now,
        CancellationToken cancellationToken) =>
        _ = await ExecuteSqlAsync(
            connection,
            transaction,
            """
            INSERT INTO mailbox_messages (
                mailbox_message_id, mission_id, scope,
                sender_member_id, recipient_member_id,
                sender_thread_id, recipient_thread_id,
                mission_task_id, artifact_id, message_kind,
                content, content_length, status, attempt_count,
                lease_owner, lease_expires_utc, error_code, diagnostic,
                created_utc, delivered_utc, acknowledged_utc)
            VALUES (
                $id, NULL, 'direct',
                NULL, NULL,
                $senderThreadId, $recipientThreadId,
                NULL, NULL, 'request',
                $content, $length, 'pending', 0,
                NULL, NULL, NULL, NULL,
                $now, NULL, NULL);
            """,
            cancellationToken,
            ("$id", messageId),
            ("$senderThreadId", senderThreadId),
            ("$recipientThreadId", recipientThreadId),
            ("$content", content),
            ("$length", Encoding.UTF8.GetByteCount(content)),
            ("$now", now));

    private static async ValueTask InsertDispatchIntentAsync(
        DbConnection connection,
        DbTransaction transaction,
        CoWorkDispatchKind kind,
        string entityKind,
        Guid entityId,
        Guid? commandId,
        long now,
        CancellationToken cancellationToken,
        string? requestHash = null)
    {
        var intentId = Guid.CreateVersion7();
        var idempotencyKey =
            $"cowork:{EnumText(kind)}:{entityId:D}:{requestHash ?? string.Empty}";
        await ExecuteSqlAsync(
            connection,
            transaction,
            """
            INSERT OR IGNORE INTO cowork_dispatch_intents (
                intent_id, idempotency_key, command_id,
                dispatch_kind, entity_kind, entity_id,
                status, attempt_count, lease_owner, lease_expires_utc,
                error_code, diagnostic, created_utc, updated_utc)
            VALUES (
                $intentId, $idempotencyKey, $commandId,
                $kind, $entityKind, $entityId,
                'pending', 0, NULL, NULL,
                NULL, NULL, $now, $now);
            """,
            cancellationToken,
            ("$intentId", intentId),
            ("$idempotencyKey", idempotencyKey),
            ("$commandId", commandId),
            ("$kind", EnumText(kind)),
            ("$entityKind", entityKind),
            ("$entityId", entityId),
            ("$now", now));
    }

    private async ValueTask PersistWorktreeAsync(
        AgentRunSnapshot run,
        ManagedWorktreeDescriptor worktree,
        CancellationToken cancellationToken) =>
        await _store.WriteAsync(
            async (connection, transaction, token) =>
            {
                var now = UtcNowMilliseconds();
                await ExecuteSqlAsync(
                    connection,
                    transaction,
                    """
                    INSERT OR IGNORE INTO cowork_worktrees (
                        cowork_worktree_id, mission_id, agent_run_id,
                        relative_path, base_commit_sha, status, is_dirty,
                        trust_json, diagnostic, created_utc, updated_utc)
                    VALUES (
                        $id, $missionId, $runId,
                        $relativePath, $baseSha, $status, $isDirty,
                        '{}', NULL, $now, $now);
                    """,
                    token,
                    ("$id", worktree.WorktreeId),
                    ("$missionId", run.MissionId),
                    ("$runId", run.AgentRunId),
                    ("$relativePath", Path.GetRelativePath(
                        _workspace!.WorktreesRoot,
                        worktree.WorktreeRoot)),
                    ("$baseSha", worktree.BaseCommitSha),
                    ("$status", EnumText(worktree.Status)),
                    ("$isDirty", worktree.IsDirty),
                    ("$now", now));
                if (run.MissionId is { } missionId)
                {
                    await ExecuteSqlAsync(
                        connection,
                        transaction,
                        """
                        UPDATE missions
                        SET base_commit_sha = COALESCE(base_commit_sha, $baseSha),
                            updated_utc = $now
                        WHERE mission_id = $missionId;
                        """,
                        token,
                        ("$baseSha", worktree.BaseCommitSha),
                        ("$now", now),
                        ("$missionId", missionId));
                }

                return 0;
            },
            cancellationToken);

    private async Task CompleteIntentAsync(
        Guid intentId,
        CancellationToken cancellationToken) =>
        await _store.WriteAsync(
            async (connection, transaction, token) =>
            {
                await ExecuteSqlAsync(
                    connection,
                    transaction,
                    """
                    UPDATE cowork_dispatch_intents
                    SET status = 'completed',
                        lease_owner = NULL,
                        lease_expires_utc = NULL,
                        error_code = NULL,
                        diagnostic = NULL,
                        updated_utc = $now
                    WHERE intent_id = $intentId;
                    """,
                    token,
                    ("$now", UtcNowMilliseconds()),
                    ("$intentId", intentId));
                return 0;
            },
            cancellationToken);

    private async Task ReleaseIntentAsync(
        Guid intentId,
        CancellationToken cancellationToken) =>
        await _store.WriteAsync(
            async (connection, transaction, token) =>
            {
                await ExecuteSqlAsync(
                    connection,
                    transaction,
                    """
                    UPDATE cowork_dispatch_intents
                    SET status = 'pending',
                        attempt_count = max(0, attempt_count - 1),
                        lease_owner = NULL,
                        lease_expires_utc = NULL,
                        updated_utc = $now
                    WHERE intent_id = $intentId;
                    """,
                    token,
                    ("$now", UtcNowMilliseconds()),
                    ("$intentId", intentId));
                return 0;
            },
            cancellationToken);

    private async Task RetryOrDeadLetterIntentAsync(
        DispatchIntentSnapshot intent,
        string diagnostic,
        CancellationToken cancellationToken)
    {
        if (intent.Attempt >= _config.MaximumDispatchAttempts)
        {
            await FailRunAndIntentAsync(
                intent,
                CoWorkErrorCodes.RetryExhausted,
                diagnostic,
                cancellationToken);
            return;
        }

        await UpdateMailboxAttemptAsync(
            intent,
            status: null,
            errorCode: null,
            diagnostic,
            cancellationToken);
        await Task.Delay(
            TimeSpan.FromMilliseconds(10L << Math.Min(intent.Attempt - 1, 6)),
            _timeProvider,
            cancellationToken);
        await ReleaseIntentWithDiagnosticAsync(
            intent.DispatchIntentId,
            diagnostic,
            cancellationToken);
    }

    private async Task ReleaseIntentWithDiagnosticAsync(
        Guid intentId,
        string diagnostic,
        CancellationToken cancellationToken) =>
        await _store.WriteAsync(
            async (connection, transaction, token) =>
            {
                await ExecuteSqlAsync(
                    connection,
                    transaction,
                    """
                    UPDATE cowork_dispatch_intents
                    SET status = 'pending',
                        lease_owner = NULL,
                        lease_expires_utc = NULL,
                        diagnostic = CASE
                            WHEN diagnostic LIKE 'expectedSequence:%'
                                THEN diagnostic
                            ELSE $diagnostic
                        END,
                        updated_utc = $now
                    WHERE intent_id = $intentId;
                    """,
                    token,
                    ("$diagnostic", diagnostic),
                    ("$now", UtcNowMilliseconds()),
                    ("$intentId", intentId));
                return 0;
            },
            cancellationToken);

    private async Task PersistIntentDiagnosticAsync(
        Guid intentId,
        string diagnostic,
        CancellationToken cancellationToken) =>
        await _store.WriteAsync(
            async (connection, transaction, token) =>
            {
                await ExecuteSqlAsync(
                    connection,
                    transaction,
                    """
                    UPDATE cowork_dispatch_intents
                    SET diagnostic = $diagnostic,
                        updated_utc = $now
                    WHERE intent_id = $intentId
                      AND status = 'leased'
                      AND lease_owner = $owner;
                    """,
                    token,
                    ("$diagnostic", diagnostic),
                    ("$now", UtcNowMilliseconds()),
                    ("$intentId", intentId),
                    ("$owner", _leaseOwner));
                return 0;
            },
            cancellationToken);

    private static long? TryReadExpectedSequence(string? diagnostic) =>
        diagnostic is not null &&
        diagnostic.StartsWith("expectedSequence:", StringComparison.Ordinal) &&
        long.TryParse(
            diagnostic["expectedSequence:".Length..],
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;

    private async Task DeadLetterIntentAsync(
        DispatchIntentSnapshot intent,
        string errorCode,
        string diagnostic,
        CancellationToken cancellationToken) =>
        await _store.WriteAsync(
            async (connection, transaction, token) =>
            {
                await ExecuteSqlAsync(
                    connection,
                    transaction,
                    """
                    UPDATE cowork_dispatch_intents
                    SET status = 'deadLettered',
                        lease_owner = NULL,
                        lease_expires_utc = NULL,
                        error_code = $errorCode,
                        diagnostic = CASE
                            WHEN diagnostic LIKE 'expectedSequence:%'
                                THEN diagnostic
                            ELSE $diagnostic
                        END,
                        updated_utc = $now
                    WHERE intent_id = $intentId;
                    """,
                    token,
                    ("$errorCode", errorCode),
                    ("$diagnostic", diagnostic),
                    ("$now", UtcNowMilliseconds()),
                    ("$intentId", intent.DispatchIntentId));
                if (string.Equals(
                        intent.EntityKind,
                        "mailboxMessage",
                        StringComparison.Ordinal))
                {
                    await ExecuteSqlAsync(
                        connection,
                        transaction,
                        """
                        UPDATE mailbox_messages
                        SET status = 'deadLettered',
                            attempt_count = $attempt,
                            error_code = $errorCode,
                            diagnostic = $diagnostic
                        WHERE mailbox_message_id = $messageId;
                        """,
                        token,
                        ("$attempt", intent.Attempt),
                        ("$errorCode", errorCode),
                        ("$diagnostic", _sensitiveData.Redact(diagnostic)),
                        ("$messageId", intent.EntityId));
                }

                return 0;
            },
            cancellationToken);

    private async Task UpdateMailboxAttemptAsync(
        DispatchIntentSnapshot intent,
        CoWorkMailboxStatus? status,
        string? errorCode,
        string diagnostic,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(intent.EntityKind, "mailboxMessage", StringComparison.Ordinal))
        {
            return;
        }

        await _store.WriteAsync(
            async (connection, transaction, token) =>
            {
                await ExecuteSqlAsync(
                    connection,
                    transaction,
                    """
                    UPDATE mailbox_messages
                    SET attempt_count = $attempt,
                        status = coalesce($status, status),
                        error_code = $errorCode,
                        diagnostic = $diagnostic
                    WHERE mailbox_message_id = $messageId;
                    """,
                    token,
                    ("$attempt", intent.Attempt),
                    ("$status", status is null ? null : EnumText(status.Value)),
                    ("$errorCode", errorCode),
                    ("$diagnostic", _sensitiveData.Redact(diagnostic)),
                    ("$messageId", intent.EntityId));
                return 0;
            },
            cancellationToken);
    }

    private async Task FailRunAndIntentAsync(
        DispatchIntentSnapshot intent,
        string errorCode,
        string diagnostic,
        CancellationToken cancellationToken)
    {
        await DeadLetterIntentAsync(intent, errorCode, diagnostic, cancellationToken);
        if (!string.Equals(intent.EntityKind, "agentRun", StringComparison.Ordinal))
        {
            return;
        }

        var run = await ReadAgentRunAsync(intent.EntityId, cancellationToken);
        if (run is not null)
        {
            await SettleAgentRunAsync(
                run,
                CoWorkAgentRunStatus.Failed,
                actualUsage: null,
                errorCode,
                outputSummary: _sensitiveData.Redact(diagnostic),
                cancellationToken);
        }
    }

    private async ValueTask ReleaseRunReservationsAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid threadId,
        long now,
        CancellationToken cancellationToken)
    {
        var active = new List<(Guid RunId, Guid ScopeId, long Reserved)>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                WITH RECURSIVE roots(run_id, parent_run_id, root_run_id) AS (
                    SELECT agent_run_id, parent_agent_run_id, agent_run_id
                    FROM agent_runs
                    WHERE thread_id = $threadId
                      AND status IN ('pending', 'starting', 'running')
                )
                SELECT r.agent_run_id, s.scope_id, r.budget_reserved_tokens
                FROM agent_runs r
                JOIN cowork_budget_scopes s
                  ON s.owner_kind = 'agentRun'
                 AND s.owner_id = (
                     WITH RECURSIVE lineage(agent_run_id, parent_agent_run_id) AS (
                         SELECT r.agent_run_id, r.parent_agent_run_id
                         UNION ALL
                         SELECT parent.agent_run_id, parent.parent_agent_run_id
                         FROM agent_runs parent
                         JOIN lineage ON parent.agent_run_id = lineage.parent_agent_run_id
                     )
                     SELECT agent_run_id
                     FROM lineage
                     WHERE parent_agent_run_id IS NULL
                     LIMIT 1)
                WHERE r.thread_id = $threadId
                  AND r.status IN ('pending', 'starting', 'running');
                """;
            AddParameter(command, "$threadId", threadId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                active.Add((
                    Guid.Parse(reader.GetString(0)),
                    Guid.Parse(reader.GetString(1)),
                    reader.GetInt64(2)));
            }
        }

        foreach (var run in active)
        {
            await ExecuteSqlAsync(
                connection,
                transaction,
                """
                UPDATE cowork_budget_scopes
                SET reserved_tokens = reserved_tokens - $reserved,
                    revision = revision + 1
                WHERE scope_id = $scopeId;

                UPDATE agent_runs
                SET status = 'cancelled',
                    budget_reserved_tokens = 0,
                    error_code = NULL,
                    diagnostic = 'cancelledByParent',
                    completed_utc = $now,
                    updated_utc = $now,
                    lease_owner = NULL,
                    lease_expires_utc = NULL
                WHERE agent_run_id = $runId;
                """,
                cancellationToken,
                ("$reserved", run.Reserved),
                ("$scopeId", run.ScopeId),
                ("$now", now),
                ("$runId", run.RunId));
        }
    }

    private async Task<AgentRunSnapshot?> ReadAgentRunAsync(
        Guid agentRunId,
        CancellationToken cancellationToken) =>
        await _store.ReadAsync(
            (connection, token) => LoadAgentRunAsync(connection, agentRunId, token),
            cancellationToken);

    private async Task<Guid?> ReadRunDispatchIdAsync(
        Guid agentRunId,
        CancellationToken cancellationToken) =>
        await _store.ReadAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT diagnostic
                    FROM agent_runs
                    WHERE agent_run_id = $id;
                    """;
                AddParameter(command, "$id", agentRunId);
                var diagnostic = Convert.ToString(
                    await command.ExecuteScalarAsync(token),
                    System.Globalization.CultureInfo.InvariantCulture);
                return (Guid?)(diagnostic is not null &&
                               diagnostic.StartsWith(
                                   "turn:",
                                   StringComparison.Ordinal) &&
                               Guid.TryParse(
                                   diagnostic.AsSpan("turn:".Length),
                                   out var id)
                    ? id
                    : null);
            },
            cancellationToken);

    private static async ValueTask<AgentRunSnapshot?> LoadLatestRunForThreadAsync(
        DbConnection connection,
        Guid threadId,
        CancellationToken cancellationToken)
    {
        var id = await ReadOptionalGuidAsync(
            connection,
            """
            SELECT agent_run_id
            FROM agent_runs
            WHERE run_kind = 'direct'
              AND thread_id = $threadId
            ORDER BY created_utc DESC, agent_run_id DESC
            LIMIT 1;
            """,
            cancellationToken,
            ("$threadId", threadId));
        return id is null ? null : await LoadAgentRunAsync(connection, id.Value, cancellationToken);
    }

    private static async ValueTask<AgentRunSnapshot?> LoadFirstRunForThreadAsync(
        DbConnection connection,
        Guid threadId,
        CancellationToken cancellationToken)
    {
        var id = await ReadOptionalGuidAsync(
            connection,
            """
            SELECT agent_run_id
            FROM agent_runs
            WHERE run_kind = 'direct'
              AND thread_id = $threadId
            ORDER BY created_utc, agent_run_id
            LIMIT 1;
            """,
            cancellationToken,
            ("$threadId", threadId));
        return id is null ? null : await LoadAgentRunAsync(connection, id.Value, cancellationToken);
    }

    private static async ValueTask<AgentRunSnapshot?> LoadAgentRunAsync(
        DbConnection connection,
        Guid agentRunId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT run_kind, status, thread_id,
                   parent_agent_run_id, parent_thread_id,
                   mission_id, mission_task_id, member_id,
                   attempt, profile_snapshot_json, workspace_json, workspace_access,
                   budget_reserved_tokens, budget_used_tokens, error_code,
                   created_utc, updated_utc
            FROM agent_runs
            WHERE agent_run_id = $id;
            """;
        AddParameter(command, "$id", agentRunId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var kind = ParseEnum<CoWorkAgentRunKind>(reader.GetString(0));
        var status = ParseEnum<CoWorkAgentRunStatus>(reader.GetString(1));
        var threadId = reader.IsDBNull(2) ? Guid.Empty : Guid.Parse(reader.GetString(2));
        Guid? parentRunId = reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3));
        Guid? parentThreadId = reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4));
        Guid? missionId = reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5));
        Guid? taskId = reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6));
        Guid? memberId = reader.IsDBNull(7) ? null : Guid.Parse(reader.GetString(7));
        var attempt = reader.GetInt32(8);
        var profile = JsonSerializer.Deserialize<AgentProfileSnapshot>(
                          reader.GetString(9),
                          JsonOptions)
                      ?? throw InvalidState("AgentRun Profile Snapshot is invalid.");
        var workspace = JsonSerializer.Deserialize<ExecutionWorkspaceDescriptor>(
                            reader.GetString(10),
                            JsonOptions)
                        ?? throw InvalidState("AgentRun Workspace Snapshot is invalid.");
        var access = ParseEnum<CoWorkWorkspaceAccess>(reader.GetString(11));
        var reserved = reader.GetInt64(12);
        var used = reader.GetInt64(13);
        var errorCode = reader.IsDBNull(14) ? null : reader.GetString(14);
        var createdAt = FromUnixMilliseconds(reader.GetInt64(15));
        var updatedAt = FromUnixMilliseconds(reader.GetInt64(16));
        await reader.DisposeAsync();

        var budget = await LoadRootBudgetAsync(connection, agentRunId, cancellationToken)
                     ?? throw InvalidState("AgentRun Budget Scope is missing.");
        Guid? previousRunId = null;
        if (threadId != Guid.Empty)
        {
            previousRunId = await ReadOptionalGuidAsync(
                connection,
                """
                SELECT agent_run_id
                FROM agent_runs
                WHERE run_kind = 'direct'
                  AND thread_id = $threadId
                  AND (created_utc < $createdUtc OR
                       (created_utc = $createdUtc AND agent_run_id < $agentRunId))
                ORDER BY created_utc DESC, agent_run_id DESC
                LIMIT 1;
                """,
                cancellationToken,
                ("$threadId", threadId),
                ("$createdUtc", createdAt.ToUnixTimeMilliseconds()),
                ("$agentRunId", agentRunId));
        }

        return new AgentRunSnapshot(
            agentRunId,
            kind,
            status,
            threadId,
            parentRunId,
            parentThreadId,
            previousRunId,
            missionId,
            taskId,
            memberId,
            attempt,
            profile,
            workspace,
            access,
            budget.BudgetScopeId,
            reserved,
            used,
            errorCode,
            createdAt,
            updatedAt);
    }

    private static async ValueTask<BudgetScopeSnapshot?> LoadRootBudgetAsync(
        DbConnection connection,
        Guid agentRunId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            WITH RECURSIVE lineage(agent_run_id, parent_agent_run_id) AS (
                SELECT agent_run_id, parent_agent_run_id
                FROM agent_runs
                WHERE agent_run_id = $runId
                UNION ALL
                SELECT parent.agent_run_id, parent.parent_agent_run_id
                FROM agent_runs parent
                JOIN lineage ON parent.agent_run_id = lineage.parent_agent_run_id
            )
            SELECT scope_id, owner_kind, owner_id,
                   limit_tokens, reserved_tokens, used_tokens
            FROM cowork_budget_scopes
            WHERE (owner_kind = 'agentRun'
                   AND owner_id IN (SELECT agent_run_id FROM lineage))
               OR (owner_kind = 'mission'
                   AND owner_id = (
                       SELECT mission_id
                       FROM agent_runs
                       WHERE agent_run_id = $runId))
            LIMIT 1;
            """;
        AddParameter(command, "$runId", agentRunId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new BudgetScopeSnapshot(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                Guid.Parse(reader.GetString(2)),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5))
            : null;
    }

    private static async ValueTask<int> ReadRunDepthAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid agentRunId,
        CancellationToken cancellationToken) =>
        checked((int)await ScalarAsync<long>(
            connection,
            transaction,
            """
            WITH RECURSIVE lineage(agent_run_id, parent_agent_run_id, depth) AS (
                SELECT agent_run_id, parent_agent_run_id, 1
                FROM agent_runs
                WHERE agent_run_id = $runId
                UNION ALL
                SELECT parent.agent_run_id, parent.parent_agent_run_id, depth + 1
                FROM agent_runs parent
                JOIN lineage ON parent.agent_run_id = lineage.parent_agent_run_id
            )
            SELECT max(depth) FROM lineage;
            """,
            cancellationToken,
            ("$runId", agentRunId)));

    private static async ValueTask<bool> HasActiveRunAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid threadId,
        CancellationToken cancellationToken) =>
        await ScalarAsync<long>(
            connection,
            transaction,
            """
            SELECT count(*)
            FROM agent_runs
            WHERE thread_id = $threadId
              AND status IN ('pending', 'starting', 'running');
            """,
            cancellationToken,
            ("$threadId", threadId)) != 0;

    private static async ValueTask<DirectSubAgentSnapshot?> LoadDirectSubAgentAsync(
        DbConnection connection,
        Guid childThreadId,
        CancellationToken cancellationToken)
    {
        var first = await LoadFirstRunForThreadAsync(
            connection,
            childThreadId,
            cancellationToken);
        if (first is null || first.ParentThreadId is null)
        {
            return null;
        }

        var activeId = await ReadOptionalGuidAsync(
            connection,
            """
            SELECT agent_run_id
            FROM agent_runs
            WHERE thread_id = $threadId
              AND status IN ('pending', 'starting', 'running')
            ORDER BY created_utc DESC, agent_run_id DESC
            LIMIT 1;
            """,
            cancellationToken,
            ("$threadId", childThreadId));
        var active = activeId is null
            ? null
            : await LoadAgentRunAsync(connection, activeId.Value, cancellationToken);
        var rootThreadId = await ReadLineageRootThreadIdAsync(
            connection,
            childThreadId,
            cancellationToken);
        return new DirectSubAgentSnapshot(
            childThreadId,
            first.ParentThreadId.Value,
            rootThreadId,
            first.Profile.ProfileId,
            first.BudgetScopeId,
            first.ExecutionWorkspace,
            active,
            first.CreatedAt);
    }

    private static async ValueTask<Guid> ReadLineageRootThreadIdAsync(
        DbConnection connection,
        Guid childThreadId,
        CancellationToken cancellationToken)
    {
        var current = childThreadId;
        while (true)
        {
            var first = await LoadFirstRunForThreadAsync(
                connection,
                current,
                cancellationToken);
            if (first?.ParentThreadId is not { } parentThreadId)
            {
                return current;
            }

            var parentRun = await LoadFirstRunForThreadAsync(
                connection,
                parentThreadId,
                cancellationToken);
            if (parentRun is null)
            {
                return current;
            }

            current = parentThreadId;
        }
    }

    private static async ValueTask<Guid[]> LoadChildThreadIdsAsync(
        DbConnection connection,
        Guid parentThreadId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT DISTINCT thread_id
            FROM agent_runs
            WHERE run_kind = 'direct'
              AND parent_thread_id = $parentThreadId
              AND thread_id IS NOT NULL
            ORDER BY thread_id;
            """;
        AddParameter(command, "$parentThreadId", parentThreadId);
        return await ReadGuidsAsync(command, cancellationToken);
    }

    private static async ValueTask<Guid[]> LoadDescendantThreadIdsAsync(
        DbConnection connection,
        Guid parentThreadId,
        bool includeRoot,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            WITH RECURSIVE descendants(thread_id) AS (
                SELECT DISTINCT thread_id
                FROM agent_runs
                WHERE run_kind = 'direct'
                  AND parent_thread_id = $parentThreadId
                  AND thread_id IS NOT NULL
                UNION
                SELECT DISTINCT child.thread_id
                FROM agent_runs child
                JOIN descendants parent
                  ON child.parent_thread_id = parent.thread_id
                WHERE child.run_kind = 'direct'
                  AND child.thread_id IS NOT NULL
            )
            SELECT thread_id FROM descendants ORDER BY thread_id;
            """;
        AddParameter(command, "$parentThreadId", parentThreadId);
        var values = (await ReadGuidsAsync(command, cancellationToken)).ToList();
        if (includeRoot &&
            await LoadFirstRunForThreadAsync(connection, parentThreadId, cancellationToken)
                is not null)
        {
            values.Insert(0, parentThreadId);
        }

        return values.Distinct().ToArray();
    }

    private static async ValueTask<Guid[]> ReadGuidsAsync(
        DbCommand command,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var values = new List<Guid>();
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(Guid.Parse(reader.GetString(0)));
        }

        return values.ToArray();
    }

    private static async ValueTask<Guid?> ReadOptionalGuidAsync(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            AddParameter(command, parameter.Name, parameter.Value);
        }

        return Guid.TryParse(
            Convert.ToString(await command.ExecuteScalarAsync(cancellationToken)),
            out var value)
            ? value
            : null;
    }

    private static async ValueTask<DispatchIntentSnapshot?> LoadDispatchIntentAsync(
        DbConnection connection,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT dispatch_kind, entity_kind, entity_id, idempotency_key,
                   status, attempt_count, lease_expires_utc, error_code,
                   created_utc, updated_utc, diagnostic
            FROM cowork_dispatch_intents
            WHERE intent_id = $id;
            """;
        AddParameter(command, "$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new DispatchIntentSnapshot(
            id,
            ParseEnum<CoWorkDispatchKind>(reader.GetString(0)),
            reader.GetString(1),
            Guid.Parse(reader.GetString(2)),
            reader.GetString(3),
            ParseEnum<CoWorkDispatchStatus>(reader.GetString(4)),
            reader.GetInt32(5),
            reader.IsDBNull(6)
                ? null
                : FromUnixMilliseconds(reader.GetInt64(6)),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            FromUnixMilliseconds(reader.GetInt64(8)),
            FromUnixMilliseconds(reader.GetInt64(9)),
            reader.IsDBNull(10) ? null : reader.GetString(10));
    }

    private async Task<MailboxMessageSnapshot[]> ReadPendingDirectMessagesAsync(
        Guid childThreadId,
        CancellationToken cancellationToken) =>
        await _store.ReadAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT mailbox_message_id
                    FROM mailbox_messages
                    WHERE scope = 'direct'
                      AND recipient_thread_id = $threadId
                      AND status = 'pending'
                    ORDER BY created_utc, mailbox_message_id;
                    """;
                AddParameter(command, "$threadId", childThreadId);
                var ids = await ReadGuidsAsync(command, token);
                var messages = new List<MailboxMessageSnapshot>(ids.Length);
                foreach (var id in ids)
                {
                    messages.Add((await LoadMailboxMessageAsync(connection, id, token))!);
                }

                return messages.ToArray();
            },
            cancellationToken);

    private async Task<MailboxMessageSnapshot?> ReadMailboxMessageAsync(
        Guid messageId,
        CancellationToken cancellationToken) =>
        await _store.ReadAsync(
            (connection, token) => LoadMailboxMessageAsync(connection, messageId, token),
            cancellationToken);

    private static async ValueTask<MailboxMessageSnapshot?> LoadMailboxMessageAsync(
        DbConnection connection,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT scope, mission_id,
                   sender_member_id, recipient_member_id,
                   sender_thread_id, recipient_thread_id,
                   message_kind, content, mission_task_id, artifact_id,
                   status, attempt_count, error_code,
                   created_utc,
                   coalesce(acknowledged_utc, delivered_utc, created_utc)
            FROM mailbox_messages
            WHERE mailbox_message_id = $id;
            """;
        AddParameter(command, "$id", messageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var senderId = reader.IsDBNull(2)
            ? Guid.Parse(reader.GetString(4))
            : Guid.Parse(reader.GetString(2));
        var recipientId = reader.IsDBNull(3)
            ? Guid.Parse(reader.GetString(5))
            : Guid.Parse(reader.GetString(3));
        return new MailboxMessageSnapshot(
            messageId,
            ParseEnum<CoWorkMailboxScope>(reader.GetString(0)),
            reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)),
            senderId,
            recipientId,
            ParseEnum<CoWorkMailboxKind>(reader.GetString(6)),
            reader.GetString(7),
            reader.IsDBNull(8) ? null : Guid.Parse(reader.GetString(8)),
            reader.IsDBNull(9) ? null : Guid.Parse(reader.GetString(9)),
            ParseEnum<CoWorkMailboxStatus>(reader.GetString(10)),
            reader.GetInt32(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            FromUnixMilliseconds(reader.GetInt64(13)),
            FromUnixMilliseconds(reader.GetInt64(14)));
    }

    private sealed record PreparedDirectRun(
        Guid AgentRunId,
        long Revision,
        CoWorkResult<AgentRunSnapshot>? Replay);
}
