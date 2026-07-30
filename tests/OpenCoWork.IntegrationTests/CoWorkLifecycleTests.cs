using OpenCoWork.Abstractions;
using OpenCoWork.Teams;
using Xunit;

namespace OpenCoWork.IntegrationTests;

public sealed class CoWorkLifecycleTests
{
    [Fact]
    public async Task Start_and_stop_change_binding_availability()
    {
        var runtime = new CoWorkModuleRuntime(service: null);

        await runtime.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ToolBindingAvailability.Available, runtime.BindingAvailability);

        await runtime.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ToolBindingAvailability.Unavailable, runtime.BindingAvailability);
    }

    [Fact]
    public async Task Normal_reconciler_stop_does_not_fail_an_active_mission()
    {
        var executor = new GatedMissionExecutor();
        await using var workspace = await CoWorkTestWorkspace.CreateAsync(executor: executor);
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
            "work",
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
        await workspace.Service.StartReconcilerAsync(token);

        await workspace.Service.StopReconcilerAsync(token);
        var stopped = await MissionTestData.GetMissionAsync(
            workspace,
            mission.MissionId,
            token);

        Assert.Equal(CoWorkMissionStatus.Active, stopped.Status);
        executor.Release();
    }
}
