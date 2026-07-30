using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenCoWork.Abstractions;

namespace OpenCoWork.Automations;

internal sealed class AutomationControlPlane
{
    private int _available;

    public bool IsAvailable => Volatile.Read(ref _available) != 0;

    public void SetAvailable(bool available) =>
        Volatile.Write(ref _available, available ? 1 : 0);
}

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
        services.TryAddSingleton<AutomationControlPlane>();
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
        services.TryAddSingleton<AutomationReconciler>();
        services.TryAddSingleton(serviceProvider =>
            AutomationsModuleRuntime.Create(
                serviceProvider.GetRequiredService<AutomationsConfig>(),
                () => serviceProvider.GetService<IWorkspaceStateStore>() is null
                    ? null
                    : serviceProvider.GetRequiredService<AutomationSourceRuntime>(),
                () => serviceProvider.GetService<IWorkspaceStateStore>() is null
                    ? null
                    : serviceProvider.GetRequiredService<AutomationReconciler>(),
                serviceProvider.GetRequiredService<AutomationControlPlane>()));
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
    private readonly Func<AutomationReconciler?> _reconciler;
    private readonly AutomationControlPlane? _controlPlane;
    private AutomationSourceRuntime? _runningSource;
    private AutomationReconciler? _runningReconciler;
    private int _bindingAvailability = (int)ToolBindingAvailability.Unavailable;

    public AutomationsModuleRuntime(AutomationsConfig config)
        : this(config, static () => null, static () => null, null)
    {
    }

    private AutomationsModuleRuntime(
        AutomationsConfig config,
        Func<AutomationSourceRuntime?> source,
        Func<AutomationReconciler?> reconciler,
        AutomationControlPlane? controlPlane)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _reconciler = reconciler ?? throw new ArgumentNullException(nameof(reconciler));
        _controlPlane = controlPlane;
    }

    internal static AutomationsModuleRuntime Create(
        AutomationsConfig config,
        Func<AutomationSourceRuntime?> source,
        Func<AutomationReconciler?> reconciler,
        AutomationControlPlane controlPlane) =>
        new(config, source, reconciler, controlPlane);

    public ToolBindingAvailability BindingAvailability =>
        (ToolBindingAvailability)Volatile.Read(ref _bindingAvailability);

    internal async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_config.Enabled)
        {
            var source = _source();
            var reconciler = _reconciler();
            try
            {
                if (source is not null)
                {
                    await source.StartAsync(cancellationToken);
                }

                _controlPlane?.SetAvailable(true);
                if (reconciler is not null)
                {
                    await reconciler.StartAsync(cancellationToken);
                }
            }
            catch
            {
                _controlPlane?.SetAvailable(false);
                if (reconciler is not null)
                {
                    await reconciler.StopAsync(CancellationToken.None);
                }

                if (source is not null)
                {
                    await source.StopAsync(CancellationToken.None);
                }

                throw;
            }

            _runningSource = source;
            _runningReconciler = reconciler;
            _controlPlane?.SetAvailable(true);
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
        _controlPlane?.SetAvailable(false);
        var reconciler = _runningReconciler;
        _runningReconciler = null;
        if (reconciler is not null)
        {
            await reconciler.StopAsync(cancellationToken);
        }

        var source = _runningSource;
        _runningSource = null;
        if (source is not null)
        {
            await source.StopAsync(cancellationToken);
        }
    }
}
