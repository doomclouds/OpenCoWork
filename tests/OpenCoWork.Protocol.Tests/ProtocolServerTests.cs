using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using OpenCoWork.Abstractions;
using OpenCoWork.Protocol;
using Xunit;

namespace OpenCoWork.Protocol.Tests;

public sealed class ProtocolServerTests
{
    [Fact]
    public async Task Stdio_transport_reads_strict_jsonl_and_writes_protocol_only()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var input = new MemoryStream(
            Encoding.UTF8.GetBytes(
                """
                {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"client":{"name":"test","version":"1"},"wireVersions":["1.0"],"workspace":{"path":"/workspace"}}}
                {"jsonrpc":"2.0","id":2,"method":"missing","params":{}}

                """));
        var output = new MemoryStream();

        await OpenCoWorkProtocolServer.RunStdioAsync(
            DispatchProxy.Create<ISessionService, ThrowingSessionProxy>(),
            "/workspace",
            input,
            output,
            cancellationToken);

        var lines = Encoding.UTF8.GetString(output.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"wireVersion\":\"1.0\"", lines[0], StringComparison.Ordinal);
        Assert.Contains("\"code\":-32601", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebSocket_transport_requires_bearer_header()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        var port = GetAvailablePort();
        var server = OpenCoWorkProtocolServer.RunWebSocketAsync(
            DispatchProxy.Create<ISessionService, ThrowingSessionProxy>(),
            "/workspace",
            port,
            "test-secret",
            lifetime.Token);
        using var client = new HttpClient();
        var endpoint = new Uri(
            $"http://127.0.0.1:{port}{OpenCoWorkProtocolServer.WebSocketPath}");

        HttpResponseMessage? unauthorized = null;
        for (var attempt = 0; attempt < 50 && unauthorized is null; attempt++)
        {
            try
            {
                unauthorized = await client.GetAsync(endpoint, cancellationToken);
            }
            catch (HttpRequestException)
            {
                await Task.Delay(20, cancellationToken);
            }
        }

        Assert.NotNull(unauthorized);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader(
            "Authorization",
            new AuthenticationHeaderValue("Bearer", "test-secret").ToString());
        await socket.ConnectAsync(
            new Uri(
                $"ws://127.0.0.1:{port}{OpenCoWorkProtocolServer.WebSocketPath}"),
            cancellationToken);
        var initialize = Encoding.UTF8.GetBytes(
            """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"client":{"name":"test","version":"1"},"wireVersions":["1.0"],"workspace":{"path":"/workspace"}}}
            """);
        await socket.SendAsync(
            initialize,
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken);
        var response = new byte[4096];
        var received = await socket.ReceiveAsync(response, cancellationToken);

        Assert.Contains(
            "\"wireVersion\":\"1.0\"",
            Encoding.UTF8.GetString(response, 0, received.Count),
            StringComparison.Ordinal);

        await socket.CloseAsync(
            WebSocketCloseStatus.NormalClosure,
            "done",
            cancellationToken);
        lifetime.Cancel();
        await server;
    }

    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private class ThrowingSessionProxy : DispatchProxy
    {
        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? args) =>
            throw new NotSupportedException(targetMethod?.Name);
    }
}
