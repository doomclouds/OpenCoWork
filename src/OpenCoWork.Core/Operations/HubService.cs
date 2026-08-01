using Microsoft.Data.Sqlite;
using OpenCoWork.Abstractions;

namespace OpenCoWork.Core.Operations;

internal sealed class HubService(
    IWorkspaceRegistryService registry,
    TimeProvider timeProvider) : IHubService
{
    public async Task<IReadOnlyList<HubWorkspaceSummary>> ListWorkspacesAsync(
        CancellationToken cancellationToken = default)
    {
        var registrations = await registry.ListAsync(cancellationToken);
        var items = new List<HubWorkspaceSummary>(registrations.Count);
        foreach (var registration in registrations)
        {
            items.Add(await ReadSummaryAsync(registration, cancellationToken));
        }
        return items;
    }

    public async Task<OperationsDashboardSnapshot?> GetDashboardAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default)
    {
        if (workspaceId.Version != 7)
        {
            throw new ArgumentException("Workspace id must be UUIDv7.", nameof(workspaceId));
        }

        var registration = (await registry.ListAsync(cancellationToken))
            .SingleOrDefault(item => item.WorkspaceId == workspaceId);
        if (registration is null || !TryGetDatabasePath(registration, out var databasePath))
        {
            return null;
        }

        try
        {
            await using var connection = CreateConnection(databasePath);
            await connection.OpenAsync(cancellationToken);
            return await OperationsQueryService.ReadDashboardAsync(
                connection,
                timeProvider.GetUtcNow(),
                cancellationToken);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return null;
        }
    }

    private async Task<HubWorkspaceSummary> ReadSummaryAsync(
        WorkspaceRegistration registration,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(registration.WorkspaceRoot) ||
            !Directory.Exists(registration.DataRoot))
        {
            return new HubWorkspaceSummary(
                registration,
                HubWorkspaceAvailability.Missing,
                null,
                "hub.workspaceMissing");
        }
        if (!TryGetDatabasePath(registration, out var databasePath))
        {
            return new HubWorkspaceSummary(
                registration,
                HubWorkspaceAvailability.Unavailable,
                null,
                "hub.stateUnavailable");
        }

        try
        {
            await using var connection = CreateConnection(databasePath);
            await connection.OpenAsync(cancellationToken);
            var heartbeat = await OperationsQueryService.ReadHeartbeatAsync(
                connection,
                timeProvider.GetUtcNow(),
                cancellationToken);
            return new HubWorkspaceSummary(
                registration,
                heartbeat?.Status switch
                {
                    null or OperationsHealthStatus.Stale => HubWorkspaceAvailability.Stale,
                    OperationsHealthStatus.Stopped => HubWorkspaceAvailability.Stopped,
                    _ => HubWorkspaceAvailability.Online,
                },
                heartbeat?.Status,
                heartbeat is null ? "hub.heartbeatMissing" : null);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return new HubWorkspaceSummary(
                registration,
                HubWorkspaceAvailability.Unavailable,
                null,
                "hub.stateUnavailable");
        }
    }

    private static SqliteConnection CreateConnection(string databasePath) =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());

    private static bool TryGetDatabasePath(
        WorkspaceRegistration registration,
        out string databasePath)
    {
        databasePath = Path.Combine(registration.DataRoot, "runtime", "state.db");
        return File.Exists(databasePath);
    }

    private static bool IsUnavailable(Exception exception) =>
        exception is SqliteException or IOException or UnauthorizedAccessException or
            InvalidDataException;
}
