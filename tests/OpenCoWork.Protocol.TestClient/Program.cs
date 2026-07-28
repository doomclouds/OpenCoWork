using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using OpenCoWork.Protocol;

return await ProtocolTestClient.RunAsync(args);

internal static class ProtocolTestClient
{
    private const string SecretEnvironment = "OPENCOWORK_PROTOCOL_TEST_KEY";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public static async Task<int> RunAsync(string[] args)
    {
        var server = ParseServer(args);
        if (server is null)
        {
            await Console.Error.WriteLineAsync(
                "Usage: OpenCoWork.Protocol.TestClient --server <opencowork>");
            return 2;
        }

        var workspace = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-m5-{Guid.NewGuid():N}");
        var transcript = new ConcurrentQueue<string>();
        var secret = $"m5-secret-{Guid.NewGuid():N}";
        var stage = "workspace initialization";
        Directory.CreateDirectory(workspace);
        try
        {
            await InitializeWorkspaceAsync(server, workspace, transcript);
            await using var provider = new HoldingHttpEndpoint();
            await WriteConfigAsync(workspace, provider.Port);

            stage = "Wire stdio";
            var wire = await RunWireAsync(
                server,
                workspace,
                secret,
                provider,
                transcript);
            stage = "ACP";
            var acp = await RunAcpAsync(
                server,
                workspace,
                secret,
                provider,
                transcript);
            stage = "WebSocket";
            var websocket = await RunWebSocketAsync(
                server,
                workspace,
                secret,
                transcript);
            stage = "secret scan";
            EnsureSecretAbsent(secret, transcript, workspace);

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                passed = true,
                platform = Environment.OSVersion.Platform.ToString(),
                architecture = System.Runtime.InteropServices
                    .RuntimeInformation.ProcessArchitecture.ToString(),
                scenarios = new[]
                {
                    wire,
                    acp,
                    websocket,
                    "secret-canary",
                },
            }));
            return 0;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync($"{stage}: {exception}");
            foreach (var line in transcript.TakeLast(20))
            {
                await Console.Error.WriteLineAsync(
                    line.Replace(secret, "[REDACTED]", StringComparison.Ordinal));
            }
            return 1;
        }
        finally
        {
            TryDelete(workspace);
        }
    }

    private static async Task<string> RunWireAsync(
        string server,
        string workspace,
        string secret,
        HoldingHttpEndpoint provider,
        ConcurrentQueue<string> transcript)
    {
        Guid threadId;
        long watermark;
        await using (var client = StartLineClient(
                         server,
                         workspace,
                         secret,
                         transcript,
                         "app-server"))
        {
            await InitializeWireAsync(client, workspace);
            var created = await client.RequestAsync(
                2,
                "thread/create",
                new
                {
                    idempotencyKey = Guid.CreateVersion7(),
                    expectedSequence = 0,
                    displayName = "M5 black-box",
                    providerId = "m5-test",
                    modelId = "qwen3.8-max-preview",
                    mode = "agent",
                });
            threadId = created.GetProperty("result").GetProperty("thread")
                .GetProperty("threadId").GetGuid();
            var sequence = created.GetProperty("result")
                .GetProperty("currentSequence").GetInt64();
            await client.RequestAsync(
                3,
                "thread/subscribe",
                new { threadId, mode = "snapshotThenLive" });
            var started = await client.RequestAsync(
                4,
                "turn/start",
                new
                {
                    threadId,
                    idempotencyKey = Guid.CreateVersion7(),
                    expectedSequence = sequence,
                    text = "Hold until cancellation.",
                });
            var turnId = started.GetProperty("result")
                .GetProperty("turnId").GetGuid();
            await provider.WaitForConnectionAsync(Timeout);
            var current = await client.RequestAsync(
                5,
                "thread/get",
                new { threadId });
            sequence = current.GetProperty("result")
                .GetProperty("currentSequence").GetInt64();
            var cancelled = await client.RequestAsync(
                6,
                "turn/cancel",
                new
                {
                    threadId,
                    turnId,
                    idempotencyKey = Guid.CreateVersion7(),
                    expectedSequence = sequence,
                });
            Require(
                cancelled.GetProperty("result").GetProperty("turn")
                    .GetProperty("status").GetString() == "cancelled",
                "Wire cancellation did not reach a terminal state.");
            watermark = cancelled.GetProperty("result")
                .GetProperty("currentSequence").GetInt64();
        }

        await using (var reconnect = StartLineClient(
                         server,
                         workspace,
                         secret,
                         transcript,
                         "app-server"))
        {
            await InitializeWireAsync(reconnect, workspace);
            var subscribed = await reconnect.RequestAsync(
                2,
                "thread/subscribe",
                new
                {
                    threadId,
                    mode = "resumeAfterSequence",
                    afterSequence = 0,
                });
            var currentSequence = subscribed.GetProperty("result")
                .GetProperty("currentSequence").GetInt64();
            Require(
                currentSequence == watermark,
                "Wire reconnect watermark changed.");
            await reconnect.ReadThroughSequenceAsync(currentSequence);
            var sequences = reconnect.NotificationSequences.ToArray();
            Require(
                sequences.Length > 0 &&
                sequences.SequenceEqual(sequences.Distinct()) &&
                sequences.SequenceEqual(sequences.Order()),
                "Wire reconnect replay was empty, duplicated, or out of order.");
        }

        return "wire-stdio-reconnect-cancel";
    }

    private static async Task<string> RunAcpAsync(
        string server,
        string workspace,
        string secret,
        HoldingHttpEndpoint provider,
        ConcurrentQueue<string> transcript)
    {
        string sessionId;
        await using (var client = StartLineClient(
                         server,
                         workspace,
                         secret,
                         transcript,
                         "acp"))
        {
            var initialized = await client.RequestAsync(
                1,
                "initialize",
                new { protocolVersion = 1 });
            Require(
                initialized.GetProperty("result")
                    .GetProperty("protocolVersion").GetInt32() == 1,
                "ACP v1 negotiation failed.");
            var created = await client.RequestAsync(
                2,
                "session/new",
                new { cwd = workspace, mcpServers = Array.Empty<object>() });
            sessionId = created.GetProperty("result")
                .GetProperty("sessionId").GetString()
                ?? throw new InvalidOperationException(
                    "ACP session ID is missing.");
            await client.SendRequestAsync(
                3,
                "session/prompt",
                new
                {
                    sessionId,
                    prompt = new[]
                    {
                        new { type = "text", text = "Hold until cancellation." },
                    },
                });
            await provider.WaitForConnectionAsync(Timeout);
            await client.SendNotificationAsync(
                "session/cancel",
                new { sessionId });
            var prompt = await client.ReadResponseAsync(3);
            Require(
                prompt.GetProperty("result").GetProperty("stopReason")
                    .GetString() == "cancelled",
                "ACP cancellation did not map to cancelled.");
        }

        await using (var reconnect = StartLineClient(
                         server,
                         workspace,
                         secret,
                         transcript,
                         "acp"))
        {
            await reconnect.RequestAsync(
                1,
                "initialize",
                new { protocolVersion = 1 });
            await reconnect.RequestAsync(
                2,
                "session/load",
                new
                {
                    sessionId,
                    cwd = workspace,
                    mcpServers = Array.Empty<object>(),
                });
            Require(
                reconnect.Messages.Any(message =>
                    message.TryGetProperty("method", out var method) &&
                    method.GetString() == "session/update"),
                "ACP reconnect did not replay session history.");
        }

        return "acp-v1-reconnect-cancel";
    }

    private static async Task<string> RunWebSocketAsync(
        string server,
        string workspace,
        string secret,
        ConcurrentQueue<string> transcript)
    {
        var port = ReservePort();
        var token = $"m5-token-{Guid.NewGuid():N}";
        await using var process = ChildProcess.Start(
            server,
            ["app-server", "--workspace", workspace, "--transport", "websocket",
                "--port", port.ToString()],
            workspace,
            new Dictionary<string, string>
            {
                [SecretEnvironment] = secret,
                [OpenCoWorkProtocolServer.WebSocketTokenEnvironment] = token,
            },
            transcript);
        await WaitForWebSocketServerAsync(port, token);

        using var http = new HttpClient();
        var endpoint =
            $"http://127.0.0.1:{port}{OpenCoWorkProtocolServer.WebSocketPath}";
        using var unauthorized = await http.GetAsync(endpoint);
        Require(
            unauthorized.StatusCode == HttpStatusCode.Unauthorized,
            "WebSocket endpoint accepted a request without bearer auth.");
        using var queryOnly = await http.GetAsync($"{endpoint}?token={token}");
        Require(
            queryOnly.StatusCode == HttpStatusCode.Unauthorized,
            "WebSocket endpoint accepted a query-string token.");

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {token}");
        await socket.ConnectAsync(
            new Uri(endpoint.Replace("http://", "ws://", StringComparison.Ordinal)),
            CancellationToken.None);
        await SendWebSocketAsync(
            socket,
            Rpc(1, "initialize", new
            {
                client = new { name = "m5-test-client", version = "1" },
                wireVersions = new[] { "1.0" },
                workspace = new { path = workspace },
            }));
        var initialized = await ReceiveWebSocketAsync(socket);
        transcript.Enqueue(initialized);
        using var initializedDocument = JsonDocument.Parse(initialized);
        Require(
            initializedDocument.RootElement
                .GetProperty("result").GetProperty("wireVersion").GetString() ==
            "1.0",
            "WebSocket Wire negotiation failed.");

        const int delayedReads = 24;
        for (var id = 2; id < delayedReads + 2; id++)
        {
            await SendWebSocketAsync(socket, Rpc(id, "thread/list", new { }));
        }

        await Task.Delay(250);
        var responseIds = new HashSet<int>();
        while (responseIds.Count < delayedReads)
        {
            var message = await ReceiveWebSocketAsync(socket);
            transcript.Enqueue(message);
            using var document = JsonDocument.Parse(message);
            if (document.RootElement.TryGetProperty("id", out var id))
            {
                responseIds.Add(id.GetInt32());
            }
        }

        Require(
            responseIds.Count == delayedReads,
            "WebSocket slow reader lost responses.");
        await socket.CloseAsync(
            WebSocketCloseStatus.NormalClosure,
            "done",
            CancellationToken.None);
        await process.TerminateAsync();
        return "wire-websocket-auth-slow-reader";
    }

    private static LineClient StartLineClient(
        string server,
        string workspace,
        string secret,
        ConcurrentQueue<string> transcript,
        string command) =>
        new(
            ChildProcess.Start(
                server,
                [command, "--workspace", workspace],
                workspace,
                new Dictionary<string, string>
                {
                    [SecretEnvironment] = secret,
                },
                transcript),
            transcript);

    private static Task<JsonElement> InitializeWireAsync(
        LineClient client,
        string workspace) =>
        client.RequestAsync(
            1,
            "initialize",
            new
            {
                client = new { name = "m5-test-client", version = "1" },
                wireVersions = new[] { "1.0" },
                workspace = new { path = workspace },
            });

    private static async Task InitializeWorkspaceAsync(
        string server,
        string workspace,
        ConcurrentQueue<string> transcript)
    {
        await using var process = ChildProcess.Start(
            server,
            ["init", "--workspace", workspace],
            workspace,
            environment: null,
            transcript);
        await process.CompleteAsync();
        Require(process.ExitCode == 0, "Workspace initialization failed.");
    }

    private static Task WriteConfigAsync(string workspace, int providerPort)
    {
        var path = Path.Combine(workspace, ".opencowork", "config.jsonc");
        var content =
            $$"""
            {
              "models": {
                "defaultProvider": "m5-test",
                "defaultModel": "qwen3.8-max-preview",
                "providers": {
                  "m5-test": {
                    "baseUrl": "http://127.0.0.1:{{providerPort}}/v1",
                    "apiKey": { "environment": "{{SecretEnvironment}}" },
                    "models": {
                      "qwen3.8-max-preview": {
                        "tokenizerProfileId": "qwen-o200k",
                        "tokenizerProfileVersion": "1",
                        "contextWindowTokens": 983616,
                        "maxOutputTokens": 131072
                      }
                    }
                  }
                }
              }
            }
            """;
        return File.WriteAllTextAsync(path, content);
    }

    private static async Task WaitForWebSocketServerAsync(int port, string token)
    {
        using var http = new HttpClient();
        using var timeout = new CancellationTokenSource(Timeout);
        var endpoint =
            $"http://127.0.0.1:{port}{OpenCoWorkProtocolServer.WebSocketPath}";
        while (true)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue(
                        "Bearer",
                        token);
                using var response = await http.SendAsync(request, timeout.Token);
                if (response.StatusCode == HttpStatusCode.BadRequest)
                {
                    return;
                }
            }
            catch (HttpRequestException) when (!timeout.IsCancellationRequested)
            {
            }

            await Task.Delay(50, timeout.Token);
        }
    }

    private static async Task SendWebSocketAsync(
        ClientWebSocket socket,
        string message)
    {
        await socket.SendAsync(
            Encoding.UTF8.GetBytes(message),
            WebSocketMessageType.Text,
            endOfMessage: true,
            CancellationToken.None);
    }

    private static async Task<string> ReceiveWebSocketAsync(
        ClientWebSocket socket)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            Require(
                result.MessageType == WebSocketMessageType.Text,
                "WebSocket closed before returning a text response.");
            output.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(output.ToArray());
            }
        }
    }

    private static string Rpc(long id, string method, object parameters) =>
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params = parameters,
        });

    private static string? ParseServer(string[] args)
    {
        if (args.Length != 2 ||
            !string.Equals(args[0], "--server", StringComparison.Ordinal))
        {
            return null;
        }

        var path = Path.GetFullPath(args[1]);
        return File.Exists(path) ? path : null;
    }

    private static void EnsureSecretAbsent(
        string secret,
        ConcurrentQueue<string> transcript,
        string workspace)
    {
        var leaked = transcript.Any(line =>
            line.Contains(secret, StringComparison.Ordinal));
        foreach (var path in Directory.EnumerateFiles(
                     workspace,
                     "*",
                     SearchOption.AllDirectories))
        {
            if (File.ReadAllText(path).Contains(secret, StringComparison.Ordinal))
            {
                leaked = true;
                break;
            }
        }

        Require(!leaked, "Secret canary leaked into protocol, logs, or workspace.");
    }

    private static int ReservePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}

internal sealed class LineClient : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private readonly ChildProcess _process;
    private readonly ConcurrentQueue<string> _transcript;

    public LineClient(
        ChildProcess process,
        ConcurrentQueue<string> transcript)
    {
        _process = process;
        _transcript = transcript;
    }

    public List<JsonElement> Messages { get; } = [];

    public IEnumerable<long> NotificationSequences =>
        Messages
            .Where(message =>
                message.TryGetProperty("method", out _) &&
                message.TryGetProperty("params", out var parameters) &&
                parameters.TryGetProperty("sequence", out _))
            .Select(message =>
                message.GetProperty("params").GetProperty("sequence").GetInt64());

    public async Task<JsonElement> RequestAsync(
        long id,
        string method,
        object parameters)
    {
        await SendRequestAsync(id, method, parameters);
        return await ReadResponseAsync(id);
    }

    public Task SendRequestAsync(long id, string method, object parameters) =>
        SendAsync(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params = parameters,
        });

    public Task SendNotificationAsync(string method, object parameters) =>
        SendAsync(new
        {
            jsonrpc = "2.0",
            method,
            @params = parameters,
        });

    public async Task<JsonElement> ReadResponseAsync(long id)
    {
        while (true)
        {
            var message = await ReadAsync();
            if (!message.TryGetProperty("id", out var responseId) ||
                responseId.ValueKind != JsonValueKind.Number ||
                responseId.GetInt64() != id)
            {
                continue;
            }

            if (message.TryGetProperty("error", out var error))
            {
                throw new InvalidOperationException(
                    $"Protocol request {id} failed: {error.GetRawText()}");
            }

            return message;
        }
    }

    public async Task ReadThroughSequenceAsync(long sequence)
    {
        while (NotificationSequences.DefaultIfEmpty().Max() < sequence)
        {
            await ReadAsync();
        }
    }

    private async Task SendAsync(object message)
    {
        var line = JsonSerializer.Serialize(message);
        _transcript.Enqueue(line);
        await _process.Process.StandardInput.WriteLineAsync(line);
        await _process.Process.StandardInput.FlushAsync();
    }

    private async Task<JsonElement> ReadAsync()
    {
        using var timeout = new CancellationTokenSource(Timeout);
        var line = await _process.Process.StandardOutput
            .ReadLineAsync(timeout.Token);
        if (line is null)
        {
            throw new InvalidOperationException(
                $"Protocol process exited early: {_process.ErrorText}");
        }

        _transcript.Enqueue(line);
        using var document = JsonDocument.Parse(line);
        var message = document.RootElement.Clone();
        Messages.Add(message);
        return message;
    }

    public async ValueTask DisposeAsync()
    {
        _process.Process.StandardInput.Close();
        await _process.DisposeAsync();
    }
}

internal sealed class ChildProcess : IAsyncDisposable
{
    private readonly Task _errorReader;
    private readonly ConcurrentQueue<string> _transcript;
    private int _disposed;

    private ChildProcess(
        Process process,
        ConcurrentQueue<string> transcript)
    {
        Process = process;
        _transcript = transcript;
        _errorReader = ReadErrorsAsync();
    }

    public Process Process { get; }

    public int ExitCode => Process.ExitCode;

    public string ErrorText { get; private set; } = string.Empty;

    public static ChildProcess Start(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        ConcurrentQueue<string> transcript)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                start.Environment[pair.Key] = pair.Value;
            }
        }

        var process = System.Diagnostics.Process.Start(start)
            ?? throw new InvalidOperationException(
                $"Could not start {Path.GetFileName(executable)}.");
        return new ChildProcess(process, transcript);
    }

    public async Task CompleteAsync()
    {
        Process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await Process.WaitForExitAsync(timeout.Token);
        await _errorReader;
    }

    public async Task TerminateAsync()
    {
        if (!Process.HasExited)
        {
            Process.Kill(entireProcessTree: true);
            await Process.WaitForExitAsync();
        }

        await _errorReader;
    }

    private async Task ReadErrorsAsync()
    {
        var error = await Process.StandardError.ReadToEndAsync();
        ErrorText = error;
        if (!string.IsNullOrEmpty(error))
        {
            _transcript.Enqueue(error);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (!Process.HasExited)
        {
            try
            {
                Process.StandardInput.Close();
                using var timeout =
                    new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await Process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                Process.Kill(entireProcessTree: true);
                await Process.WaitForExitAsync();
            }
        }

        await _errorReader;
        Process.Dispose();
    }
}

internal sealed class HoldingHttpEndpoint : IAsyncDisposable
{
    private readonly TcpListener _listener =
        new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<bool> _connections =
        Channel.CreateUnbounded<bool>();
    private readonly Task _acceptLoop;

    public HoldingHttpEndpoint()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptAsync();
    }

    public int Port { get; }

    public async Task WaitForConnectionAsync(TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        await _connections.Reader.ReadAsync(cancellation.Token);
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_lifetime.Token);
                await _connections.Writer.WriteAsync(true, _lifetime.Token);
                _ = HoldAsync(client);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (SocketException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async Task HoldAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, _lifetime.Token);
            }
            catch (OperationCanceledException) when (
                _lifetime.IsCancellationRequested)
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _listener.Stop();
        await _acceptLoop;
        _lifetime.Dispose();
    }
}
