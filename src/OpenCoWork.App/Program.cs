using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Hosting;
using OpenCoWork.Generated;

using (var host = OpenCoWork.App.OpenCoWorkCompositionRoot.Build(args))
{
    await host.StartAsync();
    await host.StopAsync();
}

return 0;

namespace OpenCoWork.App
{
    public static class OpenCoWorkCompositionRoot
    {
        public static IHost Build(string[] args)
        {
            ArgumentNullException.ThrowIfNull(args);

            var registry = new ModuleRegistry(RuntimeCatalog.Modules);
            var primaryHost = registry.SelectPrimaryModule();
            var builder = Host.CreateApplicationBuilder(args);
            builder.Services.AddOpenCoWorkRuntime(
                registry,
                primaryHost,
                new RuntimeConfig().StopTimeout);
            return builder.Build();
        }
    }

    [OpenCoWorkModule("cli", CanBePrimaryHost = true)]
    public sealed class CliModule : IOpenCoWorkModule
    {
        public void ConfigureServices(IServiceCollection services)
        {
        }

        public ValueTask StartAsync(
            IServiceProvider services,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask StopAsync(
            IServiceProvider services,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
