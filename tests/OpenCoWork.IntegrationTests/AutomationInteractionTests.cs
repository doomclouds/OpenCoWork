using OpenCoWork.Abstractions;
using OpenCoWork.Automations;
using OpenCoWork.Core.Sessions;
using System.Data.Common;
using Xunit;

namespace OpenCoWork.IntegrationTests;

public sealed class AutomationInteractionTests
{
    private static readonly AutomationActorContext Host =
        new(AutomationActorKind.Host, "wire:interaction");

    [Theory]
    [InlineData(
        SessionInteractionType.Approval,
        AutomationAttentionKind.ApprovalRequired,
        AutomationAttentionResolutionKind.Approve,
        null)]
    [InlineData(
        SessionInteractionType.Approval,
        AutomationAttentionKind.ApprovalRequired,
        AutomationAttentionResolutionKind.Reject,
        "No")]
    [InlineData(
        SessionInteractionType.UserInput,
        AutomationAttentionKind.UserInputRequired,
        AutomationAttentionResolutionKind.ProvideInput,
        "Continue")]
    public async Task Host_resolves_waiting_session_in_the_same_turn(
        SessionInteractionType interactionType,
        AutomationAttentionKind attentionKind,
        AutomationAttentionResolutionKind resolutionKind,
        string? text)
    {
        var executor = new WaitingExecutor(interactionType);
        await using var fixture = await AutomationServiceTests.Fixture.CreateAsync(
            executor: executor);
        var runId = await StartAndDispatchAsync(fixture);
        var reconciler = CreateReconciler(fixture);
        var attention = await WaitForAttentionAsync(fixture, reconciler, runId);
        Assert.Equal(AutomationRunStatus.NeedsAttention, attention.Summary.Status);
        Assert.Equal(attentionKind, attention.Summary.AttentionKind);
        Assert.Equal(executor.InteractionId, attention.AttentionId);

        var resolved = await fixture.Service.ResolveAttentionAsync(
            new ResolveAutomationAttentionRequest(
                Host,
                runId,
                attention.AttentionId!.Value,
                new AutomationAttentionResolution(resolutionKind, text),
                Guid.CreateVersion7(),
                attention.Summary.Revision),
            TestContext.Current.CancellationToken);

        Assert.True(
            resolved.IsSuccess,
            $"{resolved.Error?.Code}: {resolved.Error?.Message}");
        await WaitUntilAsync(() => executor.ResumeCount == 1);
        Assert.Equal(executor.FirstTurn!.TurnId, executor.ResumedTurn!.TurnId);
        Assert.Equal(1, executor.ResumeCount);
    }

    [Fact]
    public async Task Wrong_resolution_kind_is_rejected_without_resuming_session()
    {
        var executor = new WaitingExecutor(SessionInteractionType.Approval);
        await using var fixture = await AutomationServiceTests.Fixture.CreateAsync(
            executor: executor);
        var runId = await StartAndDispatchAsync(fixture);
        var reconciler = CreateReconciler(fixture);
        var attention = await WaitForAttentionAsync(fixture, reconciler, runId);

        var rejected = await fixture.Service.ResolveAttentionAsync(
            new ResolveAutomationAttentionRequest(
                Host,
                runId,
                attention.AttentionId!.Value,
                new AutomationAttentionResolution(
                    AutomationAttentionResolutionKind.ProvideInput,
                    "wrong"),
                Guid.CreateVersion7(),
                attention.Summary.Revision),
            TestContext.Current.CancellationToken);

        Assert.Equal(AutomationErrorCodes.InvalidState, rejected.Error!.Code);
        Assert.Equal(0, executor.ResumeCount);
    }

    [Fact]
    public async Task Explicit_cancel_persists_terminal_state_and_cancels_the_active_turn()
    {
        var executor = new WaitingExecutor(SessionInteractionType.UserInput);
        await using var fixture = await AutomationServiceTests.Fixture.CreateAsync(
            executor: executor);
        var runId = await StartAndDispatchAsync(fixture);
        var reconciler = CreateReconciler(fixture);
        var attention = await WaitForAttentionAsync(fixture, reconciler, runId);

        var cancelled = await fixture.Service.CancelRunAsync(
            new CancelAutomationRunRequest(
                Host,
                runId,
                Guid.CreateVersion7(),
                attention.Summary.Revision),
            TestContext.Current.CancellationToken);

        Assert.True(cancelled.IsSuccess, cancelled.Error?.Code);
        Assert.Equal(AutomationRunStatus.Cancelled, cancelled.Value!.Summary.Status);
        await WaitUntilAsync(async () =>
            (await fixture.Sessions.GetThreadAsync(
                attention.ThreadId!.Value,
                TestContext.Current.CancellationToken)).Value!.ActiveTurnId is null);
    }

    [Fact]
    public async Task Cancelled_request_does_not_partially_cancel_the_run()
    {
        var executor = new WaitingExecutor(SessionInteractionType.Approval);
        await using var fixture = await AutomationServiceTests.Fixture.CreateAsync(
            executor: executor);
        var runId = await StartAndDispatchAsync(fixture);
        var attention = await WaitForAttentionAsync(
            fixture,
            CreateReconciler(fixture),
            runId);
        using var request = new CancellationTokenSource();
        request.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Service.CancelRunAsync(
                new CancelAutomationRunRequest(
                    Host,
                    runId,
                    Guid.CreateVersion7(),
                    attention.Summary.Revision),
                request.Token));

        Assert.Equal(
            AutomationRunStatus.NeedsAttention,
            (await GetRunAsync(fixture, runId)).Summary.Status);
    }

    [Fact]
    public async Task Attention_deadline_times_out_and_cancels_the_active_turn()
    {
        var executor = new WaitingExecutor(SessionInteractionType.Approval);
        await using var fixture = await AutomationServiceTests.Fixture.CreateAsync(
            executor: executor);
        var runId = await StartAndDispatchAsync(fixture);
        var reconciler = CreateReconciler(fixture);
        var attention = await WaitForAttentionAsync(fixture, reconciler, runId);
        await SetDeadlineAsync(
            fixture,
            runId,
            "attention_deadline_utc",
            DateTimeOffset.UtcNow.AddMinutes(-1));

        await reconciler.ReconcileOnceAsync(
            "attention-timeout",
            TestContext.Current.CancellationToken);

        var timedOut = await GetRunAsync(fixture, runId);
        Assert.Equal(AutomationRunStatus.TimedOut, timedOut.Summary.Status);
        await WaitUntilAsync(async () =>
            (await fixture.Sessions.GetThreadAsync(
                attention.ThreadId!.Value,
                TestContext.Current.CancellationToken)).Value!.ActiveTurnId is null);
    }

    [Fact]
    public async Task Run_deadline_times_out_and_cancels_the_active_turn()
    {
        var executor = new WaitingExecutor(SessionInteractionType.UserInput);
        await using var fixture = await AutomationServiceTests.Fixture.CreateAsync(
            executor: executor);
        var runId = await StartAndDispatchAsync(fixture);
        await SetDeadlineAsync(
            fixture,
            runId,
            "run_deadline_utc",
            DateTimeOffset.UtcNow.AddMinutes(-1));
        var reconciler = CreateReconciler(fixture);

        await reconciler.ReconcileOnceAsync(
            "run-timeout",
            TestContext.Current.CancellationToken);

        var timedOut = await GetRunAsync(fixture, runId);
        Assert.Equal(AutomationRunStatus.TimedOut, timedOut.Summary.Status);
        await WaitUntilAsync(async () =>
            (await fixture.Sessions.GetThreadAsync(
                timedOut.ThreadId!.Value,
                TestContext.Current.CancellationToken)).Value!.ActiveTurnId is null);
    }

    [Fact]
    public async Task Lost_project_writer_lease_fails_the_run_and_cancels_the_turn()
    {
        var executor = new WaitingExecutor(SessionInteractionType.Approval);
        await using var fixture = await AutomationServiceTests.Fixture.CreateAsync(
            executor: executor);
        var runId = await StartAndDispatchAsync(fixture);
        var attention = await WaitForAttentionAsync(
            fixture,
            CreateReconciler(fixture),
            runId);
        await SetDeadlineAsync(
            fixture,
            runId,
            "project_writer_lease_expires_utc",
            DateTimeOffset.UtcNow.AddMinutes(-1));
        var reconciler = CreateReconciler(
            fixture,
            writerLeases: new LostWriterLeases());

        await reconciler.ReconcileOnceAsync(
            "lease-lost",
            TestContext.Current.CancellationToken);

        var failed = await GetRunAsync(fixture, runId);
        Assert.Equal(AutomationRunStatus.Failed, failed.Summary.Status);
        Assert.Equal(AutomationErrorCodes.LeaseLost, failed.Error!.Code);
        await WaitUntilAsync(async () =>
            (await fixture.Sessions.GetThreadAsync(
                attention.ThreadId!.Value,
                TestContext.Current.CancellationToken)).Value!.ActiveTurnId is null);
    }

    [Fact]
    public async Task Outcome_unknown_overrides_session_failure_and_cannot_resume_the_turn()
    {
        var executor = new WaitingExecutor(SessionInteractionType.Approval);
        await using var fixture = await AutomationServiceTests.Fixture.CreateAsync(
            executor: executor);
        var runId = await StartAndDispatchAsync(fixture);
        var reconciler = CreateReconciler(fixture);
        var attention = await WaitForAttentionAsync(fixture, reconciler, runId);
        await SeedOutcomeUnknownAsync(fixture, attention);

        await reconciler.ReconcileOnceAsync(
            "outcome-unknown",
            TestContext.Current.CancellationToken);

        var unknown = await GetRunAsync(fixture, runId);
        Assert.Equal(AutomationRunStatus.NeedsAttention, unknown.Summary.Status);
        Assert.Equal(
            AutomationAttentionKind.OutcomeUnknown,
            unknown.Summary.AttentionKind);
        Assert.NotNull(unknown.AttentionId);
        var invalidResume = await fixture.Service.ResolveAttentionAsync(
            new ResolveAutomationAttentionRequest(
                Host,
                runId,
                unknown.AttentionId!.Value,
                new AutomationAttentionResolution(
                    AutomationAttentionResolutionKind.Approve),
                Guid.CreateVersion7(),
                unknown.Summary.Revision),
            TestContext.Current.CancellationToken);
        Assert.Equal(AutomationErrorCodes.InvalidState, invalidResume.Error!.Code);

        var failed = await fixture.Service.ResolveAttentionAsync(
            new ResolveAutomationAttentionRequest(
                Host,
                runId,
                unknown.AttentionId.Value,
                new AutomationAttentionResolution(
                    AutomationAttentionResolutionKind.Fail),
                Guid.CreateVersion7(),
                unknown.Summary.Revision),
            TestContext.Current.CancellationToken);
        Assert.True(failed.IsSuccess, failed.Error?.Code);
        Assert.Equal(AutomationRunStatus.Failed, failed.Value!.Summary.Status);
        Assert.Equal(0, executor.ResumeCount);
    }

    [Fact]
    public async Task Terminal_session_is_archived_by_a_durable_retention_intent()
    {
        await using var fixture = await AutomationServiceTests.Fixture.CreateAsync(
            executor: new CompletionExecutor());
        var runId = await StartAndDispatchAsync(fixture);
        var dispatcher = fixture.CreateDispatcher();
        var reconciler = CreateReconciler(fixture, dispatcher);

        for (var attempt = 0; attempt < 100; attempt++)
        {
            await reconciler.ReconcileOnceAsync(
                "retention",
                TestContext.Current.CancellationToken);
            var run = await GetRunAsync(fixture, runId);
            if (run.Summary.Status == AutomationRunStatus.Completed)
            {
                var thread = await fixture.Sessions.GetThreadAsync(
                    run.ThreadId!.Value,
                    TestContext.Current.CancellationToken);
                if (thread.Value!.Status == ThreadStatus.Archived)
                {
                    Assert.Equal(
                        1,
                        await CountCompletedIntentAsync(
                            fixture,
                            runId,
                            "archiveThread"));
                    return;
                }
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        throw new Xunit.Sdk.XunitException(
            "Terminal Automation Thread was not archived.");
    }

    private static async Task<Guid> StartAndDispatchAsync(
        AutomationServiceTests.Fixture fixture)
    {
        await fixture.WriteAsync(
            "interactive",
            enabled: true,
            scheduled: false,
            workspaceMode: AutomationWorkspaceMode.Project);
        await fixture.ScanAsync();
        var definition = await fixture.Service.GetDefinitionAsync(
            new GetAutomationDefinitionRequest(Host, "interactive"),
            TestContext.Current.CancellationToken);
        using var inputs = System.Text.Json.JsonDocument.Parse("{}");
        var started = await fixture.Service.StartRunAsync(
            new StartAutomationRunRequest(
                Host,
                "interactive",
                inputs.RootElement.Clone(),
                Guid.CreateVersion7(),
                definition.Value!.Summary.Revision),
            TestContext.Current.CancellationToken);
        Assert.True(started.IsSuccess, started.Error?.Code);
        var dispatcher = fixture.CreateDispatcher();
        Assert.True(await dispatcher.DispatchNextAsync(
            started.Value!.Summary.RunId,
            "interaction",
            TestContext.Current.CancellationToken));
        Assert.True(await dispatcher.DispatchNextAsync(
            started.Value.Summary.RunId,
            "interaction",
            TestContext.Current.CancellationToken));
        return started.Value.Summary.RunId;
    }

    private static AutomationReconciler CreateReconciler(
        AutomationServiceTests.Fixture fixture,
        AutomationDispatcher? dispatcher = null,
        IProjectWriterLeaseService? writerLeases = null) =>
        new(
            fixture.Workspace.Store,
            fixture.Service,
            fixture.Config,
            TimeProvider.System,
            dispatcher is null
                ? static (_, _, _) => Task.FromResult(false)
                : dispatcher.DispatchNextAsync,
            writerLeases ?? fixture.WriterLeases,
            sessions: fixture.Sessions);

    private static async Task<AutomationRunSnapshot> GetRunAsync(
        AutomationServiceTests.Fixture fixture,
        Guid runId) =>
        (await fixture.Service.GetRunAsync(
            new GetAutomationRunRequest(Host, runId),
            TestContext.Current.CancellationToken)).Value!;

    private static async Task<AutomationRunSnapshot> WaitForAttentionAsync(
        AutomationServiceTests.Fixture fixture,
        AutomationReconciler reconciler,
        Guid runId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await reconciler.ReconcileOnceAsync(
                "wait-attention",
                TestContext.Current.CancellationToken);
            var run = await GetRunAsync(fixture, runId);
            if (run.Summary.Status == AutomationRunStatus.NeedsAttention)
            {
                return run;
            }

            Assert.DoesNotContain(
                run.Summary.Status,
                new[]
                {
                    AutomationRunStatus.Failed,
                    AutomationRunStatus.Cancelled,
                    AutomationRunStatus.TimedOut,
                });
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        throw new Xunit.Sdk.XunitException("Automation Run did not need attention.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 100 && !await condition(); attempt++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(await condition());
    }

    private static Task<long> CountCompletedIntentAsync(
        AutomationServiceTests.Fixture fixture,
        Guid runId,
        string kind) =>
        fixture.Workspace.Store.ReadAsync(
            async (connection, cancellationToken) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT count(*)
                    FROM automation_dispatch_intents
                    WHERE entity_id = $runId
                      AND dispatch_kind = $kind
                      AND status = 'completed';
                    """;
                Add(command, "$runId", runId.ToString("D"));
                Add(command, "$kind", kind);
                return Convert.ToInt64(
                    await command.ExecuteScalarAsync(cancellationToken));
            },
            TestContext.Current.CancellationToken).AsTask();

    private static Task SetDeadlineAsync(
        AutomationServiceTests.Fixture fixture,
        Guid runId,
        string column,
        DateTimeOffset deadline) =>
        fixture.Workspace.Store.WriteAsync(
            async (connection, transaction, cancellationToken) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    $"""
                     UPDATE automation_runs
                     SET {column} = $deadline
                     WHERE automation_run_id = $runId;
                     """;
                Add(command, "$deadline", deadline.ToUnixTimeMilliseconds());
                Add(command, "$runId", runId.ToString("D"));
                await command.ExecuteNonQueryAsync(cancellationToken);
                return 0;
            },
            TestContext.Current.CancellationToken).AsTask();

    private static Task SeedOutcomeUnknownAsync(
        AutomationServiceTests.Fixture fixture,
        AutomationRunSnapshot run) =>
        fixture.Workspace.Store.WriteAsync(
            async (connection, transaction, cancellationToken) =>
            {
                var thread = await fixture.Sessions.GetThreadAsync(
                    run.ThreadId!.Value,
                    cancellationToken);
                var turnId = thread.Value!.ActiveTurnId!.Value;
                var invocationId = Guid.CreateVersion7();
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO tool_invocations (
                        tool_invocation_id, thread_id, turn_id,
                        provider_tool_call_id, provider_tool_name,
                        tool_definition_id, runtime_binding_id,
                        snapshot_sha256, arguments_sha256, status,
                        attempt_count, result_item_id, error_code,
                        started_at, updated_at, completed_at)
                    VALUES (
                        $invocationId, $threadId, $turnId,
                        'call-unknown', 'unsafe.tool',
                        NULL, NULL,
                        $sha, $sha, 'outcomeUnknown',
                        1, NULL, $errorCode,
                        $now, $now, $now);
                    UPDATE turns
                    SET status = 'failed',
                        error_code = $errorCode,
                        error_message = 'Unsafe Tool outcome is unknown.',
                        updated_utc = $now,
                        completed_utc = $now
                    WHERE turn_id = $turnId;
                    UPDATE threads
                    SET active_turn_id = NULL
                    WHERE thread_id = $threadId;
                    """;
                Add(command, "$invocationId", invocationId.ToString("D"));
                Add(command, "$threadId", run.ThreadId.Value.ToString("D"));
                Add(command, "$turnId", turnId.ToString("D"));
                Add(command, "$sha", new string('a', 64));
                Add(command, "$errorCode", ToolErrorCodes.OutcomeUnknown);
                Add(command, "$now", now);
                await command.ExecuteNonQueryAsync(cancellationToken);
                return 0;
            },
            TestContext.Current.CancellationToken).AsTask();

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed class WaitingExecutor(SessionInteractionType type) : ISessionExecutor
    {
        public Guid? InteractionId { get; private set; }

        public TurnSnapshot? FirstTurn { get; private set; }

        public TurnSnapshot? ResumedTurn { get; private set; }

        public int ResumeCount { get; private set; }

        public async ValueTask ExecuteAsync(
            AgentSession context,
            ISessionExecutionSink sink,
            CancellationToken cancellationToken)
        {
            if (context.Checkpoint is null)
            {
                FirstTurn = context.Turn;
                InteractionId = Guid.CreateVersion7();
                await sink.EmitAsync(
                    new WaitForInteractionIntent(
                        InteractionId.Value,
                        type,
                        type == SessionInteractionType.Approval
                            ? new ApprovalRequestContent("Approve?")
                            : new UserInputRequestContent("Input?"),
                        SessionExecutionCheckpointCodec.Create(
                            GetType().FullName!,
                            1,
                            "{}"),
                        DateTimeOffset.UtcNow.AddMinutes(5)),
                    cancellationToken);
                return;
            }

            ResumedTurn = context.Turn;
            ResumeCount++;
            await sink.EmitAsync(new CompleteTurnIntent(), cancellationToken);
        }
    }

    private sealed class CompletionExecutor : ISessionExecutor
    {
        public ValueTask ExecuteAsync(
            AgentSession context,
            ISessionExecutionSink sink,
            CancellationToken cancellationToken) =>
            sink.EmitAsync(new CompleteTurnIntent(), cancellationToken);
    }

    private sealed class LostWriterLeases : IProjectWriterLeaseService
    {
        public ValueTask<ProjectWriterLease?> TryAcquireAsync(
            ProjectWriterLeaseOwner owner,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ProjectWriterLease?>(null);

        public ValueTask<ProjectWriterLease?> RenewAsync(
            ProjectWriterLeaseOwner owner,
            Guid leaseId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ProjectWriterLease?>(null);

        public ValueTask<bool> ReleaseAsync(
            ProjectWriterLeaseOwner owner,
            Guid leaseId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(false);
    }
}
