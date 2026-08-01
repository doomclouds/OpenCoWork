using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCoWork.Abstractions;
using OpenCoWork.Protocol;
using Xunit;

namespace OpenCoWork.Protocol.Tests;

public sealed class WebhookChannelTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.FromUnixTimeSeconds(1_786_070_400);
    private const string Secret = "test-webhook-secret";

    [Fact]
    public async Task Loopback_webhook_authenticates_raw_bytes_and_accepts_strict_envelope()
    {
        await using var server = await WebhookServer.StartAsync(Now, Secret);
        const string body =
            "{\"schemaVersion\":1,\"messageId\":\"m-1\",\"conversationId\":" +
            "\"c-1\",\"sentAtUtc\":\"2026-08-01T00:00:00Z\",\"text\":\"hello\"," +
            "\"attachments\":[]}";

        using var response = await server.SendAsync(body);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Single(server.Sink.Accepted);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))
                .ToLowerInvariant(),
            server.Sink.Accepted[0].BodySha256);
        var json = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains("\"receiptId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"correlationId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"duplicate\":false", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Authentication_schema_and_media_failures_never_reach_the_sink()
    {
        await using var server = await WebhookServer.StartAsync(Now, Secret);
        var valid =
            "{\"schemaVersion\":1,\"messageId\":\"m-1\",\"conversationId\":" +
            "\"c-1\",\"sentAtUtc\":\"2026-08-01T00:00:00Z\",\"text\":\"hello\"," +
            "\"attachments\":[]}";

        using var stale = await server.SendAsync(
            valid,
            timestamp: Now.AddMinutes(-6).ToUnixTimeSeconds());
        using var badSignature = await server.SendAsync(valid, signature: "v1=" + new string('0', 64));
        using var unknownChannel = await server.SendAsync(valid, channelId: "missing");
        using var notReady = await server.SendAsync(valid, ready: false);
        using var unknownField = await server.SendAsync(valid.Replace(
            "\"attachments\":[]",
            "\"unknown\":true,\"attachments\":[]",
            StringComparison.Ordinal));
        using var duplicate = await server.SendAsync(valid.Replace(
            "\"conversationId\"",
            "\"messageId\":\"m-2\",\"conversationId\"",
            StringComparison.Ordinal));
        using var fakePng = await server.SendAsync(
            "{\"schemaVersion\":1,\"messageId\":\"m-2\",\"conversationId\":" +
            "\"c-1\",\"sentAtUtc\":\"2026-08-01T00:00:00Z\",\"text\":null," +
            "\"attachments\":[{\"mediaType\":\"image/png\",\"displayName\":" +
            "\"fake.png\",\"contentBase64\":\"bm90LXBuZw==\"}]}");

        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, badSignature.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownChannel.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, notReady.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknownField.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, fakePng.StatusCode);
        Assert.Empty(server.Sink.Accepted);
    }

    [Fact]
    public async Task Oversized_body_is_rejected_before_authentication()
    {
        await using var server = await WebhookServer.StartAsync(Now, Secret);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"channels/build-bot/messages")
        {
            Content = new StreamContent(
                new RepeatingStream(WebhookChannelServer.MaximumBodyBytes + 1)),
        };
        request.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Content.Headers.ContentLength = WebhookChannelServer.MaximumBodyBytes + 1;
        request.Headers.TryAddWithoutValidation(
            WebhookChannelServer.TimestampHeader,
            Now.ToUnixTimeSeconds().ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation(
            WebhookChannelServer.SignatureHeader,
            "v1=" + new string('0', 64));
        request.Headers.ExpectContinue = true;

        using var response = await server.Client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(server.Sink.Accepted);
    }

    [Fact]
    public async Task Not_ready_channel_does_not_acquire_its_secret()
    {
        await using var server = await WebhookServer.StartAsync(Now, Secret);
        const string body =
            "{\"schemaVersion\":1,\"messageId\":\"m-1\",\"conversationId\":" +
            "\"c-1\",\"sentAtUtc\":\"2026-08-01T00:00:00Z\",\"text\":\"hello\"," +
            "\"attachments\":[]}";

        using var response = await server.SendAsync(body, ready: false);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, server.SecretAcquisitions);
        Assert.Empty(server.Sink.Accepted);
    }

    [Fact]
    public async Task Webhook_sender_does_not_follow_redirects()
    {
        var handler = new RedirectHandler();
        using var sender = new WebhookChannelSender(handler, new FixedTimeProvider(Now));
        var envelope = new ChannelOutboundEnvelope(
            1,
            Guid.CreateVersion7(),
            "m-1",
            "c-1",
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "completed",
            "done",
            null,
            Guid.CreateVersion7(),
            Now);
        var body = JsonSerializer.SerializeToUtf8Bytes(
            envelope,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var request = new ChannelSendRequest(
            "build-bot",
            new Uri("https://callback.example.test/result"),
            envelope,
            Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant());

        var result = await sender.SendAsync(
            request,
            Encoding.UTF8.GetBytes(Secret),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.False(result.Retryable);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task Supported_media_magic_is_accepted_and_single_item_limit_is_enforced()
    {
        await using var server = await WebhookServer.StartAsync(Now, Secret);
        var attachments = new[]
        {
            Media("text/plain", "note.txt", "hello"u8),
            Media("application/pdf", "doc.pdf", "%PDF-1.7"u8),
            Media("image/png", "image.png",
                new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }),
            Media("image/jpeg", "image.jpg", new byte[] { 0xff, 0xd8, 0xff, 0x00 }),
            Media("image/gif", "image.gif", "GIF89a"u8),
            Media("image/webp", "image.webp", "RIFF0000WEBP"u8),
        };
        var body = JsonSerializer.Serialize(
            new
            {
                schemaVersion = 1,
                messageId = "media-1",
                conversationId = "c-1",
                sentAtUtc = "2026-08-01T00:00:00Z",
                text = (string?)null,
                attachments,
            });

        using var accepted = await server.SendAsync(body);

        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Single(server.Sink.Accepted);

        var oversized = JsonSerializer.Serialize(
            new
            {
                schemaVersion = 1,
                messageId = "media-2",
                conversationId = "c-1",
                sentAtUtc = "2026-08-01T00:00:00Z",
                text = (string?)null,
                attachments = new[]
                {
                    Media(
                        "text/plain",
                        "large.txt",
                        new byte[(8 * 1024 * 1024) + 1]),
                },
            });
        using var rejected = await server.SendAsync(oversized);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, rejected.StatusCode);
        Assert.Single(server.Sink.Accepted);
    }

    private static object Media(string mediaType, string displayName, ReadOnlySpan<byte> content) =>
        new
        {
            mediaType,
            displayName,
            contentBase64 = Convert.ToBase64String(content),
        };

    private sealed class WebhookServer : IAsyncDisposable
    {
        private readonly CancellationTokenSource _lifetime;
        private readonly Task _server;
        private bool _ready = true;

        private WebhookServer(
            HttpClient client,
            RecordingSink sink,
            CancellationTokenSource lifetime,
            Task server)
        {
            Client = client;
            Sink = sink;
            _lifetime = lifetime;
            _server = server;
        }

        public HttpClient Client { get; }

        public RecordingSink Sink { get; }

        public int SecretAcquisitions { get; private set; }

        public static async Task<WebhookServer> StartAsync(
            DateTimeOffset now,
            string secret)
        {
            var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            var sink = new RecordingSink();
            var port = GetAvailablePort();
            WebhookServer? harness = null;
            var server = WebhookChannelServer.RunAsync(
                port,
                channelId => channelId == "build-bot"
                    ? new WebhookChannelBinding(
                        harness?._ready ?? true,
                        () =>
                        {
                            if (harness is not null)
                            {
                                harness.SecretAcquisitions++;
                            }

                            return Encoding.UTF8.GetBytes(secret);
                        })
                    : null,
                sink,
                new FixedTimeProvider(now),
                lifetime.Token);
            var client = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            };
            harness = new WebhookServer(client, sink, lifetime, server);
            for (var attempt = 0; attempt < 50; attempt++)
            {
                try
                {
                    using var probe = await client.GetAsync(
                        "channels/build-bot/messages",
                        TestContext.Current.CancellationToken);
                    return harness;
                }
                catch (HttpRequestException)
                {
                    await Task.Delay(20, TestContext.Current.CancellationToken);
                }
            }

            await harness.DisposeAsync();
            throw new TimeoutException("Webhook test server did not start.");
        }

        public async Task<HttpResponseMessage> SendAsync(
            string body,
            long? timestamp = null,
            string? signature = null,
            string channelId = "build-bot",
            bool ready = true)
        {
            _ready = ready;
            var unix = timestamp ?? Now.ToUnixTimeSeconds();
            var bytes = Encoding.UTF8.GetBytes(body);
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"channels/{channelId}/messages")
            {
                Content = new ByteArrayContent(bytes),
            };
            request.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            request.Headers.TryAddWithoutValidation(
                WebhookChannelServer.TimestampHeader,
                unix.ToString(System.Globalization.CultureInfo.InvariantCulture));
            request.Headers.TryAddWithoutValidation(
                WebhookChannelServer.SignatureHeader,
                signature ?? Sign(unix, bytes));
            return await Client.SendAsync(
                request,
                TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            _lifetime.Cancel();
            await _server;
            _lifetime.Dispose();
        }

        private static string Sign(long timestamp, byte[] body)
        {
            using var hmac = IncrementalHash.CreateHMAC(
                HashAlgorithmName.SHA256,
                Encoding.UTF8.GetBytes(Secret));
            hmac.AppendData(Encoding.ASCII.GetBytes(
                timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture) + "."));
            hmac.AppendData(body);
            return "v1=" + Convert.ToHexString(hmac.GetHashAndReset()).ToLowerInvariant();
        }

        private static int GetAvailablePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }

    private sealed class RecordingSink : IChannelInboundSink
    {
        public List<ChannelInboundRequest> Accepted { get; } = [];

        public ValueTask<ChannelInboundReceipt> AcceptAsync(
            ChannelInboundRequest request,
            CancellationToken cancellationToken = default)
        {
            Accepted.Add(request);
            return ValueTask.FromResult(new ChannelInboundReceipt(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                Duplicate: false));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RedirectHandler : HttpMessageHandler
    {
        public int Count { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://redirect.example.test/") },
            });
        }
    }

    private sealed class RepeatingStream : Stream
    {
        private readonly long _length;
        private long _remaining;

        public RepeatingStream(long length)
        {
            _length = length;
            _remaining = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _length - _remaining; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(count, _remaining);
            Array.Fill(buffer, (byte)'x', offset, read);
            _remaining -= read;
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
