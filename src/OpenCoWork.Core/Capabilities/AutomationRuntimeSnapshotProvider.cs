using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Tools;
using OpenCoWork.Core.Workspaces;

namespace OpenCoWork.Core.Capabilities;

internal sealed class AutomationRuntimeSnapshotProvider(
    CapabilityFileStore files,
    OpenCoWorkPaths paths,
    WorkspaceCapabilityRuntime capabilities,
    ToolRuntime tools,
    ToolsConfig toolsConfig,
    ModelsConfig models) : IAutomationRuntimeSnapshotProvider
{
    public async ValueTask<AutomationWorkspaceTrustSnapshot> GetWorkspaceTrustAsync(
        CancellationToken cancellationToken = default)
    {
        var source = AutomationTrustBoundary.Source;
        var decisions = await files.LoadTrustDecisionsAsync(cancellationToken);
        var trusted = decisions.Decisions.Any(decision =>
            decision.Matches(
                paths.WorkspaceRoot,
                source.Kind,
                source.Id,
                source.Version,
                source.Sha256) &&
            decision.AllowedScopes.Contains(
                CapabilityTrustScope.UnattendedAutomation) &&
            !decision.DeniedScopes.Contains(
                CapabilityTrustScope.UnattendedAutomation));
        return new AutomationWorkspaceTrustSnapshot(
            trusted,
            source,
            trusted
                ? Hash(
                    $"{CapabilityFileStore.CanonicalWorkspacePath(paths.WorkspaceRoot)}\n" +
                    $"{source.Kind}\n{source.Id}\n{source.Version}\n{source.Sha256}\n" +
                    "unattendedAutomation\nallow")
                : source.Sha256);
    }

    public async Task<AutomationRuntimeCaptureResult> CaptureAsync(
        AutomationRuntimeSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var trust = await GetWorkspaceTrustAsync(cancellationToken);
        if (!trust.IsTrusted)
        {
            return Failure(
                AutomationErrorCodes.PermissionDenied,
                "Workspace trust does not allow unattended Automation.");
        }

        var catalog = capabilities.CurrentCatalog;
        if (catalog.RuntimeState is not (
                CapabilityRuntimeState.Ready or CapabilityRuntimeState.Degraded) ||
            string.IsNullOrWhiteSpace(models.DefaultModel))
        {
            return Failure(
                AutomationErrorCodes.CapabilityUnavailable,
                "Automation runtime dependencies are unavailable.",
                retryable: true);
        }

        EffectiveToolSnapshot toolSnapshot;
        try
        {
            toolSnapshot = tools.BuildSnapshot(AgentMode.Agent, toolsConfig);
        }
        catch (InvalidOperationException)
        {
            return Failure(
                AutomationErrorCodes.CapabilityUnavailable,
                "Automation Tool snapshot is unavailable.",
                retryable: true);
        }

        if (!TryEffects(
                request.Effects,
                toolSnapshot.Authority,
                out var effects,
                out var allowedEffects))
        {
            return Failure(
                AutomationErrorCodes.DefinitionInvalid,
                "Automation effect allowlist is invalid.");
        }

        var requestedPlugins = Set(request.Plugins);
        var requestedSkills = Set(request.Skills);
        var requestedTools = Set(request.Tools);
        var plugins = SelectCatalog(
            catalog,
            CapabilityKind.Plugin,
            requestedPlugins);
        var skills = SelectCatalog(
            catalog,
            CapabilityKind.Skill,
            requestedSkills);
        var selectedPluginIds = plugins.Select(item => item.Id).ToHashSet(
            StringComparer.Ordinal);
        var selectedTools = new List<(string Id, ToolRegistration Tool, CapabilityCatalogItem Item)>();
        foreach (var registration in toolSnapshot.Registrations)
        {
            var definition = registration.Definition;
            var canonical = $"{definition.Name.Namespace}.{definition.Name.Name}";
            var requestedId = requestedTools.Contains(definition.Id.SourceToolId)
                ? definition.Id.SourceToolId
                : requestedTools.Contains(canonical)
                    ? canonical
                    : null;
            if (requestedId is null ||
                (definition.Effects & ~allowedEffects) != 0 ||
                definition.Id.SourceKind == ToolSourceKind.PluginNative &&
                !selectedPluginIds.Contains(definition.Id.SourceId))
            {
                continue;
            }

            var item = catalog.Items.FirstOrDefault(candidate =>
                candidate.Kind == CapabilityKind.Tool &&
                candidate.Status == CapabilityStatus.Ready &&
                candidate.Generation == registration.BindingGeneration &&
                (string.Equals(
                     candidate.Id,
                     definition.Id.SourceToolId,
                     StringComparison.Ordinal) ||
                 string.Equals(
                     candidate.Id,
                     $"{definition.Id.SourceId}/{definition.Id.SourceToolId}",
                     StringComparison.Ordinal) ||
                 string.Equals(
                     candidate.DisplayName,
                     canonical,
                     StringComparison.Ordinal)));
            if (item is not null)
            {
                selectedTools.Add((requestedId, registration, item));
            }
        }

        var snapshots = plugins
            .Select(item => Snapshot("plugin", item.Id, item))
            .Concat(skills.Select(item => Snapshot("skill", item.Id, item)))
            .Concat(selectedTools.Select(item =>
                new AutomationCapabilitySnapshot(
                    "tool",
                    item.Id,
                    item.Item.Source.Version,
                    ToolRuntime.RegistrationSha256(item.Tool),
                    item.Tool.BindingGeneration)))
            .OrderBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        var permission = new AutomationPermissionSnapshot(
            trust.TrustSnapshotId,
            catalog.Revision,
            plugins.Select(item => item.Id).Order(StringComparer.Ordinal).ToArray(),
            skills.Select(item => item.Id).Order(StringComparer.Ordinal).ToArray(),
            selectedTools.Select(item => item.Id).Order(StringComparer.Ordinal).ToArray(),
            effects);
        return new AutomationRuntimeCaptureResult(
            new AutomationRuntimeSnapshot(
                trust,
                ModelsConfig.ProviderId,
                models.DefaultModel,
                permission,
                snapshots),
            null);
    }

    private static CapabilityCatalogItem[] SelectCatalog(
        CapabilityCatalog catalog,
        CapabilityKind kind,
        IReadOnlySet<string> requested) =>
        catalog.Items
            .Where(item =>
                item.Kind == kind &&
                item.Status == CapabilityStatus.Ready &&
                requested.Contains(item.Id))
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();

    private static AutomationCapabilitySnapshot Snapshot(
        string kind,
        string id,
        CapabilityCatalogItem item) =>
        new(
            kind,
            id,
            item.Source.Version,
            item.Source.Sha256,
            item.Generation);

    private static bool TryEffects(
        IReadOnlyList<string> requested,
        IReadOnlyList<ToolAuthorityPolicy> authority,
        out AutomationEffectPermissionSnapshot[] effects,
        out ToolEffect allowed)
    {
        var requestedEffects = new HashSet<ToolEffect>();
        foreach (var name in requested)
        {
            if (!TryEffect(name, out var effect))
            {
                effects = [];
                allowed = ToolEffect.None;
                return false;
            }

            requestedEffects.Add(effect);
        }

        var policies = authority.ToDictionary(item => item.Effect, item => item.Decision);
        effects = requestedEffects
            .Where(effect =>
                policies.GetValueOrDefault(effect, ToolAuthorityDecision.Deny) !=
                ToolAuthorityDecision.Deny)
            .Order()
            .Select(effect => new AutomationEffectPermissionSnapshot(
                Wire(effect),
                policies[effect]))
            .ToArray();
        allowed = effects.Aggregate(
            ToolEffect.None,
            (value, item) => value | ParseEffect(item.Effect));
        return true;
    }

    private static IReadOnlySet<string> Set(IReadOnlyList<string> values) =>
        values.ToHashSet(StringComparer.Ordinal);

    private static bool TryEffect(string value, out ToolEffect effect)
    {
        effect = value switch
        {
            "workspaceRead" => ToolEffect.WorkspaceRead,
            "workspaceWrite" => ToolEffect.WorkspaceWrite,
            "processExecution" => ToolEffect.ProcessExecution,
            "networkRead" => ToolEffect.NetworkRead,
            "externalMutation" => ToolEffect.ExternalMutation,
            _ => ToolEffect.None,
        };
        return effect != ToolEffect.None;
    }

    private static ToolEffect ParseEffect(string value)
    {
        _ = TryEffect(value, out var effect);
        return effect;
    }

    private static string Wire(ToolEffect value) => value switch
    {
        ToolEffect.WorkspaceRead => "workspaceRead",
        ToolEffect.WorkspaceWrite => "workspaceWrite",
        ToolEffect.ProcessExecution => "processExecution",
        ToolEffect.NetworkRead => "networkRead",
        ToolEffect.ExternalMutation => "externalMutation",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static AutomationRuntimeCaptureResult Failure(
        string code,
        string message,
        bool retryable = false) =>
        new(null, new AutomationError(code, message, retryable));
}
