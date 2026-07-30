using OpenCoWork.Abstractions;

namespace OpenCoWork.Teams;

public sealed partial class CoWorkService
{
    public Task<CoWorkResult<CoWorkPage<MailboxMessageSnapshot>>> ListMailboxMessagesAsync(
        ListMailboxMessagesRequest request,
        CancellationToken cancellationToken = default) =>
        DeferredAsync<CoWorkPage<MailboxMessageSnapshot>>(cancellationToken);

    public Task<CoWorkResult<MailboxMessageSnapshot>> SendMailboxMessageAsync(
        SendMailboxMessageRequest request,
        CancellationToken cancellationToken = default) =>
        ContainsSensitiveData(request.Body)
            ? FailureAsync<MailboxMessageSnapshot>(
                CoWorkErrorCodes.SecretDetected,
                "Mailbox message contains sensitive data.",
                cancellationToken)
            : DeferredAsync<MailboxMessageSnapshot>(cancellationToken);

    public Task<CoWorkResult<MailboxMessageSnapshot>> AcknowledgeMailboxMessageAsync(
        MailboxMessageCommandRequest request,
        CancellationToken cancellationToken = default) =>
        DeferredAsync<MailboxMessageSnapshot>(cancellationToken);

    public Task<CoWorkResult<MailboxMessageSnapshot>> RetryMailboxMessageAsync(
        MailboxMessageCommandRequest request,
        CancellationToken cancellationToken = default) =>
        DeferredAsync<MailboxMessageSnapshot>(cancellationToken);

    public Task<CoWorkResult<CoWorkPage<ArtifactSnapshot>>> ListArtifactsAsync(
        ListArtifactsRequest request,
        CancellationToken cancellationToken = default) =>
        DeferredAsync<CoWorkPage<ArtifactSnapshot>>(cancellationToken);

    public Task<CoWorkResult<ArtifactSnapshot>> GetArtifactAsync(
        GetArtifactRequest request,
        CancellationToken cancellationToken = default) =>
        DeferredAsync<ArtifactSnapshot>(cancellationToken);

    public Task<CoWorkResult<ArtifactSnapshot>> PublishArtifactAsync(
        PublishArtifactRequest request,
        CancellationToken cancellationToken = default) =>
        DeferredAsync<ArtifactSnapshot>(cancellationToken);

    public Task<CoWorkResult<ArtifactSnapshot>> PromoteArtifactAsync(
        PromoteArtifactRequest request,
        CancellationToken cancellationToken = default) =>
        DeferredAsync<ArtifactSnapshot>(cancellationToken);

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
