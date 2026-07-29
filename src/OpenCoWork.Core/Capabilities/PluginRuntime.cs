using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Tools;

namespace OpenCoWork.Core.Capabilities;

internal sealed record PluginDiscoveryResult(
    IReadOnlyList<CapabilityContributionSet> Contributions);

internal sealed partial class PluginRuntime
{
    private const int MaximumToolFileBytes = 1024 * 1024;
    private readonly CapabilityFileStore _files;
    private readonly object _gate = new();
    private readonly Dictionary<string, PluginVersion> _active =
        new(StringComparer.Ordinal);
    private readonly List<PluginVersion> _retired = [];
    private readonly CapabilityPersistencePaths _paths;
    private readonly PluginPackageStore _store;
    private readonly ToolRuntime _tools;

    public PluginRuntime(
        CapabilityPersistencePaths paths,
        CapabilityFileStore files,
        PluginPackageStore store,
        ToolRuntime tools)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
    }

    public async Task<PluginDiscoveryResult> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        var pluginLock = await _files.LoadPluginLockAsync(cancellationToken);
        var trust = await _files.LoadTrustDecisionsAsync(cancellationToken);
        var userOverrides = await _files.LoadUserOverridesAsync(cancellationToken);
        var contributions = new List<CapabilityContributionSet>();
        var retained = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in pluginLock.Plugins.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            retained.Add(entry.Id);
            try
            {
                var package = await _store.OpenStoredAsync(
                    entry.Sha256,
                    cancellationToken);
                if (!string.Equals(package.Manifest.Id, entry.Id, StringComparison.Ordinal) ||
                    !string.Equals(
                        package.Manifest.Version,
                        entry.Version,
                        StringComparison.Ordinal))
                {
                    throw LoadFailed();
                }

                var requiredTrust = RequiredTrust(package.Manifest);
                var status = !entry.Enabled ||
                             userOverrides.IsDisabled(CapabilityKind.Plugin, entry.Id)
                    ? CapabilityStatus.Disabled
                    : IsTrusted(
                        trust,
                        package.Manifest,
                        package.ContentSha256,
                        requiredTrust)
                        ? CapabilityStatus.Ready
                        : CapabilityStatus.PendingTrust;
                IReadOnlyList<PluginToolDeclaration> declarations =
                    await ReadToolsAsync(package, cancellationToken);
                if (status == CapabilityStatus.Ready)
                {
                    await ActivateAsync(package, declarations, cancellationToken);
                    if (IsFaulted(entry.Id))
                    {
                        status = CapabilityStatus.Faulted;
                    }
                }
                else
                {
                    await RetireActiveAsync(entry.Id, cancellationToken);
                }

                contributions.Add(ContributionSet(
                    package,
                    status,
                    requiredTrust,
                    declarations,
                    status == CapabilityStatus.PendingTrust
                        ? [ToolErrorCodes.TrustRequired]
                        : status == CapabilityStatus.Faulted
                            ? [PluginErrorCodes.UnloadFailed]
                            : []));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                await RetireActiveAsync(entry.Id, cancellationToken);
                contributions.Add(FaultedContribution(entry));
            }
        }

        string[] removed;
        lock (_gate)
        {
            removed = _active.Keys
                .Where(id => !retained.Contains(id))
                .ToArray();
        }

        foreach (var pluginId in removed)
        {
            await RetireActiveAsync(pluginId, cancellationToken);
        }

        return new PluginDiscoveryResult(
            Array.AsReadOnly(contributions.ToArray()));
    }

    public IDisposable AcquireSnapshotLease(EffectiveToolSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var keys = snapshot.Registrations
            .Where(registration =>
                registration.Definition.Id.SourceKind == ToolSourceKind.PluginNative)
            .Select(registration => (
                registration.Definition.Id.SourceId,
                registration.RuntimeBindingId,
                registration.BindingGeneration))
            .Distinct()
            .ToArray();
        var versions = new List<PluginVersion>();
        lock (_gate)
        {
            foreach (var (pluginId, bindingId, generation) in keys)
            {
                var version = _active.Values
                    .Concat(_retired)
                    .FirstOrDefault(candidate =>
                        string.Equals(candidate.Manifest.Id, pluginId, StringComparison.Ordinal) &&
                        candidate.Generation == generation &&
                        candidate.Registrations.Any(registration =>
                            registration.RuntimeBindingId == bindingId &&
                            registration.BindingGeneration == generation));
                if (version is null)
                {
                    throw new PluginPackageException(
                        PluginErrorCodes.LoadFailed,
                        "The frozen Plugin version is unavailable.");
                }

                if (versions.Contains(version))
                {
                    continue;
                }

                version.SnapshotLeases = checked(version.SnapshotLeases + 1);
                versions.Add(version);
            }
        }

        return new PluginSnapshotLease(this, versions);
    }

    public WeakReference GetLoadContextReference(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        lock (_gate)
        {
            return _active.TryGetValue(pluginId, out var version)
                ? version.ContextReference
                : throw new KeyNotFoundException("Plugin is not active.");
        }
    }

    public Task RemoveAsync(
        string pluginId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return RetireActiveAsync(pluginId, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        string[] active;
        lock (_gate)
        {
            active = _active.Keys.ToArray();
        }

        foreach (var pluginId in active)
        {
            await RetireActiveAsync(pluginId, cancellationToken);
        }

        Task[] unloads;
        lock (_gate)
        {
            if (_retired.Any(version =>
                    version.SnapshotLeases != 0 || version.ActiveCalls != 0))
            {
                throw new PluginPackageException(
                    PluginErrorCodes.UnloadFailed,
                    "Plugin still owns active leases or calls.");
            }

            unloads = _retired
                .Select(version => version.UnloadTask)
                .Where(task => task is not null)
                .Cast<Task>()
                .ToArray();
        }

        await Task.WhenAll(unloads).WaitAsync(cancellationToken);
    }

    private async Task ActivateAsync(
        StoredPluginPackage package,
        IReadOnlyList<PluginToolDeclaration> declarations,
        CancellationToken cancellationToken)
    {
        PluginVersion? current;
        lock (_gate)
        {
            current = _active.GetValueOrDefault(package.Manifest.Id);
            if (current is not null &&
                string.Equals(
                    current.ContentSha256,
                    package.ContentSha256,
                    StringComparison.Ordinal))
            {
                return;
            }

            var retained = _retired.FirstOrDefault(version =>
                version.UnloadTask is null &&
                string.Equals(
                    version.Manifest.Id,
                    package.Manifest.Id,
                    StringComparison.Ordinal) &&
                string.Equals(
                    version.ContentSha256,
                    package.ContentSha256,
                    StringComparison.Ordinal));
            if (current is null && retained is not null)
            {
                retained.Retired = false;
                _retired.Remove(retained);
                _tools.PublishPlugin(
                    retained.Manifest.Id,
                    retained.Registrations,
                    retained.Registrations.Select(registration =>
                        retained.Bindings[registration.RuntimeBindingId]).ToArray());
                _active.Add(retained.Manifest.Id, retained);
                return;
            }
        }

        if (current is not null)
        {
            await RetireActiveAsync(package.Manifest.Id, cancellationToken);
            lock (_gate)
            {
                if (current.SnapshotLeases != 0 || current.ActiveCalls != 0)
                {
                    throw new PluginPackageException(
                        PluginErrorCodes.UnloadFailed,
                        "The previous Plugin version is still leased.");
                }
            }
        }

        if (declarations.Count == 0)
        {
            lock (_gate)
            {
                _active[package.Manifest.Id] = PluginVersion.Declarative(package);
            }

            return;
        }

        var entry = package.Manifest.EntryPoint ?? throw LoadFailed();
        var assemblyPath = Path.GetFullPath(
            entry.Assembly.Replace('/', Path.DirectorySeparatorChar),
            package.PackageDirectory);
        var loadContext = new PluginLoadContext(assemblyPath, package.PackageDirectory);
        PluginVersion? version = null;
        try
        {
            var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
            var type = assembly.GetType(entry.Type, throwOnError: true, ignoreCase: false)
                ?? throw LoadFailed();
            if (!typeof(IOpenCoWorkPlugin).IsAssignableFrom(type) ||
                type.IsAbstract ||
                type.GetConstructor(Type.EmptyTypes) is null ||
                Activator.CreateInstance(type) is not IOpenCoWorkPlugin plugin)
            {
                throw LoadFailed();
            }

            var executors = plugin.ToolExecutors;
            if (executors is null ||
                executors.Any(pair =>
                    string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null) ||
                declarations.Any(declaration =>
                    !executors.ContainsKey(declaration.Executor)))
            {
                throw LoadFailed();
            }

            var generation = Generation(package.ContentSha256);
            version = new PluginVersion(
                package.Manifest,
                package.ContentSha256,
                generation,
                loadContext,
                plugin);
            var registrations = declarations
                .Select(declaration =>
                    CreateRegistration(version, declaration, executors[declaration.Executor]))
                .ToArray();
            version.Registrations = registrations;
            var bindings = registrations
                .Select(registration => version.Bindings[registration.RuntimeBindingId])
                .ToArray();
            _tools.PublishPlugin(
                package.Manifest.Id,
                registrations,
                bindings);
            lock (_gate)
            {
                _active.Add(package.Manifest.Id, version);
            }
        }
        catch
        {
            if (version is not null)
            {
                await StopPluginAsync(version.Plugin!);
                version.ClearStrongReferences();
            }

            loadContext.Unload();
            throw;
        }
    }

    private ToolRegistration CreateRegistration(
        PluginVersion version,
        PluginToolDeclaration declaration,
        ToolExecutor executor)
    {
        var bindingId = new RuntimeBindingId(
            $"plugin:{version.Manifest.Id}:{version.ContentSha256}:{declaration.Executor}");
        var binding = new ToolRuntimeBinding(
            bindingId,
            ToolBindingAvailability.Available,
            Lease: null,
            declaration.DefaultTimeout,
            (arguments, cancellationToken) =>
                InvokeAsync(version, executor, arguments, cancellationToken),
            version.Generation,
            IsTrusted: true);
        version.Bindings.Add(bindingId, binding);
        return new ToolRegistration(
            new ToolDefinition(
                new ToolDefinitionId(
                    ToolSourceKind.PluginNative,
                    version.Manifest.Id,
                    declaration.Id),
                new ToolName(Namespace(version.Manifest.Id), declaration.Id),
                declaration.Description,
                declaration.InputSchema,
                declaration.Effects,
                declaration.ReplaySafety),
            bindingId,
            declaration.Exposure,
            declaration.Audience,
            version.Generation);
    }

    private async ValueTask<ToolBindingResult> InvokeAsync(
        PluginVersion version,
        ToolExecutor executor,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (version.Faulted)
            {
                throw new PluginPackageException(
                    PluginErrorCodes.UnloadFailed,
                    "Plugin execution is faulted.");
            }

            version.ActiveCalls = checked(version.ActiveCalls + 1);
        }

        var releaseOnExit = true;
        try
        {
            var call = executor(arguments, cancellationToken).AsTask();
            try
            {
                return await call.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (!call.IsCompleted)
            {
                releaseOnExit = false;
                FaultIgnoredCancellation(version);
                _ = ReleaseCallWhenCompletedAsync(version, call);
                throw;
            }
        }
        finally
        {
            if (releaseOnExit)
            {
                ReleaseCall(version);
            }
        }
    }

    private void FaultIgnoredCancellation(PluginVersion version)
    {
        lock (_gate)
        {
            version.Faulted = true;
            _tools.RemovePlugin(version.Manifest.Id);
        }
    }

    private async Task ReleaseCallWhenCompletedAsync(
        PluginVersion version,
        Task call)
    {
        try
        {
            await call;
        }
        catch
        {
        }
        finally
        {
            ReleaseCall(version);
        }
    }

    private async Task RetireActiveAsync(
        string pluginId,
        CancellationToken cancellationToken)
    {
        PluginVersion? version;
        lock (_gate)
        {
            if (!_active.Remove(pluginId, out version))
            {
                return;
            }

            version.Retired = true;
            _retired.Add(version);
            _tools.RemovePlugin(pluginId);
            StartUnloadIfReady(version);
        }

        if (version.UnloadTask is not null)
        {
            await version.UnloadTask.WaitAsync(cancellationToken);
        }
    }

    private void ReleaseSnapshot(IReadOnlyList<PluginVersion> versions)
    {
        lock (_gate)
        {
            foreach (var version in versions)
            {
                version.SnapshotLeases--;
                StartUnloadIfReady(version);
            }
        }
    }

    private void ReleaseCall(PluginVersion version)
    {
        lock (_gate)
        {
            version.ActiveCalls--;
            StartUnloadIfReady(version);
        }
    }

    private void StartUnloadIfReady(PluginVersion version)
    {
        if (!version.Retired ||
            version.SnapshotLeases != 0 ||
            version.ActiveCalls != 0 ||
            version.UnloadTask is not null)
        {
            return;
        }

        version.UnloadTask = UnloadAsync(version);
    }

    private async Task UnloadAsync(PluginVersion version)
    {
        if (version.Plugin is not null)
        {
            try
            {
                await StopPluginAsync(version.Plugin);
            }
            catch
            {
                version.Faulted = true;
                throw;
            }
        }

        foreach (var registration in version.Registrations)
        {
            _tools.RemoveBinding(
                registration.RuntimeBindingId,
                registration.BindingGeneration);
        }

        version.Unload();
    }

    private static async Task StopPluginAsync(IOpenCoWorkPlugin plugin)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await plugin.StopAsync(deadline.Token)
                .AsTask()
                .WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            throw new PluginPackageException(
                PluginErrorCodes.UnloadFailed,
                "Plugin stop timed out.");
        }
    }

    private bool IsFaulted(string pluginId)
    {
        lock (_gate)
        {
            return _active.TryGetValue(pluginId, out var version) &&
                   version.Faulted;
        }
    }

    private bool IsTrusted(
        TrustDecisionsDocument trust,
        PluginManifest manifest,
        string contentSha256,
        IReadOnlyList<CapabilityTrustScope> requiredScopes)
    {
        if (requiredScopes.Count == 0)
        {
            return true;
        }

        var decision = trust.Decisions.SingleOrDefault(item =>
            item.Matches(
                _paths.WorkspacePaths.WorkspaceRoot,
                CapabilitySourceKind.Plugin,
                manifest.Id,
                manifest.Version,
                contentSha256));
        return decision is not null &&
               requiredScopes.All(scope => decision.AllowedScopes.Contains(scope)) &&
               requiredScopes.All(scope => !decision.DeniedScopes.Contains(scope));
    }

    private static IReadOnlyList<CapabilityTrustScope> RequiredTrust(
        PluginManifest manifest)
    {
        var scopes = new List<CapabilityTrustScope>();
        if (manifest.Contributions.Skills.Count != 0)
        {
            scopes.Add(CapabilityTrustScope.PromptContribution);
        }

        if (manifest.EntryPoint is not null)
        {
            scopes.Add(CapabilityTrustScope.InProcessCode);
        }

        if (manifest.Contributions.McpServers.Count != 0 ||
            manifest.Contributions.LspServers.Count != 0)
        {
            scopes.Add(CapabilityTrustScope.OutOfProcess);
        }

        if (manifest.Contributions.Hooks.Count != 0)
        {
            scopes.Add(CapabilityTrustScope.TrustedHook);
        }

        return Array.AsReadOnly(scopes.Distinct().Order().ToArray());
    }

    private static CapabilityContributionSet ContributionSet(
        StoredPluginPackage package,
        CapabilityStatus status,
        IReadOnlyList<CapabilityTrustScope> requiredTrust,
        IReadOnlyList<PluginToolDeclaration> tools,
        IReadOnlyList<string> diagnostics)
    {
        var source = new CapabilitySourceDescriptor(
            CapabilitySourceKind.Plugin,
            package.Manifest.Id,
            package.Manifest.Version,
            package.ContentSha256);
        var items = new List<CapabilityContribution>
        {
            new(
                CapabilityKind.Plugin,
                package.Manifest.Id,
                package.Manifest.DisplayName,
                "Installed OpenCoWork Plugin.",
                status,
                requiredTrust,
                generation: 1,
                diagnostics),
        };
        items.AddRange(tools.Select(tool => new CapabilityContribution(
            CapabilityKind.Tool,
            $"{package.Manifest.Id}/{tool.Id}",
            tool.Id,
            tool.Description,
            status,
            requiredTrust,
            generation: Generation(package.ContentSha256),
            diagnostics)));
        return new CapabilityContributionSet(source, items);
    }

    private static CapabilityContributionSet FaultedContribution(PluginLockEntry entry) =>
        new(
            new CapabilitySourceDescriptor(
                CapabilitySourceKind.Plugin,
                entry.Id,
                entry.Version,
                entry.Sha256),
            [
                new CapabilityContribution(
                    CapabilityKind.Plugin,
                    entry.Id,
                    entry.Id,
                    "Installed Plugin could not be loaded.",
                    CapabilityStatus.Faulted,
                    [],
                    generation: 0,
                    [PluginErrorCodes.LoadFailed]),
            ]);

    private static async Task<IReadOnlyList<PluginToolDeclaration>> ReadToolsAsync(
        StoredPluginPackage package,
        CancellationToken cancellationToken)
    {
        var tools = new List<PluginToolDeclaration>();
        foreach (var relativePath in package.Manifest.Contributions.Tools)
        {
            var path = Path.GetFullPath(
                relativePath.Replace('/', Path.DirectorySeparatorChar),
                package.PackageDirectory);
            var bytes = await ReadBoundedAsync(path, cancellationToken);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });
            EnsureUniqueProperties(document.RootElement);
            tools.Add(ParseTool(document.RootElement));
        }

        if (tools.GroupBy(tool => tool.Id, StringComparer.Ordinal)
            .Any(group => group.Skip(1).Any()) ||
            tools.GroupBy(tool => tool.Executor, StringComparer.Ordinal)
                .Any(group => group.Skip(1).Any()))
        {
            throw LoadFailed();
        }

        return Array.AsReadOnly(tools.ToArray());
    }

    private static PluginToolDeclaration ParseTool(JsonElement root)
    {
        var allowed = new[]
        {
            "id",
            "description",
            "inputSchema",
            "effects",
            "replaySafety",
            "exposure",
            "audience",
            "defaultTimeoutMs",
            "executor",
        };
        RequireObject(root, allowed);
        var id = RequireString(root, "id");
        if (!ToolNamePattern().IsMatch(id))
        {
            throw LoadFailed();
        }

        var inputSchema = root.GetProperty("inputSchema");
        if (inputSchema.ValueKind != JsonValueKind.Object)
        {
            throw LoadFailed();
        }

        var effects = ToolEffect.None;
        foreach (var value in RequireStringArray(root, "effects"))
        {
            effects |= value switch
            {
                "workspaceRead" => ToolEffect.WorkspaceRead,
                "workspaceWrite" => ToolEffect.WorkspaceWrite,
                "processExecution" => ToolEffect.ProcessExecution,
                "networkRead" => ToolEffect.NetworkRead,
                "externalMutation" => ToolEffect.ExternalMutation,
                _ => throw LoadFailed(),
            };
        }

        var audience = ToolInvocationAudience.None;
        foreach (var value in RequireStringArray(root, "audience"))
        {
            audience |= value switch
            {
                "model" => ToolInvocationAudience.Model,
                "host" => ToolInvocationAudience.Host,
                "app" => ToolInvocationAudience.App,
                _ => throw LoadFailed(),
            };
        }

        if (audience == ToolInvocationAudience.None)
        {
            throw LoadFailed();
        }

        var timeoutMs = RequireInt32(root, "defaultTimeoutMs");
        if (timeoutMs is < 1 or > 1_800_000)
        {
            throw LoadFailed();
        }

        return new PluginToolDeclaration(
            id,
            RequireString(root, "description"),
            inputSchema.Clone(),
            effects,
            RequireString(root, "replaySafety") switch
            {
                "safe" => ToolReplaySafety.Safe,
                "unsafe" => ToolReplaySafety.Unsafe,
                _ => throw LoadFailed(),
            },
            RequireString(root, "exposure") switch
            {
                "direct" => ToolExposure.Direct,
                "hidden" => ToolExposure.Hidden,
                _ => throw LoadFailed(),
            },
            audience,
            TimeSpan.FromMilliseconds(timeoutMs),
            RequireString(root, "executor"));
    }

    private static async Task<byte[]> ReadBoundedAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytes = new byte[MaximumToolFileBytes + 1];
        var total = 0;
        while (total < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(total), cancellationToken);
            if (read == 0)
            {
                return bytes[..total];
            }

            total += read;
        }

        throw LoadFailed();
    }

    private static void RequireObject(
        JsonElement element,
        IReadOnlyCollection<string> required)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw LoadFailed();
        }

        var properties = element.EnumerateObject().Select(item => item.Name).ToArray();
        if (properties.Length != required.Count ||
            properties.Any(name => !required.Contains(name, StringComparer.Ordinal)) ||
            required.Any(name => !properties.Contains(name, StringComparer.Ordinal)))
        {
            throw LoadFailed();
        }
    }

    private static IReadOnlyList<string> RequireStringArray(
        JsonElement parent,
        string name)
    {
        var value = parent.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw LoadFailed();
        }

        var values = value.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String
                ? item.GetString()!
                : throw LoadFailed())
            .ToArray();
        return values.Distinct(StringComparer.Ordinal).Count() == values.Length
            ? values
            : throw LoadFailed();
    }

    private static string RequireString(JsonElement parent, string name)
    {
        var value = parent.GetProperty(name);
        var result = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return !string.IsNullOrWhiteSpace(result) &&
               result == result.Trim() &&
               !result.Any(char.IsControl)
            ? result
            : throw LoadFailed();
    }

    private static int RequireInt32(JsonElement parent, string name)
    {
        var value = parent.GetProperty(name);
        return value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt32(out var result)
            ? result
            : throw LoadFailed();
    }

    private static void EnsureUniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw LoadFailed();
                }

                EnsureUniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                EnsureUniqueProperties(item);
            }
        }
    }

    private static string Namespace(string pluginId)
    {
        var builder = new StringBuilder("plugin_");
        foreach (var character in pluginId)
        {
            builder.Append(char.IsAsciiLetterOrDigit(character)
                ? char.ToLowerInvariant(character)
                : '_');
        }

        return builder.ToString();
    }

    private static long Generation(string contentSha256)
    {
        var value = ulong.Parse(
            contentSha256[..16],
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture) & long.MaxValue;
        return value == 0 ? 1 : checked((long)value);
    }

    private static PluginPackageException LoadFailed() =>
        new(
            PluginErrorCodes.LoadFailed,
            "Plugin could not be loaded.");

    [System.Text.RegularExpressions.GeneratedRegex(
        "^[a-z][a-z0-9_]{0,63}$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex ToolNamePattern();

    private sealed record PluginToolDeclaration(
        string Id,
        string Description,
        JsonElement InputSchema,
        ToolEffect Effects,
        ToolReplaySafety ReplaySafety,
        ToolExposure Exposure,
        ToolInvocationAudience Audience,
        TimeSpan DefaultTimeout,
        string Executor);

    private sealed class PluginVersion
    {
        public PluginVersion(
            PluginManifest manifest,
            string contentSha256,
            long generation,
            PluginLoadContext? loadContext,
            IOpenCoWorkPlugin? plugin)
        {
            Manifest = manifest;
            ContentSha256 = contentSha256;
            Generation = generation;
            LoadContext = loadContext;
            ContextReference = new WeakReference(loadContext);
            Plugin = plugin;
        }

        public static PluginVersion Declarative(StoredPluginPackage package) =>
            new(
                package.Manifest,
                package.ContentSha256,
                Generation(package.ContentSha256),
                loadContext: null,
                plugin: null);

        public PluginManifest Manifest { get; }

        public string ContentSha256 { get; }

        public long Generation { get; }

        public PluginLoadContext? LoadContext { get; private set; }

        public WeakReference ContextReference { get; }

        public IOpenCoWorkPlugin? Plugin { get; private set; }

        public IReadOnlyList<ToolRegistration> Registrations { get; set; } = [];

        public Dictionary<RuntimeBindingId, ToolRuntimeBinding> Bindings { get; } = [];

        public int SnapshotLeases { get; set; }

        public int ActiveCalls { get; set; }

        public bool Retired { get; set; }

        public bool Faulted { get; set; }

        public Task? UnloadTask { get; set; }

        public void ClearStrongReferences()
        {
            Plugin = null;
            LoadContext = null;
            Registrations = [];
            Bindings.Clear();
        }

        public void Unload()
        {
            var context = LoadContext;
            ClearStrongReferences();
            context?.Unload();
        }
    }

    private sealed class PluginSnapshotLease(
        PluginRuntime owner,
        IReadOnlyList<PluginVersion> versions) : IDisposable
    {
        private PluginRuntime? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.ReleaseSnapshot(versions);
    }

    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;
        private readonly string _root;

        public PluginLoadContext(string assemblyPath, string root)
            : base(
                $"OpenCoWork.Plugin.{Path.GetFileNameWithoutExtension(assemblyPath)}." +
                $"{Guid.NewGuid():N}",
                isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(assemblyPath);
            _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (string.Equals(
                    assemblyName.Name,
                    typeof(IOpenCoWorkPlugin).Assembly.GetName().Name,
                    StringComparison.Ordinal))
            {
                return typeof(IOpenCoWorkPlugin).Assembly;
            }

            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(Contained(path));
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? 0 : LoadUnmanagedDllFromPath(Contained(path));
        }

        private string Contained(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!fullPath.StartsWith(
                    _root + Path.DirectorySeparatorChar,
                    comparison))
            {
                throw LoadFailed();
            }

            return fullPath;
        }
    }
}
