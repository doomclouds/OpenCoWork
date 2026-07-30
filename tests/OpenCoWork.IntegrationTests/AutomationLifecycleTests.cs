using OpenCoWork.Abstractions;
using OpenCoWork.Automations;
using Xunit;

namespace OpenCoWork.IntegrationTests;

public sealed class AutomationLifecycleTests
{
    [Fact]
    public async Task Unsafe_initial_reconcile_fails_start_and_keeps_control_plane_closed()
    {
        await using var fixture = await SeedPendingRunAsync();
        var control = new AutomationControlPlane();
        var health = new RecordingHealthReporter();
        var reconciler = CreateReconciler(
            fixture,
            control,
            health,
            static (_, _, _) => throw new IOException("state unavailable"));

        await Assert.ThrowsAsync<IOException>(() =>
            reconciler.StartAsync(TestContext.Current.CancellationToken).AsTask());

        Assert.False(control.IsAvailable);
        Assert.Empty(health.DegradedReasons);
    }

    [Fact]
    public async Task Runtime_control_plane_failure_degrades_and_recovers_without_mutating_run()
    {
        await using var fixture = await SeedPendingRunAsync();
        var control = new AutomationControlPlane();
        var health = new RecordingHealthReporter();
        var fail = false;
        var reconciler = CreateReconciler(
            fixture,
            control,
            health,
            (_, _, _) => fail
                ? throw new IOException("state unavailable")
                : Task.FromResult(false));
        await reconciler.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(control.IsAvailable);

        fail = true;
        reconciler.Wake();
        await WaitUntilAsync(() => health.DegradedReasons.Count == 1);
        Assert.False(control.IsAvailable);

        fail = false;
        reconciler.Wake();
        await WaitUntilAsync(() => health.ClearCount == 1);
        Assert.True(control.IsAvailable);

        await reconciler.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, await fixture.CountRunsAsync("not-present"));
    }

    private static async Task<AutomationServiceTests.Fixture> SeedPendingRunAsync()
    {
        var fixture = await AutomationServiceTests.Fixture.CreateAsync();
        await fixture.WriteAsync("lifecycle", enabled: true, scheduled: false);
        await fixture.ScanAsync();
        var definition = await fixture.Service.GetDefinitionAsync(
            new GetAutomationDefinitionRequest(
                new AutomationActorContext(AutomationActorKind.Host, "wire:lifecycle"),
                "lifecycle"),
            TestContext.Current.CancellationToken);
        using var inputs = System.Text.Json.JsonDocument.Parse("{}");
        var started = await fixture.Service.StartRunAsync(
            new StartAutomationRunRequest(
                new AutomationActorContext(AutomationActorKind.Host, "wire:lifecycle"),
                "lifecycle",
                inputs.RootElement.Clone(),
                Guid.CreateVersion7(),
                definition.Value!.Summary.Revision),
            TestContext.Current.CancellationToken);
        Assert.True(started.IsSuccess, started.Error?.Code);
        return fixture;
    }

    private static AutomationReconciler CreateReconciler(
        AutomationServiceTests.Fixture fixture,
        AutomationControlPlane control,
        RecordingHealthReporter health,
        Func<Guid, string, CancellationToken, Task<bool>> dispatch) =>
        new(
            fixture.Workspace.Store,
            fixture.Service,
            fixture.Config,
            TimeProvider.System,
            dispatch,
            source: fixture.Workspace.Source,
            controlPlane: control,
            health: health);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }

    private sealed class RecordingHealthReporter : IModuleHealthReporter
    {
        public List<string> DegradedReasons { get; } = [];

        public int ClearCount { get; private set; }

        public void ReportDegraded(string moduleId, string reason)
        {
            Assert.Equal("automations", moduleId);
            DegradedReasons.Add(reason);
        }

        public void ClearDegraded(string moduleId)
        {
            Assert.Equal("automations", moduleId);
            ClearCount++;
        }
    }
}
