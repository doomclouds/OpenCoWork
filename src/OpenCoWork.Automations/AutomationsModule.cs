using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenCoWork.Abstractions;

namespace OpenCoWork.Automations;

[ConfigSection("automations")]
public sealed record AutomationsConfig : IValidatableObject
{
    public bool Enabled { get; init; }

    [Range(1, AutomationRuntimeLimits.MaximumConcurrentRuns)]
    public int MaxConcurrentRuns { get; init; } =
        AutomationRuntimeLimits.DefaultMaxConcurrentRuns;

    public TimeSpan MaximumRunTimeout { get; init; } =
        AutomationRuntimeLimits.DefaultRunTimeout;

    public TimeSpan MaximumAttentionTimeout { get; init; } =
        AutomationRuntimeLimits.DefaultAttentionTimeout;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (MaximumRunTimeout < AutomationRuntimeLimits.MinimumRunTimeout ||
            MaximumRunTimeout > AutomationRuntimeLimits.MaximumRunTimeout)
        {
            yield return new ValidationResult(
                "MaximumRunTimeout must be between one minute and twenty-four hours.",
                [nameof(MaximumRunTimeout)]);
        }

        if (MaximumAttentionTimeout < AutomationRuntimeLimits.MinimumAttentionTimeout ||
            MaximumAttentionTimeout > AutomationRuntimeLimits.MaximumAttentionTimeout)
        {
            yield return new ValidationResult(
                "MaximumAttentionTimeout must be between one minute and one hundred sixty-eight hours.",
                [nameof(MaximumAttentionTimeout)]);
        }
    }
}

[OpenCoWorkModule("automations", Dependencies = ["session"])]
public sealed class AutomationsModule : IOpenCoWorkModule
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.TryAddSingleton<AutomationsConfig>();
        foreach (var contributor in AutomationsStateMigrationContributors.Create())
        {
            services.AddSingleton(contributor);
        }

        services.TryAddSingleton<AutomationDefinitionLoader>();
        services.TryAddSingleton<AutomationTemplateRenderer>();
        services.TryAddSingleton(serviceProvider =>
            new AutomationSourceRuntime(
                serviceProvider.GetRequiredService<IWorkspaceStateStore>(),
                serviceProvider.GetRequiredService<WorkspaceRuntimeDescriptor>(),
                serviceProvider.GetRequiredService<AutomationDefinitionLoader>(),
                serviceProvider.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<AutomationService>();
        services.TryAddSingleton<IAutomationService>(serviceProvider =>
            serviceProvider.GetRequiredService<AutomationService>());
        services.TryAddSingleton<AutomationDispatcher>();
        services.TryAddSingleton(serviceProvider =>
            AutomationsModuleRuntime.Create(
                serviceProvider.GetRequiredService<AutomationsConfig>(),
                () => serviceProvider.GetService<IWorkspaceStateStore>() is null
                    ? null
                    : serviceProvider.GetRequiredService<AutomationSourceRuntime>()));
    }

    public ValueTask StartAsync(
        IServiceProvider services,
        CancellationToken cancellationToken) =>
        services.GetRequiredService<AutomationsModuleRuntime>()
            .StartAsync(cancellationToken);

    public ValueTask StopAsync(
        IServiceProvider services,
        CancellationToken cancellationToken) =>
        services.GetRequiredService<AutomationsModuleRuntime>()
            .StopAsync(cancellationToken);
}

public sealed class AutomationsModuleRuntime
{
    private readonly AutomationsConfig _config;
    private readonly Func<AutomationSourceRuntime?> _source;
    private AutomationSourceRuntime? _runningSource;
    private int _bindingAvailability = (int)ToolBindingAvailability.Unavailable;

    public AutomationsModuleRuntime(AutomationsConfig config)
        : this(config, static () => null)
    {
    }

    private AutomationsModuleRuntime(
        AutomationsConfig config,
        Func<AutomationSourceRuntime?> source)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    internal static AutomationsModuleRuntime Create(
        AutomationsConfig config,
        Func<AutomationSourceRuntime?> source) =>
        new(config, source);

    public ToolBindingAvailability BindingAvailability =>
        (ToolBindingAvailability)Volatile.Read(ref _bindingAvailability);

    internal async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_config.Enabled)
        {
            var source = _source();
            if (source is not null)
            {
                await source.StartAsync(cancellationToken);
            }

            _runningSource = source;
            Volatile.Write(
                ref _bindingAvailability,
                (int)ToolBindingAvailability.Available);
        }
    }

    internal async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(
            ref _bindingAvailability,
            (int)ToolBindingAvailability.Unavailable);
        var source = _runningSource;
        _runningSource = null;
        if (source is not null)
        {
            await source.StopAsync(cancellationToken);
        }
    }
}
