using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenCoWork.Abstractions;
using OpenCoWork.App;
using OpenCoWork.Automations;
using OpenCoWork.Core.Capabilities;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Gateway;
using OpenCoWork.Core.Hosting;
using OpenCoWork.Core.Logging;
using OpenCoWork.Core.Operations;
using OpenCoWork.Core.State;
using OpenCoWork.Core.Workspaces;
using OpenCoWork.Protocol;
using OpenCoWork.Teams;
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
                    module => Assert.IsType<AcpModule>(module),
                    module => Assert.IsType<AppServerModule>(module),
                    module => Assert.IsType<AutomationsModule>(module),
                    module => Assert.IsType<CliModule>(module),
                    module => Assert.IsType<GatewayModule>(module),
                    module => Assert.IsType<TeamsModule>(module));
                Assert.IsType<ModuleLifecycleCoordinator>(
                    Assert.Single(host.Services.GetServices<IHostedService>()));

                await host.StartAsync(cancellationToken);

                var runtime = host.Services.GetRequiredService<WorkspaceRuntime>();
                Assert.Equal(WorkspaceRuntimeStatus.Running, runtime.Status);
                Assert.Equal("cli", runtime.StartedState.PrimaryHost.Id);
                Assert.Same(
                    host.Services.GetRequiredService<CoWorkService>(),
                    host.Services.GetRequiredService<ICoWorkService>());
                Assert.Same(
                    host.Services.GetRequiredService<SecretRedactor>(),
                    host.Services.GetRequiredService<ISensitiveDataService>());
                Assert.Equal(
                    ["session", "acp", "app-server", "automations", "cli", "gateway", "teams"],
                    host.Services.GetRequiredService<ModuleRegistry>()
                        .StartupOrder.Select(module => module.Id));
                var capabilities = host.Services
                    .GetRequiredService<WorkspaceCapabilityRuntime>();
                Assert.Equal(CapabilityRuntimeState.Ready, capabilities.Status);
                Assert.Contains(
                    capabilities.CurrentCatalog.Items,
                    item => item is
                    {
                        Kind: CapabilityKind.Tool,
                        Id: "file.read",
                        Status: CapabilityStatus.Ready,
                    });
                var capabilityService = host.Services
                    .GetRequiredService<ICapabilityService>();
                var page = await capabilityService.GetCatalogAsync(
                    new CapabilityCatalogQuery(Limit: 1),
                    cancellationToken);
                Assert.Equal(capabilities.CurrentCatalog.Revision, page.Revision);
                Assert.Single(page.Items);
                Assert.NotNull(page.NextCursor);
                var entry = await capabilityService.ReadAsync(
                    new CapabilityIdentity(
                        page.Items[0].Kind,
                        page.Items[0].Id),
                    cancellationToken);
                Assert.Equal(page.Revision, entry.Revision);
                var conflict = await Assert.ThrowsAsync<CapabilityServiceException>(
                    () => capabilityService.RefreshAsync(
                            page.Revision + 1,
                            cancellationToken)
                        .AsTask());
                Assert.Equal(CapabilityErrorCodes.RevisionConflict, conflict.Code);
                Assert.Equal(page.Revision, conflict.CurrentRevision);
                var unchanged = await capabilityService.RefreshAsync(
                    page.Revision,
                    cancellationToken);
                Assert.False(unchanged.Changed);
                var preserved = await capabilityService.SetEnabledAsync(
                    new CapabilitySetEnabledRequest(
                        CapabilityKind.Skill,
                        "unknown/preserved",
                        Enabled: false,
                        ExpectedRevision: page.Revision),
                    cancellationToken);
                Assert.False(preserved.Changed);
                Assert.Contains(
                    "unknown/preserved",
                    await File.ReadAllTextAsync(
                        new OpenCoWorkPaths(root).CapabilitiesPath,
                        cancellationToken),
                    StringComparison.Ordinal);

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
                Assert.Equal(CapabilityRuntimeState.Stopped, capabilities.Status);
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
    public async Task Gateway_RuntimeLifecycle_writes_and_stops_the_workspace_heartbeat()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-gateway-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var host = OpenCoWorkCompositionRoot.Build(
                [],
                root,
                services => AddTestWorkspaceRegistry(services, root),
                primaryModuleId: "gateway");
            Assert.IsType<ModuleLifecycleCoordinator>(
                Assert.Single(host.Services.GetServices<IHostedService>()));

            await host.StartAsync(cancellationToken);

            var runtime = host.Services.GetRequiredService<WorkspaceRuntime>();
            Assert.Equal("gateway", runtime.StartedState.PrimaryHost.Id);
            Assert.Equal(WorkspaceRuntimeStatus.Running, runtime.Status);
            Assert.True(
                host.Services.GetRequiredService<GatewayReconciler>().IsRunning);
            var operations = host.Services.GetRequiredService<IOperationsQueryService>();
            var heartbeat = await operations.GetHeartbeatAsync(cancellationToken);
            Assert.NotNull(heartbeat);
            Assert.Equal("gateway", heartbeat.PrimaryHost);
            Assert.Equal(OperationsHealthStatus.Healthy, heartbeat.Status);

            await host.StopAsync(cancellationToken);
            Assert.Equal(WorkspaceRuntimeStatus.Stopped, runtime.Status);
            Assert.False(
                host.Services.GetRequiredService<GatewayReconciler>().IsRunning);
            Assert.Equal(
                OperationsHealthStatus.Stopped,
                (await operations.GetHeartbeatAsync(cancellationToken))!.Status);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AppServer_RuntimeLifecycle_writes_and_stops_the_workspace_heartbeat()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-app-server-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var host = OpenCoWorkCompositionRoot.Build(
                [],
                root,
                services => AddTestWorkspaceRegistry(services, root),
                primaryModuleId: "app-server");
            await host.StartAsync(cancellationToken);

            var operations = host.Services.GetRequiredService<IOperationsQueryService>();
            var heartbeat = await operations.GetHeartbeatAsync(cancellationToken);
            Assert.NotNull(heartbeat);
            Assert.Equal("app-server", heartbeat.PrimaryHost);
            Assert.False(host.Services.GetRequiredService<GatewayReconciler>().IsRunning);

            await host.StopAsync(cancellationToken);
            Assert.Equal(
                OperationsHealthStatus.Stopped,
                (await operations.GetHeartbeatAsync(cancellationToken))!.Status);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Gateway_primary_opens_and_closes_the_configured_loopback_intake()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-gateway-intake-{Guid.NewGuid():N}");
        var user = Path.Combine(root, "user");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(user);
        var port = UnusedLoopbackPort();
        var paths = new OpenCoWorkPaths(root);
        var channel = new GatewayChannelConfig
        {
            Id = "integration",
            CallbackUrl = "https://integration.example.test/result",
            Credential = new GatewayCredentialConfig
            {
                Source = GatewayCredentialSource.Environment,
                EnvironmentVariable = "INTEGRATION_SECRET",
            },
        };
        var config = new GatewayConfig { ListenPort = port, Channels = [channel] };
        var persistencePaths = new CapabilityPersistencePaths(paths, user);
        var trust = new CapabilityFileStore(persistencePaths);
        await trust.SaveTrustDecisionsAsync(
            new TrustDecisionsDocument(
                1,
                [
                    new CapabilityTrustDecision(
                        root,
                        CapabilitySourceKind.Workspace,
                        "channel/integration",
                        "1",
                        GatewayConfig.ComputeChannelSha256(channel),
                        [CapabilityTrustScope.ExternalChannel],
                        []),
                ]),
            cancellationToken);
        try
        {
            using var host = OpenCoWorkCompositionRoot.Build(
                [],
                root,
                services =>
                {
                    AddTestWorkspaceRegistry(services, root);
                    services.AddSingleton(config);
                    services.AddSingleton(persistencePaths);
                    services.AddSingleton(trust);
                    services.AddSingleton(new ChannelCredentialService(
                        new InMemoryOsSecretStore(),
                        new SecretRedactor([]),
                        paths,
                        _ => "integration-secret"));
                },
                primaryModuleId: "gateway");

            await host.StartAsync(cancellationToken);
            Assert.True(await CanConnectAsync(port, cancellationToken));

            await host.StopAsync(cancellationToken);
            Assert.False(await CanConnectAsync(port, cancellationToken));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ChannelMedia_and_Webhook_sender_share_the_gateway_module_lifecycle()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-gateway-composition-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var host = OpenCoWorkCompositionRoot.Build(
                [],
                root,
                services => AddTestWorkspaceRegistry(services, root),
                primaryModuleId: "gateway");

            Assert.IsType<GatewayMediaStore>(
                host.Services.GetRequiredService<GatewayMediaStore>());
            Assert.IsType<WebhookChannelSender>(
                host.Services.GetRequiredService<IChannelSender>());
            Assert.IsType<ModuleLifecycleCoordinator>(
                Assert.Single(host.Services.GetServices<IHostedService>()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static int UnusedLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void AddTestWorkspaceRegistry(
        IServiceCollection services,
        string root)
    {
        var userRoot = Path.Combine(root, "user");
        Directory.CreateDirectory(userRoot);
        services.AddSingleton<IWorkspaceRegistryService>(
            new WorkspaceRegistryService(userRoot, TimeProvider.System));
    }

    private static async Task<bool> CanConnectAsync(
        int port,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    [Fact]
    public async Task GatewayCorrelation_dispatches_into_the_real_session_queue()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-gateway-dispatch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var host = OpenCoWorkCompositionRoot.Build(
                [],
                root,
                services => AddTestWorkspaceRegistry(services, root),
                primaryModuleId: "gateway");
            await host.StartAsync(cancellationToken);
            var state = host.Services.GetRequiredService<IWorkspaceStateStore>();
            await state.WriteAsync(
                async (connection, transaction, token) =>
                {
                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText =
                        """
                        INSERT INTO channels (
                            channel_id, kind, enabled, definition_sha256,
                            trust_status, runtime_status, revision, created_utc, updated_utc)
                        VALUES (
                            'integration', 'webhook', 1,
                            'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                            'trusted', 'ready', 1, 1, 1);
                        """;
                    await command.ExecuteNonQueryAsync(token);
                    return true;
                },
                cancellationToken);

            var service = host.Services.GetRequiredService<GatewayService>();
            Assert.Same(
                service,
                host.Services.GetRequiredService<IChannelInboundSink>());
            var receipt = await service.AcceptAsync(
                new ChannelInboundRequest(
                    "integration",
                    "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                    new ChannelInboundEnvelope(
                        1,
                        "message-1",
                        "conversation-1",
                        DateTimeOffset.UtcNow,
                        "hello from gateway",
                        [])),
                cancellationToken);

            _ = await service.DispatchPendingAsync(
                Guid.CreateVersion7(),
                1,
                cancellationToken);
            string? projectedCorrelation = null;
            for (var attempt = 0; attempt < 250 && projectedCorrelation is null; attempt++)
            {
                projectedCorrelation = await state.ReadAsync<string?>(
                    async (connection, token) =>
                    {
                        await using var command = connection.CreateCommand();
                        command.CommandText =
                            """
                            SELECT coalesce(
                                (SELECT json_extract(q.payload_json, '$.correlationId')
                                 FROM turn_queue q WHERE q.thread_id = i.thread_id LIMIT 1),
                                (SELECT t.correlation_id FROM turns t
                                 WHERE t.thread_id = i.thread_id
                                 ORDER BY t.created_utc DESC LIMIT 1))
                            FROM channel_inbound_messages i
                            WHERE i.inbound_message_id = $inboundId;
                            """;
                        var parameter = command.CreateParameter();
                        parameter.ParameterName = "$inboundId";
                        parameter.Value = receipt.ReceiptId.ToString("D");
                        command.Parameters.Add(parameter);
                        return await command.ExecuteScalarAsync(token) as string;
                    },
                    cancellationToken);
                if (projectedCorrelation is null)
                {
                    await Task.Delay(20, cancellationToken);
                }
            }

            Assert.Equal(receipt.CorrelationId.ToString("D"), projectedCorrelation);
            await host.StopAsync(cancellationToken);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Capability_start_failure_is_cleaned_before_workspace_faults()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-capability-start-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var source = new CapabilitySourceDescriptor(
            CapabilitySourceKind.Core,
            "opencowork.core",
            "1",
            new string('a', 64));
        var duplicate = new CapabilityContribution(
            CapabilityKind.Tool,
            "duplicate",
            "duplicate",
            "Duplicate core capability.",
            CapabilityStatus.Ready,
            [],
            generation: 1,
            []);
        var capabilities = new WorkspaceCapabilityRuntime(
        [
            new CapabilityContributionSet(source, [duplicate, duplicate]),
        ]);
        try
        {
            using var host = OpenCoWorkCompositionRoot.Build(
                [],
                root,
                services => services.AddSingleton(capabilities));
            var workspace = host.Services.GetRequiredService<WorkspaceRuntime>();

            await Assert.ThrowsAsync<CapabilityRuntimeException>(
                () => host.StartAsync(cancellationToken));

            Assert.Equal(WorkspaceRuntimeStatus.Faulted, workspace.Status);
            Assert.Equal(CapabilityRuntimeState.Stopped, capabilities.Status);
            await workspace.StopAsync(cancellationToken);
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
