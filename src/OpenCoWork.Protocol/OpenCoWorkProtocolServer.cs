using System.Buffers;
using System.IO.Pipelines;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenCoWork.Abstractions;

namespace OpenCoWork.Protocol;

public static class OpenCoWorkProtocolServer
{
    public const string WebSocketPath = "/wire";
    public const string WebSocketTokenEnvironment = "OPENCOWORK_APP_SERVER_TOKEN";

    private const int MaximumInFlightRequests = 256;
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly ReadOnlyMemory<byte> Newline = "\n"u8.ToArray();

    public static Task RunStdioAsync(
        ISessionService sessions,
        string workspacePath,
        Stream input,
        Stream output,
        CancellationToken cancellationToken = default) =>
        RunStdioAsync(
            sessions,
            capabilities: null,
            workspacePath,
            input,
            output,
            cancellationToken);

    public static Task RunStdioAsync(
        ISessionService sessions,
        ICapabilityService? capabilities,
        string workspacePath,
        Stream input,
        Stream output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        return RunConnectionAsync(
            ReadStdioFramesAsync(input, cancellationToken),
            async (message, token) =>
            {
                await output.WriteAsync(message, token);
                await output.WriteAsync(Newline, token);
                await output.FlushAsync(token);
            },
            send => new OpenCoWorkJsonRpcConnection(
                sessions,
                capabilities,
                workspacePath,
                "stdio",
                send),
            static (connection, message, token) =>
                connection.ProcessAsync(message, token),
            cancellationToken);
    }

    public static Task RunJsonLinesAsync(
        ISessionService sessions,
        string workspacePath,
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken = default) =>
        RunJsonLinesAsync(
            sessions,
            capabilities: null,
            workspacePath,
            input,
            output,
            cancellationToken);

    public static Task RunJsonLinesAsync(
        ISessionService sessions,
        ICapabilityService? capabilities,
        string workspacePath,
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        return RunConnectionAsync(
            ReadTextFramesAsync(input, cancellationToken),
            async (message, token) =>
            {
                await output.WriteAsync(StrictUtf8.GetString(message.Span));
                await output.WriteLineAsync();
                await output.FlushAsync(token);
            },
            send => new OpenCoWorkJsonRpcConnection(
                sessions,
                capabilities,
                workspacePath,
                "stdio",
                send),
            static (connection, message, token) =>
                connection.ProcessAsync(message, token),
            cancellationToken);
    }

    public static Task RunAcpStdioAsync(
        ISessionService sessions,
        string workspacePath,
        string defaultProvider,
        string defaultModel,
        Stream input,
        Stream output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        return RunConnectionAsync(
            ReadStdioFramesAsync(input, cancellationToken),
            async (message, token) =>
            {
                await output.WriteAsync(message, token);
                await output.WriteAsync(Newline, token);
                await output.FlushAsync(token);
            },
            send => new OpenCoWorkAcpConnection(
                sessions,
                workspacePath,
                defaultProvider,
                defaultModel,
                send),
            static (connection, message, token) =>
                connection.ProcessAsync(message, token),
            cancellationToken);
    }

    public static Task RunAcpJsonLinesAsync(
        ISessionService sessions,
        string workspacePath,
        string defaultProvider,
        string defaultModel,
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        return RunConnectionAsync(
            ReadTextFramesAsync(input, cancellationToken),
            async (message, token) =>
            {
                await output.WriteAsync(StrictUtf8.GetString(message.Span));
                await output.WriteLineAsync();
                await output.FlushAsync(token);
            },
            send => new OpenCoWorkAcpConnection(
                sessions,
                workspacePath,
                defaultProvider,
                defaultModel,
                send),
            static (connection, message, token) =>
                connection.ProcessAsync(message, token),
            cancellationToken);
    }

    public static async Task RunWebSocketAsync(
        ISessionService sessions,
        string workspacePath,
        int port,
        string bearerToken,
        CancellationToken cancellationToken = default) =>
        await RunWebSocketAsync(
            sessions,
            capabilities: null,
            workspacePath,
            port,
            bearerToken,
            cancellationToken);

    public static async Task RunWebSocketAsync(
        ISessionService sessions,
        ICapabilityService? capabilities,
        string workspacePath,
        int port,
        string bearerToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65_535);
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);

        var builder = WebApplication.CreateSlimBuilder(
            new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
            options.ListenLocalhost(
                port,
                static listen => listen.Protocols = HttpProtocols.Http1));
        await using var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30),
        });
        app.Map(
            WebSocketPath,
            branch => branch.Run(
            async context =>
            {
                if (!HasBearerToken(context, bearerToken))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                try
                {
                    await RunConnectionAsync(
                        ReadWebSocketFramesAsync(
                            socket,
                            context.RequestAborted),
                        (message, token) =>
                            socket.SendAsync(
                                message,
                                WebSocketMessageType.Text,
                                endOfMessage: true,
                                token),
                        send => new OpenCoWorkJsonRpcConnection(
                            sessions,
                            capabilities,
                            workspacePath,
                            "websocket",
                            send),
                        static (connection, message, token) =>
                            connection.ProcessAsync(message, token),
                        context.RequestAborted);
                }
                catch (OperationCanceledException) when (
                    context.RequestAborted.IsCancellationRequested)
                {
                }
                catch (Exception) when (
                    socket.State is WebSocketState.Aborted or WebSocketState.Closed)
                {
                }
                finally
                {
                    if (socket.State is
                        WebSocketState.Open or WebSocketState.CloseReceived)
                    {
                        await socket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "closed",
                            CancellationToken.None);
                    }
                }
            }));
        await app.StartAsync(cancellationToken);
        await app.WaitForShutdownAsync(cancellationToken);
    }

    private static async Task RunConnectionAsync<TConnection>(
        IAsyncEnumerable<ReadOnlyMemory<byte>> inbound,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> write,
        Func<Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>, TConnection>
            createConnection,
        Func<TConnection, ReadOnlyMemory<byte>, CancellationToken, Task> process,
        CancellationToken cancellationToken)
        where TConnection : IAsyncDisposable
    {
        ArgumentNullException.ThrowIfNull(createConnection);
        ArgumentNullException.ThrowIfNull(process);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        var outbound = Channel.CreateBounded<ReadOnlyMemory<byte>>(
            new BoundedChannelOptions(OpenCoWorkWire.OutboundQueueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });
        var writer = WriteOutboundAsync(
            outbound.Reader,
            write,
            lifetime.Token);
        _ = writer.ContinueWith(
            static (_, state) => ((CancellationTokenSource)state!).Cancel(),
            lifetime,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously |
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
        await using var connection = createConnection(
            (message, _) =>
            {
                var copied = message.ToArray();
                if (outbound.Writer.TryWrite(copied))
                {
                    return ValueTask.CompletedTask;
                }

                lifetime.Cancel();
                return ValueTask.FromException(
                    new IOException("Protocol outbound queue is full."));
            });
        var pending = new HashSet<Task>();
        Exception? failure = null;
        try
        {
            await foreach (var message in inbound.WithCancellation(lifetime.Token))
            {
                var request = process(connection, message, lifetime.Token);
                pending.Add(request);
                if (pending.Count < MaximumInFlightRequests)
                {
                    continue;
                }

                var completed = await Task.WhenAny(pending);
                pending.Remove(completed);
                await completed;
            }

            await Task.WhenAll(pending);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            failure = exception;
            lifetime.Cancel();
        }
        finally
        {
            await connection.DisposeAsync();
            outbound.Writer.TryComplete(failure);
            try
            {
                await writer;
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
        }

        if (failure is not null)
        {
            throw failure;
        }
    }

    private static async Task WriteOutboundAsync(
        ChannelReader<ReadOnlyMemory<byte>> outbound,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> write,
        CancellationToken cancellationToken)
    {
        await foreach (var message in outbound.ReadAllAsync(cancellationToken))
        {
            await write(message, cancellationToken);
        }
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>>
        ReadStdioFramesAsync(
            Stream input,
            [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var reader = PipeReader.Create(
            input,
            new StreamPipeReaderOptions(leaveOpen: true));
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(cancellationToken);
                var buffer = read.Buffer;
                while (TryReadLine(ref buffer, out var line))
                {
                    yield return line;
                }

                if (buffer.Length > OpenCoWorkWire.MaximumMessageBytes)
                {
                    throw new InvalidDataException(
                        "Protocol message exceeds the fixed limit.");
                }

                reader.AdvanceTo(buffer.Start, buffer.End);
                if (!read.IsCompleted)
                {
                    continue;
                }

                if (buffer.Length != 0)
                {
                    yield return CopyFrame(buffer);
                }

                break;
            }
        }
        finally
        {
            await reader.CompleteAsync();
        }
    }

    private static bool TryReadLine(
        ref ReadOnlySequence<byte> buffer,
        out ReadOnlyMemory<byte> line)
    {
        var reader = new SequenceReader<byte>(buffer);
        if (!reader.TryReadTo(
                out ReadOnlySequence<byte> frame,
                (byte)'\n',
                advancePastDelimiter: true))
        {
            line = default;
            return false;
        }

        line = CopyFrame(frame);
        buffer = buffer.Slice(reader.Position);
        return true;
    }

    private static ReadOnlyMemory<byte> CopyFrame(ReadOnlySequence<byte> frame)
    {
        if (frame.Length > OpenCoWorkWire.MaximumMessageBytes)
        {
            throw new InvalidDataException(
                "Protocol message exceeds the fixed limit.");
        }

        var bytes = frame.ToArray();
        if (bytes.Length != 0 && bytes[^1] == '\r')
        {
            Array.Resize(ref bytes, bytes.Length - 1);
        }

        return bytes;
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>>
        ReadTextFramesAsync(
            TextReader input,
            [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                if (line.Length != 0)
                {
                    yield return StrictUtf8.GetBytes(line.ToString());
                }

                yield break;
            }

            for (var index = 0; index < read; index++)
            {
                var character = buffer[index];
                if (character == '\n')
                {
                    if (line.Length != 0 && line[^1] == '\r')
                    {
                        line.Length--;
                    }

                    yield return StrictUtf8.GetBytes(line.ToString());
                    line.Clear();
                    continue;
                }

                line.Append(character);
                if (line.Length > OpenCoWorkWire.MaximumMessageBytes)
                {
                    throw new InvalidDataException(
                        "Protocol message exceeds the fixed limit.");
                }
            }
        }
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>>
        ReadWebSocketFramesAsync(
            WebSocket socket,
            [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var receiveBuffer = new byte[16 * 1024];
        while (socket.State == WebSocketState.Open)
        {
            var message = new ArrayBufferWriter<byte>();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(
                    receiveBuffer,
                    cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    yield break;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    await socket.CloseAsync(
                        WebSocketCloseStatus.InvalidMessageType,
                        "text messages only",
                        cancellationToken);
                    yield break;
                }

                if (message.WrittenCount + result.Count >
                    OpenCoWorkWire.MaximumMessageBytes)
                {
                    await socket.CloseAsync(
                        WebSocketCloseStatus.MessageTooBig,
                        "message too large",
                        cancellationToken);
                    yield break;
                }

                receiveBuffer.AsSpan(0, result.Count)
                    .CopyTo(message.GetSpan(result.Count));
                message.Advance(result.Count);
            }
            while (!result.EndOfMessage);

            yield return message.WrittenMemory.ToArray();
        }
    }

    private static bool HasBearerToken(HttpContext context, string expected)
    {
        if (context.Request.Headers.Authorization.Count != 1 ||
            !AuthenticationHeaderValue.TryParse(
                context.Request.Headers.Authorization.ToString(),
                out var authorization) ||
            !string.Equals(
                authorization.Scheme,
                "Bearer",
                StringComparison.OrdinalIgnoreCase) ||
            authorization.Parameter is null)
        {
            return false;
        }

        var expectedBytes = StrictUtf8.GetBytes(expected);
        var actualBytes = StrictUtf8.GetBytes(authorization.Parameter);
        return expectedBytes.Length == actualBytes.Length &&
               CryptographicOperations.FixedTimeEquals(
                   expectedBytes,
                   actualBytes);
    }
}
