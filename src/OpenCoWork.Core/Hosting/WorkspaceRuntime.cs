using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenCoWork.Abstractions;

namespace OpenCoWork.Core.Hosting;

public static class OpenCoWorkHostingExtensions
{
    public static IServiceCollection AddOpenCoWorkRuntime(
        this IServiceCollection services,
        ModuleRegistry registry,
        ModuleDescriptor primaryHost,
        TimeSpan stopTimeout)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(primaryHost);

        if (stopTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stopTimeout),
                stopTimeout,
                "Stop timeout must be greater than zero.");
        }

        if (services.Any(descriptor =>
                descriptor.ServiceType == typeof(ModuleLifecycleCoordinator)))
        {
            throw new ModuleRegistryException(
                "OCWMOD008",
                "The module lifecycle coordinator is already registered.");
        }

        var registeredPrimary = registry.StartupOrder.SingleOrDefault(module =>
            module.Id == primaryHost.Id);
        if (registeredPrimary is null ||
            !ReferenceEquals(registeredPrimary, primaryHost) ||
            !registeredPrimary.CanBePrimaryHost)
        {
            throw new ModuleRegistryException(
                "OCWMOD005",
                $"Module '{primaryHost.Id}' is not a registered primary host.");
        }

        var modules = ModuleLifecycleCoordinator.Activate(registry);
        foreach (var module in modules)
        {
            var hostedServiceCount = services.Count(descriptor =>
                descriptor.ServiceType == typeof(IHostedService));
            module.Module.ConfigureServices(services);
            if (services.Count(descriptor =>
                    descriptor.ServiceType == typeof(IHostedService)) != hostedServiceCount)
            {
                throw new ModuleRegistryException(
                    "OCWMOD009",
                    $"Module '{module.Descriptor.Id}' registered an independent IHostedService.");
            }

            services.AddSingleton(typeof(IOpenCoWorkModule), module.Module);
        }

        services.AddSingleton(registry);
        services.AddSingleton(serviceProvider =>
            new ModuleLifecycleCoordinator(
                modules,
                serviceProvider,
                primaryHost,
                stopTimeout));
        services.AddSingleton(serviceProvider =>
            serviceProvider.GetRequiredService<ModuleLifecycleCoordinator>().Runtime);
        services.AddSingleton<IModuleHealthReporter>(serviceProvider =>
            serviceProvider.GetRequiredService<WorkspaceRuntime>());
        services.AddSingleton<IHostedService>(serviceProvider =>
            serviceProvider.GetRequiredService<ModuleLifecycleCoordinator>());
        return services;
    }
}

public sealed class ModuleLifecycleCoordinator : IHostedService
{
    private readonly IReadOnlyList<ActivatedModule> _modules;
    private readonly IServiceProvider _services;
    private readonly TimeSpan _stopTimeout;
    private readonly List<ActivatedModule> _started = [];

    internal ModuleLifecycleCoordinator(
        ModuleRegistry registry,
        IServiceProvider services,
        ModuleDescriptor primaryHost,
        TimeSpan stopTimeout)
        : this(Activate(registry), services, primaryHost, stopTimeout)
    {
    }

    internal ModuleLifecycleCoordinator(
        IReadOnlyList<ActivatedModule> modules,
        IServiceProvider services,
        ModuleDescriptor primaryHost,
        TimeSpan stopTimeout)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(primaryHost);

        if (stopTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stopTimeout),
                stopTimeout,
                "Stop timeout must be greater than zero.");
        }

        _modules = modules;
        _services = services;
        _stopTimeout = stopTimeout;
        Runtime = new WorkspaceRuntime(this, services, primaryHost);
    }

    public WorkspaceRuntime Runtime { get; }

    internal bool HasPendingCleanup => _started.Count > 0;

    internal bool ContainsModule(string moduleId) =>
        _modules.Any(module => module.Descriptor.Id == moduleId);

    internal IReadOnlyList<string> ModuleIds =>
        _modules.Select(module => module.Descriptor.Id).ToArray();

    internal async Task StartModulesAsync(CancellationToken cancellationToken)
    {
        if (_started.Count != 0)
        {
            throw new WorkspaceRuntimeStateException(
                "OCWLIFE002",
                "Modules cannot start while cleanup is pending.");
        }

        try
        {
            foreach (var module in _modules)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await module.Module.StartAsync(_services, cancellationToken);
                _started.Add(module);
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception startupError)
        {
            var cleanupErrors = await StopModulesAsync();
            if (cleanupErrors.Count == 0)
            {
                ExceptionDispatchInfo.Capture(startupError).Throw();
            }

            throw new AggregateException(
                "Module startup failed and rollback reported errors.",
                new[] { startupError }.Concat(cleanupErrors));
        }
    }

    internal async Task<IReadOnlyList<Exception>> StopModulesAsync()
    {
        if (_started.Count == 0)
        {
            return [];
        }

        using var timeout = new CancellationTokenSource(_stopTimeout);
        var errors = new List<Exception>();

        foreach (var module in _started.ToArray().Reverse())
        {
            Task stopTask;
            try
            {
                stopTask = module.PendingStopTask ??
                    module.Module.StopAsync(_services, timeout.Token).AsTask();
                module.PendingStopTask = stopTask;
            }
            catch (Exception error)
            {
                errors.Add(new ModuleLifecycleException(
                    module.Descriptor.Id,
                    "stop",
                    error));
                continue;
            }

            try
            {
                await stopTask.WaitAsync(timeout.Token);
                module.PendingStopTask = null;
                _started.Remove(module);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                if (stopTask.IsCompletedSuccessfully)
                {
                    module.PendingStopTask = null;
                    _started.Remove(module);
                }
                else if (stopTask.IsCompleted)
                {
                    module.PendingStopTask = null;
                    if (stopTask.IsFaulted)
                    {
                        _ = stopTask.Exception;
                    }
                }
                else
                {
                    _ = stopTask.ContinueWith(
                        static task => _ = task.Exception,
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously |
                        TaskContinuationOptions.OnlyOnFaulted,
                        TaskScheduler.Default);
                }

                errors.Add(new TimeoutException(
                    $"Module '{module.Descriptor.Id}' did not stop within " +
                    $"runtime.stopTimeout ({_stopTimeout})."));
            }
            catch (Exception error)
            {
                module.PendingStopTask = null;
                errors.Add(new ModuleLifecycleException(
                    module.Descriptor.Id,
                    "stop",
                    error));
            }
        }

        return errors;
    }

    Task IHostedService.StartAsync(CancellationToken cancellationToken) =>
        Runtime.StartAsync(cancellationToken);

    Task IHostedService.StopAsync(CancellationToken cancellationToken) =>
        Runtime.StopAsync(cancellationToken);

    internal static IReadOnlyList<ActivatedModule> Activate(ModuleRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        return registry.StartupOrder
            .Select(descriptor =>
            {
                if (!typeof(IOpenCoWorkModule).IsAssignableFrom(descriptor.ModuleType) ||
                    descriptor.ModuleType.GetConstructor(Type.EmptyTypes) is null)
                {
                    throw new ModuleRegistryException(
                        "OCWMOD007",
                        $"Module type '{descriptor.ModuleType.FullName}' must implement " +
                        $"{nameof(IOpenCoWorkModule)} and have a public parameterless constructor.");
                }

                return new ActivatedModule(
                    descriptor,
                    (IOpenCoWorkModule)Activator.CreateInstance(descriptor.ModuleType)!);
            })
            .ToArray();
    }

    internal sealed class ActivatedModule(
        ModuleDescriptor descriptor,
        IOpenCoWorkModule module)
    {
        public ModuleDescriptor Descriptor { get; } = descriptor;

        public IOpenCoWorkModule Module { get; } = module;

        public Task? PendingStopTask { get; set; }
    }
}

public enum WorkspaceRuntimeStatus
{
    Stopped,
    Starting,
    Running,
    Degraded,
    Stopping,
    Faulted,
}

public sealed record WorkspaceRuntimeStartedState(
    IServiceProvider Services,
    ModuleDescriptor PrimaryHost,
    DateTimeOffset StartedAtUtc);

public sealed class WorkspaceRuntime : IAsyncDisposable, IModuleHealthReporter
{
    private readonly ModuleLifecycleCoordinator _coordinator;
    private readonly IServiceProvider _services;
    private readonly ModuleDescriptor _primaryHost;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly object _stateGate = new();
    private readonly Dictionary<string, string> _degradedModules =
        new(StringComparer.Ordinal);
    private WorkspaceRuntimeStartedState? _startedState;
    private int _status;
    private bool _disposed;

    internal WorkspaceRuntime(
        ModuleLifecycleCoordinator coordinator,
        IServiceProvider services,
        ModuleDescriptor primaryHost)
    {
        _coordinator = coordinator;
        _services = services;
        _primaryHost = primaryHost;
    }

    public WorkspaceRuntimeStatus Status =>
        (WorkspaceRuntimeStatus)Volatile.Read(ref _status);

    internal bool IsPrimaryHost(string moduleId) =>
        string.Equals(_primaryHost.Id, moduleId, StringComparison.Ordinal);

    internal (
        string PrimaryHost,
        string RuntimeStatus,
        IReadOnlyList<KeyValuePair<string, string>> Modules) ReadOperationsHealth()
    {
        lock (_stateGate)
        {
            var runtimeStatus = Status.ToString().ToLowerInvariant();
            return (
                _primaryHost.Id,
                runtimeStatus,
                _coordinator.ModuleIds.Select(moduleId =>
                    KeyValuePair.Create(
                        moduleId,
                        _degradedModules.ContainsKey(moduleId)
                            ? "degraded"
                            : Status switch
                            {
                                WorkspaceRuntimeStatus.Running or
                                    WorkspaceRuntimeStatus.Degraded => "healthy",
                                _ => runtimeStatus,
                            })).ToArray());
        }
    }

    public WorkspaceRuntimeStartedState StartedState
    {
        get
        {
            var startedState = Volatile.Read(ref _startedState);
            var status = Status;
            return startedState is not null &&
                   status is WorkspaceRuntimeStatus.Running or WorkspaceRuntimeStatus.Degraded
                ? startedState
                : throw new WorkspaceRuntimeStateException(
                    "OCWLIFE001",
                    $"Runtime services are unavailable while the runtime is {status}.");
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (Status != WorkspaceRuntimeStatus.Stopped)
            {
                throw new WorkspaceRuntimeStateException(
                    "OCWLIFE002",
                    $"Runtime cannot start while it is {Status}.");
            }

            lock (_stateGate)
            {
                _degradedModules.Clear();
                SetStatus(WorkspaceRuntimeStatus.Starting);
            }

            try
            {
                await _coordinator.StartModulesAsync(cancellationToken);
                var startedState = new WorkspaceRuntimeStartedState(
                    _services,
                    _primaryHost,
                    DateTimeOffset.UtcNow);

                lock (_stateGate)
                {
                    Volatile.Write(ref _startedState, startedState);
                    SetStatus(_degradedModules.Count == 0
                        ? WorkspaceRuntimeStatus.Running
                        : WorkspaceRuntimeStatus.Degraded);
                }
            }
            catch (OperationCanceledException) when (!_coordinator.HasPendingCleanup)
            {
                ResetUnavailableState(WorkspaceRuntimeStatus.Stopped);
                throw;
            }
            catch
            {
                ResetUnavailableState(WorkspaceRuntimeStatus.Faulted);
                throw;
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            await StopCoreAsync();
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public void ReportDegraded(string moduleId, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        lock (_stateGate)
        {
            ThrowIfDisposed();
            EnsureHealthSignalAllowed(moduleId);
            _degradedModules[moduleId] = reason;
            if (Status != WorkspaceRuntimeStatus.Starting)
            {
                SetStatus(WorkspaceRuntimeStatus.Degraded);
            }
        }
    }

    public void ClearDegraded(string moduleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);

        lock (_stateGate)
        {
            ThrowIfDisposed();
            EnsureHealthSignalAllowed(moduleId);
            _degradedModules.Remove(moduleId);
            if (Status != WorkspaceRuntimeStatus.Starting)
            {
                SetStatus(_degradedModules.Count == 0
                    ? WorkspaceRuntimeStatus.Running
                    : WorkspaceRuntimeStatus.Degraded);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleLock.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            await StopCoreAsync();
            _disposed = true;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        if (Status == WorkspaceRuntimeStatus.Stopped)
        {
            return;
        }

        if (Status is not (
            WorkspaceRuntimeStatus.Running or
            WorkspaceRuntimeStatus.Degraded or
            WorkspaceRuntimeStatus.Faulted))
        {
            throw new WorkspaceRuntimeStateException(
                "OCWLIFE002",
                $"Runtime cannot stop while it is {Status}.");
        }

        ResetUnavailableState(WorkspaceRuntimeStatus.Stopping);
        var errors = await _coordinator.StopModulesAsync();
        if (errors.Count == 0 && !_coordinator.HasPendingCleanup)
        {
            SetStatus(WorkspaceRuntimeStatus.Stopped);
            return;
        }

        SetStatus(WorkspaceRuntimeStatus.Faulted);
        throw new AggregateException("One or more modules failed to stop.", errors);
    }

    private void EnsureHealthSignalAllowed(string moduleId)
    {
        if (Status is not (
            WorkspaceRuntimeStatus.Starting or
            WorkspaceRuntimeStatus.Running or WorkspaceRuntimeStatus.Degraded))
        {
            throw new WorkspaceRuntimeStateException(
                "OCWLIFE001",
                $"Module health cannot change while the runtime is {Status}.");
        }

        if (!_coordinator.ContainsModule(moduleId))
        {
            throw new WorkspaceRuntimeStateException(
                "OCWLIFE003",
                $"Module '{moduleId}' is not registered.");
        }
    }

    private void ResetUnavailableState(WorkspaceRuntimeStatus status)
    {
        lock (_stateGate)
        {
            Volatile.Write(ref _startedState, null);
            _degradedModules.Clear();
            SetStatus(status);
        }
    }

    private void SetStatus(WorkspaceRuntimeStatus status) =>
        Volatile.Write(ref _status, (int)status);

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);
}

public sealed class WorkspaceRuntimeStateException : InvalidOperationException
{
    public WorkspaceRuntimeStateException(string code, string message)
        : base($"{code}: {message}")
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed class ModuleLifecycleException : Exception
{
    public ModuleLifecycleException(
        string moduleId,
        string operation,
        Exception innerException)
        : base(
            $"Module '{moduleId}' failed during {operation}.",
            innerException)
    {
        ModuleId = moduleId;
        Operation = operation;
    }

    public string ModuleId { get; }

    public string Operation { get; }
}
