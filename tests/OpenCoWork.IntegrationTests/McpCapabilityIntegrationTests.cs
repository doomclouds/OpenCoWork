using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Capabilities;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Logging;
using OpenCoWork.Core.Tools;
using OpenCoWork.Core.Workspaces;
using OpenCoWork.McpFixture;
using Xunit;

namespace OpenCoWork.IntegrationTests;

public sealed class McpCapabilityIntegrationTests
{
    [Fact]
    public async Task Streamable_http_handshake_publishes_tool_and_resource()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateTemporaryRoot("http");
        await using var server = new FakeHttpMcpServer();
        try
        {
            var (paths, files, tools, source) = CreateRuntime(root);
            await File.WriteAllTextAsync(
                paths.McpPath,
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    servers = new[]
                    {
                        new
                        {
                            id = "workspace/http",
                            enabled = true,
                            transport = new
                            {
                                kind = "streamableHttp",
                                url = server.Endpoint.AbsoluteUri,
                                authProfileId = (string?)null,
                            },
                        },
                    },
                }),
                cancellationToken);
            var pending = await source.DiscoverAsync(cancellationToken);
            var descriptor = Assert.Single(pending.Contributions).Source;
            Assert.Equal(
                CapabilityStatus.PendingTrust,
                Assert.Single(pending.Contributions[0].Items).Status);
            await TrustAsync(
                root,
                files,
                "workspace/http",
                descriptor.Sha256,
                cancellationToken);

            var ready = await source.DiscoverAsync(cancellationToken);

            Assert.Equal(
                CapabilityStatus.Ready,
                Assert.Single(
                    ready.Contributions[0].Items,
                    item => item.Kind == CapabilityKind.McpServer).Status);
            var echo = Resolve(tools, "echo");
            var result = await echo.Executor(
                JsonSerializer.SerializeToElement(new { }),
                cancellationToken);
            Assert.True(result.IsSuccess);
            Assert.Contains("fixture ok", result.Output!.Value.GetRawText());
            var resource = Assert.Single(source.ListResources("workspace/http"));
            Assert.Contains(
                "fixture resource",
                (await source.ReadResourceAsync(
                    "workspace/http",
                    resource.Uri,
                    cancellationToken)).GetRawText());
            await source.StopAsync(cancellationToken);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Stdio_process_honors_cancel_sanitizes_errors_and_is_killed_on_stop()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateTemporaryRoot("stdio");
        try
        {
            var pidPath = Path.Combine(root, "fixture.pid");
            var cancelPath = Path.Combine(root, "fixture.cancelled");
            var tracePath = Path.Combine(root, "fixture.trace");
            var (paths, files, tools, source) = CreateRuntime(root);
            var executable = FindExecutable("dotnet");

            await WriteStdioConfigAsync(
                paths,
                new Dictionary<string, object>
                {
                    ["OPENCOWORK_MCP_PID_FILE"] = new { literal = pidPath },
                    ["OPENCOWORK_MCP_CANCEL_FILE"] = new { literal = cancelPath },
                    ["OPENCOWORK_MCP_TRACE_FILE"] = new { literal = tracePath },
                },
                cancellationToken);
            var digest = Convert.ToHexString(
                    SHA256.HashData(await File.ReadAllBytesAsync(
                        executable,
                        cancellationToken)))
                .ToLowerInvariant();
            var pending = await source.DiscoverAsync(cancellationToken);
            Assert.Equal(
                CapabilityStatus.PendingTrust,
                Assert.Single(pending.Contributions[0].Items).Status);
            await TrustAsync(
                root,
                files,
                "workspace/stdio",
                digest,
                cancellationToken);

            var ready = await source.DiscoverAsync(cancellationToken);

            var status = Assert.Single(
                ready.Contributions[0].Items,
                item => item.Kind == CapabilityKind.McpServer).Status;
            Assert.True(
                status == CapabilityStatus.Ready,
                File.Exists(tracePath)
                    ? await File.ReadAllTextAsync(tracePath, cancellationToken)
                    : $"No fixture trace. PID file exists: {File.Exists(pidPath)}");
            await WaitForFileAsync(pidPath, cancellationToken);
            var pid = int.Parse(
                await File.ReadAllTextAsync(pidPath, cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
            using var process = Process.GetProcessById(pid);
            Assert.False(process.HasExited);
            var echo = await Resolve(tools, "echo").Executor(
                JsonSerializer.SerializeToElement(new { }),
                cancellationToken);
            Assert.True(echo.IsSuccess);
            var failed = await Resolve(tools, "fail").Executor(
                JsonSerializer.SerializeToElement(new { }),
                cancellationToken);
            Assert.Equal(McpToolErrorCodes.CallFailed, failed.Error?.Code);
            Assert.DoesNotContain(
                "malicious-fixture-secret",
                failed.Error?.Message,
                StringComparison.Ordinal);
            using var callCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            callCancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await Resolve(tools, "slow").Executor(
                    JsonSerializer.SerializeToElement(new { }),
                    callCancellation.Token));
            await WaitForFileAsync(cancelPath, cancellationToken);

            await source.StopAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken);
            Assert.True(process.HasExited);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Stdio_half_frame_faults_without_publishing_bindings()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateTemporaryRoot("half-frame");
        try
        {
            var (paths, files, tools, source) = CreateRuntime(root);
            await WriteStdioConfigAsync(
                paths,
                new Dictionary<string, object>
                {
                    ["OPENCOWORK_MCP_HALF_FRAME"] = new { literal = "1" },
                },
                cancellationToken);
            var executable = FindExecutable("dotnet");
            var digest = Convert.ToHexString(
                    SHA256.HashData(await File.ReadAllBytesAsync(
                        executable,
                        cancellationToken)))
                .ToLowerInvariant();
            _ = await source.DiscoverAsync(cancellationToken);
            await TrustAsync(
                root,
                files,
                "workspace/stdio",
                digest,
                cancellationToken);

            var result = await source.DiscoverAsync(cancellationToken);

            var server = Assert.Single(
                result.Contributions[0].Items,
                item => item.Kind == CapabilityKind.McpServer);
            Assert.Equal(CapabilityStatus.Faulted, server.Status);
            Assert.Equal(
                [McpCapabilityErrorCodes.ConnectionFailed],
                server.DiagnosticCodes);
            Assert.DoesNotContain(
                tools.Registrations,
                item => item.Definition.Id.SourceKind == ToolSourceKind.Mcp);
            await source.StopAsync(cancellationToken);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static (
        OpenCoWorkPaths Paths,
        CapabilityFileStore Files,
        ToolRuntime Tools,
        McpCapabilitySource Source) CreateRuntime(string root)
    {
        var workspace = Path.Combine(root, "workspace");
        var user = Path.Combine(root, "user");
        Directory.CreateDirectory(Path.Combine(workspace, ".opencowork"));
        Directory.CreateDirectory(user);
        var paths = new OpenCoWorkPaths(workspace);
        var files = new CapabilityFileStore(
            new CapabilityPersistencePaths(paths, user));
        var tools = new ToolRuntime(paths);
        var declarations = new ProviderDeclarationCatalog(paths);
        var auth = new ProviderAuthService(
            declarations,
            new InMemoryOsSecretStore(),
            new SecretRedactor([]),
            paths: paths);
        return (paths, files, tools, new McpCapabilitySource(
            paths,
            files,
            tools,
            auth));
    }

    private static ToolRuntimeBinding Resolve(ToolRuntime tools, string name)
    {
        var registration = Assert.Single(
            tools.Registrations,
            item => item.Definition.Id.SourceKind == ToolSourceKind.Mcp &&
                    item.Definition.Id.SourceToolId == name);
        Assert.True(tools.TryResolveBinding(
            registration.RuntimeBindingId,
            registration.BindingGeneration,
            out var binding));
        return binding!;
    }

    private static Task TrustAsync(
        string root,
        CapabilityFileStore files,
        string serverId,
        string digest,
        CancellationToken cancellationToken) =>
        files.SaveTrustDecisionsAsync(
            new TrustDecisionsDocument(
                1,
                [new CapabilityTrustDecision(
                    Path.Combine(root, "workspace"),
                    CapabilitySourceKind.Workspace,
                    serverId,
                    SourceVersion: null,
                    digest,
                    [CapabilityTrustScope.OutOfProcess],
                    [])]),
            cancellationToken);

    private static Task WriteStdioConfigAsync(
        OpenCoWorkPaths paths,
        IReadOnlyDictionary<string, object> environment,
        CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(
            paths.McpPath,
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                servers = new[]
                {
                    new
                    {
                        id = "workspace/stdio",
                        enabled = true,
                        transport = new
                        {
                            kind = "stdio",
                            command = "dotnet",
                            arguments = new[]
                            {
                                typeof(FixtureMarker).Assembly.Location,
                            },
                            workingDirectory = "workspace",
                            environment,
                        },
                    },
                },
            }),
            cancellationToken);

    private static string CreateTemporaryRoot(string kind)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-mcp-{kind}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string FindExecutable(string command)
    {
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, command + extension);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        throw new FileNotFoundException($"Could not find {command} on PATH.");
    }

    private static async Task WaitForFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!File.Exists(path))
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail($"Timed out waiting for {path}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }
    }

    private sealed class FakeHttpMcpServer : IAsyncDisposable
    {
        private readonly CancellationTokenSource _lifetime = new();
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task _run;

        public FakeHttpMcpServer()
        {
            _listener.Start();
            var endpoint = (IPEndPoint)_listener.LocalEndpoint;
            Endpoint = new Uri($"http://127.0.0.1:{endpoint.Port}/mcp");
            _run = RunAsync();
        }

        public Uri Endpoint { get; }

        public async ValueTask DisposeAsync()
        {
            await _lifetime.CancelAsync();
            _listener.Stop();
            try
            {
                await _run;
            }
            catch (Exception exception) when (
                exception is OperationCanceledException or SocketException)
            {
            }

            _lifetime.Dispose();
        }

        private async Task RunAsync()
        {
            while (!_lifetime.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(
                    _lifetime.Token);
                await HandleAsync(client, _lifetime.Token);
            }
        }

        private static async Task HandleAsync(
            TcpClient client,
            CancellationToken cancellationToken)
        {
            await using var stream = client.GetStream();
            using var reader = new StreamReader(
                stream,
                Encoding.ASCII,
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(cancellationToken);
            var contentLength = 0;
            var isChunked = false;
            string? line;
            while (!string.IsNullOrEmpty(
                       line = await reader.ReadLineAsync(cancellationToken)))
            {
                if (line.StartsWith(
                        "Content-Length:",
                        StringComparison.OrdinalIgnoreCase))
                {
                    contentLength = int.Parse(
                        line["Content-Length:".Length..].Trim(),
                        System.Globalization.CultureInfo.InvariantCulture);
                }
                else if (line.Equals(
                             "Transfer-Encoding: chunked",
                             StringComparison.OrdinalIgnoreCase))
                {
                    isChunked = true;
                }
            }

            if (requestLine?.StartsWith("GET ", StringComparison.Ordinal) == true)
            {
                await WriteHttpAsync(
                    stream,
                    "405 Method Not Allowed",
                    content: null,
                    cancellationToken);
                return;
            }

            var body = isChunked
                ? await ReadChunkedAsync(reader, cancellationToken)
                : await ReadFixedAsync(reader, contentLength, cancellationToken);
            using var document = JsonDocument.Parse(body);
            var message = document.RootElement;
            if (!message.TryGetProperty("id", out var id))
            {
                await WriteHttpAsync(
                    stream,
                    "202 Accepted",
                    content: null,
                    cancellationToken);
                return;
            }

            var result = message.GetProperty("method").GetString() switch
            {
                "server/discover" =>
                    """
                    {
                      "supportedVersions": ["2025-11-25"],
                      "capabilities": {
                        "tools": { "listChanged": false },
                        "resources": { "listChanged": false }
                      }
                    }
                    """,
                "initialize" =>
                    """
                    {
                      "protocolVersion": "2025-11-25",
                      "capabilities": {
                        "tools": { "listChanged": false },
                        "resources": { "listChanged": false }
                      },
                      "serverInfo": {
                        "name": "OpenCoWork.HttpFixture",
                        "version": "1.0.0"
                      }
                    }
                    """,
                "tools/list" =>
                    """
                    {
                      "tools": [{
                        "name": "echo",
                        "description": "Returns a fixed response.",
                        "inputSchema": {
                          "type": "object",
                          "additionalProperties": false
                        }
                      }]
                    }
                    """,
                "resources/list" =>
                    """
                    {
                      "resources": [{
                        "uri": "test://fixture",
                        "name": "fixture"
                      }]
                    }
                    """,
                "resources/read" =>
                    """
                    {
                      "contents": [{
                        "uri": "test://fixture",
                        "mimeType": "text/plain",
                        "text": "fixture resource"
                      }]
                    }
                    """,
                "tools/call" =>
                    """
                    {
                      "content": [{
                        "type": "text",
                        "text": "fixture ok"
                      }],
                      "isError": false
                    }
                    """,
                _ => throw new InvalidOperationException("Unexpected MCP method."),
            };
            await WriteHttpAsync(
                stream,
                "200 OK",
                $$"""{"jsonrpc":"2.0","id":{{id.GetRawText()}},"result":{{result}}}""",
                cancellationToken);
        }

        private static async Task<string> ReadChunkedAsync(
            TextReader reader,
            CancellationToken cancellationToken)
        {
            var body = new StringBuilder();
            while (true)
            {
                var sizeLine = await reader.ReadLineAsync(cancellationToken) ??
                               throw new EndOfStreamException();
                var size = int.Parse(
                    sizeLine.Split(';', 2)[0],
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture);
                if (size == 0)
                {
                    _ = await reader.ReadLineAsync(cancellationToken);
                    return body.ToString();
                }

                body.Append(await ReadFixedAsync(
                    reader,
                    size,
                    cancellationToken));
                _ = await reader.ReadLineAsync(cancellationToken);
            }
        }

        private static async Task<string> ReadFixedAsync(
            TextReader reader,
            int length,
            CancellationToken cancellationToken)
        {
            var buffer = new char[length];
            var read = 0;
            while (read < buffer.Length)
            {
                var count = await reader.ReadAsync(
                    buffer.AsMemory(read),
                    cancellationToken);
                if (count == 0)
                {
                    throw new EndOfStreamException();
                }

                read += count;
            }

            return new string(buffer);
        }

        private static async Task WriteHttpAsync(
            Stream stream,
            string status,
            string? content,
            CancellationToken cancellationToken)
        {
            var body = content is null ? [] : Encoding.UTF8.GetBytes(content);
            var headers = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status}\r\n" +
                (content is null
                    ? ""
                    : "Content-Type: application/json\r\n") +
                $"Content-Length: {body.Length}\r\n" +
                "Connection: close\r\n\r\n");
            await stream.WriteAsync(headers, cancellationToken);
            await stream.WriteAsync(body, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
    }
}
