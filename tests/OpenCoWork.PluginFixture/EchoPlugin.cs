using System.Text.Json;
using OpenCoWork.Abstractions;

namespace OpenCoWork.PluginFixture;

public sealed class EchoPlugin : IOpenCoWorkPlugin
{
    public IReadOnlyDictionary<string, ToolExecutor> ToolExecutors { get; } =
        new Dictionary<string, ToolExecutor>(StringComparer.Ordinal)
        {
            ["echo"] = EchoAsync,
        };

    public IReadOnlyDictionary<string, PluginHookExecutor> HookExecutors { get; } =
        new Dictionary<string, PluginHookExecutor>(StringComparer.Ordinal)
        {
            ["require_approval"] = RequireApprovalAsync,
        };

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    private static ValueTask<ToolBindingResult> EchoAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ToolBindingResult.Success(arguments));
    }

    private static ValueTask<PluginHookResult> RequireApprovalAsync(
        PluginHookContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new PluginHookResult(
            ToolAuthorityDecision.RequireApproval,
            TimeSpan.FromSeconds(2)));
    }
}
