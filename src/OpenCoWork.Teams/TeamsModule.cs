using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenCoWork.Abstractions;

namespace OpenCoWork.Teams;

[OpenCoWorkModule("teams", Dependencies = ["session"])]
public sealed class TeamsModule : IOpenCoWorkModule
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.TryAddSingleton<CoWorkConfig>();
        foreach (var contributor in TeamsStateMigrationContributors.Create())
        {
            services.AddSingleton(contributor);
        }

        services.TryAddSingleton(serviceProvider =>
            new CoWorkService(
                serviceProvider.GetRequiredService<IWorkspaceStateStore>(),
                serviceProvider.GetRequiredService<ISensitiveDataService>(),
                serviceProvider.GetRequiredService<CoWorkConfig>(),
                serviceProvider.GetRequiredService<TimeProvider>(),
                serviceProvider.GetRequiredService<ISessionService>(),
                serviceProvider.GetRequiredService<IManagedWorktreeService>(),
                serviceProvider.GetRequiredService<WorkspaceRuntimeDescriptor>()));
        services.TryAddSingleton<ICoWorkService>(serviceProvider =>
            serviceProvider.GetRequiredService<CoWorkService>());
        services.AddSingleton(serviceProvider =>
            new CoWorkModuleRuntime(
                serviceProvider.GetService<IWorkspaceStateStore>() is null
                    ? null
                    : serviceProvider.GetRequiredService<CoWorkService>()));
    }

    public ValueTask StartAsync(
        IServiceProvider services,
        CancellationToken cancellationToken) =>
        services.GetRequiredService<CoWorkModuleRuntime>()
            .StartAsync(cancellationToken);

    public ValueTask StopAsync(
        IServiceProvider services,
        CancellationToken cancellationToken) =>
        services.GetRequiredService<CoWorkModuleRuntime>()
            .StopAsync(cancellationToken);
}

public sealed class CoWorkModuleRuntime(CoWorkService? service)
{
    private readonly CoWorkService? _service = service;
    private int _bindingAvailability = (int)ToolBindingAvailability.Unavailable;

    public ToolBindingAvailability BindingAvailability =>
        (ToolBindingAvailability)Volatile.Read(ref _bindingAvailability);

    internal async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_service is not null)
        {
            await _service.StartReconcilerAsync(cancellationToken);
        }
        Volatile.Write(
            ref _bindingAvailability,
            (int)ToolBindingAvailability.Available);
    }

    internal async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(
            ref _bindingAvailability,
            (int)ToolBindingAvailability.Unavailable);
        if (_service is not null)
        {
            await _service.StopReconcilerAsync(cancellationToken);
        }
    }
}
