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
}
