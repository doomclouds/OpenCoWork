using OpenCoWork.Abstractions;
using Xunit;

namespace OpenCoWork.IntegrationTests;

public sealed class MissionReviewTests
{
    [Fact]
    public async Task Review_defaults_accept_and_rework_create_a_new_attempt()
    {
        await using var workspace = await CoWorkTestWorkspace.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        var setup = await MissionTestData.CreateAsync(
            workspace,
            CoWorkWorkspaceMode.Project,
            20_000,
            ("leader", CoWorkMemberRole.Leader, Array.Empty<string>()),
            ("one", CoWorkMemberRole.Member, Array.Empty<string>()),
            ("two", CoWorkMemberRole.Member, Array.Empty<string>()));
        var mission = setup.Mission;
        var required = await MissionTestData.AddTaskAsync(
            workspace,
            mission,
            "required",
            setup.Members["one"].MemberId,
            required: true,
            requiresReview: null,
            dependsOn: [],
            token);
        Assert.True(required.RequiresReview);
        mission = await MissionTestData.GetMissionAsync(workspace, mission.MissionId, token);
        var optional = await MissionTestData.AddTaskAsync(
            workspace,
            mission,
            "optional",
            setup.Members["two"].MemberId,
            required: false,
            requiresReview: null,
            dependsOn: [],
            token);
        Assert.False(optional.RequiresReview);
        mission = await MissionTestData.GetMissionAsync(workspace, mission.MissionId, token);
        _ = await workspace.Service.ActivateMissionAsync(
            new MissionCommandRequest(
                MissionTestData.Command(mission.Revision),
                mission.MissionId),
            token);

        mission = await MissionTestData.ReconcileUntilAsync(
            workspace,
            mission.MissionId,
            candidate =>
                candidate.Tasks.Any(task => task.Status == CoWorkTaskStatus.Review) &&
                candidate.Tasks.Any(task => task.Status == CoWorkTaskStatus.Completed),
            token);
        Assert.Equal(CoWorkMissionStatus.Active, mission.Status);
        Assert.Equal(
            CoWorkTaskStatus.Review,
            mission.Tasks.Single(task => task.Alias == "required").Status);
        Assert.Equal(
            CoWorkTaskStatus.Completed,
            mission.Tasks.Single(task => task.Alias == "optional").Status);
        Assert.Equal(
            0,
            await MissionTestData.CountAsync(
                workspace.Store,
                """
                SELECT count(*) FROM agent_runs
                WHERE mission_id = $id AND run_kind = 'leaderSynthesis';
                """,
                token,
                ("$id", mission.MissionId)));

        var reassigned = await workspace.Service.ReassignMissionTaskAsync(
            new ReassignMissionTaskRequest(
                MissionTestData.Command(mission.Revision),
                mission.MissionId,
                required.TaskId,
                setup.Members["two"].MemberId),
            token);
        Assert.True(reassigned.IsSuccess);
        mission = await MissionTestData.GetMissionAsync(workspace, mission.MissionId, token);
        var rework = await workspace.Service.ReviewMissionTaskAsync(
            new ReviewMissionTaskRequest(
                MissionTestData.Command(mission.Revision),
                mission.MissionId,
                required.TaskId,
                Accepted: false,
                "Needs another pass."),
            token);
        Assert.True(rework.IsSuccess);
        Assert.Equal(CoWorkTaskStatus.Ready, rework.Value!.Status);

        mission = await MissionTestData.ReconcileUntilAsync(
            workspace,
            mission.MissionId,
            candidate => candidate.Tasks.Single(task =>
                    task.TaskId == required.TaskId)
                .CurrentAttempt == 2 &&
                         candidate.Tasks.Single(task =>
                                 task.TaskId == required.TaskId)
                             .Status == CoWorkTaskStatus.Review,
            token);
        var reviewed = mission.Tasks.Single(task => task.TaskId == required.TaskId);
        Assert.Equal(2, reviewed.CurrentAttempt);
        Assert.Equal(CoWorkTaskStatus.Review, reviewed.Status);
        Assert.Equal(
            2,
            await MissionTestData.CountAsync(
                workspace.Store,
                "SELECT count(*) FROM agent_runs WHERE mission_task_id = $id;",
                token,
                ("$id", required.TaskId)));

        var accepted = await workspace.Service.ReviewMissionTaskAsync(
            new ReviewMissionTaskRequest(
                MissionTestData.Command(mission.Revision),
                mission.MissionId,
                required.TaskId,
                Accepted: true,
                Comment: null),
            token);
        Assert.True(accepted.IsSuccess);
        mission = await MissionTestData.ReconcileUntilAsync(
            workspace,
            mission.MissionId,
            candidate => candidate.Status == CoWorkMissionStatus.Completed,
            token);
        Assert.Equal(CoWorkMissionStatus.Completed, mission.Status);
    }

    [Fact]
    public async Task Failed_optional_task_can_be_reassigned_and_waived_without_failing_mission()
    {
        await using var workspace = await CoWorkTestWorkspace.CreateAsync(
            executor: new MissionCompletionExecutor("optional"));
        var token = TestContext.Current.CancellationToken;
        var setup = await MissionTestData.CreateAsync(
            workspace,
            CoWorkWorkspaceMode.Project,
            20_000,
            ("leader", CoWorkMemberRole.Leader, Array.Empty<string>()),
            ("one", CoWorkMemberRole.Member, Array.Empty<string>()),
            ("two", CoWorkMemberRole.Member, Array.Empty<string>()));
        var mission = setup.Mission;
        var optional = await MissionTestData.AddTaskAsync(
            workspace,
            mission,
            "optional",
            setup.Members["one"].MemberId,
            required: false,
            requiresReview: null,
            dependsOn: [],
            token);
        mission = await MissionTestData.GetMissionAsync(workspace, mission.MissionId, token);
        _ = await workspace.Service.ActivateMissionAsync(
            new MissionCommandRequest(
                MissionTestData.Command(mission.Revision),
                mission.MissionId),
            token);
        mission = await MissionTestData.ReconcileUntilAsync(
            workspace,
            mission.MissionId,
            candidate => candidate.Tasks.Single().Status == CoWorkTaskStatus.Failed,
            token);
        Assert.Equal(CoWorkTaskStatus.Failed, Assert.Single(mission.Tasks).Status);
        var reassigned = await workspace.Service.ReassignMissionTaskAsync(
            new ReassignMissionTaskRequest(
                MissionTestData.Command(mission.Revision),
                mission.MissionId,
                optional.TaskId,
                setup.Members["two"].MemberId),
            token);
        Assert.True(reassigned.IsSuccess);
        mission = await MissionTestData.GetMissionAsync(workspace, mission.MissionId, token);
        var waived = await workspace.Service.WaiveMissionTaskAsync(
            new MissionTaskCommandRequest(
                MissionTestData.Command(mission.Revision),
                mission.MissionId,
                optional.TaskId),
            token);
        Assert.True(waived.IsSuccess);
        Assert.Equal(CoWorkTaskStatus.Completed, waived.Value!.Status);

        mission = await MissionTestData.ReconcileUntilAsync(
            workspace,
            mission.MissionId,
            candidate => candidate.Status == CoWorkMissionStatus.Completed,
            token);
        Assert.Equal(CoWorkMissionStatus.Completed, mission.Status);
    }

    [Fact]
    public async Task Member_output_summary_is_redacted_before_persistence()
    {
        const string secret = "summary-secret-7f4c";
        await using var workspace = await CoWorkTestWorkspace.CreateAsync(
            secret: secret,
            executor: new TextMissionExecutor($"Result contains {secret}."));
        var token = TestContext.Current.CancellationToken;
        var setup = await MissionTestData.CreateAsync(
            workspace,
            CoWorkWorkspaceMode.Project,
            20_000,
            ("leader", CoWorkMemberRole.Leader, Array.Empty<string>()),
            ("worker", CoWorkMemberRole.Member, Array.Empty<string>()));
        _ = await MissionTestData.AddTaskAsync(
            workspace,
            setup.Mission,
            "redact",
            setup.Members["worker"].MemberId,
            required: true,
            requiresReview: false,
            dependsOn: [],
            token);
        var mission = await MissionTestData.GetMissionAsync(
            workspace,
            setup.Mission.MissionId,
            token);
        _ = await workspace.Service.ActivateMissionAsync(
            new MissionCommandRequest(
                MissionTestData.Command(mission.Revision),
                mission.MissionId),
            token);
        mission = await MissionTestData.ReconcileUntilAsync(
            workspace,
            mission.MissionId,
            candidate => candidate.Tasks.Single().Status == CoWorkTaskStatus.Completed,
            token);
        var summary = Assert.Single(mission.Tasks).OutputSummary!;
        Assert.DoesNotContain(secret, summary, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", summary, StringComparison.Ordinal);
    }
}

internal sealed class TextMissionExecutor(string text) : ISessionExecutor
{
    public async ValueTask ExecuteAsync(
        AgentSession context,
        ISessionExecutionSink sink,
        CancellationToken cancellationToken)
    {
        var itemId = Guid.CreateVersion7();
        await sink.EmitAsync(
            new StartItemIntent(
                itemId,
                SessionItemType.AgentMessage,
                new TextItemContent(text)),
            cancellationToken);
        await sink.EmitAsync(new CompleteItemIntent(itemId), cancellationToken);
        await sink.EmitAsync(new CompleteTurnIntent(), cancellationToken);
    }
}
