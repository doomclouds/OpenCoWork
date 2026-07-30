using OpenCoWork.Abstractions;
using OpenCoWork.Teams;
using Xunit;

namespace OpenCoWork.IntegrationTests;

public sealed class MissionDagPropertyTests
{
    [Fact]
    public async Task Dag_rejects_cycles_and_is_deterministic_at_the_task_limit()
    {
        await using var workspace = await CoWorkTestWorkspace.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = (await workspace.Service.UpsertAgentProfileAsync(
            Profile("dag-profile"),
            cancellationToken)).Value!;
        var team = (await workspace.Service.UpsertTeamAsync(
            Team(profile.ProfileId),
            cancellationToken)).Value!;
        var mission = (await workspace.Service.CreateMissionAsync(
            new CreateMissionRequest(
                Command(null),
                workspace.OriginThreadId,
                team.TeamId,
                "DAG",
                100_000,
                CoWorkWorkspaceMode.Project),
            cancellationToken)).Value!;
        var memberId = Assert.Single(mission.Members).MemberId;
        var random = new Random(0xC0);

        for (var index = 0; index < CoWorkRuntimeLimits.MaximumMissionTasks; index++)
        {
            var dependencies = index == 0
                ? []
                : Enumerable.Range(0, index)
                    .OrderBy(_ => random.Next())
                    .Take(Math.Min(index, 3))
                    .Select(candidate => $"task-{candidate:D3}")
                    .ToArray();
            var latest = (await workspace.Service.GetMissionAsync(
                new GetMissionRequest(CoWorkTestWorkspace.Host, mission.MissionId),
                cancellationToken)).Value!;
            var result = await workspace.Service.AddMissionTaskAsync(
                new AddMissionTaskRequest(
                    Command(latest.Revision),
                    mission.MissionId,
                    $"task-{index:D3}",
                    $"Task {index}",
                    "",
                    memberId,
                    Required: index % 2 == 0,
                    RequiresReview: index % 3 == 0,
                    DependsOn: dependencies),
                cancellationToken);
            Assert.True(result.IsSuccess, result.Error?.Message);
        }

        mission = (await workspace.Service.GetMissionAsync(
            new GetMissionRequest(CoWorkTestWorkspace.Host, mission.MissionId),
            cancellationToken)).Value!;
        var overflow = await workspace.Service.AddMissionTaskAsync(
            new AddMissionTaskRequest(
                Command(mission.Revision),
                mission.MissionId,
                "overflow",
                "Overflow",
                "",
                memberId,
                true,
                false,
                []),
            cancellationToken);
        Assert.Equal(CoWorkErrorCodes.InvalidState, overflow.Error?.Code);

        var cycle = await workspace.Service.UpdateMissionTaskAsync(
            new UpdateMissionTaskRequest(
                Command(mission.Revision),
                mission.MissionId,
                mission.Tasks[0].TaskId,
                mission.Tasks[0].Objective,
                mission.Tasks[0].Instructions,
                memberId,
                mission.Tasks[0].Required,
                mission.Tasks[0].RequiresReview,
                ["task-255"]),
            cancellationToken);
        Assert.Equal(CoWorkErrorCodes.InvalidDag, cycle.Error?.Code);

        var activated = await workspace.Service.ActivateMissionAsync(
            new MissionCommandRequest(
                Command(mission.Revision),
                mission.MissionId),
            cancellationToken);
        Assert.True(activated.IsSuccess, activated.Error?.Message);
        Assert.Equal(
            ["task-000"],
            activated.Value!.Tasks
                .Where(task => task.Status == CoWorkTaskStatus.Ready)
                .Select(task => task.Alias));

        var activeMutation = await workspace.Service.UpdateMissionTaskAsync(
            new UpdateMissionTaskRequest(
                Command(activated.Value.Revision),
                activated.Value.MissionId,
                activated.Value.Tasks[1].TaskId,
                activated.Value.Tasks[1].Objective,
                activated.Value.Tasks[1].Instructions,
                memberId,
                activated.Value.Tasks[1].Required,
                activated.Value.Tasks[1].RequiresReview,
                activated.Value.Tasks[1].DependsOn),
            cancellationToken);
        Assert.Equal(CoWorkErrorCodes.InvalidState, activeMutation.Error?.Code);
    }

    private static UpsertAgentProfileRequest Profile(string name) =>
        new(
            Command(null),
            null,
            name,
            "",
            "",
            "fake",
            "fake-model",
            [],
            []);

    private static UpsertTeamRequest Team(Guid profileId) =>
        new(
            Command(null),
            null,
            "dag-team",
            "",
            [
                new TeamMemberInput(
                    null,
                    "leader",
                    profileId,
                    CoWorkMemberRole.Leader,
                    ""),
            ]);

    private static CoWorkCommandContext Command(long? revision) =>
        new(Guid.CreateVersion7(), CoWorkTestWorkspace.Host, revision);
}
