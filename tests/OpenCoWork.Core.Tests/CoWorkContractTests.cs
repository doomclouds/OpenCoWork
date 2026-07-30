using System.Data.Common;
using OpenCoWork.Abstractions;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class CoWorkContractTests
{
    [Fact]
    public void Cowork_states_and_errors_are_frozen()
    {
        Assert.Equal(
            ["Planning", "Active", "AwaitingLeaderReview", "Completed", "Failed", "Cancelled"],
            Enum.GetNames<CoWorkMissionStatus>());
        Assert.Equal(
            [
                "Pending",
                "WaitingDependencies",
                "Ready",
                "Running",
                "Blocked",
                "Review",
                "Completed",
                "Failed",
                "Cancelled",
            ],
            Enum.GetNames<CoWorkTaskStatus>());
        Assert.Equal(
            ["Pending", "Starting", "Running", "Completed", "Failed", "Cancelled"],
            Enum.GetNames<CoWorkAgentRunStatus>());
        Assert.Equal(
            ["Pending", "Delivered", "Acknowledged", "DeadLettered"],
            Enum.GetNames<CoWorkMailboxStatus>());
        Assert.Equal(
            ["Creating", "Ready", "Removing", "Removed", "RetainedDirty", "Faulted"],
            Enum.GetNames<CoWorkWorktreeStatus>());
        Assert.Equal(
            ["Pending", "Leased", "Completed", "DeadLettered"],
            Enum.GetNames<CoWorkDispatchStatus>());
        Assert.Equal(
            ["Workspace", "Scratchpad"],
            Enum.GetNames<CoWorkFileArea>());

        Assert.Equal("cowork.notFound", CoWorkErrorCodes.NotFound);
        Assert.Equal("cowork.conflict", CoWorkErrorCodes.Conflict);
        Assert.Equal("cowork.invalidState", CoWorkErrorCodes.InvalidState);
        Assert.Equal("cowork.permissionDenied", CoWorkErrorCodes.PermissionDenied);
        Assert.Equal("cowork.invalidDag", CoWorkErrorCodes.InvalidDag);
        Assert.Equal("cowork.budgetExceeded", CoWorkErrorCodes.BudgetExceeded);
        Assert.Equal("cowork.depthExceeded", CoWorkErrorCodes.DepthExceeded);
        Assert.Equal("cowork.concurrencyExceeded", CoWorkErrorCodes.ConcurrencyExceeded);
        Assert.Equal("cowork.memberBusy", CoWorkErrorCodes.MemberBusy);
        Assert.Equal("cowork.secretDetected", CoWorkErrorCodes.SecretDetected);
        Assert.Equal("cowork.pathEscape", CoWorkErrorCodes.PathEscape);
        Assert.Equal("cowork.artifactUnavailable", CoWorkErrorCodes.ArtifactUnavailable);
        Assert.Equal("cowork.worktreeDirty", CoWorkErrorCodes.WorktreeDirty);
        Assert.Equal("cowork.retryExhausted", CoWorkErrorCodes.RetryExhausted);
        Assert.Equal("cowork.schemaInvalid", CoWorkErrorCodes.SchemaInvalid);
        Assert.Equal("cowork.sessionUnavailable", CoWorkErrorCodes.SessionUnavailable);
    }

    [Fact]
    public void Cowork_limits_and_execution_workspaces_are_frozen()
    {
        Assert.Equal(1, CoWorkRuntimeLimits.DefaultMaxDepth);
        Assert.Equal(4, CoWorkRuntimeLimits.MaximumDepth);
        Assert.Equal(16, CoWorkRuntimeLimits.DefaultMaximumConcurrentAgentRuns);
        Assert.Equal(64, CoWorkRuntimeLimits.MaximumConcurrentAgentRuns);
        Assert.Equal(4, CoWorkRuntimeLimits.DefaultMaximumConcurrentAgentRunsPerMission);
        Assert.Equal(16, CoWorkRuntimeLimits.MaximumMissionMembers);
        Assert.Equal(256, CoWorkRuntimeLimits.MaximumMissionTasks);
        Assert.Equal(64 * 1024, CoWorkRuntimeLimits.MaximumMailboxMessageBytes);
        Assert.Equal(64L * 1024 * 1024, CoWorkRuntimeLimits.MaximumArtifactBytes);
        Assert.Equal(512L * 1024 * 1024, CoWorkRuntimeLimits.MaximumOwnedFileBytes);
        Assert.Equal(5, CoWorkRuntimeLimits.DispatchAttempts);
        Assert.Equal(TimeSpan.FromMinutes(2), CoWorkRuntimeLimits.DispatchLease);
        Assert.Equal(TimeSpan.FromSeconds(30), CoWorkRuntimeLimits.LeaseRenewal);

        var project = new ExecutionWorkspaceDescriptor(
            CoWorkWorkspaceMode.Project,
            "/workspace",
            "/scratchpad",
            WorktreeId: null,
            WorktreeRoot: null,
            BaseCommitSha: null);
        var worktreeId = Guid.CreateVersion7();
        var worktree = new ExecutionWorkspaceDescriptor(
            CoWorkWorkspaceMode.Worktree,
            "/worktree",
            "/scratchpad",
            worktreeId,
            "/worktree",
            "0123456789abcdef");

        Assert.Equal(CoWorkWorkspaceMode.Project, project.Mode);
        Assert.Null(project.WorktreeId);
        Assert.Equal(CoWorkWorkspaceMode.Worktree, worktree.Mode);
        Assert.Equal(worktreeId, worktree.WorktreeId);
        Assert.Equal("0123456789abcdef", worktree.BaseCommitSha);
    }

    [Fact]
    public void Actor_revision_and_service_operations_are_explicit()
    {
        var actor = new CoWorkActorContext(
            CoWorkActorKind.Leader,
            "thread-principal",
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7());
        var command = new CoWorkCommandContext(
            Guid.CreateVersion7(),
            actor,
            ExpectedRevision: 7);

        Assert.Equal(CoWorkActorKind.Leader, command.Actor.Kind);
        Assert.Equal(7, command.ExpectedRevision);
        Assert.Equal(
            [
                "AcknowledgeMailboxMessageAsync",
                "ActivateMissionAsync",
                "AddMissionTaskAsync",
                "BlockMissionTaskAsync",
                "CancelMissionAsync",
                "CancelSubAgentAsync",
                "CreateMissionAsync",
                "FollowUpSubAgentAsync",
                "GetAgentProfileAsync",
                "GetArtifactAsync",
                "GetMissionAsync",
                "GetTeamAsync",
                "GetWorktreeAsync",
                "HandoffWorktreeAsync",
                "ListAgentProfilesAsync",
                "ListArtifactsAsync",
                "ListMailboxMessagesAsync",
                "ListMissionsAsync",
                "ListSubAgentChildrenAsync",
                "ListSubAgentsAsync",
                "ListTeamsAsync",
                "ListWorktreesAsync",
                "PromoteArtifactAsync",
                "PublishArtifactAsync",
                "ReassignMissionTaskAsync",
                "RemoveMissionTaskAsync",
                "RemoveWorktreeAsync",
                "RetryMailboxMessageAsync",
                "RetryMissionTaskAsync",
                "ReviewMissionTaskAsync",
                "SendMailboxMessageAsync",
                "SendSubAgentMessageAsync",
                "SetAgentProfileEnabledAsync",
                "SetTeamEnabledAsync",
                "SpawnSubAgentAsync",
                "UnblockMissionTaskAsync",
                "UpdateMissionTaskAsync",
                "UpsertAgentProfileAsync",
                "UpsertTeamAsync",
                "WaiveMissionTaskAsync",
            ],
            typeof(ICoWorkService)
                .GetMethods()
                .Select(method => method.Name)
                .Order()
                .ToArray());
    }

    [Fact]
    public void Workspace_state_contracts_do_not_expose_sqlite_provider_types()
    {
        var contractTypes = new[]
        {
            typeof(IWorkspaceStateStore),
            typeof(IWorkspaceStateMigrationContributor),
        };
        var exposedTypes = contractTypes
            .SelectMany(type => type.GetMethods())
            .SelectMany(method =>
                method.GetParameters()
                    .Select(parameter => parameter.ParameterType)
                    .Append(method.ReturnType))
            .SelectMany(Flatten)
            .ToArray();

        Assert.Contains(typeof(DbConnection), exposedTypes);
        Assert.Contains(typeof(DbTransaction), exposedTypes);
        Assert.DoesNotContain(
            exposedTypes,
            type => type.FullName?.StartsWith(
                "Microsoft.Data.Sqlite",
                StringComparison.Ordinal) == true);
        Assert.Equal(
            ["AgentRunId", "AllowDirtyOrigin", "BaseCommitSha"],
            typeof(ManagedWorktreeCreateRequest)
                .GetProperties()
                .Select(property => property.Name)
                .Order()
                .ToArray());
    }

    private static IEnumerable<Type> Flatten(Type type)
    {
        yield return type;
        if (!type.IsGenericType)
        {
            yield break;
        }

        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var nested in Flatten(argument))
            {
                yield return nested;
            }
        }
    }
}
