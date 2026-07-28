using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using OpenCoWork.Abstractions;
using OpenCoWork.Protocol;
using Xunit;

namespace OpenCoWork.Protocol.Tests;

public sealed class AcpConnectionTests
{
    [Fact]
    public async Task Acp_initializes_creates_session_and_sets_mode()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (sessions, proxy) = CreateProxy();
        var thread = Thread(currentSequence: 1);
        proxy.Handler = (method, args) => method.Name switch
        {
            nameof(ISessionService.CreateThreadAsync) =>
                Task.FromResult(Command(thread, sequence: 1)),
            nameof(ISessionService.SubscribeAsync) =>
                Task.FromResult(Subscription(thread)),
            nameof(ISessionService.GetThreadAsync) =>
                Task.FromResult(new SessionQueryResult<ThreadSnapshot>(thread, null)),
            nameof(ISessionService.SetAgentModeAsync) =>
                Task.FromResult(Command(thread with { }, sequence: 2)),
            _ => throw new NotSupportedException(method.Name),
        };
        var output = new ConcurrentQueue<JsonElement>();
        await using var connection = Connection(sessions, output);

        await ProcessAsync(
            connection,
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1}}""",
            cancellationToken);
        await ProcessAsync(
            connection,
            WithPlatformWorkspace(
                """{"jsonrpc":"2.0","id":2,"method":"session/new","params":{"cwd":"/workspace","mcpServers":[]}}"""),
            cancellationToken);
        await ProcessAsync(
            connection,
            $$$"""{"jsonrpc":"2.0","id":3,"method":"session/set_mode","params":{"sessionId":"{{{thread.ThreadId:D}}}","modeId":"plan"}}""",
            cancellationToken);

        Assert.Equal(1, output.ElementAt(0).GetProperty("result")
            .GetProperty("protocolVersion").GetInt32());
        Assert.True(output.ElementAt(0).GetProperty("result")
            .GetProperty("agentCapabilities").GetProperty("loadSession").GetBoolean());
        Assert.Equal(
            thread.ThreadId.ToString("D"),
            output.ElementAt(1).GetProperty("result").GetProperty("sessionId").GetString());
        Assert.Equal(
            "agent",
            output.ElementAt(1).GetProperty("result").GetProperty("modes")
                .GetProperty("currentModeId").GetString());
        Assert.Equal(JsonValueKind.Object, output.ElementAt(2)
            .GetProperty("result").ValueKind);

        var modeRequest = Assert.IsType<SetAgentModeRequest>(proxy.LastRequest);
        Assert.Equal(AgentMode.Plan, modeRequest.AgentMode);
        Assert.Equal(thread.CurrentSequence, modeRequest.ExpectedSequence);
    }

    [Fact]
    public async Task Acp_prompt_streams_text_and_returns_core_stop_reason()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (sessions, proxy) = CreateProxy();
        var thread = Thread(currentSequence: 1);
        var turnId = Guid.CreateVersion7();
        var itemId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var events = Channel.CreateUnbounded<SessionEvent>();
        proxy.Handler = (method, args) => method.Name switch
        {
            nameof(ISessionService.CreateThreadAsync) =>
                Task.FromResult(Command(thread, sequence: 1)),
            nameof(ISessionService.SubscribeAsync) =>
                Task.FromResult(Subscription(thread, new TestEventFeed(events.Reader))),
            nameof(ISessionService.GetThreadAsync) =>
                Task.FromResult(new SessionQueryResult<ThreadSnapshot>(thread, null)),
            nameof(ISessionService.EnqueueInputAsync) =>
                Task.FromResult(new SessionCommandResult<SubmittedTurnInputSnapshot>(
                    SessionCommandStatus.Committed,
                    new SubmittedTurnInputSnapshot(
                        new QueuedTurnInputSnapshot(
                            Guid.CreateVersion7(),
                            thread.ThreadId,
                            "hello",
                            0,
                            now),
                        turnId),
                    Sequence: 2,
                    CurrentSequence: null,
                    Error: null)),
            _ => throw new NotSupportedException(method.Name),
        };
        var output = new ConcurrentQueue<JsonElement>();
        await using var connection = Connection(sessions, output);
        await InitializeAndCreateAsync(connection, cancellationToken);

        var prompt = ProcessAsync(
            connection,
            $$$"""{"jsonrpc":"2.0","id":3,"method":"session/prompt","params":{"sessionId":"{{{thread.ThreadId:D}}}","prompt":[{"type":"text","text":"hello"}]}}""",
            cancellationToken);
        await events.Writer.WriteAsync(
            Event(
                thread.ThreadId,
                sequence: 2,
                SessionEventType.TurnStarted,
                new SessionEventPayload(
                    Turn: Turn(thread.ThreadId, turnId, TurnStatus.Running),
                    Item: Item(itemId, turnId, SessionItemType.UserMessage, "hello", now))),
            cancellationToken);
        await events.Writer.WriteAsync(
            Event(
                thread.ThreadId,
                sequence: 3,
                SessionEventType.ItemDeltaAppended,
                new SessionEventPayload(
                    Turn: Turn(thread.ThreadId, turnId, TurnStatus.Running),
                    Item: Item(itemId, turnId, SessionItemType.AgentMessage, "answer", now))),
            cancellationToken);
        await events.Writer.WriteAsync(
            Event(
                thread.ThreadId,
                sequence: 4,
                SessionEventType.ItemCompleted,
                new SessionEventPayload(
                    Turn: Turn(thread.ThreadId, turnId, TurnStatus.Running),
                    Item: Item(
                        Guid.CreateVersion7(),
                        turnId,
                        SessionItemType.SystemNotice,
                        "response.truncated",
                        now,
                        new SystemNoticeContent("response.truncated")))),
            cancellationToken);
        await events.Writer.WriteAsync(
            Event(
                thread.ThreadId,
                sequence: 5,
                SessionEventType.TurnCompleted,
                new SessionEventPayload(
                    Turn: Turn(thread.ThreadId, turnId, TurnStatus.Completed))),
            cancellationToken);
        await prompt;

        var notifications = output.Where(item =>
            item.TryGetProperty("method", out var method) &&
            method.GetString() == "session/update").ToArray();
        Assert.Contains(
            notifications,
            item => item.GetProperty("params").GetProperty("update")
                .GetProperty("sessionUpdate").GetString() == "user_message_chunk");
        Assert.Contains(
            notifications,
            item => item.GetProperty("params").GetProperty("update")
                .GetProperty("sessionUpdate").GetString() == "agent_message_chunk");
        Assert.Equal(
            "max_tokens",
            output.Last(item => item.TryGetProperty("id", out var id) &&
                                id.ValueKind == JsonValueKind.Number &&
                                id.GetInt32() == 3)
                .GetProperty("result").GetProperty("stopReason").GetString());
    }

    [Fact]
    public async Task Acp_approval_uses_permission_request_and_resolves_core_interaction()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var cancellationToken = timeout.Token;
        var (sessions, proxy) = CreateProxy();
        var thread = Thread(currentSequence: 1);
        var turnId = Guid.CreateVersion7();
        var interactionId = Guid.CreateVersion7();
        var itemId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var events = Channel.CreateUnbounded<SessionEvent>();
        proxy.Handler = (method, args) => method.Name switch
        {
            nameof(ISessionService.CreateThreadAsync) =>
                Task.FromResult(Command(thread, sequence: 1)),
            nameof(ISessionService.SubscribeAsync) =>
                Task.FromResult(Subscription(thread, new TestEventFeed(events.Reader))),
            nameof(ISessionService.GetThreadAsync) =>
                Task.FromResult(new SessionQueryResult<ThreadSnapshot>(
                    thread with { },
                    null)),
            nameof(ISessionService.EnqueueInputAsync) =>
                Task.FromResult(new SessionCommandResult<SubmittedTurnInputSnapshot>(
                    SessionCommandStatus.Committed,
                    new SubmittedTurnInputSnapshot(
                        new QueuedTurnInputSnapshot(
                            Guid.CreateVersion7(),
                            thread.ThreadId,
                            "run",
                            0,
                            now),
                        turnId),
                    Sequence: 2,
                    CurrentSequence: null,
                    Error: null)),
            nameof(ISessionService.ResolveInteractionAsync) =>
                Task.FromResult(new SessionCommandResult<PendingInteractionSnapshot>(
                    SessionCommandStatus.Committed,
                    new PendingInteractionSnapshot(
                        interactionId,
                        thread.ThreadId,
                        turnId,
                        SessionInteractionType.Approval,
                        IsResolved: true,
                        now,
                        TimeoutAt: null),
                    Sequence: 4,
                    CurrentSequence: null,
                    Error: null)),
            _ => throw new NotSupportedException(method.Name),
        };
        var output = new ConcurrentQueue<JsonElement>();
        await using var connection = Connection(sessions, output);
        await InitializeAndCreateAsync(connection, cancellationToken);

        var prompt = ProcessAsync(
            connection,
            $$$"""{"jsonrpc":"2.0","id":3,"method":"session/prompt","params":{"sessionId":"{{{thread.ThreadId:D}}}","prompt":[{"type":"text","text":"run"}]}}""",
            cancellationToken);
        await events.Writer.WriteAsync(
            Event(
                thread.ThreadId,
                sequence: 2,
                SessionEventType.TurnWaitingApproval,
                new SessionEventPayload(
                    Turn: Turn(thread.ThreadId, turnId, TurnStatus.WaitingApproval),
                    Item: Item(
                        itemId,
                        turnId,
                        SessionItemType.ApprovalRequest,
                        "Proceed?",
                        now,
                        new ApprovalRequestContent("Proceed?")),
                    Interaction: new PendingInteractionSnapshot(
                        interactionId,
                        thread.ThreadId,
                        turnId,
                        SessionInteractionType.Approval,
                        IsResolved: false,
                        now,
                        TimeoutAt: null))),
            cancellationToken);

        var permissionTask = WaitForAsync(
            output,
            item => item.TryGetProperty("method", out var method) &&
                    method.GetString() == "session/request_permission",
            cancellationToken);
        if (await Task.WhenAny(permissionTask, prompt) == prompt)
        {
            await prompt;
        }

        var permission = await permissionTask;
        var requestId = permission.GetProperty("id").GetString();
        await ProcessAsync(
            connection,
            "{\"jsonrpc\":\"2.0\",\"id\":\"" + requestId +
            "\",\"result\":{\"outcome\":{\"outcome\":\"selected\"," +
            "\"optionId\":\"allow-once\"}}}",
            cancellationToken);
        await events.Writer.WriteAsync(
            Event(
                thread.ThreadId,
                sequence: 3,
                SessionEventType.TurnCompleted,
                new SessionEventPayload(
                    Turn: Turn(thread.ThreadId, turnId, TurnStatus.Completed))),
            cancellationToken);
        await prompt;

        var resolution = Assert.IsType<ResolveInteractionRequest>(proxy.LastRequest);
        Assert.True(Assert.IsType<ApprovalResponseContent>(resolution.Response).Approved);
        Assert.Equal(
            "end_turn",
            output.Last(item => item.TryGetProperty("id", out var id) &&
                                id.ValueKind == JsonValueKind.Number &&
                                id.GetInt32() == 3)
                .GetProperty("result").GetProperty("stopReason").GetString());
    }

    [Fact]
    public async Task Acp_load_deduplicates_history_by_journal_sequence()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (sessions, proxy) = CreateProxy();
        var thread = Thread(currentSequence: 2);
        var turnId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var events = Channel.CreateUnbounded<SessionEvent>();
        await events.Writer.WriteAsync(
            Event(
                thread.ThreadId,
                sequence: 1,
                SessionEventType.ThreadCreated,
                new SessionEventPayload(Thread: thread)),
            cancellationToken);
        var user = Event(
            thread.ThreadId,
            sequence: 2,
            SessionEventType.TurnStarted,
            new SessionEventPayload(
                Turn: Turn(thread.ThreadId, turnId, TurnStatus.Completed),
                Item: Item(
                    Guid.CreateVersion7(),
                    turnId,
                    SessionItemType.UserMessage,
                    "history",
                    now)));
        await events.Writer.WriteAsync(user, cancellationToken);
        await events.Writer.WriteAsync(user, cancellationToken);
        events.Writer.Complete();
        proxy.Handler = (method, args) => method.Name switch
        {
            nameof(ISessionService.SubscribeAsync) =>
                Task.FromResult(Subscription(thread, new TestEventFeed(events.Reader))),
            _ => throw new NotSupportedException(method.Name),
        };
        var output = new ConcurrentQueue<JsonElement>();
        await using var connection = Connection(sessions, output);
        await ProcessAsync(
            connection,
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1}}""",
            cancellationToken);

        await ProcessAsync(
            connection,
            WithPlatformWorkspace(
                $$$"""{"jsonrpc":"2.0","id":2,"method":"session/load","params":{"sessionId":"{{{thread.ThreadId:D}}}","cwd":"/workspace","mcpServers":[]}}"""),
            cancellationToken);

        Assert.Single(
            output,
            item => item.TryGetProperty("method", out var method) &&
                    method.GetString() == "session/update" &&
                    item.GetProperty("params").GetProperty("update")
                        .GetProperty("sessionUpdate").GetString() ==
                    "user_message_chunk");
        Assert.Contains(
            output,
            item => item.TryGetProperty("id", out var id) &&
                    id.ValueKind == JsonValueKind.Number &&
                    id.GetInt32() == 2 &&
                    item.TryGetProperty("result", out _));
    }

    [Fact]
    public async Task Acp_user_input_fails_capability_and_cancels_the_turn()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (sessions, proxy) = CreateProxy();
        var thread = Thread(currentSequence: 1);
        var turnId = Guid.CreateVersion7();
        var interactionId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var events = Channel.CreateUnbounded<SessionEvent>();
        proxy.Handler = (method, args) => method.Name switch
        {
            nameof(ISessionService.CreateThreadAsync) =>
                Task.FromResult(Command(thread, sequence: 1)),
            nameof(ISessionService.SubscribeAsync) =>
                Task.FromResult(Subscription(thread, new TestEventFeed(events.Reader))),
            nameof(ISessionService.GetThreadAsync) =>
                Task.FromResult(new SessionQueryResult<ThreadSnapshot>(thread, null)),
            nameof(ISessionService.EnqueueInputAsync) =>
                Task.FromResult(new SessionCommandResult<SubmittedTurnInputSnapshot>(
                    SessionCommandStatus.Committed,
                    new SubmittedTurnInputSnapshot(
                        new QueuedTurnInputSnapshot(
                            Guid.CreateVersion7(),
                            thread.ThreadId,
                            "ask",
                            0,
                            now),
                        turnId),
                    Sequence: 2,
                    CurrentSequence: null,
                    Error: null)),
            nameof(ISessionService.CancelTurnAsync) =>
                Task.FromResult(new SessionCommandResult<TurnSnapshot>(
                    SessionCommandStatus.Committed,
                    Turn(thread.ThreadId, turnId, TurnStatus.Cancelled),
                    Sequence: 3,
                    CurrentSequence: null,
                    Error: null)),
            _ => throw new NotSupportedException(method.Name),
        };
        var output = new ConcurrentQueue<JsonElement>();
        await using var connection = Connection(sessions, output);
        await InitializeAndCreateAsync(connection, cancellationToken);

        var prompt = ProcessAsync(
            connection,
            $$$"""{"jsonrpc":"2.0","id":3,"method":"session/prompt","params":{"sessionId":"{{{thread.ThreadId:D}}}","prompt":[{"type":"text","text":"ask"}]}}""",
            cancellationToken);
        await events.Writer.WriteAsync(
            Event(
                thread.ThreadId,
                sequence: 2,
                SessionEventType.TurnWaitingInput,
                new SessionEventPayload(
                    Turn: Turn(thread.ThreadId, turnId, TurnStatus.WaitingInput),
                    Item: Item(
                        Guid.CreateVersion7(),
                        turnId,
                        SessionItemType.UserInputRequest,
                        "Value?",
                        now,
                        new UserInputRequestContent("Value?")),
                    Interaction: new PendingInteractionSnapshot(
                        interactionId,
                        thread.ThreadId,
                        turnId,
                        SessionInteractionType.UserInput,
                        IsResolved: false,
                        now,
                        TimeoutAt: null))),
            cancellationToken);
        await prompt;

        var error = output.Last(item =>
            item.TryGetProperty("id", out var id) &&
            id.ValueKind == JsonValueKind.Number &&
            id.GetInt32() == 3).GetProperty("error");
        Assert.Equal(
            "capability_not_supported",
            error.GetProperty("data").GetProperty("code").GetString());
        var cancelled = Assert.IsType<CancelTurnRequest>(proxy.LastRequest);
        Assert.Equal(turnId, cancelled.TurnId);
    }

    [Fact]
    public async Task Acp_rejects_mcp_servers_and_non_text_prompts()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (sessions, proxy) = CreateProxy();
        var thread = Thread(currentSequence: 1);
        proxy.Handler = (method, args) => method.Name switch
        {
            nameof(ISessionService.CreateThreadAsync) =>
                Task.FromResult(Command(thread, sequence: 1)),
            nameof(ISessionService.SubscribeAsync) =>
                Task.FromResult(Subscription(thread)),
            _ => throw new NotSupportedException(method.Name),
        };
        var output = new ConcurrentQueue<JsonElement>();
        await using var connection = Connection(sessions, output);
        await ProcessAsync(
            connection,
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1}}""",
            cancellationToken);
        await ProcessAsync(
            connection,
            WithPlatformWorkspace(
                """{"jsonrpc":"2.0","id":2,"method":"session/new","params":{"cwd":"/workspace","mcpServers":[{}]}}"""),
            cancellationToken);
        await ProcessAsync(
            connection,
            WithPlatformWorkspace(
                """{"jsonrpc":"2.0","id":3,"method":"session/new","params":{"cwd":"/workspace","mcpServers":[]}}"""),
            cancellationToken);
        await ProcessAsync(
            connection,
            $$$"""{"jsonrpc":"2.0","id":4,"method":"session/prompt","params":{"sessionId":"{{{thread.ThreadId:D}}}","prompt":[{"type":"image","data":"ignored"}]}}""",
            cancellationToken);

        Assert.Equal(
            "capability_not_supported",
            output.Single(item => item.TryGetProperty("id", out var id) &&
                                  id.ValueKind == JsonValueKind.Number &&
                                  id.GetInt32() == 2)
                .GetProperty("error").GetProperty("data")
                .GetProperty("code").GetString());
        Assert.Equal(
            "capability_not_supported",
            output.Single(item => item.TryGetProperty("id", out var id) &&
                                  id.ValueKind == JsonValueKind.Number &&
                                  id.GetInt32() == 4)
                .GetProperty("error").GetProperty("data")
                .GetProperty("code").GetString());
    }

    private static OpenCoWorkAcpConnection Connection(
        ISessionService sessions,
        ConcurrentQueue<JsonElement> output) =>
        new(
            sessions,
            WorkspacePath,
            "provider",
            "model",
            (message, _) =>
            {
                using var document = JsonDocument.Parse(message);
                output.Enqueue(document.RootElement.Clone());
                return ValueTask.CompletedTask;
            });

    private static async Task InitializeAndCreateAsync(
        OpenCoWorkAcpConnection connection,
        CancellationToken cancellationToken)
    {
        await ProcessAsync(
            connection,
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1}}""",
            cancellationToken);
        await ProcessAsync(
            connection,
            WithPlatformWorkspace(
                """{"jsonrpc":"2.0","id":2,"method":"session/new","params":{"cwd":"/workspace","mcpServers":[]}}"""),
            cancellationToken);
    }

    private static string WorkspacePath { get; } =
        Path.Combine(Path.GetTempPath(), "opencowork-acp-tests");

    private static string WithPlatformWorkspace(string message) =>
        message.Replace(
            "\"/workspace\"",
            JsonSerializer.Serialize(WorkspacePath),
            StringComparison.Ordinal);

    private static Task ProcessAsync(
        OpenCoWorkAcpConnection connection,
        string message,
        CancellationToken cancellationToken) =>
        connection.ProcessAsync(
            System.Text.Encoding.UTF8.GetBytes(message),
            cancellationToken);

    private static SessionSubscription Subscription(
        ThreadSnapshot thread,
        IAsyncEnumerable<SessionEvent>? events = null) =>
        new(
            SessionSubscriptionDisposition.Ready,
            thread,
            thread.CurrentSequence,
            events ?? EmptyEvents());

    private static async IAsyncEnumerable<SessionEvent> EmptyEvents()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static SessionCommandResult<ThreadSnapshot> Command(
        ThreadSnapshot thread,
        long sequence) =>
        new(
            SessionCommandStatus.Committed,
            thread,
            sequence,
            CurrentSequence: null,
            Error: null);

    private static ThreadSnapshot Thread(long currentSequence)
    {
        var now = DateTimeOffset.UtcNow;
        return new ThreadSnapshot(
            Guid.CreateVersion7(),
            "ACP",
            ThreadStatus.Active,
            ThreadAvailability.Available,
            HistoryMode.Server,
            currentSequence,
            activeTurnId: null,
            queue: [],
            now,
            now,
            SessionProjectionState.Ready,
            diagnostic: null,
            "provider",
            "model",
            AgentMode.Agent);
    }

    private static TurnSnapshot Turn(
        Guid threadId,
        Guid turnId,
        TurnStatus status) =>
        new(
            turnId,
            threadId,
            status,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            status is TurnStatus.Completed or TurnStatus.Failed or TurnStatus.Cancelled
                ? DateTimeOffset.UtcNow
                : null,
            Error: null);

    private static SessionItemSnapshot Item(
        Guid itemId,
        Guid turnId,
        SessionItemType type,
        string text,
        DateTimeOffset now,
        SessionItemContent? content = null) =>
        new(
            itemId,
            turnId,
            type,
            SessionItemStatus.Completed,
            content ?? new TextItemContent(text),
            Sequence: 1,
            now,
            now);

    private static SessionEvent Event(
        Guid threadId,
        long sequence,
        SessionEventType type,
        SessionEventPayload payload) =>
        new(
            threadId,
            sequence,
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            type,
            payload);

    private static async Task<JsonElement> WaitForAsync(
        ConcurrentQueue<JsonElement> output,
        Func<JsonElement, bool> predicate,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!output.Any(predicate))
            {
                await Task.Delay(10, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(
                "Timed out waiting for ACP output. Observed: " +
                string.Join(Environment.NewLine, output.Select(item => item.GetRawText())));
        }

        return output.First(predicate);
    }

    private static (ISessionService Service, SessionProxy Proxy) CreateProxy()
    {
        var service = DispatchProxy.Create<ISessionService, SessionProxy>();
        return (service, (SessionProxy)(object)service);
    }

    private class SessionProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?>? Handler { get; set; }

        public object? LastRequest { get; private set; }

        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? args)
        {
            LastRequest = args?.FirstOrDefault();
            return Handler?.Invoke(targetMethod!, args) ??
                   throw new NotSupportedException(targetMethod?.Name);
        }
    }

    private sealed class TestEventFeed(ChannelReader<SessionEvent> reader)
        : IAsyncEnumerable<SessionEvent>
    {
        public IAsyncEnumerator<SessionEvent> GetAsyncEnumerator(
            CancellationToken cancellationToken = default) =>
            reader.ReadAllAsync(cancellationToken).GetAsyncEnumerator(
                cancellationToken);
    }
}
