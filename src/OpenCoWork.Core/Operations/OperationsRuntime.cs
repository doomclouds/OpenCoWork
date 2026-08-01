using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.State;
using OpenCoWork.Core.Workspaces;

namespace OpenCoWork.Core.Operations;

internal sealed record OperationsRuntimeHealth(
    string PrimaryHost,
    string RuntimeStatus,
    IReadOnlyList<OperationsModuleHealth> Modules,
    DateTimeOffset? ReconcilerLastSuccessAtUtc = null);

internal sealed class OperationsRuntime
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HeartbeatLifetime = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan InsightInterval = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly StateRuntime _state;
    private readonly OperationsTraceCollector _traces;
    private readonly IWorkspaceRegistryService _registry;
    private readonly IWorkspaceInsightService _insights;
    private readonly OpenCoWorkPaths _paths;
    private readonly TimeProvider _timeProvider;
    private readonly Func<OperationsRuntimeHealth> _health;
    private readonly OperationsChangeHub? _changes;
    private CancellationTokenSource? _lifetime;
    private Task? _heartbeatLoop;
    private Task? _insightLoop;

    internal OperationsRuntime(
        StateRuntime state,
        OperationsTraceCollector traces,
        IWorkspaceRegistryService registry,
        OpenCoWorkPaths paths,
        TimeProvider timeProvider,
        Func<OperationsRuntimeHealth> health,
        IWorkspaceInsightService? insights = null,
        OperationsChangeHub? changes = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _traces = traces ?? throw new ArgumentNullException(nameof(traces));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _health = health ?? throw new ArgumentNullException(nameof(health));
        _insights = insights ?? new WorkspaceInsightService(state, timeProvider);
        _changes = changes;
        RuntimeInstanceId = Guid.CreateVersion7(timeProvider.GetUtcNow());
    }

    public Guid RuntimeInstanceId { get; }
    public bool IsRunning => Volatile.Read(ref _lifetime) is not null;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            return;
        }

        var health = _health();
        if (health.PrimaryHost is not ("app-server" or "gateway"))
        {
            throw new InvalidOperationException(
                "Operations Runtime requires the app-server or gateway primary host.");
        }

        await _traces.StartAsync(cancellationToken);
        try
        {
            var workspaceId = await ReadWorkspaceIdAsync(cancellationToken);
            _ = await _registry.UpsertAsync(
                workspaceId,
                _paths.WorkspaceRoot,
                _paths.OpenCoWorkDirectory,
                new DirectoryInfo(_paths.WorkspaceRoot).Name,
                cancellationToken);
            await ObserveAsync(cancellationToken);
            var lifetime = new CancellationTokenSource();
            _lifetime = lifetime;
            _heartbeatLoop = RunHeartbeatLoopAsync(lifetime.Token);
            _insightLoop = RunInsightLoopAsync(lifetime.Token);
        }
        catch
        {
            await _traces.StopAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var lifetime = Interlocked.Exchange(ref _lifetime, null);
        if (lifetime is null)
        {
            return;
        }

        await WriteHeartbeatAsync(OperationsHealthStatus.Stopping, cancellationToken);
        await lifetime.CancelAsync();
        try
        {
            await Task.WhenAll(_heartbeatLoop!, _insightLoop!).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            lifetime.Dispose();
            _heartbeatLoop = null;
            _insightLoop = null;
        }

        await _traces.StopAsync(cancellationToken);
        await WriteHeartbeatAsync(OperationsHealthStatus.Stopped, cancellationToken);
    }

    internal Task ObserveAsync(CancellationToken cancellationToken = default) =>
        WriteHeartbeatAsync(statusOverride: null, cancellationToken);

    private async Task RunHeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(HeartbeatInterval, _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            await ObserveAsync(cancellationToken);
        }
    }

    private async Task RunInsightLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(InsightInterval, _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            _ = await _insights.RunAsync(InsightRunTrigger.Scheduled, cancellationToken);
        }
    }

    private async Task WriteHeartbeatAsync(
        OperationsHealthStatus? statusOverride,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var health = _health();
        await _state.WriteAsync(
            async (connection, transaction, token) =>
            {
                var counts = await ReadCountsAsync(connection, transaction, token);
                var previousObserved = await ScalarAsync<long?>(
                    connection,
                    transaction,
                    "SELECT observed_utc FROM workspace_heartbeat WHERE id = 1;",
                    token);
                var observedMilliseconds = Math.Max(
                    now.ToUnixTimeMilliseconds(),
                    previousObserved ?? long.MinValue);
                var observed = DateTimeOffset.FromUnixTimeMilliseconds(observedMilliseconds);
                var status = statusOverride ?? Status(health, counts, _traces.DroppedCount);
                var payload = new OperationsHeartbeatPayload(
                    health.RuntimeStatus,
                    health.Modules,
                    counts.ReadyChannels,
                    counts.FaultedChannels,
                    counts.PendingInbound,
                    counts.FailedInbound,
                    counts.DeadLetterInbound,
                    counts.PendingOutbox,
                    counts.FailedOutbox,
                    counts.DeadLetterOutbox,
                    _traces.DroppedCount,
                    SqliteWritable: true,
                    health.ReconcilerLastSuccessAtUtc);
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO workspace_heartbeat (
                        id, runtime_instance_id, primary_host, status, snapshot_json,
                        observed_utc, expires_utc, stopped_utc, revision)
                    VALUES (1, $runtimeId, $primaryHost, $status, $snapshot,
                            $observed, $expires, $stopped, 1)
                    ON CONFLICT (id) DO UPDATE SET
                        runtime_instance_id = excluded.runtime_instance_id,
                        primary_host = excluded.primary_host,
                        status = excluded.status,
                        snapshot_json = excluded.snapshot_json,
                        observed_utc = excluded.observed_utc,
                        expires_utc = excluded.expires_utc,
                        stopped_utc = excluded.stopped_utc,
                        revision = workspace_heartbeat.revision + 1;
                    UPDATE operations_state
                    SET current_revision = current_revision + 1,
                        updated_utc = $observed
                    WHERE id = 1;
                    """;
                Add(command, "$runtimeId", RuntimeInstanceId.ToString("D"));
                Add(command, "$primaryHost", health.PrimaryHost);
                Add(command, "$status", Wire(status));
                Add(command, "$snapshot", JsonSerializer.Serialize(payload, JsonOptions));
                Add(command, "$observed", observedMilliseconds);
                Add(command, "$expires", (observed + HeartbeatLifetime).ToUnixTimeMilliseconds());
                Add(
                    command,
                    "$stopped",
                    status == OperationsHealthStatus.Stopped ? observedMilliseconds : null);
                await command.ExecuteNonQueryAsync(token);
                return true;
            },
            cancellationToken);
        _changes?.Publish(
            OperationsChangeKind.Heartbeat,
            statusOverride == OperationsHealthStatus.Stopped ? "stopped" : "observed",
            RuntimeInstanceId.ToString("D"));
    }

    private async Task<Guid> ReadWorkspaceIdAsync(CancellationToken cancellationToken) =>
        await _state.ReadAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT workspace_id FROM operations_state WHERE id = 1;";
                return Guid.Parse(Convert.ToString(
                    await command.ExecuteScalarAsync(token))!);
            },
            cancellationToken);

    private static async Task<HeartbeatCounts> ReadCountsAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                (SELECT count(*) FROM channels WHERE runtime_status = 'ready'),
                (SELECT count(*) FROM channels WHERE runtime_status = 'faulted'),
                (SELECT count(*) FROM channel_inbound_messages WHERE status = 'pending'),
                (SELECT count(*) FROM channel_inbound_messages WHERE status = 'failed'),
                (SELECT count(*) FROM channel_inbound_messages WHERE status = 'deadLettered'),
                (SELECT count(*) FROM channel_outbox WHERE status = 'pending'),
                (SELECT count(*) FROM channel_outbox WHERE status = 'failed'),
                (SELECT count(*) FROM channel_outbox WHERE status = 'deadLettered');
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        _ = await reader.ReadAsync(cancellationToken);
        return new HeartbeatCounts(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7));
    }

    private static async Task<T?> ScalarAsync<T>(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull
            ? default
            : (T)Convert.ChangeType(
                value,
                Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T),
                System.Globalization.CultureInfo.InvariantCulture);
    }

    private static OperationsHealthStatus Status(
        OperationsRuntimeHealth health,
        HeartbeatCounts counts,
        long dropped) =>
        string.Equals(health.RuntimeStatus, "faulted", StringComparison.OrdinalIgnoreCase)
            ? OperationsHealthStatus.Unhealthy
            : string.Equals(health.RuntimeStatus, "degraded", StringComparison.OrdinalIgnoreCase) ||
              health.Modules.Any(module =>
                  string.Equals(module.Status, "degraded", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(module.Status, "faulted", StringComparison.OrdinalIgnoreCase)) ||
              counts.FaultedChannels != 0 ||
              counts.DeadLetterInbound != 0 ||
              counts.DeadLetterOutbox != 0 ||
              dropped != 0
                ? OperationsHealthStatus.Degraded
                : OperationsHealthStatus.Healthy;

    private static string Wire(OperationsHealthStatus status) => status switch
    {
        OperationsHealthStatus.Healthy => "healthy",
        OperationsHealthStatus.Degraded => "degraded",
        OperationsHealthStatus.Unhealthy => "unhealthy",
        OperationsHealthStatus.Stopping => "stopping",
        OperationsHealthStatus.Stopped => "stopped",
        _ => throw new InvalidOperationException("Stale Heartbeat is query-derived."),
    };

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private sealed record HeartbeatCounts(
        int ReadyChannels,
        int FaultedChannels,
        int PendingInbound,
        int FailedInbound,
        int DeadLetterInbound,
        int PendingOutbox,
        int FailedOutbox,
        int DeadLetterOutbox);
}
