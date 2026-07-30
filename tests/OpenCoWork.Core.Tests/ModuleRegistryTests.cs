using OpenCoWork.Abstractions;
using OpenCoWork.Automations;
using OpenCoWork.Core.Hosting;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class ModuleRegistryTests
{
    [Fact]
    public void Startup_order_is_topological_and_independent_of_catalog_order()
    {
        var registry = new ModuleRegistry(
        [
            Module("leaf", ["beta", "alpha"]),
            Module("beta", ["root"]),
            Module("root", [], priority: 1, canBePrimaryHost: true),
            Module("alpha", ["root"]),
        ]);

        Assert.Equal(
            ["root", "alpha", "beta", "leaf"],
            registry.StartupOrder.Select(module => module.Id));
        Assert.Equal("root", registry.SelectPrimaryModule().Id);
    }

    [Theory]
    [MemberData(nameof(InvalidCatalogs))]
    public void Invalid_catalogs_fail_with_stable_codes(
        ModuleDescriptor[] modules,
        string expectedCode)
    {
        var error = Assert.Throws<ModuleRegistryException>(() => new ModuleRegistry(modules));

        Assert.Equal(expectedCode, error.Code);
    }

    [Fact]
    public void Primary_host_selection_rejects_invalid_preference_and_priority_ties()
    {
        var registry = new ModuleRegistry(
        [
            Module("cli", [], priority: 0, canBePrimaryHost: true),
            Module("gateway", [], priority: 100, canBePrimaryHost: true),
            Module("worker", []),
        ]);

        Assert.Equal("gateway", registry.SelectPrimaryModule().Id);
        Assert.Equal("cli", registry.SelectPrimaryModule("cli").Id);
        Assert.Equal(
            "OCWMOD005",
            Assert.Throws<ModuleRegistryException>(
                () => registry.SelectPrimaryModule("worker")).Code);

        var tied = new ModuleRegistry(
        [
            Module("first", [], priority: 10, canBePrimaryHost: true),
            Module("second", [], priority: 10, canBePrimaryHost: true),
        ]);

        Assert.Equal(
            "OCWMOD006",
            Assert.Throws<ModuleRegistryException>(() => tied.SelectPrimaryModule()).Code);
    }

    [Fact]
    public void Automations_is_non_primary_and_starts_after_session()
    {
        var attribute = typeof(AutomationsModule)
            .GetCustomAttributes(typeof(OpenCoWorkModuleAttribute), inherit: false)
            .Cast<OpenCoWorkModuleAttribute>()
            .Single();
        var registry = new ModuleRegistry(
        [
            new ModuleDescriptor(
                typeof(AutomationsModule),
                attribute.Id,
                attribute.Dependencies,
                attribute.Priority,
                attribute.CanBePrimaryHost),
            Module("session", [], canBePrimaryHost: true),
        ]);

        Assert.Equal(["session", "automations"], registry.StartupOrder.Select(item => item.Id));
        Assert.False(attribute.CanBePrimaryHost);
        Assert.Equal("session", registry.SelectPrimaryModule().Id);
    }

    public static TheoryData<ModuleDescriptor[], string> InvalidCatalogs =>
        new()
        {
            { [], "OCWMOD004" },
            { [Module("Invalid ID", [])], "OCWMOD010" },
            { [Module("valid", ["Invalid Dependency"])], "OCWMOD010" },
            { [Module("same", []), Module("same", [])], "OCWMOD001" },
            { [Module("consumer", ["missing"])], "OCWMOD002" },
            {
                [
                    Module("alpha", ["beta"]),
                    Module("beta", ["alpha"]),
                ],
                "OCWMOD003"
            },
        };

    private static ModuleDescriptor Module(
        string id,
        string[] dependencies,
        int priority = 0,
        bool canBePrimaryHost = false) =>
        new(
            typeof(TestModule),
            id,
            dependencies,
            priority,
            canBePrimaryHost);

    private sealed class TestModule;
}
