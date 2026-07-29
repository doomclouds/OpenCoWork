using System.Text.Json;

namespace OpenCoWork.Abstractions;

public interface IOpenCoWorkPlugin
{
    IReadOnlyDictionary<string, ToolExecutor> ToolExecutors { get; }

    IReadOnlyDictionary<string, PluginHookExecutor> HookExecutors { get; }

    ValueTask StopAsync(CancellationToken cancellationToken);
}

public sealed record PluginHookContext(
    string Event,
    ToolDefinitionId ToolDefinitionId,
    JsonElement Arguments,
    ToolResultSnapshot? Result);

public sealed record PluginHookResult(
    ToolAuthorityDecision Authority = ToolAuthorityDecision.Allow,
    TimeSpan? TimeoutCap = null);

public delegate ValueTask<PluginHookResult> PluginHookExecutor(
    PluginHookContext context,
    CancellationToken cancellationToken);
