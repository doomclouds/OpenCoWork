using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Logging;

namespace OpenCoWork.Core.Agents;

internal sealed record DeepSeekResponsesRequest(
    string ModelId,
    string Instructions,
    IReadOnlyList<JsonElement> Input,
    int MaxOutputTokens,
    string ReasoningEffort,
    IReadOnlyList<DeepSeekResponsesTool> Tools,
    Guid InvocationId = default,
    int AttemptNumber = 1,
    ProviderInvocationPurpose Purpose = ProviderInvocationPurpose.Response);

internal delegate IAsyncEnumerable<DeepSeekResponseEvent> DeepSeekResponseStream(
    DeepSeekResponsesRequest request,
    CancellationToken cancellationToken);

internal abstract record DeepSeekResponsesTool;

internal sealed record DeepSeekFunctionTool : DeepSeekResponsesTool
{
    public DeepSeekFunctionTool(string name, string description, JsonElement parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(description);
        if (parameters.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Function parameters must be an object.", nameof(parameters));
        }

        Name = name;
        Description = description;
        Parameters = parameters.Clone();
    }

    public string Name { get; }

    public string Description { get; }

    public JsonElement Parameters { get; }
}

internal sealed record DeepSeekWebSearchTool : DeepSeekResponsesTool;

internal sealed record DeepSeekApplyPatchTool : DeepSeekResponsesTool;

internal abstract record DeepSeekResponseEvent;

internal enum DeepSeekTextKind
{
    Output,
    Reasoning,
}

internal sealed record DeepSeekTextDeltaEvent(
    string ItemKey,
    DeepSeekTextKind Kind,
    string Delta) : DeepSeekResponseEvent;

internal sealed record DeepSeekTextCompletedEvent(
    string ItemKey,
    DeepSeekTextKind Kind,
    string Text) : DeepSeekResponseEvent;

internal sealed record DeepSeekFunctionCallCompletedEvent(
    string ItemKey,
    string CallId,
    string Name,
    string Arguments) : DeepSeekResponseEvent;

internal sealed record DeepSeekCustomToolCallCompletedEvent(
    string ItemKey,
    string CallId,
    string Name,
    string Input) : DeepSeekResponseEvent;

internal enum DeepSeekWebSearchStatus
{
    InProgress,
    Searching,
    Completed,
}

internal sealed record DeepSeekWebSearchEvent(
    string ItemKey,
    string CallId,
    DeepSeekWebSearchStatus Status,
    JsonElement? ReplayItem = null) : DeepSeekResponseEvent;

internal enum DeepSeekTerminalStatus
{
    Completed,
    Incomplete,
    Failed,
}

internal sealed record DeepSeekResponsesUsage(
    int InputTokens,
    int CachedInputTokens,
    int OutputTokens,
    int ReasoningOutputTokens,
    int TotalTokens);

internal sealed record DeepSeekTerminalEvent(
    DeepSeekTerminalStatus Status,
    DeepSeekResponsesUsage? Usage,
    string? Reason = null,
    string? ErrorCode = null,
    string? ErrorDetail = null) : DeepSeekResponseEvent;

internal sealed class DeepSeekResponsesClient
{
    private const int MaximumErrorBodyBytes = 64 * 1024;
    private const int MaximumOutputBytes = 4 * 1024 * 1024;
    private const int MaximumToolInputBytes = 512 * 1024;
    private const int MaximumReplayItemBytes = 256 * 1024;
    private const int MaximumErrorDetailBytes = 16 * 1024;
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;
    private readonly string _secret;
    private readonly SecretRedactor _redactor;
    private readonly TimeSpan _responseHeaderTimeout;
    private readonly TimeSpan _streamIdleTimeout;
    private readonly TimeProvider _timeProvider;

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

    internal DeepSeekResponsesClient(
        HttpClient httpClient,
        string secret,
        SecretRedactor redactor,
        TimeSpan responseHeaderTimeout,
        TimeSpan streamIdleTimeout,
        TimeProvider? timeProvider = null)
        : this(
            httpClient,
            new Uri(ModelsConfig.BaseUrl + "/", UriKind.Absolute),
            secret,
            redactor,
            responseHeaderTimeout,
            streamIdleTimeout,
            timeProvider)
    {
    }

    internal DeepSeekResponsesClient(
        HttpClient httpClient,
        Uri baseUri,
        string secret,
        SecretRedactor redactor,
        TimeSpan responseHeaderTimeout,
        TimeSpan streamIdleTimeout,
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        if (responseHeaderTimeout <= TimeSpan.Zero || streamIdleTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(responseHeaderTimeout));
        }

        _endpoint = new Uri(
            new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute),
            "responses");
        _secret = secret;
        _responseHeaderTimeout = responseHeaderTimeout;
        _streamIdleTimeout = streamIdleTimeout;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async IAsyncEnumerable<DeepSeekResponseEvent> StreamAsync(
        DeepSeekResponsesRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var message = CreateRequest(request);
        using var response = await SendAsync(message, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await ThrowHttpErrorAsync(response, _timeProvider.GetUtcNow(), cancellationToken);
        }

        if (!string.Equals(
                response.Content.Headers.ContentType?.MediaType,
                "text/event-stream",
                StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidStream();
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var reader = new BoundedSseReader(stream, _timeProvider, _streamIdleTimeout);
        var state = new ResponseState(_redactor);
        while (true)
        {
            var data = await reader.ReadEventAsync(cancellationToken);
            if (data is null)
            {
                break;
            }

            foreach (var item in state.Process(data))
            {
                yield return item;
            }
        }

        yield return state.Complete();
    }

    private HttpRequestMessage CreateRequest(DeepSeekResponsesRequest request)
    {
        var content = new ByteArrayContent(SerializeRequest(request));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8",
        };
        var message = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = content,
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _secret);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return message;
    }

    private static byte[] SerializeRequest(DeepSeekResponsesRequest request)
    {
        if (!string.Equals(request.ModelId, ModelsConfig.FlashModelId, StringComparison.Ordinal) ||
            request.MaxOutputTokens <= 0 ||
            request.ReasoningEffort is not ("low" or "high" or "max") ||
            request.Input is null ||
            request.Tools is null ||
            string.IsNullOrEmpty(request.Instructions) && request.Input.Count == 0)
        {
            throw new ArgumentException("DeepSeek Responses request is invalid.", nameof(request));
        }

        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("model", request.ModelId);
        if (!string.IsNullOrEmpty(request.Instructions))
        {
            writer.WriteString("instructions", request.Instructions);
        }

        writer.WriteStartArray("input");
        foreach (var item in request.Input)
        {
            WriteInputItem(writer, item);
        }

        writer.WriteEndArray();
        if (request.Tools.Count != 0)
        {
            writer.WriteStartArray("tools");
            foreach (var tool in request.Tools)
            {
                WriteTool(writer, tool);
            }

            writer.WriteEndArray();
        }

        writer.WriteStartObject("reasoning");
        writer.WriteString("effort", request.ReasoningEffort);
        writer.WriteEndObject();
        writer.WriteNumber("max_output_tokens", request.MaxOutputTokens);
        writer.WriteBoolean("stream", true);
        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteInputItem(Utf8JsonWriter writer, JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("type", out var typeProperty) ||
            typeProperty.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException("Responses input item is invalid.", nameof(item));
        }

        var type = typeProperty.GetString();
        if (type == "web_search_call")
        {
            if (StrictUtf8.GetByteCount(item.GetRawText()) > MaximumReplayItemBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(item));
            }

            item.WriteTo(writer);
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("type", type);
        switch (type)
        {
            case "message":
                writer.WriteString("role", RequiredString(item, "role"));
                WriteMessageContent(writer, item.GetProperty("content"));
                break;
            case "reasoning":
                WriteOptionalString(writer, item, "id");
                writer.WriteStartArray("content");
                writer.WriteStartObject();
                writer.WriteString("type", "reasoning_text");
                writer.WriteString("text", RequiredString(item, "content"));
                writer.WriteEndObject();
                writer.WriteEndArray();
                break;
            case "function_call":
                WriteCall(writer, item, "arguments");
                break;
            case "function_call_output":
                writer.WriteString("call_id", RequiredString(item, "call_id"));
                writer.WriteString("output", RequiredString(item, "output"));
                break;
            case "custom_tool_call":
                WriteCall(writer, item, "input");
                break;
            case "custom_tool_call_output":
                writer.WriteString("call_id", RequiredString(item, "call_id"));
                writer.WriteString("output", RequiredString(item, "output"));
                break;
            default:
                throw new ArgumentException("Responses input item type is unsupported.", nameof(item));
        }

        writer.WriteEndObject();
    }

    private static void WriteMessageContent(Utf8JsonWriter writer, JsonElement content)
    {
        writer.WritePropertyName("content");
        if (content.ValueKind == JsonValueKind.String)
        {
            writer.WriteStringValue(content.GetString());
            return;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("Message content is invalid.");
        }

        writer.WriteStartArray();
        foreach (var part in content.EnumerateArray())
        {
            var type = RequiredString(part, "type");
            if (type is not ("input_text" or "output_text"))
            {
                throw new ArgumentException("Message content part is unsupported.");
            }

            writer.WriteStartObject();
            writer.WriteString("type", type);
            writer.WriteString("text", RequiredString(part, "text"));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteCall(Utf8JsonWriter writer, JsonElement item, string valueName)
    {
        writer.WriteString("call_id", RequiredString(item, "call_id"));
        writer.WriteString("name", RequiredString(item, "name"));
        var value = RequiredString(item, valueName);
        if (StrictUtf8.GetByteCount(value) > MaximumToolInputBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(item));
        }

        writer.WriteString(valueName, value);
    }

    private static void WriteTool(Utf8JsonWriter writer, DeepSeekResponsesTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        writer.WriteStartObject();
        switch (tool)
        {
            case DeepSeekFunctionTool function:
                writer.WriteString("type", "function");
                writer.WriteString("name", function.Name);
                writer.WriteString("description", function.Description);
                writer.WritePropertyName("parameters");
                function.Parameters.WriteTo(writer);
                break;
            case DeepSeekWebSearchTool:
                writer.WriteString("type", "web_search");
                break;
            case DeepSeekApplyPatchTool:
                writer.WriteString("type", "custom");
                writer.WriteString("name", "apply_patch");
                break;
            default:
                throw new ArgumentException("Responses tool type is unsupported.", nameof(tool));
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
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
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
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
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
            throw new ProviderException(
                AgentErrorCodes.ProviderTlsFailure,
                "Provider TLS validation failed.");
        }
        catch (HttpRequestException)
        {
            throw new ProviderException(
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
        _ = await ReadBoundedAsync(response.Content, MaximumErrorBodyBytes, cancellationToken);
        var status = response.StatusCode;
        var (code, transient) = status switch
        {
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity =>
                (AgentErrorCodes.ProviderInvalidRequest, false),
            HttpStatusCode.Unauthorized =>
                (AgentErrorCodes.ProviderAuthenticationFailed, false),
            HttpStatusCode.PaymentRequired =>
                (AgentErrorCodes.ProviderQuotaExceeded, false),
            HttpStatusCode.Forbidden =>
                (AgentErrorCodes.ProviderPermissionDenied, false),
            HttpStatusCode.NotFound =>
                (AgentErrorCodes.ProviderNotFound, false),
            HttpStatusCode.TooManyRequests =>
                (AgentErrorCodes.ProviderRateLimited, true),
            HttpStatusCode.InternalServerError or HttpStatusCode.ServiceUnavailable =>
                (AgentErrorCodes.ProviderServerUnavailable, true),
            >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest =>
                (AgentErrorCodes.ProviderRedirectNotAllowed, false),
            >= HttpStatusCode.InternalServerError =>
                (AgentErrorCodes.ProviderServerUnavailable, false),
            _ => (AgentErrorCodes.ProviderInvalidRequest, false),
        };
        throw new ProviderException(
            code,
            $"Provider request failed with HTTP {(int)status}.",
            status,
            ReadRetryAfter(response, now),
            transient);
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
                throw new ProviderException(
                    AgentErrorCodes.ProviderOutputTooLarge,
                    "Provider error response exceeded the size limit.");
            }

            buffer.Write(block, 0, read);
        }
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta;
        }

        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var value = date - now;
            return value > TimeSpan.Zero ? value : TimeSpan.Zero;
        }

        return null;
    }

    private static string RequiredString(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(property.GetString()))
        {
            throw InvalidStream();
        }

        return property.GetString()!;
    }

    private static void WriteOptionalString(Utf8JsonWriter writer, JsonElement value, string name)
    {
        if (value.TryGetProperty(name, out var property))
        {
            if (property.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(property.GetString()))
            {
                throw new ArgumentException("Responses input item is invalid.", nameof(value));
            }

            writer.WriteString(name, property.GetString());
        }
    }

    private static ProviderException InvalidStream() =>
        new(
            AgentErrorCodes.ProviderInvalidStream,
            "Provider returned an invalid streaming response.");

    private static ProviderException ProviderTimeout() =>
        new(
            AgentErrorCodes.ProviderTimeout,
            "Provider response timed out.",
            isTransient: true);

    private sealed class ResponseState(SecretRedactor redactor)
    {
        private readonly Dictionary<int, ItemFrame> _items = [];
        private readonly HashSet<string> _itemIds = new(StringComparer.Ordinal);
        private readonly HashSet<string> _callIds = new(StringComparer.Ordinal);
        private string? _responseId;
        private long _lastSequence = -1;
        private bool _created;
        private int _outputBytes;
        private DeepSeekTerminalEvent? _terminal;

        public IReadOnlyList<DeepSeekResponseEvent> Process(string data)
        {
            if (_terminal is not null)
            {
                throw InvalidStream();
            }

            try
            {
                using var document = JsonDocument.Parse(data);
                var root = document.RootElement;
                var type = RequiredString(root, "type");
                var sequence = ReadNonNegativeLong(root, "sequence_number");
                if (sequence <= _lastSequence || !_created && type != "response.created")
                {
                    throw InvalidStream();
                }

                _lastSequence = sequence;
                return type switch
                {
                    "response.created" => Created(root),
                    "response.in_progress" => InProgress(root),
                    "response.output_item.added" => ItemAdded(root),
                    "response.output_item.done" => ItemDone(root),
                    "response.content_part.added" => ContentPart(root, done: false),
                    "response.content_part.done" => ContentPart(root, done: true),
                    "response.output_text.delta" => TextDelta(root, DeepSeekTextKind.Output),
                    "response.output_text.done" => TextDone(root, DeepSeekTextKind.Output),
                    "response.reasoning_text.delta" =>
                        TextDelta(root, DeepSeekTextKind.Reasoning),
                    "response.reasoning_text.done" =>
                        TextDone(root, DeepSeekTextKind.Reasoning),
                    "response.function_call_arguments.delta" => CallDelta(root, custom: false),
                    "response.function_call_arguments.done" => CallDone(root, custom: false),
                    "response.custom_tool_call_input.delta" => CallDelta(root, custom: true),
                    "response.custom_tool_call_input.done" => CallDone(root, custom: true),
                    "response.web_search_call.in_progress" =>
                        WebSearch(root, DeepSeekWebSearchStatus.InProgress),
                    "response.web_search_call.searching" =>
                        WebSearch(root, DeepSeekWebSearchStatus.Searching),
                    "response.web_search_call.completed" =>
                        WebSearch(root, DeepSeekWebSearchStatus.Completed),
                    "response.completed" => Terminal(root, DeepSeekTerminalStatus.Completed),
                    "response.incomplete" => Terminal(root, DeepSeekTerminalStatus.Incomplete),
                    "response.failed" => Terminal(root, DeepSeekTerminalStatus.Failed),
                    _ => throw InvalidStream(),
                };
            }
            catch (ProviderException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is JsonException or InvalidOperationException or
                    KeyNotFoundException or FormatException or OverflowException or
                    ArgumentException or DecoderFallbackException)
            {
                throw InvalidStream();
            }
        }

        public DeepSeekTerminalEvent Complete()
        {
            if (_terminal is null || _items.Values.Any(item => !item.IsDone))
            {
                throw InvalidStream();
            }

            return _terminal;
        }

        private IReadOnlyList<DeepSeekResponseEvent> Created(JsonElement root)
        {
            if (_created)
            {
                throw InvalidStream();
            }

            var response = RequiredObject(root, "response");
            _responseId = RequiredString(response, "id");
            RequireString(response, "status", "in_progress");
            _created = true;
            return [];
        }

        private IReadOnlyList<DeepSeekResponseEvent> InProgress(JsonElement root)
        {
            var response = RequiredObject(root, "response");
            RequireResponse(response, "in_progress");
            return [];
        }

        private IReadOnlyList<DeepSeekResponseEvent> ItemAdded(JsonElement root)
        {
            var outputIndex = ReadNonNegativeInt(root, "output_index");
            var item = RequiredObject(root, "item");
            var itemId = RequiredString(item, "id");
            var type = RequiredString(item, "type");
            RequireString(item, "status", "in_progress");
            if (_items.ContainsKey(outputIndex) || !_itemIds.Add(itemId))
            {
                throw InvalidStream();
            }

            var kind = type switch
            {
                "message" => ItemKind.Output,
                "reasoning" => ItemKind.Reasoning,
                "function_call" => ItemKind.Function,
                "custom_tool_call" => ItemKind.Custom,
                "web_search_call" => ItemKind.WebSearch,
                _ => throw InvalidStream(),
            };
            string? callId = null;
            string? name = null;
            var frame = new ItemFrame(outputIndex, itemId, kind);
            if (kind is ItemKind.Function or ItemKind.Custom)
            {
                callId = RequiredString(item, "call_id");
                name = RequiredString(item, "name");
                if (!_callIds.Add(callId))
                {
                    throw InvalidStream();
                }

                var initial = RequiredStringAllowEmpty(
                    item,
                    kind == ItemKind.Function ? "arguments" : "input");
                AddBounded(frame, initial, MaximumToolInputBytes);
            }

            frame.CallId = callId;
            frame.Name = name;
            _items.Add(outputIndex, frame);
            return [];
        }

        private IReadOnlyList<DeepSeekResponseEvent> ItemDone(JsonElement root)
        {
            var outputIndex = ReadNonNegativeInt(root, "output_index");
            var item = RequiredObject(root, "item");
            var frame = GetFrame(outputIndex, RequiredString(item, "id"));
            RequireString(item, "status", "completed");
            var expectedType = frame.Kind switch
            {
                ItemKind.Output => "message",
                ItemKind.Reasoning => "reasoning",
                ItemKind.Function => "function_call",
                ItemKind.Custom => "custom_tool_call",
                ItemKind.WebSearch => "web_search_call",
                _ => throw InvalidStream(),
            };
            RequireString(item, "type", expectedType);
            if (frame.IsDone || frame.Parts.Values.Any(done => !done))
            {
                throw InvalidStream();
            }

            DeepSeekResponseEvent? result = null;
            var value = frame.Value.ToString();
            switch (frame.Kind)
            {
                case ItemKind.Output:
                case ItemKind.Reasoning:
                    if (!frame.ValueDone || !ItemTextEquals(item, frame.Kind, value))
                    {
                        throw InvalidStream();
                    }

                    break;
                case ItemKind.Function:
                    RequireCallMatches(item, frame, "arguments", value);
                    result = new DeepSeekFunctionCallCompletedEvent(
                        frame.Key,
                        frame.CallId!,
                        frame.Name!,
                        value);
                    break;
                case ItemKind.Custom:
                    RequireCallMatches(item, frame, "input", value);
                    result = new DeepSeekCustomToolCallCompletedEvent(
                        frame.Key,
                        frame.CallId!,
                        frame.Name!,
                        value);
                    break;
                case ItemKind.WebSearch:
                    if (frame.WebSearchStatus != DeepSeekWebSearchStatus.Completed)
                    {
                        throw InvalidStream();
                    }

                    var raw = item.GetRawText();
                    if (StrictUtf8.GetByteCount(raw) > MaximumReplayItemBytes)
                    {
                        throw new ProviderException(
                            AgentErrorCodes.ProviderOutputTooLarge,
                            "Provider web search replay item exceeded the size limit.");
                    }

                    result = new DeepSeekWebSearchEvent(
                        frame.Key,
                        frame.ItemId,
                        DeepSeekWebSearchStatus.Completed,
                        item.Clone());
                    break;
            }

            frame.IsDone = true;
            return result is null ? [] : [result];
        }

        private IReadOnlyList<DeepSeekResponseEvent> ContentPart(JsonElement root, bool done)
        {
            var frame = GetFrame(root);
            var contentIndex = ReadNonNegativeInt(root, "content_index");
            var part = RequiredObject(root, "part");
            var expectedType = frame.Kind switch
            {
                ItemKind.Output => "output_text",
                ItemKind.Reasoning => "reasoning_text",
                _ => throw InvalidStream(),
            };
            RequireString(part, "type", expectedType);
            if (!done)
            {
                if (!frame.Parts.TryAdd(contentIndex, false) ||
                    RequiredStringAllowEmpty(part, "text").Length != 0)
                {
                    throw InvalidStream();
                }
            }
            else if (!frame.Parts.TryGetValue(contentIndex, out var alreadyDone) ||
                     alreadyDone ||
                     !string.Equals(
                         RequiredStringAllowEmpty(part, "text"),
                         frame.Value.ToString(),
                         StringComparison.Ordinal))
            {
                throw InvalidStream();
            }
            else
            {
                frame.Parts[contentIndex] = true;
            }

            return [];
        }

        private IReadOnlyList<DeepSeekResponseEvent> TextDelta(
            JsonElement root,
            DeepSeekTextKind kind)
        {
            var frame = GetFrame(root);
            RequireKind(frame, kind);
            RequireContentIndex(root);
            if (frame.ValueDone)
            {
                throw InvalidStream();
            }

            var delta = RequiredStringAllowEmpty(root, "delta");
            _outputBytes = checked(_outputBytes + StrictUtf8.GetByteCount(delta));
            if (_outputBytes > MaximumOutputBytes)
            {
                throw new ProviderException(
                    AgentErrorCodes.ProviderOutputTooLarge,
                    "Provider output exceeded the size limit.");
            }

            frame.Value.Append(delta);
            return [new DeepSeekTextDeltaEvent(frame.Key, kind, delta)];
        }

        private IReadOnlyList<DeepSeekResponseEvent> TextDone(
            JsonElement root,
            DeepSeekTextKind kind)
        {
            var frame = GetFrame(root);
            RequireKind(frame, kind);
            RequireContentIndex(root);
            var text = RequiredStringAllowEmpty(root, "text");
            if (frame.ValueDone ||
                !string.Equals(text, frame.Value.ToString(), StringComparison.Ordinal))
            {
                throw InvalidStream();
            }

            frame.ValueDone = true;
            return [new DeepSeekTextCompletedEvent(frame.Key, kind, text)];
        }

        private IReadOnlyList<DeepSeekResponseEvent> CallDelta(JsonElement root, bool custom)
        {
            var frame = GetFrame(root);
            RequireCallKind(frame, custom);
            if (frame.ValueDone)
            {
                throw InvalidStream();
            }

            AddBounded(frame, RequiredStringAllowEmpty(root, "delta"), MaximumToolInputBytes);
            return [];
        }

        private IReadOnlyList<DeepSeekResponseEvent> CallDone(JsonElement root, bool custom)
        {
            var frame = GetFrame(root);
            RequireCallKind(frame, custom);
            var value = RequiredStringAllowEmpty(root, custom ? "input" : "arguments");
            if (frame.ValueDone ||
                !string.Equals(value, frame.Value.ToString(), StringComparison.Ordinal))
            {
                throw InvalidStream();
            }

            frame.ValueDone = true;
            return [];
        }

        private IReadOnlyList<DeepSeekResponseEvent> WebSearch(
            JsonElement root,
            DeepSeekWebSearchStatus status)
        {
            var frame = GetFrame(root);
            if (frame.Kind != ItemKind.WebSearch ||
                status switch
                {
                    DeepSeekWebSearchStatus.InProgress => frame.WebSearchStatus is not null,
                    DeepSeekWebSearchStatus.Searching =>
                        frame.WebSearchStatus != DeepSeekWebSearchStatus.InProgress,
                    DeepSeekWebSearchStatus.Completed =>
                        frame.WebSearchStatus != DeepSeekWebSearchStatus.Searching,
                    _ => true,
                })
            {
                throw InvalidStream();
            }

            frame.WebSearchStatus = status;
            return status == DeepSeekWebSearchStatus.Completed
                ? []
                : [new DeepSeekWebSearchEvent(frame.Key, frame.ItemId, status)];
        }

        private IReadOnlyList<DeepSeekResponseEvent> Terminal(
            JsonElement root,
            DeepSeekTerminalStatus status)
        {
            var response = RequiredObject(root, "response");
            RequireResponse(
                response,
                status switch
                {
                    DeepSeekTerminalStatus.Completed => "completed",
                    DeepSeekTerminalStatus.Incomplete => "incomplete",
                    DeepSeekTerminalStatus.Failed => "failed",
                    _ => throw InvalidStream(),
                });
            if (_items.Values.Any(item => !item.IsDone))
            {
                throw InvalidStream();
            }

            DeepSeekResponsesUsage? usage = null;
            string? reason = null;
            string? errorCode = null;
            string? errorDetail = null;
            if (status != DeepSeekTerminalStatus.Failed)
            {
                usage = ReadUsage(RequiredObject(response, "usage"));
            }

            if (status == DeepSeekTerminalStatus.Incomplete &&
                response.TryGetProperty("incomplete_details", out var details) &&
                details.ValueKind != JsonValueKind.Null)
            {
                reason = BoundUtf8(RequiredString(details, "reason"), MaximumErrorDetailBytes);
            }
            else if (status == DeepSeekTerminalStatus.Failed)
            {
                var error = RequiredObject(response, "error");
                var redacted = redactor.RedactJson(error, out _);
                errorCode = AgentErrorCodes.ProviderResponseFailed;
                errorDetail = BoundUtf8(redacted.GetRawText(), MaximumErrorDetailBytes);
            }

            _terminal = new DeepSeekTerminalEvent(
                status,
                usage,
                reason,
                errorCode,
                errorDetail);
            return [];
        }

        private void RequireResponse(JsonElement response, string status)
        {
            if (!string.Equals(RequiredString(response, "id"), _responseId, StringComparison.Ordinal))
            {
                throw InvalidStream();
            }

            RequireString(response, "status", status);
        }

        private ItemFrame GetFrame(JsonElement root) =>
            GetFrame(
                ReadNonNegativeInt(root, "output_index"),
                RequiredString(root, "item_id"));

        private ItemFrame GetFrame(int outputIndex, string itemId)
        {
            if (!_items.TryGetValue(outputIndex, out var frame) ||
                !string.Equals(frame.ItemId, itemId, StringComparison.Ordinal) ||
                frame.IsDone)
            {
                throw InvalidStream();
            }

            return frame;
        }

        private static void RequireKind(ItemFrame frame, DeepSeekTextKind kind)
        {
            if ((kind == DeepSeekTextKind.Output && frame.Kind != ItemKind.Output) ||
                (kind == DeepSeekTextKind.Reasoning && frame.Kind != ItemKind.Reasoning))
            {
                throw InvalidStream();
            }
        }

        private static void RequireCallKind(ItemFrame frame, bool custom)
        {
            if ((custom && frame.Kind != ItemKind.Custom) ||
                (!custom && frame.Kind != ItemKind.Function))
            {
                throw InvalidStream();
            }
        }

        private static void RequireCallMatches(
            JsonElement item,
            ItemFrame frame,
            string valueName,
            string value)
        {
            if (!frame.ValueDone ||
                !string.Equals(RequiredString(item, "call_id"), frame.CallId, StringComparison.Ordinal) ||
                !string.Equals(RequiredString(item, "name"), frame.Name, StringComparison.Ordinal) ||
                !string.Equals(
                    RequiredStringAllowEmpty(item, valueName),
                    value,
                    StringComparison.Ordinal))
            {
                throw InvalidStream();
            }
        }

        private static bool ItemTextEquals(JsonElement item, ItemKind kind, string text)
        {
            if (!item.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array ||
                content.GetArrayLength() != 1)
            {
                return false;
            }

            var part = content[0];
            var type = kind == ItemKind.Output ? "output_text" : "reasoning_text";
            return string.Equals(RequiredString(part, "type"), type, StringComparison.Ordinal) &&
                   string.Equals(
                       RequiredStringAllowEmpty(part, "text"),
                       text,
                       StringComparison.Ordinal);
        }

        private static DeepSeekResponsesUsage ReadUsage(JsonElement usage)
        {
            var input = ReadNonNegativeInt(usage, "input_tokens");
            var output = ReadNonNegativeInt(usage, "output_tokens");
            var total = ReadNonNegativeInt(usage, "total_tokens");
            var cached = ReadOptionalDetail(usage, "input_tokens_details", "cached_tokens");
            var reasoning = ReadOptionalDetail(
                usage,
                "output_tokens_details",
                "reasoning_tokens");
            if (cached > input || reasoning > output || total != input + output)
            {
                throw InvalidStream();
            }

            return new DeepSeekResponsesUsage(input, cached, output, reasoning, total);
        }

        private static int ReadOptionalDetail(
            JsonElement usage,
            string objectName,
            string valueName)
        {
            if (!usage.TryGetProperty(objectName, out var details) ||
                details.ValueKind == JsonValueKind.Null)
            {
                return 0;
            }

            if (details.ValueKind != JsonValueKind.Object)
            {
                throw InvalidStream();
            }

            return ReadNonNegativeInt(details, valueName);
        }

        private static void RequireContentIndex(JsonElement root)
        {
            if (ReadNonNegativeInt(root, "content_index") != 0)
            {
                throw InvalidStream();
            }
        }

        private static void AddBounded(ItemFrame target, string value, int maximumBytes)
        {
            var valueBytes = StrictUtf8.GetByteCount(value);
            if (target.ValueBytes + valueBytes > maximumBytes)
            {
                throw new ProviderException(
                    AgentErrorCodes.ProviderOutputTooLarge,
                    "Provider tool input exceeded the size limit.");
            }

            target.Value.Append(value);
            target.ValueBytes += valueBytes;
        }

        private sealed class ItemFrame(int outputIndex, string itemId, ItemKind kind)
        {
            public int OutputIndex { get; } = outputIndex;

            public string ItemId { get; } = itemId;

            public ItemKind Kind { get; } = kind;

            public string Key => $"{OutputIndex}:{ItemId}";

            public string? CallId { get; set; }

            public string? Name { get; set; }

            public StringBuilder Value { get; } = new();

            public int ValueBytes { get; set; }

            public Dictionary<int, bool> Parts { get; } = [];

            public bool ValueDone { get; set; }

            public bool IsDone { get; set; }

            public DeepSeekWebSearchStatus? WebSearchStatus { get; set; }
        }

        private enum ItemKind
        {
            Output,
            Reasoning,
            Function,
            Custom,
            WebSearch,
        }
    }

    private static JsonElement RequiredObject(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.Object)
        {
            throw InvalidStream();
        }

        return property;
    }

    private static string RequiredStringAllowEmpty(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            throw InvalidStream();
        }

        return property.GetString() ?? string.Empty;
    }

    private static void RequireString(JsonElement value, string name, string expected)
    {
        if (!string.Equals(
                RequiredString(value, name),
                expected,
                StringComparison.Ordinal))
        {
            throw InvalidStream();
        }
    }

    private static int ReadNonNegativeInt(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) ||
            !property.TryGetInt32(out var result) ||
            result < 0)
        {
            throw InvalidStream();
        }

        return result;
    }

    private static long ReadNonNegativeLong(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) ||
            !property.TryGetInt64(out var result) ||
            result < 0)
        {
            throw InvalidStream();
        }

        return result;
    }

    private static string BoundUtf8(string value, int maximumBytes)
    {
        if (StrictUtf8.GetByteCount(value) <= maximumBytes)
        {
            return value;
        }

        var length = value.Length;
        while (length > 0 && StrictUtf8.GetByteCount(value.AsSpan(0, length)) > maximumBytes)
        {
            length--;
        }

        return value[..length];
    }
}
