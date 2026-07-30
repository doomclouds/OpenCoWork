using System.Data.Common;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Sessions;
using OpenCoWork.Core.State;
using OpenCoWork.Core.Workspaces;
using OpenCoWork.Teams;
using Xunit;

namespace OpenCoWork.IntegrationTests;

public sealed class CoWorkServiceTests
{
    [Fact]
    public async Task Profile_team_and_command_receipt_are_atomic_and_revision_checked()
    {
        await using var workspace = await CoWorkTestWorkspace.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var host = CoWorkTestWorkspace.Host;
        var commandId = Guid.CreateVersion7();
        var create = Profile(commandId, host, name: "planner");

        var denied = await workspace.Service.UpsertAgentProfileAsync(
            Profile(
                Guid.CreateVersion7(),
                new CoWorkActorContext(CoWorkActorKind.Member, "intruder"),
                "denied"),
            cancellationToken);
        Assert.Equal(CoWorkErrorCodes.PermissionDenied, denied.Error?.Code);

        var first = await workspace.Service.UpsertAgentProfileAsync(
            create,
            cancellationToken);
        var replay = await workspace.Service.UpsertAgentProfileAsync(
            create,
            cancellationToken);

        Assert.True(first.IsSuccess);
        Assert.True(replay.IsSuccess);
        Assert.Equivalent(first.Value, replay.Value, strict: true);
        Assert.Equal(first.CoWorkRevision, replay.CoWorkRevision);

        var conflictingReplay = await workspace.Service.UpsertAgentProfileAsync(
            create with { Name = "different" },
            cancellationToken);
        Assert.Equal(CoWorkErrorCodes.Conflict, conflictingReplay.Error?.Code);

        var stale = await workspace.Service.UpsertAgentProfileAsync(
            Profile(Guid.CreateVersion7(), host, "planner") with
            {
                ProfileId = first.Value!.ProfileId,
                Command = new CoWorkCommandContext(
                    Guid.CreateVersion7(),
                    host,
                    ExpectedRevision: 0),
            },
            cancellationToken);
        Assert.Equal(CoWorkErrorCodes.Conflict, stale.Error?.Code);

        var invalidTeam = await workspace.Service.UpsertTeamAsync(
            new UpsertTeamRequest(
                new CoWorkCommandContext(Guid.CreateVersion7(), host, null),
                null,
                "invalid",
                "",
                [
                    new TeamMemberInput(
                        null,
                        "worker",
                        first.Value!.ProfileId,
                        CoWorkMemberRole.Member,
                        ""),
                ]),
            cancellationToken);
        Assert.Equal(CoWorkErrorCodes.InvalidState, invalidTeam.Error?.Code);

        var team = await workspace.Service.UpsertTeamAsync(
            Team(Guid.CreateVersion7(), host, first.Value!.ProfileId),
            cancellationToken);
        Assert.True(team.IsSuccess);
        Assert.Single(team.Value!.Members);
        Assert.Equal(CoWorkMemberRole.Leader, team.Value.Members[0].Role);
    }

    [Fact]
    public async Task Mission_planning_freezes_members_validates_dag_and_activates()
    {
        await using var workspace = await CoWorkTestWorkspace.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = (await workspace.Service.UpsertAgentProfileAsync(
            Profile(Guid.CreateVersion7(), CoWorkTestWorkspace.Host, "leader"),
            cancellationToken)).Value!;
        var team = (await workspace.Service.UpsertTeamAsync(
            Team(Guid.CreateVersion7(), CoWorkTestWorkspace.Host, profile.ProfileId),
            cancellationToken)).Value!;
        var mission = (await workspace.Service.CreateMissionAsync(
            new CreateMissionRequest(
                Command(expectedRevision: null),
                workspace.OriginThreadId,
                team.TeamId,
                "Ship M7",
                10_000,
                CoWorkWorkspaceMode.Project),
            cancellationToken)).Value!;
        var member = Assert.Single(mission.Members);
        Assert.Equal(profile.ProfileId, member.Profile.ProfileId);
        Assert.Equal(profile.Revision, member.Profile.Revision);

        var first = await workspace.Service.AddMissionTaskAsync(
            new AddMissionTaskRequest(
                Command(mission.Revision),
                mission.MissionId,
                "design",
                "Design",
                "Write the design.",
                member.MemberId,
                Required: true,
                RequiresReview: true,
                DependsOn: []),
            cancellationToken);
        Assert.True(first.IsSuccess);
        mission = (await workspace.Service.GetMissionAsync(
            new GetMissionRequest(CoWorkTestWorkspace.Host, mission.MissionId),
            cancellationToken)).Value!;

        var second = await workspace.Service.AddMissionTaskAsync(
            new AddMissionTaskRequest(
                Command(mission.Revision),
                mission.MissionId,
                "build",
                "Build",
                "Implement it.",
                member.MemberId,
                Required: true,
                RequiresReview: false,
                DependsOn: ["design"]),
            cancellationToken);
        Assert.True(second.IsSuccess);
        mission = (await workspace.Service.GetMissionAsync(
            new GetMissionRequest(CoWorkTestWorkspace.Host, mission.MissionId),
            cancellationToken)).Value!;

        var missing = await workspace.Service.AddMissionTaskAsync(
            new AddMissionTaskRequest(
                Command(mission.Revision),
                mission.MissionId,
                "broken",
                "Broken",
                "",
                member.MemberId,
                Required: true,
                RequiresReview: false,
                DependsOn: ["missing"]),
            cancellationToken);
        Assert.Equal(CoWorkErrorCodes.InvalidDag, missing.Error?.Code);

        var activated = await workspace.Service.ActivateMissionAsync(
            new MissionCommandRequest(
                Command(mission.Revision),
                mission.MissionId),
            cancellationToken);
        Assert.True(activated.IsSuccess);
        Assert.Equal(CoWorkMissionStatus.Active, activated.Value!.Status);
        Assert.Equal(
            CoWorkTaskStatus.Ready,
            activated.Value.Tasks.Single(task => task.Alias == "design").Status);
        Assert.Equal(
            CoWorkTaskStatus.WaitingDependencies,
            activated.Value.Tasks.Single(task => task.Alias == "build").Status);

        var activeMutation = await workspace.Service.UpdateMissionTaskAsync(
            new UpdateMissionTaskRequest(
                Command(activated.Value.Revision),
                activated.Value.MissionId,
                activated.Value.Tasks[0].TaskId,
                "Changed after activation",
                "",
                member.MemberId,
                true,
                true,
                []),
            cancellationToken);
        Assert.Equal(CoWorkErrorCodes.InvalidState, activeMutation.Error?.Code);
    }

    [Fact]
    public async Task Mission_activation_rejects_profile_drift()
    {
        await using var workspace = await CoWorkTestWorkspace.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = (await workspace.Service.UpsertAgentProfileAsync(
            Profile(Guid.CreateVersion7(), CoWorkTestWorkspace.Host, "drift"),
            cancellationToken)).Value!;
        var team = (await workspace.Service.UpsertTeamAsync(
            Team(Guid.CreateVersion7(), CoWorkTestWorkspace.Host, profile.ProfileId),
            cancellationToken)).Value!;
        var mission = (await workspace.Service.CreateMissionAsync(
            new CreateMissionRequest(
                Command(null),
                workspace.OriginThreadId,
                team.TeamId,
                "Detect drift",
                1_000,
                CoWorkWorkspaceMode.Project),
            cancellationToken)).Value!;
        var member = Assert.Single(mission.Members);
        var task = await workspace.Service.AddMissionTaskAsync(
            new AddMissionTaskRequest(
                Command(mission.Revision),
                mission.MissionId,
                "one",
                "One",
                "",
                member.MemberId,
                true,
                true,
                []),
            cancellationToken);
        Assert.True(task.IsSuccess);
        mission = (await workspace.Service.GetMissionAsync(
            new GetMissionRequest(CoWorkTestWorkspace.Host, mission.MissionId),
            cancellationToken)).Value!;

        var disabled = await workspace.Service.SetAgentProfileEnabledAsync(
            new SetAgentProfileEnabledRequest(
                Command(profile.Revision),
                profile.ProfileId,
                false),
            cancellationToken);
        Assert.True(disabled.IsSuccess);

        var activated = await workspace.Service.ActivateMissionAsync(
            new MissionCommandRequest(
                Command(mission.Revision),
                mission.MissionId),
            cancellationToken);
        Assert.Equal(CoWorkErrorCodes.Conflict, activated.Error?.Code);
    }

    [Fact]
    public async Task Mission_permissions_are_bound_to_frozen_members()
    {
        await using var workspace = await CoWorkTestWorkspace.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var leaderProfile = (await workspace.Service.UpsertAgentProfileAsync(
            Profile(Guid.CreateVersion7(), CoWorkTestWorkspace.Host, "leader-role"),
            cancellationToken)).Value!;
        var memberProfile = (await workspace.Service.UpsertAgentProfileAsync(
            Profile(Guid.CreateVersion7(), CoWorkTestWorkspace.Host, "member-role"),
            cancellationToken)).Value!;
        var team = (await workspace.Service.UpsertTeamAsync(
            new UpsertTeamRequest(
                Command(null),
                null,
                "roles",
                "",
                [
                    new TeamMemberInput(
                        null,
                        "leader",
                        leaderProfile.ProfileId,
                        CoWorkMemberRole.Leader,
                        ""),
                    new TeamMemberInput(
                        null,
                        "member",
                        memberProfile.ProfileId,
                        CoWorkMemberRole.Member,
                        ""),
                ]),
            cancellationToken)).Value!;
        var mission = (await workspace.Service.CreateMissionAsync(
            new CreateMissionRequest(
                Command(null),
                workspace.OriginThreadId,
                team.TeamId,
                "Permissions",
                1_000,
                CoWorkWorkspaceMode.Project),
            cancellationToken)).Value!;
        var leader = mission.Members.Single(candidate =>
            candidate.Role == CoWorkMemberRole.Leader);
        var member = mission.Members.Single(candidate =>
            candidate.Role == CoWorkMemberRole.Member);

        foreach (var assignment in new[]
                 {
                     (Alias: "leader-task", MemberId: leader.MemberId),
                     (Alias: "member-task", MemberId: member.MemberId),
                 })
        {
            var added = await workspace.Service.AddMissionTaskAsync(
                new AddMissionTaskRequest(
                    Command(mission.Revision),
                    mission.MissionId,
                    assignment.Alias,
                    assignment.Alias,
                    "",
                    assignment.MemberId,
                    true,
                    true,
                    []),
                cancellationToken);
            Assert.True(added.IsSuccess);
            mission = (await workspace.Service.GetMissionAsync(
                new GetMissionRequest(CoWorkTestWorkspace.Host, mission.MissionId),
                cancellationToken)).Value!;
        }

        var forgedLeader = new CoWorkActorContext(
            CoWorkActorKind.Leader,
            "forged",
            MissionId: mission.MissionId,
            MemberId: Guid.CreateVersion7());
        var forged = await workspace.Service.AddMissionTaskAsync(
            new AddMissionTaskRequest(
                new CoWorkCommandContext(
                    Guid.CreateVersion7(),
                    forgedLeader,
                    mission.Revision),
                mission.MissionId,
                "forged",
                "Forged",
                "",
                member.MemberId,
                true,
                true,
                []),
            cancellationToken);
        Assert.Equal(CoWorkErrorCodes.PermissionDenied, forged.Error?.Code);

        var activated = await workspace.Service.ActivateMissionAsync(
            new MissionCommandRequest(
                Command(mission.Revision),
                mission.MissionId),
            cancellationToken);
        Assert.True(activated.IsSuccess);
        mission = activated.Value!;
        var memberActor = new CoWorkActorContext(
            CoWorkActorKind.Member,
            "member-thread",
            MissionId: mission.MissionId,
            MemberId: member.MemberId);

        var visible = await workspace.Service.GetMissionAsync(
            new GetMissionRequest(memberActor, mission.MissionId),
            cancellationToken);
        Assert.True(visible.IsSuccess);
        var hiddenProfiles = await workspace.Service.ListAgentProfilesAsync(
            new ListAgentProfilesRequest(memberActor),
            cancellationToken);
        Assert.Equal(CoWorkErrorCodes.PermissionDenied, hiddenProfiles.Error?.Code);

        var memberTask = mission.Tasks.Single(task => task.Alias == "member-task");
        var blocked = await workspace.Service.BlockMissionTaskAsync(
            new BlockMissionTaskRequest(
                new CoWorkCommandContext(
                    Guid.CreateVersion7(),
                    memberActor,
                    mission.Revision),
                mission.MissionId,
                memberTask.TaskId,
                "Waiting for input."),
            cancellationToken);
        Assert.True(blocked.IsSuccess);
        mission = (await workspace.Service.GetMissionAsync(
            new GetMissionRequest(memberActor, mission.MissionId),
            cancellationToken)).Value!;

        var leaderTask = mission.Tasks.Single(task => task.Alias == "leader-task");
        var denied = await workspace.Service.BlockMissionTaskAsync(
            new BlockMissionTaskRequest(
                new CoWorkCommandContext(
                    Guid.CreateVersion7(),
                    memberActor,
                    mission.Revision),
                mission.MissionId,
                leaderTask.TaskId,
                "Not mine."),
            cancellationToken);
        Assert.Equal(CoWorkErrorCodes.PermissionDenied, denied.Error?.Code);
    }

    [Fact]
    public async Task Sensitive_profile_task_and_mailbox_inputs_are_rejected()
    {
        const string canary = "cowork-secret-4f1b";
        await using var workspace = await CoWorkTestWorkspace.CreateAsync(secret: canary);
        var cancellationToken = TestContext.Current.CancellationToken;

        var result = await workspace.Service.UpsertAgentProfileAsync(
            Profile(Guid.CreateVersion7(), CoWorkTestWorkspace.Host, "unsafe") with
            {
                Instructions = $"Never persist {canary}.",
            },
            cancellationToken);

        Assert.Equal(CoWorkErrorCodes.SecretDetected, result.Error?.Code);
        var rows = await workspace.Store.ReadAsync(
            (connection, token) => ScalarAsync<long>(
                connection,
                "SELECT count(*) FROM agent_profiles;",
                token),
            cancellationToken);
        Assert.Equal(0, rows);

        var profile = (await workspace.Service.UpsertAgentProfileAsync(
            Profile(Guid.CreateVersion7(), CoWorkTestWorkspace.Host, "safe"),
            cancellationToken)).Value!;
        var team = (await workspace.Service.UpsertTeamAsync(
            Team(Guid.CreateVersion7(), CoWorkTestWorkspace.Host, profile.ProfileId),
            cancellationToken)).Value!;
        var mission = (await workspace.Service.CreateMissionAsync(
            new CreateMissionRequest(
                Command(null),
                workspace.OriginThreadId,
                team.TeamId,
                "Safe mission",
                1_000,
                CoWorkWorkspaceMode.Project),
            cancellationToken)).Value!;
        var member = Assert.Single(mission.Members);

        var task = await workspace.Service.AddMissionTaskAsync(
            new AddMissionTaskRequest(
                Command(mission.Revision),
                mission.MissionId,
                "unsafe",
                "Unsafe task",
                $"Never persist {canary}.",
                member.MemberId,
                true,
                true,
                []),
            cancellationToken);
        Assert.Equal(CoWorkErrorCodes.SecretDetected, task.Error?.Code);

        var mailbox = await workspace.Service.SendMailboxMessageAsync(
            new SendMailboxMessageRequest(
                Command(mission.Revision),
                mission.MissionId,
                member.MemberId,
                CoWorkMailboxKind.Request,
                $"Never persist {canary}."),
            cancellationToken);
        Assert.Equal(CoWorkErrorCodes.SecretDetected, mailbox.Error?.Code);

        var taskRows = await workspace.Store.ReadAsync(
            (connection, token) => ScalarAsync<long>(
                connection,
                "SELECT count(*) FROM mission_tasks;",
                token),
            cancellationToken);
        Assert.Equal(0, taskRows);
    }

    private static UpsertAgentProfileRequest Profile(
        Guid commandId,
        CoWorkActorContext host,
        string name) =>
        new(
            new CoWorkCommandContext(commandId, host, null),
            null,
            name,
            "",
            "Be concise.",
            "fake",
            "fake-model",
            [],
            []);

    private static UpsertTeamRequest Team(
        Guid commandId,
        CoWorkActorContext host,
        Guid profileId) =>
        new(
            new CoWorkCommandContext(commandId, host, null),
            null,
            "delivery",
            "",
            [
                new TeamMemberInput(
                    null,
                    "leader",
                    profileId,
                    CoWorkMemberRole.Leader,
                    ""),
            ]);

    private static CoWorkCommandContext Command(long? expectedRevision) =>
        new(Guid.CreateVersion7(), CoWorkTestWorkspace.Host, expectedRevision);

    private static async ValueTask<T> ScalarAsync<T>(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidDataException("Scalar query returned null.");
        return (T)Convert.ChangeType(
            value,
            typeof(T),
            System.Globalization.CultureInfo.InvariantCulture)!;
    }
}

internal sealed class CoWorkTestWorkspace : IAsyncDisposable
{
    private CoWorkTestWorkspace(
        string root,
        StateRuntime store,
        SessionService sessions,
        CoWorkService service,
        Guid originThreadId,
        ISensitiveDataService sensitiveData,
        CoWorkConfig config,
        TimeProvider timeProvider,
        IManagedWorktreeService worktrees,
        WorkspaceRuntimeDescriptor workspace)
    {
        Root = root;
        Store = store;
        Sessions = sessions;
        Service = service;
        OriginThreadId = originThreadId;
        _sensitiveData = sensitiveData;
        _config = config;
        _timeProvider = timeProvider;
        _worktrees = worktrees;
        _workspace = workspace;
    }

    private readonly ISensitiveDataService _sensitiveData;
    private readonly CoWorkConfig _config;
    private readonly TimeProvider _timeProvider;
    private readonly IManagedWorktreeService _worktrees;
    private readonly WorkspaceRuntimeDescriptor _workspace;

    public static CoWorkActorContext Host { get; } =
        new(CoWorkActorKind.Host, "integration-test");

    public string Root { get; }

    public StateRuntime Store { get; }

    public SessionService Sessions { get; }

    public CoWorkService Service { get; private set; }

    public Guid OriginThreadId { get; }

    public static async Task<CoWorkTestWorkspace> CreateAsync(
        CoWorkConfig? config = null,
        string? secret = null,
        bool completeAgentRuns = true,
        TimeProvider? timeProvider = null,
        Action<CoWorkDispatchFaultPoint>? dispatchFaultInjector = null,
        ISessionExecutor? executor = null,
        Func<OpenCoWorkPaths, IManagedWorktreeService>? worktreeFactory = null)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-cowork-service-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        timeProvider ??= TimeProvider.System;
        config ??= new CoWorkConfig();
        var paths = new OpenCoWorkPaths(root);
        var store = new StateRuntime(
            paths,
            TimeSpan.FromSeconds(2),
            TeamsStateMigrationContributors.Create());
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        executor ??= completeAgentRuns ? new CompletionExecutor() : null;
        var sessions = new SessionService(
            store,
            new ThreadJournal(paths),
            new SessionProjection(store),
            new SessionConfig(),
            timeProvider,
            executor,
            executorKind: executor is null ? null : "test",
            paths: paths);
        var origin = await sessions.CreateThreadAsync(
            new CreateThreadRequest(
                Guid.CreateVersion7(),
                ExpectedSequence: 0,
                DisplayName: "origin"),
            TestContext.Current.CancellationToken);
        var originThreadId = origin.Value!.ThreadId;
        var workspace = new WorkspaceRuntimeDescriptor(
            paths.WorkspaceRoot,
            paths.OpenCoWorkDirectory,
            paths.RuntimeDirectory,
            paths.TeamsRuntimeDirectory,
            paths.MissionsDirectory,
            paths.SubAgentsDirectory,
            paths.WorktreesDirectory);
        var sensitiveData = new TestSensitiveDataService(secret);
        var worktrees = worktreeFactory?.Invoke(paths) ??
                        new ManagedWorktreeService(paths);
        var service = new CoWorkService(
            store,
            sensitiveData,
            config,
            timeProvider,
            sessions,
            worktrees,
            workspace,
            dispatchFaultInjector);
        return new CoWorkTestWorkspace(
            root,
            store,
            sessions,
            service,
            originThreadId,
            sensitiveData,
            config,
            timeProvider,
            worktrees,
            workspace);
    }

    public CoWorkService ReplaceService(
        Action<CoWorkDispatchFaultPoint>? dispatchFaultInjector = null)
    {
        Service = new CoWorkService(
            Store,
            _sensitiveData,
            _config,
            _timeProvider,
            Sessions,
            _worktrees,
            _workspace,
            dispatchFaultInjector);
        return Service;
    }

    public async ValueTask DisposeAsync()
    {
        await Service.StopReconcilerAsync(CancellationToken.None);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(Root, recursive: true);
    }

    private sealed class CompletionExecutor : ISessionExecutor
    {
        public ValueTask ExecuteAsync(
            AgentSession context,
            ISessionExecutionSink sink,
            CancellationToken cancellationToken) =>
            sink.EmitAsync(new CompleteTurnIntent(), cancellationToken);
    }

    private sealed class TestSensitiveDataService(string? secret)
        : ISensitiveDataService
    {
        public bool ContainsSensitiveData(string value) =>
            secret is not null && value.Contains(secret, StringComparison.Ordinal);

        public string Redact(string value) =>
            secret is null
                ? value
                : value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);

        public async ValueTask<bool> ContainsSensitiveDataAsync(
            Stream source,
            CancellationToken cancellationToken = default)
        {
            using var reader = new StreamReader(source, leaveOpen: true);
            return ContainsSensitiveData(await reader.ReadToEndAsync(cancellationToken));
        }
    }
}
