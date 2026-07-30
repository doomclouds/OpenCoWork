using System.Collections.ObjectModel;
using System.Text.Json;

namespace OpenCoWork.Abstractions;

public enum ToolSourceKind
{
    CoreNative,
    PluginNative,
    Mcp,
    RuntimeDynamic,
}

public sealed record ToolDefinitionId(
    ToolSourceKind SourceKind,
    string SourceId,
    string SourceToolId);

public sealed record ToolName(string Namespace, string Name);

public sealed record RuntimeBindingId(string Value);

[Flags]
public enum ToolEffect
{
    None = 0,
    WorkspaceRead = 1 << 0,
    WorkspaceWrite = 1 << 1,
    ProcessExecution = 1 << 2,
    NetworkRead = 1 << 3,
    ExternalMutation = 1 << 4,
}

public enum ToolAuthorityDecision
{
    Deny,
    RequireApproval,
    Allow,
}

public enum ToolExposure
{
    Direct,
    Deferred,
    Hidden,
}

[Flags]
public enum ToolInvocationAudience
{
    None = 0,
    Model = 1 << 0,
    Host = 1 << 1,
    App = 1 << 2,
}

public enum ToolBindingAvailability
{
    Available,
    Unavailable,
}

public enum ToolReplaySafety
{
    Unsafe,
    Safe,
}

public enum ToolInvocationStatus
{
    Started,
    WaitingApproval,
    Completed,
    Rejected,
    Failed,
    Cancelled,
    TimedOut,
    OutcomeUnknown,
}

public static class ToolRuntimeLimits
{
    public const int MaximumSchemaBytes = 64 * 1024;
    public const int MaximumSnapshotBytes = 1024 * 1024;
    public const int MaximumArgumentsBytes = 512 * 1024;
    public const int MaximumJsonDepth = 64;
    public const int MaximumBindingResultBytes = 1024 * 1024;
    public const int MaximumResultEnvelopeBytes = 256 * 1024;
    public static readonly TimeSpan TurnExecutionBudget = TimeSpan.FromMinutes(30);
}

public static class ToolErrorCodes
{
    public const string DefinitionInvalid = "tool.definitionInvalid";
    public const string NameConflict = "tool.nameConflict";
    public const string SnapshotTooLarge = "tool.snapshotTooLarge";
    public const string IterationLimitExceeded = "tool.iterationLimitExceeded";
    public const string NotFound = "tool.notFound";
    public const string CallIdConflict = "tool.callIdConflict";
    public const string AudienceDenied = "tool.audienceDenied";
    public const string ExposureDenied = "tool.exposureDenied";
    public const string ModeDenied = "tool.modeDenied";
    public const string BindingUnavailable = "tool.bindingUnavailable";
    public const string BindingGenerationMismatch = "tool.bindingGenerationMismatch";
    public const string TrustRequired = "trust.required";
    public const string LeaseExpired = "tool.leaseExpired";
    public const string AuthorityDenied = "tool.authorityDenied";
    public const string InputInvalid = "tool.inputInvalid";
    public const string InputTooLarge = "tool.inputTooLarge";
    public const string SensitiveInputRejected = "tool.sensitiveInputRejected";
    public const string PolicyDenied = "tool.policyDenied";
    public const string HookDenied = "tool.hookDenied";
    public const string HookFailed = "tool.hookFailed";
    public const string ApprovalDenied = "tool.approvalDenied";
    public const string ExecutionFailed = "tool.executionFailed";
    public const string ResultInvalid = "tool.resultInvalid";
    public const string OutputLimitExceeded = "tool.outputLimitExceeded";
    public const string Timeout = "tool.timeout";
    public const string Cancelled = "tool.cancelled";
    public const string OutcomeUnknown = "tool.outcomeUnknown";
    public const string PathDenied = "tool.pathDenied";
    public const string PathNotFound = "tool.pathNotFound";
    public const string ContentUnsupported = "tool.contentUnsupported";
    public const string PreconditionFailed = "tool.preconditionFailed";
    public const string NetworkTargetDenied = "tool.networkTargetDenied";
}

public static class DynamicToolErrorCodes
{
    public const string DefinitionInvalid = "dynamicTool.definitionInvalid";
    public const string LimitExceeded = "dynamicTool.limitExceeded";
    public const string NotFound = "dynamicTool.notFound";
    public const string Disconnected = "dynamicTool.disconnected";
    public const string LeaseExpired = "dynamicTool.leaseExpired";
}

public static class BackgroundTerminalErrorCodes
{
    public const string SessionConflict = "terminal.sessionConflict";
    public const string LimitExceeded = "terminal.limitExceeded";
    public const string Lost = "terminal.lost";
    public const string ResetRequired = "terminal.resetRequired";
}

public static class WorkspaceMemoryErrorCodes
{
    public const string VersionConflict = "memory.versionConflict";
}

public static class McpToolErrorCodes
{
    public const string Disconnected = "mcp.disconnected";
    public const string CallFailed = "mcp.callFailed";
    public const string InvalidResponse = "mcp.invalidResponse";
}

public sealed record ToolDefinition
{
    public ToolDefinition(
        ToolDefinitionId id,
        ToolName name,
        string description,
        JsonElement inputSchema,
        ToolEffect effects,
        ToolReplaySafety replaySafety = ToolReplaySafety.Unsafe)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(description);
        Id = id;
        Name = name;
        Description = description;
        InputSchema = inputSchema.Clone();
        Effects = effects;
        ReplaySafety = replaySafety;
    }

    public ToolDefinitionId Id { get; }

    public ToolName Name { get; }

    public string Description { get; }

    public JsonElement InputSchema { get; }

    public ToolEffect Effects { get; }

    public ToolReplaySafety ReplaySafety { get; }
}

public delegate ValueTask<ToolBindingResult> ToolExecutor(
    JsonElement arguments,
    CancellationToken cancellationToken);

public delegate ValueTask<ToolBindingResult> ContextualToolExecutor(
    ToolInvocationContext context,
    CancellationToken cancellationToken);

public sealed record ToolBindingLease(string LeaseId, DateTimeOffset? ExpiresAt);

public sealed record ToolRuntimeBinding(
    RuntimeBindingId Id,
    ToolBindingAvailability Availability,
    ToolBindingLease? Lease,
    TimeSpan DefaultTimeout,
    ToolExecutor Executor,
    long Generation = 1,
    bool IsTrusted = true,
    ContextualToolExecutor? ContextualExecutor = null);

public sealed record ToolRegistration(
    ToolDefinition Definition,
    RuntimeBindingId RuntimeBindingId,
    ToolExposure Exposure,
    ToolInvocationAudience Audience,
    long BindingGeneration = 1);

public sealed record ToolSnapshotDiagnostic(
    string Code,
    ToolDefinitionId? DefinitionId,
    string? CanonicalName);

public sealed record ToolAuthorityPolicy(
    ToolEffect Effect,
    ToolAuthorityDecision Decision);

public sealed class EffectiveToolSnapshot
{
    public EffectiveToolSnapshot(
        int schemaVersion,
        AgentMode effectiveAgentMode,
        IReadOnlyList<ToolAuthorityPolicy> authority,
        IReadOnlyList<ToolRegistration> registrations,
        IReadOnlyDictionary<string, string> canonicalToProviderNames,
        IReadOnlyDictionary<string, string> providerToCanonicalNames,
        IReadOnlyList<ToolSnapshotDiagnostic> diagnostics,
        string snapshotSha256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(schemaVersion, 1);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(canonicalToProviderNames);
        ArgumentNullException.ThrowIfNull(providerToCanonicalNames);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotSha256);
        if (registrations.Any(registration =>
                registration is null || registration.BindingGeneration <= 0))
        {
            throw new ArgumentException(
                "Tool registrations must use a positive Binding Generation.",
                nameof(registrations));
        }

        SchemaVersion = schemaVersion;
        EffectiveAgentMode = effectiveAgentMode;
        Authority = Array.AsReadOnly(authority.ToArray());
        Registrations = Array.AsReadOnly(registrations.ToArray());
        CanonicalToProviderNames = ReadOnly(canonicalToProviderNames);
        ProviderToCanonicalNames = ReadOnly(providerToCanonicalNames);
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
        SnapshotSha256 = snapshotSha256;
    }

    public int SchemaVersion { get; }

    public AgentMode EffectiveAgentMode { get; }

    public IReadOnlyList<ToolAuthorityPolicy> Authority { get; }

    public IReadOnlyList<ToolRegistration> Registrations { get; }

    public IReadOnlyDictionary<string, string> CanonicalToProviderNames { get; }

    public IReadOnlyDictionary<string, string> ProviderToCanonicalNames { get; }

    public IReadOnlyList<ToolSnapshotDiagnostic> Diagnostics { get; }

    public string SnapshotSha256 { get; }

    private static IReadOnlyDictionary<string, string> ReadOnly(
        IReadOnlyDictionary<string, string> source) =>
        new ReadOnlyDictionary<string, string>(
            source.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
}

public sealed record ToolInvocationContext(
    Guid ThreadId,
    Guid TurnId,
    Guid ToolInvocationId,
    Guid ToolCallItemId,
    int CallIndex,
    string ProviderToolCallId,
    string ProviderToolName,
    JsonElement Arguments,
    string ArgumentsSha256,
    bool SensitiveInputDetected,
    EffectiveToolSnapshot Snapshot,
    SessionExecutionCheckpoint? ApprovalCheckpoint = null,
    DateTimeOffset? ApprovalTimeoutAt = null,
    bool? ApprovalGranted = null,
    int PriorAttemptCount = 0,
    TimeSpan? RemainingExecutionBudget = null,
    ToolResultSnapshot? ReplayResult = null,
    bool ProviderCallIdConflict = false,
    EffectiveSkillSnapshot? Skills = null,
    IReadOnlyList<ToolDefinitionId>? ActivatedDeferredTools = null,
    ExecutionWorkspaceDescriptor? ExecutionWorkspace = null,
    CoWorkThreadProvenance? CoWorkProvenance = null);

public sealed class ToolBindingResult
{
    private ToolBindingResult(
        JsonElement? output,
        SessionError? error,
        IEnumerable<ToolDefinitionId>? deferredActivations = null)
    {
        Output = output?.Clone();
        Error = error;
        DeferredActivations = Array.AsReadOnly(
            (deferredActivations ?? []).ToArray());
    }

    public JsonElement? Output { get; }

    public SessionError? Error { get; }

    public IReadOnlyList<ToolDefinitionId> DeferredActivations { get; }

    public bool IsSuccess => Error is null;

    public static ToolBindingResult Success(JsonElement output) =>
        new(output, error: null);

    public static ToolBindingResult Success(
        JsonElement output,
        IEnumerable<ToolDefinitionId> deferredActivations)
    {
        ArgumentNullException.ThrowIfNull(deferredActivations);
        return new(output, error: null, deferredActivations);
    }

    public static ToolBindingResult Failure(SessionError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(output: null, error);
    }
}

public sealed class ToolResultSnapshot
{
    public ToolResultSnapshot(
        Guid ToolInvocationId,
        string ProviderToolCallId,
        ToolInvocationStatus Status,
        JsonElement? Output,
        SessionError? Error,
        bool IsTruncated,
        int OriginalByteCount,
        string ResultSha256,
        int AttemptCount)
    {
        ArgumentNullException.ThrowIfNull(ProviderToolCallId);
        ArgumentOutOfRangeException.ThrowIfNegative(OriginalByteCount);
        ArgumentException.ThrowIfNullOrWhiteSpace(ResultSha256);
        ArgumentOutOfRangeException.ThrowIfNegative(AttemptCount);
        this.ToolInvocationId = ToolInvocationId;
        this.ProviderToolCallId = ProviderToolCallId;
        this.Status = Status;
        this.Output = Output?.Clone();
        this.Error = Error;
        this.IsTruncated = IsTruncated;
        this.OriginalByteCount = OriginalByteCount;
        this.ResultSha256 = ResultSha256;
        this.AttemptCount = AttemptCount;
    }

    public Guid ToolInvocationId { get; }

    public string ProviderToolCallId { get; }

    public ToolInvocationStatus Status { get; }

    public JsonElement? Output { get; }

    public SessionError? Error { get; }

    public bool IsTruncated { get; }

    public int OriginalByteCount { get; }

    public string ResultSha256 { get; }

    public int AttemptCount { get; }
}

public sealed record ToolInvocationSnapshot(
    Guid ToolInvocationId,
    Guid ThreadId,
    Guid TurnId,
    string ProviderToolCallId,
    string ProviderToolName,
    ToolDefinitionId? ToolDefinitionId,
    RuntimeBindingId? RuntimeBindingId,
    string SnapshotSha256,
    string ArgumentsSha256,
    ToolInvocationStatus Status,
    int AttemptCount,
    Guid? ResultItemId,
    string? ErrorCode,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt);

public interface IToolInvocationPipeline
{
    ValueTask<ToolResultSnapshot> InvokeAsync(
        ToolInvocationContext context,
        ISessionExecutionSink sink,
        CancellationToken cancellationToken);
}
