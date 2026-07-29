using OpenCoWork.Abstractions;
using OpenCoWork.Core.Capabilities;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class CapabilityCatalogTests
{
    [Fact]
    public void Contract_values_match_the_frozen_design()
    {
        Assert.Equal(
            [
                "Plugin",
                "Skill",
                "Tool",
                "Provider",
                "Model",
                "AuthProfile",
                "McpServer",
                "LspServer",
                "Hook",
            ],
            Enum.GetNames<CapabilityKind>());
        Assert.Equal(
            [
                "Ready",
                "Disabled",
                "PendingTrust",
                "Starting",
                "Authenticating",
                "Unavailable",
                "Disconnected",
                "Faulted",
                "Conflict",
            ],
            Enum.GetNames<CapabilityStatus>());
        Assert.Equal("capability.revisionConflict", CapabilityErrorCodes.RevisionConflict);
    }

    [Fact]
    public void Contracts_copy_input_collections()
    {
        var scopes = new[] { CapabilityTrustScope.PromptContribution };
        var diagnostics = new[] { "skill.warning" };
        var contribution = new CapabilityContribution(
            CapabilityKind.Skill,
            "review",
            "Review",
            "Review the current change.",
            CapabilityStatus.Ready,
            scopes,
            generation: 3,
            diagnostics);
        var items = new[] { contribution };
        var set = new CapabilityContributionSet(
            Source(CapabilitySourceKind.Workspace, "workspace.skills", 'a'),
            items);

        scopes[0] = CapabilityTrustScope.TrustedHook;
        diagnostics[0] = "changed";
        items[0] = Item(CapabilityKind.Tool, "changed");

        Assert.Equal(CapabilityTrustScope.PromptContribution, contribution.RequiredTrustScopes[0]);
        Assert.Equal("skill.warning", contribution.DiagnosticCodes[0]);
        Assert.Same(contribution, set.Items[0]);
    }

    [Fact]
    public async Task Catalog_hash_and_order_are_deterministic()
    {
        var first = new WorkspaceCapabilityRuntime(
        [
            Set(
                Source(CapabilitySourceKind.Core, "opencowork.core", '1'),
                Item(CapabilityKind.Tool, "web.fetch"),
                Item(CapabilityKind.Tool, "file.read")),
        ]);
        var second = new WorkspaceCapabilityRuntime(
        [
            Set(
                Source(CapabilitySourceKind.Core, "opencowork.core", '1'),
                Item(CapabilityKind.Tool, "file.read"),
                Item(CapabilityKind.Tool, "web.fetch")),
        ]);

        await first.StartAsync(TestContext.Current.CancellationToken);
        await second.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(first.CurrentCatalog.CatalogSha256, second.CurrentCatalog.CatalogSha256);
        Assert.Equal(
            ["file.read", "web.fetch"],
            first.CurrentCatalog.Items.Select(item => item.Id));
        Assert.Equal(1, first.CurrentCatalog.Revision);

        await first.RefreshAsync([], TestContext.Current.CancellationToken);

        Assert.Equal(1, first.CurrentCatalog.Revision);
    }

    private static CapabilityContribution Item(CapabilityKind kind, string id) =>
        new(
            kind,
            id,
            id,
            $"Capability {id}.",
            CapabilityStatus.Ready,
            [],
            generation: 1,
            []);

    private static CapabilityContributionSet Set(
        CapabilitySourceDescriptor source,
        params CapabilityContribution[] items) =>
        new(source, items);

    private static CapabilitySourceDescriptor Source(
        CapabilitySourceKind kind,
        string id,
        char digest) =>
        new(kind, id, "1.0.0", new string(digest, 64));
}

public sealed class WorkspaceCapabilityRuntimeTests
{
    [Fact]
    public async Task Core_wins_external_collision_and_external_collisions_are_isolated()
    {
        var runtime = new WorkspaceCapabilityRuntime(
        [
            Set(
                Source(CapabilitySourceKind.Core, "opencowork.core", '1'),
                Item(CapabilityKind.Tool, "file.read")),
        ]);
        var pluginA = Set(
            Source(CapabilitySourceKind.Plugin, "plugin.a", 'a'),
            Item(CapabilityKind.Tool, "file.read"),
            Item(CapabilityKind.Tool, "shared"));
        var pluginB = Set(
            Source(CapabilitySourceKind.Plugin, "plugin.b", 'b'),
            Item(CapabilityKind.Tool, "shared"));
        await runtime.StartAsync(TestContext.Current.CancellationToken);

        await runtime.RefreshAsync(
            [pluginB, pluginA],
            TestContext.Current.CancellationToken);

        Assert.Equal(CapabilityRuntimeState.Degraded, runtime.Status);
        Assert.Equal(2, runtime.CurrentCatalog.Revision);
        var core = Assert.Single(
            runtime.CurrentCatalog.Items,
            item => item.Id == "file.read");
        Assert.Equal(CapabilityStatus.Ready, core.Status);
        Assert.Equal(CapabilitySourceKind.Core, core.Source.Kind);
        Assert.Equal(
            CapabilityErrorCodes.Conflict,
            Assert.Single(core.DiagnosticCodes));
        Assert.Equal("plugin.a", Assert.Single(core.ConflictingSources).Id);

        var conflict = Assert.Single(
            runtime.CurrentCatalog.Items,
            item => item.Id == "shared");
        Assert.Equal(CapabilityStatus.Conflict, conflict.Status);
        Assert.Equal(CapabilitySourceKind.Conflict, conflict.Source.Kind);
        Assert.Equal(
            ["plugin.a", "plugin.b"],
            conflict.ConflictingSources.Select(source => source.Id));

        var revision = runtime.CurrentCatalog.Revision;
        var hash = runtime.CurrentCatalog.CatalogSha256;
        await runtime.RefreshAsync(
            [pluginA, pluginB],
            TestContext.Current.CancellationToken);

        Assert.Equal(revision, runtime.CurrentCatalog.Revision);
        Assert.Equal(hash, runtime.CurrentCatalog.CatalogSha256);
    }

    [Fact]
    public async Task Binding_generation_change_advances_revision_and_failed_candidate_keeps_old_catalog()
    {
        var runtime = new WorkspaceCapabilityRuntime(
        [
            Set(
                Source(CapabilitySourceKind.Core, "opencowork.core", '1'),
                Item(CapabilityKind.Tool, "file.read")),
        ]);
        await runtime.StartAsync(TestContext.Current.CancellationToken);
        var source = Source(CapabilitySourceKind.Plugin, "plugin.a", 'a');

        await runtime.RefreshAsync(
            [Set(source, Item(CapabilityKind.Tool, "plugin.status", generation: 1))],
            TestContext.Current.CancellationToken);
        var previous = runtime.CurrentCatalog;

        await runtime.RefreshAsync(
            [Set(source, Item(CapabilityKind.Tool, "plugin.status", generation: 2))],
            TestContext.Current.CancellationToken);

        Assert.Equal(previous.Revision + 1, runtime.CurrentCatalog.Revision);
        Assert.NotEqual(previous.CatalogSha256, runtime.CurrentCatalog.CatalogSha256);
        previous = runtime.CurrentCatalog;

        var error = await Assert.ThrowsAsync<CapabilityRuntimeException>(() =>
            runtime.RefreshAsync(
                [
                    Set(
                        Source(CapabilitySourceKind.Conflict, "invalid", 'f'),
                        Item(CapabilityKind.Tool, "invalid")),
                ],
                TestContext.Current.CancellationToken));

        Assert.Equal(CapabilityErrorCodes.DefinitionInvalid, error.Code);
        Assert.Same(previous, runtime.CurrentCatalog);
    }

    [Fact]
    public async Task Snapshot_lease_pins_one_revision_and_releases_once()
    {
        var runtime = new WorkspaceCapabilityRuntime(
        [
            Set(
                Source(CapabilitySourceKind.Core, "opencowork.core", '1'),
                Item(CapabilityKind.Tool, "file.read")),
        ]);
        await runtime.StartAsync(TestContext.Current.CancellationToken);

        var lease = runtime.AcquireSnapshot();

        Assert.Equal(runtime.CurrentCatalog.Revision, lease.Catalog.Revision);
        Assert.Equal(1, runtime.ActiveLeaseCount(lease.Catalog.Revision));

        lease.Dispose();
        lease.Dispose();

        Assert.Equal(0, runtime.ActiveLeaseCount(lease.Catalog.Revision));
    }

    [Fact]
    public async Task Lifecycle_rejects_refresh_outside_ready_states()
    {
        var runtime = new WorkspaceCapabilityRuntime(
        [
            Set(
                Source(CapabilitySourceKind.Core, "opencowork.core", '1'),
                Item(CapabilityKind.Tool, "file.read")),
        ]);

        Assert.Equal(CapabilityRuntimeState.Stopped, runtime.Status);
        Assert.Equal(
            CapabilityErrorCodes.RuntimeUnavailable,
            Assert.Throws<CapabilityRuntimeException>(() => runtime.AcquireSnapshot()).Code);

        await runtime.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(CapabilityRuntimeState.Ready, runtime.Status);

        await runtime.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CapabilityRuntimeState.Stopped, runtime.Status);
        var error = await Assert.ThrowsAsync<CapabilityRuntimeException>(() =>
            runtime.RefreshAsync([], TestContext.Current.CancellationToken));
        Assert.Equal(CapabilityErrorCodes.RuntimeUnavailable, error.Code);
    }

    private static CapabilityContribution Item(
        CapabilityKind kind,
        string id,
        long generation = 1) =>
        new(
            kind,
            id,
            id,
            $"Capability {id}.",
            CapabilityStatus.Ready,
            [],
            generation,
            []);

    private static CapabilityContributionSet Set(
        CapabilitySourceDescriptor source,
        params CapabilityContribution[] items) =>
        new(source, items);

    private static CapabilitySourceDescriptor Source(
        CapabilitySourceKind kind,
        string id,
        char digest) =>
        new(kind, id, "1.0.0", new string(digest, 64));
}
