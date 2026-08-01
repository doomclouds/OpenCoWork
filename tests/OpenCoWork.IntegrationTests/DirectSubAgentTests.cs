using OpenCoWork.Abstractions;
using OpenCoWork.Teams;
using Xunit;

namespace OpenCoWork.IntegrationTests;

public sealed class DirectSubAgentTests
{
    [Fact]
    public async Task CoWorkCorrelation_spawn_list_message_followup_and_cancel_are_persistent()
    {
        await using var workspace = await CoWorkTestWorkspace.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = await CreateProfileAsync(workspace, "direct", cancellationToken);

        var correlationId = Guid.CreateVersion7();
        var spawned = await workspace.Service.SpawnSubAgentAsync(
            new SpawnSubAgentRequest(
                Command() with { CorrelationId = correlationId },
                workspace.OriginThreadId,
                profile.ProfileId,
                "Inspect the runtime.",
                8_000,
                CoWorkWorkspaceMode.Project),
            cancellationToken);

        Assert.True(spawned.IsSuccess);
        var firstRun = spawned.Value!;
        Assert.NotEqual(Guid.Empty, firstRun.ThreadId);
        Assert.Equal(CoWorkAgentRunKind.Direct, firstRun.Kind);
        Assert.Equal(correlationId, firstRun.CorrelationId);

        var children = await workspace.Service.ListSubAgentChildrenAsync(
            new SubAgentQueryRequest(
                CoWorkTestWorkspace.Host,
                workspace.OriginThreadId),
            cancellationToken);
        var child = Assert.Single(children.Value!.Items);
        Assert.Equal(firstRun.ThreadId, child.ChildThreadId);
        Assert.Equal(workspace.OriginThreadId, child.ParentThreadId);

        var message = await workspace.Service.SendSubAgentMessageAsync(
            new SendSubAgentMessageRequest(
                Command(),
                child.ChildThreadId,
                "Include recovery evidence."),
            cancellationToken);
        Assert.True(message.IsSuccess);
        Assert.Equal(CoWorkMailboxScope.Direct, message.Value!.Scope);

        var followUp = await workspace.Service.FollowUpSubAgentAsync(
            new FollowUpSubAgentRequest(
                Command(),
                child.ChildThreadId,
                "Now summarize the findings."),
            cancellationToken);
        Assert.True(followUp.IsSuccess, followUp.Error?.ToString());
        Assert.Equal(child.ChildThreadId, followUp.Value!.ThreadId);
        Assert.NotEqual(firstRun.AgentRunId, followUp.Value.AgentRunId);
        Assert.Equal(firstRun.AgentRunId, followUp.Value.PreviousRunId);
        Assert.Equivalent(firstRun.Profile, followUp.Value.Profile, strict: true);
        Assert.Equal(firstRun.ExecutionWorkspace, followUp.Value.ExecutionWorkspace);
        Assert.Equal(firstRun.BudgetScopeId, followUp.Value.BudgetScopeId);

        var nested = await workspace.Service.SpawnSubAgentAsync(
            new SpawnSubAgentRequest(
                new CoWorkCommandContext(
                    Guid.CreateVersion7(),
                    new CoWorkActorContext(
                        CoWorkActorKind.DirectParent,
                        "direct-parent",
                        ThreadId: child.ChildThreadId),
                    ExpectedRevision: null),
                child.ChildThreadId,
                profile.ProfileId,
                "Check one nested detail.",
                1_000,
                CoWorkWorkspaceMode.Project),
            cancellationToken);
        Assert.Equal(CoWorkErrorCodes.DepthExceeded, nested.Error?.Code);

        var cancelled = await workspace.Service.CancelSubAgentAsync(
            new CancelSubAgentRequest(Command(), child.ChildThreadId),
            cancellationToken);
        Assert.True(cancelled.IsSuccess);
        Assert.Null(cancelled.Value!.ActiveRun);

        var all = await workspace.Service.ListSubAgentsAsync(
            new SubAgentQueryRequest(
                CoWorkTestWorkspace.Host,
                workspace.OriginThreadId),
            cancellationToken);
        Assert.Single(all.Value!.Items);
    }

    [Fact]
    public async Task Active_agent_run_prevents_child_thread_deletion()
    {
        await using var workspace = await CoWorkTestWorkspace.CreateAsync(
            completeAgentRuns: false);
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = await CreateProfileAsync(workspace, "delete-guard", cancellationToken);
        var run = (await workspace.Service.SpawnSubAgentAsync(
            new SpawnSubAgentRequest(
                Command(),
                workspace.OriginThreadId,
                profile.ProfileId,
                "Stay active.",
                4_000,
                CoWorkWorkspaceMode.Project),
            cancellationToken)).Value!;
        var thread = (await workspace.Sessions.GetThreadAsync(
            run.ThreadId,
            cancellationToken)).Value!;
        var queued = Assert.Single(thread.Queue);
        thread = (await workspace.Sessions.RemoveQueuedInputAsync(
            new RemoveQueuedInputRequest(
                run.ThreadId,
                queued.QueueItemId,
                Guid.CreateVersion7(),
                thread.CurrentSequence),
            cancellationToken)).Value!;
        thread = (await workspace.Sessions.ArchiveThreadAsync(
            new ThreadMutationRequest(
                run.ThreadId,
                Guid.CreateVersion7(),
                thread.CurrentSequence),
            cancellationToken)).Value!;

        var prepared = await workspace.Sessions.PrepareDeleteAsync(
            new PrepareDeleteRequest(run.ThreadId, thread.CurrentSequence),
            cancellationToken);

        Assert.Equal(SessionErrorCodes.InvalidState, prepared.Error?.Code);
    }

    [Fact]
    public async Task Cancelling_parent_recursively_cancels_persistent_descendants()
    {
        await using var workspace = await CoWorkTestWorkspace.CreateAsync(
            new CoWorkConfig { MaxDepth = 2 },
            completeAgentRuns: false);
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = await CreateProfileAsync(workspace, "recursive", cancellationToken);
        var parent = (await workspace.Service.SpawnSubAgentAsync(
            new SpawnSubAgentRequest(
                Command(),
                workspace.OriginThreadId,
                profile.ProfileId,
                "Parent.",
                8_000,
                CoWorkWorkspaceMode.Project),
            cancellationToken)).Value!;
        var child = await workspace.Service.SpawnSubAgentAsync(
            new SpawnSubAgentRequest(
                new CoWorkCommandContext(
                    Guid.CreateVersion7(),
                    new CoWorkActorContext(
                        CoWorkActorKind.DirectParent,
                        "parent",
                        ThreadId: parent.ThreadId),
                    ExpectedRevision: null),
                parent.ThreadId,
                profile.ProfileId,
                "Child.",
                2_000,
                CoWorkWorkspaceMode.Project),
            cancellationToken);
        Assert.True(child.IsSuccess, child.Error?.ToString());

        var cancelled = await workspace.Service.CancelSubAgentAsync(
            new CancelSubAgentRequest(Command(), parent.ThreadId),
            cancellationToken);
        var lineage = await workspace.Service.ListSubAgentsAsync(
            new SubAgentQueryRequest(
                CoWorkTestWorkspace.Host,
                workspace.OriginThreadId),
            cancellationToken);

        Assert.True(cancelled.IsSuccess, cancelled.Error?.ToString());
        Assert.Equal(2, lineage.Value!.Items.Count);
        Assert.All(lineage.Value.Items, item => Assert.Null(item.ActiveRun));
    }

    private static async Task<AgentProfileSnapshot> CreateProfileAsync(
        CoWorkTestWorkspace workspace,
        string name,
        CancellationToken cancellationToken)
    {
        var result = await workspace.Service.UpsertAgentProfileAsync(
            new UpsertAgentProfileRequest(
                Command(),
                null,
                name,
                "",
                "Be concise.",
                "fake",
                "fake-model",
                [],
                []),
            cancellationToken);
        return result.Value!;
    }

    private static CoWorkCommandContext Command() =>
        new(Guid.CreateVersion7(), CoWorkTestWorkspace.Host, ExpectedRevision: null);
}
