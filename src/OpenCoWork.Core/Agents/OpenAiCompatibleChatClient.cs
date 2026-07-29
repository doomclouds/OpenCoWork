using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Capabilities;

namespace OpenCoWork.Core.Agents;

internal sealed class OpenAiCompatibleChatClient : IChatCompletionClient
{
    private const int MaximumEventBytes = 1024 * 1024;
    private const int MaximumBodyBytes = 16 * 1024 * 1024;
    private const int MaximumErrorBodyBytes = 64 * 1024;
    private const int MaximumOutputBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly ProviderAuthPlacement _authPlacement;
    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;
    private readonly string? _secret;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _responseHeaderTimeout;
    private readonly TimeSpan _streamIdleTimeout;

    public OpenAiCompatibleChatClient(
        HttpClient httpClient,
        Uri baseUri,
        string apiKey,
        TimeProvider? timeProvider = null)
        : this(
            httpClient,
            baseUri,
            apiKey,
            ProviderAuthPlacement.Bearer,
            DefaultTimeout,
            DefaultTimeout,
            timeProvider)
    {
    }

    internal OpenAiCompatibleChatClient(
        HttpClient httpClient,
        Uri baseUri,
        string? secret,
        ProviderAuthPlacement authPlacement,
        TimeSpan responseHeaderTimeout,
        TimeSpan streamIdleTimeout,
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentNullException.ThrowIfNull(baseUri);
        _endpoint = new Uri(
            new Uri(
                baseUri.AbsoluteUri.TrimEnd('/') + "/",
                UriKind.Absolute),
            "chat/completions");
        _authPlacement = authPlacement ??
                         throw new ArgumentNullException(nameof(authPlacement));
        if (_authPlacement.Kind != ProviderAuthPlacementKind.None &&
            string.IsNullOrWhiteSpace(secret))
        {
            throw new ArgumentException("Provider secret cannot be empty.", nameof(secret));
        }

        if (responseHeaderTimeout <= TimeSpan.Zero ||
            streamIdleTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(responseHeaderTimeout));
        }

        _secret = secret;
        _responseHeaderTimeout = responseHeaderTimeout;
        _streamIdleTimeout = streamIdleTimeout;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async IAsyncEnumerable<ChatCompletionEvent> StreamAsync(
        ChatCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var message = CreateRequest(request);
        using var response = await SendAsync(message, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await ThrowHttpErrorAsync(
                response,
                _timeProvider.GetUtcNow(),
                cancellationToken);
        }

        if (!string.Equals(
                response.Content.Headers.ContentType?.MediaType,
                "text/event-stream",
                StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidStream();
        }

        await using var stream =
            await response.Content.ReadAsStreamAsync(cancellationToken);
        var reader = new SseReader(stream, _timeProvider, _streamIdleTimeout);
        ChatCompletionFinishReason? finishReason = null;
        var toolCalls = new List<ToolCallFrame>();
        var outputBytes = 0;
        while (true)
        {
            var data = await reader.ReadEventAsync(cancellationToken);
            if (data is null)
            {
                throw InvalidStream();
            }

            if (string.Equals(data, "[DONE]", StringComparison.Ordinal))
            {
                if (finishReason is null)
                {
                    throw InvalidStream();
                }

                if (finishReason == ChatCompletionFinishReason.ToolCall)
                {
                    foreach (var toolCall in CompleteToolCalls(toolCalls))
                    {
                        yield return toolCall;
                    }
                }
                else if (toolCalls.Count != 0)
                {
                    throw InvalidStream();
                }

                yield return new ChatCompletionCompletedEvent(finishReason.Value);
                yield break;
            }

            var chunk = ParseChunk(data);
            if (chunk.FinishReason is { } candidate)
            {
                if (finishReason is not null && finishReason != candidate)
                {
                    throw InvalidStream();
                }

                finishReason = candidate;
            }

            foreach (var item in chunk.Events)
            {
                if (item is ChatCompletionContentDeltaEvent content)
                {
                    outputBytes = AddOutputBytes(outputBytes, content.Delta);
                }
                else if (item is ChatCompletionReasoningDeltaEvent reasoning)
                {
                    outputBytes = AddOutputBytes(outputBytes, reasoning.Delta);
                }
                else if (item is ChatCompletionToolCallDeltaEvent toolCall)
                {
                    outputBytes = AddOutputBytes(
                        outputBytes,
                        (toolCall.Id ?? string.Empty) +
                        (toolCall.Name ?? string.Empty) +
                        toolCall.ArgumentsDelta);
                    AddToolCallDelta(toolCalls, toolCall);
                }

                yield return item;
            }
        }
    }

    internal static HttpClient CreateSharedHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression =
                DecompressionMethods.GZip |
                DecompressionMethods.Deflate |
                DecompressionMethods.Brotli,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            ResponseDrainTimeout = TimeSpan.FromSeconds(2),
            UseCookies = false,
        };
        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    private HttpRequestMessage CreateRequest(ChatCompletionRequest request)
    {
        var body = SerializeRequest(request);
        var content = new ByteArrayContent(body);
        content.Headers.ContentType =
            new MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8",
            };
        var message = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = content,
        };
        if (_authPlacement.Kind == ProviderAuthPlacementKind.Bearer)
        {
            message.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", _secret);
        }
        else if (_authPlacement.Kind == ProviderAuthPlacementKind.Header)
        {
            message.Headers.TryAddWithoutValidation(
                _authPlacement.HeaderName!,
                _secret);
        }
        message.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return message;
    }

    private static byte[] SerializeRequest(ChatCompletionRequest request)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("model", request.ModelId);
        writer.WriteStartArray("messages");
        foreach (var message in request.Messages)
        {
            WriteMessage(writer, message);
        }

        writer.WriteEndArray();
        if (request.Purpose == ChatCompletionInvocationPurpose.Response &&
            request.Tools.Count != 0)
        {
            writer.WriteStartArray("tools");
            foreach (var tool in request.Tools)
            {
                writer.WriteStartObject();
                writer.WriteString("type", "function");
                writer.WriteStartObject("function");
                writer.WriteString("name", tool.ProviderName);
                writer.WriteString("description", tool.Description);
                writer.WritePropertyName("parameters");
                tool.InputSchema.WriteTo(writer);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        writer.WriteBoolean("stream", true);
        writer.WriteStartObject("stream_options");
        writer.WriteBoolean("include_usage", true);
        writer.WriteEndObject();
        writer.WriteNumber("max_tokens", request.MaxOutputTokens);
        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteMessage(
        Utf8JsonWriter writer,
        ChatCompletionMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(message.Content);
        var role = message.Role switch
        {
            ChatCompletionMessageRole.System => "system",
            ChatCompletionMessageRole.User => "user",
            ChatCompletionMessageRole.Assistant => "assistant",
            ChatCompletionMessageRole.Tool => "tool",
            _ => throw new ArgumentOutOfRangeException(
                nameof(message),
                "Message role is invalid."),
        };
        var toolCalls = message.ToolCalls ?? [];
        if (message.Role == ChatCompletionMessageRole.Tool)
        {
            if (string.IsNullOrWhiteSpace(message.ToolCallId) ||
                toolCalls.Count != 0)
            {
                throw new ArgumentException("Tool message is invalid.", nameof(message));
            }
        }
        else if (message.ToolCallId is not null ||
                 message.Role != ChatCompletionMessageRole.Assistant &&
                 toolCalls.Count != 0)
        {
            throw new ArgumentException("Message tool fields are invalid.", nameof(message));
        }

        writer.WriteStartObject();
        writer.WriteString("role", role);
        if (message.Role == ChatCompletionMessageRole.Assistant &&
            toolCalls.Count != 0 &&
            message.Content.Length == 0)
        {
            writer.WriteNull("content");
        }
        else
        {
            writer.WriteString("content", message.Content);
        }

        if (toolCalls.Count != 0)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            writer.WriteStartArray("tool_calls");
            foreach (var toolCall in toolCalls)
            {
                if (string.IsNullOrWhiteSpace(toolCall.Id) ||
                    string.IsNullOrWhiteSpace(toolCall.Name) ||
                    string.IsNullOrWhiteSpace(toolCall.Arguments) ||
                    !ids.Add(toolCall.Id))
                {
                    throw new ArgumentException(
                        "Assistant tool call is invalid.",
                        nameof(message));
                }

                writer.WriteStartObject();
                writer.WriteString("id", toolCall.Id);
                writer.WriteString("type", "function");
                writer.WriteStartObject("function");
                writer.WriteString("name", toolCall.Name);
                writer.WriteString("arguments", toolCall.Arguments);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        if (message.Role == ChatCompletionMessageRole.Tool)
        {
            writer.WriteString("tool_call_id", message.ToolCallId);
        }

        writer.WriteEndObject();
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(
            _responseHeaderTimeout,
            _timeProvider);
        using var requestCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeout.Token);
        try
        {
            return await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                requestCancellation.Token);
        }
        catch (OperationCanceledException) when (
            timeout.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            throw ProviderTimeout();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException exception) when (
            exception.HttpRequestError == HttpRequestError.SecureConnectionError ||
            exception.InnerException is AuthenticationException)
        {
            throw new ChatCompletionException(
                AgentErrorCodes.ProviderTlsFailure,
                "Provider TLS validation failed.");
        }
        catch (HttpRequestException)
        {
            throw new ChatCompletionException(
                AgentErrorCodes.ProviderServerUnavailable,
                "Provider transport failed.",
                isTransient: true);
        }
    }

    private static async Task ThrowHttpErrorAsync(
        HttpResponseMessage response,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var body = await ReadBoundedAsync(
            response.Content,
            MaximumErrorBodyBytes,
            cancellationToken);
        var status = response.StatusCode;
        var retryAfter = ReadRetryAfter(response, now);
        var promptTooLong = IsPromptTooLong(status, body);
        var (code, transient) = status switch
        {
            HttpStatusCode.Unauthorized =>
                (AgentErrorCodes.ProviderAuthenticationFailed, false),
            HttpStatusCode.PaymentRequired =>
                (AgentErrorCodes.ProviderQuotaExceeded, false),
            HttpStatusCode.Forbidden =>
                (AgentErrorCodes.ProviderPermissionDenied, false),
            HttpStatusCode.NotFound =>
                (AgentErrorCodes.ProviderNotFound, false),
            HttpStatusCode.RequestTimeout =>
                (AgentErrorCodes.ProviderTimeout, true),
            HttpStatusCode.TooManyRequests =>
                (AgentErrorCodes.ProviderRateLimited, true),
            HttpStatusCode.InternalServerError or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout =>
                (AgentErrorCodes.ProviderServerUnavailable, true),
            >= HttpStatusCode.InternalServerError =>
                (AgentErrorCodes.ProviderServerUnavailable, false),
            >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest =>
                (AgentErrorCodes.ProviderRedirectNotAllowed, false),
            _ => (AgentErrorCodes.ProviderInvalidRequest, false),
        };
        throw new ChatCompletionException(
            code,
            $"Provider request failed with HTTP {(int)status}.",
            status,
            retryAfter,
            transient,
            promptTooLong);
    }

    private static bool IsPromptTooLong(
        HttpStatusCode status,
        byte[] body)
    {
        if (status is not (
            HttpStatusCode.BadRequest or
            HttpStatusCode.RequestEntityTooLarge or
            HttpStatusCode.UnprocessableEntity))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var error = document.RootElement.GetProperty("error");
            if (!string.Equals(
                    error.GetProperty("code").GetString(),
                    "InvalidParameter",
                    StringComparison.Ordinal))
            {
                return false;
            }

            var message = error.GetProperty("message").GetString();
            return message?.StartsWith(
                       "Range of input length should be [",
                       StringComparison.Ordinal) == true ||
                   message?.StartsWith(
                       "Total message token length exceed model limit (",
                       StringComparison.Ordinal) == true;
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException or
            KeyNotFoundException)
        {
            return false;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var block = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(block, cancellationToken);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > maximumBytes)
            {
                throw new ChatCompletionException(
                    AgentErrorCodes.ProviderOutputTooLarge,
                    "Provider error response exceeded the size limit.");
            }

            buffer.Write(block, 0, read);
        }
    }

    private static TimeSpan? ReadRetryAfter(
        HttpResponseMessage response,
        DateTimeOffset now)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var value = date - now;
            return value > TimeSpan.Zero ? value : TimeSpan.Zero;
        }

        return null;
    }

    private static ParsedChunk ParseChunk(string data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            var choices = root.GetProperty("choices");
            if (choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() > 1)
            {
                throw InvalidStream();
            }

            var events = new List<ChatCompletionEvent>();
            ChatCompletionFinishReason? finishReason = null;
            if (choices.GetArrayLength() == 1)
            {
                var choice = choices[0];
                if (choice.GetProperty("index").GetInt32() != 0 ||
                    choice.GetProperty("delta").ValueKind != JsonValueKind.Object)
                {
                    throw InvalidStream();
                }

                var delta = choice.GetProperty("delta");
                AddStringDelta(
                    delta,
                    "content",
                    value => events.Add(
                        new ChatCompletionContentDeltaEvent(value)));
                AddStringDelta(
                    delta,
                    "reasoning_content",
                    value => events.Add(
                        new ChatCompletionReasoningDeltaEvent(value)));
                AddToolCallDeltas(delta, events);
                if (choice.TryGetProperty("finish_reason", out var finish) &&
                    finish.ValueKind != JsonValueKind.Null)
                {
                    finishReason = finish.GetString() switch
                    {
                        "stop" => ChatCompletionFinishReason.Stop,
                        "length" => ChatCompletionFinishReason.Length,
                        "content_filter" =>
                            ChatCompletionFinishReason.ContentFilter,
                        "tool_calls" => ChatCompletionFinishReason.ToolCall,
                        _ => ChatCompletionFinishReason.Unknown,
                    };
                }
            }

            if (root.TryGetProperty("usage", out var usage) &&
                usage.ValueKind != JsonValueKind.Null)
            {
                var value = new ChatCompletionUsage(
                    ReadNonNegativeInt(usage, "prompt_tokens"),
                    ReadNonNegativeInt(usage, "completion_tokens"),
                    ReadNonNegativeInt(usage, "total_tokens"));
                events.Add(new ChatCompletionUsageEvent(value));
            }

            if (choices.GetArrayLength() == 0 && events.Count == 0)
            {
                throw InvalidStream();
            }

            return new ParsedChunk(events, finishReason);
        }
        catch (ChatCompletionException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException or
            KeyNotFoundException or FormatException or OverflowException)
        {
            throw InvalidStream();
        }
    }

    private static void AddToolCallDeltas(
        JsonElement delta,
        List<ChatCompletionEvent> events)
    {
        if (!delta.TryGetProperty("tool_calls", out var toolCalls))
        {
            return;
        }

        if (toolCalls.ValueKind != JsonValueKind.Array ||
            toolCalls.GetArrayLength() == 0)
        {
            throw InvalidStream();
        }

        var indices = new HashSet<int>();
        foreach (var toolCall in toolCalls.EnumerateArray())
        {
            if (toolCall.ValueKind != JsonValueKind.Object ||
                !toolCall.TryGetProperty("index", out var indexValue) ||
                !indexValue.TryGetInt32(out var index) ||
                index < 0 ||
                !indices.Add(index) ||
                !toolCall.TryGetProperty("function", out var function) ||
                function.ValueKind != JsonValueKind.Object)
            {
                throw InvalidStream();
            }

            var id = OptionalString(toolCall, "id");
            var name = OptionalString(function, "name");
            var arguments = OptionalString(function, "arguments") ?? string.Empty;
            events.Add(new ChatCompletionToolCallDeltaEvent(
                index,
                id,
                name,
                arguments));
        }
    }

    private static string? OptionalString(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property))
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw InvalidStream();
        }

        return property.GetString();
    }

    private static void AddToolCallDelta(
        List<ToolCallFrame> frames,
        ChatCompletionToolCallDeltaEvent delta)
    {
        ToolCallFrame frame;
        if (delta.Index == frames.Count)
        {
            frame = new ToolCallFrame(delta.Index);
            frames.Add(frame);
        }
        else if (delta.Index >= 0 && delta.Index < frames.Count)
        {
            frame = frames[delta.Index];
        }
        else
        {
            throw InvalidStream();
        }

        if (delta.Id is { } id)
        {
            if (id.Length == 0 ||
                frame.Id is not null &&
                !string.Equals(frame.Id, id, StringComparison.Ordinal))
            {
                throw InvalidStream();
            }

            frame.Id = id;
        }

        if (delta.Name is { } name)
        {
            if (name.Length == 0 ||
                frame.Name is not null &&
                !string.Equals(frame.Name, name, StringComparison.Ordinal))
            {
                throw InvalidStream();
            }

            frame.Name = name;
        }

        frame.Arguments.Append(delta.ArgumentsDelta);
        frame.ArgumentBytes = checked(
            frame.ArgumentBytes +
            StrictUtf8.GetByteCount(delta.ArgumentsDelta));
        if (frame.ArgumentBytes >
            ToolRuntimeLimits.MaximumArgumentsBytes)
        {
            throw InvalidStream();
        }
    }

    private static IReadOnlyList<ChatCompletionToolCallCompletedEvent>
        CompleteToolCalls(IReadOnlyList<ToolCallFrame> frames)
    {
        if (frames.Count == 0)
        {
            throw InvalidStream();
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var completed = new List<ChatCompletionToolCallCompletedEvent>(frames.Count);
        foreach (var frame in frames)
        {
            var arguments = frame.Arguments.ToString();
            if (string.IsNullOrWhiteSpace(frame.Id) ||
                string.IsNullOrWhiteSpace(frame.Name) ||
                string.IsNullOrWhiteSpace(arguments) ||
                !ids.Add(frame.Id))
            {
                throw InvalidStream();
            }

            try
            {
                using var document = JsonDocument.Parse(
                    arguments,
                    new JsonDocumentOptions
                    {
                        MaxDepth = ToolRuntimeLimits.MaximumJsonDepth,
                    });
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw InvalidStream();
                }
            }
            catch (ChatCompletionException)
            {
                throw;
            }
            catch (JsonException)
            {
                throw InvalidStream();
            }

            completed.Add(new ChatCompletionToolCallCompletedEvent(
                frame.Index,
                frame.Id,
                frame.Name,
                arguments));
        }

        return completed;
    }

    private static void AddStringDelta(
        JsonElement delta,
        string name,
        Action<string> add)
    {
        if (!delta.TryGetProperty(name, out var value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw InvalidStream();
        }

        var text = value.GetString()!;
        if (text.Length != 0)
        {
            add(text);
        }
    }

    private static int ReadNonNegativeInt(JsonElement value, string name)
    {
        var result = value.GetProperty(name).GetInt32();
        return result >= 0 ? result : throw InvalidStream();
    }

    private static int AddOutputBytes(int current, string delta)
    {
        var total = checked(current + StrictUtf8.GetByteCount(delta));
        return total <= MaximumOutputBytes
            ? total
            : throw new ChatCompletionException(
                AgentErrorCodes.ProviderOutputTooLarge,
                "Provider output exceeded the size limit.");
    }

    private static ChatCompletionException InvalidStream() =>
        new(
            AgentErrorCodes.ProviderInvalidStream,
            "Provider returned an invalid streaming response.");

    private static ChatCompletionException ProviderTimeout() =>
        new(
            AgentErrorCodes.ProviderTimeout,
            "Provider response timed out.",
            isTransient: true);

    private sealed record ParsedChunk(
        IReadOnlyList<ChatCompletionEvent> Events,
        ChatCompletionFinishReason? FinishReason);

    private sealed class ToolCallFrame(int index)
    {
        public int Index { get; } = index;

        public string? Id { get; set; }

        public string? Name { get; set; }

        public StringBuilder Arguments { get; } = new();

        public int ArgumentBytes { get; set; }
    }

    private sealed class SseReader(
        Stream stream,
        TimeProvider timeProvider,
        TimeSpan streamIdleTimeout)
    {
        private readonly byte[] _buffer = new byte[8192];
        private int _offset;
        private int _count;
        private int _bodyBytes;

        public async ValueTask<string?> ReadEventAsync(
            CancellationToken cancellationToken)
        {
            var data = new ArrayBufferWriter<byte>();
            var hasData = false;
            while (true)
            {
                var line = await ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    return hasData ? DecodeEvent(data) : null;
                }

                if (line.Length == 0)
                {
                    if (hasData)
                    {
                        return DecodeEvent(data);
                    }

                    continue;
                }

                if (line[0] == (byte)':')
                {
                    continue;
                }

                var colon = line.AsSpan().IndexOf((byte)':');
                var field = colon < 0 ? line.AsSpan() : line.AsSpan(0, colon);
                if (!field.SequenceEqual("data"u8))
                {
                    continue;
                }

                var value = colon < 0 ? ReadOnlySpan<byte>.Empty : line.AsSpan(colon + 1);
                if (!value.IsEmpty && value[0] == (byte)' ')
                {
                    value = value[1..];
                }

                if (data.WrittenCount + value.Length + 1 > MaximumEventBytes)
                {
                    throw InvalidStream();
                }

                data.Write(value);
                data.Write("\n"u8);
                hasData = true;
            }
        }

        private async ValueTask<byte[]?> ReadLineAsync(
            CancellationToken cancellationToken)
        {
            var line = new ArrayBufferWriter<byte>();
            while (true)
            {
                if (_offset == _count)
                {
                    using var timeout = new CancellationTokenSource(
                        streamIdleTimeout,
                        timeProvider);
                    using var readCancellation =
                        CancellationTokenSource.CreateLinkedTokenSource(
                            cancellationToken,
                            timeout.Token);
                    try
                    {
                        _count = await stream.ReadAsync(
                            _buffer,
                            readCancellation.Token);
                    }
                    catch (OperationCanceledException) when (
                        timeout.IsCancellationRequested &&
                        !cancellationToken.IsCancellationRequested)
                    {
                        throw ProviderTimeout();
                    }

                    _offset = 0;
                    if (_count == 0)
                    {
                        return line.WrittenCount == 0
                            ? null
                            : TrimCarriageReturn(line.WrittenSpan).ToArray();
                    }

                    _bodyBytes = checked(_bodyBytes + _count);
                    if (_bodyBytes > MaximumBodyBytes)
                    {
                        throw new ChatCompletionException(
                            AgentErrorCodes.ProviderOutputTooLarge,
                            "Provider response exceeded the size limit.");
                    }
                }

                var available = _buffer.AsSpan(_offset, _count - _offset);
                var newline = available.IndexOf((byte)'\n');
                var length = newline < 0 ? available.Length : newline;
                if (line.WrittenCount + length > MaximumEventBytes)
                {
                    throw InvalidStream();
                }

                line.Write(available[..length]);
                _offset += length + (newline >= 0 ? 1 : 0);
                if (newline >= 0)
                {
                    return TrimCarriageReturn(line.WrittenSpan).ToArray();
                }
            }
        }

        private static string DecodeEvent(ArrayBufferWriter<byte> data)
        {
            try
            {
                return StrictUtf8.GetString(data.WrittenSpan[..^1]);
            }
            catch (DecoderFallbackException)
            {
                throw InvalidStream();
            }
        }

        private static ReadOnlySpan<byte> TrimCarriageReturn(
            ReadOnlySpan<byte> line) =>
            !line.IsEmpty && line[^1] == (byte)'\r'
                ? line[..^1]
                : line;
    }
}
