using System.Collections.Concurrent;
using System.Data.Common;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Workspaces;
using Xunit;

namespace OpenCoWork.IntegrationTests;

public sealed class MissionReconcilerTests
{
    [Fact]
    public async Task Planning_intent_creates_one_leader_thread_and_dependencies_run_in_order()
    {
        await using var workspace = await CoWorkTestWorkspace.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        var setup = await MissionTestData.CreateAsync(
            workspace,
            CoWorkWorkspaceMode.Project,
            20_000,
            ("leader", CoWorkMemberRole.Leader, Array.Empty<string>()),
            ("worker", CoWorkMemberRole.Member, Array.Empty<string>()));

        await workspace.Service.ReconcilePendingAsync(token);
        var planning = await MissionTestData.GetMissionAsync(
            workspace,
            setup.Mission.MissionId,
            token);
        Assert.NotNull(planning.LeaderThreadId);
        Assert.Equal(
            1,
            await MissionTestData.CountAsync(
                workspace.Store,
                "SELECT count(*) FROM agent_runs WHERE mission_id = $id AND run_kind = 'leaderPlanning';",
                token,
                ("$id", planning.MissionId)));

        var worker = setup.Members["worker"];
        var first = await MissionTestData.AddTaskAsync(
            workspace,
            planning,
            "first",
            worker.MemberId,
            required: true,
            requiresReview: false,
            dependsOn: [],
            token);
        planning = await MissionTestData.GetMissionAsync(workspace, planning.MissionId, token);
        _ = await MissionTestData.AddTaskAsync(
            workspace,
            planning,
            "second",
            worker.MemberId,
            required: true,
            requiresReview: false,
            dependsOn: [first.Alias],
            token);
        planning = await MissionTestData.GetMissionAsync(workspace, planning.MissionId, token);

        var activated = await workspace.Service.ActivateMissionAsync(
            new MissionCommandRequest(
                MissionTestData.Command(planning.Revision),
                planning.MissionId),
            token);
        Assert.True(activated.IsSuccess);

        var completed = await MissionTestData.ReconcileUntilAsync(
            workspace,
            planning.MissionId,
            mission => mission.Tasks.All(task =>
                task.Status == CoWorkTaskStatus.Completed),
            token);
        Assert.All(
            completed.Tasks,
            task => Assert.Equal(CoWorkTaskStatus.Completed, task.Status));
        Assert.Equal(CoWorkMissionStatus.AwaitingLeaderReview, completed.Status);
        Assert.Equal(
            2,
            await MissionTestData.CountAsync(
                workspace.Store,
                "SELECT count(*) FROM agent_runs WHERE mission_id = $id AND run_kind = 'missionTask';",
                token,
                ("$id", planning.MissionId)));
    }

    [Fact]
    public async Task Same_member_can_hold_only_one_active_task_run()
    {
        var executor = new GatedMissionExecutor();
        await using var workspace = await CoWorkTestWorkspace.CreateAsync(executor: executor);
        var token = TestContext.Current.CancellationToken;
        var setup = await MissionTestData.CreateAsync(
            workspace,
            CoWorkWorkspaceMode.Project,
            20_000,
            ("leader", CoWorkMemberRole.Leader, Array.Empty<string>()),
            ("one", CoWorkMemberRole.Member, Array.Empty<string>()),
            ("two", CoWorkMemberRole.Member, Array.Empty<string>()));
        var mission = setup.Mission;
        foreach (var (alias, member) in new[]
                 {
                     ("one-a", setup.Members["one"]),
                     ("one-b", setup.Members["one"]),
                     ("two-a", setup.Members["two"]),
                 })
        {
            _ = await MissionTestData.AddTaskAsync(
                workspace,
                mission,
                alias,
                member.MemberId,
                true,
                false,
                [],
                token);
            mission = await MissionTestData.GetMissionAsync(workspace, mission.MissionId, token);
        }

        _ = await workspace.Service.ActivateMissionAsync(
            new MissionCommandRequest(
                MissionTestData.Command(mission.Revision),
                mission.MissionId),
            token);
        await workspace.Service.ReconcilePendingAsync(token);

        Assert.Equal(
            2,
            await MissionTestData.CountAsync(
                workspace.Store,
                """
                SELECT count(*) FROM agent_runs
                WHERE mission_id = $id
                  AND run_kind = 'missionTask'
                  AND status IN ('pending', 'starting', 'running');
                """,
                token,
                ("$id", mission.MissionId)));
        Assert.Equal(
            1,
            await MissionTestData.CountAsync(
                workspace.Store,
                """
                SELECT count(*) FROM agent_runs
                WHERE mission_id = $id
                  AND member_id = $member
                  AND run_kind = 'missionTask'
                  AND status IN ('pending', 'starting', 'running');
                """,
                token,
                ("$id", mission.MissionId),
                ("$member", setup.Members["one"].MemberId)));

        executor.Release();
        await workspace.Service.ReconcilePendingAsync(token);
    }

    [Fact]
    public async Task Project_writer_is_serialized_while_worktree_writers_run_together()
    {
        var projectExecutor = new GatedMissionExecutor();
        await using (var project = await CoWorkTestWorkspace.CreateAsync(
                         executor: projectExecutor))
        {
            var token = TestContext.Current.CancellationToken;
            var setup = await MissionTestData.CreateAsync(
                project,
                CoWorkWorkspaceMode.Project,
                20_000,
                ("leader", CoWorkMemberRole.Leader, Array.Empty<string>()),
                ("one", CoWorkMemberRole.Member, ["file.write"]),
                ("two", CoWorkMemberRole.Member, ["shell.execute"]));
            var mission = await MissionTestData.AddIndependentTasksAsync(
                project,
                setup,
                token);
            await project.Service.ReconcilePendingAsync(token);
            Assert.Equal(
                1,
                await MissionTestData.ActiveTaskRunsAsync(project, mission.MissionId, token));
            projectExecutor.Release();
            await project.Service.ReconcilePendingAsync(token);
        }

        var worktreeExecutor = new GatedMissionExecutor();
        await using var worktree = await CoWorkTestWorkspace.CreateAsync(
            executor: worktreeExecutor,
            worktreeFactory: paths => new FakeManagedWorktrees(paths));
        var worktreeToken = TestContext.Current.CancellationToken;
        var worktreeSetup = await MissionTestData.CreateAsync(
            worktree,
            CoWorkWorkspaceMode.Worktree,
            20_000,
            ("leader", CoWorkMemberRole.Leader, Array.Empty<string>()),
            ("one", CoWorkMemberRole.Member, ["file.write"]),
            ("two", CoWorkMemberRole.Member, ["shell.execute"]));
        var worktreeMission = await MissionTestData.AddIndependentTasksAsync(
            worktree,
            worktreeSetup,
            worktreeToken);
        await worktree.Service.ReconcilePendingAsync(worktreeToken);
        Assert.Equal(
            2,
            await MissionTestData.ActiveTaskRunsAsync(
                worktree,
                worktreeMission.MissionId,
                worktreeToken));
        worktreeExecutor.Release();
        await worktree.Service.ReconcilePendingAsync(worktreeToken);
    }
}

internal static class MissionTestData
{
    internal sealed record Setup(
        MissionSnapshot Mission,
        IReadOnlyDictionary<string, MissionMemberSnapshot> Members);

    public static CoWorkCommandContext Command(long? revision) =>
        new(Guid.CreateVersion7(), CoWorkTestWorkspace.Host, revision);

    public static async Task<Setup> CreateAsync(
        CoWorkTestWorkspace workspace,
        CoWorkWorkspaceMode mode,
        long budget,
        params (string Alias, CoWorkMemberRole Role, string[] Tools)[] members)
    {
        var token = TestContext.Current.CancellationToken;
        var inputs = new List<TeamMemberInput>();
        foreach (var member in members)
        {
            var profile = await workspace.Service.UpsertAgentProfileAsync(
                new UpsertAgentProfileRequest(
                    Command(null),
                    null,
                    member.Alias,
                    "",
                    "Complete the assigned work.",
                    "fake",
                    "fake-model",
                    [],
                    member.Tools),
                token);
            Assert.True(profile.IsSuccess);
            inputs.Add(new TeamMemberInput(
                null,
                member.Alias,
                profile.Value!.ProfileId,
                member.Role,
                ""));
        }

        var team = await workspace.Service.UpsertTeamAsync(
            new UpsertTeamRequest(
                Command(null),
                null,
                $"team-{Guid.CreateVersion7():N}",
                "",
                inputs),
            token);
        Assert.True(team.IsSuccess);
        var mission = await workspace.Service.CreateMissionAsync(
            new CreateMissionRequest(
                Command(null),
                workspace.OriginThreadId,
                team.Value!.TeamId,
                "Complete the mission.",
                budget,
                mode),
            token);
        Assert.True(mission.IsSuccess);
        return new Setup(
            mission.Value!,
            mission.Value!.Members.ToDictionary(member => member.Alias));
    }

    public static async Task<MissionTaskSnapshot> AddTaskAsync(
        CoWorkTestWorkspace workspace,
        MissionSnapshot mission,
        string alias,
        Guid memberId,
        bool required,
        bool? requiresReview,
        IReadOnlyList<string> dependsOn,
        CancellationToken token)
    {
        var result = await workspace.Service.AddMissionTaskAsync(
            new AddMissionTaskRequest(
                Command(mission.Revision),
                mission.MissionId,
                alias,
                $"Objective {alias}",
                $"Instructions {alias}",
                memberId,
                required,
                requiresReview,
                dependsOn),
            token);
        Assert.True(result.IsSuccess);
        return result.Value!;
    }

    public static async Task<MissionSnapshot> AddIndependentTasksAsync(
        CoWorkTestWorkspace workspace,
        Setup setup,
        CancellationToken token)
    {
        var mission = setup.Mission;
        foreach (var alias in new[] { "one", "two" })
        {
            _ = await AddTaskAsync(
                workspace,
                mission,
                alias,
                setup.Members[alias].MemberId,
                true,
                false,
                [],
                token);
            mission = await GetMissionAsync(workspace, mission.MissionId, token);
        }

        var activated = await workspace.Service.ActivateMissionAsync(
            new MissionCommandRequest(Command(mission.Revision), mission.MissionId),
            token);
        Assert.True(activated.IsSuccess);
        return activated.Value!;
    }

    public static async Task<MissionSnapshot> GetMissionAsync(
        CoWorkTestWorkspace workspace,
        Guid missionId,
        CancellationToken token) =>
        (await workspace.Service.GetMissionAsync(
            new GetMissionRequest(CoWorkTestWorkspace.Host, missionId),
            token)).Value!;

    public static async Task<MissionSnapshot> ReconcileUntilAsync(
        CoWorkTestWorkspace workspace,
        Guid missionId,
        Func<MissionSnapshot, bool> predicate,
        CancellationToken token)
    {
        for (var attempt = 0; attempt < 1_000; attempt++)
        {
            await workspace.Service.ReconcilePendingAsync(token);
            var mission = await GetMissionAsync(workspace, missionId, token);
            if (predicate(mission))
            {
                return mission;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), token);
        }

        throw new TimeoutException("Mission did not reach the expected state.");
    }

    public static ValueTask<long> ActiveTaskRunsAsync(
        CoWorkTestWorkspace workspace,
        Guid missionId,
        CancellationToken token) =>
        CountAsync(
            workspace.Store,
            """
            SELECT count(*) FROM agent_runs
            WHERE mission_id = $id
              AND run_kind = 'missionTask'
              AND status IN ('pending', 'starting', 'running');
            """,
            token,
            ("$id", missionId));

    public static ValueTask<long> CountAsync(
        IWorkspaceStateStore store,
        string sql,
        CancellationToken token,
        params (string Name, object? Value)[] parameters) =>
        store.ReadAsync(
            async (connection, cancellationToken) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                foreach (var parameter in parameters)
                {
                    var value = command.CreateParameter();
                    value.ParameterName = parameter.Name;
                    value.Value = parameter.Value is Guid guid
                        ? guid.ToString("D")
                        : parameter.Value ?? DBNull.Value;
                    command.Parameters.Add(value);
                }

                return Convert.ToInt64(
                    await command.ExecuteScalarAsync(cancellationToken),
                    System.Globalization.CultureInfo.InvariantCulture);
            },
            token);
}

internal sealed class GatedMissionExecutor : ISessionExecutor
{
    private readonly TaskCompletionSource _release =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask ExecuteAsync(
        AgentSession context,
        ISessionExecutionSink sink,
        CancellationToken cancellationToken)
    {
        await _release.Task.WaitAsync(cancellationToken);
        await sink.EmitAsync(new CompleteTurnIntent(), cancellationToken);
    }

    public void Release() => _release.TrySetResult();
}

internal sealed class FakeManagedWorktrees(OpenCoWorkPaths paths)
    : IManagedWorktreeService
{
    private readonly ConcurrentDictionary<Guid, ManagedWorktreeDescriptor> _items = [];

    public ValueTask<ManagedWorktreeDescriptor> CreateAsync(
        Guid agentRunId,
        CancellationToken cancellationToken = default)
    {
        var root = Path.Combine(paths.WorktreesDirectory, agentRunId.ToString("D"));
        Directory.CreateDirectory(root);
        var value = new ManagedWorktreeDescriptor(
            Guid.CreateVersion7(),
            root,
            new string('a', 40),
            CoWorkWorktreeStatus.Ready,
            IsDirty: false);
        _items[value.WorktreeId] = value;
        return ValueTask.FromResult(value);
    }

    public ValueTask<ManagedWorktreeDescriptor> CreateAsync(
        ManagedWorktreeCreateRequest request,
        CancellationToken cancellationToken = default) =>
        CreateAsync(request.AgentRunId, cancellationToken);

    public ValueTask<ManagedWorktreeDescriptor?> GetAsync(
        Guid worktreeId,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_items.GetValueOrDefault(worktreeId));

    public ValueTask<ManagedWorktreeDescriptor> RemoveAsync(
        Guid worktreeId,
        CancellationToken cancellationToken = default)
    {
        var value = _items[worktreeId] with { Status = CoWorkWorktreeStatus.Removed };
        _items[worktreeId] = value;
        return ValueTask.FromResult(value);
    }
}
