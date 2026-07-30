using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenCoWork.Abstractions;
using OpenCoWork.Automations;
using OpenCoWork.Core.Hosting;
using OpenCoWork.Teams;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class WorkspaceRuntimeTests
{
    [Fact]
    public async Task Runtime_publishes_started_state_and_stops_modules_in_strict_reverse_order()
    {
        var probe = new LifecycleProbe();
        var runtime = CreateRuntime(
            probe,
            Module<RootModule>("root", [], canBePrimaryHost: true),
            Module<AlphaModule>("alpha", ["root"]),
            Module<BetaModule>("beta", ["root"]));

        await runtime.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(WorkspaceRuntimeStatus.Running, runtime.Status);
        Assert.Equal("root", runtime.StartedState.PrimaryHost.Id);

        runtime.ReportDegraded("alpha", "health check failed");
        Assert.Equal(WorkspaceRuntimeStatus.Degraded, runtime.Status);
        runtime.ClearDegraded("alpha");
        Assert.Equal(WorkspaceRuntimeStatus.Running, runtime.Status);

        await runtime.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(WorkspaceRuntimeStatus.Stopped, runtime.Status);
        Assert.Equal(
            [
                "start:root",
                "start:alpha",
                "start:beta",
                "stop:beta",
                "stop:alpha",
                "stop:root",
            ],
            probe.Events);
        Assert.Equal(
            "OCWLIFE001",
            Assert.Throws<WorkspaceRuntimeStateException>(() => runtime.StartedState).Code);
    }

    [Fact]
    public async Task Module_can_report_degraded_while_runtime_is_starting()
    {
        var probe = new LifecycleProbe();
        WorkspaceRuntime? runtime = null;
        probe.StartActions["root"] = _ =>
        {
            runtime!.ReportDegraded("root", "startup recovery is incomplete");
            return ValueTask.CompletedTask;
        };
        runtime = CreateRuntime(
            probe,
            Module<RootModule>("root", [], canBePrimaryHost: true));

        await runtime.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(WorkspaceRuntimeStatus.Degraded, runtime.Status);
        runtime.ClearDegraded("root");
        Assert.Equal(WorkspaceRuntimeStatus.Running, runtime.Status);
        await runtime.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Startup_failure_rolls_back_only_successful_modules_and_requires_fault_cleanup()
    {
        var probe = new LifecycleProbe();
        probe.StartActions["alpha"] = _ =>
            ValueTask.FromException(new InvalidOperationException("start failed"));
        var runtime = CreateRuntime(
            probe,
            Module<RootModule>("root", [], canBePrimaryHost: true),
            Module<AlphaModule>("alpha", ["root"]),
            Module<BetaModule>("beta", ["alpha"]));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(WorkspaceRuntimeStatus.Faulted, runtime.Status);
        Assert.Equal(["start:root", "start:alpha", "stop:root"], probe.Events);
        Assert.Equal(
            "OCWLIFE002",
            (await Assert.ThrowsAsync<WorkspaceRuntimeStateException>(
                () => runtime.StartAsync(TestContext.Current.CancellationToken))).Code);

        await runtime.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(WorkspaceRuntimeStatus.Stopped, runtime.Status);

        probe.StartActions.Clear();
        await runtime.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(WorkspaceRuntimeStatus.Running, runtime.Status);
        await runtime.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Startup_cancellation_rolls_back_and_returns_to_stopped()
    {
        using var cancellation = new CancellationTokenSource();
        var probe = new LifecycleProbe();
        probe.StartActions["alpha"] = _ =>
        {
            cancellation.Cancel();
            return ValueTask.FromCanceled(cancellation.Token);
        };
        var runtime = CreateRuntime(
            probe,
            Module<RootModule>("root", [], canBePrimaryHost: true),
            Module<AlphaModule>("alpha", ["root"]));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runtime.StartAsync(cancellation.Token));

        Assert.Equal(WorkspaceRuntimeStatus.Stopped, runtime.Status);
        Assert.Equal(["start:root", "start:alpha", "stop:root"], probe.Events);
    }

    [Fact]
    public async Task Stop_aggregates_failures_continues_cleanup_and_can_retry_faulted_modules()
    {
        var probe = new LifecycleProbe();
        probe.StopActions["beta"] = _ =>
            ValueTask.FromException(new InvalidOperationException("beta stop failed"));
        probe.StopActions["root"] = _ =>
            ValueTask.FromException(new InvalidOperationException("root stop failed"));
        var runtime = CreateRuntime(
            probe,
            Module<RootModule>("root", [], canBePrimaryHost: true),
            Module<AlphaModule>("alpha", ["root"]),
            Module<BetaModule>("beta", ["root"]));
        await runtime.StartAsync(TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<AggregateException>(
            () => runtime.StopAsync(TestContext.Current.CancellationToken));

        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.Equal(WorkspaceRuntimeStatus.Faulted, runtime.Status);
        Assert.Equal(
            ["stop:beta", "stop:alpha", "stop:root"],
            probe.Events.Skip(3));

        probe.StopActions.Clear();
        await runtime.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(WorkspaceRuntimeStatus.Stopped, runtime.Status);
        Assert.Equal(["stop:beta", "stop:root"], probe.Events.Skip(6));
    }

    [Fact]
    public async Task Stop_timeout_does_not_skip_remaining_modules_and_pending_cleanup_can_finish()
    {
        var pendingStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new LifecycleProbe();
        probe.StopActions["beta"] = _ => new ValueTask(pendingStop.Task);
        var runtime = CreateRuntime(
            probe,
            TimeSpan.FromMilliseconds(50),
            Module<RootModule>("root", [], canBePrimaryHost: true),
            Module<AlphaModule>("alpha", ["root"]),
            Module<BetaModule>("beta", ["root"]));
        await runtime.StartAsync(TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<AggregateException>(
            () => runtime.StopAsync(TestContext.Current.CancellationToken));

        Assert.Contains(error.InnerExceptions, exception => exception is TimeoutException);
        Assert.Equal(["stop:beta", "stop:alpha", "stop:root"], probe.Events.Skip(3));
        Assert.Equal(WorkspaceRuntimeStatus.Faulted, runtime.Status);

        pendingStop.SetResult();
        await runtime.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(WorkspaceRuntimeStatus.Stopped, runtime.Status);
    }

    [Fact]
    public async Task Module_that_finishes_at_timeout_is_not_stopped_twice()
    {
        var probe = new LifecycleProbe();
        probe.StopActions["root"] = cancellationToken =>
        {
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => completion.TrySetResult());
            return new ValueTask(completion.Task);
        };
        var runtime = CreateRuntime(
            probe,
            TimeSpan.FromMilliseconds(50),
            Module<RootModule>("root", [], canBePrimaryHost: true));
        await runtime.StartAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<AggregateException>(
            () => runtime.StopAsync(TestContext.Current.CancellationToken));
        await runtime.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(WorkspaceRuntimeStatus.Stopped, runtime.Status);
        Assert.Single(probe.Events, entry => entry == "stop:root");
    }

    [Fact]
    public async Task Concurrent_start_does_not_start_modules_twice()
    {
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new LifecycleProbe();
        probe.StartActions["root"] = _ => new ValueTask(startGate.Task);
        var runtime = CreateRuntime(
            probe,
            Module<RootModule>("root", [], canBePrimaryHost: true));
        var first = runtime.StartAsync(TestContext.Current.CancellationToken);

        await probe.WaitForEventAsync("start:root");
        var second = runtime.StartAsync(TestContext.Current.CancellationToken);
        startGate.SetResult();

        await first;
        Assert.Equal(
            "OCWLIFE002",
            (await Assert.ThrowsAsync<WorkspaceRuntimeStateException>(() => second)).Code);
        Assert.Single(probe.Events, entry => entry == "start:root");

        await runtime.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Caller_cancellation_after_stop_begins_does_not_skip_cleanup()
    {
        using var callerCancellation = new CancellationTokenSource();
        var probe = new LifecycleProbe();
        probe.StopActions["beta"] = _ =>
        {
            callerCancellation.Cancel();
            return ValueTask.CompletedTask;
        };
        var runtime = CreateRuntime(
            probe,
            Module<RootModule>("root", [], canBePrimaryHost: true),
            Module<AlphaModule>("alpha", ["root"]),
            Module<BetaModule>("beta", ["root"]));
        await runtime.StartAsync(TestContext.Current.CancellationToken);

        await runtime.StopAsync(callerCancellation.Token);

        Assert.Equal(WorkspaceRuntimeStatus.Stopped, runtime.Status);
        Assert.Equal(["stop:beta", "stop:alpha", "stop:root"], probe.Events.Skip(3));
    }

    [Fact]
    public async Task Failed_dispose_keeps_faulted_runtime_available_for_cleanup_retry()
    {
        var probe = new LifecycleProbe();
        probe.StopActions["root"] = _ =>
            ValueTask.FromException(new InvalidOperationException("stop failed"));
        var runtime = CreateRuntime(
            probe,
            Module<RootModule>("root", [], canBePrimaryHost: true));
        await runtime.StartAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<AggregateException>(
            async () => await runtime.DisposeAsync());

        probe.StopActions.Clear();
        await runtime.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(WorkspaceRuntimeStatus.Stopped, runtime.Status);
    }

    [Fact]
    public void Service_registration_is_topological_and_modules_cannot_add_hosted_services()
    {
        var services = new ServiceCollection();
        var registry = new ModuleRegistry(
        [
            Module<RegistrationBetaModule>("beta", ["root"]),
            Module<RegistrationRootModule>("root", [], canBePrimaryHost: true),
            Module<RegistrationAlphaModule>("alpha", ["root"]),
        ]);

        services.AddOpenCoWorkRuntime(
            registry,
            registry.SelectPrimaryModule(),
            TimeSpan.FromSeconds(1));

        Assert.Equal(
            ["root", "alpha", "beta"],
            services
                .Where(descriptor => descriptor.ServiceType == typeof(RegistrationMarker))
                .Select(descriptor =>
                    Assert.IsType<RegistrationMarker>(descriptor.ImplementationInstance).Id));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IHostedService));

        var invalidServices = new ServiceCollection();
        var invalidRegistry = new ModuleRegistry(
        [
            Module<IndependentHostedServiceModule>(
                "invalid",
                [],
                canBePrimaryHost: true),
        ]);

        Assert.Equal(
            "OCWMOD009",
            Assert.Throws<ModuleRegistryException>(() =>
                invalidServices.AddOpenCoWorkRuntime(
                    invalidRegistry,
                    invalidRegistry.SelectPrimaryModule(),
                    TimeSpan.FromSeconds(1))).Code);

        var forgedPrimary = new ModuleDescriptor(
            typeof(RegistrationAlphaModule),
            "root",
            [],
            priority: 0,
            canBePrimaryHost: true);
        Assert.Equal(
            "OCWMOD005",
            Assert.Throws<ModuleRegistryException>(() =>
                new ServiceCollection().AddOpenCoWorkRuntime(
                    registry,
                    forgedPrimary,
                    TimeSpan.FromSeconds(1))).Code);

        var invalidTimeoutServices = new ServiceCollection();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            invalidTimeoutServices.AddOpenCoWorkRuntime(
                registry,
                registry.SelectPrimaryModule(),
                TimeSpan.Zero));
        Assert.DoesNotContain(
            invalidTimeoutServices,
            descriptor => descriptor.ServiceType == typeof(RegistrationMarker));
    }

    [Fact]
    public async Task Teams_is_non_primary_and_binding_follows_runtime_lifecycle()
    {
        var attribute = typeof(TeamsModule)
            .GetCustomAttributes(typeof(OpenCoWorkModuleAttribute), inherit: false)
            .Cast<OpenCoWorkModuleAttribute>()
            .Single();
        Assert.Equal("teams", attribute.Id);
        Assert.Equal(["session"], attribute.Dependencies);
        Assert.False(attribute.CanBePrimaryHost);

        var probe = new TeamsLifecycleProbe();
        var services = new ServiceCollection();
        services.AddSingleton(probe);
        var registry = new ModuleRegistry(
        [
            Module<TeamsSessionModule>("session", []),
            Module<TeamsModule>("teams", ["session"]),
            Module<TeamsHostModule>("host", ["teams"], canBePrimaryHost: true),
        ]);
        services.AddOpenCoWorkRuntime(
            registry,
            registry.SelectPrimaryModule(),
            TimeSpan.FromSeconds(1));
        await using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<WorkspaceRuntime>();
        var binding = provider.GetRequiredService<CoWorkModuleRuntime>();

        Assert.Equal(ToolBindingAvailability.Unavailable, binding.BindingAvailability);

        await runtime.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ToolBindingAvailability.Available, binding.BindingAvailability);

        await runtime.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ToolBindingAvailability.Unavailable, binding.BindingAvailability);
        Assert.True(probe.BindingWasUnavailableWhenSessionStopped);
    }

    [Fact]
    public async Task Automations_is_non_primary_and_binding_follows_runtime_lifecycle()
    {
        var probe = new AutomationsLifecycleProbe();
        var services = new ServiceCollection();
        services.AddSingleton(probe);
        services.AddSingleton(new AutomationsConfig { Enabled = true });
        var registry = new ModuleRegistry(
        [
            Module<AutomationsSessionModule>("session", []),
            Module<AutomationsModule>("automations", ["session"]),
            Module<AutomationsHostModule>("host", ["automations"], canBePrimaryHost: true),
        ]);
        services.AddOpenCoWorkRuntime(
            registry,
            registry.SelectPrimaryModule(),
            TimeSpan.FromSeconds(1));
        await using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<WorkspaceRuntime>();
        var binding = provider.GetRequiredService<AutomationsModuleRuntime>();

        Assert.Equal(ToolBindingAvailability.Unavailable, binding.BindingAvailability);

        await runtime.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ToolBindingAvailability.Available, binding.BindingAvailability);

        await runtime.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ToolBindingAvailability.Unavailable, binding.BindingAvailability);
        Assert.True(probe.BindingWasUnavailableWhenSessionStopped);
    }

    [Fact]
    public async Task Disabled_automations_stays_unavailable()
    {
        var module = new AutomationsModule();
        var services = new ServiceCollection();
        module.ConfigureServices(services);
        await using var provider = services.BuildServiceProvider();
        var binding = provider.GetRequiredService<AutomationsModuleRuntime>();

        await module.StartAsync(provider, TestContext.Current.CancellationToken);
        Assert.Equal(ToolBindingAvailability.Unavailable, binding.BindingAvailability);

        await module.StopAsync(provider, TestContext.Current.CancellationToken);
        Assert.Equal(ToolBindingAvailability.Unavailable, binding.BindingAvailability);
    }

    private static WorkspaceRuntime CreateRuntime(
        LifecycleProbe probe,
        params ModuleDescriptor[] modules) =>
        CreateRuntime(probe, TimeSpan.FromSeconds(1), modules);

    private static WorkspaceRuntime CreateRuntime(
        LifecycleProbe probe,
        TimeSpan stopTimeout,
        params ModuleDescriptor[] modules)
    {
        var registry = new ModuleRegistry(modules);
        return new ModuleLifecycleCoordinator(
            registry,
            probe,
            registry.SelectPrimaryModule(),
            stopTimeout).Runtime;
    }

    private static ModuleDescriptor Module<T>(
        string id,
        string[] dependencies,
        bool canBePrimaryHost = false) =>
        new(typeof(T), id, dependencies, priority: 0, canBePrimaryHost);

    public sealed class RootModule : ProbeModule
    {
        protected override string Id => "root";
    }

    public sealed class AlphaModule : ProbeModule
    {
        protected override string Id => "alpha";
    }

    public sealed class BetaModule : ProbeModule
    {
        protected override string Id => "beta";
    }

    public sealed class RegistrationRootModule : RegistrationModule
    {
        protected override string Id => "root";
    }

    public sealed class RegistrationAlphaModule : RegistrationModule
    {
        protected override string Id => "alpha";
    }

    public sealed class RegistrationBetaModule : RegistrationModule
    {
        protected override string Id => "beta";
    }

    public abstract class ProbeModule : IOpenCoWorkModule
    {
        protected abstract string Id { get; }

        public void ConfigureServices(IServiceCollection services)
        {
        }

        public ValueTask StartAsync(
            IServiceProvider services,
            CancellationToken cancellationToken) =>
            GetProbe(services).StartAsync(Id, cancellationToken);

        public ValueTask StopAsync(
            IServiceProvider services,
            CancellationToken cancellationToken) =>
            GetProbe(services).StopAsync(Id, cancellationToken);

        private static LifecycleProbe GetProbe(IServiceProvider services) =>
            (LifecycleProbe)(services.GetService(typeof(LifecycleProbe)) ??
                throw new InvalidOperationException("Lifecycle probe is unavailable."));
    }

    public abstract class RegistrationModule : IOpenCoWorkModule
    {
        protected abstract string Id { get; }

        public void ConfigureServices(IServiceCollection services) =>
            services.AddSingleton(new RegistrationMarker(Id));

        public ValueTask StartAsync(
            IServiceProvider services,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask StopAsync(
            IServiceProvider services,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    public sealed class IndependentHostedServiceModule : IOpenCoWorkModule
    {
        public void ConfigureServices(IServiceCollection services) =>
            services.AddSingleton<IHostedService, NoopHostedService>();

        public ValueTask StartAsync(
            IServiceProvider services,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask StopAsync(
            IServiceProvider services,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    public sealed class TeamsSessionModule : IOpenCoWorkModule
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
            CancellationToken cancellationToken)
        {
            services.GetRequiredService<TeamsLifecycleProbe>()
                .BindingWasUnavailableWhenSessionStopped =
                services.GetRequiredService<CoWorkModuleRuntime>().BindingAvailability ==
                ToolBindingAvailability.Unavailable;
            return ValueTask.CompletedTask;
        }
    }

    public sealed class TeamsHostModule : IOpenCoWorkModule
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

    public sealed class AutomationsSessionModule : IOpenCoWorkModule
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
            CancellationToken cancellationToken)
        {
            services.GetRequiredService<AutomationsLifecycleProbe>()
                .BindingWasUnavailableWhenSessionStopped =
                services.GetRequiredService<AutomationsModuleRuntime>().BindingAvailability ==
                ToolBindingAvailability.Unavailable;
            return ValueTask.CompletedTask;
        }
    }

    public sealed class AutomationsHostModule : IOpenCoWorkModule
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

    public sealed class TeamsLifecycleProbe
    {
        public bool BindingWasUnavailableWhenSessionStopped { get; set; }
    }

    public sealed class AutomationsLifecycleProbe
    {
        public bool BindingWasUnavailableWhenSessionStopped { get; set; }
    }

    public sealed class NoopHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public sealed record RegistrationMarker(string Id);

    public sealed class LifecycleProbe : IServiceProvider
    {
        private readonly ConcurrentQueue<string> _events = new();

        public IReadOnlyList<string> Events => _events.ToArray();

        public Dictionary<string, Func<CancellationToken, ValueTask>> StartActions { get; } =
            new(StringComparer.Ordinal);

        public Dictionary<string, Func<CancellationToken, ValueTask>> StopActions { get; } =
            new(StringComparer.Ordinal);

        public object? GetService(Type serviceType) =>
            serviceType == typeof(LifecycleProbe) ? this : null;

        public ValueTask StartAsync(string id, CancellationToken cancellationToken)
        {
            _events.Enqueue($"start:{id}");
            return StartActions.TryGetValue(id, out var action)
                ? action(cancellationToken)
                : ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(string id, CancellationToken cancellationToken)
        {
            _events.Enqueue($"stop:{id}");
            return StopActions.TryGetValue(id, out var action)
                ? action(cancellationToken)
                : ValueTask.CompletedTask;
        }

        public async Task WaitForEventAsync(string expected)
        {
            var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (!Events.Contains(expected, StringComparer.Ordinal))
            {
                if (DateTime.UtcNow >= timeout)
                {
                    throw new TimeoutException($"Event '{expected}' was not observed.");
                }

                await Task.Yield();
            }
        }
    }
}
