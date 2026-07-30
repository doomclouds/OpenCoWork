using OpenCoWork.Abstractions;
using OpenCoWork.Automations;
using Xunit;

namespace OpenCoWork.IntegrationTests;

public sealed class AutomationSourceTests
{
    [Fact]
    public async Task Full_scan_projects_ready_faulted_missing_and_recovery_without_stale_fallback()
    {
        await using var workspace = await AutomationSourceTestWorkspace.CreateAsync();
        var path = workspace.DefinitionPath("nightly-maintenance");
        await File.WriteAllTextAsync(path, Definition(), TestContext.Current.CancellationToken);

        await workspace.Source.ScanAsync(TestContext.Current.CancellationToken);
        var ready = await workspace.Source.ReadAsync(
            "nightly-maintenance",
            TestContext.Current.CancellationToken);
        Assert.Equal(AutomationDefinitionSourceStatus.Ready, ready!.Status);
        Assert.Equal(1, ready.Revision);
        Assert.Equal(1, ready.AutomationRevision);
        Assert.NotNull(ready.DefinitionJson);
        Assert.NotNull(ready.Schedule);

        await File.WriteAllTextAsync(
            path,
            "# formatting only\n" + Definition().Replace("enabled: true", "enabled:    true"),
            TestContext.Current.CancellationToken);
        await workspace.Source.ScanAsync(TestContext.Current.CancellationToken);
        var formatted = await workspace.Source.ReadAsync(
            "nightly-maintenance",
            TestContext.Current.CancellationToken);
        Assert.Equal(ready.DefinitionVersion, formatted!.DefinitionVersion);
        Assert.NotEqual(ready.SourceSha256, formatted.SourceSha256);
        Assert.Equal(ready.Revision, formatted.Revision);
        Assert.Equal(ready.AutomationRevision, formatted.AutomationRevision);

        await File.WriteAllTextAsync(
            path,
            Definition().Replace("enabled: true", "enabled: nope"),
            TestContext.Current.CancellationToken);
        await workspace.Source.ScanAsync(TestContext.Current.CancellationToken);
        var faulted = await workspace.Source.ReadAsync(
            "nightly-maintenance",
            TestContext.Current.CancellationToken);
        Assert.Equal(AutomationDefinitionSourceStatus.Faulted, faulted!.Status);
        Assert.Null(faulted.DefinitionJson);
        Assert.Null(faulted.Schedule);
        Assert.Equal(2, faulted.Revision);

        await workspace.Source.ScanAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            2,
            (await workspace.Source.ReadAsync(
                "nightly-maintenance",
                TestContext.Current.CancellationToken))!.Revision);

        File.Delete(path);
        await workspace.Source.ScanAsync(TestContext.Current.CancellationToken);
        var missing = await workspace.Source.ReadAsync(
            "nightly-maintenance",
            TestContext.Current.CancellationToken);
        Assert.Equal(AutomationDefinitionSourceStatus.Missing, missing!.Status);
        Assert.Equal(3, missing.Revision);

        await File.WriteAllTextAsync(path, Definition(), TestContext.Current.CancellationToken);
        await workspace.Source.ScanAsync(TestContext.Current.CancellationToken);
        var recovered = await workspace.Source.ReadAsync(
            "nightly-maintenance",
            TestContext.Current.CancellationToken);
        Assert.Equal(AutomationDefinitionSourceStatus.Ready, recovered!.Status);
        Assert.Equal(4, recovered.Revision);
    }

    [Fact]
    public async Task Rename_is_one_atomic_revision_with_old_tombstone_and_new_definition()
    {
        await using var workspace = await AutomationSourceTestWorkspace.CreateAsync();
        var oldPath = workspace.DefinitionPath("nightly-maintenance");
        await File.WriteAllTextAsync(oldPath, Definition(), TestContext.Current.CancellationToken);
        await workspace.Source.ScanAsync(TestContext.Current.CancellationToken);

        var newPath = workspace.DefinitionPath("morning-maintenance");
        File.Move(oldPath, newPath);
        await File.WriteAllTextAsync(
            newPath,
            Definition()
                .Replace("nightly-maintenance", "morning-maintenance")
                .Replace("Nightly Maintenance", "Morning Maintenance"),
            TestContext.Current.CancellationToken);
        await workspace.Source.ScanAsync(TestContext.Current.CancellationToken);

        var old = await workspace.Source.ReadAsync(
            "nightly-maintenance",
            TestContext.Current.CancellationToken);
        var current = await workspace.Source.ReadAsync(
            "morning-maintenance",
            TestContext.Current.CancellationToken);
        Assert.Equal(AutomationDefinitionSourceStatus.Missing, old!.Status);
        Assert.Equal(AutomationDefinitionSourceStatus.Ready, current!.Status);
        Assert.Equal(old.AutomationRevision, current.AutomationRevision);
        Assert.Equal(2, current.AutomationRevision);
    }

    [Fact]
    public async Task Watcher_coalesces_bursts_and_full_scan_recovers_from_event_loss()
    {
        await using var workspace = await AutomationSourceTestWorkspace.CreateAsync();
        await workspace.Source.StartAsync(TestContext.Current.CancellationToken);
        var path = workspace.DefinitionPath("nightly-maintenance");

        await File.WriteAllTextAsync(path, Definition(), TestContext.Current.CancellationToken);
        for (var index = 0; index < 5; index++)
        {
            await File.AppendAllTextAsync(
                path,
                $"{Environment.NewLine}# event {index}",
                TestContext.Current.CancellationToken);
        }

        var projected = await EventuallyAsync(
            () => workspace.Source.ReadAsync(
                "nightly-maintenance",
                TestContext.Current.CancellationToken));
        Assert.Equal(AutomationDefinitionSourceStatus.Ready, projected!.Status);
        Assert.Equal(1, projected.AutomationRevision);

        await File.WriteAllTextAsync(
            workspace.DefinitionPath("manual-rescan"),
            Definition().Replace("nightly-maintenance", "manual-rescan"),
            TestContext.Current.CancellationToken);
        await workspace.Source.ScanAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(await workspace.Source.ReadAsync(
            "manual-rescan",
            TestContext.Current.CancellationToken));
    }

    private static async Task<AutomationSourceProjection?> EventuallyAsync(
        Func<ValueTask<AutomationSourceProjection?>> read)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (await read() is { } value)
            {
                return value;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        return null;
    }

    internal static string Definition() =>
        """
        schemaVersion: 1
        id: nightly-maintenance
        displayName: Nightly Maintenance
        enabled: true
        schedule:
          cron: "0 2 * * *"
          timeZone: Asia/Shanghai
        workspace:
          mode: worktree
          allowDirtyOrigin: false
        prompt: Do {{ inputs.task }}
        inputSchema:
          type: object
          properties:
            task:
              type: string
          required: [task]
          additionalProperties: false
        defaults:
          task: cleanup
        allow:
          effects: [workspaceRead]
        runTimeout: 30m
        attentionTimeout: 24h
        """;
}
