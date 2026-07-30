using System.Text.Json;
using OpenCoWork.Abstractions;

namespace OpenCoWork.Protocol;

public sealed partial class OpenCoWorkJsonRpcConnection
{
    [OpenCoWorkWireMethod(
        "agent/profile/list", OpenCoWorkWire.ClientToServer, "agent",
        OpenCoWorkWire.LatestVersion, typeof(WireListAgentProfilesRequest),
        typeof(WireCoWorkResponse<CoWorkPage<AgentProfileSnapshot>>),
        OpenCoWorkWire.ConnectionAuthority, false, OpenCoWorkWire.NoIdempotency)]
    public async Task<WireCoWorkResponse<CoWorkPage<AgentProfileSnapshot>>>
        ListAgentProfilesAsync(
            WireListAgentProfilesRequest request,
            CancellationToken cancellationToken) =>
        Project(await RequireCoWork().ListAgentProfilesAsync(
            new ListAgentProfilesRequest(
                HostActor(),
                request.PageSize,
                request.Cursor),
            cancellationToken));

    [OpenCoWorkWireMethod(
        "agent/profile/get", OpenCoWorkWire.ClientToServer, "agent",
        OpenCoWorkWire.LatestVersion, typeof(WireGetAgentProfileRequest),
        typeof(WireCoWorkResponse<AgentProfileSnapshot>),
        OpenCoWorkWire.ConnectionAuthority, false, OpenCoWorkWire.NoIdempotency)]
    public async Task<WireCoWorkResponse<AgentProfileSnapshot>> GetAgentProfileAsync(
        WireGetAgentProfileRequest request,
        CancellationToken cancellationToken) =>
        Project(await RequireCoWork().GetAgentProfileAsync(
            new GetAgentProfileRequest(HostActor(), request.ProfileId),
            cancellationToken));

    [OpenCoWorkWireMethod(
        "agent/profile/upsert", OpenCoWorkWire.ClientToServer, "agent",
        OpenCoWorkWire.LatestVersion, typeof(WireUpsertAgentProfileRequest),
        typeof(WireCoWorkResponse<AgentProfileSnapshot>),
        OpenCoWorkWire.ConnectionAuthority, true,
        OpenCoWorkWire.RequiredIdempotency)]
    public Task<WireCoWorkResponse<AgentProfileSnapshot>> UpsertAgentProfileAsync(
        WireUpsertAgentProfileRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            RequireCoWork().UpsertAgentProfileAsync(
                new UpsertAgentProfileRequest(
                    Command(request.CommandId, request.ExpectedRevision),
                    request.ProfileId,
                    request.Name,
                    request.Description,
                    request.Instructions,
                    request.ProviderId,
                    request.ModelId,
                    request.SkillAllowlist,
                    request.ToolAllowlist),
                cancellationToken),
            "agent/changed",
            "upserted",
            value => [value.ProfileId],
            cancellationToken);

    [OpenCoWorkWireMethod(
        "agent/profile/setEnabled", OpenCoWorkWire.ClientToServer, "agent",
        OpenCoWorkWire.LatestVersion, typeof(WireSetAgentProfileEnabledRequest),
        typeof(WireCoWorkResponse<AgentProfileSnapshot>),
        OpenCoWorkWire.ConnectionAuthority, true,
        OpenCoWorkWire.RequiredIdempotency)]
    public Task<WireCoWorkResponse<AgentProfileSnapshot>> SetAgentProfileEnabledAsync(
        WireSetAgentProfileEnabledRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            RequireCoWork().SetAgentProfileEnabledAsync(
                new SetAgentProfileEnabledRequest(
                    Command(request.CommandId, request.ExpectedRevision),
                    request.ProfileId,
                    request.Enabled),
                cancellationToken),
            "agent/changed",
            "enabledChanged",
            value => [value.ProfileId],
            cancellationToken);

    [OpenCoWorkWireMethod(
        "team/list", OpenCoWorkWire.ClientToServer, "team",
        OpenCoWorkWire.LatestVersion, typeof(WireListTeamsRequest),
        typeof(WireCoWorkResponse<CoWorkPage<TeamSnapshot>>),
        OpenCoWorkWire.ConnectionAuthority, false, OpenCoWorkWire.NoIdempotency)]
    public async Task<WireCoWorkResponse<CoWorkPage<TeamSnapshot>>> ListTeamsAsync(
        WireListTeamsRequest request,
        CancellationToken cancellationToken) =>
        Project(await RequireCoWork().ListTeamsAsync(
            new ListTeamsRequest(HostActor(), request.PageSize, request.Cursor),
            cancellationToken));

    [OpenCoWorkWireMethod(
        "team/get", OpenCoWorkWire.ClientToServer, "team",
        OpenCoWorkWire.LatestVersion, typeof(WireGetTeamRequest),
        typeof(WireCoWorkResponse<TeamSnapshot>),
        OpenCoWorkWire.ConnectionAuthority, false, OpenCoWorkWire.NoIdempotency)]
    public async Task<WireCoWorkResponse<TeamSnapshot>> GetTeamAsync(
        WireGetTeamRequest request,
        CancellationToken cancellationToken) =>
        Project(await RequireCoWork().GetTeamAsync(
            new GetTeamRequest(HostActor(), request.TeamId),
            cancellationToken));

    [OpenCoWorkWireMethod(
        "team/upsert", OpenCoWorkWire.ClientToServer, "team",
        OpenCoWorkWire.LatestVersion, typeof(WireUpsertTeamRequest),
        typeof(WireCoWorkResponse<TeamSnapshot>),
        OpenCoWorkWire.ConnectionAuthority, true,
        OpenCoWorkWire.RequiredIdempotency)]
    public Task<WireCoWorkResponse<TeamSnapshot>> UpsertTeamAsync(
        WireUpsertTeamRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            RequireCoWork().UpsertTeamAsync(
                new UpsertTeamRequest(
                    Command(request.CommandId, request.ExpectedRevision),
                    request.TeamId,
                    request.Name,
                    request.Description,
                    request.Members),
                cancellationToken),
            "team/changed",
            "upserted",
            value => [value.TeamId],
            cancellationToken);

    [OpenCoWorkWireMethod(
        "team/setEnabled", OpenCoWorkWire.ClientToServer, "team",
        OpenCoWorkWire.LatestVersion, typeof(WireSetTeamEnabledRequest),
        typeof(WireCoWorkResponse<TeamSnapshot>),
        OpenCoWorkWire.ConnectionAuthority, true,
        OpenCoWorkWire.RequiredIdempotency)]
    public Task<WireCoWorkResponse<TeamSnapshot>> SetTeamEnabledAsync(
        WireSetTeamEnabledRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            RequireCoWork().SetTeamEnabledAsync(
                new SetTeamEnabledRequest(
                    Command(request.CommandId, request.ExpectedRevision),
                    request.TeamId,
                    request.Enabled),
                cancellationToken),
            "team/changed",
            "enabledChanged",
            value => [value.TeamId],
            cancellationToken);

    [OpenCoWorkWireMethod(
        "subagent/spawn", OpenCoWorkWire.ClientToServer, "subagent",
        OpenCoWorkWire.LatestVersion, typeof(WireSpawnSubAgentRequest),
        typeof(WireCoWorkResponse<AgentRunSnapshot>),
        OpenCoWorkWire.ConnectionAuthority, true,
        OpenCoWorkWire.RequiredIdempotency)]
    public Task<WireCoWorkResponse<AgentRunSnapshot>> SpawnSubAgentAsync(
        WireSpawnSubAgentRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            RequireCoWork().SpawnSubAgentAsync(
                new SpawnSubAgentRequest(
                    Command(request.CommandId, request.ExpectedRevision),
                    request.ParentThreadId,
                    request.ProfileId,
                    request.Task,
                    request.TokenBudget,
                    ParseEnum<CoWorkWorkspaceMode>(request.WorkspaceMode)),
                cancellationToken),
            "subagent/changed",
            "spawned",
            value => [value.AgentRunId, value.ThreadId],
            cancellationToken);

    [OpenCoWorkWireMethod(
        "subagent/children", OpenCoWorkWire.ClientToServer, "subagent",
        OpenCoWorkWire.LatestVersion, typeof(WireSubAgentQueryRequest),
        typeof(WireCoWorkResponse<CoWorkPage<DirectSubAgentSnapshot>>),
        OpenCoWorkWire.ConnectionAuthority, false, OpenCoWorkWire.NoIdempotency)]
    public async Task<WireCoWorkResponse<CoWorkPage<DirectSubAgentSnapshot>>>
        ListSubAgentChildrenAsync(
            WireSubAgentQueryRequest request,
            CancellationToken cancellationToken) =>
        Project(await RequireCoWork().ListSubAgentChildrenAsync(
            SubAgentQuery(request),
            cancellationToken));

    [OpenCoWorkWireMethod(
        "subagent/list", OpenCoWorkWire.ClientToServer, "subagent",
        OpenCoWorkWire.LatestVersion, typeof(WireSubAgentQueryRequest),
        typeof(WireCoWorkResponse<CoWorkPage<DirectSubAgentSnapshot>>),
        OpenCoWorkWire.ConnectionAuthority, false, OpenCoWorkWire.NoIdempotency)]
    public async Task<WireCoWorkResponse<CoWorkPage<DirectSubAgentSnapshot>>>
        ListSubAgentsAsync(
            WireSubAgentQueryRequest request,
            CancellationToken cancellationToken) =>
        Project(await RequireCoWork().ListSubAgentsAsync(
            SubAgentQuery(request),
            cancellationToken));

    [OpenCoWorkWireMethod(
        "subagent/send", OpenCoWorkWire.ClientToServer, "subagent",
        OpenCoWorkWire.LatestVersion, typeof(WireSendSubAgentMessageRequest),
        typeof(WireCoWorkResponse<MailboxMessageSnapshot>),
        OpenCoWorkWire.ConnectionAuthority, true,
        OpenCoWorkWire.RequiredIdempotency)]
    public Task<WireCoWorkResponse<MailboxMessageSnapshot>> SendSubAgentMessageAsync(
        WireSendSubAgentMessageRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            RequireCoWork().SendSubAgentMessageAsync(
                new SendSubAgentMessageRequest(
                    Command(request.CommandId, request.ExpectedRevision),
                    request.ChildThreadId,
                    request.Message),
                cancellationToken),
            "subagent/changed",
            "messageSent",
            value => [value.MessageId, request.ChildThreadId],
            cancellationToken);

    [OpenCoWorkWireMethod(
        "subagent/followup", OpenCoWorkWire.ClientToServer, "subagent",
        OpenCoWorkWire.LatestVersion, typeof(WireFollowUpSubAgentRequest),
        typeof(WireCoWorkResponse<AgentRunSnapshot>),
        OpenCoWorkWire.ConnectionAuthority, true,
        OpenCoWorkWire.RequiredIdempotency)]
    public Task<WireCoWorkResponse<AgentRunSnapshot>> FollowUpSubAgentAsync(
        WireFollowUpSubAgentRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            RequireCoWork().FollowUpSubAgentAsync(
                new FollowUpSubAgentRequest(
                    Command(request.CommandId, request.ExpectedRevision),
                    request.ChildThreadId,
                    request.Task),
                cancellationToken),
            "subagent/changed",
            "followedUp",
            value => [value.AgentRunId, request.ChildThreadId],
            cancellationToken);

    [OpenCoWorkWireMethod(
        "subagent/cancel", OpenCoWorkWire.ClientToServer, "subagent",
        OpenCoWorkWire.LatestVersion, typeof(WireCancelSubAgentRequest),
        typeof(WireCoWorkResponse<DirectSubAgentSnapshot>),
        OpenCoWorkWire.ConnectionAuthority, true,
        OpenCoWorkWire.RequiredIdempotency)]
    public Task<WireCoWorkResponse<DirectSubAgentSnapshot>> CancelSubAgentAsync(
        WireCancelSubAgentRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            RequireCoWork().CancelSubAgentAsync(
                new CancelSubAgentRequest(
                    Command(request.CommandId, request.ExpectedRevision),
                    request.ChildThreadId),
                cancellationToken),
            "subagent/changed",
            "cancelled",
            value => [value.ChildThreadId],
            cancellationToken);

    [OpenCoWorkWireMethod(
        "mission/create", OpenCoWorkWire.ClientToServer, "mission",
        OpenCoWorkWire.LatestVersion, typeof(WireCreateMissionRequest),
        typeof(WireCoWorkResponse<MissionSnapshot>),
        OpenCoWorkWire.ConnectionAuthority, true,
        OpenCoWorkWire.RequiredIdempotency)]
    public Task<WireCoWorkResponse<MissionSnapshot>> CreateMissionAsync(
        WireCreateMissionRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            RequireCoWork().CreateMissionAsync(
                new CreateMissionRequest(
                    Command(request.CommandId, request.ExpectedRevision),
                    request.OriginThreadId,
                    request.TeamId,
                    request.Objective,
                    request.TokenBudget,
                    ParseEnum<CoWorkWorkspaceMode>(request.WorkspaceMode),
                    request.AllowDirtyOrigin),
                cancellationToken),
            "mission/changed",
            "created",
            value => [value.MissionId],
            cancellationToken);

    [OpenCoWorkWireMethod(
        "mission/list", OpenCoWorkWire.ClientToServer, "mission",
        OpenCoWorkWire.LatestVersion, typeof(WireListMissionsRequest),
        typeof(WireCoWorkResponse<CoWorkPage<MissionSnapshot>>),
        OpenCoWorkWire.ConnectionAuthority, false, OpenCoWorkWire.NoIdempotency)]
    public async Task<WireCoWorkResponse<CoWorkPage<MissionSnapshot>>> ListMissionsAsync(
        WireListMissionsRequest request,
        CancellationToken cancellationToken) =>
        Project(await RequireCoWork().ListMissionsAsync(
            new ListMissionsRequest(
                HostActor(),
                ParseOptionalEnum<CoWorkMissionStatus>(request.Status),
                request.PageSize,
                request.Cursor),
            cancellationToken));

    [OpenCoWorkWireMethod(
        "mission/get", OpenCoWorkWire.ClientToServer, "mission",
        OpenCoWorkWire.LatestVersion, typeof(WireGetMissionRequest),
        typeof(WireCoWorkResponse<MissionSnapshot>),
        OpenCoWorkWire.ConnectionAuthority, false, OpenCoWorkWire.NoIdempotency)]
    public async Task<WireCoWorkResponse<MissionSnapshot>> GetMissionAsync(
        WireGetMissionRequest request,
        CancellationToken cancellationToken) =>
        Project(await RequireCoWork().GetMissionAsync(
            new GetMissionRequest(HostActor(), request.MissionId),
            cancellationToken));

    [OpenCoWorkWireMethod(
        "mission/activate", OpenCoWorkWire.ClientToServer, "mission",
        OpenCoWorkWire.LatestVersion, typeof(WireMissionCommandRequest),
        typeof(WireCoWorkResponse<MissionSnapshot>),
        OpenCoWorkWire.ConnectionAuthority, true,
        OpenCoWorkWire.RequiredIdempotency)]
    public Task<WireCoWorkResponse<MissionSnapshot>> ActivateMissionAsync(
        WireMissionCommandRequest request,
        CancellationToken cancellationToken) =>
        MissionMutation(
            RequireCoWork().ActivateMissionAsync(
                MissionCommand(request),
                cancellationToken),
            "activated",
            cancellationToken);

    [OpenCoWorkWireMethod(
        "mission/cancel", OpenCoWorkWire.ClientToServer, "mission",
        OpenCoWorkWire.LatestVersion, typeof(WireMissionCommandRequest),
        typeof(WireCoWorkResponse<MissionSnapshot>),
        OpenCoWorkWire.ConnectionAuthority, true,
        OpenCoWorkWire.RequiredIdempotency)]
    public Task<WireCoWorkResponse<MissionSnapshot>> CancelMissionAsync(
        WireMissionCommandRequest request,
        CancellationToken cancellationToken) =>
        MissionMutation(
            RequireCoWork().CancelMissionAsync(
                MissionCommand(request),
                cancellationToken),
            "cancelled",
            cancellationToken);

    [OpenCoWorkWireMethod(
        "mission/task/add", OpenCoWorkWire.ClientToServer, "mission",
        OpenCoWorkWire.LatestVersion, typeof(WireAddMissionTaskRequest),
        typeof(WireCoWorkResponse<MissionTaskSnapshot>),
        OpenCoWorkWire.ConnectionAuthority, true,
        OpenCoWorkWire.RequiredIdempotency)]
    public Task<WireCoWorkResponse<MissionTaskSnapshot>> AddMissionTaskAsync(
        WireAddMissionTaskRequest request,
        CancellationToken cancellationToken) =>
        TaskMutation(
            RequireCoWork().AddMissionTaskAsync(
                new AddMissionTaskRequest(
                    Command(request.CommandId, request.ExpectedRevision),
                    request.MissionId,
                    request.Alias,
                    request.Objective,
                    request.Instructions,
                    request.AssignedMemberId,
                    request.Required,
                    request.RequiresReview,
                    request.DependsOn),
                cancellationToken),
            request.MissionId,
            "added",
            cancellationToken);

    [OpenCoWorkWireMethod(
        "mission/task/update", OpenCoWorkWire.ClientToServer, "mission",
        OpenCoWorkWire.LatestVersion, typeof(WireUpdateMissionTaskRequest),
        typeof(WireCoWorkResponse<MissionTaskSnapshot>),
        OpenCoWorkWire.ConnectionAuthority, true,
        OpenCoWorkWire.RequiredIdempotency)]
    public Task<WireCoWorkResponse<MissionTaskSnapshot>> UpdateMissionTaskAsync(
        WireUpdateMissionTaskRequest request,
        CancellationToken cancellationToken) =>
        TaskMutation(
            RequireCoWork().UpdateMissionTaskAsync(
                new UpdateMissionTaskRequest(
                    Command(request.CommandId, request.ExpectedRevision),
                    request.MissionId,
                    request.TaskId,
                    request.Objective,
                    request.Instructions,
                    request.AssignedMemberId,
                    request.Required,
                    request.RequiresReview,
                    request.DependsOn),
                cancellationToken),
            request.MissionId,
            "updated",
            cancellationToken);

    [OpenCoWorkWireMethod(
        "mission/task/remove", OpenCoWorkWire.ClientToServer, "mission",
        OpenCoWorkWire.LatestVersion, typeof(WireMissionTaskCommandRequest),
        typeof(WireCoWorkResponse<MissionTaskSnapshot>),
        OpenCoWorkWire.ConnectionAuthority, true,
        OpenCoWorkWire.RequiredIdempotency)]
    public Task<WireCoWorkResponse<MissionTaskSnapshot>> RemoveMissionTaskAsync(
        WireMissionTaskCommandRequest request,
        CancellationToken cancellationToken) =>
        TaskMutation(
            RequireCoWork().RemoveMissionTaskAsync(
                TaskCommand(request),
                cancellationToken),
            request.MissionId,
            "removed",
            cancellationToken);

    [OpenCoWorkWireMethod(
        "mission/task/block", OpenCoWorkWire.ClientToServer, "mission",
        OpenCoWorkWire.LatestVersion, typeof(WireBlockMissionTaskRequest),
        typeof(WireCoWorkResponse<MissionTaskSnapshot>),
        OpenCoWorkWire.ConnectionAuthority, true,
        OpenCoWorkWire.RequiredIdempotency)]
    public Task<WireCoWorkResponse<MissionTaskSnapshot>> BlockMissionTaskAsync(
        WireBlockMissionTaskRequest request,
        CancellationToken cancellationToken) =>
        TaskMutation(
            RequireCoWork().BlockMissionTaskAsync(
                new BlockMissionTaskRequest(
                    Command(request.CommandId, request.ExpectedRevision),
                    request.MissionId,
                    request.TaskId,
                    request.Reason),
                cancellationToken),
            request.MissionId,
            "blocked",
            cancellationToken);

    [OpenCoWorkWireMethod(
        "mission/task/unblock", OpenCoWorkWire.ClientToServer, "mission",
        OpenCoWorkWire.LatestVersion, typeof(WireMissionTaskCommandRequest),
        typeof(WireCoWorkResponse<MissionTaskSnapshot>),
        OpenCoWorkWire.ConnectionAuthority, true,
        OpenCoWorkWire.RequiredIdempotency)]
    public Task<WireCoWorkResponse<MissionTaskSnapshot>> UnblockMissionTaskAsync(
        WireMissionTaskCommandRequest request,
        CancellationToken cancellationToken) =>
        TaskMutation(
            RequireCoWork().UnblockMissionTaskAsync(
                TaskCommand(request),
                cancellationToken),
            request.MissionId,
            "unblocked",
            cancellationToken);

    [OpenCoWorkWireMethod(
        "mission/task/retry", OpenCoWorkWire.ClientToServer, "mission",
        OpenCoWorkWire.LatestVersion, typeof(WireMissionTaskCommandRequest),
        typeof(WireCoWorkResponse<MissionTaskSnapshot>),
        OpenCoWorkWire.ConnectionAuthority, true,
        OpenCoWorkWire.RequiredIdempotency)]
    public Task<WireCoWorkResponse<MissionTaskSnapshot>> RetryMissionTaskAsync(
        WireMissionTaskCommandRequest request,
        CancellationToken cancellationToken) =>
        TaskMutation(
            RequireCoWork().RetryMissionTaskAsync(
                TaskCommand(request),
                cancellationToken),
            request.MissionId,
            "retried",
            cancellationToken);

    [OpenCoWorkWireMethod(
        "mission/task/reassign", OpenCoWorkWire.ClientToServer, "mission",
        OpenCoWorkWire.LatestVersion, typeof(WireReassignMissionTaskRequest),
        typeof(WireCoWorkResponse<MissionTaskSnapshot>),
        OpenCoWorkWire.ConnectionAuthority, true,
        OpenCoWorkWire.RequiredIdempotency)]
    public Task<WireCoWorkResponse<MissionTaskSnapshot>> ReassignMissionTaskAsync(
        WireReassignMissionTaskRequest request,
        CancellationToken cancellationToken) =>
        TaskMutation(
            RequireCoWork().ReassignMissionTaskAsync(
                new ReassignMissionTaskRequest(
                    Command(request.CommandId, request.ExpectedRevision),
                    request.MissionId,
                    request.TaskId,
                    request.MemberId),
                cancellationToken),
            request.MissionId,
            "reassigned",
            cancellationToken);

    [OpenCoWorkWireMethod(
        "mission/task/waive", OpenCoWorkWire.ClientToServer, "mission",
        OpenCoWorkWire.LatestVersion, typeof(WireMissionTaskCommandRequest),
        typeof(WireCoWorkResponse<MissionTaskSnapshot>),
        OpenCoWorkWire.ConnectionAuthority, true,
        OpenCoWorkWire.RequiredIdempotency)]
    public Task<WireCoWorkResponse<MissionTaskSnapshot>> WaiveMissionTaskAsync(
        WireMissionTaskCommandRequest request,
        CancellationToken cancellationToken) =>
        TaskMutation(
            RequireCoWork().WaiveMissionTaskAsync(
                TaskCommand(request),
                cancellationToken),
            request.MissionId,
            "waived",
            cancellationToken);

    [OpenCoWorkWireMethod(
        "mission/task/review", OpenCoWorkWire.ClientToServer, "mission",
        OpenCoWorkWire.LatestVersion, typeof(WireReviewMissionTaskRequest),
        typeof(WireCoWorkResponse<MissionTaskSnapshot>),
        OpenCoWorkWire.ConnectionAuthority, true,
        OpenCoWorkWire.RequiredIdempotency)]
    public Task<WireCoWorkResponse<MissionTaskSnapshot>> ReviewMissionTaskAsync(
        WireReviewMissionTaskRequest request,
        CancellationToken cancellationToken) =>
        TaskMutation(
            RequireCoWork().ReviewMissionTaskAsync(
                new ReviewMissionTaskRequest(
                    Command(request.CommandId, request.ExpectedRevision),
                    request.MissionId,
                    request.TaskId,
                    request.Accepted,
                    request.Comment),
                cancellationToken),
            request.MissionId,
            "reviewed",
            cancellationToken);

    [OpenCoWorkWireMethod(
        "mailbox/list", OpenCoWorkWire.ClientToServer, "mailbox",
        OpenCoWorkWire.LatestVersion, typeof(WireListMailboxMessagesRequest),
        typeof(WireCoWorkResponse<CoWorkPage<MailboxMessageSnapshot>>),
        OpenCoWorkWire.ConnectionAuthority, false, OpenCoWorkWire.NoIdempotency)]
    public async Task<WireCoWorkResponse<CoWorkPage<MailboxMessageSnapshot>>>
        ListMailboxMessagesAsync(
            WireListMailboxMessagesRequest request,
            CancellationToken cancellationToken) =>
        Project(await RequireCoWork().ListMailboxMessagesAsync(
            new ListMailboxMessagesRequest(
                HostActor(),
                request.MissionId,
                ParseOptionalEnum<CoWorkMailboxStatus>(request.Status),
                request.PageSize,
                request.Cursor),
            cancellationToken));

    [OpenCoWorkWireMethod(
        "mailbox/send", OpenCoWorkWire.ClientToServer, "mailbox",
        OpenCoWorkWire.LatestVersion, typeof(WireSendMailboxMessageRequest),
        typeof(WireCoWorkResponse<MailboxMessageSnapshot>),
        OpenCoWorkWire.ConnectionAuthority, true,
        OpenCoWorkWire.RequiredIdempotency)]
    public Task<WireCoWorkResponse<MailboxMessageSnapshot>> SendMailboxMessageAsync(
        WireSendMailboxMessageRequest request,
        CancellationToken cancellationToken) =>
        MailboxMutation(
            RequireCoWork().SendMailboxMessageAsync(
                new SendMailboxMessageRequest(
                    Command(request.CommandId, request.ExpectedRevision),
                    request.MissionId,
                    request.RecipientId,
                    ParseEnum<CoWorkMailboxKind>(request.Kind),
                    request.Body,
                    request.TaskId,
                    request.ArtifactId),
                cancellationToken),
            "sent",
            cancellationToken);

    [OpenCoWorkWireMethod(
        "mailbox/acknowledge", OpenCoWorkWire.ClientToServer, "mailbox",
        OpenCoWorkWire.LatestVersion, typeof(WireMailboxMessageCommandRequest),
        typeof(WireCoWorkResponse<MailboxMessageSnapshot>),
        OpenCoWorkWire.ConnectionAuthority, true,
        OpenCoWorkWire.RequiredIdempotency)]
    public Task<WireCoWorkResponse<MailboxMessageSnapshot>>
        AcknowledgeMailboxMessageAsync(
            WireMailboxMessageCommandRequest request,
            CancellationToken cancellationToken) =>
        MailboxMutation(
            RequireCoWork().AcknowledgeMailboxMessageAsync(
                MailboxCommand(request),
                cancellationToken),
            "acknowledged",
            cancellationToken);

    [OpenCoWorkWireMethod(
        "mailbox/retry", OpenCoWorkWire.ClientToServer, "mailbox",
        OpenCoWorkWire.LatestVersion, typeof(WireMailboxMessageCommandRequest),
        typeof(WireCoWorkResponse<MailboxMessageSnapshot>),
        OpenCoWorkWire.ConnectionAuthority, true,
        OpenCoWorkWire.RequiredIdempotency)]
    public Task<WireCoWorkResponse<MailboxMessageSnapshot>> RetryMailboxMessageAsync(
        WireMailboxMessageCommandRequest request,
        CancellationToken cancellationToken) =>
        MailboxMutation(
            RequireCoWork().RetryMailboxMessageAsync(
                MailboxCommand(request),
                cancellationToken),
            "retried",
            cancellationToken);

    [OpenCoWorkWireMethod(
        "artifact/list", OpenCoWorkWire.ClientToServer, "artifact",
        OpenCoWorkWire.LatestVersion, typeof(WireListArtifactsRequest),
        typeof(WireCoWorkResponse<CoWorkPage<ArtifactSnapshot>>),
        OpenCoWorkWire.ConnectionAuthority, false, OpenCoWorkWire.NoIdempotency)]
    public async Task<WireCoWorkResponse<CoWorkPage<ArtifactSnapshot>>> ListArtifactsAsync(
        WireListArtifactsRequest request,
        CancellationToken cancellationToken) =>
        Project(await RequireCoWork().ListArtifactsAsync(
            new ListArtifactsRequest(
                HostActor(),
                request.MissionId,
                request.PageSize,
                request.Cursor),
            cancellationToken));

    [OpenCoWorkWireMethod(
        "artifact/get", OpenCoWorkWire.ClientToServer, "artifact",
        OpenCoWorkWire.LatestVersion, typeof(WireGetArtifactRequest),
        typeof(WireCoWorkResponse<ArtifactSnapshot>),
        OpenCoWorkWire.ConnectionAuthority, false, OpenCoWorkWire.NoIdempotency)]
    public async Task<WireCoWorkResponse<ArtifactSnapshot>> GetArtifactAsync(
        WireGetArtifactRequest request,
        CancellationToken cancellationToken) =>
        Project(await RequireCoWork().GetArtifactAsync(
            new GetArtifactRequest(HostActor(), request.ArtifactId),
            cancellationToken));

    [OpenCoWorkWireMethod(
        "artifact/publish", OpenCoWorkWire.ClientToServer, "artifact",
        OpenCoWorkWire.LatestVersion, typeof(WirePublishArtifactRequest),
        typeof(WireCoWorkResponse<ArtifactSnapshot>),
        OpenCoWorkWire.ConnectionAuthority, true,
        OpenCoWorkWire.RequiredIdempotency)]
    public Task<WireCoWorkResponse<ArtifactSnapshot>> PublishArtifactAsync(
        WirePublishArtifactRequest request,
        CancellationToken cancellationToken) =>
        ArtifactMutation(
            RequireCoWork().PublishArtifactAsync(
                new PublishArtifactRequest(
                    Command(request.CommandId, request.ExpectedRevision),
                    request.MissionId,
                    request.AgentRunId,
                    ParseEnum<CoWorkFileArea>(request.SourceArea),
                    request.SourceRelativePath,
                    request.DisplayName,
                    request.MediaType),
                cancellationToken),
            "published",
            cancellationToken);

    [OpenCoWorkWireMethod(
        "artifact/promote", OpenCoWorkWire.ClientToServer, "artifact",
        OpenCoWorkWire.LatestVersion, typeof(WirePromoteArtifactRequest),
        typeof(WireCoWorkResponse<ArtifactSnapshot>),
        OpenCoWorkWire.ConnectionAuthority, true,
        OpenCoWorkWire.RequiredIdempotency)]
    public Task<WireCoWorkResponse<ArtifactSnapshot>> PromoteArtifactAsync(
        WirePromoteArtifactRequest request,
        CancellationToken cancellationToken) =>
        ArtifactMutation(
            RequireCoWork().PromoteArtifactAsync(
                new PromoteArtifactRequest(
                    Command(request.CommandId, request.ExpectedRevision),
                    request.ArtifactId),
                cancellationToken),
            "promoted",
            cancellationToken);

    [OpenCoWorkWireMethod(
        "worktree/list", OpenCoWorkWire.ClientToServer, "worktree",
        OpenCoWorkWire.LatestVersion, typeof(WireListWorktreesRequest),
        typeof(WireCoWorkResponse<CoWorkPage<WorktreeSnapshot>>),
        OpenCoWorkWire.ConnectionAuthority, false, OpenCoWorkWire.NoIdempotency)]
    public async Task<WireCoWorkResponse<CoWorkPage<WorktreeSnapshot>>> ListWorktreesAsync(
        WireListWorktreesRequest request,
        CancellationToken cancellationToken) =>
        Project(await RequireCoWork().ListWorktreesAsync(
            new ListWorktreesRequest(
                HostActor(),
                request.MissionId,
                request.PageSize,
                request.Cursor),
            cancellationToken));

    [OpenCoWorkWireMethod(
        "worktree/get", OpenCoWorkWire.ClientToServer, "worktree",
        OpenCoWorkWire.LatestVersion, typeof(WireGetWorktreeRequest),
        typeof(WireCoWorkResponse<WorktreeSnapshot>),
        OpenCoWorkWire.ConnectionAuthority, false, OpenCoWorkWire.NoIdempotency)]
    public async Task<WireCoWorkResponse<WorktreeSnapshot>> GetWorktreeAsync(
        WireGetWorktreeRequest request,
        CancellationToken cancellationToken) =>
        Project(await RequireCoWork().GetWorktreeAsync(
            new GetWorktreeRequest(HostActor(), request.WorktreeId),
            cancellationToken));

    [OpenCoWorkWireMethod(
        "worktree/handoff", OpenCoWorkWire.ClientToServer, "worktree",
        OpenCoWorkWire.LatestVersion, typeof(WireWorktreeCommandRequest),
        typeof(WireCoWorkResponse<WorktreeHandoffSnapshot>),
        OpenCoWorkWire.ConnectionAuthority, true,
        OpenCoWorkWire.RequiredIdempotency)]
    public Task<WireCoWorkResponse<WorktreeHandoffSnapshot>> HandoffWorktreeAsync(
        WireWorktreeCommandRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            RequireCoWork().HandoffWorktreeAsync(
                WorktreeCommand(request),
                cancellationToken),
            "worktree/changed",
            "handedOff",
            value => [value.WorktreeId],
            cancellationToken);

    [OpenCoWorkWireMethod(
        "worktree/remove", OpenCoWorkWire.ClientToServer, "worktree",
        OpenCoWorkWire.LatestVersion, typeof(WireWorktreeCommandRequest),
        typeof(WireCoWorkResponse<WorktreeSnapshot>),
        OpenCoWorkWire.ConnectionAuthority, true,
        OpenCoWorkWire.RequiredIdempotency)]
    public Task<WireCoWorkResponse<WorktreeSnapshot>> RemoveWorktreeAsync(
        WireWorktreeCommandRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            RequireCoWork().RemoveWorktreeAsync(
                WorktreeCommand(request),
                cancellationToken),
            "worktree/changed",
            "removed",
            value => [value.WorktreeId],
            cancellationToken);

    private ICoWorkService RequireCoWork() =>
        _coWork ?? throw new WireMethodNotFoundException();

    private CoWorkActorContext HostActor() =>
        new(
            CoWorkActorKind.Host,
            $"wire:{_connectionId:D}");

    private CoWorkCommandContext Command(Guid commandId, long? expectedRevision) =>
        new(commandId, HostActor(), expectedRevision);

    private SubAgentQueryRequest SubAgentQuery(WireSubAgentQueryRequest request) =>
        new(
            HostActor(),
            request.ParentThreadId,
            request.ChildThreadId,
            request.PageSize,
            request.Cursor);

    private MissionCommandRequest MissionCommand(WireMissionCommandRequest request) =>
        new(
            Command(request.CommandId, request.ExpectedRevision),
            request.MissionId);

    private MissionTaskCommandRequest TaskCommand(
        WireMissionTaskCommandRequest request) =>
        new(
            Command(request.CommandId, request.ExpectedRevision),
            request.MissionId,
            request.TaskId);

    private MailboxMessageCommandRequest MailboxCommand(
        WireMailboxMessageCommandRequest request) =>
        new(
            Command(request.CommandId, request.ExpectedRevision),
            request.MessageId);

    private WorktreeCommandRequest WorktreeCommand(
        WireWorktreeCommandRequest request) =>
        new(
            Command(request.CommandId, request.ExpectedRevision),
            request.WorktreeId);

    private Task<WireCoWorkResponse<MissionSnapshot>> MissionMutation(
        Task<CoWorkResult<MissionSnapshot>> result,
        string changeKind,
        CancellationToken cancellationToken) =>
        MutateAsync(
            result,
            "mission/changed",
            changeKind,
            value => [value.MissionId],
            cancellationToken);

    private Task<WireCoWorkResponse<MissionTaskSnapshot>> TaskMutation(
        Task<CoWorkResult<MissionTaskSnapshot>> result,
        Guid missionId,
        string changeKind,
        CancellationToken cancellationToken) =>
        MutateAsync(
            result,
            "mission/changed",
            changeKind,
            value => [missionId, value.TaskId],
            cancellationToken);

    private Task<WireCoWorkResponse<MailboxMessageSnapshot>> MailboxMutation(
        Task<CoWorkResult<MailboxMessageSnapshot>> result,
        string changeKind,
        CancellationToken cancellationToken) =>
        MutateAsync(
            result,
            "mailbox/changed",
            changeKind,
            value => [value.MessageId],
            cancellationToken);

    private Task<WireCoWorkResponse<ArtifactSnapshot>> ArtifactMutation(
        Task<CoWorkResult<ArtifactSnapshot>> result,
        string changeKind,
        CancellationToken cancellationToken) =>
        MutateAsync(
            result,
            "artifact/changed",
            changeKind,
            value => [value.ArtifactId],
            cancellationToken);

    private async Task<WireCoWorkResponse<T>> MutateAsync<T>(
        Task<CoWorkResult<T>> operation,
        string notification,
        string changeKind,
        Func<T, IReadOnlyList<Guid>> affectedIds,
        CancellationToken cancellationToken)
    {
        var result = await operation;
        var response = Project(result);
        if (!result.IsReplay)
        {
            await SendCoWorkChangedAsync(
                notification,
                new WireCoWorkChangedNotification(
                    response.CoWorkRevision,
                    changeKind,
                    affectedIds(response.Value)),
                cancellationToken);
        }
        return response;
    }

    private static WireCoWorkResponse<T> Project<T>(CoWorkResult<T> result)
    {
        if (result.Error is { } error)
        {
            throw new WireRpcException(
                new SessionError(error.Code, error.Message, error.IsRetryable),
                currentRevision: result.CoWorkRevision);
        }

        return new WireCoWorkResponse<T>(
            result.CoWorkRevision,
            result.Value ?? throw new InvalidDataException(
                "CoWork returned a successful result without a value."));
    }

    private async ValueTask SendCoWorkChangedAsync(
        string method,
        WireCoWorkChangedNotification notification,
        CancellationToken cancellationToken)
    {
        if (_wireVersion != OpenCoWorkWire.LatestVersion)
        {
            return;
        }

        var message = new JsonRpcNotification("2.0", method, notification);
        await _send(
            JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions),
            cancellationToken);
    }

    private static T ParseEnum<T>(string value)
        where T : struct, Enum
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        foreach (var candidate in Enum.GetValues<T>())
        {
            if (string.Equals(Wire(candidate), value, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        throw new ArgumentException($"'{value}' is not a valid {typeof(T).Name}.");
    }

    private static T? ParseOptionalEnum<T>(string? value)
        where T : struct, Enum =>
        value is null ? null : ParseEnum<T>(value);
}
