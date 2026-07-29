using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Agents;
using OpenCoWork.Core.Capabilities;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class ChatCompletionClientTests
{
    [Fact]
    public async Task Explicit_header_auth_does_not_emit_bearer_authorization()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(request =>
        {
            Assert.Null(request.Headers.Authorization);
            Assert.True(request.Headers.TryGetValues("X-Api-Key", out var values));
            Assert.Equal("header-secret", Assert.Single(values));
            return Response(
                HttpStatusCode.OK,
                """
                data: {"choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":"stop"}]}

                data: [DONE]

                """);
        });
        using var httpClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var client = new OpenAiCompatibleChatClient(
            httpClient,
            new Uri("https://provider.example/v1/"),
            "header-secret",
            new ProviderAuthPlacement(
                ProviderAuthPlacementKind.Header,
                "X-Api-Key"),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(60));

        var events = await DrainAsync(
            client.StreamAsync(Request(), cancellationToken),
            cancellationToken);

        Assert.Equal(2, events.Count);
    }

    [Fact]
    public async Task Shared_http_client_completes_a_real_loopback_sse_exchange()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var server = new LoopbackSseServer(
            """
            data: {"choices":[{"index":0,"delta":{"content":"loopback"},"finish_reason":"stop"}]}

            data: [DONE]

            """);
        using var httpClient =
            OpenAiCompatibleChatClient.CreateSharedHttpClient();
        var client = new OpenAiCompatibleChatClient(
            httpClient,
            server.BaseUri,
            "loopback-secret");

        var events = await DrainAsync(
            client.StreamAsync(Request(), cancellationToken),
            cancellationToken);
        var headers = await server.RequestHeaders.WaitAsync(cancellationToken);

        Assert.Equal(2, events.Count);
        Assert.Contains(
            "POST /v1/chat/completions HTTP/1.1",
            headers,
            StringComparison.Ordinal);
        Assert.Contains(
            "Authorization: Bearer loopback-secret",
            headers,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Streams_fragmented_content_reasoning_usage_and_completion_after_done()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string secret = "provider-secret-c2f17e";
        var sse = """
                  data: {"choices":[{"index":0,"delta":{"reasoning_content":"think","content":"Hel"},"finish_reason":null}],"usage":null}

                  data: {"choices":[{"index":0,"delta":{"content":"lo"},"finish_reason":"stop"}],"usage":{"prompt_tokens":11,"completion_tokens":3,"total_tokens":14}}

                  data: [DONE]

                  """;
        var handler = new RecordingHandler(_ =>
            Response(HttpStatusCode.OK, sse, maximumReadSize: 1));
        using var httpClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var client = new OpenAiCompatibleChatClient(
            httpClient,
            new Uri("https://provider.example/v1/"),
            secret);

        var events = await DrainAsync(
            client.StreamAsync(Request(), cancellationToken),
            cancellationToken);

        Assert.Collection(
            events,
            item => Assert.Equal(
                new ChatCompletionContentDeltaEvent("Hel"),
                item),
            item => Assert.Equal(
                new ChatCompletionReasoningDeltaEvent("think"),
                item),
            item => Assert.Equal(
                new ChatCompletionContentDeltaEvent("lo"),
                item),
            item => Assert.Equal(
                new ChatCompletionUsageEvent(
                    new ChatCompletionUsage(11, 3, 14)),
                item),
            item => Assert.Equal(
                new ChatCompletionCompletedEvent(
                    ChatCompletionFinishReason.Stop),
                item));
        Assert.Equal(
            new Uri("https://provider.example/v1/chat/completions"),
            handler.RequestUri);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal(secret, handler.AuthorizationParameter);
        Assert.DoesNotContain(secret, handler.RequestBody, StringComparison.Ordinal);
        using var requestJson = JsonDocument.Parse(handler.RequestBody);
        Assert.True(requestJson.RootElement.GetProperty("stream").GetBoolean());
        Assert.True(
            requestJson.RootElement
                .GetProperty("stream_options")
                .GetProperty("include_usage")
                .GetBoolean());
        Assert.False(
            requestJson.RootElement.TryGetProperty(
                "previous_response_id",
                out _));
    }

    [Fact]
    public async Task Response_serializes_tools_and_tool_history_while_compaction_omits_tools()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var schema = JsonDocument.Parse(
            """{"type":"object","additionalProperties":false}""");
        var definition = new ChatCompletionToolDefinition(
            "file__list",
            "List files.",
            schema.RootElement);
        var messages = new[]
        {
            new ChatCompletionMessage(ChatCompletionMessageRole.System, "system"),
            new ChatCompletionMessage(
                ChatCompletionMessageRole.Assistant,
                string.Empty,
                [new ChatCompletionToolCall("call-1", "file__list", "{}")]),
            new ChatCompletionMessage(
                ChatCompletionMessageRole.Tool,
                """{"status":"completed"}""",
                ToolCallId: "call-1"),
        };
        var handler = new RecordingHandler(_ => Response(
            HttpStatusCode.OK,
            """
            data: {"choices":[{"index":0,"delta":{"content":"done"},"finish_reason":"stop"}]}

            data: [DONE]

            """));
        using var httpClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var client = new OpenAiCompatibleChatClient(
            httpClient,
            new Uri("https://provider.example/v1/"),
            "secret");

        await DrainAsync(
            client.StreamAsync(
                new ChatCompletionRequest(
                    "model",
                    messages,
                    maxOutputTokens: 32,
                    Guid.CreateVersion7(),
                    attemptNumber: 1,
                    ChatCompletionInvocationPurpose.Response,
                    [definition]),
                cancellationToken),
            cancellationToken);
        using var responseJson = JsonDocument.Parse(handler.RequestBody);
        var root = responseJson.RootElement;
        var tools = root.GetProperty("tools");
        Assert.Equal("function", tools[0].GetProperty("type").GetString());
        Assert.Equal(
            "file__list",
            tools[0].GetProperty("function").GetProperty("name").GetString());
        var requestMessages = root.GetProperty("messages");
        Assert.Equal(
            "call-1",
            requestMessages[1]
                .GetProperty("tool_calls")[0]
                .GetProperty("id")
                .GetString());
        Assert.Equal("tool", requestMessages[2].GetProperty("role").GetString());
        Assert.Equal(
            "call-1",
            requestMessages[2].GetProperty("tool_call_id").GetString());

        await DrainAsync(
            client.StreamAsync(
                new ChatCompletionRequest(
                    "model",
                    messages[..1],
                    maxOutputTokens: 32,
                    Guid.CreateVersion7(),
                    attemptNumber: 2,
                    ChatCompletionInvocationPurpose.Compaction,
                    [definition]),
                cancellationToken),
            cancellationToken);
        using var compactionJson = JsonDocument.Parse(handler.RequestBody);
        Assert.False(compactionJson.RootElement.TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task Streams_fragmented_multiple_tool_calls_only_after_a_complete_frame()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(_ => Response(
            HttpStatusCode.OK,
            """
            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call-1","function":{"name":"file__list","arguments":"{\"pa"}},{"index":1,"id":"call-2","function":{"name":"web__fetch","arguments":"{\"ur"}}]},"finish_reason":null}]}

            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"th\":\"src\"}"}},{"index":1,"function":{"arguments":"l\":\"https://example.test\"}"}}]},"finish_reason":"tool_calls"}]}

            data: [DONE]

            """,
            maximumReadSize: 1));
        using var httpClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var client = new OpenAiCompatibleChatClient(
            httpClient,
            new Uri("https://provider.example/v1/"),
            "secret");

        var events = await DrainAsync(
            client.StreamAsync(Request(), cancellationToken),
            cancellationToken);

        Assert.Equal(4, events.OfType<ChatCompletionToolCallDeltaEvent>().Count());
        Assert.Equal(
            [
                new ChatCompletionToolCallCompletedEvent(
                    0,
                    "call-1",
                    "file__list",
                    """{"path":"src"}"""),
                new ChatCompletionToolCallCompletedEvent(
                    1,
                    "call-2",
                    "web__fetch",
                    """{"url":"https://example.test"}"""),
            ],
            events.OfType<ChatCompletionToolCallCompletedEvent>());
        Assert.Equal(
            ChatCompletionFinishReason.ToolCall,
            Assert.IsType<ChatCompletionCompletedEvent>(events[^1]).FinishReason);
    }

    [Theory]
    [InlineData(
        """data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"id":"call-2","function":{"name":"file__list","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}\n\ndata: [DONE]\n\n""")]
    [InlineData(
        """data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call-1","function":{"name":"file__list","arguments":"{}"}},{"index":0,"function":{"arguments":"{}"}}]},"finish_reason":"tool_calls"}]}\n\ndata: [DONE]\n\n""")]
    [InlineData(
        """data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call-1","function":{"name":"file__list","arguments":""}}]},"finish_reason":"tool_calls"}]}\n\ndata: [DONE]\n\n""")]
    [InlineData(
        """data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call-1","function":{"name":"file__list","arguments":"{}"}}]},"finish_reason":"stop"}]}\n\ndata: [DONE]\n\n""")]
    public async Task Rejects_sparse_duplicate_incomplete_or_mismatched_tool_frames(
        string sse)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(_ => Response(
            HttpStatusCode.OK,
            sse.Replace("\\n", "\n", StringComparison.Ordinal)));
        using var httpClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var client = new OpenAiCompatibleChatClient(
            httpClient,
            new Uri("https://provider.example/v1/"),
            "secret");

        var exception = await Assert.ThrowsAsync<ChatCompletionException>(
            () => DrainAsync(
                client.StreamAsync(Request(), cancellationToken),
                cancellationToken));

        Assert.Equal(AgentErrorCodes.ProviderInvalidStream, exception.Code);
    }

    [Fact]
    public async Task Rejects_the_whole_tool_frame_before_publishing_completed_calls()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(_ => Response(
            HttpStatusCode.OK,
            """
            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call-1","function":{"name":"file__list","arguments":"{}"}},{"index":1,"id":"call-2","function":{"name":"web__fetch","arguments":""}}]},"finish_reason":"tool_calls"}]}

            data: [DONE]

            """));
        using var httpClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var client = new OpenAiCompatibleChatClient(
            httpClient,
            new Uri("https://provider.example/v1/"),
            "secret");
        var observed = new List<ChatCompletionEvent>();

        var exception = await Assert.ThrowsAsync<ChatCompletionException>(async () =>
        {
            await foreach (var item in client.StreamAsync(
                               Request(),
                               cancellationToken))
            {
                observed.Add(item);
            }
        });

        Assert.Equal(AgentErrorCodes.ProviderInvalidStream, exception.Code);
        Assert.Empty(observed.OfType<ChatCompletionToolCallCompletedEvent>());
    }

    [Fact]
    public async Task Rejects_early_eof_and_maps_http_errors_without_exposing_body()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var earlyEofHandler = new RecordingHandler(_ => Response(
            HttpStatusCode.OK,
            """
            data: {"choices":[{"index":0,"delta":{"content":"partial"},"finish_reason":"stop"}]}

            """));
        using var earlyEofHttpClient = new HttpClient(earlyEofHandler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var earlyEofClient = new OpenAiCompatibleChatClient(
            earlyEofHttpClient,
            new Uri("https://provider.example/v1/"),
            "secret");

        var invalidStream = await Assert.ThrowsAsync<ChatCompletionException>(
            () => DrainAsync(
                earlyEofClient.StreamAsync(Request(), cancellationToken),
                cancellationToken));
        Assert.Equal(AgentErrorCodes.ProviderInvalidStream, invalidStream.Code);

        const string canary = "error-body-canary-6b13";
        var rateLimitHandler = new RecordingHandler(_ =>
        {
            var response = Response(
                HttpStatusCode.TooManyRequests,
                JsonSerializer.Serialize(new
                {
                    error = new
                    {
                        message = canary,
                    },
                }));
            response.Headers.RetryAfter =
                new System.Net.Http.Headers.RetryConditionHeaderValue(
                    TimeSpan.FromSeconds(2));
            return response;
        });
        using var rateLimitHttpClient = new HttpClient(rateLimitHandler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var rateLimitClient = new OpenAiCompatibleChatClient(
            rateLimitHttpClient,
            new Uri("https://provider.example/v1/"),
            "secret");

        var rateLimited = await Assert.ThrowsAsync<ChatCompletionException>(
            () => DrainAsync(
                rateLimitClient.StreamAsync(Request(), cancellationToken),
                cancellationToken));
        Assert.Equal(AgentErrorCodes.ProviderRateLimited, rateLimited.Code);
        Assert.Equal(TimeSpan.FromSeconds(2), rateLimited.RetryAfter);
        Assert.True(rateLimited.IsTransient);
        Assert.DoesNotContain(canary, rateLimited.ToString(), StringComparison.Ordinal);

        var promptTooLongHandler = new RecordingHandler(_ => Response(
            HttpStatusCode.BadRequest,
            JsonSerializer.Serialize(new
            {
                error = new
                {
                    code = "InvalidParameter",
                    message = "Range of input length should be [1, 100].",
                },
            })));
        using var promptTooLongHttpClient = new HttpClient(promptTooLongHandler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var promptTooLongClient = new OpenAiCompatibleChatClient(
            promptTooLongHttpClient,
            new Uri("https://provider.example/v1/"),
            "secret");
        var promptTooLong = await Assert.ThrowsAsync<ChatCompletionException>(
            () => DrainAsync(
                promptTooLongClient.StreamAsync(Request(), cancellationToken),
                cancellationToken));
        Assert.Equal(AgentErrorCodes.ProviderInvalidRequest, promptTooLong.Code);
        Assert.True(promptTooLong.IsPromptTooLong);
        Assert.False(promptTooLong.IsTransient);

        var redirectHandler = new RecordingHandler(_ => Response(
            HttpStatusCode.Redirect,
            string.Empty));
        using var redirectHttpClient = new HttpClient(redirectHandler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var redirectClient = new OpenAiCompatibleChatClient(
            redirectHttpClient,
            new Uri("https://provider.example/v1/"),
            "secret");
        var redirect = await Assert.ThrowsAsync<ChatCompletionException>(
            () => DrainAsync(
                redirectClient.StreamAsync(Request(), cancellationToken),
                cancellationToken));
        Assert.Equal(
            AgentErrorCodes.ProviderRedirectNotAllowed,
            redirect.Code);
    }

    [Fact]
    public async Task Rejects_an_sse_event_over_one_mebibyte_before_json_parsing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(_ => Response(
            HttpStatusCode.OK,
            "data: " + new string('x', (1024 * 1024) + 1) + "\n\n"));
        using var httpClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var client = new OpenAiCompatibleChatClient(
            httpClient,
            new Uri("https://provider.example/v1/"),
            "secret");

        var exception = await Assert.ThrowsAsync<ChatCompletionException>(
            () => DrainAsync(
                client.StreamAsync(Request(), cancellationToken),
                cancellationToken));

        Assert.Equal(AgentErrorCodes.ProviderInvalidStream, exception.Code);
    }

    [Fact]
    public async Task Response_header_wait_times_out_with_a_typed_error()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new ManualTimerTimeProvider(
            new DateTimeOffset(2026, 7, 27, 13, 0, 0, TimeSpan.Zero));
        var handler = new BlockingHandler();
        using var httpClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var client = new OpenAiCompatibleChatClient(
            httpClient,
            new Uri("https://provider.example/v1/"),
            "secret",
            clock);
        var operation = DrainAsync(
            client.StreamAsync(Request(), cancellationToken),
            cancellationToken);

        await handler.Started.Task.WaitAsync(cancellationToken);
        clock.Advance(TimeSpan.FromSeconds(120));
        var exception = await Assert.ThrowsAsync<ChatCompletionException>(
            () => operation.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken));

        Assert.Equal(AgentErrorCodes.ProviderTimeout, exception.Code);
        Assert.True(exception.IsTransient);
    }

    [Fact]
    public async Task Sse_byte_idle_times_out_with_a_typed_error()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new ManualTimerTimeProvider(
            new DateTimeOffset(2026, 7, 27, 13, 0, 0, TimeSpan.Zero));
        var stream = new BlockingReadStream();
        var handler = new RecordingHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream),
            };
            response.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue(
                    "text/event-stream");
            return response;
        });
        using var httpClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var client = new OpenAiCompatibleChatClient(
            httpClient,
            new Uri("https://provider.example/v1/"),
            "secret",
            clock);
        var operation = DrainAsync(
            client.StreamAsync(Request(), cancellationToken),
            cancellationToken);

        await stream.ReadStarted.Task.WaitAsync(cancellationToken);
        clock.Advance(TimeSpan.FromSeconds(120));
        var exception = await Assert.ThrowsAsync<ChatCompletionException>(
            () => operation.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken));

        Assert.Equal(AgentErrorCodes.ProviderTimeout, exception.Code);
        Assert.True(exception.IsTransient);
    }

    private static ChatCompletionRequest Request() =>
        new(
            "qwen3.8-max-preview",
            [
                new ChatCompletionMessage(
                    ChatCompletionMessageRole.System,
                    "system"),
                new ChatCompletionMessage(
                    ChatCompletionMessageRole.User,
                    "hello"),
            ],
            maxOutputTokens: 32,
            Guid.Parse("019f2fac-2732-7c7e-86ec-46375a08d598"),
            attemptNumber: 1,
            ChatCompletionInvocationPurpose.Response);

    private static HttpResponseMessage Response(
        HttpStatusCode statusCode,
        string body,
        int maximumReadSize = int.MaxValue)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StreamContent(new FragmentedReadStream(
                Encoding.UTF8.GetBytes(body),
                maximumReadSize)),
        };
        response.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue(
                statusCode == HttpStatusCode.OK
                    ? "text/event-stream"
                    : "application/json");
        return response;
    }

    private static async Task<List<ChatCompletionEvent>> DrainAsync(
        IAsyncEnumerable<ChatCompletionEvent> source,
        CancellationToken cancellationToken)
    {
        var result = new List<ChatCompletionEvent>();
        await foreach (var item in source.WithCancellation(cancellationToken))
        {
            result.Add(item);
        }

        return result;
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> response)
        : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        public string? AuthorizationScheme { get; private set; }

        public string? AuthorizationParameter { get; private set; }

        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return response(request);
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class BlockingReadStream : Stream
    {
        public TaskCompletionSource ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class FragmentedReadStream(
        byte[] bytes,
        int maximumReadSize) : MemoryStream(bytes, writable: false)
    {
        public override int Read(byte[] buffer, int offset, int count) =>
            base.Read(buffer, offset, Math.Min(count, maximumReadSize));

        public override int Read(Span<byte> buffer) =>
            base.Read(buffer[..Math.Min(buffer.Length, maximumReadSize)]);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            base.ReadAsync(
                buffer[..Math.Min(buffer.Length, maximumReadSize)],
                cancellationToken);
    }

    private sealed class LoopbackSseServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _serverTask;

        public LoopbackSseServer(string responseBody)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var endpoint = (IPEndPoint)_listener.LocalEndpoint;
            BaseUri = new Uri($"http://127.0.0.1:{endpoint.Port}/v1/");
            var requestHeaders =
                new TaskCompletionSource<string>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            RequestHeaders = requestHeaders.Task;
            _serverTask = ServeAsync(responseBody, requestHeaders);
        }

        public Uri BaseUri { get; }

        public Task<string> RequestHeaders { get; }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            await _serverTask;
        }

        private async Task ServeAsync(
            string responseBody,
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
                var body = Encoding.UTF8.GetBytes(responseBody);
                var head = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
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
}
