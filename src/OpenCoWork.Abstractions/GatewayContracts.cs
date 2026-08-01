namespace OpenCoWork.Abstractions;

public enum ChannelRuntimeStatus
{
    Disabled,
    PendingTrust,
    Unavailable,
    Ready,
    Faulted,
    Degraded,
    Stopping,
    Stopped,
}

public enum ChannelInboundStatus
{
    Pending,
    Dispatching,
    Delivered,
    Failed,
    DeadLettered,
}

public enum ChannelOutboxStatus
{
    Pending,
    Sending,
    Sent,
    Failed,
    DeadLettered,
}

public static class ChannelErrorCodes
{
    public const string DefinitionInvalid = "channel.definitionInvalid";
    public const string NotFound = "channel.notFound";
    public const string CredentialUnavailable = "channel.credentialUnavailable";
    public const string PendingTrust = "channel.pendingTrust";
    public const string AuthenticationFailed = "channel.authenticationFailed";
    public const string MessageConflict = "channel.messageConflict";
    public const string CapacityExceeded = "channel.capacityExceeded";
    public const string MediaInvalid = "channel.mediaInvalid";
    public const string MediaRejected = "channel.mediaRejected";
    public const string MediaNotFound = "channel.mediaNotFound";
    public const string SchemaInvalid = "channel.schemaInvalid";
    public const string IdempotencyConflict = "channel.idempotencyConflict";
    public const string PermissionDenied = "channel.permissionDenied";
    public const string Unavailable = "channel.unavailable";
    public const string RateLimited = "channel.rateLimited";
    public const string StateUnavailable = "channel.stateUnavailable";
    public const string RevisionConflict = "channel.revisionConflict";
    public const string CursorInvalid = "channel.cursorInvalid";
    public const string DeliveryFailed = "channel.deliveryFailed";
    public const string TurnRemoved = "channel.turnRemoved";
}

public sealed class ChannelServiceException : Exception
{
    public ChannelServiceException(
        string code,
        string message,
        bool retryable = false,
        long? currentRevision = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
        Retryable = retryable;
        CurrentRevision = currentRevision;
    }

    public string Code { get; }

    public bool Retryable { get; }

    public long? CurrentRevision { get; }
}

public interface IChannelCredentialAdmin
{
    void Set(string channelId, string secret);

    void Clear(string channelId);
}

public sealed record ChannelMediaInput(
    string MediaType,
    string DisplayName,
    string ContentBase64);

public sealed record ChannelMediaReference(
    Guid MediaId,
    string MediaType,
    string DisplayName,
    long ContentLength,
    string ContentSha256,
    string RelativePath);

public sealed record ChannelInboundEnvelope(
    int SchemaVersion,
    string MessageId,
    string ConversationId,
    DateTimeOffset SentAtUtc,
    string? Text,
    IReadOnlyList<ChannelMediaInput> Attachments);

public sealed record ChannelInboundRequest(
    string ChannelId,
    string BodySha256,
    ChannelInboundEnvelope Envelope);

public sealed record ChannelInboundReceipt(
    Guid ReceiptId,
    Guid CorrelationId,
    bool Duplicate);

public interface IChannelInboundSink
{
    ValueTask<ChannelInboundReceipt> AcceptAsync(
        ChannelInboundRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record ChannelOutboundEnvelope(
    int SchemaVersion,
    Guid DeliveryId,
    string SourceMessageId,
    string ConversationId,
    Guid ThreadId,
    Guid TurnId,
    string Status,
    string? Text,
    string? ErrorCode,
    Guid CorrelationId,
    DateTimeOffset CreatedAtUtc,
    bool Truncated = false);

public sealed record ChannelSendRequest(
    string ChannelId,
    Uri CallbackUrl,
    ChannelOutboundEnvelope Envelope,
    string BodySha256);

public sealed record ChannelSendResult(
    bool Succeeded,
    bool Retryable,
    TimeSpan? RetryAfter = null,
    string? ErrorCode = null);

public interface IChannelSender
{
    ValueTask<ChannelSendResult> SendAsync(
        ChannelSendRequest request,
        ReadOnlyMemory<byte> secret,
        CancellationToken cancellationToken = default);
}

public sealed record ChannelListQuery(
    ChannelRuntimeStatus? Status = null,
    int PageSize = 100,
    string? Cursor = null);

public sealed record ChannelInboundQuery(
    string? ChannelId = null,
    ChannelInboundStatus? Status = null,
    int PageSize = 100,
    string? Cursor = null);

public sealed record ChannelOutboxQuery(
    string? ChannelId = null,
    ChannelOutboxStatus? Status = null,
    int PageSize = 100,
    string? Cursor = null);

public sealed record ChannelSnapshot(
    string ChannelId,
    string Kind,
    bool Enabled,
    string DefinitionSha256,
    string TrustStatus,
    ChannelRuntimeStatus RuntimeStatus,
    string? DiagnosticCode,
    long Revision,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record ChannelMediaSummary(
    Guid MediaId,
    string MediaType,
    string DisplayName,
    long ContentLength,
    string ContentSha256);

public sealed record ChannelInboundSummary(
    Guid InboundMessageId,
    string ChannelId,
    string ExternalMessageId,
    string ExternalConversationId,
    long PartitionSequence,
    string BodySha256,
    Guid CorrelationId,
    Guid? ThreadId,
    Guid? TurnId,
    ChannelInboundStatus Status,
    int AttemptCount,
    string? ErrorCode,
    IReadOnlyList<ChannelMediaSummary> Media,
    long Revision,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? DeliveredAtUtc);

public sealed record ChannelOutboxSummary(
    Guid OutboxMessageId,
    Guid DeliveryId,
    string ChannelId,
    string ExternalConversationId,
    string SourceMessageId,
    Guid ThreadId,
    Guid TurnId,
    Guid CorrelationId,
    long PartitionSequence,
    string BodySha256,
    ChannelOutboxStatus Status,
    int AttemptCount,
    string? ErrorCode,
    long Revision,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? SentAtUtc);

public sealed record ChannelPage<T>(
    long OperationsRevision,
    IReadOnlyList<T> Items,
    string? NextCursor);

public sealed record ChannelMediaReadRequest(
    Guid MediaId,
    long Offset = 0,
    int Length = 256 * 1024);

public sealed record ChannelMediaChunk(
    Guid MediaId,
    string MediaType,
    long Offset,
    byte[] Data,
    bool EndOfFile);

public sealed record ChannelDeadLetterRetryRequest(
    Guid OutboxMessageId,
    Guid IdempotencyKey,
    long ExpectedRevision);

public interface IChannelService
{
    Task<ChannelPage<ChannelSnapshot>> ListChannelsAsync(
        ChannelListQuery query,
        CancellationToken cancellationToken = default);

    Task<ChannelSnapshot?> GetChannelAsync(
        string channelId,
        CancellationToken cancellationToken = default);

    Task<ChannelPage<ChannelInboundSummary>> ListInboundAsync(
        ChannelInboundQuery query,
        CancellationToken cancellationToken = default);

    Task<ChannelPage<ChannelOutboxSummary>> ListOutboxAsync(
        ChannelOutboxQuery query,
        CancellationToken cancellationToken = default);

    Task<ChannelMediaChunk> ReadMediaAsync(
        ChannelMediaReadRequest request,
        CancellationToken cancellationToken = default);

    Task<ChannelOutboxSummary> RetryDeadLetterAsync(
        ChannelDeadLetterRetryRequest request,
        CancellationToken cancellationToken = default);
}
