using System.Data.Common;
using System.Globalization;
using System.Threading.Channels;
using OpenCoWork.Abstractions;

namespace OpenCoWork.Automations;

internal sealed class AutomationReconciler : IAsyncDisposable
{
    internal static readonly TimeSpan ReconcileInterval =
        ProjectWriterLeaseLimits.RenewalInterval;

    private readonly IWorkspaceStateStore _store;
    private readonly AutomationService _service;
    private readonly AutomationsConfig _config;
    private readonly TimeProvider _timeProvider;
    private readonly Func<Guid, string, CancellationToken, Task<bool>> _dispatch;
    private readonly IProjectWriterLeaseService? _writerLeases;
    private readonly AutomationSourceRuntime? _source;
    private readonly AutomationControlPlane? _controlPlane;
    private readonly IModuleHealthReporter? _health;
    private readonly ISessionService? _sessions;
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);
    private CancellationTokenSource? _lifetime;
    private Task? _worker;
    private int _degraded;

    public AutomationReconciler(
        IWorkspaceStateStore store,
        AutomationService service,
        AutomationsConfig config,
        TimeProvider timeProvider,
        AutomationDispatcher dispatcher,
        IProjectWriterLeaseService writerLeases,
        AutomationSourceRuntime source,
        AutomationControlPlane controlPlane,
        ISessionService sessions,
        IModuleHealthReporter? health = null)
        : this(
            store,
            service,
            config,
            timeProvider,
            dispatcher.DispatchNextAsync,
            writerLeases,
            source,
            controlPlane,
            health,
            sessions)
    {
    }

    internal AutomationReconciler(
        IWorkspaceStateStore store,
        AutomationService service,
        AutomationsConfig config,
        TimeProvider timeProvider,
        Func<Guid, string, CancellationToken, Task<bool>> dispatch,
        IProjectWriterLeaseService? writerLeases = null,
        AutomationSourceRuntime? source = null,
        AutomationControlPlane? controlPlane = null,
        IModuleHealthReporter? health = null,
        ISessionService? sessions = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        _writerLeases = writerLeases;
        _source = source;
        _controlPlane = controlPlane;
        _health = health;
        _sessions = sessions;
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        if (_lifetime is not null)
        {
            return;
        }

        if (_source is not null)
        {
            _source.Changed += OnSourceChanged;
            _source.Faulted += OnSourceFaulted;
        }
        try
        {
            await ReconcileOnceAsync(ProcessOwner(), cancellationToken);
            MarkHealthy();
            var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            _lifetime = lifetime;
            _worker = RunAsync(lifetime.Token);
        }
        catch
        {
            if (_source is not null)
            {
                _source.Changed -= OnSourceChanged;
                _source.Faulted -= OnSourceFaulted;
            }
            throw;
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        var lifetime = Interlocked.Exchange(ref _lifetime, null);
        if (lifetime is null)
        {
            return;
        }

        if (_source is not null)
        {
            _source.Changed -= OnSourceChanged;
            _source.Faulted -= OnSourceFaulted;
        }
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

        await ReleaseWriterLeasesAsync(cancellationToken);
        lifetime.Dispose();
    }

    public void Wake() => _wake.Writer.TryWrite(true);

    internal async Task ReconcileOnceAsync(
        string leaseOwner,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        await _reconcileGate.WaitAsync(cancellationToken);
        try
        {
            await ApplyDeadlinesAsync(cancellationToken);
            await RecoverSessionFactsAsync(cancellationToken);
            await AcquireMissingWriterLeasesAsync(cancellationToken);
            await RenewWriterLeasesAsync(cancellationToken);
            await CancelTerminalTurnsAsync(cancellationToken);
            await ReleaseTerminalWriterLeasesAsync(cancellationToken);
            await EnsureTerminalIntentsAsync(cancellationToken);
            await ClaimDueSchedulesAsync(cancellationToken);
            var pending = await ReadPendingRunsAsync(cancellationToken);
            foreach (var runId in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = await _dispatch(runId, leaseOwner, cancellationToken);
            }
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        _reconcileGate.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await WaitForWakeAsync(cancellationToken);
                await ReconcileOnceAsync(ProcessOwner(), cancellationToken);
                MarkHealthy();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                MarkDegraded(exception);
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

    private void OnSourceChanged()
    {
        Wake();
    }

    private void OnSourceFaulted(Exception exception)
    {
        MarkDegraded(exception);
        Wake();
    }

    private void MarkDegraded(Exception exception)
    {
        _controlPlane?.SetAvailable(false);
        Interlocked.Exchange(ref _degraded, 1);
        _health?.ReportDegraded(
            "automations",
            $"Automation control plane is unavailable: {exception.GetType().Name}.");
    }

    private void MarkHealthy()
    {
        if (_source is { IsHealthy: false })
        {
            return;
        }

        _controlPlane?.SetAvailable(true);
        if (Interlocked.Exchange(ref _degraded, 0) != 0)
        {
            _health?.ClearDegraded("automations");
        }
    }

    private async Task ClaimDueSchedulesAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var due = await _store.ReadAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT s.automation_id, s.next_occurrence_utc
                    FROM automation_schedules s
                    JOIN automation_definitions d
                      ON d.automation_id = s.automation_id
                    WHERE s.next_occurrence_utc <= $now
                      AND d.source_status = 'ready'
                      AND d.enabled = 1
                    ORDER BY s.next_occurrence_utc, s.automation_id;
                    """;
                Add(command, "$now", Milliseconds(now));
                await using var reader = await command.ExecuteReaderAsync(token);
                var result = new List<(string AutomationId, DateTimeOffset Due)>();
                while (await reader.ReadAsync(token))
                {
                    result.Add((
                        reader.GetString(0),
                        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1))));
                }

                return result;
            },
            cancellationToken);
        foreach (var item in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = await _service.StartScheduledRunAsync(
                item.AutomationId,
                item.Due,
                cancellationToken);
        }
    }

    private ValueTask<Guid[]> ReadPendingRunsAsync(
        CancellationToken cancellationToken) =>
        _store.ReadAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT entity_id
                    FROM automation_dispatch_intents
                    WHERE entity_kind = 'automationRun'
                      AND attempt_count < 5
                      AND (
                          status = 'pending' OR
                          (status = 'leased' AND lease_expires_utc <= $now)
                      )
                    GROUP BY entity_id
                    ORDER BY min(created_utc), entity_id
                    LIMIT $limit;
                    """;
                Add(command, "$limit", _config.MaxConcurrentRuns);
                Add(command, "$now", Milliseconds(_timeProvider.GetUtcNow()));
                await using var reader = await command.ExecuteReaderAsync(token);
                var result = new List<Guid>();
                while (await reader.ReadAsync(token))
                {
                    result.Add(Guid.Parse(reader.GetString(0)));
                }

                return result.ToArray();
            },
            cancellationToken);

    private Task EnsureTerminalIntentsAsync(
        CancellationToken cancellationToken) =>
        _store.WriteAsync(
            async (connection, transaction, token) =>
            {
                await using var select = connection.CreateCommand();
                select.Transaction = transaction;
                select.CommandText =
                    """
                    SELECT r.automation_run_id
                    FROM automation_runs r
                    WHERE r.status IN ('completed', 'failed', 'cancelled', 'timedOut')
                      AND NOT EXISTS (
                          SELECT 1
                          FROM automation_dispatch_intents i
                          WHERE i.entity_kind = 'automationRun'
                            AND i.entity_id = r.automation_run_id
                            AND i.dispatch_kind = 'archiveThread'
                      )
                    ORDER BY r.completed_utc, r.automation_run_id;
                    """;
                var runIds = new List<Guid>();
                await using (var reader = await select.ExecuteReaderAsync(token))
                {
                    while (await reader.ReadAsync(token))
                    {
                        runIds.Add(Guid.Parse(reader.GetString(0)));
                    }
                }

                var now = Milliseconds(_timeProvider.GetUtcNow());
                foreach (var runId in runIds)
                {
                    await ExecuteAsync(
                        connection,
                        transaction,
                        """
                        INSERT INTO automation_dispatch_intents (
                            intent_id, idempotency_key, dispatch_kind,
                            entity_kind, entity_id, status, attempt_count,
                            lease_owner, lease_expires_utc, error_code, diagnostic,
                            created_utc, updated_utc)
                        VALUES (
                            $intentId, $key, 'archiveThread',
                            'automationRun', $runId, 'pending', 0,
                            NULL, NULL, NULL, NULL,
                            $now, $now)
                        ON CONFLICT(idempotency_key) DO NOTHING;
                        """,
                        token,
                        ("$intentId", DerivedId(runId, 0x33).ToString("D")),
                        ("$key", $"automation-run:{runId:D}:archiveThread"),
                        ("$runId", runId.ToString("D")),
                        ("$now", now));
                }

                return 0;
            },
            cancellationToken).AsTask();

    private Task ApplyDeadlinesAsync(CancellationToken cancellationToken) =>
        _store.WriteAsync(
            async (connection, transaction, token) =>
            {
                var now = Milliseconds(_timeProvider.GetUtcNow());
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    UPDATE automation_runs
                    SET status = 'timedOut',
                        attention_kind = NULL,
                        diagnostic = 'Automation deadline elapsed.',
                        completed_utc = $now,
                        revision = revision + 1,
                        updated_utc = $now
                    WHERE (status = 'running' AND run_deadline_utc <= $now)
                       OR (status = 'needsAttention' AND
                           attention_deadline_utc IS NOT NULL AND
                           attention_deadline_utc <= $now);
                    UPDATE automation_state
                    SET automation_revision = automation_revision + 1,
                        updated_utc = $now
                    WHERE id = 1 AND changes() > 0;
                    """,
                    token,
                    ("$now", now));
                return 0;
            },
            cancellationToken).AsTask();

    private async Task RecoverSessionFactsAsync(CancellationToken cancellationToken)
    {
        var facts = await _store.ReadAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT r.automation_run_id, t.status,
                           t.error_code, t.error_message,
                           r.project_writer_lease_id,
                           EXISTS (
                               SELECT 1
                               FROM tool_invocations ti
                               WHERE ti.thread_id = r.thread_id
                                 AND ti.turn_id = t.turn_id
                                 AND ti.status = 'outcomeUnknown'
                           ),
                           (
                               SELECT p.timeout_utc
                               FROM pending_interactions p
                               WHERE p.thread_id = r.thread_id
                                 AND p.turn_id = t.turn_id
                                 AND p.status = 'pending'
                               ORDER BY p.created_utc DESC, p.interaction_id DESC
                               LIMIT 1
                           ),
                           json_extract(
                               r.definition_snapshot_json,
                               '$.attentionTimeoutMilliseconds')
                    FROM automation_runs r
                    JOIN turns t ON t.thread_id = r.thread_id
                    WHERE (
                              r.status IN ('running', 'needsAttention') OR
                              EXISTS (
                                  SELECT 1
                                  FROM tool_invocations ti
                                  WHERE ti.thread_id = r.thread_id
                                    AND ti.turn_id = t.turn_id
                                    AND ti.status = 'outcomeUnknown'
                              )
                          )
                      AND t.turn_id = (
                          SELECT t2.turn_id
                          FROM turns t2
                          WHERE t2.thread_id = r.thread_id
                          ORDER BY t2.created_utc DESC, t2.turn_id DESC
                          LIMIT 1)
                    ORDER BY r.automation_run_id;
                    """;
                await using var reader = await command.ExecuteReaderAsync(token);
                var result = new List<SessionFact>();
                while (await reader.ReadAsync(token))
                {
                    result.Add(new SessionFact(
                        Guid.Parse(reader.GetString(0)),
                        reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)),
                        reader.GetInt64(5) != 0,
                        reader.IsDBNull(6)
                            ? null
                            : DateTimeOffset.FromUnixTimeMilliseconds(
                                reader.GetInt64(6)),
                        reader.GetInt64(7)));
                }

                return result;
            },
            cancellationToken);
        foreach (var fact in facts)
        {
            await ApplySessionFactAsync(fact, cancellationToken);
        }
    }

    private async Task ApplySessionFactAsync(
        SessionFact fact,
        CancellationToken cancellationToken)
    {
        var target = fact.HasOutcomeUnknown
            ? "needsAttention"
            : fact.Status switch
            {
                "running" => "running",
                "waitingApproval" => "needsAttention",
                "waitingInput" => "needsAttention",
                "completed" => "completed",
                "failed" => "failed",
                "cancelled" => "cancelled",
                _ => null,
            };
        if (target is null)
        {
            return;
        }

        var attention = fact.HasOutcomeUnknown
            ? "outcomeUnknown"
            : fact.Status switch
            {
                "waitingApproval" => "approvalRequired",
                "waitingInput" => "userInputRequired",
                _ => null,
            };
        var terminal = target is "completed" or "failed" or "cancelled";
        var configuredDeadline =
            _timeProvider.GetUtcNow() +
            TimeSpan.FromMilliseconds(
                Math.Min(
                    fact.AttentionTimeoutMilliseconds,
                    (long)_config.MaximumAttentionTimeout.TotalMilliseconds));
        var attentionDeadline = attention is null
            ? (DateTimeOffset?)null
            : fact.InteractionTimeoutUtc is { } interactionTimeout &&
              interactionTimeout < configuredDeadline
                ? interactionTimeout
                : configuredDeadline;
        var errorCode = fact.HasOutcomeUnknown
            ? AutomationErrorCodes.OutcomeUnknown
            : fact.ErrorCode;
        var diagnostic = fact.ErrorMessage is { Length: > 4096 } message
            ? message[..4096]
            : fact.ErrorMessage;
        await _store.WriteAsync(
            async (connection, transaction, token) =>
            {
                var now = _timeProvider.GetUtcNow();
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    UPDATE automation_runs
                    SET status = $status,
                        attention_kind = $attention,
                        attention_deadline_utc = CASE
                            WHEN $attention IS NULL THEN NULL
                            ELSE COALESCE(attention_deadline_utc, $attentionDeadline)
                        END,
                        error_code = $errorCode,
                        diagnostic = $diagnostic,
                        project_writer_lease_id = CASE WHEN $terminal = 1
                                                       THEN NULL
                                                       ELSE project_writer_lease_id END,
                        project_writer_lease_expires_utc = CASE WHEN $terminal = 1
                                                                THEN NULL
                                                                ELSE project_writer_lease_expires_utc END,
                        completed_utc = CASE WHEN $terminal = 1 THEN $now
                                             ELSE completed_utc END,
                        revision = revision + 1,
                        updated_utc = $now
                    WHERE automation_run_id = $runId
                      AND (
                          status IN ('running', 'needsAttention') OR
                          ($outcomeUnknown = 1 AND
                           status IN ('failed', 'cancelled', 'timedOut'))
                      )
                      AND (
                          status <> $status OR
                          COALESCE(attention_kind, '') <>
                              COALESCE($attention, '') OR
                          ($attention IS NOT NULL AND
                           attention_deadline_utc IS NULL) OR
                          COALESCE(error_code, '') <>
                              COALESCE($errorCode, '') OR
                          COALESCE(diagnostic, '') <>
                              COALESCE($diagnostic, '')
                      );
                    UPDATE automation_state
                    SET automation_revision = automation_revision + 1,
                        updated_utc = $now
                    WHERE id = 1 AND changes() > 0;
                    """,
                    token,
                    ("$status", target),
                    ("$attention", attention),
                    ("$attentionDeadline", attentionDeadline is null
                        ? null
                        : Milliseconds(attentionDeadline.Value)),
                    ("$errorCode", errorCode),
                    ("$diagnostic", diagnostic),
                    ("$outcomeUnknown", fact.HasOutcomeUnknown ? 1 : 0),
                    ("$terminal", terminal ? 1 : 0),
                    ("$now", Milliseconds(now)),
                    ("$runId", fact.RunId.ToString("D")));
                return 0;
            },
            cancellationToken);
        if (terminal && fact.ProjectWriterLeaseId is { } leaseId &&
            _writerLeases is not null)
        {
            _ = await _writerLeases.ReleaseAsync(
                new ProjectWriterLeaseOwner(
                    ProjectWriterLeaseOwnerKind.AutomationRun,
                    fact.RunId),
                leaseId,
                cancellationToken);
        }
    }

    private async Task CancelTerminalTurnsAsync(
        CancellationToken cancellationToken)
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
                    SELECT r.automation_run_id, r.thread_id
                    FROM automation_runs r
                    JOIN threads t ON t.thread_id = r.thread_id
                    WHERE (
                              r.status IN ('cancelled', 'timedOut') OR
                              (r.status = 'failed' AND
                               r.error_code = $leaseLost)
                          )
                      AND r.attention_kind IS NULL
                      AND t.active_turn_id IS NOT NULL
                    ORDER BY r.automation_run_id;
                    """;
                Add(command, "$leaseLost", AutomationErrorCodes.LeaseLost);
                await using var reader = await command.ExecuteReaderAsync(token);
                var result = new List<(Guid RunId, Guid ThreadId)>();
                while (await reader.ReadAsync(token))
                {
                    result.Add((
                        Guid.Parse(reader.GetString(0)),
                        Guid.Parse(reader.GetString(1))));
                }

                return result;
            },
            cancellationToken);
        foreach (var run in runs)
        {
            var thread = await _sessions.GetThreadAsync(
                run.ThreadId,
                cancellationToken);
            if (thread.Value?.ActiveTurnId is not { } turnId)
            {
                continue;
            }

            _ = await _sessions.CancelTurnAsync(
                new CancelTurnRequest(
                    run.ThreadId,
                    turnId,
                    DerivedId(run.RunId, 0xc2),
                    thread.Value.CurrentSequence),
                cancellationToken);
        }
    }

    private async Task RenewWriterLeasesAsync(CancellationToken cancellationToken)
    {
        if (_writerLeases is null)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var renewBefore = now +
                          ProjectWriterLeaseLimits.LeaseDuration -
                          ProjectWriterLeaseLimits.RenewalInterval;
        var leases = await _store.ReadAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT automation_run_id, project_writer_lease_id
                    FROM automation_runs
                    WHERE status IN ('running', 'needsAttention')
                      AND project_writer_lease_id IS NOT NULL
                      AND project_writer_lease_expires_utc <= $renewBefore
                    ORDER BY automation_run_id;
                    """;
                Add(command, "$renewBefore", Milliseconds(renewBefore));
                await using var reader = await command.ExecuteReaderAsync(token);
                var result = new List<(Guid RunId, Guid LeaseId)>();
                while (await reader.ReadAsync(token))
                {
                    result.Add((
                        Guid.Parse(reader.GetString(0)),
                        Guid.Parse(reader.GetString(1))));
                }

                return result;
            },
            cancellationToken);
        foreach (var item in leases)
        {
            var renewed = await _writerLeases.RenewAsync(
                new ProjectWriterLeaseOwner(
                    ProjectWriterLeaseOwnerKind.AutomationRun,
                    item.RunId),
                item.LeaseId,
                cancellationToken);
            await PersistWriterLeaseAsync(item.RunId, renewed, cancellationToken);
        }
    }

    private async Task AcquireMissingWriterLeasesAsync(
        CancellationToken cancellationToken)
    {
        if (_writerLeases is null)
        {
            return;
        }

        var runIds = await _store.ReadAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT automation_run_id
                    FROM automation_runs
                    WHERE status IN ('running', 'needsAttention')
                      AND workspace_mode = 'project'
                      AND workspace_access = 'readWrite'
                      AND project_writer_lease_id IS NULL
                    ORDER BY automation_run_id;
                    """;
                await using var reader = await command.ExecuteReaderAsync(token);
                var result = new List<Guid>();
                while (await reader.ReadAsync(token))
                {
                    result.Add(Guid.Parse(reader.GetString(0)));
                }

                return result;
            },
            cancellationToken);
        foreach (var runId in runIds)
        {
            var lease = await _writerLeases.TryAcquireAsync(
                new ProjectWriterLeaseOwner(
                    ProjectWriterLeaseOwnerKind.AutomationRun,
                    runId),
                cancellationToken);
            if (lease is not null)
            {
                await PersistWriterLeaseAsync(runId, lease, cancellationToken);
            }
        }
    }

    private async Task ReleaseTerminalWriterLeasesAsync(
        CancellationToken cancellationToken)
    {
        if (_writerLeases is null)
        {
            return;
        }

        var leases = await _store.ReadAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT automation_run_id, project_writer_lease_id
                    FROM automation_runs
                    WHERE status IN ('completed', 'failed', 'cancelled', 'timedOut')
                      AND project_writer_lease_id IS NOT NULL
                    ORDER BY automation_run_id;
                    """;
                await using var reader = await command.ExecuteReaderAsync(token);
                var result = new List<(Guid RunId, Guid LeaseId)>();
                while (await reader.ReadAsync(token))
                {
                    result.Add((
                        Guid.Parse(reader.GetString(0)),
                        Guid.Parse(reader.GetString(1))));
                }

                return result;
            },
            cancellationToken);
        foreach (var lease in leases)
        {
            _ = await _writerLeases.ReleaseAsync(
                new ProjectWriterLeaseOwner(
                    ProjectWriterLeaseOwnerKind.AutomationRun,
                    lease.RunId),
                lease.LeaseId,
                cancellationToken);
            await _store.WriteAsync(
                async (connection, transaction, token) =>
                {
                    await ExecuteAsync(
                        connection,
                        transaction,
                        """
                        UPDATE automation_runs
                        SET project_writer_lease_id = NULL,
                            project_writer_lease_expires_utc = NULL
                        WHERE automation_run_id = $runId
                          AND project_writer_lease_id = $leaseId;
                        """,
                        token,
                        ("$runId", lease.RunId.ToString("D")),
                        ("$leaseId", lease.LeaseId.ToString("D")));
                    return 0;
                },
                cancellationToken);
        }
    }

    private Task PersistWriterLeaseAsync(
        Guid runId,
        ProjectWriterLease? lease,
        CancellationToken cancellationToken) =>
        _store.WriteAsync(
            async (connection, transaction, token) =>
            {
                var now = _timeProvider.GetUtcNow();
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    UPDATE automation_runs
                    SET status = CASE
                            WHEN $leaseId IS NULL AND
                                 COALESCE(attention_kind, '') <> 'outcomeUnknown'
                            THEN 'failed'
                            ELSE status
                        END,
                        project_writer_lease_id = $leaseId,
                        project_writer_lease_expires_utc = $expires,
                        attention_kind = CASE
                            WHEN $leaseId IS NULL AND
                                 COALESCE(attention_kind, '') <> 'outcomeUnknown'
                            THEN NULL
                            ELSE attention_kind
                        END,
                        attention_deadline_utc = CASE
                            WHEN $leaseId IS NULL AND
                                 COALESCE(attention_kind, '') <> 'outcomeUnknown'
                            THEN NULL
                            ELSE attention_deadline_utc
                        END,
                        error_code = CASE
                            WHEN $leaseId IS NULL AND
                                 COALESCE(attention_kind, '') <> 'outcomeUnknown'
                            THEN $leaseLost
                            ELSE error_code
                        END,
                        diagnostic = CASE WHEN $leaseId IS NULL
                                          THEN 'Project writer lease was lost.'
                                          ELSE diagnostic END,
                        completed_utc = CASE
                            WHEN $leaseId IS NULL AND
                                 COALESCE(attention_kind, '') <> 'outcomeUnknown'
                            THEN $now
                            ELSE completed_utc
                        END,
                        revision = revision + CASE
                            WHEN $leaseId IS NULL AND
                                 COALESCE(attention_kind, '') <> 'outcomeUnknown'
                            THEN 1
                            ELSE 0
                        END,
                        updated_utc = $now
                    WHERE automation_run_id = $runId
                      AND status IN ('running', 'needsAttention');
                    """,
                    token,
                    ("$leaseId", lease?.LeaseId.ToString("D")),
                    ("$expires", lease is null
                        ? null
                        : Milliseconds(lease.ExpiresAtUtc)),
                    ("$leaseLost", AutomationErrorCodes.LeaseLost),
                    ("$now", Milliseconds(now)),
                    ("$runId", runId.ToString("D")));
                return 0;
            },
            cancellationToken).AsTask();

    private async Task ReleaseWriterLeasesAsync(
        CancellationToken cancellationToken)
    {
        if (_writerLeases is null)
        {
            return;
        }

        var leases = await _store.ReadAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT automation_run_id, project_writer_lease_id
                    FROM automation_runs
                    WHERE status IN ('pending', 'running', 'needsAttention')
                      AND project_writer_lease_id IS NOT NULL
                    ORDER BY automation_run_id;
                    """;
                await using var reader = await command.ExecuteReaderAsync(token);
                var result = new List<(Guid RunId, Guid LeaseId)>();
                while (await reader.ReadAsync(token))
                {
                    result.Add((
                        Guid.Parse(reader.GetString(0)),
                        Guid.Parse(reader.GetString(1))));
                }

                return result;
            },
            cancellationToken);
        foreach (var lease in leases)
        {
            _ = await _writerLeases.ReleaseAsync(
                new ProjectWriterLeaseOwner(
                    ProjectWriterLeaseOwnerKind.AutomationRun,
                    lease.RunId),
                lease.LeaseId,
                cancellationToken);
            await PersistReleasedWriterLeaseAsync(lease.RunId, cancellationToken);
        }
    }

    private Task PersistReleasedWriterLeaseAsync(
        Guid runId,
        CancellationToken cancellationToken) =>
        _store.WriteAsync(
            async (connection, transaction, token) =>
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    UPDATE automation_runs
                    SET project_writer_lease_id = NULL,
                        project_writer_lease_expires_utc = NULL
                    WHERE automation_run_id = $runId
                      AND status IN ('pending', 'running', 'needsAttention');
                    """,
                    token,
                    ("$runId", runId.ToString("D")));
                return 0;
            },
            cancellationToken).AsTask();

    private static string ProcessOwner() =>
        $"{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}:" +
        $"{Guid.NewGuid():N}";

    private static long Milliseconds(DateTimeOffset value) =>
        value.ToUnixTimeMilliseconds();

    private static Guid DerivedId(Guid source, byte marker)
    {
        var bytes = source.ToByteArray();
        bytes[^1] ^= marker;
        return new Guid(bytes);
    }

    private static async ValueTask ExecuteAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            Add(command, parameter.Name, parameter.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private sealed record SessionFact(
        Guid RunId,
        string Status,
        string? ErrorCode,
        string? ErrorMessage,
        Guid? ProjectWriterLeaseId,
        bool HasOutcomeUnknown,
        DateTimeOffset? InteractionTimeoutUtc,
        long AttentionTimeoutMilliseconds);
}
