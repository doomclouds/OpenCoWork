using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using OpenCoWork.Abstractions;
using OpenCoWork.App;
using OpenCoWork.Core.Capabilities;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Gateway;
using OpenCoWork.Core.Logging;
using OpenCoWork.Core.Operations;
using OpenCoWork.Core.Workspaces;
using Xunit;

namespace OpenCoWork.IntegrationTests;

public sealed class GatewayOutboxIntegrationTests
{
    [Fact]
    public async Task Real_session_terminal_is_captured_and_sent_once()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-gateway-outbox-integration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var user = Path.Combine(root, "user");
        Directory.CreateDirectory(user);
        var paths = new OpenCoWorkPaths(root);
        var channel = new GatewayChannelConfig
        {
            Id = "integration",
            CallbackUrl = "https://integration.example.test/result",
            Credential = new GatewayCredentialConfig
            {
                Source = GatewayCredentialSource.Environment,
                EnvironmentVariable = "TEST_SECRET",
            },
        };
        var config = new GatewayConfig
        {
            ListenPort = UnusedLoopbackPort(),
            Channels = [channel],
        };
        var sender = new RecordingSender();
        var credentials = new ChannelCredentialService(
            new InMemoryOsSecretStore(),
            new SecretRedactor([]),
            paths,
            _ => "integration-secret");
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
                    services.AddSingleton<IWorkspaceRegistryService>(
                        new WorkspaceRegistryService(user, TimeProvider.System));
                    services.AddSingleton(config);
                    services.AddSingleton(credentials);
                    services.AddSingleton(persistencePaths);
                    services.AddSingleton(trust);
                    services.AddSingleton<IChannelSender>(sender);
                },
                primaryModuleId: "gateway");
            await host.StartAsync(cancellationToken);
            var state = host.Services.GetRequiredService<IWorkspaceStateStore>();

            var sessions = host.Services.GetRequiredService<ISessionService>();
            var created = await sessions.CreateThreadAsync(
                new CreateThreadRequest(Guid.CreateVersion7(), 0, "gateway integration"),
                cancellationToken);
            var threadId = created.Value!.ThreadId;
            var completed = await sessions.AppendCompletedAgentTurnAsync(
                new AppendCompletedAgentTurnRequest(
                    threadId,
                    "gateway-outbox-integration",
                    "real terminal text"),
                cancellationToken);
            Assert.NotEqual(SessionCommandStatus.Rejected, completed.Status);
            var turnId = await state.ReadAsync<Guid>(
                async (connection, token) =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText =
                        "SELECT turn_id FROM turns WHERE thread_id = $threadId AND status = 'completed';";
                    Add(command, "$threadId", threadId.ToString("D"));
                    return Guid.Parse((string)(await command.ExecuteScalarAsync(token))!);
                },
                cancellationToken);
            var correlationId = Guid.CreateVersion7();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await state.WriteAsync(
                async (connection, transaction, token) =>
                {
                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText =
                        """
                        INSERT INTO channel_inbound_messages (
                            inbound_message_id, channel_id, external_message_id,
                            external_conversation_id, partition_sequence, payload_json,
                            body_sha256, session_create_idempotency_key,
                            session_submit_idempotency_key, session_expected_sequence,
                            session_queue_item_id, correlation_id, thread_id, turn_id,
                            status, attempt_count, next_attempt_utc, revision,
                            created_utc, updated_utc, delivered_utc)
                        VALUES ($inboundId, 'integration', 'message-1', 'conversation-1',
                                1, '{}',
                                'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                                $createKey, $submitKey, 1, $queueItemId, $correlationId,
                                $threadId, $turnId, 'delivered', 1, $now, 1,
                                $now, $now, $now);
                        """;
                    Add(command, "$inboundId", Guid.CreateVersion7().ToString("D"));
                    Add(command, "$createKey", Guid.CreateVersion7().ToString("D"));
                    Add(command, "$submitKey", Guid.CreateVersion7().ToString("D"));
                    Add(command, "$queueItemId", Guid.CreateVersion7().ToString("D"));
                    Add(command, "$correlationId", correlationId.ToString("D"));
                    Add(command, "$threadId", threadId.ToString("D"));
                    Add(command, "$turnId", turnId.ToString("D"));
                    Add(command, "$now", now);
                    await command.ExecuteNonQueryAsync(token);
                    return true;
                },
                cancellationToken);

            var reconciler = host.Services.GetRequiredService<GatewayReconciler>();
            reconciler.Wake();
            await WaitUntilAsync(
                () => sender.Requests.Count == 1,
                cancellationToken);

            var request = Assert.Single(sender.Requests.ToArray());
            Assert.Equal("message-1", request.Envelope.SourceMessageId);
            Assert.Equal("real terminal text", request.Envelope.Text);
            Assert.Equal(correlationId, request.Envelope.CorrelationId);
            await host.StopAsync(cancellationToken);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static int UnusedLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        CancellationToken cancellationToken)
    {
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < timeout, "Gateway reconciliation timed out.");
            await Task.Delay(20, cancellationToken);
        }
    }

    private sealed class RecordingSender : IChannelSender
    {
        public ConcurrentQueue<ChannelSendRequest> Requests { get; } = [];

        public ValueTask<ChannelSendResult> SendAsync(
            ChannelSendRequest request,
            ReadOnlyMemory<byte> secret,
            CancellationToken cancellationToken = default)
        {
            Assert.False(secret.IsEmpty);
            Requests.Enqueue(request);
            return ValueTask.FromResult(new ChannelSendResult(true, false));
        }
    }
}
