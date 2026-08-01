using OpenCoWork.Abstractions;

namespace OpenCoWork.Core.Operations;

internal sealed class OperationsChangeHub : IOperationsChangeSource
{
    public event EventHandler<OperationsChangedEvent>? Changed;

    internal void Publish(
        OperationsChangeKind kind,
        string changeKind,
        string? entityId = null) =>
        Changed?.Invoke(this, new OperationsChangedEvent(kind, changeKind, entityId));
}
