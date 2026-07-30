using System.Security.Cryptography;
using System.Text;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Capabilities;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Sessions;
using OpenCoWork.Core.Tools;
using OpenCoWork.Core.Workspaces;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class AutomationRuntimeSnapshotTests
{
    [Fact]
    public async Task Runtime_snapshot_intersects_trust_policy_yaml_catalog_and_bindings()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-automation-runtime-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "workspace");
        var user = Path.Combine(root, "user");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(user);
        try
        {
            var paths = new OpenCoWorkPaths(workspace);
            var files = new CapabilityFileStore(
                new CapabilityPersistencePaths(paths, user));
            var tools = new ToolRuntime(paths);
            var catalog = new WorkspaceCapabilityRuntime(
                [WorkspaceCapabilityRuntime.CreateCoreContributions(tools)]);
            await catalog.StartAsync(TestContext.Current.CancellationToken);
            var models = new ModelsConfig
            {
                DefaultProvider = "provider-a",
                DefaultModel = "model-a",
            };
            var provider = new AutomationRuntimeSnapshotProvider(
                files,
                paths,
                catalog,
                tools,
                new ToolsConfig
                {
                    Effects = new ToolEffectPoliciesConfig
                    {
                        WorkspaceWrite = ToolAuthorityDecision.RequireApproval,
                        ProcessExecution = ToolAuthorityDecision.Deny,
                        NetworkRead = ToolAuthorityDecision.Allow,
                        ExternalMutation = ToolAuthorityDecision.RequireApproval,
                    },
                },
                models);
            var read = tools.Registrations.First(item =>
                item.Definition.Effects == ToolEffect.WorkspaceRead);
            var write = tools.Registrations.First(item =>
                (item.Definition.Effects & ToolEffect.WorkspaceWrite) != 0);
            var process = tools.Registrations.First(item =>
                (item.Definition.Effects & ToolEffect.ProcessExecution) != 0);
            var request = new AutomationRuntimeSnapshotRequest(
                [],
                [],
                [
                    read.Definition.Id.SourceToolId,
                    write.Definition.Id.SourceToolId,
                    process.Definition.Id.SourceToolId,
                ],
                ["workspaceRead", "workspaceWrite", "processExecution"]);

            var denied = await provider.CaptureAsync(
                request,
                TestContext.Current.CancellationToken);
            Assert.Equal(AutomationErrorCodes.PermissionDenied, denied.Error!.Code);

            var source = AutomationTrustBoundary.Source;
            await files.SaveTrustDecisionsAsync(
                new TrustDecisionsDocument(
                    1,
                    [
                        new CapabilityTrustDecision(
                            workspace,
                            source.Kind,
                            source.Id,
                            source.Version,
                            source.Sha256,
                            [CapabilityTrustScope.UnattendedAutomation],
                            []),
                    ]),
                TestContext.Current.CancellationToken);

            var captured = await provider.CaptureAsync(
                request,
                TestContext.Current.CancellationToken);

            Assert.True(captured.IsSuccess, captured.Error?.Code);
            Assert.Equal("provider-a", captured.Value!.ProviderId);
            Assert.Equal("model-a", captured.Value.ModelId);
            Assert.Contains(
                read.Definition.Id.SourceToolId,
                captured.Value.Permissions.Tools);
            Assert.Contains(
                write.Definition.Id.SourceToolId,
                captured.Value.Permissions.Tools);
            Assert.DoesNotContain(
                process.Definition.Id.SourceToolId,
                captured.Value.Permissions.Tools);
            Assert.Equal(
                [
                    new AutomationEffectPermissionSnapshot(
                        "workspaceRead",
                        ToolAuthorityDecision.Allow),
                    new AutomationEffectPermissionSnapshot(
                        "workspaceWrite",
                        ToolAuthorityDecision.RequireApproval),
                ],
                captured.Value.Permissions.Effects);
            Assert.Equal(
                captured.Value.Permissions.Tools,
                captured.Value.Capabilities
                    .Where(item => item.Kind == "tool")
                    .Select(item => item.Id)
                    .Order(StringComparer.Ordinal));
            Assert.All(captured.Value.Capabilities, item =>
            {
                Assert.Equal(64, item.Sha256.Length);
                Assert.True(item.Generation > 0);
            });
            await catalog.StopAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Prepared_turn_store_is_atomic_idempotent_and_secret_checked()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-prepared-turn-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var id = Guid.CreateVersion7();
            var prompt = "safe prompt";
            var requestSha = new string('a', 64);
            var snapshot = new AutomationPreparedTurnSnapshot(
                id,
                requestSha,
                prompt,
                Hash(prompt),
                DateTimeOffset.UtcNow);
            var store = new PreparedAutomationTurnStore(
                new OpenCoWorkPaths(root),
                new NoSensitiveDataService());

            var written = await store.PrepareAsync(
                snapshot,
                TestContext.Current.CancellationToken);
            var replay = await store.PrepareAsync(
                snapshot,
                TestContext.Current.CancellationToken);
            var conflict = await store.PrepareAsync(
                snapshot with
                {
                    RenderedPrompt = "changed",
                    RenderedPromptSha256 = Hash("changed"),
                },
                TestContext.Current.CancellationToken);

            Assert.False(written.IsReplay);
            Assert.True(replay.IsReplay);
            Assert.True(conflict.IsConflict);
            Assert.Equal(
                snapshot,
                await store.ReadAsync(id, TestContext.Current.CancellationToken));
            Assert.False(await store.DeleteAsync(
                id,
                new string('b', 64),
                TestContext.Current.CancellationToken));
            Assert.True(await store.DeleteAsync(
                id,
                requestSha,
                TestContext.Current.CancellationToken));
            Assert.Null(await store.ReadAsync(
                id,
                TestContext.Current.CancellationToken));

            var secretStore = new PreparedAutomationTurnStore(
                new OpenCoWorkPaths(root),
                new ExactSensitiveDataService("secret-canary"));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                secretStore.PrepareAsync(
                    snapshot with
                    {
                        PreparedTurnId = Guid.CreateVersion7(),
                        RenderedPrompt = "secret-canary",
                        RenderedPromptSha256 = Hash("secret-canary"),
                    },
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}
