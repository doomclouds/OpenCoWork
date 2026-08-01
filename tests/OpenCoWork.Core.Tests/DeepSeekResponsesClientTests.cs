using System.Net;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Agents;
using OpenCoWork.Core.Logging;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class DeepSeekResponsesClientTests
{
    public static TheoryData<string> InvalidStreams => new()
    {
        Sse(
            Created(),
            """{"type":"response.unknown","sequence_number":2}"""),
        Sse(
            Created(),
            """{"type":"response.in_progress","sequence_number":1,"response":{"id":"resp-1","status":"in_progress"}}"""),
        Sse(Created(), "[DONE]"),
        Sse(Created()),
        CompleteFixture() + Sse(
            """{"type":"response.in_progress","sequence_number":41,"response":{"id":"resp-1","status":"in_progress"}}"""),
        CompleteFixture().Replace(
            "\"type\":\"response.output_text.done\",\"sequence_number\":10,\"output_index\":0,\"item_id\":\"msg-1\",\"content_index\":0,\"text\":\"hello\"",
            "\"type\":\"response.output_text.done\",\"sequence_number\":10,\"output_index\":0,\"item_id\":\"msg-1\",\"content_index\":0,\"text\":\"HELLO\"",
            StringComparison.Ordinal),
        CompleteFixture().Replace(
            "\"sequence_number\":8,\"output_index\":0,\"item_id\":\"msg-1\"",
            "\"sequence_number\":8,\"output_index\":0,\"item_id\":\"msg-X\"",
            StringComparison.Ordinal),
        Sse(
            Created(),
            """{"type":"response.output_item.added","sequence_number":2,"output_index":0,"item":{"id":"function-1","type":"function_call","status":"in_progress","call_id":"call-1","name":"lookup","arguments":""}}""",
            Completed(sequence: 3)),
        Sse(
            Created(),
            """{"type":"response.output_item.added","sequence_number":2,"output_index":0,"item":{"id":"search-1","type":"web_search_call","status":"in_progress"}}""",
            """{"type":"response.web_search_call.searching","sequence_number":3,"output_index":0,"item_id":"search-1"}"""),
        Sse(
            Created(),
            """{"type":"response.output_item.added","sequence_number":2,"output_index":0,"item":{"id":"function-1","type":"function_call","status":"in_progress","call_id":"call-1","name":"lookup","arguments":""}}""",
            """{"type":"response.function_call_arguments.done","sequence_number":3,"output_index":0,"item_id":"function-1","arguments":""}""",
            """{"type":"response.output_item.done","sequence_number":4,"output_index":0,"item":{"id":"function-1","type":"function_call","status":"completed","call_id":"call-1","name":"lookup","arguments":""}}""",
            """{"type":"response.output_item.added","sequence_number":5,"output_index":1,"item":{"id":"custom-1","type":"custom_tool_call","status":"in_progress","call_id":"call-1","name":"apply_patch","input":""}}"""),
    };

    [Fact]
    public async Task Official_semantic_stream_is_assembled_from_a_real_loopback_exchange()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var server = new LoopbackSseServer(CompleteFixture());
        using var httpClient = DeepSeekResponsesClient.CreateSharedHttpClient();
        var client = Client(httpClient, server.BaseUri);

        var events = await DrainAsync(
            client.StreamAsync(Request(), cancellationToken),
            cancellationToken);
        var headers = await server.RequestHeaders.WaitAsync(cancellationToken);

        Assert.Contains("POST /v1/responses HTTP/1.1", headers, StringComparison.Ordinal);
        Assert.Contains("Authorization: Bearer test-secret", headers, StringComparison.Ordinal);
        Assert.Equal(
            [
                typeof(DeepSeekTextDeltaEvent),
                typeof(DeepSeekTextDeltaEvent),
                typeof(DeepSeekTextCompletedEvent),
                typeof(DeepSeekTextDeltaEvent),
                typeof(DeepSeekTextCompletedEvent),
                typeof(DeepSeekFunctionCallCompletedEvent),
                typeof(DeepSeekCustomToolCallCompletedEvent),
                typeof(DeepSeekWebSearchEvent),
                typeof(DeepSeekWebSearchEvent),
                typeof(DeepSeekWebSearchEvent),
                typeof(DeepSeekTerminalEvent),
            ],
            events.Select(item => item.GetType()));
        Assert.Equal(
            ["hel", "lo"],
            events.OfType<DeepSeekTextDeltaEvent>()
                .Where(item => item.Kind == DeepSeekTextKind.Output)
                .Select(item => item.Delta));
        Assert.Equal(
            "hello",
            Assert.Single(
                events.OfType<DeepSeekTextCompletedEvent>(),
                item => item.Kind == DeepSeekTextKind.Output).Text);
        Assert.Equal(
            "think",
            Assert.Single(
                events.OfType<DeepSeekTextCompletedEvent>(),
                item => item.Kind == DeepSeekTextKind.Reasoning).Text);
        var function = Assert.Single(events.OfType<DeepSeekFunctionCallCompletedEvent>());
        Assert.Equal(("call-1", "lookup", "{\"q\":\"x\"}"),
            (function.CallId, function.Name, function.Arguments));
        var custom = Assert.Single(events.OfType<DeepSeekCustomToolCallCompletedEvent>());
        Assert.Equal(("call-2", "apply_patch", "*** Begin Patch\n*** End Patch"),
            (custom.CallId, custom.Name, custom.Input));
        var searches = events.OfType<DeepSeekWebSearchEvent>().ToArray();
        Assert.Equal(
            [
                DeepSeekWebSearchStatus.InProgress,
                DeepSeekWebSearchStatus.Searching,
                DeepSeekWebSearchStatus.Completed,
            ],
            searches.Select(item => item.Status));
        Assert.Null(searches[0].ReplayItem);
        Assert.Null(searches[1].ReplayItem);
        Assert.Equal(
            "web_search_call",
            searches[2].ReplayItem?.GetProperty("type").GetString());
        var terminal = Assert.IsType<DeepSeekTerminalEvent>(events[^1]);
        Assert.Equal(DeepSeekTerminalStatus.Completed, terminal.Status);
        Assert.Equal(new DeepSeekResponsesUsage(10, 2, 6, 3, 16), terminal.Usage);
    }

    [Fact]
    public async Task Shared_http_client_decompresses_a_gzip_Responses_stream()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var server = new LoopbackSseServer(CompleteFixture(), gzip: true);
        using var httpClient = DeepSeekResponsesClient.CreateSharedHttpClient();

        var events = await DrainAsync(
            Client(httpClient, server.BaseUri).StreamAsync(Request(), cancellationToken),
            cancellationToken);

        Assert.Equal(
            DeepSeekTerminalStatus.Completed,
            Assert.IsType<DeepSeekTerminalEvent>(events[^1]).Status);
    }

    [Fact]
    public async Task Request_uses_only_the_frozen_stateless_Responses_subset()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(CompleteFixture());
        using var httpClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var client = Client(httpClient, new Uri("https://provider.example/"));

        _ = await DrainAsync(
            client.StreamAsync(Request(), cancellationToken),
            cancellationToken);

        Assert.Equal(new Uri("https://provider.example/responses"), handler.RequestUri);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("test-secret", handler.AuthorizationParameter);
        using var body = JsonDocument.Parse(handler.RequestBody!);
        var root = body.RootElement;
        Assert.Equal("deepseek-v4-flash", root.GetProperty("model").GetString());
        Assert.Equal("system", root.GetProperty("instructions").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal(128, root.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal("max", root.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal("message", root.GetProperty("input")[0].GetProperty("type").GetString());
        Assert.Equal("function", root.GetProperty("tools")[0].GetProperty("type").GetString());
        Assert.Equal("web_search", root.GetProperty("tools")[1].GetProperty("type").GetString());
        Assert.Equal("custom", root.GetProperty("tools")[2].GetProperty("type").GetString());
        Assert.Equal("apply_patch", root.GetProperty("tools")[2].GetProperty("name").GetString());
        Assert.DoesNotContain(
            root.EnumerateObject().Select(property => property.Name),
            name => name is "previous_response_id" or "conversation" or "store" or
                "background" or "stream_options");
    }

    [Fact]
    public async Task Request_serializes_the_supported_stateless_history_items()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var history = JsonDocument.Parse(
            """
            [
              {"type":"message","role":"assistant","content":[{"type":"output_text","text":"answer"}]},
              {"type":"reasoning","id":"reason-1","content":"thought"},
              {"type":"function_call","call_id":"call-1","name":"lookup","arguments":"{}"},
              {"type":"function_call_output","call_id":"call-1","output":"ok"},
              {"type":"custom_tool_call","call_id":"call-2","name":"apply_patch","input":"patch"},
              {"type":"custom_tool_call_output","call_id":"call-2","output":"done"},
              {"type":"web_search_call","id":"search-1","status":"completed","action":{"type":"search","query":"OpenCoWork"}}
            ]
            """);
        var handler = new RecordingHandler(CompleteFixture());
        using var httpClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var request = Request() with
        {
            Input = history.RootElement.EnumerateArray().Select(item => item.Clone()).ToArray(),
            Tools = [],
        };

        _ = await DrainAsync(
            Client(httpClient, new Uri("https://provider.example/"))
                .StreamAsync(request, cancellationToken),
            cancellationToken);

        using var body = JsonDocument.Parse(handler.RequestBody!);
        var input = body.RootElement.GetProperty("input");
        Assert.Equal(
            [
                "message",
                "reasoning",
                "function_call",
                "function_call_output",
                "custom_tool_call",
                "custom_tool_call_output",
                "web_search_call",
            ],
            input.EnumerateArray().Select(item => item.GetProperty("type").GetString()));
        Assert.Equal("thought", input[1].GetProperty("content").GetString());
        Assert.Equal("completed", input[6].GetProperty("status").GetString());
    }

    [Theory]
    [MemberData(nameof(InvalidStreams))]
    public async Task Unknown_inconsistent_or_unclosed_streams_are_rejected(string body)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var httpClient = new HttpClient(new StaticHandler(Response(body)))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var exception = await Assert.ThrowsAsync<ProviderException>(() => DrainAsync(
            Client(httpClient, new Uri("https://provider.example/"))
                .StreamAsync(Request(), cancellationToken),
            cancellationToken));

        Assert.Equal(AgentErrorCodes.ProviderInvalidStream, exception.Code);
        Assert.False(exception.IsTransient);
    }

    [Theory]
    [InlineData(10, 11, 4, 0, 14)]
    [InlineData(10, 0, 4, 5, 14)]
    [InlineData(10, 0, 4, 0, 15)]
    public async Task Terminal_usage_must_balance_cached_reasoning_and_total_tokens(
        int input,
        int cached,
        int output,
        int reasoning,
        int total)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var body = Sse(Created(), Completed(2, input, cached, output, reasoning, total));
        using var httpClient = new HttpClient(new StaticHandler(Response(body)))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        var exception = await Assert.ThrowsAsync<ProviderException>(() => DrainAsync(
            Client(httpClient, new Uri("https://provider.example/"))
                .StreamAsync(Request(), cancellationToken),
            cancellationToken));

        Assert.Equal(AgentErrorCodes.ProviderInvalidStream, exception.Code);
    }

    [Fact]
    public async Task Failed_terminal_detail_is_bounded_and_redacted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var secret = "canary-secret-34f9";
        var body = Sse(
            Created(),
            JsonSerializer.Serialize(new
            {
                type = "response.failed",
                sequence_number = 2,
                response = new
                {
                    id = "resp-1",
                    status = "failed",
                    error = new
                    {
                        code = "server_error",
                        message = $"api_key={secret} {new string('x', 20_000)}",
                    },
                },
            }));
        using var httpClient = new HttpClient(new StaticHandler(Response(body)))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var client = new DeepSeekResponsesClient(
            httpClient,
            new Uri("https://provider.example/"),
            secret,
            new SecretRedactor([secret]),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(60));

        var events = await DrainAsync(
            client.StreamAsync(Request(), cancellationToken),
            cancellationToken);

        var terminal = Assert.IsType<DeepSeekTerminalEvent>(Assert.Single(events));
        Assert.Equal(DeepSeekTerminalStatus.Failed, terminal.Status);
        Assert.Equal(AgentErrorCodes.ProviderResponseFailed, terminal.ErrorCode);
        Assert.NotNull(terminal.ErrorDetail);
        Assert.DoesNotContain(secret, terminal.ErrorDetail, StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(terminal.ErrorDetail) <= 16 * 1024);
    }

    [Fact]
    public async Task Incomplete_terminal_preserves_usage_and_reason()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var body = Sse(
            Created(),
            """{"type":"response.incomplete","sequence_number":2,"response":{"id":"resp-1","status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"usage":{"input_tokens":10,"input_tokens_details":{"cached_tokens":2},"output_tokens":6,"output_tokens_details":{"reasoning_tokens":3},"total_tokens":16}}}""");
        using var httpClient = new HttpClient(new StaticHandler(Response(body)))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        var terminal = Assert.IsType<DeepSeekTerminalEvent>(Assert.Single(await DrainAsync(
            Client(httpClient, new Uri("https://provider.example/"))
                .StreamAsync(Request(), cancellationToken),
            cancellationToken)));

        Assert.Equal(DeepSeekTerminalStatus.Incomplete, terminal.Status);
        Assert.Equal("max_output_tokens", terminal.Reason);
        Assert.Equal(new DeepSeekResponsesUsage(10, 2, 6, 3, 16), terminal.Usage);
    }

    [Theory]
    [InlineData(400, "provider.invalidRequest", false)]
    [InlineData(401, "provider.authenticationFailed", false)]
    [InlineData(402, "provider.quotaExceeded", false)]
    [InlineData(422, "provider.invalidRequest", false)]
    [InlineData(429, "provider.rateLimited", true)]
    [InlineData(500, "provider.serverUnavailable", true)]
    [InlineData(503, "provider.serverUnavailable", true)]
    [InlineData(502, "provider.serverUnavailable", false)]
    [InlineData(504, "provider.serverUnavailable", false)]
    [InlineData(302, "provider.redirectNotAllowed", false)]
    public async Task Only_official_transient_http_statuses_are_retryable(
        int statusCode,
        string expectedCode,
        bool expectedTransient)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var httpClient = new HttpClient(new StaticHandler(
            Response("{\"error\":{}}", (HttpStatusCode)statusCode, "application/json")))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        var exception = await Assert.ThrowsAsync<ProviderException>(() => DrainAsync(
            Client(httpClient, new Uri("https://provider.example/"))
                .StreamAsync(Request(), cancellationToken),
            cancellationToken));

        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal(expectedTransient, exception.IsTransient);
        Assert.Equal((HttpStatusCode)statusCode, exception.StatusCode);
    }

    [Fact]
    public async Task Invalid_utf8_and_oversized_semantic_output_are_rejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var prefix = Encoding.UTF8.GetBytes("data: ");
        var invalidBytes = new byte[prefix.Length + 3];
        prefix.CopyTo(invalidBytes, 0);
        invalidBytes[^3] = 0xFF;
        invalidBytes[^2] = (byte)'\n';
        invalidBytes[^1] = (byte)'\n';
        var invalidResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(invalidBytes),
        };
        invalidResponse.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
        using (var invalidClient = new HttpClient(new StaticHandler(invalidResponse))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        })
        {
            var exception = await Assert.ThrowsAsync<ProviderException>(() => DrainAsync(
                Client(invalidClient, new Uri("https://provider.example/"))
                    .StreamAsync(Request(), cancellationToken),
                cancellationToken));
            Assert.Equal(AgentErrorCodes.ProviderInvalidStream, exception.Code);
        }

        var chunk = new string('x', 900_000);
        var body = Sse(
            Created(),
            """{"type":"response.output_item.added","sequence_number":2,"output_index":0,"item":{"id":"msg-1","type":"message","status":"in_progress","role":"assistant","content":[]}}""",
            $$"""{"type":"response.output_text.delta","sequence_number":3,"output_index":0,"item_id":"msg-1","content_index":0,"delta":"{{chunk}}"}""",
            $$"""{"type":"response.output_text.delta","sequence_number":4,"output_index":0,"item_id":"msg-1","content_index":0,"delta":"{{chunk}}"}""",
            $$"""{"type":"response.output_text.delta","sequence_number":5,"output_index":0,"item_id":"msg-1","content_index":0,"delta":"{{chunk}}"}""",
            $$"""{"type":"response.output_text.delta","sequence_number":6,"output_index":0,"item_id":"msg-1","content_index":0,"delta":"{{chunk}}"}""",
            $$"""{"type":"response.output_text.delta","sequence_number":7,"output_index":0,"item_id":"msg-1","content_index":0,"delta":"{{chunk}}"}""");
        using var oversizedClient = new HttpClient(new StaticHandler(Response(body)))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        var oversized = await Assert.ThrowsAsync<ProviderException>(() => DrainAsync(
            Client(oversizedClient, new Uri("https://provider.example/"))
                .StreamAsync(Request(), cancellationToken),
            cancellationToken));
        Assert.Equal(AgentErrorCodes.ProviderOutputTooLarge, oversized.Code);
    }

    [Fact]
    public async Task Sse_body_search_replay_and_error_body_limits_are_enforced()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using (var stream = new RepeatingByteStream(
                         (16 * 1024 * 1024) + 1,
                         (byte)'\n'))
        {
            var reader = new BoundedSseReader(
                stream,
                TimeProvider.System,
                TimeSpan.FromSeconds(30));
            var bodyLimit = await Assert.ThrowsAsync<ProviderException>(
                () => reader.ReadEventAsync(cancellationToken).AsTask());
            Assert.Equal(AgentErrorCodes.ProviderOutputTooLarge, bodyLimit.Code);
        }

        var replay = Sse(
            Created(),
            """{"type":"response.output_item.added","sequence_number":2,"output_index":0,"item":{"id":"search-1","type":"web_search_call","status":"in_progress"}}""",
            """{"type":"response.web_search_call.in_progress","sequence_number":3,"output_index":0,"item_id":"search-1"}""",
            """{"type":"response.web_search_call.searching","sequence_number":4,"output_index":0,"item_id":"search-1"}""",
            """{"type":"response.web_search_call.completed","sequence_number":5,"output_index":0,"item_id":"search-1"}""",
            JsonSerializer.Serialize(new
            {
                type = "response.output_item.done",
                sequence_number = 6,
                output_index = 0,
                item = new
                {
                    id = "search-1",
                    type = "web_search_call",
                    status = "completed",
                    action = new { type = "search", query = new string('x', 300_000) },
                },
            }));
        using (var replayClient = new HttpClient(new StaticHandler(Response(replay)))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        })
        {
            var replayLimit = await Assert.ThrowsAsync<ProviderException>(() => DrainAsync(
                Client(replayClient, new Uri("https://provider.example/"))
                    .StreamAsync(Request(), cancellationToken),
                cancellationToken));
            Assert.Equal(AgentErrorCodes.ProviderOutputTooLarge, replayLimit.Code);
        }

        using var errorClient = new HttpClient(new StaticHandler(Response(
            new string('x', (64 * 1024) + 1),
            HttpStatusCode.BadRequest,
            "application/json")))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var errorLimit = await Assert.ThrowsAsync<ProviderException>(() => DrainAsync(
            Client(errorClient, new Uri("https://provider.example/"))
                .StreamAsync(Request(), cancellationToken),
            cancellationToken));
        Assert.Equal(AgentErrorCodes.ProviderOutputTooLarge, errorLimit.Code);
        Assert.False(errorLimit.IsTransient);
    }

    [Fact]
    public async Task Invalid_content_type_tls_and_transport_failures_are_typed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using (var contentClient = new HttpClient(new StaticHandler(Response(
                   "{}",
                   mediaType: "application/json")))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        })
        {
            var contentType = await Assert.ThrowsAsync<ProviderException>(() => DrainAsync(
                Client(contentClient, new Uri("https://provider.example/"))
                    .StreamAsync(Request(), cancellationToken),
                cancellationToken));
            Assert.Equal(AgentErrorCodes.ProviderInvalidStream, contentType.Code);
            Assert.False(contentType.IsTransient);
        }

        using (var tlsClient = new HttpClient(new ThrowingHandler(
                   new HttpRequestException("TLS failed.", new AuthenticationException())))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        })
        {
            var tls = await Assert.ThrowsAsync<ProviderException>(() => DrainAsync(
                Client(tlsClient, new Uri("https://provider.example/"))
                    .StreamAsync(Request(), cancellationToken),
                cancellationToken));
            Assert.Equal(AgentErrorCodes.ProviderTlsFailure, tls.Code);
            Assert.False(tls.IsTransient);
        }

        using var transportClient = new HttpClient(new ThrowingHandler(
            new HttpRequestException("Connection failed.")))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var transport = await Assert.ThrowsAsync<ProviderException>(() => DrainAsync(
            Client(transportClient, new Uri("https://provider.example/"))
                .StreamAsync(Request(), cancellationToken),
            cancellationToken));
        Assert.Equal(AgentErrorCodes.ProviderServerUnavailable, transport.Code);
        Assert.True(transport.IsTransient);
    }

    [Fact]
    public async Task Response_header_and_idle_timeouts_are_transient()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using (var headerClient = new HttpClient(new BlockingHandler())
        {
            Timeout = Timeout.InfiniteTimeSpan,
        })
        {
            var exception = await Assert.ThrowsAsync<ProviderException>(() => DrainAsync(
                Client(
                    headerClient,
                    new Uri("https://provider.example/"),
                    TimeSpan.FromMilliseconds(20),
                    TimeSpan.FromSeconds(1)).StreamAsync(Request(), cancellationToken),
                cancellationToken));
            Assert.Equal(AgentErrorCodes.ProviderTimeout, exception.Code);
            Assert.True(exception.IsTransient);
        }

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new BlockingReadStream()),
        };
        response.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
        using var idleClient = new HttpClient(new StaticHandler(response))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var idle = await Assert.ThrowsAsync<ProviderException>(() => DrainAsync(
            Client(
                idleClient,
                new Uri("https://provider.example/"),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(20)).StreamAsync(Request(), cancellationToken),
            cancellationToken));
        Assert.Equal(AgentErrorCodes.ProviderTimeout, idle.Code);
        Assert.True(idle.IsTransient);
    }

    private static DeepSeekResponsesClient Client(HttpClient httpClient, Uri baseUri) =>
        Client(
            httpClient,
            baseUri,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(60));

    private static DeepSeekResponsesClient Client(
        HttpClient httpClient,
        Uri baseUri,
        TimeSpan responseHeaderTimeout,
        TimeSpan streamIdleTimeout) =>
        new(
            httpClient,
            baseUri,
            "test-secret",
            new SecretRedactor(["test-secret"]),
            responseHeaderTimeout,
            streamIdleTimeout);

    private static DeepSeekResponsesRequest Request()
    {
        using var message = JsonDocument.Parse(
            """{"type":"message","role":"user","content":"hello"}""");
        using var parameters = JsonDocument.Parse(
            """{"type":"object","properties":{},"additionalProperties":false}""");
        return new DeepSeekResponsesRequest(
            "deepseek-v4-flash",
            "system",
            [message.RootElement.Clone()],
            MaxOutputTokens: 128,
            ReasoningEffort: "max",
            [
                new DeepSeekFunctionTool(
                    "lookup",
                    "Look up a value.",
                    parameters.RootElement),
                new DeepSeekWebSearchTool(),
                new DeepSeekApplyPatchTool(),
            ]);
    }

    private static async Task<List<DeepSeekResponseEvent>> DrainAsync(
        IAsyncEnumerable<DeepSeekResponseEvent> source,
        CancellationToken cancellationToken)
    {
        var events = new List<DeepSeekResponseEvent>();
        await foreach (var item in source.WithCancellation(cancellationToken))
        {
            events.Add(item);
        }

        return events;
    }

    private static string CompleteFixture() => Sse(
        """{"type":"response.created","sequence_number":1,"response":{"id":"resp-1","status":"in_progress"}}""",
        """{"type":"response.in_progress","sequence_number":3,"response":{"id":"resp-1","status":"in_progress"}}""",
        """{"type":"response.output_item.added","sequence_number":5,"output_index":0,"item":{"id":"msg-1","type":"message","status":"in_progress","role":"assistant","content":[]}}""",
        """{"type":"response.content_part.added","sequence_number":6,"output_index":0,"item_id":"msg-1","content_index":0,"part":{"type":"output_text","text":""}}""",
        """{"type":"response.output_text.delta","sequence_number":8,"output_index":0,"item_id":"msg-1","content_index":0,"delta":"hel"}""",
        """{"type":"response.output_text.delta","sequence_number":9,"output_index":0,"item_id":"msg-1","content_index":0,"delta":"lo"}""",
        """{"type":"response.output_text.done","sequence_number":10,"output_index":0,"item_id":"msg-1","content_index":0,"text":"hello"}""",
        """{"type":"response.content_part.done","sequence_number":11,"output_index":0,"item_id":"msg-1","content_index":0,"part":{"type":"output_text","text":"hello"}}""",
        """{"type":"response.output_item.done","sequence_number":13,"output_index":0,"item":{"id":"msg-1","type":"message","status":"completed","role":"assistant","content":[{"type":"output_text","text":"hello"}]}}""",
        """{"type":"response.output_item.added","sequence_number":15,"output_index":1,"item":{"id":"reason-1","type":"reasoning","status":"in_progress","content":[]}}""",
        """{"type":"response.reasoning_text.delta","sequence_number":16,"output_index":1,"item_id":"reason-1","content_index":0,"delta":"think"}""",
        """{"type":"response.reasoning_text.done","sequence_number":17,"output_index":1,"item_id":"reason-1","content_index":0,"text":"think"}""",
        """{"type":"response.output_item.done","sequence_number":18,"output_index":1,"item":{"id":"reason-1","type":"reasoning","status":"completed","content":[{"type":"reasoning_text","text":"think"}]}}""",
        """{"type":"response.output_item.added","sequence_number":20,"output_index":2,"item":{"id":"function-1","type":"function_call","status":"in_progress","call_id":"call-1","name":"lookup","arguments":""}}""",
        """{"type":"response.function_call_arguments.delta","sequence_number":21,"output_index":2,"item_id":"function-1","delta":"{\"q\":"}""",
        """{"type":"response.function_call_arguments.delta","sequence_number":22,"output_index":2,"item_id":"function-1","delta":"\"x\"}"}""",
        """{"type":"response.function_call_arguments.done","sequence_number":23,"output_index":2,"item_id":"function-1","arguments":"{\"q\":\"x\"}"}""",
        """{"type":"response.output_item.done","sequence_number":24,"output_index":2,"item":{"id":"function-1","type":"function_call","status":"completed","call_id":"call-1","name":"lookup","arguments":"{\"q\":\"x\"}"}}""",
        """{"type":"response.output_item.added","sequence_number":26,"output_index":3,"item":{"id":"custom-1","type":"custom_tool_call","status":"in_progress","call_id":"call-2","name":"apply_patch","input":""}}""",
        """{"type":"response.custom_tool_call_input.delta","sequence_number":27,"output_index":3,"item_id":"custom-1","delta":"*** Begin Patch\n"}""",
        """{"type":"response.custom_tool_call_input.delta","sequence_number":28,"output_index":3,"item_id":"custom-1","delta":"*** End Patch"}""",
        """{"type":"response.custom_tool_call_input.done","sequence_number":29,"output_index":3,"item_id":"custom-1","input":"*** Begin Patch\n*** End Patch"}""",
        """{"type":"response.output_item.done","sequence_number":30,"output_index":3,"item":{"id":"custom-1","type":"custom_tool_call","status":"completed","call_id":"call-2","name":"apply_patch","input":"*** Begin Patch\n*** End Patch"}}""",
        """{"type":"response.output_item.added","sequence_number":32,"output_index":4,"item":{"id":"search-1","type":"web_search_call","status":"in_progress"}}""",
        """{"type":"response.web_search_call.in_progress","sequence_number":33,"output_index":4,"item_id":"search-1"}""",
        """{"type":"response.web_search_call.searching","sequence_number":34,"output_index":4,"item_id":"search-1"}""",
        """{"type":"response.web_search_call.completed","sequence_number":35,"output_index":4,"item_id":"search-1"}""",
        """{"type":"response.output_item.done","sequence_number":36,"output_index":4,"item":{"id":"search-1","type":"web_search_call","status":"completed","action":{"type":"search","query":"OpenCoWork"}}}""",
        """{"type":"response.completed","sequence_number":40,"response":{"id":"resp-1","status":"completed","usage":{"input_tokens":10,"input_tokens_details":{"cached_tokens":2},"output_tokens":6,"output_tokens_details":{"reasoning_tokens":3},"total_tokens":16}}}""");

    private static string Sse(params string[] events) =>
        string.Join("\n\n", events.Select(item => "data: " + item)) + "\n\n";

    private static string Created() =>
        """{"type":"response.created","sequence_number":1,"response":{"id":"resp-1","status":"in_progress"}}""";

    private static string Completed(
        int sequence,
        int input = 10,
        int cached = 2,
        int output = 6,
        int reasoning = 3,
        int total = 16) =>
        JsonSerializer.Serialize(new
        {
            type = "response.completed",
            sequence_number = sequence,
            response = new
            {
                id = "resp-1",
                status = "completed",
                usage = new
                {
                    input_tokens = input,
                    input_tokens_details = new { cached_tokens = cached },
                    output_tokens = output,
                    output_tokens_details = new { reasoning_tokens = reasoning },
                    total_tokens = total,
                },
            },
        });

    private static HttpResponseMessage Response(
        string body,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string mediaType = "text/event-stream") =>
        new(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType),
        };

    private sealed class StaticHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(response);
    }

    private sealed class ThrowingHandler(HttpRequestException exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromException<HttpResponseMessage>(exception);
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class BlockingReadStream : Stream
    {
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
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class RepeatingByteStream : Stream
    {
        private readonly int _length;
        private readonly byte _value;
        private int _remaining;

        public RepeatingByteStream(int length, byte value)
        {
            _length = length;
            _remaining = length;
            _value = value;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _length;

        public override long Position
        {
            get => _length - _remaining;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(buffer.Length, _remaining);
            buffer.Span[..count].Fill(_value);
            _remaining -= count;
            return ValueTask.FromResult(count);
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingHandler(string responseBody) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        public string? AuthorizationScheme { get; private set; }

        public string? AuthorizationParameter { get; private set; }

        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    responseBody,
                    Encoding.UTF8,
                    "text/event-stream"),
            };
        }
    }
}
