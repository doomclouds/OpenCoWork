using System.Collections.Concurrent;
using OpenCoWork.Abstractions;
using Xunit;

namespace OpenCoWork.IntegrationTests;

public sealed class MissionSynthesisTests
{
    [Fact]
    public async Task Synthesis_waits_for_the_active_leader_run()
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
        var task = await MissionTestData.AddTaskAsync(
            workspace,
            setup.Mission,
            "done",
            setup.Members["worker"].MemberId,
            required: true,
            requiresReview: false,
            dependsOn: [],
            token);
        await workspace.Store.WriteAsync(
            async (connection, transaction, cancellationToken) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    UPDATE mission_tasks
                    SET status = 'completed',
                        completed_utc = $now,
                        updated_utc = $now
                    WHERE mission_task_id = $taskId;
                    """;
                var now = command.CreateParameter();
                now.ParameterName = "$now";
                now.Value = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                command.Parameters.Add(now);
                var taskId = command.CreateParameter();
                taskId.ParameterName = "$taskId";
                taskId.Value = task.TaskId.ToString("D");
                command.Parameters.Add(taskId);
                return await command.ExecuteNonQueryAsync(cancellationToken);
            },
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

        await workspace.Service.ReconcilePendingAsync(token);

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

        executor.Release();
        _ = await MissionTestData.ReconcileUntilAsync(
            workspace,
            mission.MissionId,
            candidate => candidate.Status == CoWorkMissionStatus.Completed,
            token);
    }

    [Fact]
    public async Task Required_completion_and_optional_failure_synthesize_once_from_bounded_input()
    {
        var executor = new MissionCompletionExecutor("optional");
        await using var workspace = await CoWorkTestWorkspace.CreateAsync(executor: executor);
        var token = TestContext.Current.CancellationToken;
        var setup = await MissionTestData.CreateAsync(
            workspace,
            CoWorkWorkspaceMode.Project,
            40_000,
            ("leader", CoWorkMemberRole.Leader, Array.Empty<string>()),
            ("required-worker", CoWorkMemberRole.Member, Array.Empty<string>()),
            ("optional-worker", CoWorkMemberRole.Member, Array.Empty<string>()));
        var mission = setup.Mission;
        _ = await MissionTestData.AddTaskAsync(
            workspace,
            mission,
            "required",
            setup.Members["required-worker"].MemberId,
            required: true,
            requiresReview: false,
            dependsOn: [],
            token);
        mission = await MissionTestData.GetMissionAsync(workspace, mission.MissionId, token);
        _ = await MissionTestData.AddTaskAsync(
            workspace,
            mission,
            "optional",
            setup.Members["optional-worker"].MemberId,
            required: false,
            requiresReview: false,
            dependsOn: [],
            token);
        mission = await MissionTestData.GetMissionAsync(workspace, mission.MissionId, token);
        _ = await workspace.Service.ActivateMissionAsync(
            new MissionCommandRequest(
                MissionTestData.Command(mission.Revision),
                mission.MissionId),
            token);

        var completed = await MissionTestData.ReconcileUntilAsync(
            workspace,
            mission.MissionId,
            candidate => candidate.Status == CoWorkMissionStatus.Completed,
            token);
        await workspace.Service.ReconcilePendingAsync(token);
        workspace.ReplaceService();
        await workspace.Service.ReconcilePendingAsync(token);

        Assert.Equal("mission final summary", completed.FinalSummary);
        Assert.Equal(
            CoWorkTaskStatus.Completed,
            completed.Tasks.Single(task => task.Alias == "required").Status);
        Assert.Equal(
            CoWorkTaskStatus.Failed,
            completed.Tasks.Single(task => task.Alias == "optional").Status);
        Assert.Equal(
            1,
            await MissionTestData.CountAsync(
                workspace.Store,
                """
                SELECT count(*) FROM agent_runs
                WHERE mission_id = $id AND run_kind = 'leaderSynthesis';
                """,
                token,
                ("$id", mission.MissionId)));

        var synthesis = Assert.Single(executor.Inputs[CoWorkAgentRunKind.LeaderSynthesis]);
        Assert.Contains("required task result", synthesis, StringComparison.Ordinal);
        Assert.Contains("optional", synthesis, StringComparison.Ordinal);
        Assert.Contains("failed", synthesis, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("planning-only-history-marker", synthesis, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Synthesis_includes_artifact_metadata_without_reading_artifact_content()
    {
        var executor = new ArtifactMissionExecutor();
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
            "artifact",
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
        await workspace.Service.ReconcilePendingAsync(token);
        await executor.TaskStarted.WaitAsync(token);
        var runId = await workspace.Store.ReadAsync(
            async (connection, cancellationToken) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT agent_run_id FROM agent_runs
                    WHERE mission_id = $missionId AND run_kind = 'missionTask';
                    """;
                var parameter = command.CreateParameter();
                parameter.ParameterName = "$missionId";
                parameter.Value = mission.MissionId.ToString("D");
                command.Parameters.Add(parameter);
                return Guid.Parse((string)(await command.ExecuteScalarAsync(
                    cancellationToken))!);
            },
            token);
        const string artifactBody = "artifact-content-must-not-enter-synthesis";
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Root, "artifact-note.txt"),
            artifactBody,
            token);
        mission = await MissionTestData.GetMissionAsync(workspace, mission.MissionId, token);
        var published = await workspace.Service.PublishArtifactAsync(
            new PublishArtifactRequest(
                MissionTestData.Command(mission.Revision),
                mission.MissionId,
                runId,
                CoWorkFileArea.Workspace,
                "artifact-note.txt",
                "artifact-note",
                "text/plain"),
            token);
        Assert.True(published.IsSuccess);

        executor.ReleaseTask();
        _ = await MissionTestData.ReconcileUntilAsync(
            workspace,
            mission.MissionId,
            candidate => candidate.Status == CoWorkMissionStatus.Completed,
            token);

        Assert.Contains("artifact-note", executor.SynthesisInput, StringComparison.Ordinal);
        Assert.Contains("text/plain", executor.SynthesisInput, StringComparison.Ordinal);
        Assert.Contains("status=available", executor.SynthesisInput, StringComparison.Ordinal);
        Assert.DoesNotContain(artifactBody, executor.SynthesisInput, StringComparison.Ordinal);
    }
}

internal sealed class MissionCompletionExecutor(string? failingTaskAlias = null)
    : ISessionExecutor
{
    private readonly ConcurrentDictionary<CoWorkAgentRunKind, ConcurrentBag<string>>
        _inputs = [];

    public IReadOnlyDictionary<CoWorkAgentRunKind, IReadOnlyList<string>> Inputs =>
        _inputs.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.ToArray());

    public async ValueTask ExecuteAsync(
        AgentSession context,
        ISessionExecutionSink sink,
        CancellationToken cancellationToken)
    {
        var input = context.ModelHistory
            .Where(item => item.Type == SessionItemType.UserMessage)
            .Select(item => ((TextItemContent)item.Content).Text)
            .Last();
        var kind = input.StartsWith("Synthesize Mission ", StringComparison.Ordinal)
            ? CoWorkAgentRunKind.LeaderSynthesis
            : context.Thread.CoWorkProvenance?.RunKind
              ?? throw new InvalidOperationException("Mission provenance is missing.");
        _inputs.GetOrAdd(kind, _ => []).Add(input);
        if (kind == CoWorkAgentRunKind.MissionTask &&
            failingTaskAlias is not null &&
            input.Contains($"Task {failingTaskAlias}:", StringComparison.Ordinal))
        {
            await sink.EmitAsync(
                new FailTurnIntent(
                    new SessionError("optionalFailed", "Optional task failed.", false)),
                cancellationToken);
            return;
        }

        var output = kind switch
        {
            CoWorkAgentRunKind.LeaderPlanning => "planning-only-history-marker",
            CoWorkAgentRunKind.LeaderSynthesis => "mission final summary",
            CoWorkAgentRunKind.MissionTask when input.Contains(
                "Task required:",
                StringComparison.Ordinal) => "required task result",
            _ => "task result",
        };
        var itemId = Guid.CreateVersion7();
        await sink.EmitAsync(
            new StartItemIntent(
                itemId,
                SessionItemType.AgentMessage,
                new TextItemContent(output)),
            cancellationToken);
        await sink.EmitAsync(new CompleteItemIntent(itemId), cancellationToken);
        await sink.EmitAsync(new CompleteTurnIntent(), cancellationToken);
    }
}

internal sealed class ArtifactMissionExecutor : ISessionExecutor
{
    private readonly TaskCompletionSource _release =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task TaskStarted => _started.Task;

    public string SynthesisInput { get; private set; } = string.Empty;

    public async ValueTask ExecuteAsync(
        AgentSession context,
        ISessionExecutionSink sink,
        CancellationToken cancellationToken)
    {
        var input = context.ModelHistory
            .Where(item => item.Type == SessionItemType.UserMessage)
            .Select(item => ((TextItemContent)item.Content).Text)
            .Last();
        if (input.Contains("Task artifact:", StringComparison.Ordinal))
        {
            _started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }

        var output = input.StartsWith("Synthesize Mission ", StringComparison.Ordinal)
            ? "mission final summary"
            : "task result";
        if (input.StartsWith("Synthesize Mission ", StringComparison.Ordinal))
        {
            SynthesisInput = input;
        }

        var itemId = Guid.CreateVersion7();
        await sink.EmitAsync(
            new StartItemIntent(
                itemId,
                SessionItemType.AgentMessage,
                new TextItemContent(output)),
            cancellationToken);
        await sink.EmitAsync(new CompleteItemIntent(itemId), cancellationToken);
        await sink.EmitAsync(new CompleteTurnIntent(), cancellationToken);
    }

    public void ReleaseTask() => _release.TrySetResult();
}
