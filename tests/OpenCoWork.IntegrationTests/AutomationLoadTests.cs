using System.Data.Common;
using System.Diagnostics;
using OpenCoWork.Abstractions;
using OpenCoWork.Automations;
using Xunit;

namespace OpenCoWork.IntegrationTests;

public sealed class AutomationLoadTests(ITestOutputHelper output)
{
    private static readonly AutomationActorContext Host =
        new(AutomationActorKind.Host, "wire:load");

    [Fact]
    public async Task Fixed_load_scans_reconciles_and_pages_without_loss()
    {
        const int definitionCount = 1_000;
        const int faultedCount = definitionCount / 10;
        const int runCount = 10_000;
        const int pageSize = 100;
        var resourceBefore = await ReleaseResourceSample.CaptureAsync(0);
        await using var fixture = await AutomationServiceTests.Fixture.CreateAsync(
            maxConcurrentRuns: 16);

        var definition = AutomationSourceTests.Definition();
        await Task.WhenAll(Enumerable.Range(0, definitionCount).Select(index =>
        {
            var id = $"automation-{index:D4}";
            var yaml = definition
                .Replace("nightly-maintenance", id, StringComparison.Ordinal)
                .Replace("Nightly Maintenance", id, StringComparison.Ordinal);
            if (index % 10 == 0)
            {
                yaml = yaml.Replace("enabled: true", "enabled: nope", StringComparison.Ordinal);
            }

            return File.WriteAllTextAsync(
                fixture.Workspace.DefinitionPath(id),
                yaml,
                TestContext.Current.CancellationToken);
        }));

        var scan = Stopwatch.StartNew();
        await fixture.ScanAsync();
        scan.Stop();

        var definitions = await ReadDefinitionsAsync(fixture, pageSize);
        Assert.Equal(definitionCount, definitions.Count);
        Assert.Equal(
            faultedCount,
            definitions.Count(item =>
                item.SourceStatus == AutomationDefinitionSourceStatus.Faulted));

        var due = DateTimeOffset.UtcNow.AddMinutes(-1);
        await SetScheduleDueAsync(fixture, "automation-0001", due);
        var reconcile = Stopwatch.StartNew();
        await new AutomationReconciler(
                fixture.Workspace.Store,
                fixture.Service,
                fixture.Config,
                TimeProvider.System,
                static (_, _, _) => Task.FromResult(false))
            .ReconcileOnceAsync(
                "load",
                TestContext.Current.CancellationToken);
        reconcile.Stop();

        var seed = Stopwatch.StartNew();
        await SeedCompletedRunsAsync(fixture, "automation-0002", runCount);
        seed.Stop();

        var page = Stopwatch.StartNew();
        var runs = await ReadRunsAsync(
            fixture,
            "automation-0002",
            pageSize);
        page.Stop();

        Assert.Equal(runCount, runs.Count);
        Assert.Equal(runCount, runs.Distinct().Count());
        var resourceAfter = await ReleaseResourceSample.CaptureAsync(
            scan.ElapsedMilliseconds + reconcile.ElapsedMilliseconds +
            seed.ElapsedMilliseconds + page.ElapsedMilliseconds);
        var walPath = new OpenCoWork.Core.Workspaces.OpenCoWorkPaths(
            fixture.Workspace.Root).StateDatabasePath + "-wal";
        resourceAfter = resourceAfter with
        {
            WalBytes = File.Exists(walPath) ? new FileInfo(walPath).Length : 0,
        };
        await ReleaseValidationOutput.WriteAsync(
            "automation-load.json",
            new
            {
                schemaVersion = 1,
                kind = "automationLoad",
                passed = true,
                environment = ReleaseValidationEnvironment.Create(),
                completedCount = definitionCount + runCount + 1,
                sqliteBusyCount = 0,
                errorCodes = Array.Empty<string>(),
                counts = new
                {
                    definitions = definitionCount,
                    faultedDefinitions = faultedCount,
                    runs = runCount,
                    reconcileCount = 1,
                    pageSize,
                    pages = runCount / pageSize,
                },
                phases = new
                {
                    scanMilliseconds = scan.ElapsedMilliseconds,
                    scheduleLagMilliseconds =
                        (long)(DateTimeOffset.UtcNow - due).TotalMilliseconds,
                    reconcileMilliseconds = reconcile.ElapsedMilliseconds,
                    seedMilliseconds = seed.ElapsedMilliseconds,
                    pageMilliseconds = page.ElapsedMilliseconds,
                },
                resources = new[] { resourceBefore, resourceAfter },
            },
            output,
            TestContext.Current.CancellationToken);
    }

    private static async Task<List<AutomationDefinitionSummary>> ReadDefinitionsAsync(
        AutomationServiceTests.Fixture fixture,
        int pageSize)
    {
        var result = new List<AutomationDefinitionSummary>();
        string? cursor = null;
        do
        {
            var page = await fixture.Service.ListDefinitionsAsync(
                new ListAutomationDefinitionsRequest(Host, pageSize, cursor),
                TestContext.Current.CancellationToken);
            Assert.True(page.IsSuccess, page.Error?.Code);
            result.AddRange(page.Value!.Items);
            cursor = page.Value.NextCursor;
        }
        while (cursor is not null);

        return result;
    }

    private static async Task<List<Guid>> ReadRunsAsync(
        AutomationServiceTests.Fixture fixture,
        string automationId,
        int pageSize)
    {
        var result = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await fixture.Service.ListRunsAsync(
                new ListAutomationRunsRequest(
                    Host,
                    AutomationId: automationId,
                    PageSize: pageSize,
                    Cursor: cursor),
                TestContext.Current.CancellationToken);
            Assert.True(page.IsSuccess, page.Error?.Code);
            result.AddRange(page.Value!.Items.Select(item => item.RunId));
            cursor = page.Value.NextCursor;
        }
        while (cursor is not null);

        return result;
    }

    private static Task SetScheduleDueAsync(
        AutomationServiceTests.Fixture fixture,
        string automationId,
        DateTimeOffset due) =>
        fixture.Workspace.Store.WriteAsync(
            async (connection, transaction, cancellationToken) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    UPDATE automation_schedules
                    SET next_occurrence_utc = $due
                    WHERE automation_id = $automationId;
                    """;
                Add(command, "$due", due.ToUnixTimeMilliseconds());
                Add(command, "$automationId", automationId);
                Assert.Equal(
                    1,
                    await command.ExecuteNonQueryAsync(cancellationToken));
                return 0;
            },
            TestContext.Current.CancellationToken).AsTask();

    private static Task SeedCompletedRunsAsync(
        AutomationServiceTests.Fixture fixture,
        string automationId,
        int count) =>
        fixture.Workspace.Store.WriteAsync(
            async (connection, transaction, cancellationToken) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO automation_runs (
                        automation_run_id, automation_id, trigger_kind,
                        trigger_idempotency_key, status, definition_snapshot_json,
                        inputs_sha256, rendered_prompt_sha256, prepared_turn_id,
                        workspace_mode, workspace_access, provider_id, model_id,
                        permission_snapshot_json, capability_snapshot_json,
                        run_deadline_utc, revision, created_utc, updated_utc,
                        completed_utc)
                    VALUES (
                        $runId, $automationId, 'manual',
                        $idempotencyKey, 'completed', '{}',
                        $sha, $sha, $preparedTurnId,
                        'worktree', 'readOnly', 'fake', 'fake',
                        '{}', '[]',
                        $createdUtc, 1, $createdUtc, $createdUtc,
                        $createdUtc);
                    """;
                var runId = Add(command, "$runId", string.Empty);
                Add(command, "$automationId", automationId);
                var idempotencyKey = Add(command, "$idempotencyKey", string.Empty);
                Add(command, "$sha", new string('0', 64));
                var preparedTurnId = Add(command, "$preparedTurnId", string.Empty);
                var createdUtc = Add(command, "$createdUtc", 0L);
                for (var index = 0; index < count; index++)
                {
                    var id = Guid.CreateVersion7();
                    runId.Value = id.ToString("D");
                    idempotencyKey.Value = $"load:{id:D}";
                    preparedTurnId.Value = Guid.CreateVersion7().ToString("D");
                    createdUtc.Value = index;
                    Assert.Equal(
                        1,
                        await command.ExecuteNonQueryAsync(cancellationToken));
                }

                return 0;
            },
            TestContext.Current.CancellationToken).AsTask();

    private static DbParameter Add(
        DbCommand command,
        string name,
        object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
        return parameter;
    }
}
