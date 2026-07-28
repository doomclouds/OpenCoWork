using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OpenCoWork.Abstractions;

namespace OpenCoWork.Core.Tools;

internal delegate ValueTask<IPAddress[]> WebAddressResolver(
    string host,
    CancellationToken cancellationToken);

internal delegate ValueTask<Stream> WebConnector(
    IReadOnlyList<IPAddress> addresses,
    int port,
    CancellationToken cancellationToken);

internal sealed class CoreWebTool
{
    private const int MaximumRedirects = 5;
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly WebConnector _connector;
    private readonly WebAddressResolver _resolver;

    public CoreWebTool()
        : this(ResolveAsync, ConnectAsync)
    {
    }

    internal CoreWebTool(
        WebAddressResolver resolver,
        WebConnector connector)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
    }

    public async ValueTask<ToolBindingResult> FetchAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = ParseUri(RequiredString(arguments, "url"));
            var method = arguments.TryGetProperty("method", out var methodValue)
                ? methodValue.GetString()
                : "GET";
            if (method is not ("GET" or "HEAD"))
            {
                return Failure(
                    ToolErrorCodes.InputInvalid,
                    "Web method is invalid.");
            }

            for (var redirectCount = 0;
                 redirectCount <= MaximumRedirects;
                 redirectCount++)
            {
                var addresses = await ResolveTargetAsync(
                    uri,
                    cancellationToken);
                using var handler = new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    AutomaticDecompression =
                        DecompressionMethods.GZip |
                        DecompressionMethods.Deflate |
                        DecompressionMethods.Brotli,
                    UseCookies = false,
                    UseProxy = false,
                    Credentials = null,
                    ConnectCallback = async (context, token) =>
                    {
                        if (!string.Equals(
                                context.DnsEndPoint.Host,
                                uri.IdnHost,
                                StringComparison.OrdinalIgnoreCase) ||
                            context.DnsEndPoint.Port != EffectivePort(uri))
                        {
                            throw new HttpRequestException(
                                "Validated web target changed before connection.");
                        }

                        return await _connector(
                            addresses,
                            context.DnsEndPoint.Port,
                            token);
                    },
                };
                using var client = new HttpClient(
                    handler,
                    disposeHandler: false)
                {
                    Timeout = Timeout.InfiniteTimeSpan,
                };
                using var request = new HttpRequestMessage(
                    method == "HEAD" ? HttpMethod.Head : HttpMethod.Get,
                    uri);
                using var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                if (IsRedirect(response.StatusCode) &&
                    response.Headers.Location is { } location)
                {
                    if (redirectCount == MaximumRedirects)
                    {
                        return Failure(
                            ToolErrorCodes.ExecutionFailed,
                            "Web redirect limit exceeded.");
                    }

                    uri = ParseUri(new Uri(uri, location).AbsoluteUri);
                    continue;
                }

                var contentType =
                    response.Content.Headers.ContentType?.MediaType;
                var body = string.Empty;
                if (method == "GET")
                {
                    if (!IsTextMediaType(contentType) ||
                        !IsUtf8Charset(
                            response.Content.Headers.ContentType?.CharSet))
                    {
                        return Failure(
                            ToolErrorCodes.ContentUnsupported,
                            "Web response content is unsupported.");
                    }

                    var bytes = await ReadBoundedAsync(
                        response.Content,
                        cancellationToken);
                    if (bytes is null)
                    {
                        return Failure(
                            ToolErrorCodes.OutputLimitExceeded,
                            "Web response exceeds the size limit.");
                    }

                    try
                    {
                        body = StrictUtf8.GetString(bytes);
                    }
                    catch (DecoderFallbackException)
                    {
                        return Failure(
                            ToolErrorCodes.ContentUnsupported,
                            "Web response is not valid UTF-8 text.");
                    }

                    if (!IsSupportedText(body))
                    {
                        return Failure(
                            ToolErrorCodes.ContentUnsupported,
                            "Web response content is unsupported.");
                    }
                }

                var output = JsonSerializer.SerializeToElement(new
                {
                    url = uri.AbsoluteUri,
                    method,
                    statusCode = (int)response.StatusCode,
                    reasonPhrase = response.ReasonPhrase ?? string.Empty,
                    contentType = contentType ?? string.Empty,
                    redirectCount,
                    body,
                });
                if (JsonSerializer.SerializeToUtf8Bytes(output).Length >
                    ToolRuntimeLimits.MaximumBindingResultBytes)
                {
                    return Failure(
                        ToolErrorCodes.OutputLimitExceeded,
                        "Web response exceeds the size limit.");
                }

                return ToolBindingResult.Success(output);
            }

            throw new InvalidOperationException("Unreachable redirect state.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CoreWebException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or
                SocketException or InvalidOperationException)
        {
            return Failure(
                ToolErrorCodes.ExecutionFailed,
                "Web request failed.");
        }
    }

    private async ValueTask<IPAddress[]> ResolveTargetAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(uri.IdnHost, out var literal)
                ? [literal]
                : await _resolver(uri.IdnHost, cancellationToken);
        }
        catch (Exception exception) when (
            exception is SocketException or ArgumentException)
        {
            throw Denied();
        }

        addresses = addresses
            .Distinct()
            .ToArray();
        if (addresses.Length == 0 || addresses.Any(IsDeniedAddress))
        {
            throw Denied();
        }

        return addresses;
    }

    private static Uri ParseUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            string.IsNullOrWhiteSpace(uri.IdnHost))
        {
            throw Denied();
        }

        return uri;
    }

    private static bool IsDeniedAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
        {
            return bytes[0] is 0 or 10 or 127 ||
                   bytes[0] == 100 && bytes[1] is >= 64 and <= 127 ||
                   bytes[0] == 169 && bytes[1] == 254 ||
                   bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                   bytes[0] == 192 && bytes[1] == 168 ||
                   bytes[0] == 192 && bytes[1] == 0 && bytes[2] is 0 or 2 ||
                   bytes[0] == 198 && bytes[1] is 18 or 19 ||
                   bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100 ||
                   bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113 ||
                   bytes[0] >= 224 ||
                   address.Equals(IPAddress.Parse("168.63.129.16"));
        }

        return bytes.Length != 16 ||
               (bytes[0] & 0xE0) != 0x20 ||
               HasPrefix(bytes, [0x20, 0x01, 0x00, 0x00], 32) ||
               HasPrefix(bytes, [0x20, 0x01, 0x00, 0x02, 0x00, 0x00], 48) ||
               HasPrefix(bytes, [0x20, 0x01, 0x0D, 0xB8], 32) ||
               HasPrefix(bytes, [0x20, 0x02], 16);
    }

    private static bool HasPrefix(
        ReadOnlySpan<byte> address,
        ReadOnlySpan<byte> prefix,
        int prefixLength)
    {
        var wholeBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;
        if (!address[..wholeBytes].SequenceEqual(prefix[..wholeBytes]))
        {
            return false;
        }

        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (address[wholeBytes] & mask) ==
               (prefix[wholeBytes] & mask);
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is
            HttpStatusCode.MovedPermanently or
            HttpStatusCode.Redirect or
            HttpStatusCode.SeeOther or
            HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.PermanentRedirect;

    private static bool IsTextMediaType(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return false;
        }

        return mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
               mediaType.Equals(
                   "application/json",
                   StringComparison.OrdinalIgnoreCase) ||
               mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase) ||
               mediaType.Equals(
                   "application/xml",
                   StringComparison.OrdinalIgnoreCase) ||
               mediaType.EndsWith("+xml", StringComparison.OrdinalIgnoreCase) ||
               mediaType.Equals(
                   "application/javascript",
                   StringComparison.OrdinalIgnoreCase) ||
               mediaType.Equals(
                   "application/ecmascript",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUtf8Charset(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset))
        {
            return true;
        }

        charset = charset.Trim().Trim('"');
        return charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase) ||
               charset.Equals("utf8", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSupportedText(string content) =>
        !content.Any(character =>
            char.IsControl(character) &&
            character is not '\t' and not '\n' and not '\r');

    private static async Task<byte[]?> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength >
            ToolRuntimeLimits.MaximumBindingResultBytes)
        {
            return null;
        }

        await using var stream = await content.ReadAsStreamAsync(
            cancellationToken);
        var bytes = new byte[ToolRuntimeLimits.MaximumBindingResultBytes + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await stream.ReadAsync(
                bytes.AsMemory(length, bytes.Length - length),
                cancellationToken);
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        return length > ToolRuntimeLimits.MaximumBindingResultBytes
            ? null
            : bytes.AsSpan(0, length).ToArray();
    }

    private static ValueTask<IPAddress[]> ResolveAsync(
        string host,
        CancellationToken cancellationToken) =>
        new(Dns.GetHostAddressesAsync(
            host,
            AddressFamily.Unspecified,
            cancellationToken));

    private static async ValueTask<Stream> ConnectAsync(
        IReadOnlyList<IPAddress> addresses,
        int port,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(
                address.AddressFamily,
                SocketType.Stream,
                ProtocolType.Tcp)
            {
                NoDelay = true,
            };
            try
            {
                await socket.ConnectAsync(
                    new IPEndPoint(address, port),
                    cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception exception) when (
                exception is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                failure = exception;
                if (exception is OperationCanceledException)
                {
                    throw;
                }
            }
        }

        throw new HttpRequestException(
            "Unable to connect to the validated web target.",
            failure);
    }

    private static int EffectivePort(Uri uri) =>
        uri.IsDefaultPort
            ? uri.Scheme == "https" ? 443 : 80
            : uri.Port;

    private static string RequiredString(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            throw new CoreWebException(
                ToolErrorCodes.InputInvalid,
                "Web arguments are invalid.");
        }

        return value.GetString()!;
    }

    private static CoreWebException Denied() =>
        new(
            ToolErrorCodes.NetworkTargetDenied,
            "Web target is denied.");

    private static ToolBindingResult Failure(string code, string message) =>
        ToolBindingResult.Failure(new SessionError(
            code,
            message,
            IsRetryable: false));

    private sealed class CoreWebException(string code, string message)
        : Exception(message)
    {
        public string Code { get; } = code;
    }
}
