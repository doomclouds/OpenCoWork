using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Agents;
using OpenCoWork.Core.Sessions;
using OpenCoWork.Core.Tools;
using OpenCoWork.Core.Workspaces;

namespace OpenCoWork.Core.Capabilities;

internal sealed class WorkspaceCapabilityService : ICapabilityService
{
    private const int MaximumPageSize = 100;
    private readonly WorkspaceCapabilityRuntime _runtime;
    private readonly CapabilityFileStore _files;
    private readonly OpenCoWorkPaths _paths;
    private readonly PluginManager _plugins;
    private readonly SkillCatalog _skills;
    private readonly McpCapabilitySource _mcp;
    private readonly LspCapabilitySource _lsp;
    private readonly ProviderAuthService _auth;
    private readonly DynamicToolRegistry _dynamicTools;
    private readonly CoreSourceControlTool _sourceControl;
    private readonly BackgroundTerminalRuntime _terminal;
    private readonly WorkspaceMemoryRuntime _memory;
    private readonly SemaphoreSlim _mutationLock = new(1, 1);
    private static readonly EffectiveToolSnapshot WireToolSnapshot = new(
        1,
        AgentMode.Agent,
        [],
        [],
        new Dictionary<string, string>(StringComparer.Ordinal),
        new Dictionary<string, string>(StringComparer.Ordinal),
        [],
        new string('0', 64));

    public WorkspaceCapabilityService(
        WorkspaceCapabilityRuntime runtime,
        CapabilityFileStore files,
        OpenCoWorkPaths paths,
        PluginManager plugins,
        SkillCatalog skills,
        McpCapabilitySource mcp,
        LspCapabilitySource lsp,
        ProviderAuthService auth,
        DynamicToolRegistry dynamicTools,
        CoreSourceControlTool sourceControl,
        BackgroundTerminalRuntime terminal,
        WorkspaceMemoryRuntime memory)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _plugins = plugins ?? throw new ArgumentNullException(nameof(plugins));
        _skills = skills ?? throw new ArgumentNullException(nameof(skills));
        _mcp = mcp ?? throw new ArgumentNullException(nameof(mcp));
        _lsp = lsp ?? throw new ArgumentNullException(nameof(lsp));
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _dynamicTools =
            dynamicTools ?? throw new ArgumentNullException(nameof(dynamicTools));
        _sourceControl =
            sourceControl ?? throw new ArgumentNullException(nameof(sourceControl));
        _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _runtime.CatalogChanged += OnCatalogChanged;
    }

    public event EventHandler<CapabilityCatalogChangedEventArgs>? CatalogChanged;

    public ValueTask<CapabilityCatalogPage> GetCatalogAsync(
        CapabilityCatalogQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        if (query.Limit is < 1 or > MaximumPageSize)
        {
            throw InvalidCursor();
        }

        var catalog = _runtime.CurrentCatalog;
        var offset = DecodeCursor(query.Cursor, catalog.Revision);
        if (offset > catalog.Items.Count)
        {
            throw InvalidCursor();
        }

        var items = catalog.Items.Skip(offset).Take(query.Limit).ToArray();
        var nextOffset = offset + items.Length;
        return ValueTask.FromResult(new CapabilityCatalogPage(
            catalog.SchemaVersion,
            catalog.Revision,
            catalog.CatalogSha256,
            catalog.RuntimeState,
            Array.AsReadOnly(items),
            nextOffset < catalog.Items.Count
                ? EncodeCursor(catalog.Revision, nextOffset)
                : null));
    }

    public ValueTask<CapabilityCatalogEntry> ReadAsync(
        CapabilityIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.Id);
        cancellationToken.ThrowIfCancellationRequested();
        var catalog = _runtime.CurrentCatalog;
        var item = catalog.Items.SingleOrDefault(candidate =>
            candidate.Kind == identity.Kind &&
            string.Equals(candidate.Id, identity.Id, StringComparison.Ordinal));
        return item is null
            ? throw new CapabilityServiceException(
                CapabilityErrorCodes.NotFound,
                "Capability was not found.")
            : ValueTask.FromResult(new CapabilityCatalogEntry(
                catalog.Revision,
                item));
    }

    public async ValueTask<CapabilityCatalogChange> RefreshAsync(
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        EnsureRevision(expectedRevision);
        await _mutationLock.WaitAsync(cancellationToken);
        try
        {
            EnsureRevision(expectedRevision);
            var refreshed = await _runtime.RefreshDiscoveredAsync(cancellationToken);
            return Change(
                refreshed,
                refreshed.Revision != expectedRevision);
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    public async ValueTask<CapabilityCatalogChange> SetEnabledAsync(
        CapabilitySetEnabledRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Id);
        EnsureRevision(request.ExpectedRevision);
        await _mutationLock.WaitAsync(cancellationToken);
        try
        {
            EnsureRevision(request.ExpectedRevision);
            var document = await _files.LoadWorkspaceOverridesAsync(
                cancellationToken);
            var disabled = document.Disabled.ToList();
            var existing = disabled.FindIndex(item =>
                item.Kind == request.Kind &&
                string.Equals(item.Id, request.Id, StringComparison.Ordinal));
            if (request.Enabled == (existing < 0))
            {
                return Change(_runtime.CurrentCatalog, changed: false);
            }

            if (request.Enabled)
            {
                disabled.RemoveAt(existing);
            }
            else
            {
                disabled.Add(new DisabledCapability(request.Kind, request.Id));
            }

            await _files.SaveWorkspaceOverridesAsync(
                document with { Disabled = disabled },
                cancellationToken);
            var refreshed = await _runtime.RefreshDiscoveredAsync(cancellationToken);
            return Change(
                refreshed,
                refreshed.Revision != request.ExpectedRevision);
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    public async ValueTask<CapabilityDomainResult> ExecuteDomainAsync(
        CapabilityDomainRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Operation);
        try
        {
            return request.Operation switch
            {
                "plugin/install" => await CatalogMutationAsync(
                    request.Arguments,
                    InstallPluginAsync,
                    cancellationToken),
                "plugin/remove" => await CatalogMutationAsync(
                    request.Arguments,
                    RemovePluginAsync,
                    cancellationToken),
                "plugin/setEnabled" => await CatalogMutationAsync(
                    request.Arguments,
                    SetPluginEnabledAsync,
                    cancellationToken),
                "skill/read" => await ReadSkillAsync(
                    request.Arguments,
                    cancellationToken),
                "skill/selectVariant" => await SelectSkillVariantAsync(
                    request.Arguments,
                    cancellationToken),
                "trust/decide" => await DecideTrustAsync(
                    request.ConnectionId,
                    request.Arguments,
                    cancellationToken),
                "trust/revoke" => await RevokeTrustAsync(
                    request.ConnectionId,
                    request.Arguments,
                    cancellationToken),
                "mcp/resource/list" => McpListResources(request.Arguments),
                "mcp/resource/read" => await McpReadResourceAsync(
                    request.Arguments,
                    cancellationToken),
                "mcp/restart" => await CatalogMutationAsync(
                    request.Arguments,
                    RestartMcpAsync,
                    cancellationToken),
                "lsp/request" => await LspRequestAsync(
                    request.Arguments,
                    cancellationToken),
                "lsp/restart" => await CatalogMutationAsync(
                    request.Arguments,
                    RestartLspAsync,
                    cancellationToken),
                "auth/secret/set" => SetSecret(request.Arguments),
                "auth/secret/clear" => ClearSecret(request.Arguments),
                "sourceControl/inspect" => await InspectSourceControlAsync(
                    cancellationToken),
                "sourceControl/status" => await ToolResultAsync(
                    _sourceControl.StatusAsync(
                        request.Arguments,
                        cancellationToken)),
                "sourceControl/diff" => await ToolResultAsync(
                    _sourceControl.DiffAsync(
                        request.Arguments,
                        cancellationToken)),
                "sourceControl/log" => await ToolResultAsync(
                    _sourceControl.LogAsync(
                        request.Arguments,
                        cancellationToken)),
                "sourceControl/show" => await ToolResultAsync(
                    _sourceControl.ShowAsync(
                        request.Arguments,
                        cancellationToken)),
                "terminal/start" => await TerminalAsync(
                    request,
                    _terminal.StartAsync,
                    cancellationToken),
                "terminal/list" => await TerminalAsync(
                    request,
                    _terminal.ListAsync,
                    cancellationToken),
                "terminal/read" => await TerminalAsync(
                    request,
                    _terminal.ReadAsync,
                    cancellationToken),
                "terminal/write" => await TerminalAsync(
                    request,
                    _terminal.WriteAsync,
                    cancellationToken),
                "terminal/stop" => await TerminalAsync(
                    request,
                    _terminal.StopAsync,
                    cancellationToken),
                "terminal/release" => await TerminalAsync(
                    request,
                    _terminal.ReleaseAsync,
                    cancellationToken),
                "memory/list" => await ToolResultAsync(
                    _memory.ListAsync(request.Arguments, cancellationToken)),
                "memory/search" => await ToolResultAsync(
                    _memory.SearchAsync(request.Arguments, cancellationToken)),
                "memory/read" => await ToolResultAsync(
                    _memory.ReadAsync(request.Arguments, cancellationToken)),
                "memory/write" => await ToolResultAsync(
                    _memory.WriteAsync(request.Arguments, cancellationToken)),
                "memory/archive" => await ToolResultAsync(
                    _memory.ArchiveAsync(request.Arguments, cancellationToken)),
                _ => throw new CapabilityServiceException(
                    CapabilityErrorCodes.NotFound,
                    "Capability domain operation was not found."),
            };
        }
        catch (CapabilityServiceException)
        {
            throw;
        }
        catch (AgentPreparationException exception)
        {
            throw ServiceError(exception.Code, exception.Message);
        }
        catch (PluginPackageException exception)
        {
            throw ServiceError(exception.Code, exception.Message);
        }
        catch (CapabilityPersistenceException exception)
        {
            throw ServiceError(exception.Code, exception.Message);
        }
        catch (McpCapabilityException exception)
        {
            throw ServiceError(exception.Code, exception.Message);
        }
        catch (LspCapabilityException exception)
        {
            throw ServiceError(exception.Code, exception.Message);
        }
        catch (DynamicToolException exception)
        {
            throw ServiceError(exception.Code, exception.Message);
        }
    }

    public ValueTask<CapabilityDynamicToolRegistration> RegisterDynamicToolAsync(
        Guid connectionId,
        CapabilityDynamicToolRegistrationRequest request,
        ToolExecutor executor,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Definition);
        try
        {
            var snapshot = _dynamicTools.Register(
                connectionId,
                request.ThreadId,
                new DynamicToolRegistrationRequest(
                    request.RegistrationId,
                    new DynamicToolDefinition(
                        request.Definition.Name,
                        request.Definition.Description,
                        request.Definition.InputSchema,
                        request.Definition.Effects,
                        request.Definition.ReplaySafety),
                    request.DefinitionSha256,
                    request.LeaseDuration),
                executor);
            return ValueTask.FromResult(Map(snapshot));
        }
        catch (DynamicToolException exception)
        {
            throw ServiceError(exception.Code, exception.Message);
        }
    }

    public ValueTask<CapabilityDynamicToolRegistration> RenewDynamicToolAsync(
        Guid connectionId,
        Guid threadId,
        Guid registrationId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return ValueTask.FromResult(Map(_dynamicTools.Renew(
                connectionId,
                threadId,
                registrationId,
                leaseDuration)));
        }
        catch (DynamicToolException exception)
        {
            throw ServiceError(exception.Code, exception.Message);
        }
    }

    public ValueTask UnregisterDynamicToolAsync(
        Guid connectionId,
        Guid threadId,
        Guid registrationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            _dynamicTools.Unregister(connectionId, threadId, registrationId);
            return ValueTask.CompletedTask;
        }
        catch (DynamicToolException exception)
        {
            throw ServiceError(exception.Code, exception.Message);
        }
    }

    public void DisconnectDynamicTools(Guid connectionId) =>
        _dynamicTools.Disconnect(connectionId);

    private async ValueTask<CapabilityDomainResult> CatalogMutationAsync(
        JsonElement arguments,
        Func<
            JsonElement,
            CancellationToken,
            ValueTask<CapabilityDomainResult>> mutation,
        CancellationToken cancellationToken)
    {
        var expectedRevision = RequiredInt64(arguments, "expectedRevision");
        EnsureRevision(expectedRevision);
        await _mutationLock.WaitAsync(cancellationToken);
        try
        {
            EnsureRevision(expectedRevision);
            return await mutation(arguments, cancellationToken);
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    private async ValueTask<CapabilityDomainResult> InstallPluginAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        EnsureRevision(RequiredInt64(arguments, "expectedRevision"));
        var kind = RequiredString(arguments, "kind");
        var installed = kind switch
        {
            "local" => await _plugins.InstallLocalAsync(
                RequiredString(arguments, "archivePath"),
                cancellationToken),
            "https" => await _plugins.InstallHttpsAsync(
                new Uri(RequiredString(arguments, "artifactUri"), UriKind.Absolute),
                RequiredString(arguments, "artifactSha256"),
                cancellationToken),
            _ => throw new ArgumentException("Plugin install kind is invalid."),
        };
        return DomainResult(installed, _runtime.CurrentCatalog.Revision);
    }

    private async ValueTask<CapabilityDomainResult> RemovePluginAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        EnsureRevision(RequiredInt64(arguments, "expectedRevision"));
        await _plugins.RemoveAsync(
            RequiredString(arguments, "pluginId"),
            cancellationToken);
        return DomainResult(
            new { removed = true },
            _runtime.CurrentCatalog.Revision);
    }

    private async ValueTask<CapabilityDomainResult> SetPluginEnabledAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        EnsureRevision(RequiredInt64(arguments, "expectedRevision"));
        await _plugins.SetEnabledAsync(
            RequiredString(arguments, "pluginId"),
            RequiredBoolean(arguments, "enabled"),
            cancellationToken);
        return DomainResult(
            new { enabled = RequiredBoolean(arguments, "enabled") },
            _runtime.CurrentCatalog.Revision);
    }

    private async ValueTask<CapabilityDomainResult> ReadSkillAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var id = RequiredString(arguments, "skillId");
        var result = await _skills.DiscoverAsync(
            cancellationToken: cancellationToken);
        var item = result.Snapshot.Items.SingleOrDefault(candidate =>
            string.Equals(candidate.Id, id, StringComparison.Ordinal)) ??
            throw new CapabilityServiceException(
                CapabilityErrorCodes.NotFound,
                "Skill was not found.");
        return DomainResult(item);
    }

    private async ValueTask<CapabilityDomainResult> SelectSkillVariantAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        EnsureRevision(RequiredInt64(arguments, "expectedRevision"));
        var baseId = RequiredString(arguments, "baseId");
        var variantId = OptionalString(arguments, "variantId");
        await _mutationLock.WaitAsync(cancellationToken);
        try
        {
            EnsureRevision(RequiredInt64(arguments, "expectedRevision"));
            var previous = await _files.LoadWorkspaceOverridesAsync(
                cancellationToken);
            var variants = previous.SkillVariants
                .Where(item => !string.Equals(
                    item.BaseId,
                    baseId,
                    StringComparison.Ordinal))
                .ToList();
            if (variantId is not null)
            {
                variants.Add(new SkillVariantOverride(baseId, variantId));
            }

            if (previous.SkillVariants.SequenceEqual(variants))
            {
                return DomainResult(
                    new { baseId, variantId },
                    _runtime.CurrentCatalog.Revision);
            }

            await _files.SaveWorkspaceOverridesAsync(
                previous with { SkillVariants = variants },
                cancellationToken);
            await _runtime.RefreshDiscoveredAsync(cancellationToken);
            return DomainResult(
                new { baseId, variantId },
                _runtime.CurrentCatalog.Revision);
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    private async ValueTask<CapabilityDomainResult> DecideTrustAsync(
        Guid connectionId,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (OptionalBoolean(arguments, "dynamicConnection", defaultValue: false))
        {
            _dynamicTools.GrantConnectionTrust(connectionId);
            return DomainResult(new { trusted = true });
        }

        EnsureRevision(RequiredInt64(arguments, "expectedRevision"));
        await _mutationLock.WaitAsync(cancellationToken);
        try
        {
            EnsureRevision(RequiredInt64(arguments, "expectedRevision"));
            var previous = await _files.LoadTrustDecisionsAsync(cancellationToken);
            var decision = ParseTrustDecision(arguments);
            var decisions = previous.Decisions
                .Where(item => !SameTrustIdentity(item, decision))
                .Append(decision)
                .ToArray();
            await _files.SaveTrustDecisionsAsync(
                new TrustDecisionsDocument(1, Array.AsReadOnly(decisions)),
                cancellationToken);
            await _runtime.RefreshDiscoveredAsync(cancellationToken);
            return DomainResult(
                new { trusted = true },
                _runtime.CurrentCatalog.Revision);
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    private async ValueTask<CapabilityDomainResult> RevokeTrustAsync(
        Guid connectionId,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (OptionalBoolean(arguments, "dynamicConnection", defaultValue: false))
        {
            _dynamicTools.RevokeConnectionTrust(connectionId);
            return DomainResult(new { trusted = false });
        }

        EnsureRevision(RequiredInt64(arguments, "expectedRevision"));
        await _mutationLock.WaitAsync(cancellationToken);
        try
        {
            EnsureRevision(RequiredInt64(arguments, "expectedRevision"));
            var previous = await _files.LoadTrustDecisionsAsync(cancellationToken);
            var identity = ParseTrustDecision(arguments);
            var decisions = previous.Decisions
                .Where(item => !SameTrustIdentity(item, identity))
                .ToArray();
            await _files.SaveTrustDecisionsAsync(
                new TrustDecisionsDocument(1, Array.AsReadOnly(decisions)),
                cancellationToken);
            await _runtime.RefreshDiscoveredAsync(cancellationToken);
            return DomainResult(
                new { trusted = false },
                _runtime.CurrentCatalog.Revision);
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    private CapabilityDomainResult McpListResources(JsonElement arguments) =>
        DomainResult(_mcp.ListResources(RequiredString(arguments, "serverId")));

    private async ValueTask<CapabilityDomainResult> McpReadResourceAsync(
        JsonElement arguments,
        CancellationToken cancellationToken) =>
        DomainResult(await _mcp.ReadResourceAsync(
            RequiredString(arguments, "serverId"),
            RequiredString(arguments, "uri"),
            cancellationToken));

    private async ValueTask<CapabilityDomainResult> RestartMcpAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        EnsureRevision(RequiredInt64(arguments, "expectedRevision"));
        var serverId = RequiredString(arguments, "serverId");
        try
        {
            await _mcp.RestartAsync(
                serverId,
                RequiredInt64(arguments, "expectedGeneration"),
                cancellationToken);
        }
        catch (McpCapabilityException exception)
        {
            throw new CapabilityServiceException(
                exception.Code,
                exception.Message,
                currentGeneration: CurrentGeneration(
                    CapabilityKind.McpServer,
                    serverId));
        }

        return DomainResult(
            new { restarted = true },
            _runtime.CurrentCatalog.Revision);
    }

    private async ValueTask<CapabilityDomainResult> LspRequestAsync(
        JsonElement arguments,
        CancellationToken cancellationToken) =>
        DomainResult(await _lsp.RequestAsync(
            new LspRequest(
                RequiredString(arguments, "serverId"),
                RequiredString(arguments, "method"),
                OptionalString(arguments, "path"),
                OptionalInt32(arguments, "line"),
                OptionalInt32(arguments, "character"),
                OptionalString(arguments, "query")),
            cancellationToken));

    private async ValueTask<CapabilityDomainResult> RestartLspAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        EnsureRevision(RequiredInt64(arguments, "expectedRevision"));
        var serverId = RequiredString(arguments, "serverId");
        try
        {
            await _lsp.RestartAsync(
                serverId,
                RequiredInt64(arguments, "expectedGeneration"),
                cancellationToken);
        }
        catch (LspCapabilityException exception)
        {
            throw new CapabilityServiceException(
                exception.Code,
                exception.Message,
                currentGeneration: CurrentGeneration(
                    CapabilityKind.LspServer,
                    serverId));
        }

        return DomainResult(
            new { restarted = true },
            _runtime.CurrentCatalog.Revision);
    }

    private CapabilityDomainResult SetSecret(JsonElement arguments)
    {
        _auth.Set(
            RequiredString(arguments, "profileId"),
            RequiredString(arguments, "secret"));
        return DomainResult(new { stored = true });
    }

    private CapabilityDomainResult ClearSecret(JsonElement arguments)
    {
        _auth.Clear(RequiredString(arguments, "profileId"));
        return DomainResult(new { stored = false });
    }

    private async ValueTask<CapabilityDomainResult> InspectSourceControlAsync(
        CancellationToken cancellationToken) =>
        DomainResult(await _sourceControl.InspectAsync(cancellationToken));

    private static async ValueTask<CapabilityDomainResult> ToolResultAsync(
        ValueTask<ToolBindingResult> operation) =>
        ToolResult(await operation);

    private static CapabilityDomainResult ToolResult(ToolBindingResult result)
    {
        if (!result.IsSuccess)
        {
            throw new CapabilityServiceException(
                result.Error!.Code,
                result.Error.Message,
                result.Error.IsRetryable);
        }

        return new CapabilityDomainResult(
            result.Output ?? JsonSerializer.SerializeToElement(new { }));
    }

    private static async ValueTask<CapabilityDomainResult> TerminalAsync(
        CapabilityDomainRequest request,
        Func<
            ToolInvocationContext,
            CancellationToken,
            ValueTask<ToolBindingResult>> operation,
        CancellationToken cancellationToken)
    {
        SessionIds.RequireVersion7(
            RequiredGuid(request.Arguments, "threadId"),
            "threadId",
            "Thread ID");
        var threadId = RequiredGuid(request.Arguments, "threadId");
        var toolArguments = RequiredObject(request.Arguments, "arguments");
        var context = new ToolInvocationContext(
            threadId,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            0,
            Guid.CreateVersion7().ToString("D"),
            request.Operation,
            toolArguments,
            new string('0', 64),
            SensitiveInputDetected: false,
            WireToolSnapshot);
        return ToolResult(await operation(context, cancellationToken));
    }

    private CapabilityTrustDecision ParseTrustDecision(JsonElement arguments) =>
        new(
            CapabilityFileStore.CanonicalWorkspacePath(_paths.WorkspaceRoot),
            ParseEnum<CapabilitySourceKind>(
                RequiredString(arguments, "sourceKind")),
            RequiredString(arguments, "sourceId"),
            OptionalString(arguments, "sourceVersion"),
            RequiredString(arguments, "sha256"),
            RequiredEnumArray<CapabilityTrustScope>(arguments, "allowedScopes"),
            RequiredEnumArray<CapabilityTrustScope>(arguments, "deniedScopes"));

    private static bool SameTrustIdentity(
        CapabilityTrustDecision left,
        CapabilityTrustDecision right) =>
        left.Matches(
            right.WorkspacePath,
            right.SourceKind,
            right.SourceId,
            right.SourceVersion,
            right.Sha256);

    private static CapabilityDynamicToolRegistration Map(
        DynamicToolRegistrationSnapshot snapshot) =>
        new(
            snapshot.ConnectionId,
            snapshot.ThreadId,
            snapshot.RegistrationId,
            snapshot.DefinitionSha256,
            snapshot.Status,
            snapshot.RuntimeBindingId.Value,
            snapshot.ExpiresAt);

    private static CapabilityDomainResult DomainResult<T>(
        T value,
        long? revision = null) =>
        new(JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web), revision);

    private static string RequiredString(JsonElement value, string propertyName)
    {
        var property = value.GetProperty(propertyName);
        return property.ValueKind == JsonValueKind.String &&
               !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()!
            : throw new ArgumentException($"{propertyName} is required.");
    }

    private static string? OptionalString(
        JsonElement value,
        string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String &&
               !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()
            : throw new ArgumentException($"{propertyName} is invalid.");
    }

    private static bool RequiredBoolean(JsonElement value, string propertyName)
    {
        var property = value.GetProperty(propertyName);
        return property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : throw new ArgumentException($"{propertyName} is invalid.");
    }

    private static bool OptionalBoolean(
        JsonElement value,
        string propertyName,
        bool defaultValue) =>
        !value.TryGetProperty(propertyName, out var property)
            ? defaultValue
            : property.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? property.GetBoolean()
                : throw new ArgumentException($"{propertyName} is invalid.");

    private static long RequiredInt64(JsonElement value, string propertyName) =>
        value.GetProperty(propertyName).TryGetInt64(out var result)
            ? result
            : throw new ArgumentException($"{propertyName} is invalid.");

    private static int? OptionalInt32(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return property.TryGetInt32(out var result)
            ? result
            : throw new ArgumentException($"{propertyName} is invalid.");
    }

    private static Guid RequiredGuid(JsonElement value, string propertyName) =>
        value.GetProperty(propertyName).TryGetGuid(out var result)
            ? result
            : throw new ArgumentException($"{propertyName} is invalid.");

    private static JsonElement RequiredObject(
        JsonElement value,
        string propertyName)
    {
        var property = value.GetProperty(propertyName);
        return property.ValueKind == JsonValueKind.Object
            ? property.Clone()
            : throw new ArgumentException($"{propertyName} is invalid.");
    }

    private static T ParseEnum<T>(string value)
        where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: true, out var parsed) &&
        string.Equals(
            JsonNamingPolicy.CamelCase.ConvertName(parsed.ToString()),
            value,
            StringComparison.Ordinal)
            ? parsed
            : throw new ArgumentException($"{typeof(T).Name} is invalid.");

    private static IReadOnlyList<T> RequiredEnumArray<T>(
        JsonElement value,
        string propertyName)
        where T : struct, Enum
    {
        var property = value.GetProperty(propertyName);
        return property.ValueKind == JsonValueKind.Array
            ? Array.AsReadOnly(property
                .EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.String
                    ? ParseEnum<T>(item.GetString()!)
                    : throw new ArgumentException($"{propertyName} is invalid."))
                .Distinct()
                .ToArray())
            : throw new ArgumentException($"{propertyName} is invalid.");
    }

    private static CapabilityServiceException ServiceError(
        string code,
        string message) =>
        new(code, message);

    private long? CurrentGeneration(CapabilityKind kind, string id) =>
        _runtime.CurrentCatalog.Items.SingleOrDefault(item =>
            item.Kind == kind &&
            string.Equals(item.Id, id, StringComparison.Ordinal))?.Generation;

    private void OnCatalogChanged(
        object? sender,
        CapabilityCatalogChangedEventArgs args) =>
        CatalogChanged?.Invoke(this, args);

    private void EnsureRevision(long expectedRevision)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        var currentRevision = _runtime.CurrentCatalog.Revision;
        if (currentRevision != expectedRevision)
        {
            throw new CapabilityServiceException(
                CapabilityErrorCodes.RevisionConflict,
                "Capability Catalog revision no longer matches.",
                currentRevision: currentRevision);
        }
    }

    private static CapabilityCatalogChange Change(
        CapabilityCatalog catalog,
        bool changed) =>
        new(catalog.Revision, catalog.RuntimeState, changed);

    private static string EncodeCursor(long revision, int offset) =>
        Convert.ToBase64String(Encoding.ASCII.GetBytes(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{revision}:{offset}")));

    private static int DecodeCursor(string? cursor, long currentRevision)
    {
        if (cursor is null)
        {
            return 0;
        }

        try
        {
            var value = Encoding.ASCII.GetString(Convert.FromBase64String(cursor));
            var separator = value.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0 ||
                !long.TryParse(
                    value.AsSpan(0, separator),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var revision) ||
                !int.TryParse(
                    value.AsSpan(separator + 1),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var offset) ||
                offset < 0)
            {
                throw InvalidCursor();
            }

            if (revision != currentRevision)
            {
                throw new CapabilityServiceException(
                    CapabilityErrorCodes.RevisionConflict,
                    "Capability Catalog cursor revision no longer matches.",
                    currentRevision: currentRevision);
            }

            return offset;
        }
        catch (FormatException)
        {
            throw InvalidCursor();
        }
    }

    private static CapabilityServiceException InvalidCursor() =>
        new(CapabilityErrorCodes.CursorInvalid, "Capability Catalog cursor is invalid.");
}
