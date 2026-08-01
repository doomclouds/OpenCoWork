namespace OpenCoWork.Abstractions;

public enum CoWorkActorKind
{
    Host,
    Leader,
    Member,
    DirectParent,
}

public enum CoWorkMemberRole
{
    Leader,
    Member,
}

public enum CoWorkWorkspaceMode
{
    Project,
    Worktree,
}

public enum CoWorkWorkspaceAccess
{
    ReadOnly,
    ReadWrite,
}

public enum CoWorkMissionStatus
{
    Planning,
    Active,
    AwaitingLeaderReview,
    Completed,
    Failed,
    Cancelled,
}

public enum CoWorkTaskStatus
{
    Pending,
    WaitingDependencies,
    Ready,
    Running,
    Blocked,
    Review,
    Completed,
    Failed,
    Cancelled,
}

public enum CoWorkAgentRunKind
{
    Direct,
    LeaderPlanning,
    LeaderReview,
    LeaderSynthesis,
    MissionTask,
}

public enum CoWorkAgentRunStatus
{
    Pending,
    Starting,
    Running,
    Completed,
    Failed,
    Cancelled,
}

public enum CoWorkMailboxScope
{
    Mission,
    Direct,
}

public enum CoWorkMailboxKind
{
    Info,
    Request,
    Handoff,
    Blocker,
    Review,
    Rework,
}

public enum CoWorkMailboxStatus
{
    Pending,
    Delivered,
    Acknowledged,
    DeadLettered,
}

public enum CoWorkFileKind
{
    Scratchpad,
    Artifact,
}

public enum CoWorkFileArea
{
    Workspace,
    Scratchpad,
}

public enum CoWorkArtifactVisibility
{
    Mission,
    Origin,
}

public enum CoWorkArtifactStatus
{
    Available,
    Unavailable,
}

public enum CoWorkWorktreeStatus
{
    Creating,
    Ready,
    Removing,
    Removed,
    RetainedDirty,
    Faulted,
}

public enum CoWorkDispatchKind
{
    CreateThread,
    CreateWorktree,
    SubmitTurn,
    DeliverMessage,
    FinalizeArtifact,
    SynthesizeMission,
    DeliverOrigin,
    Cleanup,
}

public enum CoWorkDispatchStatus
{
    Pending,
    Leased,
    Completed,
    DeadLettered,
}

public static class CoWorkRuntimeLimits
{
    public const int DefaultMaxDepth = 1;
    public const int MaximumDepth = 4;
    public const int DefaultMaximumConcurrentAgentRuns = 16;
    public const int MaximumConcurrentAgentRuns = 64;
    public const int DefaultMaximumConcurrentAgentRunsPerMission = 4;
    public const int MaximumMissionMembers = 16;
    public const int MaximumMissionTasks = 256;
    public const int MaximumMailboxMessageBytes = 64 * 1024;
    public const long MaximumArtifactBytes = 64L * 1024 * 1024;
    public const long MaximumOwnedFileBytes = 512L * 1024 * 1024;
    public const int DispatchAttempts = 5;
    public static readonly TimeSpan DispatchLease = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan LeaseRenewal = TimeSpan.FromSeconds(30);
}

public static class CoWorkErrorCodes
{
    public const string NotFound = "cowork.notFound";
    public const string Conflict = "cowork.conflict";
    public const string InvalidState = "cowork.invalidState";
    public const string PermissionDenied = "cowork.permissionDenied";
    public const string InvalidDag = "cowork.invalidDag";
    public const string BudgetExceeded = "cowork.budgetExceeded";
    public const string DepthExceeded = "cowork.depthExceeded";
    public const string ConcurrencyExceeded = "cowork.concurrencyExceeded";
    public const string MemberBusy = "cowork.memberBusy";
    public const string SecretDetected = "cowork.secretDetected";
    public const string PathEscape = "cowork.pathEscape";
    public const string ArtifactUnavailable = "cowork.artifactUnavailable";
    public const string WorktreeDirty = "cowork.worktreeDirty";
    public const string RetryExhausted = "cowork.retryExhausted";
    public const string SchemaInvalid = "cowork.schemaInvalid";
    public const string SessionUnavailable = "cowork.sessionUnavailable";
}

public sealed record CoWorkActorContext(
    CoWorkActorKind Kind,
    string PrincipalId,
    Guid? ThreadId = null,
    Guid? MissionId = null,
    Guid? MemberId = null);

public sealed record CoWorkCommandContext(
    Guid CommandId,
    CoWorkActorContext Actor,
    long? ExpectedRevision,
    Guid? CorrelationId = null);

public sealed record CoWorkError(
    string Code,
    string Message,
    bool IsRetryable = false);

public sealed record CoWorkResult<T>(
    T? Value,
    long CoWorkRevision,
    CoWorkError? Error,
    bool IsReplay = false)
{
    public bool IsSuccess => Error is null;
}

public sealed record CoWorkPage<T>(
    IReadOnlyList<T> Items,
    string? NextCursor);

public sealed record AgentProfileSnapshot(
    Guid ProfileId,
    string Name,
    string Description,
    string Instructions,
    string ProviderId,
    string ModelId,
    IReadOnlyList<string> SkillAllowlist,
    IReadOnlyList<string> ToolAllowlist,
    bool Enabled,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record TeamMemberSnapshot(
    Guid MemberId,
    string Alias,
    Guid ProfileId,
    CoWorkMemberRole Role,
    string Description,
    int Order);

public sealed record TeamSnapshot(
    Guid TeamId,
    string Name,
    string Description,
    IReadOnlyList<TeamMemberSnapshot> Members,
    bool Enabled,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record MissionMemberSnapshot(
    Guid MemberId,
    string Alias,
    CoWorkMemberRole Role,
    string Description,
    AgentProfileSnapshot Profile,
    int Order);

public sealed record MissionTaskSnapshot(
    Guid TaskId,
    Guid MissionId,
    string Alias,
    string Objective,
    string Instructions,
    Guid AssignedMemberId,
    bool Required,
    bool RequiresReview,
    IReadOnlyList<string> DependsOn,
    CoWorkTaskStatus Status,
    string? BlockedReason,
    int CurrentAttempt,
    string? OutputSummary,
    IReadOnlyList<Guid> ArtifactIds,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record MissionSnapshot(
    Guid MissionId,
    Guid OriginThreadId,
    Guid TeamId,
    long PlanningTeamRevision,
    string Objective,
    CoWorkMissionStatus Status,
    CoWorkWorkspaceMode WorkspaceMode,
    string? BaseCommitSha,
    long TokenBudget,
    Guid? LeaderThreadId,
    IReadOnlyList<MissionMemberSnapshot> Members,
    IReadOnlyList<MissionTaskSnapshot> Tasks,
    string? FinalSummary,
    string? OriginDeliveryId,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record BudgetScopeSnapshot(
    Guid BudgetScopeId,
    string OwnerKind,
    Guid OwnerId,
    long TokenLimit,
    long ReservedTokens,
    long UsedTokens);

public sealed record AgentRunSnapshot(
    Guid AgentRunId,
    CoWorkAgentRunKind Kind,
    CoWorkAgentRunStatus Status,
    Guid ThreadId,
    Guid? ParentRunId,
    Guid? ParentThreadId,
    Guid? PreviousRunId,
    Guid? MissionId,
    Guid? TaskId,
    Guid? MemberId,
    int Attempt,
    AgentProfileSnapshot Profile,
    ExecutionWorkspaceDescriptor ExecutionWorkspace,
    CoWorkWorkspaceAccess WorkspaceAccess,
    Guid BudgetScopeId,
    long ReservedTokens,
    long UsedTokens,
    string? ErrorCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    Guid? CorrelationId = null);

public sealed record DirectSubAgentSnapshot(
    Guid ChildThreadId,
    Guid ParentThreadId,
    Guid LineageRootThreadId,
    Guid ProfileId,
    Guid BudgetScopeId,
    ExecutionWorkspaceDescriptor ExecutionWorkspace,
    AgentRunSnapshot? ActiveRun,
    DateTimeOffset CreatedAt);

public sealed record MailboxMessageSnapshot(
    Guid MessageId,
    CoWorkMailboxScope Scope,
    Guid? MissionId,
    Guid SenderId,
    Guid RecipientId,
    CoWorkMailboxKind Kind,
    string Body,
    Guid? TaskId,
    Guid? ArtifactId,
    CoWorkMailboxStatus Status,
    int Attempt,
    string? ErrorCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ArtifactSnapshot(
    Guid ArtifactId,
    Guid MissionId,
    Guid OriginAgentRunId,
    string RelativePath,
    string Sha256,
    long Bytes,
    string MediaType,
    string DisplayName,
    CoWorkArtifactVisibility Visibility,
    CoWorkArtifactStatus Status,
    DateTimeOffset CreatedAt);

public sealed record WorktreeSnapshot(
    Guid WorktreeId,
    Guid? MissionId,
    Guid AgentRunId,
    string RelativePath,
    string BaseCommitSha,
    CoWorkWorktreeStatus Status,
    bool IsDirty,
    string? DiagnosticCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record DispatchIntentSnapshot(
    Guid DispatchIntentId,
    CoWorkDispatchKind Kind,
    string EntityKind,
    Guid EntityId,
    string IdempotencyKey,
    CoWorkDispatchStatus Status,
    int Attempt,
    DateTimeOffset? LeaseExpiresAt,
    string? ErrorCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Diagnostic = null);

public sealed record WorktreeHandoffSnapshot(
    Guid WorktreeId,
    string Path,
    string BaseCommitSha,
    CoWorkWorktreeStatus Status,
    IReadOnlyList<Guid> ArtifactIds);

public sealed record ListAgentProfilesRequest(
    CoWorkActorContext Actor,
    int PageSize = 100,
    string? Cursor = null);

public sealed record GetAgentProfileRequest(
    CoWorkActorContext Actor,
    Guid ProfileId);

public sealed record UpsertAgentProfileRequest(
    CoWorkCommandContext Command,
    Guid? ProfileId,
    string Name,
    string Description,
    string Instructions,
    string ProviderId,
    string ModelId,
    IReadOnlyList<string> SkillAllowlist,
    IReadOnlyList<string> ToolAllowlist);

public sealed record SetAgentProfileEnabledRequest(
    CoWorkCommandContext Command,
    Guid ProfileId,
    bool Enabled);

public sealed record ListTeamsRequest(
    CoWorkActorContext Actor,
    int PageSize = 100,
    string? Cursor = null);

public sealed record GetTeamRequest(
    CoWorkActorContext Actor,
    Guid TeamId);

public sealed record TeamMemberInput(
    Guid? MemberId,
    string Alias,
    Guid ProfileId,
    CoWorkMemberRole Role,
    string Description);

public sealed record UpsertTeamRequest(
    CoWorkCommandContext Command,
    Guid? TeamId,
    string Name,
    string Description,
    IReadOnlyList<TeamMemberInput> Members);

public sealed record SetTeamEnabledRequest(
    CoWorkCommandContext Command,
    Guid TeamId,
    bool Enabled);

public sealed record SpawnSubAgentRequest(
    CoWorkCommandContext Command,
    Guid ParentThreadId,
    Guid ProfileId,
    string Task,
    long TokenBudget,
    CoWorkWorkspaceMode WorkspaceMode);

public sealed record SubAgentQueryRequest(
    CoWorkActorContext Actor,
    Guid ParentThreadId,
    Guid? ChildThreadId = null,
    int PageSize = 100,
    string? Cursor = null);

public sealed record SendSubAgentMessageRequest(
    CoWorkCommandContext Command,
    Guid ChildThreadId,
    string Message);

public sealed record FollowUpSubAgentRequest(
    CoWorkCommandContext Command,
    Guid ChildThreadId,
    string Task);

public sealed record CancelSubAgentRequest(
    CoWorkCommandContext Command,
    Guid ChildThreadId);

public sealed record CreateMissionRequest(
    CoWorkCommandContext Command,
    Guid OriginThreadId,
    Guid TeamId,
    string Objective,
    long TokenBudget,
    CoWorkWorkspaceMode WorkspaceMode,
    bool AllowDirtyOrigin = false);

public sealed record ListMissionsRequest(
    CoWorkActorContext Actor,
    CoWorkMissionStatus? Status = null,
    int PageSize = 100,
    string? Cursor = null);

public sealed record GetMissionRequest(
    CoWorkActorContext Actor,
    Guid MissionId);

public sealed record MissionCommandRequest(
    CoWorkCommandContext Command,
    Guid MissionId);

public sealed record AddMissionTaskRequest(
    CoWorkCommandContext Command,
    Guid MissionId,
    string Alias,
    string Objective,
    string Instructions,
    Guid AssignedMemberId,
    bool Required,
    bool? RequiresReview,
    IReadOnlyList<string> DependsOn);

public sealed record UpdateMissionTaskRequest(
    CoWorkCommandContext Command,
    Guid MissionId,
    Guid TaskId,
    string Objective,
    string Instructions,
    Guid AssignedMemberId,
    bool Required,
    bool? RequiresReview,
    IReadOnlyList<string> DependsOn);

public sealed record MissionTaskCommandRequest(
    CoWorkCommandContext Command,
    Guid MissionId,
    Guid TaskId);

public sealed record BlockMissionTaskRequest(
    CoWorkCommandContext Command,
    Guid MissionId,
    Guid TaskId,
    string Reason);

public sealed record ReassignMissionTaskRequest(
    CoWorkCommandContext Command,
    Guid MissionId,
    Guid TaskId,
    Guid MemberId);

public sealed record ReviewMissionTaskRequest(
    CoWorkCommandContext Command,
    Guid MissionId,
    Guid TaskId,
    bool Accepted,
    string? Comment);

public sealed record ListMailboxMessagesRequest(
    CoWorkActorContext Actor,
    Guid MissionId,
    CoWorkMailboxStatus? Status = null,
    int PageSize = 100,
    string? Cursor = null);

public sealed record SendMailboxMessageRequest(
    CoWorkCommandContext Command,
    Guid MissionId,
    Guid RecipientId,
    CoWorkMailboxKind Kind,
    string Body,
    Guid? TaskId = null,
    Guid? ArtifactId = null);

public sealed record MailboxMessageCommandRequest(
    CoWorkCommandContext Command,
    Guid MessageId);

public sealed record ListArtifactsRequest(
    CoWorkActorContext Actor,
    Guid MissionId,
    int PageSize = 100,
    string? Cursor = null);

public sealed record GetArtifactRequest(
    CoWorkActorContext Actor,
    Guid ArtifactId);

public sealed record PublishArtifactRequest(
    CoWorkCommandContext Command,
    Guid MissionId,
    Guid AgentRunId,
    CoWorkFileArea SourceArea,
    string SourceRelativePath,
    string DisplayName,
    string MediaType);

public sealed record PromoteArtifactRequest(
    CoWorkCommandContext Command,
    Guid ArtifactId);

public sealed record ListWorktreesRequest(
    CoWorkActorContext Actor,
    Guid? MissionId = null,
    int PageSize = 100,
    string? Cursor = null);

public sealed record GetWorktreeRequest(
    CoWorkActorContext Actor,
    Guid WorktreeId);

public sealed record WorktreeCommandRequest(
    CoWorkCommandContext Command,
    Guid WorktreeId);

public interface ICoWorkService
{
    Task<CoWorkResult<CoWorkPage<AgentProfileSnapshot>>> ListAgentProfilesAsync(
        ListAgentProfilesRequest request,
        CancellationToken cancellationToken = default);

    Task<CoWorkResult<AgentProfileSnapshot>> GetAgentProfileAsync(
        GetAgentProfileRequest request,
        CancellationToken cancellationToken = default);

    Task<CoWorkResult<AgentProfileSnapshot>> UpsertAgentProfileAsync(
        UpsertAgentProfileRequest request,
        CancellationToken cancellationToken = default);

    Task<CoWorkResult<AgentProfileSnapshot>> SetAgentProfileEnabledAsync(
        SetAgentProfileEnabledRequest request,
        CancellationToken cancellationToken = default);

    Task<CoWorkResult<CoWorkPage<TeamSnapshot>>> ListTeamsAsync(
        ListTeamsRequest request,
        CancellationToken cancellationToken = default);

    Task<CoWorkResult<TeamSnapshot>> GetTeamAsync(
        GetTeamRequest request,
        CancellationToken cancellationToken = default);

    Task<CoWorkResult<TeamSnapshot>> UpsertTeamAsync(
        UpsertTeamRequest request,
        CancellationToken cancellationToken = default);

    Task<CoWorkResult<TeamSnapshot>> SetTeamEnabledAsync(
        SetTeamEnabledRequest request,
        CancellationToken cancellationToken = default);

    Task<CoWorkResult<AgentRunSnapshot>> SpawnSubAgentAsync(
        SpawnSubAgentRequest request,
        CancellationToken cancellationToken = default);

    Task<CoWorkResult<CoWorkPage<DirectSubAgentSnapshot>>> ListSubAgentChildrenAsync(
        SubAgentQueryRequest request,
        CancellationToken cancellationToken = default);

    Task<CoWorkResult<CoWorkPage<DirectSubAgentSnapshot>>> ListSubAgentsAsync(
        SubAgentQueryRequest request,
        CancellationToken cancellationToken = default);

    Task<CoWorkResult<MailboxMessageSnapshot>> SendSubAgentMessageAsync(
        SendSubAgentMessageRequest request,
        CancellationToken cancellationToken = default);

    Task<CoWorkResult<AgentRunSnapshot>> FollowUpSubAgentAsync(
        FollowUpSubAgentRequest request,
        CancellationToken cancellationToken = default);

    Task<CoWorkResult<DirectSubAgentSnapshot>> CancelSubAgentAsync(
        CancelSubAgentRequest request,
        CancellationToken cancellationToken = default);

    Task<CoWorkResult<MissionSnapshot>> CreateMissionAsync(
        CreateMissionRequest request,
        CancellationToken cancellationToken = default);

    Task<CoWorkResult<CoWorkPage<MissionSnapshot>>> ListMissionsAsync(
        ListMissionsRequest request,
        CancellationToken cancellationToken = default);

    Task<CoWorkResult<MissionSnapshot>> GetMissionAsync(
        GetMissionRequest request,
        CancellationToken cancellationToken = default);

    Task<CoWorkResult<MissionSnapshot>> ActivateMissionAsync(
        MissionCommandRequest request,
        CancellationToken cancellationToken = default);

    Task<CoWorkResult<MissionSnapshot>> CancelMissionAsync(
        MissionCommandRequest request,
        CancellationToken cancellationToken = default);

    Task<CoWorkResult<MissionTaskSnapshot>> AddMissionTaskAsync(
        AddMissionTaskRequest request,
        CancellationToken cancellationToken = default);

    Task<CoWorkResult<MissionTaskSnapshot>> UpdateMissionTaskAsync(
        UpdateMissionTaskRequest request,
        CancellationToken cancellationToken = default);

    Task<CoWorkResult<MissionTaskSnapshot>> RemoveMissionTaskAsync(
        MissionTaskCommandRequest request,
        CancellationToken cancellationToken = default);

    Task<CoWorkResult<MissionTaskSnapshot>> BlockMissionTaskAsync(
        BlockMissionTaskRequest request,
        CancellationToken cancellationToken = default);

    Task<CoWorkResult<MissionTaskSnapshot>> UnblockMissionTaskAsync(
        MissionTaskCommandRequest request,
        CancellationToken cancellationToken = default);

    Task<CoWorkResult<MissionTaskSnapshot>> RetryMissionTaskAsync(
        MissionTaskCommandRequest request,
        CancellationToken cancellationToken = default);

    Task<CoWorkResult<MissionTaskSnapshot>> ReassignMissionTaskAsync(
        ReassignMissionTaskRequest request,
        CancellationToken cancellationToken = default);

    Task<CoWorkResult<MissionTaskSnapshot>> WaiveMissionTaskAsync(
        MissionTaskCommandRequest request,
        CancellationToken cancellationToken = default);

    Task<CoWorkResult<MissionTaskSnapshot>> ReviewMissionTaskAsync(
        ReviewMissionTaskRequest request,
        CancellationToken cancellationToken = default);

    Task<CoWorkResult<CoWorkPage<MailboxMessageSnapshot>>> ListMailboxMessagesAsync(
        ListMailboxMessagesRequest request,
        CancellationToken cancellationToken = default);

    Task<CoWorkResult<MailboxMessageSnapshot>> SendMailboxMessageAsync(
        SendMailboxMessageRequest request,
        CancellationToken cancellationToken = default);

    Task<CoWorkResult<MailboxMessageSnapshot>> AcknowledgeMailboxMessageAsync(
        MailboxMessageCommandRequest request,
        CancellationToken cancellationToken = default);

    Task<CoWorkResult<MailboxMessageSnapshot>> RetryMailboxMessageAsync(
        MailboxMessageCommandRequest request,
        CancellationToken cancellationToken = default);

    Task<CoWorkResult<CoWorkPage<ArtifactSnapshot>>> ListArtifactsAsync(
        ListArtifactsRequest request,
        CancellationToken cancellationToken = default);

    Task<CoWorkResult<ArtifactSnapshot>> GetArtifactAsync(
        GetArtifactRequest request,
        CancellationToken cancellationToken = default);

    Task<CoWorkResult<ArtifactSnapshot>> PublishArtifactAsync(
        PublishArtifactRequest request,
        CancellationToken cancellationToken = default);

    Task<CoWorkResult<ArtifactSnapshot>> PromoteArtifactAsync(
        PromoteArtifactRequest request,
        CancellationToken cancellationToken = default);

    Task<CoWorkResult<CoWorkPage<WorktreeSnapshot>>> ListWorktreesAsync(
        ListWorktreesRequest request,
        CancellationToken cancellationToken = default);

    Task<CoWorkResult<WorktreeSnapshot>> GetWorktreeAsync(
        GetWorktreeRequest request,
        CancellationToken cancellationToken = default);

    Task<CoWorkResult<WorktreeHandoffSnapshot>> HandoffWorktreeAsync(
        WorktreeCommandRequest request,
        CancellationToken cancellationToken = default);

    Task<CoWorkResult<WorktreeSnapshot>> RemoveWorktreeAsync(
        WorktreeCommandRequest request,
        CancellationToken cancellationToken = default);
}
