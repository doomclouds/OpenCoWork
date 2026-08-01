using System.Buffers;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using OpenCoWork.Abstractions;

namespace OpenCoWork.Protocol;

public sealed class WebhookChannelBinding : IDisposable
{
    private Func<byte[]>? _acquireSecret;
    private byte[]? _secret;

    public WebhookChannelBinding(bool ready, byte[] secret)
        : this(ready, () => secret)
    {
    }

    public WebhookChannelBinding(bool ready, Func<byte[]> acquireSecret)
    {
        ArgumentNullException.ThrowIfNull(acquireSecret);
        Ready = ready;
        _acquireSecret = acquireSecret;
    }

    public bool Ready { get; }

    internal ReadOnlySpan<byte> Secret =>
        (_secret ??= (_acquireSecret ??
            throw new ObjectDisposedException(nameof(WebhookChannelBinding)))().ToArray());

    public void Dispose()
    {
        if (_secret is not null)
        {
            CryptographicOperations.ZeroMemory(_secret);
            _secret = null;
        }

        _acquireSecret = null;
    }
}

public static class WebhookChannelServer
{
    public const string TimestampHeader = "X-OpenCoWork-Timestamp";
    public const string SignatureHeader = "X-OpenCoWork-Signature";
    public const long MaximumBodyBytes = 24 * 1024 * 1024;

    private static readonly TimeSpan TimestampWindow = TimeSpan.FromMinutes(5);

    public static async Task RunAsync(
        int port,
        Func<string, WebhookChannelBinding?> resolveChannel,
        IChannelInboundSink sink,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65_535);
        ArgumentNullException.ThrowIfNull(resolveChannel);
        ArgumentNullException.ThrowIfNull(sink);
        timeProvider ??= TimeProvider.System;

        var builder = WebApplication.CreateSlimBuilder(
            new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = MaximumBodyBytes;
            options.Listen(
                IPAddress.Loopback,
                port,
                static listen => listen.Protocols = HttpProtocols.Http1);
        });
        await using var app = builder.Build();
        app.MapPost(
            "/channels/{channelId}/messages",
            (HttpContext context, string channelId) =>
                ProcessAsync(
                    context,
                    channelId,
                    resolveChannel,
                    sink,
                    timeProvider,
                    context.RequestAborted));
        await app.StartAsync(cancellationToken);
        await app.WaitForShutdownAsync(cancellationToken);
    }

    private static async Task<IResult> ProcessAsync(
        HttpContext context,
        string channelId,
        Func<string, WebhookChannelBinding?> resolveChannel,
        IChannelInboundSink sink,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        byte[] body;
        try
        {
            body = await ReadBodyAsync(context.Request, cancellationToken);
        }
        catch (WebhookRequestException exception)
        {
            return Error(exception.StatusCode, exception.Code);
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException)
        {
            return Error(StatusCodes.Status413PayloadTooLarge, ChannelErrorCodes.CapacityExceeded);
        }

        if (!TryReadSingleHeader(context.Request.Headers, TimestampHeader, out var timestampText) ||
            !long.TryParse(
                timestampText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var timestamp))
        {
            return Error(StatusCodes.Status400BadRequest, ChannelErrorCodes.AuthenticationFailed);
        }

        DateTimeOffset sent;
        try
        {
            sent = DateTimeOffset.FromUnixTimeSeconds(timestamp);
        }
        catch (ArgumentOutOfRangeException)
        {
            return Error(StatusCodes.Status400BadRequest, ChannelErrorCodes.AuthenticationFailed);
        }

        if ((timeProvider.GetUtcNow() - sent).Duration() > TimestampWindow)
        {
            return Unauthorized();
        }

        WebhookChannelBinding? binding;
        try
        {
            binding = resolveChannel(channelId);
        }
        catch
        {
            binding = null;
        }

        using (binding)
        {
            if (binding is null || !binding.Ready)
            {
                return Unauthorized();
            }

            if (!TryReadSingleHeader(context.Request.Headers, SignatureHeader, out var signature) ||
                !TryParseSignature(signature, out var actualSignature))
            {
                return Error(
                    StatusCodes.Status400BadRequest,
                    ChannelErrorCodes.AuthenticationFailed);
            }

            Span<byte> expectedSignature = stackalloc byte[32];
            try
            {
                using var hmac = IncrementalHash.CreateHMAC(
                    HashAlgorithmName.SHA256,
                    binding.Secret);
                hmac.AppendData(Encoding.ASCII.GetBytes(timestampText + "."));
                hmac.AppendData(body);
                hmac.TryGetHashAndReset(expectedSignature, out _);
            }
            catch
            {
                return Unauthorized();
            }

            if (!CryptographicOperations.FixedTimeEquals(
                    expectedSignature,
                    actualSignature))
            {
                return Unauthorized();
            }
        }

        if (!string.Equals(
                context.Request.ContentType?.Split(';', 2)[0],
                "application/json",
                StringComparison.OrdinalIgnoreCase))
        {
            return Error(
                StatusCodes.Status415UnsupportedMediaType,
                ChannelErrorCodes.SchemaInvalid);
        }

        ChannelInboundEnvelope envelope;
        try
        {
            envelope = WebhookEnvelopeParser.Parse(body);
        }
        catch (WebhookRequestException exception)
        {
            return Error(exception.StatusCode, exception.Code);
        }

        ChannelInboundReceipt receipt;
        try
        {
            receipt = await sink.AcceptAsync(
                new ChannelInboundRequest(
                    channelId,
                    Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant(),
                    envelope),
                cancellationToken);
        }
        catch (ChannelServiceException exception)
        {
            var status = exception.Code switch
            {
                ChannelErrorCodes.IdempotencyConflict or
                    ChannelErrorCodes.MessageConflict => StatusCodes.Status409Conflict,
                ChannelErrorCodes.CapacityExceeded or
                    ChannelErrorCodes.RateLimited => StatusCodes.Status429TooManyRequests,
                _ => StatusCodes.Status503ServiceUnavailable,
            };
            return Error(status, exception.Code);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Error(StatusCodes.Status503ServiceUnavailable, ChannelErrorCodes.Unavailable);
        }

        return Results.Json(
            new
            {
                receiptId = receipt.ReceiptId,
                correlationId = receipt.CorrelationId,
                duplicate = receipt.Duplicate,
            },
            statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<byte[]> ReadBodyAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaximumBodyBytes)
        {
            throw new WebhookRequestException(
                StatusCodes.Status413PayloadTooLarge,
                ChannelErrorCodes.CapacityExceeded);
        }

        await using var body = new MemoryStream(
            request.ContentLength is > 0
                ? (int)request.ContentLength.Value
                : 0);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                var read = await request.Body.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    return body.ToArray();
                }

                if (body.Length + read > MaximumBodyBytes)
                {
                    throw new WebhookRequestException(
                        StatusCodes.Status413PayloadTooLarge,
                        ChannelErrorCodes.CapacityExceeded);
                }

                await body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static bool TryReadSingleHeader(
        IHeaderDictionary headers,
        string name,
        out string value)
    {
        value = string.Empty;
        return headers.TryGetValue(name, out StringValues values) &&
               values.Count == 1 &&
               !string.IsNullOrWhiteSpace(value = values[0]!);
    }

    private static bool TryParseSignature(string value, out byte[] signature)
    {
        signature = [];
        if (value.Length != 67 ||
            !value.StartsWith("v1=", StringComparison.Ordinal) ||
            value.AsSpan(3).ContainsAnyExcept("0123456789abcdef"))
        {
            return false;
        }

        signature = Convert.FromHexString(value[3..]);
        return true;
    }

    private static IResult Unauthorized() =>
        Error(StatusCodes.Status401Unauthorized, ChannelErrorCodes.AuthenticationFailed);

    private static IResult Error(int statusCode, string code) =>
        Results.Json(new { error = new { code } }, statusCode: statusCode);
}

internal static class WebhookEnvelopeParser
{
    private const int MaximumTextBytes = 256 * 1024;
    private const int MaximumAttachmentBytes = 8 * 1024 * 1024;
    private const int MaximumTotalAttachmentBytes = 16 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static ChannelInboundEnvelope Parse(ReadOnlySpan<byte> body)
    {
        try
        {
            var reader = new Utf8JsonReader(body, new JsonReaderOptions { MaxDepth = 8 });
            Require(reader.Read() && reader.TokenType == JsonTokenType.StartObject);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int? schemaVersion = null;
            string? messageId = null;
            string? conversationId = null;
            DateTimeOffset? sentAtUtc = null;
            string? text = null;
            var textSeen = false;
            List<ChannelMediaInput>? attachments = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                Require(reader.TokenType == JsonTokenType.PropertyName);
                var property = reader.GetString()!;
                Require(seen.Add(property) && reader.Read());
                switch (property)
                {
                    case "schemaVersion":
                        Require(reader.TryGetInt32(out var version));
                        schemaVersion = version;
                        break;
                    case "messageId":
                        Require(reader.TokenType == JsonTokenType.String);
                        messageId = reader.GetString();
                        break;
                    case "conversationId":
                        Require(reader.TokenType == JsonTokenType.String);
                        conversationId = reader.GetString();
                        break;
                    case "sentAtUtc":
                        if (reader.TokenType != JsonTokenType.String ||
                            !reader.TryGetDateTimeOffset(out var sentAt))
                        {
                            throw InvalidSchema();
                        }
                        sentAtUtc = sentAt;
                        break;
                    case "text":
                        Require(reader.TokenType is JsonTokenType.String or JsonTokenType.Null);
                        text = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
                        textSeen = true;
                        break;
                    case "attachments":
                        attachments = ReadAttachments(ref reader);
                        break;
                    default:
                        throw InvalidSchema();
                }
            }

            Require(reader.TokenType == JsonTokenType.EndObject && !reader.Read());
            Require(schemaVersion == 1 &&
                    IsExternalId(messageId) &&
                    IsExternalId(conversationId) &&
                    sentAtUtc is not null &&
                    textSeen &&
                    attachments is not null);
            if (text is not null)
            {
                Require(StrictUtf8.GetByteCount(text) <= MaximumTextBytes);
            }

            var requiredVersion = schemaVersion!.Value;
            var requiredSentAt = sentAtUtc!.Value;
            var requiredAttachments = attachments!;
            Require(!string.IsNullOrEmpty(text) || requiredAttachments.Count > 0);
            ValidateMedia(requiredAttachments);
            return new ChannelInboundEnvelope(
                requiredVersion,
                messageId!,
                conversationId!,
                requiredSentAt.ToUniversalTime(),
                text,
                requiredAttachments);
        }
        catch (WebhookRequestException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or FormatException or DecoderFallbackException or
                InvalidOperationException)
        {
            throw InvalidSchema();
        }
    }

    private static List<ChannelMediaInput> ReadAttachments(ref Utf8JsonReader reader)
    {
        Require(reader.TokenType == JsonTokenType.StartArray);
        var result = new List<ChannelMediaInput>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            Require(reader.TokenType == JsonTokenType.StartObject && result.Count < 8);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? mediaType = null;
            string? displayName = null;
            string? contentBase64 = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                Require(reader.TokenType == JsonTokenType.PropertyName);
                var property = reader.GetString()!;
                Require(seen.Add(property) && reader.Read() &&
                        reader.TokenType == JsonTokenType.String);
                switch (property)
                {
                    case "mediaType":
                        mediaType = reader.GetString();
                        break;
                    case "displayName":
                        displayName = reader.GetString();
                        break;
                    case "contentBase64":
                        contentBase64 = reader.GetString();
                        break;
                    default:
                        throw InvalidSchema();
                }
            }

            Require(reader.TokenType == JsonTokenType.EndObject &&
                    seen.Count == 3 &&
                    !string.IsNullOrEmpty(mediaType) &&
                    IsDisplayName(displayName) &&
                    contentBase64 is not null);
            result.Add(new ChannelMediaInput(mediaType!, displayName!, contentBase64!));
        }

        Require(reader.TokenType == JsonTokenType.EndArray);
        return result;
    }

    private static void ValidateMedia(IReadOnlyList<ChannelMediaInput> attachments)
    {
        var total = 0;
        foreach (var attachment in attachments)
        {
            var maximumEncoded = ((MaximumAttachmentBytes + 2) / 3) * 4;
            if (attachment.ContentBase64.Length > maximumEncoded)
            {
                throw new WebhookRequestException(
                    StatusCodes.Status413PayloadTooLarge,
                    ChannelErrorCodes.CapacityExceeded);
            }

            var padding = attachment.ContentBase64.EndsWith("==", StringComparison.Ordinal)
                ? 2
                : attachment.ContentBase64.EndsWith('=') ? 1 : 0;
            var decodedLength = ((long)attachment.ContentBase64.Length / 4 * 3) - padding;
            if (decodedLength > MaximumAttachmentBytes)
            {
                throw new WebhookRequestException(
                    StatusCodes.Status413PayloadTooLarge,
                    ChannelErrorCodes.CapacityExceeded);
            }

            if (attachment.ContentBase64.Any(character =>
                    character is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or
                        >= '0' and <= '9' or '+' or '/' or '=')))
            {
                throw InvalidSchema();
            }

            var rented = ArrayPool<byte>.Shared.Rent(MaximumAttachmentBytes);
            try
            {
                if (!Convert.TryFromBase64String(
                        attachment.ContentBase64,
                        rented,
                        out var length))
                {
                    throw InvalidSchema();
                }

                total = checked(total + length);
                if (total > MaximumTotalAttachmentBytes)
                {
                    throw new WebhookRequestException(
                        StatusCodes.Status413PayloadTooLarge,
                        ChannelErrorCodes.CapacityExceeded);
                }

                if (!MediaTypeMatches(attachment.MediaType, rented.AsSpan(0, length)))
                {
                    throw new WebhookRequestException(
                        StatusCodes.Status415UnsupportedMediaType,
                        ChannelErrorCodes.MediaRejected);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            }
        }
    }

    internal static bool MediaTypeMatches(string mediaType, ReadOnlySpan<byte> content) =>
        mediaType switch
        {
            "text/plain" => IsUtf8(content),
            "application/pdf" => content.StartsWith("%PDF-"u8),
            "image/png" => content.StartsWith(
                new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }),
            "image/jpeg" => content.StartsWith(new byte[] { 0xff, 0xd8, 0xff }),
            "image/gif" => content.StartsWith("GIF87a"u8) || content.StartsWith("GIF89a"u8),
            "image/webp" => content.Length >= 12 &&
                            content[..4].SequenceEqual("RIFF"u8) &&
                            content.Slice(8, 4).SequenceEqual("WEBP"u8),
            _ => false,
        };

    private static bool IsUtf8(ReadOnlySpan<byte> content)
    {
        try
        {
            _ = StrictUtf8.GetCharCount(content);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool IsExternalId(string? value) =>
        value is not null && IsBoundedText(value, 256);

    private static bool IsDisplayName(string? value) =>
        value is not null && IsBoundedText(value, 256);

    private static bool IsBoundedText(string value, int maximumRunes)
    {
        var count = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsControl(rune) || ++count > maximumRunes)
            {
                return false;
            }
        }

        return count > 0;
    }

    private static void Require(bool condition)
    {
        if (!condition)
        {
            throw InvalidSchema();
        }
    }

    private static WebhookRequestException InvalidSchema() =>
        new(StatusCodes.Status400BadRequest, ChannelErrorCodes.SchemaInvalid);
}

internal sealed class WebhookRequestException(int statusCode, string code) : Exception
{
    public int StatusCode { get; } = statusCode;

    public string Code { get; } = code;
}

public sealed class WebhookChannelSender : IChannelSender, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly HttpClient _client;
    private readonly TimeProvider _timeProvider;

    public WebhookChannelSender()
        : this(
            new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(10),
            },
            TimeProvider.System)
    {
    }

    internal WebhookChannelSender(HttpMessageHandler handler, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public async ValueTask<ChannelSendResult> SendAsync(
        ChannelSendRequest request,
        ReadOnlyMemory<byte> secret,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.CallbackUrl.Scheme != Uri.UriSchemeHttps || secret.IsEmpty)
        {
            return new ChannelSendResult(false, false, ErrorCode: ChannelErrorCodes.DeliveryFailed);
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(request.Envelope, JsonOptions);
        var bodySha256 = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        if (!string.Equals(bodySha256, request.BodySha256, StringComparison.Ordinal))
        {
            return new ChannelSendResult(false, false, ErrorCode: ChannelErrorCodes.DeliveryFailed);
        }

        var timestamp = _timeProvider.GetUtcNow().ToUnixTimeSeconds()
            .ToString(CultureInfo.InvariantCulture);
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, secret.Span);
        hmac.AppendData(Encoding.ASCII.GetBytes(timestamp + "."));
        hmac.AppendData(body);
        var signature = "v1=" + Convert.ToHexString(hmac.GetHashAndReset()).ToLowerInvariant();
        using var message = new HttpRequestMessage(HttpMethod.Post, request.CallbackUrl)
        {
            Content = new ByteArrayContent(body),
        };
        message.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        message.Headers.TryAddWithoutValidation(WebhookChannelServer.TimestampHeader, timestamp);
        message.Headers.TryAddWithoutValidation(WebhookChannelServer.SignatureHeader, signature);

        try
        {
            using var response = await _client.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return new ChannelSendResult(true, false);
            }

            var retryable = response.StatusCode is
                HttpStatusCode.RequestTimeout or
                HttpStatusCode.TooManyRequests ||
                (int)response.StatusCode == 425 ||
                (int)response.StatusCode >= 500;
            return new ChannelSendResult(
                false,
                retryable,
                retryable ? RetryAfter(response) : null,
                ChannelErrorCodes.DeliveryFailed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or OperationCanceledException)
        {
            return new ChannelSendResult(
                false,
                true,
                ErrorCode: ChannelErrorCodes.DeliveryFailed);
        }
    }

    public void Dispose() => _client.Dispose();

    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter;
        var value = retry?.Delta ??
            (retry?.Date is { } date ? date - _timeProvider.GetUtcNow() : null);
        if (value is null || value <= TimeSpan.Zero)
        {
            return null;
        }

        return value > TimeSpan.FromMinutes(10)
            ? TimeSpan.FromMinutes(10)
            : value;
    }
}
