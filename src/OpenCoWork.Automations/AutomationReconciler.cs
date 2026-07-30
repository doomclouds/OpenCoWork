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
            health)
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
        IModuleHealthReporter? health = null)
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
            await RecoverSessionFactsAsync(cancellationToken);
            await RenewWriterLeasesAsync(cancellationToken);
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
                    SELECT automation_run_id
                    FROM automation_runs
                    WHERE status = 'pending'
                    ORDER BY created_utc, automation_run_id
                    LIMIT $limit;
                    """;
                Add(command, "$limit", _config.MaxConcurrentRuns);
                await using var reader = await command.ExecuteReaderAsync(token);
                var result = new List<Guid>();
                while (await reader.ReadAsync(token))
                {
                    result.Add(Guid.Parse(reader.GetString(0)));
                }

                return result.ToArray();
            },
            cancellationToken);

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
                           r.project_writer_lease_id
                    FROM automation_runs r
                    JOIN turns t ON t.thread_id = r.thread_id
                    WHERE r.status IN ('running', 'needsAttention')
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
                        reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4))));
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
        var target = fact.Status switch
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

        var attention = fact.Status switch
        {
            "waitingApproval" => "approvalRequired",
            "waitingInput" => "userInputRequired",
            _ => null,
        };
        var terminal = target is "completed" or "failed" or "cancelled";
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
                        revision = revision + CASE
                            WHEN status <> $status OR
                                 COALESCE(attention_kind, '') <> COALESCE($attention, '')
                            THEN 1 ELSE 0 END,
                        updated_utc = CASE
                            WHEN status <> $status OR
                                 COALESCE(attention_kind, '') <> COALESCE($attention, '')
                            THEN $now ELSE updated_utc END
                    WHERE automation_run_id = $runId
                      AND status IN ('running', 'needsAttention');
                    """,
                    token,
                    ("$status", target),
                    ("$attention", attention),
                    ("$errorCode", fact.ErrorCode),
                    ("$diagnostic", fact.ErrorMessage),
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
                    SET status = CASE WHEN $leaseId IS NULL THEN 'failed' ELSE status END,
                        project_writer_lease_id = $leaseId,
                        project_writer_lease_expires_utc = $expires,
                        error_code = CASE WHEN $leaseId IS NULL THEN $leaseLost
                                          ELSE error_code END,
                        diagnostic = CASE WHEN $leaseId IS NULL
                                          THEN 'Project writer lease was lost.'
                                          ELSE diagnostic END,
                        completed_utc = CASE WHEN $leaseId IS NULL THEN $now
                                             ELSE completed_utc END,
                        revision = revision + 1,
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
                        project_writer_lease_expires_utc = NULL,
                        revision = revision + 1,
                        updated_utc = $now
                    WHERE automation_run_id = $runId
                      AND status IN ('pending', 'running', 'needsAttention');
                    """,
                    token,
                    ("$now", Milliseconds(_timeProvider.GetUtcNow())),
                    ("$runId", runId.ToString("D")));
                return 0;
            },
            cancellationToken).AsTask();

    private static string ProcessOwner() =>
        $"{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}:" +
        $"{Guid.NewGuid():N}";

    private static long Milliseconds(DateTimeOffset value) =>
        value.ToUnixTimeMilliseconds();

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
        Guid? ProjectWriterLeaseId);
}
