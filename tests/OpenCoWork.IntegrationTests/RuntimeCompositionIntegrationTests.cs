using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenCoWork.Abstractions;
using OpenCoWork.App;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Hosting;
using OpenCoWork.Core.State;
using OpenCoWork.Core.Workspaces;
using Xunit;

namespace OpenCoWork.IntegrationTests;

public sealed class RuntimeCompositionIntegrationTests
{
    [Fact]
    public async Task Production_composition_recovers_session_before_cli_and_stops_cleanly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-session-runtime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            Guid threadId;
            using (var host = OpenCoWorkCompositionRoot.Build([], root))
            {
                var modules = host.Services.GetServices<IOpenCoWorkModule>().ToArray();
                Assert.Collection(
                    modules,
                    module => Assert.IsType<SessionModule>(module),
                    module => Assert.IsType<CliModule>(module));
                Assert.IsType<ModuleLifecycleCoordinator>(
                    Assert.Single(host.Services.GetServices<IHostedService>()));

                await host.StartAsync(cancellationToken);

                var runtime = host.Services.GetRequiredService<WorkspaceRuntime>();
                Assert.Equal(WorkspaceRuntimeStatus.Running, runtime.Status);
                Assert.Equal("cli", runtime.StartedState.PrimaryHost.Id);
                Assert.Equal(
                    ["session", "cli"],
                    host.Services.GetRequiredService<ModuleRegistry>()
                        .StartupOrder.Select(module => module.Id));

                var service = host.Services.GetRequiredService<ISessionService>();
                var created = await service.CreateThreadAsync(
                    new CreateThreadRequest(
                        Guid.CreateVersion7(),
                        ExpectedSequence: 0,
                        DisplayName: "runtime recovery"),
                    cancellationToken);
                threadId = created.Value!.ThreadId;

                await host.StopAsync(cancellationToken);
                Assert.Equal(WorkspaceRuntimeStatus.Stopped, runtime.Status);
                var rejected = await service.CreateThreadAsync(
                    new CreateThreadRequest(
                        Guid.CreateVersion7(),
                        ExpectedSequence: 0,
                        DisplayName: "after stop"),
                    cancellationToken);
                Assert.Equal(SessionErrorCodes.RuntimeShuttingDown, rejected.Error?.Code);
            }

            using var restarted = OpenCoWorkCompositionRoot.Build([], root);
            await restarted.StartAsync(cancellationToken);
            var recovered = await restarted.Services
                .GetRequiredService<ISessionService>()
                .GetThreadAsync(threadId, cancellationToken);
            Assert.Equal("runtime recovery", recovered.Value!.DisplayName);
            await restarted.StopAsync(cancellationToken);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var paths = new OpenCoWorkPaths(root);
            if (Directory.Exists(paths.OpenCoWorkDirectory))
            {
                Directory.Delete(paths.OpenCoWorkDirectory, recursive: true);
            }

            Directory.Delete(root);
        }
    }

    [Fact]
    public async Task Unsupported_state_schema_faults_the_workspace_runtime()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-session-schema-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var paths = new OpenCoWorkPaths(root);
        try
        {
            var state = new StateRuntime(paths, TimeSpan.FromSeconds(2));
            await state.InitializeAsync(cancellationToken);
            await using (var connection =
                         await state.OpenReadWriteConnectionAsync(cancellationToken))
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    UPDATE state_info
                    SET schema_version = 99,
                        target_version = 99,
                        migration_status = 'Completed'
                    WHERE id = 1;
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            using var host = OpenCoWorkCompositionRoot.Build([], root);
            var runtime = host.Services.GetRequiredService<WorkspaceRuntime>();
            await Assert.ThrowsAsync<StateMigrationException>(
                () => host.StartAsync(cancellationToken));
            Assert.Equal(WorkspaceRuntimeStatus.Faulted, runtime.Status);
            await runtime.StopAsync(cancellationToken);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(paths.OpenCoWorkDirectory))
            {
                Directory.Delete(paths.OpenCoWorkDirectory, recursive: true);
            }

            Directory.Delete(root);
        }
    }
}
