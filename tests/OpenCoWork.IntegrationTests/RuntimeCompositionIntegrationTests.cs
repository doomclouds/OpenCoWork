using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenCoWork.Abstractions;
using OpenCoWork.App;
using OpenCoWork.Core.Hosting;
using Xunit;

namespace OpenCoWork.IntegrationTests;

public sealed class RuntimeCompositionIntegrationTests
{
    [Fact]
    public async Task Production_composition_uses_only_cli_and_one_lifecycle_coordinator()
    {
        using var host = OpenCoWorkCompositionRoot.Build([]);

        Assert.IsType<CliModule>(
            Assert.Single(host.Services.GetServices<IOpenCoWorkModule>()));
        Assert.IsType<ModuleLifecycleCoordinator>(
            Assert.Single(host.Services.GetServices<IHostedService>()));

        await host.StartAsync(TestContext.Current.CancellationToken);

        var runtime = host.Services.GetRequiredService<WorkspaceRuntime>();
        Assert.Equal(WorkspaceRuntimeStatus.Running, runtime.Status);
        Assert.Equal("cli", runtime.StartedState.PrimaryHost.Id);

        await host.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(WorkspaceRuntimeStatus.Stopped, runtime.Status);
    }
}
