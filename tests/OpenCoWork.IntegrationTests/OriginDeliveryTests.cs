using OpenCoWork.Abstractions;
using Xunit;

namespace OpenCoWork.IntegrationTests;

public sealed class OriginDeliveryTests
{
    [Fact]
    public async Task Archived_origin_is_reactivated_and_duplicate_reconcile_appends_once()
    {
        await using var workspace = await CoWorkTestWorkspace.CreateAsync(
            executor: new MissionCompletionExecutor());
        var token = TestContext.Current.CancellationToken;
        await using (var subscription = await workspace.Sessions.SubscribeAsync(
                         new SessionSubscriptionRequest(
                             workspace.OriginThreadId,
                             SessionSubscriptionMode.SnapshotThenLive),
                         token))
        {
        }
        var origin = (await workspace.Sessions.GetThreadAsync(
            workspace.OriginThreadId,
            token)).Value!;
        var archived = await workspace.Sessions.ArchiveThreadAsync(
            new ThreadMutationRequest(
                origin.ThreadId,
                Guid.CreateVersion7(),
                origin.CurrentSequence),
            token);
        Assert.Equal(SessionCommandStatus.Committed, archived.Status);
        var mission = await CreateSingleTaskMissionAsync(workspace, token);

        var completed = await MissionTestData.ReconcileUntilAsync(
            workspace,
            mission.MissionId,
            candidate => candidate.Status == CoWorkMissionStatus.Completed,
            token);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await workspace.Service.ReconcilePendingAsync(token);
            if (await MissionTestData.CountAsync(
                    workspace.Store,
                    """
                    SELECT count(*) FROM missions
                    WHERE mission_id = $id AND origin_delivered_utc IS NOT NULL;
                    """,
                    token,
                    ("$id", mission.MissionId)) == 1)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), token);
        }
        workspace.ReplaceService();
        await workspace.Service.ReconcilePendingAsync(token);

        var originIntent = await workspace.Store.ReadAsync(
            async (connection, cancellationToken) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT status || ':' || coalesce(error_code, '') || ':' ||
                           coalesce(diagnostic, '')
                    FROM cowork_dispatch_intents
                    WHERE dispatch_kind = 'deliverOrigin'
                    ORDER BY created_utc DESC
                    LIMIT 1;
                    """;
                return Convert.ToString(
                    await command.ExecuteScalarAsync(cancellationToken)) ?? "missing";
            },
            token);
        var currentOrigin = (await workspace.Sessions.GetThreadAsync(
            workspace.OriginThreadId,
            token)).Value!;
        Assert.True(
            currentOrigin.Status == ThreadStatus.Active,
            $"Origin status: {currentOrigin.Status}; intent: {originIntent}");
        Assert.NotNull(completed.OriginDeliveryId);
        Assert.True(
            await MissionTestData.CountAsync(
                workspace.Store,
                """
                SELECT count(*) FROM missions
                WHERE mission_id = $id AND origin_delivered_utc IS NOT NULL;
                """,
                token,
                ("$id", mission.MissionId)) == 1,
            $"Intent: {originIntent}");
        Assert.Equal(
            1,
            await CountOriginSummaryAsync(workspace, token));
    }

    [Fact]
    public async Task Busy_origin_defers_without_consuming_attempt_and_delivers_after_turn()
    {
        var executor = new BusyOriginExecutor();
        await using var workspace = await CoWorkTestWorkspace.CreateAsync(executor: executor);
        var token = TestContext.Current.CancellationToken;
        var origin = (await workspace.Sessions.GetThreadAsync(
            workspace.OriginThreadId,
            token)).Value!;
        _ = await workspace.Sessions.EnqueueInputAsync(
            new EnqueueInputRequest(
                origin.ThreadId,
                Guid.CreateVersion7(),
                origin.CurrentSequence,
                "hold origin"),
            token);
        await executor.OriginStarted.WaitAsync(token);
        var mission = await CreateSingleTaskMissionAsync(workspace, token);

        _ = await MissionTestData.ReconcileUntilAsync(
            workspace,
            mission.MissionId,
            candidate => candidate.Status == CoWorkMissionStatus.Completed,
            token);
        await workspace.Service.ReconcilePendingAsync(token);
        Assert.Equal(
            0,
            await MissionTestData.CountAsync(
                workspace.Store,
                """
                SELECT attempt_count FROM cowork_dispatch_intents
                WHERE dispatch_kind = 'deliverOrigin';
                """,
                token));

        executor.ReleaseOrigin();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var current = (await workspace.Sessions.GetThreadAsync(
                workspace.OriginThreadId,
                token)).Value!;
            if (current.ActiveTurnId is null)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), token);
        }

        for (var attempt = 0; attempt < 100; attempt++)
        {
            await workspace.Service.ReconcilePendingAsync(token);
            if (await CountOriginSummaryAsync(workspace, token) == 1)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), token);
        }

        Assert.Equal(1, await CountOriginSummaryAsync(workspace, token));
    }

    private static async Task<MissionSnapshot> CreateSingleTaskMissionAsync(
        CoWorkTestWorkspace workspace,
        CancellationToken token)
    {
        var setup = await MissionTestData.CreateAsync(
            workspace,
            CoWorkWorkspaceMode.Project,
            20_000,
            ("leader", CoWorkMemberRole.Leader, Array.Empty<string>()),
            ("worker", CoWorkMemberRole.Member, Array.Empty<string>()));
        _ = await MissionTestData.AddTaskAsync(
            workspace,
            setup.Mission,
            "required",
            setup.Members["worker"].MemberId,
            required: true,
            requiresReview: false,
            dependsOn: [],
            token);
        var mission = await MissionTestData.GetMissionAsync(
            workspace,
            setup.Mission.MissionId,
            token);
        return (await workspace.Service.ActivateMissionAsync(
            new MissionCommandRequest(
                MissionTestData.Command(mission.Revision),
                mission.MissionId),
            token)).Value!;
    }

    private static async ValueTask<long> CountOriginSummaryAsync(
        CoWorkTestWorkspace workspace,
        CancellationToken token)
    {
        var history = await workspace.Sessions.ReadHistoryAsync(
            new ReadHistoryRequest(workspace.OriginThreadId, PageSize: 100),
            token);
        return history.Value!.Items.LongCount(item =>
            item.Type == SessionEventType.ItemCompleted &&
            item.Payload.Item is
            {
                Type: SessionItemType.AgentMessage,
                Content: TextItemContent text,
            } &&
            text.Text.Contains("mission final summary", StringComparison.Ordinal));
    }
}

internal sealed class BusyOriginExecutor : ISessionExecutor
{
    private readonly TaskCompletionSource _release =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly MissionCompletionExecutor _missions = new();

    public Task OriginStarted => _started.Task;

    public async ValueTask ExecuteAsync(
        AgentSession context,
        ISessionExecutionSink sink,
        CancellationToken cancellationToken)
    {
        if (context.Thread.CoWorkProvenance is not null)
        {
            await _missions.ExecuteAsync(context, sink, cancellationToken);
            return;
        }

        _started.TrySetResult();
        await _release.Task.WaitAsync(cancellationToken);
        await sink.EmitAsync(new CompleteTurnIntent(), cancellationToken);
    }

    public void ReleaseOrigin() => _release.TrySetResult();
}
