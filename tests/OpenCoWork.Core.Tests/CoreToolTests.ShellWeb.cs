using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Logging;
using OpenCoWork.Core.Sessions;
using OpenCoWork.Core.Tools;
using OpenCoWork.Core.Workspaces;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed partial class CoreToolTests
{
    [Fact]
    public async Task Shell_records_process_result_and_removes_sensitive_environment()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
        {
            return;
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = CreateWorkspace();
        const string credentialName = "OPENCOWORK_PROVIDER_VALUE";
        const string visibleName = "OPENCOWORK_VISIBLE_VALUE";
        var previousCredential = Environment.GetEnvironmentVariable(credentialName);
        var previousVisible = Environment.GetEnvironmentVariable(visibleName);
        Environment.SetEnvironmentVariable(credentialName, "credential-secret");
        Environment.SetEnvironmentVariable(visibleName, "visible");
        try
        {
            var command = OperatingSystem.IsWindows()
                ? """
                  $credential = if ($env:OPENCOWORK_PROVIDER_VALUE) { $env:OPENCOWORK_PROVIDER_VALUE } else { 'missing' }
                  [Console]::Out.Write("$credential|$env:OPENCOWORK_VISIBLE_VALUE")
                  [Console]::Error.Write("problem")
                  exit 7
                  """
                : """
                  printf '%s|%s' "${OPENCOWORK_PROVIDER_VALUE-missing}" "$OPENCOWORK_VISIBLE_VALUE"
                  printf 'problem' >&2
                  exit 7
                  """;
            var result = await new CoreShellTool(
                    new OpenCoWorkPaths(directory),
                    [credentialName])
                .RunAsync(
                    JsonSerializer.SerializeToElement(new { command }),
                    cancellationToken);

            Assert.True(result.IsSuccess);
            var output = result.Output!.Value;
            Assert.Equal(7, output.GetProperty("exitCode").GetInt32());
            Assert.Equal(
                "missing|visible",
                output.GetProperty("stdout").GetString());
            Assert.Equal("problem", output.GetProperty("stderr").GetString());
            Assert.True(output.GetProperty("durationMilliseconds").GetInt64() >= 0);
            Assert.DoesNotContain(
                "credential-secret",
                output.GetRawText(),
                StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                credentialName,
                previousCredential);
            Environment.SetEnvironmentVariable(visibleName, previousVisible);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Shell_output_limit_kills_the_process()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = CreateWorkspace();
        try
        {
            var result = await new CoreShellTool(
                    new OpenCoWorkPaths(directory),
                    [])
                .RunAsync(
                    JsonSerializer.SerializeToElement(new
                    {
                        command = OperatingSystem.IsWindows()
                            ? "while ($true) { [Console]::Out.Write('0123456789') }"
                            : "while true; do printf '0123456789'; done",
                    }),
                    TestContext.Current.CancellationToken);

            Assert.False(result.IsSuccess);
            Assert.Equal(
                ToolErrorCodes.OutputLimitExceeded,
                result.Error!.Code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Shell_cancellation_kills_the_process_tree()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = CreateWorkspace();
        using var cancellation = new CancellationTokenSource();
        try
        {
            var tool = new CoreShellTool(new OpenCoWorkPaths(directory), []);
            var execution = tool.RunAsync(
                    JsonSerializer.SerializeToElement(new
                    {
                        command = OperatingSystem.IsWindows()
                            ? """
                              $child = Start-Process -FilePath $env:ComSpec -ArgumentList '/d','/c','ping -n 31 127.0.0.1 >nul' -PassThru
                              [IO.File]::WriteAllText((Join-Path (Get-Location) 'child.pid'), $child.Id.ToString())
                              $child.WaitForExit()
                              """
                            : "sleep 30 & child=$!; printf '%s' \"$child\" > child.pid; wait",
                    }),
                    cancellation.Token)
                .AsTask();
            var pidPath = Path.Combine(directory, "child.pid");
            for (var attempt = 0;
                 attempt < 100 && !File.Exists(pidPath);
                 attempt++)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            Assert.True(File.Exists(pidPath));
            var childId = int.Parse(
                await File.ReadAllTextAsync(
                    pidPath,
                    TestContext.Current.CancellationToken));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => execution);

            for (var attempt = 0;
                 attempt < 100 && ProcessExists(childId);
                 attempt++)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            Assert.False(ProcessExists(childId));
        }
        finally
        {
            cancellation.Cancel();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Shell_approval_prompt_contains_the_complete_command()
    {
        var directory = CreateWorkspace();
        try
        {
            var runtime = new ToolRuntime(new OpenCoWorkPaths(directory));
            var snapshot = runtime.BuildSnapshot(
                AgentMode.Agent,
                new ToolsConfig());
            var registration = Assert.Single(
                snapshot.Registrations,
                item => item.Definition.Name is
                { Namespace: "shell", Name: "run" });
            const string command = "printf 'approval-bound-command'";
            var arguments = JsonSerializer.SerializeToElement(new { command });
            var context = new ToolInvocationContext(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                CallIndex: 0,
                "call-shell",
                snapshot.CanonicalToProviderNames["shell.run"],
                arguments,
                Sha256(ThreadJournal.Canonicalize(arguments)),
                SensitiveInputDetected: false,
                snapshot,
                ApprovalCheckpoint: new SessionExecutionCheckpoint(
                    "test",
                    1,
                    "{}",
                    new string('a', 64)));
            var sink = new CapturingSink();

            await Assert.ThrowsAsync<ToolInvocationSuspendedException>(
                () => new ToolInvocationPipeline(
                        runtime,
                        new SecretRedactor([]))
                    .InvokeAsync(
                        context,
                        sink,
                        TestContext.Current.CancellationToken)
                    .AsTask());

            var waiting = Assert.Single(
                sink.Intents.OfType<WaitForInteractionIntent>());
            var approval = Assert.IsType<ToolApprovalRequestContent>(
                waiting.Request);
            Assert.Equal(registration.Definition.Id, approval.ToolDefinitionId);
            Assert.Contains(command, approval.Prompt, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Web_revalidates_redirects_and_completes_http_errors()
    {
        await using var server = new ScriptedHttpServer(
            """
            HTTP/1.1 302 Found
            Location: http://next.test:{PORT}/final
            Content-Length: 0
            Connection: close


            """,
            """
            HTTP/1.1 404 Not Found
            Content-Type: text/plain; charset=utf-8
            Content-Length: 7
            Connection: close

            missing
            """);
        var hosts = new List<string>();
        var tool = new CoreWebTool(
            (host, _) =>
            {
                hosts.Add(host);
                return ValueTask.FromResult(
                    new[] { IPAddress.Parse("8.8.8.8") });
            },
            server.ConnectAsync);

        var result = await tool.FetchAsync(
            JsonSerializer.SerializeToElement(new
            {
                url = $"http://public.test:{server.Port}/start",
                method = "GET",
            }),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var output = result.Output!.Value;
        Assert.Equal(404, output.GetProperty("statusCode").GetInt32());
        Assert.Equal("missing", output.GetProperty("body").GetString());
        Assert.Equal(1, output.GetProperty("redirectCount").GetInt32());
        Assert.Equal(["public.test", "next.test"], hosts);
        await server.Completion;
    }

    [Fact]
    public async Task Web_denies_private_targets_and_private_redirects()
    {
        var connectorCalls = 0;
        var denied = await new CoreWebTool(
                (_, _) => ValueTask.FromResult(
                    new[] { IPAddress.Loopback }),
                (_, _, _) =>
                {
                    connectorCalls++;
                    return ValueTask.FromException<Stream>(
                        new InvalidOperationException("must not connect"));
                })
            .FetchAsync(
                JsonSerializer.SerializeToElement(new
                {
                    url = "http://private.test/",
                }),
                TestContext.Current.CancellationToken);

        Assert.Equal(ToolErrorCodes.NetworkTargetDenied, denied.Error!.Code);
        Assert.Equal(0, connectorCalls);

        await using var server = new ScriptedHttpServer(
            """
            HTTP/1.1 302 Found
            Location: http://private.test/
            Content-Length: 0
            Connection: close


            """);
        var redirected = await new CoreWebTool(
                (host, _) => ValueTask.FromResult(
                    new[]
                    {
                        host == "private.test"
                            ? IPAddress.Loopback
                            : IPAddress.Parse("8.8.8.8"),
                    }),
                server.ConnectAsync)
            .FetchAsync(
                JsonSerializer.SerializeToElement(new
                {
                    url = $"http://public.test:{server.Port}/",
                }),
                TestContext.Current.CancellationToken);

        Assert.Equal(
            ToolErrorCodes.NetworkTargetDenied,
            redirected.Error!.Code);
        await server.Completion;
    }

    [Fact]
    public async Task Web_validates_public_and_reserved_ipv6_targets()
    {
        var connectorCalls = 0;
        var publicResult = await new CoreWebTool(
                (_, _) => ValueTask.FromResult(
                    new[] { IPAddress.Parse("2001:4860:4860::8888") }),
                (_, _, _) =>
                {
                    connectorCalls++;
                    return ValueTask.FromException<Stream>(new IOException());
                })
            .FetchAsync(
                JsonSerializer.SerializeToElement(new
                {
                    url = "http://public.test/",
                }),
                TestContext.Current.CancellationToken);
        Assert.Equal(ToolErrorCodes.ExecutionFailed, publicResult.Error!.Code);
        Assert.Equal(1, connectorCalls);

        var reservedResult = await new CoreWebTool(
                (_, _) => ValueTask.FromResult(
                    new[] { IPAddress.Parse("2001:2::1") }),
                (_, _, _) =>
                {
                    connectorCalls++;
                    return ValueTask.FromException<Stream>(new IOException());
                })
            .FetchAsync(
                JsonSerializer.SerializeToElement(new
                {
                    url = "http://reserved.test/",
                }),
                TestContext.Current.CancellationToken);
        Assert.Equal(
            ToolErrorCodes.NetworkTargetDenied,
            reservedResult.Error!.Code);
        Assert.Equal(1, connectorCalls);
    }

    [Fact]
    public async Task Web_rejects_binary_and_oversized_bodies()
    {
        await using (var binary = new ScriptedHttpServer(
                         """
                         HTTP/1.1 200 OK
                         Content-Type: application/octet-stream
                         Content-Length: 3
                         Connection: close

                         abc
                         """))
        {
            var result = await PublicWeb(binary).FetchAsync(
                JsonSerializer.SerializeToElement(new
                {
                    url = $"http://public.test:{binary.Port}/",
                }),
                TestContext.Current.CancellationToken);
            Assert.Equal(
                ToolErrorCodes.ContentUnsupported,
                result.Error!.Code);
            await binary.Completion;
        }

        var body = new string(
            'x',
            ToolRuntimeLimits.MaximumBindingResultBytes + 1);
        await using var oversized = new ScriptedHttpServer(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n" +
            body);
        var tooLarge = await PublicWeb(oversized).FetchAsync(
            JsonSerializer.SerializeToElement(new
            {
                url = $"http://public.test:{oversized.Port}/",
            }),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ToolErrorCodes.OutputLimitExceeded,
            tooLarge.Error!.Code);
        await oversized.Completion;
    }

    [Fact]
    public async Task Web_cancellation_stops_a_slow_response()
    {
        await using var server = ScriptedHttpServer.Slow(
            """
            HTTP/1.1 200 OK
            Content-Type: text/plain; charset=utf-8
            Transfer-Encoding: chunked
            Connection: close

            1
            x

            """);
        using var cancellation = new CancellationTokenSource();
        var fetch = PublicWeb(server).FetchAsync(
                JsonSerializer.SerializeToElement(new
                {
                    url = $"http://public.test:{server.Port}/",
                }),
                cancellation.Token)
            .AsTask();

        await server.ResponseWritten;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
    }

    private static CoreWebTool PublicWeb(ScriptedHttpServer server) =>
        new(
            (_, _) => ValueTask.FromResult(
                new[] { IPAddress.Parse("8.8.8.8") }),
            server.ConnectAsync);

    private static bool ProcessExists(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(
                processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private sealed class CapturingSink : ISessionExecutionSink
    {
        public List<SessionExecutionIntent> Intents { get; } = [];

        public ValueTask EmitAsync(
            SessionExecutionIntent intent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Intents.Add(intent);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScriptedHttpServer : IAsyncDisposable
    {
        private readonly CancellationTokenSource _disposeCancellation = new();
        private readonly TcpListener _listener =
            new(IPAddress.Loopback, 0);
        private readonly TaskCompletionSource _responseWritten =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _holdOpenAfterResponse;
        private readonly string[] _responses;

        public ScriptedHttpServer(params string[] responses)
            : this(holdOpenAfterResponse: false, responses)
        {
        }

        private ScriptedHttpServer(
            bool holdOpenAfterResponse,
            params string[] responses)
        {
            _holdOpenAfterResponse = holdOpenAfterResponse;
            _responses = responses;
            _listener.Start();
            Completion = ServeAsync();
        }

        public static ScriptedHttpServer Slow(string response) =>
            new(holdOpenAfterResponse: true, response);

        public int Port =>
            ((IPEndPoint)_listener.LocalEndpoint).Port;

        public Task Completion { get; }

        public Task ResponseWritten => _responseWritten.Task;

        public async ValueTask<Stream> ConnectAsync(
            IReadOnlyList<IPAddress> addresses,
            int port,
            CancellationToken cancellationToken)
        {
            Assert.All(addresses, address => Assert.False(IPAddress.IsLoopback(address)));
            Assert.Equal(Port, port);
            var socket = new Socket(
                AddressFamily.InterNetwork,
                SocketType.Stream,
                ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(
                    IPAddress.Loopback,
                    Port,
                    cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            _disposeCancellation.Cancel();
            _listener.Stop();
            try
            {
                await Completion;
            }
            catch (Exception exception) when (
                exception is IOException or SocketException or
                    ObjectDisposedException or OperationCanceledException)
            {
            }
            finally
            {
                _disposeCancellation.Dispose();
            }
        }

        private async Task ServeAsync()
        {
            foreach (var template in _responses)
            {
                using var client = await _listener.AcceptTcpClientAsync();
                await using var stream = client.GetStream();
                using var reader = new StreamReader(
                    stream,
                    Encoding.ASCII,
                    detectEncodingFromByteOrderMarks: false,
                    bufferSize: 1024,
                    leaveOpen: true);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync()))
                {
                }

                var response = template
                    .Replace("\r", string.Empty, StringComparison.Ordinal)
                    .Replace("\n", "\r\n", StringComparison.Ordinal)
                    .Replace(
                        "{PORT}",
                        Port.ToString(),
                        StringComparison.Ordinal);
                try
                {
                    await stream.WriteAsync(
                        Encoding.UTF8.GetBytes(response));
                    _responseWritten.TrySetResult();
                    if (_holdOpenAfterResponse)
                    {
                        await Task.Delay(
                            Timeout.InfiniteTimeSpan,
                            _disposeCancellation.Token);
                    }
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
