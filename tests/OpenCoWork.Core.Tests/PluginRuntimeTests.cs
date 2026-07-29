using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Capabilities;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Tools;
using OpenCoWork.Core.Workspaces;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class PluginRuntimeTests
{
    [Fact]
    public async Task Trusted_plugin_hook_only_observes_its_plugin_tools()
    {
        var (root, workspace, user) = PluginPackageTests.CreateDirectories();
        try
        {
            var paths = new CapabilityPersistencePaths(
                new OpenCoWorkPaths(workspace),
                user);
            var files = new CapabilityFileStore(paths);
            var tools = new ToolRuntime(paths.WorkspacePaths);
            using var store = new PluginPackageStore(paths);
            var archive = PluginPackageTests.CreateToolPackage(
                root,
                "1.0.0",
                Path.Combine(
                    AppContext.BaseDirectory,
                    "OpenCoWork.PluginFixture.dll"),
                includeHook: true);
            var package = await store.StoreLocalAsync(
                archive,
                TestContext.Current.CancellationToken);
            await files.SavePluginLockAsync(
                new PluginLockDocument(
                    1,
                    [new PluginLockEntry(
                        package.Manifest.Id,
                        package.Manifest.Version,
                        package.ContentSha256,
                        Enabled: true)]),
                TestContext.Current.CancellationToken);
            await files.SaveTrustDecisionsAsync(
                new TrustDecisionsDocument(
                    1,
                    [new CapabilityTrustDecision(
                        workspace,
                        CapabilitySourceKind.Plugin,
                        package.Manifest.Id,
                        package.Manifest.Version,
                        package.ContentSha256,
                        [
                            CapabilityTrustScope.InProcessCode,
                            CapabilityTrustScope.TrustedHook,
                        ],
                        [])]),
                TestContext.Current.CancellationToken);
            var plugins = new PluginRuntime(paths, files, store, tools);
            _ = await plugins.DiscoverAsync(
                TestContext.Current.CancellationToken);
            var snapshot = tools.BuildSnapshot(
                AgentMode.Agent,
                new ToolsConfig());
            var registration = Assert.Single(
                snapshot.Registrations,
                item => item.Definition.Id.SourceKind ==
                        ToolSourceKind.PluginNative);
            var canonicalName =
                $"{registration.Definition.Name.Namespace}." +
                registration.Definition.Name.Name;
            var context = new ToolInvocationContext(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                0,
                "call-plugin-hook",
                snapshot.CanonicalToProviderNames[canonicalName],
                JsonSerializer.SerializeToElement(new { text = "hello" }),
                new string('a', 64),
                SensitiveInputDetected: false,
                snapshot);

            var decision = await new CapabilityHookRuntime(plugins.GetHooks())
                .PreToolUseAsync(
                    context,
                    TestContext.Current.CancellationToken);

            Assert.Equal(
                ToolAuthorityDecision.RequireApproval,
                decision.Authority);
            Assert.Equal(TimeSpan.FromSeconds(2), decision.TimeoutCap);
            await plugins.StopAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Trusted_plugin_binds_declared_tool_and_unloads_after_lease_release()
    {
        var (root, workspace, user) = PluginPackageTests.CreateDirectories();
        try
        {
            var paths = new CapabilityPersistencePaths(
                new OpenCoWorkPaths(workspace),
                user);
            var files = new CapabilityFileStore(paths);
            var store = new PluginPackageStore(paths);
            var archive = PluginPackageTests.CreateToolPackage(
                root,
                "1.0.0",
                Path.Combine(AppContext.BaseDirectory, "OpenCoWork.PluginFixture.dll"));
            var package = await store.StoreLocalAsync(
                archive,
                TestContext.Current.CancellationToken);
            await files.SavePluginLockAsync(
                new PluginLockDocument(
                    1,
                    [new PluginLockEntry(
                        package.Manifest.Id,
                        package.Manifest.Version,
                        package.ContentSha256,
                        Enabled: true)]),
                TestContext.Current.CancellationToken);
            await files.SaveTrustDecisionsAsync(
                new TrustDecisionsDocument(
                    1,
                    [new CapabilityTrustDecision(
                        workspace,
                        CapabilitySourceKind.Plugin,
                        package.Manifest.Id,
                        package.Manifest.Version,
                        package.ContentSha256,
                        [CapabilityTrustScope.InProcessCode],
                        [])]),
                TestContext.Current.CancellationToken);
            var context = LoadInvokeAndRemove(
                paths,
                files,
                store,
                package.Manifest.Id,
                TestContext.Current.CancellationToken);
            await CollectAsync(context, TestContext.Current.CancellationToken);
            Assert.False(context.IsAlive);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Digest_change_returns_to_pending_trust_without_loading_code()
    {
        var (root, workspace, user) = PluginPackageTests.CreateDirectories();
        try
        {
            var paths = new CapabilityPersistencePaths(
                new OpenCoWorkPaths(workspace),
                user);
            var files = new CapabilityFileStore(paths);
            var tools = new ToolRuntime(new OpenCoWorkPaths(workspace));
            var store = new PluginPackageStore(paths);
            var archive = PluginPackageTests.CreateToolPackage(
                root,
                "1.0.0",
                Path.Combine(AppContext.BaseDirectory, "OpenCoWork.PluginFixture.dll"));
            var package = await store.StoreLocalAsync(
                archive,
                TestContext.Current.CancellationToken);
            await files.SavePluginLockAsync(
                new PluginLockDocument(
                    1,
                    [new PluginLockEntry(
                        package.Manifest.Id,
                        package.Manifest.Version,
                        package.ContentSha256,
                        Enabled: true)]),
                TestContext.Current.CancellationToken);
            var runtime = new PluginRuntime(paths, files, store, tools);

            var discovery = await runtime.DiscoverAsync(
                TestContext.Current.CancellationToken);

            Assert.Contains(
                discovery.Contributions.SelectMany(set => set.Items),
                item => item.Kind == CapabilityKind.Plugin &&
                        item.Status == CapabilityStatus.PendingTrust);
            Assert.DoesNotContain(
                tools.Registrations,
                item => item.Definition.Id.SourceKind == ToolSourceKind.PluginNative);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Install_updates_lock_and_catalog_but_requires_digest_trust()
    {
        var (root, workspace, user) = PluginPackageTests.CreateDirectories();
        try
        {
            var paths = new CapabilityPersistencePaths(
                new OpenCoWorkPaths(workspace),
                user);
            var files = new CapabilityFileStore(paths);
            using var store = new PluginPackageStore(paths);
            var tools = new ToolRuntime(paths.WorkspacePaths);
            var plugins = new PluginRuntime(paths, files, store, tools);
            var runtime = CreateCapabilityRuntime(paths, files, plugins);
            await runtime.StartAsync(TestContext.Current.CancellationToken);
            var manager = new PluginManager(store, files, runtime);
            var archive = PluginPackageTests.CreateToolPackage(
                root,
                "1.0.0",
                Path.Combine(AppContext.BaseDirectory, "OpenCoWork.PluginFixture.dll"));

            var installed = await manager.InstallLocalAsync(
                archive,
                TestContext.Current.CancellationToken);

            Assert.Equal(CapabilityStatus.PendingTrust, installed.Status);
            Assert.Equal(
                installed.Sha256,
                Assert.Single(
                    (await files.LoadPluginLockAsync(
                        TestContext.Current.CancellationToken)).Plugins).Sha256);
            Assert.Contains(
                runtime.CurrentCatalog.Items,
                item => item.Kind == CapabilityKind.Plugin &&
                        item.Id == installed.Id &&
                        item.Status == CapabilityStatus.PendingTrust);
            Assert.DoesNotContain(
                tools.Registrations,
                item => item.Definition.Id.SourceKind == ToolSourceKind.PluginNative);
            await runtime.StopAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Failed_install_restores_lock_and_catalog()
    {
        var (root, workspace, user) = PluginPackageTests.CreateDirectories();
        try
        {
            var paths = new CapabilityPersistencePaths(
                new OpenCoWorkPaths(workspace),
                user);
            var files = new CapabilityFileStore(paths);
            using var store = new PluginPackageStore(paths);
            var tools = new ToolRuntime(paths.WorkspacePaths);
            var plugins = new PluginRuntime(paths, files, store, tools);
            var archive = Path.Combine(root, "invalid-plugin.zip");
            PluginPackageTests.CreateArchive(
                archive,
                ("opencowork.plugin.json",
                    PluginPackageTests.Manifest("1.0.0", includeEntryPoint: true)
                        .Replace(
                            "OpenCoWork.PluginFixture.EchoPlugin",
                            "OpenCoWork.PluginFixture.MissingPlugin",
                            StringComparison.Ordinal)),
                ("lib/net10.0/OpenCoWork.PluginFixture.dll",
                    File.ReadAllBytes(Path.Combine(
                        AppContext.BaseDirectory,
                        "OpenCoWork.PluginFixture.dll"))),
                ("tools/echo.json",
                    """
                    {
                      "id": "echo",
                      "description": "Echo text.",
                      "inputSchema": {
                        "type": "object",
                        "additionalProperties": true
                      },
                      "effects": [],
                      "replaySafety": "safe",
                      "exposure": "direct",
                      "audience": ["model"],
                      "defaultTimeoutMs": 30000,
                      "executor": "echo"
                    }
                    """));
            var package = await store.StoreLocalAsync(
                archive,
                TestContext.Current.CancellationToken);
            await files.SaveTrustDecisionsAsync(
                new TrustDecisionsDocument(
                    1,
                    [new CapabilityTrustDecision(
                        workspace,
                        CapabilitySourceKind.Plugin,
                        package.Manifest.Id,
                        package.Manifest.Version,
                        package.ContentSha256,
                        [CapabilityTrustScope.InProcessCode],
                        [])]),
                TestContext.Current.CancellationToken);
            var runtime = CreateCapabilityRuntime(paths, files, plugins);
            await runtime.StartAsync(TestContext.Current.CancellationToken);
            var manager = new PluginManager(store, files, runtime);

            await Assert.ThrowsAsync<PluginPackageException>(() =>
                manager.InstallLocalAsync(
                    archive,
                    TestContext.Current.CancellationToken));

            Assert.Empty(
                (await files.LoadPluginLockAsync(
                    TestContext.Current.CancellationToken)).Plugins);
            Assert.DoesNotContain(
                runtime.CurrentCatalog.Items,
                item => item.Kind == CapabilityKind.Plugin &&
                        item.Id == package.Manifest.Id);
            Assert.DoesNotContain(
                tools.Registrations,
                item => item.Definition.Id.SourceKind == ToolSourceKind.PluginNative);
            await runtime.StopAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadInvokeAndRemove(
        CapabilityPersistencePaths paths,
        CapabilityFileStore files,
        PluginPackageStore store,
        string pluginId,
        CancellationToken cancellationToken)
    {
        var tools = new ToolRuntime(paths.WorkspacePaths);
        var runtime = new PluginRuntime(paths, files, store, tools);
        var discovery = runtime.DiscoverAsync(cancellationToken)
            .GetAwaiter()
            .GetResult();
        var lease = runtime.AcquireSnapshotLease(
            tools.BuildSnapshot(AgentMode.Agent, new ToolsConfig()));
        var registration = Assert.Single(
            tools.Registrations,
            item => item.Definition.Id.SourceKind == ToolSourceKind.PluginNative);
        Assert.Equal(
            "hello",
            InvokeAsync(tools, registration, cancellationToken)
                .GetAwaiter()
                .GetResult());
        Assert.Contains(
            discovery.Contributions.SelectMany(set => set.Items),
            item => item.Kind == CapabilityKind.Plugin &&
                    item.Status == CapabilityStatus.Ready);

        var context = runtime.GetLoadContextReference(pluginId);
        runtime.RemoveAsync(pluginId, cancellationToken)
            .GetAwaiter()
            .GetResult();
        Assert.True(context.IsAlive);
        lease.Dispose();
        runtime.StopAsync(cancellationToken)
            .GetAwaiter()
            .GetResult();
        Assert.False(tools.TryResolveBinding(
            registration.RuntimeBindingId,
            registration.BindingGeneration,
            out _));
        Assert.DoesNotContain(
            tools.Registrations,
            item => item.Definition.Id.SourceKind == ToolSourceKind.PluginNative);
        return context;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<string?> InvokeAsync(
        ToolRuntime tools,
        ToolRegistration registration,
        CancellationToken cancellationToken)
    {
        Assert.True(tools.TryResolveBinding(
            registration.RuntimeBindingId,
            registration.BindingGeneration,
            out var binding));
        using var arguments = JsonDocument.Parse("""{"text":"hello"}""");
        var result = await binding!.Executor(arguments.RootElement, cancellationToken);
        Assert.True(result.IsSuccess);
        return result.Output!.Value.GetProperty("text").GetString();
    }

    private static async Task CollectAsync(
        WeakReference context,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 10 && context.IsAlive; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Yield();
        }
    }

    private static WorkspaceCapabilityRuntime CreateCapabilityRuntime(
        CapabilityPersistencePaths paths,
        CapabilityFileStore files,
        PluginRuntime plugins) =>
        new(
            [],
            new WorkspaceCapabilityDiscovery(
                new SkillCatalog(paths, files),
                new ProviderDeclarationCatalog(paths.WorkspacePaths, _ => null),
                plugins));
}
