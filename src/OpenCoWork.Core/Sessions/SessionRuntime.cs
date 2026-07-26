using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.State;
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
            new StateRuntime(
                serviceProvider.GetRequiredService<OpenCoWorkPaths>(),
                serviceProvider.GetRequiredService<RuntimeConfig>().State.BusyTimeout));
        services.TryAddSingleton<ThreadJournal>();
        services.TryAddSingleton<SessionProjection>();
        services.TryAddSingleton(serviceProvider =>
        {
            var executor = serviceProvider.GetService<ISessionExecutor>();
            return new SessionService(
                serviceProvider.GetRequiredService<StateRuntime>(),
                serviceProvider.GetRequiredService<ThreadJournal>(),
                serviceProvider.GetRequiredService<SessionProjection>(),
                serviceProvider.GetRequiredService<SessionConfig>(),
                serviceProvider.GetRequiredService<TimeProvider>(),
                executor,
                executor?.GetType().FullName);
        });
        services.TryAddSingleton<ISessionService>(serviceProvider =>
            serviceProvider.GetRequiredService<SessionService>());
        services.TryAddSingleton(serviceProvider =>
            new SessionRuntime(
                serviceProvider.GetRequiredService<StateRuntime>(),
                serviceProvider.GetRequiredService<SessionService>(),
                serviceProvider.GetRequiredService<SessionProjection>()));
        return services;
    }
}

public sealed class SessionRuntime
{
    private readonly StateRuntime _stateRuntime;
    private readonly SessionService _service;
    private readonly SessionProjection _projection;
    private IReadOnlyList<Guid> _recoveryRequiredThreadIds = [];

    internal SessionRuntime(
        StateRuntime stateRuntime,
        SessionService service,
        SessionProjection projection)
    {
        _stateRuntime = stateRuntime;
        _service = service;
        _projection = projection;
    }

    public bool IsDegraded => _projection.State == SessionProjectionState.Degraded;

    public IReadOnlyList<Guid> RecoveryRequiredThreadIds =>
        _recoveryRequiredThreadIds;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _stateRuntime.InitializeAsync(cancellationToken);
        _recoveryRequiredThreadIds =
            await _service.StartRuntimeAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _service.StopRuntimeAsync(cancellationToken);
        await _stateRuntime.CheckpointAsync(cancellationToken);
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
            if (_turns.TryGetValue(turnId, out var turn))
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
