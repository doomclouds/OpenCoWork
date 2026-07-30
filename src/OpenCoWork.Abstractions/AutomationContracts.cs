using System.Text.Json;

namespace OpenCoWork.Abstractions;

public enum AutomationActorKind
{
    Host,
    Scheduler,
}

public enum AutomationDefinitionSourceStatus
{
    Ready,
    Faulted,
    Missing,
}

public enum AutomationWorkspaceMode
{
    Project,
    Worktree,
}

public enum AutomationTriggerKind
{
    Manual,
    Cron,
}

public enum AutomationRunStatus
{
    Pending,
    Running,
    NeedsAttention,
    Completed,
    Failed,
    Cancelled,
    TimedOut,
}

public enum AutomationAttentionKind
{
    ApprovalRequired,
    UserInputRequired,
    OutcomeUnknown,
}

public enum AutomationAttentionResolutionKind
{
    Approve,
    Reject,
    ProvideInput,
    Fail,
    Cancel,
}

public enum AutomationResourceAvailability
{
    Available,
    Missing,
    Deleted,
}

public static class AutomationRuntimeLimits
{
    public const int DefaultMaxConcurrentRuns = 3;
    public const int MaximumConcurrentRuns = 16;
    public const int MaximumDefinitionBytes = 256 * 1024;
    public const int MaximumDocumentDepth = 64;
    public const int MaximumDocumentNodes = 4096;
    public const int MaximumInputBytes = 256 * 1024;
    public const int MaximumRenderedPromptBytes = 256 * 1024;
    public const int MaximumDiagnostics = 32;
    public const int MaximumSummaryBytes = 16 * 1024;
    public const int DispatchAttempts = 5;
    public const int DefaultPageSize = 100;
    public const int MaximumPageSize = 100;
    public static readonly TimeSpan MinimumRunTimeout = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan DefaultRunTimeout = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan MaximumRunTimeout = TimeSpan.FromHours(24);
    public static readonly TimeSpan MinimumAttentionTimeout = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan DefaultAttentionTimeout = TimeSpan.FromHours(24);
    public static readonly TimeSpan MaximumAttentionTimeout = TimeSpan.FromHours(168);
    public static readonly TimeSpan DispatchLease = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan LeaseRenewal = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DefinitionWatcherDebounce =
        TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(2);
}

public static class AutomationErrorCodes
{
    public const string NotFound = "automation.notFound";
    public const string Conflict = "automation.conflict";
    public const string RunConflict = "automation.runConflict";
    public const string InvalidState = "automation.invalidState";
    public const string InvalidCursor = "automation.invalidCursor";
    public const string DefinitionInvalid = "automation.definitionInvalid";
    public const string InputInvalid = "automation.inputInvalid";
    public const string PermissionDenied = "automation.permissionDenied";
    public const string SecretDetected = "automation.secretDetected";
    public const string CapabilityUnavailable = "automation.capabilityUnavailable";
    public const string PathEscape = "automation.pathEscape";
    public const string WorktreeDirty = "automation.worktreeDirty";
    public const string OutcomeUnknown = "automation.outcomeUnknown";
    public const string LeaseLost = "automation.leaseLost";
    public const string RetryExhausted = "automation.retryExhausted";
    public const string SchemaInvalid = "automation.schemaInvalid";
    public const string Unavailable = "automation.unavailable";
}

public sealed record AutomationActorContext(
    AutomationActorKind Kind,
    string PrincipalId);

public sealed record AutomationError(
    string Code,
    string Message,
    bool IsRetryable = false);

public sealed record AutomationResult<T>(
    T? Value,
    long AutomationRevision,
    AutomationError? Error,
    bool IsReplay = false)
{
    public bool IsSuccess => Error is null;
}

public sealed record AutomationPage<T>(
    IReadOnlyList<T> Items,
    string? NextCursor);

public sealed record AutomationDefinitionSummary(
    string AutomationId,
    string DisplayName,
    bool Enabled,
    AutomationDefinitionSourceStatus SourceStatus,
    string? DefinitionVersion,
    bool HasSchedule,
    long Revision);

public sealed record AutomationDefinitionSnapshot(
    AutomationDefinitionSummary Summary,
    string SourceRelativePath,
    JsonElement? Definition,
    IReadOnlyList<OpenCoWorkDiagnostic> Diagnostics);

public sealed record AutomationScheduleSnapshot(
    string AutomationId,
    string Cron,
    string TimeZone,
    DateTimeOffset? NextOccurrenceUtc,
    DateTimeOffset? LastOccurrenceUtc,
    DateTimeOffset? CoalescedOccurrenceUtc,
    long Revision);

public sealed record AutomationRunSummary(
    Guid RunId,
    string AutomationId,
    AutomationTriggerKind TriggerKind,
    AutomationRunStatus Status,
    AutomationAttentionKind? AttentionKind,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    long Revision);

public sealed record AutomationCapabilitySnapshot(
    string Kind,
    string Id,
    string? Version,
    string Sha256,
    long Generation);

public sealed record AutomationPermissionSnapshot(
    string TrustSnapshotId,
    long CatalogRevision,
    IReadOnlyList<string> Plugins,
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> Tools,
    IReadOnlyList<string> Effects);

public sealed record AutomationRunSnapshot(
    AutomationRunSummary Summary,
    string? SafeSummary,
    AutomationError? Error,
    Guid? ThreadId,
    AutomationResourceAvailability ThreadAvailability,
    Guid? WorktreeId,
    AutomationResourceAvailability WorktreeAvailability,
    DateTimeOffset RunDeadlineUtc,
    DateTimeOffset? AttentionDeadlineUtc,
    string ProviderId,
    string ModelId,
    AutomationPermissionSnapshot Permissions,
    IReadOnlyList<AutomationCapabilitySnapshot> Capabilities);

public sealed record ListAutomationDefinitionsRequest(
    AutomationActorContext Actor,
    int PageSize = AutomationRuntimeLimits.DefaultPageSize,
    string? Cursor = null);

public sealed record GetAutomationDefinitionRequest(
    AutomationActorContext Actor,
    string AutomationId);

public sealed record ListAutomationSchedulesRequest(
    AutomationActorContext Actor,
    int PageSize = AutomationRuntimeLimits.DefaultPageSize,
    string? Cursor = null);

public sealed record GetAutomationScheduleRequest(
    AutomationActorContext Actor,
    string AutomationId);

public sealed record StartAutomationRunRequest(
    AutomationActorContext Actor,
    string AutomationId,
    JsonElement Inputs,
    Guid CommandId,
    long ExpectedRevision);

public sealed record ListAutomationRunsRequest(
    AutomationActorContext Actor,
    string? AutomationId = null,
    AutomationRunStatus? Status = null,
    int PageSize = AutomationRuntimeLimits.DefaultPageSize,
    string? Cursor = null);

public sealed record GetAutomationRunRequest(
    AutomationActorContext Actor,
    Guid RunId);

public sealed record CancelAutomationRunRequest(
    AutomationActorContext Actor,
    Guid RunId,
    Guid CommandId,
    long ExpectedRevision);

public sealed record AutomationAttentionResolution(
    AutomationAttentionResolutionKind Kind,
    string? Text = null);

public sealed record ResolveAutomationAttentionRequest(
    AutomationActorContext Actor,
    Guid RunId,
    Guid AttentionId,
    AutomationAttentionResolution Resolution,
    Guid CommandId,
    long ExpectedRevision);

public interface IAutomationService
{
    Task<AutomationResult<AutomationPage<AutomationDefinitionSummary>>>
        ListDefinitionsAsync(
            ListAutomationDefinitionsRequest request,
            CancellationToken cancellationToken = default);

    Task<AutomationResult<AutomationDefinitionSnapshot>> GetDefinitionAsync(
        GetAutomationDefinitionRequest request,
        CancellationToken cancellationToken = default);

    Task<AutomationResult<AutomationPage<AutomationScheduleSnapshot>>>
        ListSchedulesAsync(
            ListAutomationSchedulesRequest request,
            CancellationToken cancellationToken = default);

    Task<AutomationResult<AutomationScheduleSnapshot>> GetScheduleAsync(
        GetAutomationScheduleRequest request,
        CancellationToken cancellationToken = default);

    Task<AutomationResult<AutomationRunSnapshot>> StartRunAsync(
        StartAutomationRunRequest request,
        CancellationToken cancellationToken = default);

    Task<AutomationResult<AutomationPage<AutomationRunSummary>>> ListRunsAsync(
        ListAutomationRunsRequest request,
        CancellationToken cancellationToken = default);

    Task<AutomationResult<AutomationRunSnapshot>> GetRunAsync(
        GetAutomationRunRequest request,
        CancellationToken cancellationToken = default);

    Task<AutomationResult<AutomationRunSnapshot>> CancelRunAsync(
        CancelAutomationRunRequest request,
        CancellationToken cancellationToken = default);

    Task<AutomationResult<AutomationRunSnapshot>> ResolveAttentionAsync(
        ResolveAutomationAttentionRequest request,
        CancellationToken cancellationToken = default);
}
