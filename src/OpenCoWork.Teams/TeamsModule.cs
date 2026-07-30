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

        services.TryAddSingleton<CoWorkService>();
        services.TryAddSingleton<ICoWorkService>(serviceProvider =>
            serviceProvider.GetRequiredService<CoWorkService>());
        services.AddSingleton<CoWorkModuleRuntime>();
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
    private int _bindingAvailability = (int)ToolBindingAvailability.Unavailable;

    public ToolBindingAvailability BindingAvailability =>
        (ToolBindingAvailability)Volatile.Read(ref _bindingAvailability);

    internal ValueTask StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Volatile.Write(
            ref _bindingAvailability,
            (int)ToolBindingAvailability.Available);
        return ValueTask.CompletedTask;
    }

    internal ValueTask StopAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(
            ref _bindingAvailability,
            (int)ToolBindingAvailability.Unavailable);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}
