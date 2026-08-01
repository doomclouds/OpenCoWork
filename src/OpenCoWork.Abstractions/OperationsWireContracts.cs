namespace OpenCoWork.Abstractions;

public sealed record WireChannelGetRequest(string ChannelId);

public sealed record WireChannelMediaReadRequest(
    Guid MediaId,
    long Offset = 0,
    int Length = 256 * 1024);

public sealed record WireChannelDeadLetterRetryRequest(
    Guid OutboxMessageId,
    Guid CommandId,
    long ExpectedRevision);

public sealed record WireHubWorkspaceListRequest(
    int PageSize = 100,
    string? Cursor = null);

public sealed record WireHubWorkspaceGetRequest(Guid WorkspaceId);

public sealed record WireHeartbeatGetRequest(Guid? WorkspaceId = null);

public sealed record WireTraceGetRequest(string TraceId);

public sealed record WireInsightRunRequest(Guid CommandId);

public sealed record WireInsightListRequest(
    string Kind = "proposals",
    int PageSize = 100,
    string? Cursor = null);

public sealed record WireInsightListResponse(
    string Kind,
    IReadOnlyList<InsightRunSnapshot> Runs,
    IReadOnlyList<ImprovementProposalSnapshot> Proposals,
    string? NextCursor);

public sealed record WireInsightGetRequest(Guid ProposalId);

public sealed record WireInsightArchiveRequest(
    Guid ProposalId,
    Guid CommandId,
    long ExpectedRevision);

public sealed record WireOperationsChangedNotification(
    string ChangeKind,
    string? EntityId = null);
