namespace OpenCoWork.Abstractions;

public interface IOpenCoWorkPlugin
{
    IReadOnlyDictionary<string, ToolExecutor> ToolExecutors { get; }

    ValueTask StopAsync(CancellationToken cancellationToken);
}
