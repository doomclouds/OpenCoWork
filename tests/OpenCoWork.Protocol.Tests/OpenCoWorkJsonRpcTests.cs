using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using OpenCoWork.Abstractions;
using OpenCoWork.Protocol;
using Xunit;

namespace OpenCoWork.Protocol.Tests;

public sealed class OpenCoWorkJsonRpcTests
{
    [Fact]
    public async Task JsonRpc_initializes_then_returns_standard_method_error()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var output = new List<JsonElement>();
        await using var connection = new OpenCoWorkJsonRpcConnection(
            DispatchProxy.Create<ISessionService, ThrowingSessionProxy>(),
            "/workspace",
            "stdio",
            (message, _) =>
            {
                using var document = JsonDocument.Parse(message);
                output.Add(document.RootElement.Clone());
                return ValueTask.CompletedTask;
            });

        await connection.ProcessAsync(
            """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"client":{"name":"test","version":"1"},"wireVersions":["1.0"],"workspace":{"path":"/workspace"}}}
            """u8.ToArray(),
            cancellationToken);
        await connection.ProcessAsync(
            """{"jsonrpc":"2.0","id":"missing","method":"missing","params":{}}"""u8
                .ToArray(),
            cancellationToken);

        Assert.Equal("1.0", output[0].GetProperty("result")
            .GetProperty("wireVersion").GetString());
        Assert.Equal(
            -32601,
            output[1].GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal("missing", output[1].GetProperty("id").GetString());
    }

    [Fact]
    public async Task JsonRpc_rejects_malformed_and_preinitialize_requests()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var output = new List<JsonElement>();
        await using var connection = new OpenCoWorkJsonRpcConnection(
            DispatchProxy.Create<ISessionService, ThrowingSessionProxy>(),
            "/workspace",
            "stdio",
            (message, _) =>
            {
                using var document = JsonDocument.Parse(message);
                output.Add(document.RootElement.Clone());
                return ValueTask.CompletedTask;
            });

        await connection.ProcessAsync("{"u8.ToArray(), cancellationToken);
        await connection.ProcessAsync(
            """{"jsonrpc":"2.0","id":2,"method":"thread/list","params":{}}"""u8
                .ToArray(),
            cancellationToken);

        Assert.Equal(
            -32700,
            output[0].GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(
            -32003,
            output[1].GetProperty("error").GetProperty("code").GetInt32());
        Assert.DoesNotContain(
            "stack",
            output[1].GetRawText(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task JsonRpc_cancel_request_only_cancels_the_matching_rpc_wait()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (sessions, proxy) = CreateProxy();
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.Handler = (method, args) =>
            method.Name == nameof(ISessionService.ListThreadsAsync)
                ? WaitForCancellationAsync((CancellationToken)args![1]!)
                : throw new NotSupportedException(method.Name);
        var output = new ConcurrentQueue<JsonElement>();
        await using var connection = new OpenCoWorkJsonRpcConnection(
            sessions,
            "/workspace",
            "stdio",
            (message, _) =>
            {
                using var document = JsonDocument.Parse(message);
                output.Enqueue(document.RootElement.Clone());
                return ValueTask.CompletedTask;
            });
        await InitializeAsync(connection, cancellationToken);
        var pending = connection.ProcessAsync(
            """{"jsonrpc":"2.0","id":"pending","method":"thread/list","params":{}}"""u8
                .ToArray(),
            cancellationToken);
        await started.Task.WaitAsync(cancellationToken);

        await connection.ProcessAsync(
            """{"jsonrpc":"2.0","method":"$/cancelRequest","params":{"id":"pending"}}"""u8
                .ToArray(),
            cancellationToken);
        await pending;

        var cancelled = output.Last();
        Assert.Equal(
            -32005,
            cancelled.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal("pending", cancelled.GetProperty("id").GetString());

        async Task<SessionQueryResult<SessionPage<ThreadSnapshot>>>
            WaitForCancellationAsync(CancellationToken token)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException();
        }
    }

    [Fact]
    public void OpenCoWorkWire_declares_the_frozen_25_method_catalog()
    {
        var declarations = typeof(OpenCoWorkJsonRpcConnection)
            .GetMethods(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic)
            .Where(method => method
                .GetCustomAttribute<OpenCoWorkWireMethodAttribute>()?.Since ==
                OpenCoWorkWire.Version)
            .ToArray();
        var methods = declarations
            .Select(method =>
                method.GetCustomAttribute<OpenCoWorkWireMethodAttribute>()!.Method)
            .ToArray();

        Assert.Equal(25, methods.Length);
        Assert.Equal(25, methods.Distinct(StringComparer.Ordinal).Count());
        Assert.All(declarations, method => Assert.True(method.IsPublic));
        Assert.Contains("thread/history/read", methods);
        Assert.Contains("thread/model/set", methods);
        Assert.Contains("thread/mode/set", methods);
        Assert.Contains("thread/delete/prepare", methods);
    }

    [Fact]
    public void OpenCoWorkWire_11_declares_the_frozen_39_method_catalog()
    {
        var declarations = typeof(OpenCoWorkJsonRpcConnection).Assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods(
                BindingFlags.Instance |
                BindingFlags.Static |
                BindingFlags.Public |
                BindingFlags.NonPublic))
            .Select(method => method
                .GetCustomAttribute<OpenCoWorkWireMethodAttribute>())
            .Where(attribute => attribute?.Since == OpenCoWorkWire.CapabilityVersion)
            .Cast<OpenCoWorkWireMethodAttribute>()
            .ToArray();
        var methods = declarations.Select(attribute => attribute.Method).ToArray();

        Assert.Equal(39, methods.Length);
        Assert.Equal(39, methods.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("capability/catalog", methods);
        Assert.Contains("tool/dynamic/register", methods);
        Assert.Contains("tool/invoke", methods);
        Assert.Contains("memory/archive", methods);
    }

    [Fact]
    public async Task OpenCoWorkWire_history_keeps_method_and_uses_opaque_cursor()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (sessions, proxy) = CreateProxy();
        var threadId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var thread = new ThreadSnapshot(
            threadId,
            "History",
            ThreadStatus.Active,
            ThreadAvailability.Available,
            HistoryMode.Server,
            currentSequence: 1,
            activeTurnId: null,
            queue: [],
            now,
            now,
            SessionProjectionState.Ready,
            diagnostic: null);
        var afterSequences = new List<long>();
        proxy.Handler = (method, args) =>
        {
            if (method.Name != nameof(ISessionService.ReadHistoryAsync))
            {
                throw new NotSupportedException(method.Name);
            }

            var request = (ReadHistoryRequest)args![0]!;
            afterSequences.Add(request.AfterSequence);
            SessionEvent[] events = request.AfterSequence == 0
                ?
                [
                    new SessionEvent(
                        threadId,
                        Sequence: 1,
                        Guid.CreateVersion7(),
                        now,
                        SessionEventType.ThreadCreated,
                        new SessionEventPayload(Thread: thread)),
                ]
                : [];
            return Task.FromResult(
                new SessionQueryResult<SessionPage<SessionEvent>>(
                    new SessionPage<SessionEvent>(
                        events,
                        events.Length == 0 ? null : "1"),
                    Error: null));
        };
        await using var connection = new OpenCoWorkJsonRpcConnection(
            sessions,
            "/workspace",
            "stdio",
            static (_, _) => ValueTask.CompletedTask);

        var first = await connection.ReadHistoryAsync(
            new WireReadHistoryRequest(threadId),
            cancellationToken);
        var second = await connection.ReadHistoryAsync(
            new WireReadHistoryRequest(threadId, first.NextCursor),
            cancellationToken);

        Assert.Equal("thread/created", Assert.Single(first.Events).Method);
        Assert.NotEqual("1", first.NextCursor);
        Assert.Empty(second.Events);
        Assert.Equal([0L, 0L, 1L], afterSequences);
    }

    [Fact]
    public async Task OpenCoWorkWire_maps_create_and_start_to_session_core()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (sessions, proxy) = CreateProxy();
        var threadId = Guid.CreateVersion7();
        var turnId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var thread = new ThreadSnapshot(
            threadId,
            "Desktop",
            ThreadStatus.Active,
            ThreadAvailability.Available,
            HistoryMode.Server,
            currentSequence: 1,
            activeTurnId: null,
            queue: [],
            now,
            now,
            SessionProjectionState.Ready,
            diagnostic: null,
            "provider",
            "model",
            AgentMode.Plan);
        EnqueueInputRequest? submitted = null;
        proxy.Handler = (method, args) =>
        {
            if (method.Name == nameof(ISessionService.CreateThreadAsync))
            {
                return Task.FromResult(new SessionCommandResult<ThreadSnapshot>(
                    SessionCommandStatus.Committed,
                    thread,
                    Sequence: 1,
                    CurrentSequence: null,
                    Error: null));
            }

            if (method.Name == nameof(ISessionService.EnqueueInputAsync))
            {
                submitted = (EnqueueInputRequest)args![0]!;
                return Task.FromResult(
                    new SessionCommandResult<SubmittedTurnInputSnapshot>(
                        SessionCommandStatus.Committed,
                        new SubmittedTurnInputSnapshot(
                            new QueuedTurnInputSnapshot(
                                Guid.CreateVersion7(),
                                threadId,
                                submitted.Text,
                                Position: 0,
                                now),
                            turnId),
                        Sequence: 2,
                        CurrentSequence: null,
                        Error: null));
            }

            throw new NotSupportedException(method.Name);
        };
        var output = new List<JsonElement>();
        await using var connection = new OpenCoWorkJsonRpcConnection(
            sessions,
            "/workspace",
            "stdio",
            (message, _) =>
            {
                using var document = JsonDocument.Parse(message);
                output.Add(document.RootElement.Clone());
                return ValueTask.CompletedTask;
            });

        await InitializeAsync(connection, cancellationToken);
        await connection.ProcessAsync(
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "thread/create",
                @params = new
                {
                    idempotencyKey = Guid.CreateVersion7(),
                    expectedSequence = 0,
                    displayName = "Desktop",
                    providerId = "provider",
                    modelId = "model",
                    mode = "plan",
                },
            }),
            cancellationToken);
        await connection.ProcessAsync(
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                jsonrpc = "2.0",
                id = 3,
                method = "turn/start",
                @params = new
                {
                    threadId,
                    idempotencyKey = Guid.CreateVersion7(),
                    expectedSequence = 1,
                    text = "hello",
                },
            }),
            cancellationToken);

        Assert.Equal("plan", output[1].GetProperty("result")
            .GetProperty("thread").GetProperty("mode").GetString());
        Assert.Equal(
            turnId,
            output[2].GetProperty("result").GetProperty("turnId").GetGuid());
        Assert.Equal(TurnAdmission.StartOnly, submitted?.Admission);
    }

    [Fact]
    public async Task Subscription_preserves_sequences_and_emits_true_text_deltas()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (sessions, proxy) = CreateProxy();
        var threadId = Guid.CreateVersion7();
        var turnId = Guid.CreateVersion7();
        var itemId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var thread = new ThreadSnapshot(
            threadId,
            "Live",
            ThreadStatus.Active,
            ThreadAvailability.Available,
            HistoryMode.Server,
            currentSequence: 1,
            activeTurnId: turnId,
            queue: [],
            now,
            now,
            SessionProjectionState.Ready,
            diagnostic: null);
        var events = Channel.CreateUnbounded<SessionEvent>();
        proxy.Handler = (method, _) =>
            method.Name == nameof(ISessionService.SubscribeAsync)
                ? Task.FromResult(new SessionSubscription(
                    SessionSubscriptionDisposition.Ready,
                    thread,
                    currentSequence: 1,
                    events.Reader.ReadAllAsync(cancellationToken)))
                : throw new NotSupportedException(method.Name);
        var output = new ConcurrentQueue<JsonElement>();
        var received = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = new OpenCoWorkJsonRpcConnection(
            sessions,
            "/workspace",
            "stdio",
            (message, sendCancellationToken) =>
            {
                using var document = JsonDocument.Parse(message);
                output.Enqueue(document.RootElement.Clone());
                if (output.Count(element => element.TryGetProperty("method", out _)) >= 6)
                {
                    received.TrySetResult();
                }

                return ValueTask.CompletedTask;
            });

        await InitializeAsync(connection, cancellationToken);
        await connection.ProcessAsync(
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "thread/subscribe",
                @params = new
                {
                    threadId,
                    mode = "snapshotThenLive",
                },
            }),
            cancellationToken);
        await events.Writer.WriteAsync(
            Event(2, SessionEventType.ThreadRenamed, new SessionEventPayload(
                Thread: thread with { })),
            cancellationToken);
        await events.Writer.WriteAsync(
            Event(3, SessionEventType.ItemStarted, new SessionEventPayload(
                Item: Item(SessionItemStatus.Started, string.Empty))),
            cancellationToken);
        await events.Writer.WriteAsync(
            Event(4, SessionEventType.ItemDeltaAppended, new SessionEventPayload(
                Item: Item(SessionItemStatus.Streaming, "A"))),
            cancellationToken);
        await events.Writer.WriteAsync(
            Event(5, SessionEventType.ItemDeltaAppended, new SessionEventPayload(
                Item: Item(SessionItemStatus.Streaming, "AB"))),
            cancellationToken);
        await events.Writer.WriteAsync(
            Event(6, SessionEventType.ItemStarted, new SessionEventPayload(
                Item: new SessionItemSnapshot(
                    Guid.CreateVersion7(),
                    turnId,
                    SessionItemType.ProviderAction,
                    SessionItemStatus.Started,
                    new ProviderActionItemContent(
                        "search-1",
                        ProviderActionStatus.Searching),
                    Sequence: 6,
                    now,
                    now))),
            cancellationToken);
        await events.Writer.WriteAsync(
            Event(7, SessionEventType.ProviderUsageRecorded, new SessionEventPayload()),
            cancellationToken);
        await received.Task.WaitAsync(cancellationToken);
        events.Writer.Complete();

        var notifications = output
            .Where(element => element.TryGetProperty("method", out _))
            .OrderBy(element => element.GetProperty("params")
                .GetProperty("sequence").GetInt64())
            .ToArray();
        Assert.Equal(
            [2L, 3L, 4L, 5L, 6L, 7L],
            notifications.Select(element => element.GetProperty("params")
                .GetProperty("sequence").GetInt64()));
        Assert.Equal(
            ["A", "B"],
            notifications
                .Where(element =>
                    element.GetProperty("method").GetString() == "item/delta")
                .Select(element => element.GetProperty("params")
                    .GetProperty("payload").GetProperty("delta").GetString()));
        Assert.Equal(
            "system/event",
            notifications[^1].GetProperty("method").GetString());
        var providerAction = Assert.Single(
            notifications,
            element => element.GetProperty("params").GetProperty("sequence").GetInt64() == 6);
        Assert.Equal("item/started", providerAction.GetProperty("method").GetString());
        var actionItem = providerAction.GetProperty("params")
            .GetProperty("payload")
            .GetProperty("item");
        Assert.Equal("providerAction", actionItem.GetProperty("type").GetString());
        Assert.Equal(
            "search-1",
            actionItem.GetProperty("content").GetProperty("providerCallId").GetString());
        Assert.Equal(
            "searching",
            actionItem.GetProperty("content").GetProperty("status").GetString());

        SessionEvent Event(
            long sequence,
            SessionEventType type,
            SessionEventPayload payload) =>
            new(
                threadId,
                sequence,
                Guid.CreateVersion7(),
                now,
                type,
                payload);

        SessionItemSnapshot Item(SessionItemStatus status, string text) =>
            new(
                itemId,
                turnId,
                SessionItemType.AgentMessage,
                status,
                new TextItemContent(text),
                Sequence: 3,
                now,
                now);
    }

    private static async Task InitializeAsync(
        OpenCoWorkJsonRpcConnection connection,
        CancellationToken cancellationToken) =>
        await connection.ProcessAsync(
            """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"client":{"name":"test","version":"1"},"wireVersions":["1.0"],"workspace":{"path":"/workspace"}}}
            """u8.ToArray(),
            cancellationToken);

    private static (ISessionService Service, ThrowingSessionProxy Proxy) CreateProxy()
    {
        var service = DispatchProxy.Create<ISessionService, ThrowingSessionProxy>();
        return (service, (ThrowingSessionProxy)(object)service);
    }

    private class ThrowingSessionProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?>? Handler { get; set; }

        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? args) =>
            Handler?.Invoke(targetMethod!, args) ??
            throw new NotSupportedException(targetMethod?.Name);
    }
}
