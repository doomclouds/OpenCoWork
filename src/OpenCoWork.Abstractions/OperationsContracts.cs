using System.Diagnostics;
using System.Text.Json;

namespace OpenCoWork.Abstractions;

public static class OperationsErrorCodes
{
    public const string HubWorkspaceNotFound = "hub.workspaceNotFound";
    public const string HubRegistryInvalid = "hub.registryInvalid";
    public const string TraceNotFound = "trace.notFound";
    public const string TraceUnavailable = "trace.unavailable";
    public const string HeartbeatUnavailable = "heartbeat.unavailable";
    public const string InsightNotFound = "insight.notFound";
    public const string InsightRevisionConflict = "insight.revisionConflict";
    public const string InsightInvalidState = "insight.invalidState";
}

public sealed class OperationsServiceException : Exception
{
    public OperationsServiceException(
        string code,
        string message,
        bool retryable = false,
        long? currentRevision = null)
        : base(message)
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

public enum OperationsTimeBucket
{
    Hour,
    Day,
}

public sealed record UsageQuery(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    OperationsTimeBucket Bucket,
    string? ProviderId = null,
    string? ModelId = null,
    string? ChannelId = null,
    ProviderInvocationPurpose? Purpose = null,
    Guid? ThreadId = null);

public sealed record UsageAggregate(
    DateTimeOffset BucketStartUtc,
    string ProviderId,
    string ModelId,
    ProviderInvocationPurpose Purpose,
    Guid ThreadId,
    string? ChannelId,
    ProviderUsageSource Source,
    long PromptTokens,
    long CachedPromptTokens,
    long CompletionTokens,
    long ReasoningCompletionTokens,
    long TotalTokens);

public sealed record TraceListQuery(
    Guid? CorrelationId = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    int PageSize = 100,
    string? Cursor = null);

public sealed record TraceSummary(
    string TraceId,
    Guid? CorrelationId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    double DurationMs,
    int SpanCount,
    bool HasError);

public sealed record TraceSpanSnapshot(
    string TraceId,
    string SpanId,
    string? ParentSpanId,
    string Name,
    string Kind,
    string Status,
    Guid? CorrelationId,
    Guid? ThreadId,
    Guid? TurnId,
    Guid? AutomationRunId,
    Guid? AgentRunId,
    string? ChannelId,
    double DurationMs,
    IReadOnlyDictionary<string, string> Tags,
    string? ErrorCode,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc);

public sealed record OperationsPage<T>(
    IReadOnlyList<T> Items,
    string? NextCursor);

public interface IOperationsQueryService
{
    Task<IReadOnlyList<UsageAggregate>> QueryUsageAsync(
        UsageQuery query,
        CancellationToken cancellationToken = default);

    Task<OperationsPage<TraceSummary>> ListTracesAsync(
        TraceListQuery query,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TraceSpanSnapshot>> GetTraceAsync(
        string traceId,
        CancellationToken cancellationToken = default);

    Task<OperationsHeartbeatSnapshot?> GetHeartbeatAsync(
        CancellationToken cancellationToken = default);

    Task<OperationsDashboardSnapshot> GetDashboardAsync(
        CancellationToken cancellationToken = default);
}

public enum OperationsHealthStatus
{
    Healthy,
    Degraded,
    Unhealthy,
    Stopping,
    Stopped,
    Stale,
}

public sealed record OperationsModuleHealth(
    string ModuleId,
    string Status);

public sealed record OperationsHeartbeatSnapshot(
    Guid RuntimeInstanceId,
    string PrimaryHost,
    OperationsHealthStatus Status,
    string RuntimeStatus,
    IReadOnlyList<OperationsModuleHealth> Modules,
    int ReadyChannels,
    int FaultedChannels,
    int PendingInbound,
    int FailedInbound,
    int DeadLetterInbound,
    int PendingOutbox,
    int FailedOutbox,
    int DeadLetterOutbox,
    long TraceDroppedCount,
    bool SqliteWritable,
    DateTimeOffset? ReconcilerLastSuccessAtUtc,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? StoppedAtUtc,
    long Revision);

public sealed record OperationsDashboardSnapshot(
    Guid WorkspaceId,
    OperationsHeartbeatSnapshot? Heartbeat,
    int ReadyChannels,
    int FaultedChannels,
    int PendingInbound,
    int FailedInbound,
    int DeadLetterInbound,
    int PendingOutbox,
    int FailedOutbox,
    int DeadLetterOutbox,
    long Last24HoursTokens,
    int TraceErrors,
    int OpenProposals,
    DateTimeOffset ObservedAtUtc);

public sealed record WorkspaceRegistration(
    Guid WorkspaceId,
    string WorkspaceRoot,
    string DataRoot,
    string DisplayName,
    DateTimeOffset RegisteredAtUtc,
    DateTimeOffset LastSeenAtUtc);

public sealed record WorkspaceRegistryRoot(string UserProfileDirectory);

public enum HubWorkspaceAvailability
{
    Online,
    Stale,
    Stopped,
    Missing,
    Unavailable,
}

public sealed record HubWorkspaceSummary(
    WorkspaceRegistration Registration,
    HubWorkspaceAvailability Availability,
    OperationsHealthStatus? HealthStatus,
    string? Diagnostic);

public sealed record HubWorkspaceQuery(
    int PageSize = 100,
    string? Cursor = null);

public interface IWorkspaceRegistryService
{
    Task<WorkspaceRegistration> UpsertAsync(
        Guid workspaceId,
        string workspaceRoot,
        string dataRoot,
        string displayName,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkspaceRegistration>> ListAsync(
        CancellationToken cancellationToken = default);
}

public interface IHubService
{
    Task<IReadOnlyList<HubWorkspaceSummary>> ListWorkspacesAsync(
        CancellationToken cancellationToken = default);

    Task<OperationsPage<HubWorkspaceSummary>> ListWorkspacesAsync(
        HubWorkspaceQuery query,
        CancellationToken cancellationToken = default);

    Task<HubWorkspaceSummary?> GetWorkspaceAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default);

    Task<OperationsDashboardSnapshot?> GetDashboardAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default);
}

public enum InsightRunTrigger
{
    Manual,
    Scheduled,
}

public enum InsightRunStatus
{
    Running,
    Completed,
    Failed,
}

public enum ImprovementProposalType
{
    Reliability,
    Performance,
    Configuration,
    Maintenance,
}

public enum ImprovementProposalSeverity
{
    Info,
    Warning,
    Critical,
}

public enum ImprovementProposalStatus
{
    Open,
    Archived,
}

public sealed record InsightRunSnapshot(
    Guid InsightRunId,
    InsightRunTrigger Trigger,
    InsightRunStatus Status,
    DateTimeOffset HighWatermarkUtc,
    int ProposalCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    long Revision);

public sealed record InsightRunRequest(
    Guid CommandId,
    InsightRunTrigger Trigger);

public sealed record ImprovementProposalSnapshot(
    Guid ProposalId,
    string FingerprintSha256,
    ImprovementProposalType Type,
    ImprovementProposalSeverity Severity,
    string Summary,
    JsonElement Evidence,
    ImprovementProposalStatus Status,
    long Revision,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? ArchivedAtUtc);

public interface IWorkspaceInsightService
{
    Task<InsightRunSnapshot> RunAsync(
        InsightRunTrigger trigger,
        CancellationToken cancellationToken = default);

    Task<InsightRunSnapshot> RunAsync(
        InsightRunRequest request,
        CancellationToken cancellationToken = default);

    Task<OperationsPage<ImprovementProposalSnapshot>> ListAsync(
        int pageSize = 100,
        string? cursor = null,
        CancellationToken cancellationToken = default);

    Task<OperationsPage<InsightRunSnapshot>> ListRunsAsync(
        int pageSize = 100,
        string? cursor = null,
        CancellationToken cancellationToken = default);

    Task<ImprovementProposalSnapshot?> GetAsync(
        Guid proposalId,
        CancellationToken cancellationToken = default);

    Task<ImprovementProposalSnapshot> ArchiveAsync(
        Guid proposalId,
        long expectedRevision,
        CancellationToken cancellationToken = default);
}

public enum OperationsChangeKind
{
    Channel,
    Heartbeat,
    Insight,
}

public sealed record OperationsChangedEvent(
    OperationsChangeKind Kind,
    string ChangeKind,
    string? EntityId = null);

public interface IOperationsChangeSource
{
    event EventHandler<OperationsChangedEvent>? Changed;
}

public static class OpenCoWorkTelemetry
{
    public const string SourceName = "OpenCoWork";
    public const string GatewayReceive = "gateway.receive";
    public const string GatewayDispatch = "gateway.dispatch";
    public const string SessionTurn = "session.turn";
    public const string ProviderResponses = "provider.responses";
    public const string ToolInvoke = "tool.invoke";
    public const string AutomationRun = "automation.run";
    public const string CoWorkAgentRun = "cowork.agentRun";
    public const string GatewayOutboxSend = "gateway.outbox.send";

    public const string CorrelationIdTag = "opencowork.correlation_id";
    public const string ThreadIdTag = "opencowork.thread_id";
    public const string TurnIdTag = "opencowork.turn_id";
    public const string AutomationRunIdTag = "opencowork.automation_run_id";
    public const string AgentRunIdTag = "opencowork.agent_run_id";
    public const string ChannelIdTag = "opencowork.channel_id";
    public const string ProviderIdTag = "opencowork.provider_id";
    public const string ModelIdTag = "opencowork.model_id";
    public const string PurposeTag = "opencowork.purpose";
    public const string ToolIdTag = "opencowork.tool_id";
    public const string ErrorCodeTag = "opencowork.error_code";

    private static readonly ActivitySource Source = new(SourceName);

    public static Activity? StartActivity(
        string name,
        ActivityKind kind = ActivityKind.Internal,
        Guid? correlationId = null,
        Guid? threadId = null,
        Guid? turnId = null,
        Guid? automationRunId = null,
        Guid? agentRunId = null,
        string? channelId = null)
    {
        var activity = Source.StartActivity(name, kind);
        activity?.SetTag(CorrelationIdTag, correlationId?.ToString("D"));
        activity?.SetTag(ThreadIdTag, threadId?.ToString("D"));
        activity?.SetTag(TurnIdTag, turnId?.ToString("D"));
        activity?.SetTag(AutomationRunIdTag, automationRunId?.ToString("D"));
        activity?.SetTag(AgentRunIdTag, agentRunId?.ToString("D"));
        activity?.SetTag(ChannelIdTag, channelId);
        return activity;
    }
}
