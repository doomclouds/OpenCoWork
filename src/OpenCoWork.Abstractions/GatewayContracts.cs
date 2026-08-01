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
