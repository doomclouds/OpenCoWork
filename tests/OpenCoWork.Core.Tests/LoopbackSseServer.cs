using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OpenCoWork.Core.Tests;

internal sealed class LoopbackSseServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly Task _serverTask;

    public LoopbackSseServer(string responseBody, bool gzip = false)
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        BaseUri = new Uri($"http://127.0.0.1:{endpoint.Port}/v1/");
        var requestHeaders = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        RequestHeaders = requestHeaders.Task;
        var body = Encoding.UTF8.GetBytes(responseBody);
        if (gzip)
        {
            using var compressed = new MemoryStream();
            using (var encoder = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                encoder.Write(body);
            }

            body = compressed.ToArray();
        }

        _serverTask = ServeAsync(body, gzip, requestHeaders);
    }

    public Uri BaseUri { get; }

    public Task<string> RequestHeaders { get; }

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        await _serverTask;
    }

    private async Task ServeAsync(
        byte[] body,
        bool gzip,
        TaskCompletionSource<string> requestHeaders)
    {
        try
        {
            using var client = await _listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            using var reader = new StreamReader(
                stream,
                Encoding.ASCII,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 1024,
                leaveOpen: true);
            var headers = new StringBuilder();
            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.Length == 0)
                {
                    break;
                }

                headers.AppendLine(line);
            }

            requestHeaders.TrySetResult(headers.ToString());
            var contentEncoding = gzip ? "Content-Encoding: gzip\r\n" : string.Empty;
            var head = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\n{contentEncoding}Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head);
            for (var offset = 0; offset < body.Length; offset += 7)
            {
                await stream.WriteAsync(
                    body.AsMemory(offset, Math.Min(7, body.Length - offset)));
            }
        }
        catch (Exception exception)
        {
            requestHeaders.TrySetException(exception);
            throw;
        }
    }
}
