using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
    Tool,
}

public enum ChatCompletionFinishReason
{
    Stop,
    Length,
    ContentFilter,
    ToolCall,
    Unknown,
}

public enum ProviderInvocationPurpose
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
    public const string ProviderResponseFailed = "provider.responseFailed";
    public const string ContextInputInvalid = "context.inputInvalid";
    public const string ContextInputTooLarge = "context.inputTooLarge";
    public const string ContextInstructionsInvalid = "context.instructionsInvalid";
    public const string ContextCompactionFailed = "context.compactionFailed";
}

public sealed record ChatCompletionToolCall(
    string Id,
    string Name,
    string Arguments);

public sealed record ChatCompletionMessage(
    ChatCompletionMessageRole Role,
    string Content,
    IReadOnlyList<ChatCompletionToolCall>? ToolCalls = null,
    string? ToolCallId = null);

public sealed record ChatCompletionToolDefinition
{
    public ChatCompletionToolDefinition(
        string providerName,
        string description,
        JsonElement inputSchema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentNullException.ThrowIfNull(description);
        ProviderName = providerName;
        Description = description;
        InputSchema = inputSchema.Clone();
    }

    public string ProviderName { get; }

    public string Description { get; }

    public JsonElement InputSchema { get; }
}

public sealed class ChatCompletionRequest
{
    public ChatCompletionRequest(
        string modelId,
        IEnumerable<ChatCompletionMessage> messages,
        int maxOutputTokens,
        Guid invocationId,
        int attemptNumber,
        ProviderInvocationPurpose purpose,
        IEnumerable<ChatCompletionToolDefinition>? tools = null)
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
        Tools = purpose == ProviderInvocationPurpose.Response
            ? Array.AsReadOnly((tools ?? []).ToArray())
            : Array.AsReadOnly<ChatCompletionToolDefinition>([]);
    }

    public string ModelId { get; }

    public IReadOnlyList<ChatCompletionMessage> Messages { get; }

    public int MaxOutputTokens { get; }

    public Guid InvocationId { get; }

    public int AttemptNumber { get; }

    public ProviderInvocationPurpose Purpose { get; }

    public IReadOnlyList<ChatCompletionToolDefinition> Tools { get; }
}

public abstract record ChatCompletionEvent;

public sealed record ChatCompletionContentDeltaEvent(string Delta)
    : ChatCompletionEvent;

public sealed record ChatCompletionReasoningDeltaEvent(string Delta)
    : ChatCompletionEvent;

public sealed record ChatCompletionToolCallDeltaEvent(
    int Index,
    string? Id,
    string? Name,
    string ArgumentsDelta) : ChatCompletionEvent;

public sealed record ChatCompletionToolCallCompletedEvent(
    int Index,
    string Id,
    string Name,
    string Arguments) : ChatCompletionEvent;

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
    int TokenCount,
    IReadOnlyList<string>? Sources = null);

public sealed record WorkspaceInstructionSnapshot(
    string RelativePath,
    string ContentSha256,
    int RawByteCount,
    int TokenCount);

public sealed record EffectiveSkillSnapshotItem(
    string Id,
    CapabilitySourceDescriptor Source,
    string Description,
    string MarkdownBody,
    string ContentSha256,
    bool IsActive,
    string? SelectedVariantId);

public sealed class EffectiveSkillSnapshot
{
    private const int MaximumSkillBytes = 64 * 1024;
    private const int MaximumSnapshotBytes = 1024 * 1024;

    public EffectiveSkillSnapshot(
        int schemaVersion,
        IReadOnlyList<EffectiveSkillSnapshotItem> items,
        string snapshotSha256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(schemaVersion, 1);
        ArgumentNullException.ThrowIfNull(items);
        if (!IsSha256(snapshotSha256))
        {
            throw new ArgumentException(
                "Skill snapshot SHA-256 must contain 64 lowercase hexadecimal characters.",
                nameof(snapshotSha256));
        }

        var copied = items.ToArray();
        if (copied.Any(item => item is null))
        {
            throw new ArgumentException("Effective Skill Snapshot is invalid.", nameof(items));
        }

        var ordered = copied.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        if (ordered.Any(item =>
                string.IsNullOrWhiteSpace(item.Id) ||
                item.Source is null ||
                item.Description is null ||
                item.MarkdownBody is null ||
                !IsSha256(item.ContentSha256) ||
                !string.Equals(
                    item.ContentSha256,
                    Hash(item.MarkdownBody),
                    StringComparison.Ordinal) ||
                Encoding.UTF8.GetByteCount(item.MarkdownBody) > MaximumSkillBytes) ||
            ordered.GroupBy(item => item.Id, StringComparer.Ordinal)
                .Any(group => group.Skip(1).Any()) ||
            JsonSerializer.SerializeToUtf8Bytes(ordered).Length > MaximumSnapshotBytes)
        {
            throw new ArgumentException("Effective Skill Snapshot is invalid.", nameof(items));
        }

        SchemaVersion = schemaVersion;
        Items = Array.AsReadOnly(ordered);
        SnapshotSha256 = snapshotSha256;
    }

    public static EffectiveSkillSnapshot Empty { get; } = Create([]);

    public static EffectiveSkillSnapshot Create(
        IReadOnlyList<EffectiveSkillSnapshotItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var ordered = items.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        return new EffectiveSkillSnapshot(
            1,
            ordered,
            Hash(JsonSerializer.SerializeToUtf8Bytes(ordered)));
    }

    public int SchemaVersion { get; }

    public IReadOnlyList<EffectiveSkillSnapshotItem> Items { get; }

    public string SnapshotSha256 { get; }

    private static string Hash(string value) =>
        Hash(Encoding.UTF8.GetBytes(value));

    private static string Hash(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

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
    string ConfigurationSha256,
    EffectiveToolSnapshot? Tools = null,
    long CapabilityRevision = 0,
    EffectiveSkillSnapshot? Skills = null,
    string ReasoningEffort = "high");

public sealed record ProviderUsageSnapshot(
    Guid InvocationId,
    int AttemptNumber,
    ProviderInvocationPurpose Purpose,
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    ProviderUsageSource Source,
    bool IsEstimate,
    int CachedPromptTokens = 0,
    int ReasoningCompletionTokens = 0);

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
