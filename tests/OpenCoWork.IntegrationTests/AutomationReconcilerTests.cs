using System.Data.Common;
using System.Text.Json;
using OpenCoWork.Abstractions;
using OpenCoWork.Automations;
using Xunit;

namespace OpenCoWork.IntegrationTests;

public sealed class AutomationReconcilerTests
{
    private static readonly AutomationActorContext Host =
        new(AutomationActorKind.Host, "wire:reconciler");

    [Fact]
    public async Task Concurrent_reconcilers_create_one_cron_run_and_advance_schedule()
    {
        await using var fixture = await AutomationServiceTests.Fixture.CreateAsync();
        await fixture.WriteAsync("scheduled", enabled: true, scheduled: true);
        await fixture.ScanAsync();
        var due = DateTimeOffset.UtcNow.AddMinutes(-1);
        await SetScheduleDueAsync(fixture, "scheduled", due);
        var first = CreateReconciler(fixture);
        var second = CreateReconciler(fixture);

        await Task.WhenAll(
            first.ReconcileOnceAsync("first", TestContext.Current.CancellationToken),
            second.ReconcileOnceAsync("second", TestContext.Current.CancellationToken));

        var state = await ReadScheduleStateAsync(fixture, "scheduled");
        Assert.Equal(1, state.RunCount);
        Assert.Equal("cron", state.TriggerKind);
        Assert.Equal(due.ToUnixTimeMilliseconds(), state.ScheduledOccurrenceUtc);
        Assert.Equal(due.ToUnixTimeMilliseconds(), state.LastOccurrenceUtc);
        Assert.True(state.NextOccurrenceUtc > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Due_schedule_coalesces_while_the_automation_has_an_active_run()
    {
        await using var fixture = await AutomationServiceTests.Fixture.CreateAsync();
        await fixture.WriteAsync("scheduled", enabled: true, scheduled: true);
        await fixture.ScanAsync();
        var definition = await fixture.Service.GetDefinitionAsync(
            new GetAutomationDefinitionRequest(Host, "scheduled"),
            TestContext.Current.CancellationToken);
        using var inputs = JsonDocument.Parse("{}");
        var manual = await fixture.Service.StartRunAsync(
            new StartAutomationRunRequest(
                Host,
                "scheduled",
                inputs.RootElement.Clone(),
                Guid.CreateVersion7(),
                definition.Value!.Summary.Revision),
            TestContext.Current.CancellationToken);
        Assert.True(manual.IsSuccess, manual.Error?.Code);
        var due = DateTimeOffset.UtcNow.AddMinutes(-1);
        await SetScheduleDueAsync(fixture, "scheduled", due);

        await CreateReconciler(fixture).ReconcileOnceAsync(
            "coalesce",
            TestContext.Current.CancellationToken);

        var state = await ReadScheduleStateAsync(fixture, "scheduled");
        Assert.Equal(1, state.RunCount);
        Assert.Equal(due.ToUnixTimeMilliseconds(), state.CoalescedOccurrenceUtc);
        Assert.True(state.NextOccurrenceUtc > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Needs_attention_releases_a_global_slot_but_keeps_single_automation_mutex()
    {
        const int limit = 3;
        await using var fixture = await AutomationServiceTests.Fixture.CreateAsync(
            maxConcurrentRuns: limit);
        foreach (var id in new[] { "one", "two", "three", "four" })
        {
            await fixture.WriteAsync(id, enabled: true, scheduled: false);
        }

        await fixture.ScanAsync();
        var runs = new List<Guid>();
        foreach (var id in new[] { "one", "two", "three" })
        {
            var started = await StartAsync(fixture, id);
            Assert.True(started.IsSuccess, started.Error?.Code);
            runs.Add(started.Value!.Summary.RunId);
        }

        await SetRunStatusAsync(fixture, runs[0], "needsAttention");
        var replacement = await StartAsync(fixture, "four");
        var sameAutomation = await StartAsync(fixture, "one");

        Assert.True(replacement.IsSuccess, replacement.Error?.Code);
        Assert.Equal(AutomationErrorCodes.RunConflict, sameAutomation.Error!.Code);
    }

    [Theory]
    [InlineData("completed", AutomationRunStatus.Completed, null)]
    [InlineData(
        "waitingApproval",
        AutomationRunStatus.NeedsAttention,
        AutomationAttentionKind.ApprovalRequired)]
    [InlineData(
        "waitingInput",
        AutomationRunStatus.NeedsAttention,
        AutomationAttentionKind.UserInputRequired)]
    public async Task Reconcile_recovers_run_state_from_persisted_session_facts(
        string turnStatus,
        AutomationRunStatus expectedStatus,
        AutomationAttentionKind? expectedAttention)
    {
        await using var fixture = await AutomationServiceTests.Fixture.CreateAsync();
        await fixture.WriteAsync("recover", enabled: true, scheduled: false);
        await fixture.ScanAsync();
        var started = await StartAsync(fixture, "recover");
        Assert.True(started.IsSuccess, started.Error?.Code);
        await AttachTurnAsync(
            fixture,
            started.Value!.Summary.RunId,
            turnStatus);
        var reconciler = CreateReconciler(fixture);

        await reconciler.ReconcileOnceAsync(
            "recover",
            TestContext.Current.CancellationToken);

        var recovered = await fixture.Service.GetRunAsync(
            new GetAutomationRunRequest(Host, started.Value.Summary.RunId),
            TestContext.Current.CancellationToken);
        Assert.Equal(expectedStatus, recovered.Value!.Summary.Status);
        Assert.Equal(expectedAttention, recovered.Value.Summary.AttentionKind);
    }

    private static AutomationReconciler CreateReconciler(
        AutomationServiceTests.Fixture fixture) =>
        new(
            fixture.Workspace.Store,
            fixture.Service,
            fixture.Config,
            TimeProvider.System,
            static (_, _, _) => Task.FromResult(false));

    private static async Task<AutomationResult<AutomationRunSnapshot>> StartAsync(
        AutomationServiceTests.Fixture fixture,
        string id)
    {
        var definition = await fixture.Service.GetDefinitionAsync(
            new GetAutomationDefinitionRequest(Host, id),
            TestContext.Current.CancellationToken);
        using var inputs = JsonDocument.Parse("{}");
        return await fixture.Service.StartRunAsync(
            new StartAutomationRunRequest(
                Host,
                id,
                inputs.RootElement.Clone(),
                Guid.CreateVersion7(),
                definition.Value!.Summary.Revision),
            TestContext.Current.CancellationToken);
    }

    private static Task SetScheduleDueAsync(
        AutomationServiceTests.Fixture fixture,
        string id,
        DateTimeOffset due) =>
        fixture.Workspace.Store.WriteAsync(
            async (connection, transaction, cancellationToken) =>
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    UPDATE automation_schedules
                    SET next_occurrence_utc = $due,
                        last_occurrence_utc = NULL,
                        coalesced_occurrence_utc = NULL
                    WHERE automation_id = $id;
                    """,
                    cancellationToken,
                    ("$due", due.ToUnixTimeMilliseconds()),
                    ("$id", id));
                return 0;
            },
            TestContext.Current.CancellationToken).AsTask();

    private static Task SetRunStatusAsync(
        AutomationServiceTests.Fixture fixture,
        Guid runId,
        string status) =>
        fixture.Workspace.Store.WriteAsync(
            async (connection, transaction, cancellationToken) =>
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    UPDATE automation_runs
                    SET status = $status
                    WHERE automation_run_id = $runId;
                    """,
                    cancellationToken,
                    ("$status", status),
                    ("$runId", runId.ToString("D")));
                return 0;
            },
            TestContext.Current.CancellationToken).AsTask();

    private static Task AttachTurnAsync(
        AutomationServiceTests.Fixture fixture,
        Guid runId,
        string turnStatus) =>
        fixture.Workspace.Store.WriteAsync(
            async (connection, transaction, cancellationToken) =>
            {
                var threadId = Guid.CreateVersion7();
                var turnId = Guid.CreateVersion7();
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO threads (
                        thread_id, display_name, display_name_search,
                        status, availability, history_mode,
                        current_sequence, last_applied_sequence, active_turn_id,
                        first_user_message, first_user_message_search,
                        fork_source_thread_id, fork_source_sequence, diagnostic,
                        created_utc, updated_utc)
                    VALUES (
                        $threadId, 'Automation recovery', 'AUTOMATION RECOVERY',
                        'active', 'available', 'server',
                        1, 1, NULL,
                        NULL, NULL,
                        NULL, NULL, NULL,
                        $now, $now);
                    INSERT INTO turns (
                        turn_id, thread_id, status, error_code, error_message,
                        created_utc, updated_utc, completed_utc)
                    VALUES (
                        $turnId, $threadId, $turnStatus, NULL, NULL,
                        $now, $now,
                        CASE WHEN $turnStatus IN ('completed', 'failed', 'cancelled')
                             THEN $now ELSE NULL END);
                    UPDATE threads
                    SET active_turn_id = CASE
                        WHEN $turnStatus IN ('completed', 'failed', 'cancelled')
                        THEN NULL ELSE $turnId END
                    WHERE thread_id = $threadId;
                    UPDATE automation_runs
                    SET status = 'running',
                        thread_id = $threadId,
                        started_utc = $now,
                        updated_utc = $now
                    WHERE automation_run_id = $runId;
                    """,
                    cancellationToken,
                    ("$threadId", threadId.ToString("D")),
                    ("$turnId", turnId.ToString("D")),
                    ("$turnStatus", turnStatus),
                    ("$now", now),
                    ("$runId", runId.ToString("D")));
                return 0;
            },
            TestContext.Current.CancellationToken).AsTask();

    private static Task<ScheduleState> ReadScheduleStateAsync(
        AutomationServiceTests.Fixture fixture,
        string id) =>
        fixture.Workspace.Store.ReadAsync(
            async (connection, cancellationToken) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT s.next_occurrence_utc, s.last_occurrence_utc,
                           s.coalesced_occurrence_utc,
                           (SELECT count(*) FROM automation_runs
                            WHERE automation_id = s.automation_id),
                           (SELECT trigger_kind FROM automation_runs
                            WHERE automation_id = s.automation_id
                            ORDER BY created_utc DESC LIMIT 1),
                           (SELECT scheduled_occurrence_utc FROM automation_runs
                            WHERE automation_id = s.automation_id
                            ORDER BY created_utc DESC LIMIT 1)
                    FROM automation_schedules s
                    WHERE s.automation_id = $id;
                    """;
                Add(command, "$id", id);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                Assert.True(await reader.ReadAsync(cancellationToken));
                return new ScheduleState(
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetInt64(1),
                    reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetInt64(5));
            },
            TestContext.Current.CancellationToken).AsTask();

    private static async Task ExecuteAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            Add(command, parameter.Name, parameter.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record ScheduleState(
        long NextOccurrenceUtc,
        long? LastOccurrenceUtc,
        long? CoalescedOccurrenceUtc,
        long RunCount,
        string? TriggerKind,
        long? ScheduledOccurrenceUtc);
}
