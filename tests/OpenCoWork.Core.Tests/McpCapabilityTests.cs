using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Capabilities;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Logging;
using OpenCoWork.Core.Tools;
using OpenCoWork.Core.Workspaces;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class McpCapabilityTests
{
    [Fact]
    public async Task Core_sdk_supports_required_client_transports_notifications_and_cancellation()
    {
        var testCancellation = TestContext.Current.CancellationToken;
        _ = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "contract-stdio",
            Command = "fake",
            Arguments = [],
        });
        _ = new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = "contract-http",
            Endpoint = new Uri("https://example.invalid/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            MaxReconnectionAttempts = 0,
        });

        var clientMessages = Channel.CreateUnbounded<JsonRpcMessage>();
        var serverMessages = Channel.CreateUnbounded<JsonRpcMessage>();
        await using var clientTransport = new ChannelTransport(
            serverMessages.Reader,
            clientMessages.Writer,
            serverMessages.Writer);
        await using var serverTransport = new ChannelTransport(
            clientMessages.Reader,
            serverMessages.Writer,
            clientMessages.Writer);
        using var serverLifetime =
            CancellationTokenSource.CreateLinkedTokenSource(testCancellation);
        var server = RunFakeServerAsync(
            serverTransport,
            serverLifetime.Token);

        await using var client = await McpClient.CreateAsync(
            new ChannelClientTransport(clientTransport),
            cancellationToken: testCancellation);
        Assert.Single(await client.ListToolsAsync(cancellationToken: testCancellation));
        Assert.Single(await client.ListResourcesAsync(cancellationToken: testCancellation));

        var changed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = client.RegisterNotificationHandler(
            "notifications/tools/list_changed",
            (_, _) =>
            {
                changed.TrySetResult();
                return ValueTask.CompletedTask;
            });
        await serverTransport.SendMessageAsync(new JsonRpcNotification
        {
            Method = "notifications/tools/list_changed",
        }, testCancellation);
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(2), testCancellation);

        using var callCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(testCancellation);
        callCancellation.CancelAfter(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await client.CallToolAsync(
                "slow",
                cancellationToken: callCancellation.Token));
        await serverLifetime.CancelAsync();
        await server.WaitAsync(testCancellation);
    }

    [Fact]
    public async Task Workspace_source_publishes_tools_resources_changes_and_disconnects()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-mcp-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "workspace");
        var user = Path.Combine(root, "user");
        Directory.CreateDirectory(Path.Combine(workspace, ".opencowork"));
        Directory.CreateDirectory(user);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(workspace, ".opencowork", "mcp.json"),
                """
                {
                  "schemaVersion": 1,
                  "servers": [{
                    "id": "workspace/test",
                    "enabled": true,
                    "transport": {
                      "kind": "streamableHttp",
                      "url": "https://example.invalid/mcp",
                      "authProfileId": null
                    }
                  }]
                }
                """,
                cancellationToken);
            var clientMessages = Channel.CreateUnbounded<JsonRpcMessage>();
            var serverMessages = Channel.CreateUnbounded<JsonRpcMessage>();
            await using var clientTransport = new ChannelTransport(
                serverMessages.Reader,
                clientMessages.Writer,
                serverMessages.Writer);
            await using var serverTransport = new ChannelTransport(
                clientMessages.Reader,
                serverMessages.Writer,
                clientMessages.Writer);
            var state = new FakeMcpState();
            var server = RunCapabilityServerAsync(
                serverTransport,
                state,
                cancellationToken);
            var paths = new OpenCoWorkPaths(workspace);
            var persistence = new CapabilityPersistencePaths(paths, user);
            var files = new CapabilityFileStore(persistence);
            var tools = new ToolRuntime(paths);
            var declarations = new ProviderDeclarationCatalog(paths);
            var auth = new ProviderAuthService(
                new ModelsConfig(),
                declarations,
                new InMemoryOsSecretStore(),
                new SecretRedactor([]),
                paths: paths);
            var source = new McpCapabilitySource(
                paths,
                files,
                tools,
                auth,
                (_, _) => Task.FromResult(new McpConnection(
                    new ChannelClientTransport(clientTransport),
                    transportLifetime: null,
                    process: null,
                    stderr: null,
                    [])));
            var changes = Channel.CreateUnbounded<bool>();
            source.Changed = token =>
                changes.Writer.WriteAsync(true, token).AsTask();

            var discovered = await source.DiscoverAsync(cancellationToken);

            Assert.Contains(
                discovered.Contributions.SelectMany(set => set.Items),
                item => item.Kind == CapabilityKind.McpServer &&
                        item.Status == CapabilityStatus.Ready);
            var echo = Assert.Single(
                tools.Registrations,
                registration =>
                    registration.Definition.Id.SourceKind == ToolSourceKind.Mcp);
            Assert.True(tools.TryResolveBinding(
                echo.RuntimeBindingId,
                echo.BindingGeneration,
                out var echoBinding));
            var echoResult = await echoBinding!.Executor(
                JsonSerializer.SerializeToElement(new { text = "hello" }),
                cancellationToken);
            Assert.True(echoResult.IsSuccess);
            Assert.Contains("ok", echoResult.Output!.Value.GetRawText());

            var resources = source.ListResources("workspace/test");
            Assert.Single(resources);
            var resource = await source.ReadResourceAsync(
                "workspace/test",
                resources[0].Uri,
                cancellationToken);
            Assert.Contains("resource body", resource.GetRawText());

            state.ToolName = "slow";
            await serverTransport.SendMessageAsync(
                new JsonRpcNotification
                {
                    Method = "notifications/tools/list_changed",
                },
                cancellationToken);
            _ = await changes.Reader.ReadAsync(cancellationToken);
            var slow = Assert.Single(
                tools.Registrations,
                registration =>
                    registration.Definition.Id.SourceKind == ToolSourceKind.Mcp);
            Assert.Equal("slow", slow.Definition.Id.SourceToolId);
            Assert.True(tools.TryResolveBinding(
                slow.RuntimeBindingId,
                slow.BindingGeneration,
                out var slowBinding));
            using var callCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            callCancellation.CancelAfter(TimeSpan.FromMilliseconds(50));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await slowBinding!.Executor(
                    JsonSerializer.SerializeToElement(new { }),
                    callCancellation.Token));
            await state.CancellationObserved.Task.WaitAsync(
                TimeSpan.FromSeconds(2),
                cancellationToken);

            await serverTransport.DisposeAsync();
            _ = await changes.Reader.ReadAsync(cancellationToken);
            Assert.False(tools.TryResolveBinding(
                slow.RuntimeBindingId,
                slow.BindingGeneration,
                out _));

            await source.StopAsync(cancellationToken);
            await server.WaitAsync(cancellationToken);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Stdio_server_stays_pending_until_the_executable_digest_is_trusted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-mcp-trust-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "workspace");
        var user = Path.Combine(root, "user");
        var toolsDirectory = Path.Combine(workspace, "tools");
        Directory.CreateDirectory(Path.Combine(workspace, ".opencowork"));
        Directory.CreateDirectory(toolsDirectory);
        Directory.CreateDirectory(user);
        try
        {
            var executable = Path.Combine(toolsDirectory, "fake");
            await File.WriteAllTextAsync(
                executable,
                "fixture",
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(workspace, ".opencowork", "mcp.json"),
                """
                {
                  "schemaVersion": 1,
                  "servers": [{
                    "id": "workspace/stdio",
                    "enabled": true,
                    "transport": {
                      "kind": "stdio",
                      "command": "tools/fake",
                      "arguments": [],
                      "workingDirectory": "workspace",
                      "environment": {}
                    }
                  }]
                }
                """,
                cancellationToken);
            var paths = new OpenCoWorkPaths(workspace);
            var persistence = new CapabilityPersistencePaths(paths, user);
            var files = new CapabilityFileStore(persistence);
            var tools = new ToolRuntime(paths);
            var declarations = new ProviderDeclarationCatalog(paths);
            var called = false;
            var source = new McpCapabilitySource(
                paths,
                files,
                tools,
                new ProviderAuthService(
                    new ModelsConfig(),
                    declarations,
                    new InMemoryOsSecretStore(),
                    new SecretRedactor([]),
                    paths: paths),
                (_, _) =>
                {
                    called = true;
                    throw new IOException();
                });

            var result = await source.DiscoverAsync(cancellationToken);

            var server = Assert.Single(
                result.Contributions.SelectMany(set => set.Items));
            Assert.Equal(CapabilityStatus.PendingTrust, server.Status);
            Assert.Contains(
                CapabilityTrustScope.OutOfProcess,
                server.RequiredTrustScopes);
            Assert.False(called);
            var digest = Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes("fixture")))
                .ToLowerInvariant();
            await files.SaveTrustDecisionsAsync(
                new TrustDecisionsDocument(
                    1,
                    [new CapabilityTrustDecision(
                        workspace,
                        CapabilitySourceKind.Workspace,
                        "workspace/stdio",
                        SourceVersion: null,
                        digest,
                        [CapabilityTrustScope.OutOfProcess],
                        [])]),
                cancellationToken);

            result = await source.DiscoverAsync(cancellationToken);

            Assert.True(called);
            Assert.Equal(
                CapabilityStatus.Faulted,
                Assert.Single(
                    result.Contributions.SelectMany(set => set.Items)).Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Initialization_timeout_faults_without_escaping_the_exception()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-mcp-timeout-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "workspace");
        var user = Path.Combine(root, "user");
        Directory.CreateDirectory(Path.Combine(workspace, ".opencowork"));
        Directory.CreateDirectory(user);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(workspace, ".opencowork", "mcp.json"),
                """
                {
                  "schemaVersion": 1,
                  "servers": [{
                    "id": "workspace/timeout",
                    "enabled": true,
                    "transport": {
                      "kind": "streamableHttp",
                      "url": "https://example.invalid/mcp",
                      "authProfileId": null
                    }
                  }]
                }
                """,
                cancellationToken);
            var paths = new OpenCoWorkPaths(workspace);
            var files = new CapabilityFileStore(
                new CapabilityPersistencePaths(paths, user));
            var declarations = new ProviderDeclarationCatalog(paths);
            var source = new McpCapabilitySource(
                paths,
                files,
                new ToolRuntime(paths),
                new ProviderAuthService(
                    new ModelsConfig(),
                    declarations,
                    new InMemoryOsSecretStore(),
                    new SecretRedactor([]),
                    paths: paths),
                (_, _) => throw new TimeoutException("unsafe remote detail"));

            var result = await source.DiscoverAsync(cancellationToken);

            var server = Assert.Single(
                result.Contributions[0].Items,
                item => item.Kind == CapabilityKind.McpServer);
            Assert.Equal(CapabilityStatus.Faulted, server.Status);
            Assert.Equal(
                [McpCapabilityErrorCodes.ConnectionFailed],
                server.DiagnosticCodes);
            await source.StopAsync(cancellationToken);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OAuth_tokens_round_trip_only_through_the_secret_store()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-mcp-oauth-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "workspace");
        var user = Path.Combine(root, "user");
        Directory.CreateDirectory(Path.Combine(workspace, ".opencowork"));
        Directory.CreateDirectory(user);
        try
        {
            var paths = new OpenCoWorkPaths(workspace);
            await File.WriteAllTextAsync(
                paths.AuthPath,
                """
                {
                  "schemaVersion": 1,
                  "profiles": [{
                    "id": "auth/mcp",
                    "kind": "oauth",
                    "scopes": ["tools.read", "resources.read"]
                  }]
                }
                """,
                cancellationToken);
            var declarations = new ProviderDeclarationCatalog(paths);
            Assert.Equal(
                ["resources.read", "tools.read"],
                declarations.AuthProfiles["auth/mcp"].Scopes);
            var auth = new ProviderAuthService(
                new ModelsConfig(),
                declarations,
                new InMemoryOsSecretStore(),
                new SecretRedactor([]),
                paths: paths);
            using var cache = new McpOAuthTokenCache(auth, "auth/mcp");
            var tokens = new TokenContainer
            {
                TokenType = "Bearer",
                AccessToken = "oauth-access-secret",
                RefreshToken = "oauth-refresh-secret",
                ObtainedAt = DateTimeOffset.UtcNow,
            };

            await cache.StoreTokensAsync(tokens, cancellationToken);
            var loaded = await cache.GetTokensAsync(cancellationToken);

            Assert.Equal(tokens.AccessToken, loaded?.AccessToken);
            Assert.Equal(tokens.RefreshToken, loaded?.RefreshToken);
            Assert.DoesNotContain(
                Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories),
                path => File.ReadAllText(path).Contains(
                    "oauth-access-secret",
                    StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task RunFakeServerAsync(
        ITransport transport,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in transport.MessageReader.ReadAllAsync(
                               cancellationToken))
            {
                switch (message)
                {
                    case JsonRpcRequest { Method: "server/discover" } request:
                        await ReplyAsync(
                            transport,
                            request,
                            """
                            {
                              "supportedVersions": ["2025-11-25"],
                              "capabilities": {
                                "tools": { "listChanged": true },
                                "resources": { "listChanged": true }
                              }
                            }
                            """,
                            cancellationToken);
                        break;
                    case JsonRpcRequest { Method: "initialize" } request:
                        await ReplyAsync(
                            transport,
                            request,
                            """
                            {
                              "protocolVersion": "2025-11-25",
                              "capabilities": {
                                "tools": { "listChanged": true },
                                "resources": { "listChanged": true }
                              },
                              "serverInfo": {
                                "name": "OpenCoWork.Contract",
                                "version": "1.0.0"
                              }
                            }
                            """,
                            cancellationToken);
                        break;
                    case JsonRpcRequest { Method: "tools/list" } request:
                        await ReplyAsync(
                            transport,
                            request,
                            """
                            {
                              "tools": [{
                                "name": "slow",
                                "description": "Waits for cancellation.",
                                "inputSchema": {
                                  "$schema": "https://json-schema.org/draft/2020-12/schema",
                                  "type": "object",
                                  "additionalProperties": false
                                }
                              }]
                            }
                            """,
                            cancellationToken);
                        break;
                    case JsonRpcRequest { Method: "resources/list" } request:
                        await ReplyAsync(
                            transport,
                            request,
                            """
                            {
                              "resources": [{
                                "uri": "test://resource",
                                "name": "resource"
                              }]
                            }
                            """,
                            cancellationToken);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task RunCapabilityServerAsync(
        ITransport transport,
        FakeMcpState state,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in transport.MessageReader.ReadAllAsync(
                               cancellationToken))
            {
                switch (message)
                {
                    case JsonRpcRequest { Method: "server/discover" } request:
                        await ReplyAsync(
                            transport,
                            request,
                            """
                            {
                              "supportedVersions": ["2025-11-25"],
                              "capabilities": {
                                "tools": { "listChanged": true },
                                "resources": { "listChanged": true }
                              }
                            }
                            """,
                            cancellationToken);
                        break;
                    case JsonRpcRequest { Method: "initialize" } request:
                        await ReplyAsync(
                            transport,
                            request,
                            """
                            {
                              "protocolVersion": "2025-11-25",
                              "capabilities": {
                                "tools": { "listChanged": true },
                                "resources": { "listChanged": true }
                              },
                              "serverInfo": {
                                "name": "OpenCoWork.FakeMcp",
                                "version": "1.0.0"
                              }
                            }
                            """,
                            cancellationToken);
                        break;
                    case JsonRpcRequest { Method: "tools/list" } request:
                        await ReplyAsync(
                            transport,
                            request,
                            $$"""
                            {
                              "tools": [{
                                "name": "{{state.ToolName}}",
                                "description": "Fake Tool",
                                "inputSchema": {
                                  "$schema": "https://json-schema.org/draft/2020-12/schema",
                                  "type": "object",
                                  "additionalProperties": false
                                }
                              }]
                            }
                            """,
                            cancellationToken);
                        break;
                    case JsonRpcRequest { Method: "resources/list" } request:
                        await ReplyAsync(
                            transport,
                            request,
                            """
                            {
                              "resources": [{
                                "uri": "test://resource",
                                "name": "resource",
                                "description": "Fake resource",
                                "mimeType": "text/plain"
                              }]
                            }
                            """,
                            cancellationToken);
                        break;
                    case JsonRpcRequest { Method: "resources/read" } request:
                        await ReplyAsync(
                            transport,
                            request,
                            """
                            {
                              "contents": [{
                                "uri": "test://resource",
                                "mimeType": "text/plain",
                                "text": "resource body"
                              }]
                            }
                            """,
                            cancellationToken);
                        break;
                    case JsonRpcRequest { Method: "tools/call" } request
                        when state.ToolName != "slow":
                        await ReplyAsync(
                            transport,
                            request,
                            """
                            {
                              "content": [{
                                "type": "text",
                                "text": "ok"
                              }],
                              "isError": false
                            }
                            """,
                            cancellationToken);
                        break;
                    case JsonRpcNotification { Method: "notifications/cancelled" }:
                        state.CancellationObserved.TrySetResult();
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static Task ReplyAsync(
        ITransport transport,
        JsonRpcRequest request,
        string result,
        CancellationToken cancellationToken) =>
        transport.SendMessageAsync(
            new JsonRpcResponse
            {
                Id = request.Id,
                Result = JsonNode.Parse(result),
            },
            cancellationToken);

    private sealed class ChannelClientTransport(ITransport transport)
        : IClientTransport
    {
        public string Name => "contract";

        public Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(transport);
    }

    private sealed class ChannelTransport(
        ChannelReader<JsonRpcMessage> reader,
        ChannelWriter<JsonRpcMessage> writer,
        ChannelWriter<JsonRpcMessage> incomingWriter)
        : ITransport
    {
        public string? SessionId => null;

        public ChannelReader<JsonRpcMessage> MessageReader { get; } = reader;

        public Task SendMessageAsync(
            JsonRpcMessage message,
            CancellationToken cancellationToken = default) =>
            writer.WriteAsync(message, cancellationToken).AsTask();

        public ValueTask DisposeAsync()
        {
            writer.TryComplete();
            incomingWriter.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeMcpState
    {
        public string ToolName { get; set; } = "echo";

        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
