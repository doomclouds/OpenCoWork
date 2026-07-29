using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Capabilities;

namespace OpenCoWork.Core.Tools;

internal enum CapabilityHookEvent
{
    PreToolUse,
    ToolTerminal,
}

internal sealed record CapabilityHook(
    string Id,
    CapabilityHookEvent Event,
    string? PluginSourceId,
    ToolPreUseHook? PreUse,
    ToolTerminalHook? Terminal)
{
    public static CapabilityHook Pre(string id, ToolPreUseHook hook) =>
        new(id, CapabilityHookEvent.PreToolUse, null, hook, null);

    public static CapabilityHook TerminalHook(string id, ToolTerminalHook hook) =>
        new(id, CapabilityHookEvent.ToolTerminal, null, null, hook);
}

internal sealed class CapabilityHookRuntime
{
    private readonly IReadOnlyList<CapabilityHook> _hooks;
    private readonly ILogger<CapabilityHookRuntime> _logger;
    private readonly WorkspaceProcessHookSource? _workspace;
    private readonly PluginRuntime? _plugins;

    public CapabilityHookRuntime(
        IEnumerable<CapabilityHook> hooks,
        ILogger<CapabilityHookRuntime>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(hooks);
        _hooks = Array.AsReadOnly(hooks
            .OrderBy(hook => hook.Id, StringComparer.Ordinal)
            .ToArray());
        _logger = logger ?? NullLogger<CapabilityHookRuntime>.Instance;
    }

    internal CapabilityHookRuntime(
        WorkspaceProcessHookSource workspace,
        PluginRuntime plugins,
        ILogger<CapabilityHookRuntime>? logger = null)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _plugins = plugins ?? throw new ArgumentNullException(nameof(plugins));
        _hooks = [];
        _logger = logger ?? NullLogger<CapabilityHookRuntime>.Instance;
    }

    public async ValueTask<ToolPreUseDecision> PreToolUseAsync(
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        var authority = ToolAuthorityDecision.Allow;
        TimeSpan? timeout = null;
        foreach (var hook in await MatchingAsync(
                     context,
                     CapabilityHookEvent.PreToolUse,
                     cancellationToken))
        {
            var decision = await hook.PreUse!(context, cancellationToken);
            if (!Enum.IsDefined(decision.Authority) ||
                decision.TimeoutCap is { } cap &&
                (cap <= TimeSpan.Zero || cap > TimeSpan.FromSeconds(10)))
            {
                throw new InvalidDataException("Hook returned an invalid decision.");
            }

            authority = (ToolAuthorityDecision)Math.Min(
                (int)authority,
                (int)decision.Authority);
            if (decision.TimeoutCap is { } candidate)
            {
                timeout = timeout is null || candidate < timeout
                    ? candidate
                    : timeout;
            }
        }

        return new ToolPreUseDecision(authority, timeout);
    }

    public async ValueTask ToolTerminalAsync(
        ToolInvocationContext context,
        ToolResultSnapshot result,
        CancellationToken cancellationToken)
    {
        foreach (var hook in await MatchingAsync(
                     context,
                     CapabilityHookEvent.ToolTerminal,
                     cancellationToken))
        {
            try
            {
                await hook.Terminal!(context, result, cancellationToken);
            }
            catch (Exception)
            {
                _logger.LogWarning("Capability terminal hook {HookId} failed.", hook.Id);
            }
        }
    }

    private async ValueTask<IReadOnlyList<CapabilityHook>> MatchingAsync(
        ToolInvocationContext context,
        CapabilityHookEvent eventType,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CapabilityHook> workspaceHooks = [];
        if (_workspace is not null)
        {
            try
            {
                workspaceHooks = await _workspace.LoadAsync(cancellationToken);
            }
            catch (Exception) when (eventType == CapabilityHookEvent.ToolTerminal)
            {
                _logger.LogWarning("Workspace terminal Hook discovery failed.");
            }
        }

        var hooks = _hooks
            .Concat(_plugins?.GetHooks() ?? [])
            .Concat(workspaceHooks)
            .GroupBy(hook => hook.Id, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .Select(group => group.Single())
            .OrderBy(hook => hook.Id, StringComparer.Ordinal);
        return Array.AsReadOnly(hooks.Where(hook =>
            hook.Event == eventType &&
            (hook.PluginSourceId is null ||
             context.Snapshot.Registrations.Any(registration =>
                 registration.Definition.Id.SourceKind ==
                 ToolSourceKind.PluginNative &&
                 string.Equals(
                     registration.Definition.Id.SourceId,
                     hook.PluginSourceId,
                     StringComparison.Ordinal) &&
                 context.Snapshot.ProviderToCanonicalNames.TryGetValue(
                     context.ProviderToolName,
                     out var canonicalName) &&
                 string.Equals(
                     canonicalName,
                     $"{registration.Definition.Name.Namespace}." +
                     registration.Definition.Name.Name,
                     StringComparison.Ordinal))))
            .ToArray());
    }
}
