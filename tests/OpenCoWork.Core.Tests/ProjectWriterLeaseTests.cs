using Microsoft.Data.Sqlite;
using OpenCoWork.Abstractions;
using OpenCoWork.Automations;
using OpenCoWork.Core.State;
using OpenCoWork.Core.Workspaces;
using OpenCoWork.Teams;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class ProjectWriterLeaseTests
{
    [Fact]
    public async Task Lease_uses_owner_and_lease_id_compare_and_swap()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var clock = new ManualTimerTimeProvider(
            new DateTimeOffset(2026, 7, 30, 8, 0, 0, TimeSpan.Zero));
        var service = await CreateServiceAsync(files, clock, cancellationToken);
        var coWork = new ProjectWriterLeaseOwner(
            ProjectWriterLeaseOwnerKind.CoWorkAgentRun,
            Guid.CreateVersion7());
        var automation = new ProjectWriterLeaseOwner(
            ProjectWriterLeaseOwnerKind.AutomationRun,
            Guid.CreateVersion7());

        var acquired = Assert.IsType<ProjectWriterLease>(
            await service.TryAcquireAsync(coWork, cancellationToken));
        var replay = Assert.IsType<ProjectWriterLease>(
            await service.TryAcquireAsync(coWork, cancellationToken));
        Assert.Equal(acquired, replay);
        Assert.Null(await service.TryAcquireAsync(automation, cancellationToken));
        Assert.Null(await service.RenewAsync(
            automation,
            acquired.LeaseId,
            cancellationToken));
        Assert.False(await service.ReleaseAsync(
            coWork,
            Guid.CreateVersion7(),
            cancellationToken));

        clock.Advance(ProjectWriterLeaseLimits.RenewalInterval);
        var renewed = Assert.IsType<ProjectWriterLease>(
            await service.RenewAsync(coWork, acquired.LeaseId, cancellationToken));
        Assert.Equal(acquired.LeaseId, renewed.LeaseId);
        Assert.True(renewed.ExpiresAtUtc > acquired.ExpiresAtUtc);
        Assert.True(await service.ReleaseAsync(
            coWork,
            acquired.LeaseId,
            cancellationToken));
        Assert.NotNull(await service.TryAcquireAsync(automation, cancellationToken));
    }

    [Fact]
    public async Task Expired_lease_can_be_taken_over_and_lost_owner_cannot_renew()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var clock = new ManualTimerTimeProvider(
            new DateTimeOffset(2026, 7, 30, 8, 0, 0, TimeSpan.Zero));
        var service = await CreateServiceAsync(files, clock, cancellationToken);
        var firstOwner = new ProjectWriterLeaseOwner(
            ProjectWriterLeaseOwnerKind.CoWorkAgentRun,
            Guid.CreateVersion7());
        var nextOwner = new ProjectWriterLeaseOwner(
            ProjectWriterLeaseOwnerKind.AutomationRun,
            Guid.CreateVersion7());
        var first = Assert.IsType<ProjectWriterLease>(
            await service.TryAcquireAsync(firstOwner, cancellationToken));

        clock.Advance(ProjectWriterLeaseLimits.LeaseDuration);
        var next = Assert.IsType<ProjectWriterLease>(
            await service.TryAcquireAsync(nextOwner, cancellationToken));

        Assert.NotEqual(first.LeaseId, next.LeaseId);
        Assert.Null(await service.RenewAsync(
            firstOwner,
            first.LeaseId,
            cancellationToken));
        Assert.False(await service.ReleaseAsync(
            firstOwner,
            first.LeaseId,
            cancellationToken));
    }

    [Fact]
    public async Task Concurrent_acquire_has_one_winner()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var service = await CreateServiceAsync(
            files,
            TimeProvider.System,
            cancellationToken);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 16)
                .Select(_ => service.TryAcquireAsync(
                        new ProjectWriterLeaseOwner(
                            ProjectWriterLeaseOwnerKind.AutomationRun,
                            Guid.CreateVersion7()),
                        cancellationToken)
                    .AsTask()));

        Assert.Single(results, result => result is not null);
    }

    private static async Task<ProjectWriterLeaseService> CreateServiceAsync(
        TempWorkspace files,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var runtime = new StateRuntime(
            files.Paths,
            TimeSpan.FromSeconds(2),
            [
                .. TeamsStateMigrationContributors.Create(),
                .. AutomationsStateMigrationContributors.Create(),
            ]);
        await runtime.InitializeAsync(cancellationToken);
        return new ProjectWriterLeaseService(runtime, clock);
    }

    private sealed class TempWorkspace : IDisposable
    {
        public TempWorkspace()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"opencowork-writer-lease-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            Paths = new OpenCoWorkPaths(Root);
        }

        public string Root { get; }

        public OpenCoWorkPaths Paths { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
        }
    }
}
