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
        services.TryAddSingleton<AutomationsModuleRuntime>();
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

public sealed class AutomationsModuleRuntime(AutomationsConfig config)
{
    private int _bindingAvailability = (int)ToolBindingAvailability.Unavailable;

    public ToolBindingAvailability BindingAvailability =>
        (ToolBindingAvailability)Volatile.Read(ref _bindingAvailability);

    internal ValueTask StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (config.Enabled)
        {
            Volatile.Write(
                ref _bindingAvailability,
                (int)ToolBindingAvailability.Available);
        }

        return ValueTask.CompletedTask;
    }

    internal ValueTask StopAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(
            ref _bindingAvailability,
            (int)ToolBindingAvailability.Unavailable);
        return ValueTask.CompletedTask;
    }
}
