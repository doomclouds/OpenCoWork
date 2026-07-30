using OpenCoWork.Abstractions;
using Xunit;

namespace OpenCoWork.IntegrationTests;

public sealed class MissionRecoveryTests
{
    [Fact]
    public async Task Maximum_256_task_mission_recovers_all_terminal_runs_from_session_history()
    {
        await using var workspace = await CoWorkTestWorkspace.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        var setup = await MissionTestData.CreateAsync(
            workspace,
            CoWorkWorkspaceMode.Project,
            500_000,
            ("leader", CoWorkMemberRole.Leader, Array.Empty<string>()),
            ("worker", CoWorkMemberRole.Member, Array.Empty<string>()));
        var mission = setup.Mission;
        for (var index = 0; index < CoWorkRuntimeLimits.MaximumMissionTasks; index++)
        {
            _ = await MissionTestData.AddTaskAsync(
                workspace,
                mission,
                $"task-{index:D3}",
                setup.Members["worker"].MemberId,
                required: true,
                requiresReview: false,
                dependsOn: [],
                token);
            mission = await MissionTestData.GetMissionAsync(workspace, mission.MissionId, token);
        }

        var overflow = await workspace.Service.AddMissionTaskAsync(
            new AddMissionTaskRequest(
                MissionTestData.Command(mission.Revision),
                mission.MissionId,
                "overflow",
                "Must be rejected.",
                "",
                setup.Members["worker"].MemberId,
                true,
                false,
                []),
            token);
        Assert.Equal(CoWorkErrorCodes.InvalidState, overflow.Error?.Code);

        var activated = await workspace.Service.ActivateMissionAsync(
            new MissionCommandRequest(
                MissionTestData.Command(mission.Revision),
                mission.MissionId),
            token);
        Assert.True(activated.IsSuccess);
        mission = await MissionTestData.ReconcileUntilAsync(
            workspace,
            mission.MissionId,
            candidate => candidate.Tasks.All(task =>
                task.Status == CoWorkTaskStatus.Completed),
            token);
        Assert.Equal(CoWorkRuntimeLimits.MaximumMissionTasks, mission.Tasks.Count);
        Assert.All(
            mission.Tasks,
            task => Assert.Equal(CoWorkTaskStatus.Completed, task.Status));
        Assert.Equal(CoWorkMissionStatus.AwaitingLeaderReview, mission.Status);
    }

    [Fact]
    public async Task Restart_and_duplicate_wake_reconcile_one_task_exactly_once()
    {
        await using var workspace = await CoWorkTestWorkspace.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        var setup = await MissionTestData.CreateAsync(
            workspace,
            CoWorkWorkspaceMode.Project,
            20_000,
            ("leader", CoWorkMemberRole.Leader, Array.Empty<string>()),
            ("worker", CoWorkMemberRole.Member, Array.Empty<string>()));
        var task = await MissionTestData.AddTaskAsync(
            workspace,
            setup.Mission,
            "recover",
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

        workspace.ReplaceService();
        await Task.WhenAll(
            Enumerable.Range(0, 8)
                .Select(_ => workspace.Service.ReconcilePendingAsync(token)));
        workspace.ReplaceService();
        mission = await MissionTestData.ReconcileUntilAsync(
            workspace,
            mission.MissionId,
            candidate => candidate.Tasks.Single().Status == CoWorkTaskStatus.Completed,
            token);
        Assert.Equal(CoWorkTaskStatus.Completed, Assert.Single(mission.Tasks).Status);
        Assert.Equal(
            1,
            await MissionTestData.CountAsync(
                workspace.Store,
                "SELECT count(*) FROM agent_runs WHERE mission_task_id = $id;",
                token,
                ("$id", task.TaskId)));
        Assert.Equal(
            1,
            await MissionTestData.CountAsync(
                workspace.Store,
                """
                SELECT count(DISTINCT thread_id)
                FROM agent_runs
                WHERE mission_task_id = $id;
                """,
                token,
                ("$id", task.TaskId)));
    }
}
