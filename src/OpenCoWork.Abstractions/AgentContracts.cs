using System.Net;

namespace OpenCoWork.Abstractions;

public enum AgentMode
{
    Agent,
    Plan,
}

public enum ChatCompletionMessageRole
{
    System,
    User,
    Assistant,
}

public enum ChatCompletionFinishReason
{
    Stop,
    Length,
    ContentFilter,
    ToolCall,
    Unknown,
}

public enum ChatCompletionInvocationPurpose
{
    Response,
    Compaction,
}

public enum ProviderUsageSource
{
    Provider,
    LocalEstimate,
}

public static class AgentErrorCodes
{
    public const string ProviderAuthenticationFailed = "provider.authenticationFailed";
    public const string ProviderQuotaExceeded = "provider.quotaExceeded";
    public const string ProviderPermissionDenied = "provider.permissionDenied";
    public const string ProviderNotFound = "provider.notFound";
    public const string ProviderInvalidRequest = "provider.invalidRequest";
    public const string ProviderRateLimited = "provider.rateLimited";
    public const string ProviderTimeout = "provider.timeout";
    public const string ProviderServerUnavailable = "provider.serverUnavailable";
    public const string ProviderTlsFailure = "provider.tlsFailure";
    public const string ProviderRedirectNotAllowed = "provider.redirectNotAllowed";
    public const string ProviderInvalidStream = "provider.invalidStream";
    public const string ProviderOutputTooLarge = "provider.outputTooLarge";
    public const string ProviderContentFiltered = "provider.contentFiltered";
    public const string ProviderUnsupportedToolCall = "provider.unsupportedToolCall";
    public const string ProviderEmptyResponse = "provider.emptyResponse";
    public const string ContextInputInvalid = "context.inputInvalid";
    public const string ContextInputTooLarge = "context.inputTooLarge";
    public const string ContextInstructionsInvalid = "context.instructionsInvalid";
    public const string ContextCompactionFailed = "context.compactionFailed";
}

public sealed record ChatCompletionMessage(
    ChatCompletionMessageRole Role,
    string Content);

public sealed class ChatCompletionRequest
{
    public ChatCompletionRequest(
        string modelId,
        IEnumerable<ChatCompletionMessage> messages,
        int maxOutputTokens,
        Guid invocationId,
        int attemptNumber,
        ChatCompletionInvocationPurpose purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOutputTokens);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attemptNumber);

        ModelId = modelId;
        Messages = Array.AsReadOnly(messages.ToArray());
        MaxOutputTokens = maxOutputTokens;
        InvocationId = invocationId;
        AttemptNumber = attemptNumber;
        Purpose = purpose;
    }

    public string ModelId { get; }

    public IReadOnlyList<ChatCompletionMessage> Messages { get; }

    public int MaxOutputTokens { get; }

    public Guid InvocationId { get; }

    public int AttemptNumber { get; }

    public ChatCompletionInvocationPurpose Purpose { get; }
}

public abstract record ChatCompletionEvent;

public sealed record ChatCompletionContentDeltaEvent(string Delta)
    : ChatCompletionEvent;

public sealed record ChatCompletionReasoningDeltaEvent(string Delta)
    : ChatCompletionEvent;

public sealed record ChatCompletionUsage(
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens);

public sealed record ChatCompletionUsageEvent(ChatCompletionUsage Usage)
    : ChatCompletionEvent;

public sealed record ChatCompletionCompletedEvent(
    ChatCompletionFinishReason FinishReason)
    : ChatCompletionEvent;

public sealed class ChatCompletionException : Exception
{
    public ChatCompletionException(
        string code,
        string message,
        HttpStatusCode? statusCode = null,
        TimeSpan? retryAfter = null,
        bool isTransient = false,
        bool isPromptTooLong = false,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Code = code;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
        IsTransient = isTransient;
        IsPromptTooLong = isPromptTooLong;
    }

    public string Code { get; }

    public HttpStatusCode? StatusCode { get; }

    public TimeSpan? RetryAfter { get; }

    public bool IsTransient { get; }

    public bool IsPromptTooLong { get; }
}

public interface IChatCompletionClient
{
    IAsyncEnumerable<ChatCompletionEvent> StreamAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record AgentPromptSnapshot(
    string Version,
    string SystemMessageSha256,
    int TokenCount);

public sealed record WorkspaceInstructionSnapshot(
    string RelativePath,
    string ContentSha256,
    int RawByteCount,
    int TokenCount);

public sealed record AgentInvocationSnapshot(
    Guid InvocationId,
    string ProviderId,
    string ModelId,
    string TokenizerProfileId,
    string TokenizerProfileVersion,
    AgentMode EffectiveAgentMode,
    AgentPromptSnapshot ResponsePrompt,
    AgentPromptSnapshot CompactionPrompt,
    WorkspaceInstructionSnapshot? WorkspaceInstructions,
    int ContextWindowTokens,
    int MaxOutputTokens,
    string ConfigurationSha256);

public sealed record ProviderUsageSnapshot(
    Guid InvocationId,
    int AttemptNumber,
    ChatCompletionInvocationPurpose Purpose,
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    ProviderUsageSource Source,
    bool IsEstimate);

public sealed record CompactionCheckpointSnapshot(
    int SchemaVersion,
    string Summary,
    string SummarySha256,
    long SourceStartSequence,
    long SourceEndSequence,
    string SourceMessagesSha256,
    string SummaryPromptVersion,
    string TokenizerProfileId,
    string TokenizerProfileVersion,
    int SummaryTokenCount);
