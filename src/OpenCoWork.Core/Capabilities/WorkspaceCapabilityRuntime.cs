using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Agents;
using OpenCoWork.Core.Tools;

namespace OpenCoWork.Core.Capabilities;

public static class OpenCoWorkCapabilityExtensions
{
    public static IServiceCollection AddOpenCoWorkCapabilityRuntime(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(serviceProvider =>
            new CapabilityPersistencePaths(
                serviceProvider.GetRequiredService<OpenCoWork.Core.Workspaces.OpenCoWorkPaths>(),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        services.TryAddSingleton(serviceProvider =>
            new CapabilityFileStore(
                serviceProvider.GetRequiredService<CapabilityPersistencePaths>()));
        services.TryAddSingleton(serviceProvider =>
            new CoreSourceControlTool(
                serviceProvider.GetRequiredService<
                    OpenCoWork.Core.Workspaces.OpenCoWorkPaths>(),
                serviceProvider.GetRequiredService<CapabilityFileStore>(),
                serviceProvider.GetRequiredService<
                    OpenCoWork.Core.Logging.SecretRedactor>()));
        services.TryAddSingleton(serviceProvider =>
            new SkillCatalog(
                serviceProvider.GetRequiredService<CapabilityPersistencePaths>(),
                serviceProvider.GetRequiredService<CapabilityFileStore>()));
        services.TryAddSingleton(serviceProvider =>
            new PluginPackageStore(
                serviceProvider.GetRequiredService<CapabilityPersistencePaths>()));
        services.TryAddSingleton(serviceProvider =>
            new PluginRuntime(
                serviceProvider.GetRequiredService<CapabilityPersistencePaths>(),
                serviceProvider.GetRequiredService<CapabilityFileStore>(),
                serviceProvider.GetRequiredService<PluginPackageStore>(),
                serviceProvider.GetRequiredService<ToolRuntime>()));
        services.TryAddSingleton(serviceProvider =>
            new WorkspaceProcessHookSource(
                serviceProvider.GetRequiredService<OpenCoWork.Core.Workspaces.OpenCoWorkPaths>(),
                serviceProvider.GetRequiredService<CapabilityFileStore>(),
                serviceProvider.GetRequiredService<OpenCoWork.Core.Logging.SecretRedactor>(),
                serviceProvider.GetService<
                    Microsoft.Extensions.Logging.ILogger<WorkspaceProcessHookSource>>()));
        services.TryAddSingleton(serviceProvider =>
            new CapabilityHookRuntime(
                serviceProvider.GetRequiredService<WorkspaceProcessHookSource>(),
                serviceProvider.GetRequiredService<PluginRuntime>(),
                serviceProvider.GetService<
                    Microsoft.Extensions.Logging.ILogger<CapabilityHookRuntime>>()));
        services.TryAddSingleton(serviceProvider =>
            new DynamicToolRegistry(
                serviceProvider.GetRequiredService<ToolRuntime>(),
                serviceProvider.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton(serviceProvider =>
            new McpCapabilitySource(
                serviceProvider.GetRequiredService<
                    OpenCoWork.Core.Workspaces.OpenCoWorkPaths>(),
                serviceProvider.GetRequiredService<CapabilityFileStore>(),
                serviceProvider.GetRequiredService<ToolRuntime>(),
                serviceProvider.GetRequiredService<ProviderAuthService>()));
        services.TryAddSingleton(serviceProvider =>
            new LspCapabilitySource(
                serviceProvider.GetRequiredService<
                    OpenCoWork.Core.Workspaces.OpenCoWorkPaths>(),
                serviceProvider.GetRequiredService<CapabilityFileStore>(),
                serviceProvider.GetRequiredService<ProviderAuthService>()));
        services.TryAddSingleton(serviceProvider =>
            new WorkspaceCapabilityDiscovery(
                serviceProvider.GetRequiredService<SkillCatalog>(),
                serviceProvider.GetRequiredService<ProviderDeclarationCatalog>(),
                serviceProvider.GetRequiredService<PluginRuntime>(),
                serviceProvider.GetRequiredService<McpCapabilitySource>(),
                serviceProvider.GetRequiredService<LspCapabilitySource>()));
        services.TryAddSingleton(serviceProvider =>
            new WorkspaceCapabilityRuntime(
            [
                WorkspaceCapabilityRuntime.CreateCoreContributions(
                    serviceProvider.GetRequiredService<ToolRuntime>()),
                serviceProvider.GetRequiredService<ProviderRegistry>()
                    .CreateCoreContributions(),
            ],
            serviceProvider.GetRequiredService<WorkspaceCapabilityDiscovery>()));
        services.TryAddSingleton(serviceProvider =>
            new PluginManager(
                serviceProvider.GetRequiredService<PluginPackageStore>(),
                serviceProvider.GetRequiredService<CapabilityFileStore>(),
                serviceProvider.GetRequiredService<WorkspaceCapabilityRuntime>()));
        services.TryAddSingleton<ICapabilityService>(serviceProvider =>
            new WorkspaceCapabilityService(
                serviceProvider.GetRequiredService<WorkspaceCapabilityRuntime>(),
                serviceProvider.GetRequiredService<CapabilityFileStore>(),
                serviceProvider.GetRequiredService<
                    OpenCoWork.Core.Workspaces.OpenCoWorkPaths>(),
                serviceProvider.GetRequiredService<PluginManager>(),
                serviceProvider.GetRequiredService<SkillCatalog>(),
                serviceProvider.GetRequiredService<McpCapabilitySource>(),
                serviceProvider.GetRequiredService<LspCapabilitySource>(),
                serviceProvider.GetRequiredService<ProviderAuthService>(),
                serviceProvider.GetRequiredService<DynamicToolRegistry>(),
                serviceProvider.GetRequiredService<CoreSourceControlTool>(),
                serviceProvider.GetRequiredService<BackgroundTerminalRuntime>(),
                serviceProvider.GetRequiredService<WorkspaceMemoryRuntime>()));
        return services;
    }
}

public sealed class WorkspaceCapabilityRuntime
{
    private const int CatalogSchemaVersion = 1;
    private readonly CapabilityContributionSet[] _coreContributions;
    private readonly WorkspaceCapabilityDiscovery? _discovery;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly object _leaseGate = new();
    private readonly Dictionary<long, int> _activeLeases = [];
    private CapabilityCatalog _catalog;
    private EffectiveSkillSnapshot _skills = EffectiveSkillSnapshot.Empty;
    private int _status;

    internal WorkspaceCapabilityRuntime(
        IEnumerable<CapabilityContributionSet> coreContributions,
        WorkspaceCapabilityDiscovery? discovery = null)
    {
        ArgumentNullException.ThrowIfNull(coreContributions);
        _coreContributions = coreContributions.ToArray();
        _discovery = discovery;
        _discovery?.SetRefresh(async cancellationToken =>
        {
            if (Status is CapabilityRuntimeState.Ready or
                CapabilityRuntimeState.Degraded)
            {
                _ = await RefreshDiscoveredAsync(cancellationToken);
            }
        });
        if (_coreContributions.Any(set =>
                set.Source.Kind != CapabilitySourceKind.Core))
        {
            throw InvalidDefinition(
                "Core capability contributions must use the Core source kind.");
        }

        _catalog = CreateCatalog(
            revision: 0,
            CapabilityRuntimeState.Stopped,
            []);
    }

    public CapabilityRuntimeState Status =>
        (CapabilityRuntimeState)Volatile.Read(ref _status);

    public event EventHandler<CapabilityCatalogChangedEventArgs>? CatalogChanged;

    public CapabilityCatalog CurrentCatalog => Volatile.Read(ref _catalog);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            EnsureStatus(CapabilityRuntimeState.Stopped, "start");
            SetStatus(CapabilityRuntimeState.Starting);
            try
            {
                var discovered = _discovery is null
                    ? new WorkspaceCapabilityDiscoveryResult(
                        [],
                        EffectiveSkillSnapshot.Empty)
                    : await _discovery.DiscoverAsync(cancellationToken);
                var candidate = BuildCandidate(
                    _coreContributions.Concat(discovered.Contributions));
                Publish(
                    candidate.IsDegraded
                        ? CapabilityRuntimeState.Degraded
                        : CapabilityRuntimeState.Ready,
                    candidate.Items,
                    discovered.Skills);
            }
            catch (OperationCanceledException)
            {
                SetStatus(CapabilityRuntimeState.Stopped);
                throw;
            }
            catch
            {
                Publish(CapabilityRuntimeState.Faulted, []);
                throw;
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    internal async Task RefreshAsync(
        IEnumerable<CapabilityContributionSet> contributions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contributions);
        var external = contributions.ToArray();
        if (external.Any(set => set.Source.Kind is
                CapabilitySourceKind.Core or CapabilitySourceKind.Conflict))
        {
            throw InvalidDefinition(
                "Refresh contributions must come from a concrete external source.");
        }

        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            EnsureAvailable("refresh");
            var candidate = BuildCandidate(_coreContributions.Concat(external));
            Publish(
                candidate.IsDegraded
                    ? CapabilityRuntimeState.Degraded
                    : CapabilityRuntimeState.Ready,
                candidate.Items,
                _skills);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    internal async Task<CapabilityCatalog> RefreshDiscoveredAsync(
        CancellationToken cancellationToken = default)
    {
        if (_discovery is null)
        {
            throw Unavailable("Capability discovery is unavailable.");
        }

        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            EnsureAvailable("refresh discovery");
            var discovered = await _discovery.DiscoverAsync(cancellationToken);
            var candidate = BuildCandidate(
                _coreContributions.Concat(discovered.Contributions));
            Publish(
                candidate.IsDegraded
                    ? CapabilityRuntimeState.Degraded
                    : CapabilityRuntimeState.Ready,
                candidate.Items,
                discovered.Skills);
            return CurrentCatalog;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (Status == CapabilityRuntimeState.Stopped)
            {
                return;
            }

            if (Status is not (
                CapabilityRuntimeState.Ready or
                CapabilityRuntimeState.Degraded or
                CapabilityRuntimeState.Faulted))
            {
                throw Unavailable($"Capability runtime cannot stop while it is {Status}.");
            }

            SetStatus(CapabilityRuntimeState.Stopping);
            try
            {
                if (_discovery is not null)
                {
                    await _discovery.StopAsync(cancellationToken);
                }

                Publish(CapabilityRuntimeState.Stopped, CurrentCatalog.Items);
            }
            catch
            {
                SetStatus(CapabilityRuntimeState.Faulted);
                throw;
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public CapabilitySnapshotLease AcquireSnapshot()
    {
        lock (_leaseGate)
        {
            EnsureAvailable("acquire a snapshot");
            var catalog = CurrentCatalog;
            _activeLeases.TryGetValue(catalog.Revision, out var count);
            _activeLeases[catalog.Revision] = checked(count + 1);
            return new CapabilitySnapshotLease(
                catalog,
                _skills,
                () => ReleaseLease(catalog.Revision));
        }
    }

    internal int ActiveLeaseCount(long revision)
    {
        lock (_leaseGate)
        {
            return _activeLeases.GetValueOrDefault(revision);
        }
    }

    internal IDisposable? AcquirePluginSnapshot(EffectiveToolSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        try
        {
            return _discovery?.AcquirePluginSnapshot(snapshot);
        }
        catch (PluginPackageException exception)
        {
            throw Unavailable(exception.Message);
        }
    }

    internal static CapabilityContributionSet CreateCoreContributions(
        ToolRuntime tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var registrations = tools.Registrations
            .OrderBy(
                registration => registration.Definition.Id.SourceToolId,
                StringComparer.Ordinal)
            .ToArray();
        var digest = HashCoreRegistrations(registrations);
        var source = new CapabilitySourceDescriptor(
            CapabilitySourceKind.Core,
            "opencowork.core",
            "1",
            digest);
        return new CapabilityContributionSet(
            source,
            registrations.Select(registration =>
                new CapabilityContribution(
                    CapabilityKind.Tool,
                    registration.Definition.Id.SourceToolId,
                    $"{registration.Definition.Name.Namespace}." +
                    registration.Definition.Name.Name,
                    registration.Definition.Description,
                    CapabilityStatus.Ready,
                    [],
                    registration.BindingGeneration,
                    [])));
    }

    private static CandidateCatalog BuildCandidate(
        IEnumerable<CapabilityContributionSet> contributionSets)
    {
        var contributed = contributionSets
            .SelectMany(set => set.Items.Select(item => new CandidateItem(set.Source, item)))
            .ToArray();
        var items = new List<CapabilityCatalogItem>();
        var degraded = false;

        foreach (var group in contributed
                     .GroupBy(item => (item.Contribution.Kind, item.Contribution.Id))
                     .OrderBy(group => group.Key.Kind)
                     .ThenBy(group => group.Key.Id, StringComparer.Ordinal))
        {
            var ordered = group
                .OrderBy(item => item.Source.Kind)
                .ThenBy(item => item.Source.Id, StringComparer.Ordinal)
                .ThenBy(item => item.Source.Version, StringComparer.Ordinal)
                .ThenBy(item => item.Source.Sha256, StringComparer.Ordinal)
                .ToArray();
            var core = ordered
                .Where(item => item.Source.Kind == CapabilitySourceKind.Core)
                .ToArray();
            if (core.Length > 1)
            {
                throw InvalidDefinition(
                    $"Core capability '{group.Key.Kind}/{group.Key.Id}' is duplicated.");
            }

            if (core.Length == 1)
            {
                var externalSources = OrderedSources(
                    ordered
                        .Where(item => item.Source.Kind != CapabilitySourceKind.Core)
                        .Select(item => item.Source));
                var diagnostics = OrderedDiagnostics(
                    core[0].Contribution.DiagnosticCodes.Concat(
                        externalSources.Count == 0
                            ? []
                            : [CapabilityErrorCodes.Conflict]));
                items.Add(CreateItem(
                    core[0],
                    diagnostics,
                    externalSources));
                degraded |= externalSources.Count != 0 ||
                            IsDegraded(core[0].Contribution.Status);
                continue;
            }

            if (ordered.Length == 1)
            {
                items.Add(CreateItem(
                    ordered[0],
                    OrderedDiagnostics(ordered[0].Contribution.DiagnosticCodes),
                    []));
                degraded |= IsDegraded(ordered[0].Contribution.Status);
                continue;
            }

            var conflictingSources = OrderedSources(ordered.Select(item => item.Source));
            var selected = ordered[0].Contribution;
            var conflictSource = new CapabilitySourceDescriptor(
                CapabilitySourceKind.Conflict,
                $"conflict.{selected.Kind}.{selected.Id}",
                "1",
                HashSources(conflictingSources));
            items.Add(new CapabilityCatalogItem(
                selected.Kind,
                selected.Id,
                selected.Id,
                "Multiple sources contribute the same capability.",
                conflictSource,
                CapabilityStatus.Conflict,
                ordered
                    .SelectMany(item => item.Contribution.RequiredTrustScopes)
                    .Distinct()
                    .Order()
                    .ToArray(),
                ordered.Max(item => item.Contribution.Generation),
                OrderedDiagnostics(
                    ordered
                        .SelectMany(item => item.Contribution.DiagnosticCodes)
                        .Append(CapabilityErrorCodes.Conflict)),
                conflictingSources));
            degraded = true;
        }

        return new CandidateCatalog(
            Array.AsReadOnly(items.ToArray()),
            degraded);
    }

    private static CapabilityCatalogItem CreateItem(
        CandidateItem candidate,
        IReadOnlyList<string> diagnostics,
        IReadOnlyList<CapabilitySourceDescriptor> conflictingSources) =>
        new(
            candidate.Contribution.Kind,
            candidate.Contribution.Id,
            candidate.Contribution.DisplayName,
            candidate.Contribution.Description,
            candidate.Source,
            candidate.Contribution.Status,
            candidate.Contribution.RequiredTrustScopes
                .Order()
                .ToArray(),
            candidate.Contribution.Generation,
            diagnostics,
            conflictingSources);

    private void Publish(
        CapabilityRuntimeState runtimeState,
        IReadOnlyList<CapabilityCatalogItem> items,
        EffectiveSkillSnapshot? skills = null)
    {
        var hash = HashCatalog(runtimeState, items);
        CapabilityCatalog? changed = null;
        lock (_leaseGate)
        {
            var previous = CurrentCatalog;
            var revision = string.Equals(
                previous.CatalogSha256,
                hash,
                StringComparison.Ordinal)
                ? previous.Revision
                : checked(previous.Revision + 1);
            var catalog = new CapabilityCatalog(
                CatalogSchemaVersion,
                revision,
                hash,
                runtimeState,
                items);
            _skills = skills ?? _skills;
            Volatile.Write(ref _catalog, catalog);
            Volatile.Write(ref _status, (int)runtimeState);
            if (catalog.Revision != previous.Revision)
            {
                changed = catalog;
            }
        }

        if (changed is not null)
        {
            CatalogChanged?.Invoke(
                this,
                new CapabilityCatalogChangedEventArgs(
                    changed.Revision,
                    changed.RuntimeState));
        }
    }

    private void ReleaseLease(long revision)
    {
        lock (_leaseGate)
        {
            if (!_activeLeases.TryGetValue(revision, out var count))
            {
                return;
            }

            if (count == 1)
            {
                _activeLeases.Remove(revision);
            }
            else
            {
                _activeLeases[revision] = count - 1;
            }
        }
    }

    private void EnsureAvailable(string operation)
    {
        if (Status is not (
            CapabilityRuntimeState.Ready or CapabilityRuntimeState.Degraded))
        {
            throw Unavailable(
                $"Capability runtime cannot {operation} while it is {Status}.");
        }
    }

    private void EnsureStatus(CapabilityRuntimeState expected, string operation)
    {
        if (Status != expected)
        {
            throw Unavailable(
                $"Capability runtime cannot {operation} while it is {Status}.");
        }
    }

    private void SetStatus(CapabilityRuntimeState status)
    {
        lock (_leaseGate)
        {
            Volatile.Write(ref _status, (int)status);
        }
    }

    private static bool IsDegraded(CapabilityStatus status) =>
        status is
            CapabilityStatus.Unavailable or
            CapabilityStatus.Disconnected or
            CapabilityStatus.Faulted or
            CapabilityStatus.Conflict;

    private static IReadOnlyList<string> OrderedDiagnostics(
        IEnumerable<string> diagnostics) =>
        Array.AsReadOnly(diagnostics
            .Distinct(StringComparer.Ordinal)
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToArray());

    private static IReadOnlyList<CapabilitySourceDescriptor> OrderedSources(
        IEnumerable<CapabilitySourceDescriptor> sources) =>
        Array.AsReadOnly(sources
            .Distinct()
            .OrderBy(source => source.Kind)
            .ThenBy(source => source.Id, StringComparer.Ordinal)
            .ThenBy(source => source.Version, StringComparer.Ordinal)
            .ThenBy(source => source.Sha256, StringComparer.Ordinal)
            .ToArray());

    private static CapabilityCatalog CreateCatalog(
        long revision,
        CapabilityRuntimeState runtimeState,
        IReadOnlyList<CapabilityCatalogItem> items) =>
        new(
            CatalogSchemaVersion,
            revision,
            HashCatalog(runtimeState, items),
            runtimeState,
            items);

    private static string HashCatalog(
        CapabilityRuntimeState runtimeState,
        IReadOnlyList<CapabilityCatalogItem> items) =>
        HashJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", CatalogSchemaVersion);
            writer.WriteString("runtimeState", runtimeState.ToString());
            writer.WriteStartArray("items");
            foreach (var item in items)
            {
                writer.WriteStartObject();
                writer.WriteString("kind", item.Kind.ToString());
                writer.WriteString("id", item.Id);
                writer.WriteString("displayName", item.DisplayName);
                writer.WriteString("description", item.Description);
                WriteSource(writer, "source", item.Source);
                writer.WriteString("status", item.Status.ToString());
                writer.WriteNumber("generation", item.Generation);
                writer.WriteStartArray("requiredTrustScopes");
                foreach (var scope in item.RequiredTrustScopes.Order())
                {
                    writer.WriteStringValue(scope.ToString());
                }

                writer.WriteEndArray();
                writer.WriteStartArray("diagnosticCodes");
                foreach (var code in item.DiagnosticCodes.Order(StringComparer.Ordinal))
                {
                    writer.WriteStringValue(code);
                }

                writer.WriteEndArray();
                writer.WriteStartArray("conflictingSources");
                foreach (var source in OrderedSources(item.ConflictingSources))
                {
                    writer.WriteStartObject();
                    WriteSourceProperties(writer, source);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });

    private static string HashSources(
        IReadOnlyList<CapabilitySourceDescriptor> sources) =>
        HashJson(writer =>
        {
            writer.WriteStartArray();
            foreach (var source in sources)
            {
                writer.WriteStartObject();
                WriteSourceProperties(writer, source);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        });

    private static string HashCoreRegistrations(
        IReadOnlyList<ToolRegistration> registrations) =>
        HashJson(writer =>
        {
            writer.WriteStartArray();
            foreach (var registration in registrations)
            {
                var definition = registration.Definition;
                writer.WriteStartObject();
                writer.WriteString("id", definition.Id.SourceToolId);
                writer.WriteString("namespace", definition.Name.Namespace);
                writer.WriteString("name", definition.Name.Name);
                writer.WriteString("description", definition.Description);
                writer.WritePropertyName("inputSchema");
                definition.InputSchema.WriteTo(writer);
                writer.WriteNumber("effects", (int)definition.Effects);
                writer.WriteString("replaySafety", definition.ReplaySafety.ToString());
                writer.WriteString("bindingId", registration.RuntimeBindingId.Value);
                writer.WriteString("exposure", registration.Exposure.ToString());
                writer.WriteNumber("audience", (int)registration.Audience);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        });

    private static string HashJson(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            write(writer);
        }

        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(
                0,
                checked((int)stream.Length))))
            .ToLowerInvariant();
    }

    private static void WriteSource(
        Utf8JsonWriter writer,
        string propertyName,
        CapabilitySourceDescriptor source)
    {
        writer.WriteStartObject(propertyName);
        WriteSourceProperties(writer, source);
        writer.WriteEndObject();
    }

    private static void WriteSourceProperties(
        Utf8JsonWriter writer,
        CapabilitySourceDescriptor source)
    {
        writer.WriteString("kind", source.Kind.ToString());
        writer.WriteString("id", source.Id);
        writer.WriteString("version", source.Version);
        writer.WriteString("sha256", source.Sha256);
    }

    private static CapabilityRuntimeException InvalidDefinition(string message) =>
        new(CapabilityErrorCodes.DefinitionInvalid, message);

    private static CapabilityRuntimeException Unavailable(string message) =>
        new(CapabilityErrorCodes.RuntimeUnavailable, message);

    private sealed record CandidateItem(
        CapabilitySourceDescriptor Source,
        CapabilityContribution Contribution);

    private sealed record CandidateCatalog(
        IReadOnlyList<CapabilityCatalogItem> Items,
        bool IsDegraded);
}

public sealed class CapabilitySnapshotLease : IDisposable
{
    private Action? _release;

    internal CapabilitySnapshotLease(
        CapabilityCatalog catalog,
        EffectiveSkillSnapshot skills,
        Action release)
    {
        Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        Skills = skills ?? throw new ArgumentNullException(nameof(skills));
        _release = release ?? throw new ArgumentNullException(nameof(release));
    }

    public CapabilityCatalog Catalog { get; }

    public EffectiveSkillSnapshot Skills { get; }

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

public sealed class CapabilityRuntimeException(string code, string message)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
