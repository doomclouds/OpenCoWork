using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Agents;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Gateway;
using OpenCoWork.Core.Hosting;
using OpenCoWork.Core.Logging;
using OpenCoWork.Core.Operations;
using OpenCoWork.Core.State;
using OpenCoWork.Core.Tools;
using OpenCoWork.Core.Workspaces;

namespace OpenCoWork.Core.Sessions;

public static class OpenCoWorkSessionExtensions
{
    public static IServiceCollection AddOpenCoWorkSessionRuntime(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(static _ =>
            WorkspaceDiscovery.Discover(Environment.CurrentDirectory));
        services.TryAddSingleton<RuntimeConfig>();
        services.TryAddSingleton<SessionConfig>();
        services.TryAddSingleton(static _ => TimeProvider.System);
        services.TryAddSingleton(serviceProvider =>
        {
            var paths = serviceProvider.GetRequiredService<OpenCoWorkPaths>();
            var busyTimeout =
                serviceProvider.GetRequiredService<RuntimeConfig>().State.BusyTimeout;
            var contributors = serviceProvider
                .GetServices<IWorkspaceStateMigrationContributor>()
                .ToArray();
            return contributors.Length == 0
                ? new StateRuntime(paths, busyTimeout)
                : new StateRuntime(paths, busyTimeout, contributors);
        });
        services.TryAddSingleton<IWorkspaceStateStore>(serviceProvider =>
            serviceProvider.GetRequiredService<StateRuntime>());
        services.TryAddSingleton<IOperationsQueryService, OperationsQueryService>();
        services.TryAddSingleton<OperationsChangeHub>();
        services.TryAddSingleton<IOperationsChangeSource>(serviceProvider =>
            serviceProvider.GetRequiredService<OperationsChangeHub>());
        services.TryAddSingleton<OperationsTraceCollector>();
        services.TryAddSingleton(serviceProvider =>
            serviceProvider.GetService<WorkspaceRegistryRoot>() is { } root
                ? new WorkspaceRegistryService(
                    root.UserProfileDirectory,
                    serviceProvider.GetRequiredService<TimeProvider>())
                : new WorkspaceRegistryService(
                    serviceProvider.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<IWorkspaceRegistryService>(serviceProvider =>
            serviceProvider.GetRequiredService<WorkspaceRegistryService>());
        services.TryAddSingleton<WorkspaceInsightService>();
        services.TryAddSingleton<IWorkspaceInsightService>(serviceProvider =>
            serviceProvider.GetRequiredService<WorkspaceInsightService>());
        services.TryAddSingleton<HubService>();
        services.TryAddSingleton<IHubService>(serviceProvider =>
            serviceProvider.GetRequiredService<HubService>());
        services.TryAddSingleton(serviceProvider =>
        {
            var runtime = serviceProvider.GetRequiredService<WorkspaceRuntime>();
            return new OperationsRuntime(
                serviceProvider.GetRequiredService<StateRuntime>(),
                serviceProvider.GetRequiredService<OperationsTraceCollector>(),
                serviceProvider.GetRequiredService<IWorkspaceRegistryService>(),
                serviceProvider.GetRequiredService<OpenCoWorkPaths>(),
                serviceProvider.GetRequiredService<TimeProvider>(),
                () =>
                {
                    var health = runtime.ReadOperationsHealth();
                    return new OperationsRuntimeHealth(
                        health.PrimaryHost,
                        health.RuntimeStatus,
                        health.Modules.Select(module =>
                            new OperationsModuleHealth(module.Key, module.Value)).ToArray(),
                        serviceProvider.GetService<GatewayReconciler>()
                            ?.LastSuccessfulReconcileAtUtc);
                },
                serviceProvider.GetRequiredService<IWorkspaceInsightService>(),
                serviceProvider.GetRequiredService<OperationsChangeHub>());
        });
        services.TryAddSingleton<ProjectWriterLeaseService>();
        services.TryAddSingleton<IProjectWriterLeaseService>(serviceProvider =>
            serviceProvider.GetRequiredService<ProjectWriterLeaseService>());
        services.TryAddSingleton<JsonSchemaValidationService>();
        services.TryAddSingleton<IJsonSchemaValidationService>(serviceProvider =>
            serviceProvider.GetRequiredService<JsonSchemaValidationService>());
        services.TryAddSingleton<PreparedAutomationTurnStore>();
        services.TryAddSingleton<IAutomationPreparedTurnStore>(serviceProvider =>
            serviceProvider.GetRequiredService<PreparedAutomationTurnStore>());
        services.TryAddSingleton(serviceProvider =>
        {
            var paths = serviceProvider.GetRequiredService<OpenCoWorkPaths>();
            return new WorkspaceRuntimeDescriptor(
                paths.WorkspaceRoot,
                paths.OpenCoWorkDirectory,
                paths.RuntimeDirectory,
                paths.TeamsRuntimeDirectory,
                paths.MissionsDirectory,
                paths.SubAgentsDirectory,
                paths.WorktreesDirectory);
        });
        services.TryAddSingleton<IManagedWorktreeService>(serviceProvider =>
            new ManagedWorktreeService(
                serviceProvider.GetRequiredService<OpenCoWorkPaths>(),
                serviceProvider.GetService<CoreSourceControlTool>()));
        services.TryAddSingleton(serviceProvider =>
            new BackgroundTerminalRuntime(
                serviceProvider.GetRequiredService<OpenCoWorkPaths>(),
                serviceProvider.GetRequiredService<StateRuntime>(),
                serviceProvider.GetRequiredService<SecretRedactor>()));
        services.TryAddSingleton(serviceProvider =>
            new WorkspaceMemoryRuntime(
                serviceProvider.GetRequiredService<OpenCoWorkPaths>(),
                serviceProvider.GetRequiredService<StateRuntime>()));
        services.TryAddSingleton<ThreadJournal>();
        services.TryAddSingleton<SessionProjection>();
        services.TryAddSingleton(serviceProvider =>
        {
            var executor = serviceProvider.GetService<ISessionExecutor>();
            Func<string, string, SessionError?>? validateProviderModel = null;
            if (serviceProvider.GetService<ProviderRegistry>() is { } providers)
            {
                validateProviderModel = (providerId, modelId) =>
                {
                    try
                    {
                        providers.Resolve(providerId, modelId);
                        return null;
                    }
                    catch (AgentPreparationException exception)
                    {
                        return new SessionError(
                            exception.Code,
                            exception.Message,
                            IsRetryable: false);
                    }
                };
            }

            return new SessionService(
                serviceProvider.GetRequiredService<StateRuntime>(),
                serviceProvider.GetRequiredService<ThreadJournal>(),
                serviceProvider.GetRequiredService<SessionProjection>(),
                serviceProvider.GetRequiredService<SessionConfig>(),
                serviceProvider.GetRequiredService<TimeProvider>(),
                executor,
                executor?.GetType().FullName,
                providerModelValidator: validateProviderModel,
                terminal: serviceProvider.GetService<BackgroundTerminalRuntime>(),
                paths: serviceProvider.GetRequiredService<OpenCoWorkPaths>());
        });
        services.TryAddSingleton<ISessionService>(serviceProvider =>
            serviceProvider.GetRequiredService<SessionService>());
        services.TryAddSingleton(serviceProvider =>
            new SessionRuntime(
                serviceProvider.GetRequiredService<StateRuntime>(),
                serviceProvider.GetRequiredService<SessionService>(),
                serviceProvider.GetRequiredService<SessionProjection>(),
                serviceProvider.GetService<BackgroundTerminalRuntime>()));
        return services;
    }
}

public sealed class SessionRuntime
{
    private readonly StateRuntime _stateRuntime;
    private readonly SessionService _service;
    private readonly SessionProjection _projection;
    private readonly BackgroundTerminalRuntime? _terminal;
    private IReadOnlyList<Guid> _recoveryRequiredThreadIds = [];

    internal SessionRuntime(
        StateRuntime stateRuntime,
        SessionService service,
        SessionProjection projection,
        BackgroundTerminalRuntime? terminal = null)
    {
        _stateRuntime = stateRuntime;
        _service = service;
        _projection = projection;
        _terminal = terminal;
    }

    public bool IsDegraded => _projection.State == SessionProjectionState.Degraded;

    public IReadOnlyList<Guid> RecoveryRequiredThreadIds =>
        _recoveryRequiredThreadIds;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _stateRuntime.InitializeAsync(cancellationToken);
        if (_terminal is not null)
        {
            await _terminal.InitializeAsync(cancellationToken);
        }

        _recoveryRequiredThreadIds =
            await _service.StartRuntimeAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var errors = new List<Exception>();
        try
        {
            if (_terminal is not null)
            {
                await _terminal.StopAllAsync(CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }

        try
        {
            await _service.StopRuntimeAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }

        try
        {
            await _stateRuntime.CheckpointAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }

        if (errors.Count != 0)
        {
            throw new AggregateException("Session runtime cleanup failed.", errors);
        }
    }
}

internal sealed partial class SessionService
{
    internal async Task<IReadOnlyList<Guid>> StartRuntimeAsync(
        CancellationToken cancellationToken)
    {
        Volatile.Write(ref _acceptingWork, 0);
        var failures = (await RecoverSessionStateAsync(cancellationToken)).ToHashSet();
        var activeThreadIds = _journal.ListThreadIds(ThreadJournalLocation.Active);
        foreach (var threadId in activeThreadIds)
        {
            if (failures.Contains(threadId))
            {
                continue;
            }

            try
            {
                await RecoverExecutionAsync(threadId, cancellationToken);
            }
            catch
            {
                failures.Add(threadId);
                var snapshot = await _projection.ReadThreadSnapshotAsync(
                    threadId,
                    cancellationToken);
                if (snapshot is not null)
                {
                    MarkRecoveryRequired(snapshot, "Thread execution recovery failed.");
                }
            }
        }

        await ProcessInteractionTimeoutsAsync(cancellationToken);
        if (_projection.CanAcceptNewWork)
        {
            Volatile.Write(ref _acceptingWork, 1);
            foreach (var threadId in activeThreadIds.Where(id => !failures.Contains(id)))
            {
                await TryScheduleNextAsync(threadId, CancellationToken.None);
            }
        }

        return failures.Order().ToArray();
    }

    internal async Task StopRuntimeAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _acceptingWork, 0);
        _eventChannel.Complete();
        var executions = _executionRuns.Keys.ToArray();
        foreach (var turnId in executions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_turns.TryGetValue(turnId, out var turn) &&
                turn.Status is not (
                    TurnStatus.Completed or
                    TurnStatus.Failed or
                    TurnStatus.Cancelled))
            {
                await StopExecutionWithErrorAsync(
                    turn.ThreadId,
                    turnId,
                    new SessionError(
                        SessionErrorCodes.RuntimeInterrupted,
                        "Execution stopped with the session runtime.",
                        IsRetryable: false));
            }
        }

        var pending = _executionTasks.Values.ToArray();
        if (pending.Length != 0)
        {
            await Task.WhenAll(pending).WaitAsync(cancellationToken);
        }
    }
}
