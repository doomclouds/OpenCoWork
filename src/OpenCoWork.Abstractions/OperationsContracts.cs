using System.Diagnostics;

namespace OpenCoWork.Abstractions;

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
