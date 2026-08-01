using System.Data.Common;
using System.Text;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Capabilities;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Workspaces;

namespace OpenCoWork.Core.Gateway;

internal sealed class GatewayChannelRuntime
{
    private readonly IWorkspaceStateStore _state;
    private readonly GatewayConfig _config;
    private readonly CapabilityFileStore _trust;
    private readonly OpenCoWorkPaths _paths;
    private readonly ChannelCredentialService _credentials;
    private readonly TimeProvider _timeProvider;
    private readonly object _leaseGate = new();
    private readonly Dictionary<string, SecretLease> _leases =
        new(StringComparer.Ordinal);

    public GatewayChannelRuntime(
        IWorkspaceStateStore state,
        GatewayConfig config,
        CapabilityFileStore trust,
        OpenCoWorkPaths paths,
        ChannelCredentialService credentials,
        TimeProvider timeProvider)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _trust = trust ?? throw new ArgumentNullException(nameof(trust));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    internal bool HasEnabledChannels => _config.Channels.Any(channel => channel.Enabled);

    internal byte[]? AcquireInboundSecret(string channelId)
    {
        lock (_leaseGate)
        {
            return _leases.TryGetValue(channelId, out var lease)
                ? Encoding.UTF8.GetBytes(lease.Secret!)
                : null;
        }
    }

    internal async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        var decisions = await _trust.LoadTrustDecisionsAsync(cancellationToken);
        var activations = new List<Activation>(_config.Channels.Length);
        try
        {
            foreach (var channel in _config.Channels)
            {
                var digest = GatewayConfig.ComputeChannelSha256(channel);
                var decision = decisions.Decisions.SingleOrDefault(item => item.Matches(
                    _paths.WorkspaceRoot,
                    CapabilitySourceKind.Workspace,
                    $"channel/{channel.Id}",
                    "1",
                    digest));
                var trustStatus = decision?.DeniedScopes.Contains(
                        CapabilityTrustScope.ExternalChannel) == true
                    ? "denied"
                    : decision?.AllowedScopes.Contains(
                        CapabilityTrustScope.ExternalChannel) == true
                        ? "trusted"
                        : "pending";
                SecretLease? lease = null;
                var isNewLease = false;
                var runtimeStatus = channel.Enabled
                    ? trustStatus == "trusted" ? "ready" : "pendingTrust"
                    : "disabled";
                string? diagnostic = null;
                if (runtimeStatus == "ready")
                {
                    lock (_leaseGate)
                    {
                        _leases.TryGetValue(channel.Id, out lease);
                    }

                    if (lease is null)
                    {
                        try
                        {
                            lease = _credentials.Acquire(channel);
                            isNewLease = true;
                        }
                        catch (ChannelServiceException error)
                        {
                            runtimeStatus = "unavailable";
                            diagnostic = error.Code;
                        }
                    }
                }

                activations.Add(new Activation(
                    channel,
                    digest,
                    trustStatus,
                    runtimeStatus,
                    diagnostic,
                    lease,
                    isNewLease));
            }

            var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            await _state.WriteAsync(
                async (connection, transaction, token) =>
                {
                    var changed = 0;
                    foreach (var activation in activations)
                    {
                        await using var command = connection.CreateCommand();
                        command.Transaction = transaction;
                        command.CommandText =
                            """
                            INSERT INTO channels (
                                channel_id, kind, enabled, definition_sha256,
                                trust_status, runtime_status, diagnostic, revision,
                                created_utc, updated_utc)
                            VALUES ($id, $kind, $enabled, $digest, $trust, $runtime,
                                    $diagnostic, 1, $now, $now)
                            ON CONFLICT(channel_id) DO UPDATE SET
                                kind = excluded.kind,
                                enabled = excluded.enabled,
                                definition_sha256 = excluded.definition_sha256,
                                trust_status = excluded.trust_status,
                                runtime_status = excluded.runtime_status,
                                diagnostic = excluded.diagnostic,
                                revision = channels.revision + 1,
                                updated_utc = excluded.updated_utc
                            WHERE channels.kind <> excluded.kind
                               OR channels.enabled <> excluded.enabled
                               OR channels.definition_sha256 <> excluded.definition_sha256
                               OR channels.trust_status <> excluded.trust_status
                               OR channels.runtime_status <> excluded.runtime_status
                               OR COALESCE(channels.diagnostic, '') <>
                                  COALESCE(excluded.diagnostic, '');
                            """;
                        Add(command, "$id", activation.Channel.Id);
                        Add(command, "$kind", activation.Channel.Kind);
                        Add(command, "$enabled", activation.Channel.Enabled ? 1 : 0);
                        Add(command, "$digest", activation.Digest);
                        Add(command, "$trust", activation.TrustStatus);
                        Add(command, "$runtime", activation.RuntimeStatus);
                        Add(
                            command,
                            "$diagnostic",
                            activation.Diagnostic is null
                                ? DBNull.Value
                                : activation.Diagnostic);
                        Add(command, "$now", now);
                        changed += await command.ExecuteNonQueryAsync(token);
                    }

                    await using (var stale = connection.CreateCommand())
                    {
                        stale.Transaction = transaction;
                        var ids = activations
                            .Select((activation, index) => ($"$channel{index}", activation.Channel.Id))
                            .ToArray();
                        stale.CommandText = ids.Length == 0
                            ?
                            """
                            UPDATE channels SET enabled = 0, runtime_status = 'stopped',
                                diagnostic = NULL, revision = revision + 1, updated_utc = $now
                            WHERE enabled <> 0 OR runtime_status <> 'stopped';
                            """
                            :
                            $"""
                            UPDATE channels SET enabled = 0, runtime_status = 'stopped',
                                diagnostic = NULL, revision = revision + 1, updated_utc = $now
                            WHERE channel_id NOT IN ({string.Join(", ", ids.Select(item => item.Item1))})
                              AND (enabled <> 0 OR runtime_status <> 'stopped');
                            """;
                        Add(stale, "$now", now);
                        foreach (var (name, value) in ids)
                        {
                            Add(stale, name, value);
                        }
                        changed += await stale.ExecuteNonQueryAsync(token);
                    }

                    if (changed != 0)
                    {
                        await using var revision = connection.CreateCommand();
                        revision.Transaction = transaction;
                        revision.CommandText =
                            """
                            UPDATE operations_state
                            SET current_revision = current_revision + 1, updated_utc = $now
                            WHERE id = 1;
                            """;
                        Add(revision, "$now", now);
                        await revision.ExecuteNonQueryAsync(token);
                    }

                    return changed;
                },
                cancellationToken);

            lock (_leaseGate)
            {
                var ready = activations
                    .Where(activation => activation.RuntimeStatus == "ready")
                    .ToDictionary(
                        activation => activation.Channel.Id,
                        activation => activation.Lease!,
                        StringComparer.Ordinal);
                foreach (var (id, lease) in _leases.ToArray())
                {
                    if (!ready.ContainsKey(id))
                    {
                        lease.Dispose();
                    }
                }

                _leases.Clear();
                foreach (var (id, lease) in ready)
                {
                    _leases.Add(id, lease);
                }
            }
        }
        catch
        {
            foreach (var activation in activations.Where(item => item.IsNewLease))
            {
                activation.Lease?.Dispose();
            }
            throw;
        }
    }

    internal async Task SetUnavailableAsync(CancellationToken cancellationToken)
    {
        DisposeLeases();
        await SetRuntimeStatusAsync("unavailable", cancellationToken);
    }

    internal async Task StopAsync(CancellationToken cancellationToken)
    {
        DisposeLeases();
        await SetRuntimeStatusAsync("stopped", cancellationToken);
    }

    private async Task SetRuntimeStatusAsync(
        string runtimeStatus,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await _state.WriteAsync(
            async (connection, transaction, token) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    UPDATE channels
                    SET runtime_status = $status, revision = revision + 1,
                        updated_utc = $now
                    WHERE enabled = 1 AND runtime_status <> $status;
                    """;
                Add(command, "$status", runtimeStatus);
                Add(command, "$now", now);
                return await command.ExecuteNonQueryAsync(token);
            },
            cancellationToken);
    }

    private void DisposeLeases()
    {
        lock (_leaseGate)
        {
            foreach (var lease in _leases.Values)
            {
                lease.Dispose();
            }
            _leases.Clear();
        }
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record Activation(
        GatewayChannelConfig Channel,
        string Digest,
        string TrustStatus,
        string RuntimeStatus,
        string? Diagnostic,
        SecretLease? Lease,
        bool IsNewLease);
}
