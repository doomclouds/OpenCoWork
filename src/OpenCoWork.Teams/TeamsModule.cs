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
                serviceProvider.GetRequiredService<WorkspaceRuntimeDescriptor>(),
                projectWriterLeases:
                    serviceProvider.GetRequiredService<IProjectWriterLeaseService>()));
        services.TryAddSingleton<ICoWorkService>(serviceProvider =>
            serviceProvider.GetRequiredService<CoWorkService>());
        services.AddSingleton(serviceProvider =>
            CoWorkModuleRuntime.Create(
                () => serviceProvider.GetService<IWorkspaceStateStore>() is null
                    ? null
                    : serviceProvider.GetRequiredService<CoWorkService>()));
        services.AddSingleton(serviceProvider =>
            CoWorkToolCatalog.Create(
                () => serviceProvider.GetRequiredService<ICoWorkService>(),
                serviceProvider.GetRequiredService<CoWorkModuleRuntime>()));
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

public sealed class CoWorkModuleRuntime
{
    private readonly Func<CoWorkService?> _service;
    private CoWorkService? _runningService;
    private int _bindingAvailability = (int)ToolBindingAvailability.Unavailable;

    public CoWorkModuleRuntime(CoWorkService? service)
        : this(() => service)
    {
    }

    private CoWorkModuleRuntime(Func<CoWorkService?> service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
    }

    internal static CoWorkModuleRuntime Create(Func<CoWorkService?> service) =>
        new(service);

    public ToolBindingAvailability BindingAvailability =>
        (ToolBindingAvailability)Volatile.Read(ref _bindingAvailability);

    internal async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var service = _service();
        if (service is not null)
        {
            await service.StartReconcilerAsync(cancellationToken);
        }
        _runningService = service;
        Volatile.Write(
            ref _bindingAvailability,
            (int)ToolBindingAvailability.Available);
    }

    internal async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(
            ref _bindingAvailability,
            (int)ToolBindingAvailability.Unavailable);
        var service = _runningService;
        _runningService = null;
        if (service is not null)
        {
            await service.StopReconcilerAsync(cancellationToken);
        }
    }
}
