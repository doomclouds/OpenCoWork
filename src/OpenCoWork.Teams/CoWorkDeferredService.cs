using OpenCoWork.Abstractions;

namespace OpenCoWork.Teams;

public sealed partial class CoWorkService
{
    public Task<CoWorkResult<CoWorkPage<WorktreeSnapshot>>> ListWorktreesAsync(
        ListWorktreesRequest request,
        CancellationToken cancellationToken = default) =>
        DeferredAsync<CoWorkPage<WorktreeSnapshot>>(cancellationToken);

    public Task<CoWorkResult<WorktreeSnapshot>> GetWorktreeAsync(
        GetWorktreeRequest request,
        CancellationToken cancellationToken = default) =>
        DeferredAsync<WorktreeSnapshot>(cancellationToken);

    public Task<CoWorkResult<WorktreeHandoffSnapshot>> HandoffWorktreeAsync(
        WorktreeCommandRequest request,
        CancellationToken cancellationToken = default) =>
        DeferredAsync<WorktreeHandoffSnapshot>(cancellationToken);

    public Task<CoWorkResult<WorktreeSnapshot>> RemoveWorktreeAsync(
        WorktreeCommandRequest request,
        CancellationToken cancellationToken = default) =>
        DeferredAsync<WorktreeSnapshot>(cancellationToken);

    private Task<CoWorkResult<T>> DeferredAsync<T>(
        CancellationToken cancellationToken) =>
        FailureAsync<T>(
            CoWorkErrorCodes.InvalidState,
            "This CoWork operation is not available in the current runtime slice.",
            cancellationToken);
}
