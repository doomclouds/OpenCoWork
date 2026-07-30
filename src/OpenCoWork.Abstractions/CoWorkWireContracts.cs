namespace OpenCoWork.Abstractions;

public sealed record WireCoWorkResponse<T>(long CoWorkRevision, T Value);

public sealed record WireCoWorkChangedNotification(
    long CoWorkRevision,
    string ChangeKind,
    IReadOnlyList<Guid> AffectedIds);

public sealed record WireListAgentProfilesRequest(
    int PageSize = 100,
    string? Cursor = null);

public sealed record WireGetAgentProfileRequest(Guid ProfileId);

public sealed record WireUpsertAgentProfileRequest(
    Guid CommandId,
    long? ExpectedRevision,
    Guid? ProfileId,
    string Name,
    string Description,
    string Instructions,
    string ProviderId,
    string ModelId,
    IReadOnlyList<string> SkillAllowlist,
    IReadOnlyList<string> ToolAllowlist);

public sealed record WireSetAgentProfileEnabledRequest(
    Guid CommandId,
    long? ExpectedRevision,
    Guid ProfileId,
    bool Enabled);

public sealed record WireListTeamsRequest(int PageSize = 100, string? Cursor = null);

public sealed record WireGetTeamRequest(Guid TeamId);

public sealed record WireUpsertTeamRequest(
    Guid CommandId,
    long? ExpectedRevision,
    Guid? TeamId,
    string Name,
    string Description,
    IReadOnlyList<TeamMemberInput> Members);

public sealed record WireSetTeamEnabledRequest(
    Guid CommandId,
    long? ExpectedRevision,
    Guid TeamId,
    bool Enabled);

public sealed record WireSpawnSubAgentRequest(
    Guid CommandId,
    long? ExpectedRevision,
    Guid ParentThreadId,
    Guid ProfileId,
    string Task,
    long TokenBudget,
    string WorkspaceMode);

public sealed record WireSubAgentQueryRequest(
    Guid ParentThreadId,
    Guid? ChildThreadId = null,
    int PageSize = 100,
    string? Cursor = null);

public sealed record WireSendSubAgentMessageRequest(
    Guid CommandId,
    long? ExpectedRevision,
    Guid ChildThreadId,
    string Message);

public sealed record WireFollowUpSubAgentRequest(
    Guid CommandId,
    long? ExpectedRevision,
    Guid ChildThreadId,
    string Task);

public sealed record WireCancelSubAgentRequest(
    Guid CommandId,
    long? ExpectedRevision,
    Guid ChildThreadId);

public sealed record WireCreateMissionRequest(
    Guid CommandId,
    long? ExpectedRevision,
    Guid OriginThreadId,
    Guid TeamId,
    string Objective,
    long TokenBudget,
    string WorkspaceMode,
    bool AllowDirtyOrigin = false);

public sealed record WireListMissionsRequest(
    string? Status = null,
    int PageSize = 100,
    string? Cursor = null);

public sealed record WireGetMissionRequest(Guid MissionId);

public sealed record WireMissionCommandRequest(
    Guid CommandId,
    long? ExpectedRevision,
    Guid MissionId);

public sealed record WireAddMissionTaskRequest(
    Guid CommandId,
    long? ExpectedRevision,
    Guid MissionId,
    string Alias,
    string Objective,
    string Instructions,
    Guid AssignedMemberId,
    bool Required,
    bool? RequiresReview,
    IReadOnlyList<string> DependsOn);

public sealed record WireUpdateMissionTaskRequest(
    Guid CommandId,
    long? ExpectedRevision,
    Guid MissionId,
    Guid TaskId,
    string Objective,
    string Instructions,
    Guid AssignedMemberId,
    bool Required,
    bool? RequiresReview,
    IReadOnlyList<string> DependsOn);

public sealed record WireMissionTaskCommandRequest(
    Guid CommandId,
    long? ExpectedRevision,
    Guid MissionId,
    Guid TaskId);

public sealed record WireBlockMissionTaskRequest(
    Guid CommandId,
    long? ExpectedRevision,
    Guid MissionId,
    Guid TaskId,
    string Reason);

public sealed record WireReassignMissionTaskRequest(
    Guid CommandId,
    long? ExpectedRevision,
    Guid MissionId,
    Guid TaskId,
    Guid MemberId);

public sealed record WireReviewMissionTaskRequest(
    Guid CommandId,
    long? ExpectedRevision,
    Guid MissionId,
    Guid TaskId,
    bool Accepted,
    string? Comment);

public sealed record WireListMailboxMessagesRequest(
    Guid MissionId,
    string? Status = null,
    int PageSize = 100,
    string? Cursor = null);

public sealed record WireSendMailboxMessageRequest(
    Guid CommandId,
    long? ExpectedRevision,
    Guid MissionId,
    Guid RecipientId,
    string Kind,
    string Body,
    Guid? TaskId = null,
    Guid? ArtifactId = null);

public sealed record WireMailboxMessageCommandRequest(
    Guid CommandId,
    long? ExpectedRevision,
    Guid MessageId);

public sealed record WireListArtifactsRequest(
    Guid MissionId,
    int PageSize = 100,
    string? Cursor = null);

public sealed record WireGetArtifactRequest(Guid ArtifactId);

public sealed record WirePublishArtifactRequest(
    Guid CommandId,
    long? ExpectedRevision,
    Guid MissionId,
    Guid AgentRunId,
    string SourceArea,
    string SourceRelativePath,
    string DisplayName,
    string MediaType);

public sealed record WirePromoteArtifactRequest(
    Guid CommandId,
    long? ExpectedRevision,
    Guid ArtifactId);

public sealed record WireListWorktreesRequest(
    Guid? MissionId = null,
    int PageSize = 100,
    string? Cursor = null);

public sealed record WireGetWorktreeRequest(Guid WorktreeId);

public sealed record WireWorktreeCommandRequest(
    Guid CommandId,
    long? ExpectedRevision,
    Guid WorktreeId);
