using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCoWork.Abstractions;

namespace OpenCoWork.Protocol;

public sealed class OpenCoWorkAcpConnection : IAsyncDisposable
{
    private const int ProtocolVersion = 1;
    private const int ParseError = -32700;
    private const int InvalidRequest = -32600;
    private const int MethodNotFound = -32601;
    private const int InvalidParams = -32602;
    private const int InternalError = -32603;
    private const int BusinessError = -32000;
    private const string CapabilityNotSupported = "capability_not_supported";

    private static readonly JsonElement EmptyParameters =
        JsonSerializer.SerializeToElement(new { });
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 64,
    };

    private readonly ISessionService _sessions;
    private readonly string _workspacePath;
    private readonly string _defaultProvider;
    private readonly string _defaultModel;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> _send;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<Guid, ActiveSession> _activeSessions = [];
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>>
        _clientRequests = [];
    private int _nextClientRequestId;
    private int _initialized;
    private int _disposed;

    public OpenCoWorkAcpConnection(
        ISessionService sessions,
        string workspacePath,
        string defaultProvider,
        string defaultModel,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> send)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultModel);
        ArgumentNullException.ThrowIfNull(send);
        _sessions = sessions;
        _workspacePath = Path.GetFullPath(workspacePath);
        _defaultProvider = defaultProvider;
        _defaultModel = defaultModel;
        _send = send;
    }

    public async Task ProcessAsync(
        ReadOnlyMemory<byte> message,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        if (message.Length > OpenCoWorkWire.MaximumMessageBytes)
        {
            await SendErrorAsync(
                id: null,
                InvalidRequest,
                "Invalid Request.",
                data: null,
                cancellationToken);
            return;
        }

        JsonElement envelope;
        try
        {
            using var document = JsonDocument.Parse(
                message,
                new JsonDocumentOptions { MaxDepth = JsonOptions.MaxDepth });
            envelope = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            await SendErrorAsync(
                id: null,
                ParseError,
                "Parse error.",
                data: null,
                cancellationToken);
            return;
        }

        if (TryCompleteClientRequest(envelope))
        {
            return;
        }

        JsonElement? id = null;
        var hasId = false;
        if (envelope.ValueKind == JsonValueKind.Object &&
            envelope.TryGetProperty("id", out var requestId))
        {
            hasId = true;
            id = requestId.Clone();
        }

        try
        {
            if (envelope.ValueKind != JsonValueKind.Object ||
                !envelope.TryGetProperty("jsonrpc", out var jsonrpc) ||
                jsonrpc.GetString() != "2.0" ||
                !envelope.TryGetProperty("method", out var methodElement) ||
                methodElement.ValueKind != JsonValueKind.String)
            {
                throw new AcpRpcException(
                    InvalidRequest,
                    "Invalid Request.");
            }

            var method = methodElement.GetString()!;
            var parameters = envelope.TryGetProperty("params", out var value)
                ? value
                : EmptyParameters;
            object result;
            if (method == "initialize")
            {
                result = Initialize(parameters);
            }
            else
            {
                if (Volatile.Read(ref _initialized) == 0)
                {
                    throw SessionFailure(new SessionError(
                        SessionErrorCodes.InvalidState,
                        "Connection is not initialized.",
                        IsRetryable: false));
                }

                result = await DispatchAsync(
                    method,
                    parameters,
                    cancellationToken);
            }

            if (hasId)
            {
                await SendResultAsync(id, result, cancellationToken);
            }
        }
        catch (AcpRpcException exception)
        {
            if (hasId)
            {
                await SendErrorAsync(
                    id,
                    exception.Code,
                    exception.Message,
                    exception.ErrorData,
                    cancellationToken);
            }
        }
        catch (Exception exception) when (
            exception is JsonException
                or ArgumentException
                or FormatException)
        {
            if (hasId)
            {
                await SendErrorAsync(
                    id,
                    InvalidParams,
                    "Invalid params.",
                    data: null,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested ||
            _lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            if (hasId)
            {
                await SendErrorAsync(
                    id,
                    InternalError,
                    "Internal error.",
                    data: null,
                    cancellationToken);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifetime.CancelAsync();
        foreach (var request in _clientRequests.Values)
        {
            request.TrySetCanceled(_lifetime.Token);
        }

        foreach (var pair in _activeSessions.ToArray())
        {
            if (_activeSessions.TryRemove(pair.Key, out var active))
            {
                await active.DisposeAsync();
            }
        }

        _lifetime.Dispose();
    }

    private object Initialize(JsonElement parameters)
    {
        _ = Required(parameters, "protocolVersion").GetInt32();
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0)
        {
            throw SessionFailure(new SessionError(
                SessionErrorCodes.InvalidState,
                "Connection is already initialized.",
                IsRetryable: false));
        }

        return new
        {
            protocolVersion = ProtocolVersion,
            agentCapabilities = new
            {
                loadSession = true,
                promptCapabilities = new
                {
                    image = false,
                    audio = false,
                    embeddedContext = false,
                },
                mcpCapabilities = new
                {
                    http = false,
                    sse = false,
                },
                sessionCapabilities = new { },
                auth = new { },
            },
            authMethods = Array.Empty<object>(),
            agentInfo = new
            {
                name = "OpenCoWork",
                version = typeof(OpenCoWorkAcpConnection).Assembly
                              .GetName().Version?.ToString() ?? "1.0.0",
            },
        };
    }

    private async Task<object> DispatchAsync(
        string method,
        JsonElement parameters,
        CancellationToken cancellationToken) =>
        method switch
        {
            "session/new" => await NewSessionAsync(parameters, cancellationToken),
            "session/load" => await LoadSessionAsync(parameters, cancellationToken),
            "session/prompt" => await PromptAsync(parameters, cancellationToken),
            "session/cancel" => await CancelAsync(parameters, cancellationToken),
            "session/set_mode" => await SetModeAsync(parameters, cancellationToken),
            _ => throw new AcpRpcException(MethodNotFound, "Method not found."),
        };

    private async Task<object> NewSessionAsync(
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        ValidateSessionBoundary(parameters);
        var result = await _sessions.CreateThreadAsync(
            new CreateThreadRequest(
                Guid.CreateVersion7(),
                ExpectedSequence: 0,
                DisplayName: null,
                HistoryMode.Server,
                _defaultProvider,
                _defaultModel,
                AgentMode.Agent),
            cancellationToken);
        var thread = Require(result);
        await AttachAsync(thread.ThreadId, replay: false, cancellationToken);
        return new
        {
            sessionId = Id(thread.ThreadId),
            modes = Modes(thread.AgentMode),
        };
    }

    private async Task<object> LoadSessionAsync(
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        ValidateSessionBoundary(parameters);
        var threadId = SessionId(parameters);
        var thread = await AttachAsync(threadId, replay: true, cancellationToken);
        return new { modes = Modes(thread.AgentMode) };
    }

    private async Task<object> PromptAsync(
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var threadId = SessionId(parameters);
        if (!_activeSessions.TryGetValue(threadId, out var active))
        {
            throw SessionFailure(new SessionError(
                SessionErrorCodes.InvalidState,
                "Session must be created or loaded first.",
                IsRetryable: false));
        }

        var prompt = Required(parameters, "prompt");
        if (prompt.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("Prompt must be an array.");
        }

        var text = new StringBuilder();
        foreach (var block in prompt.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object ||
                Required(block, "type").GetString() != "text")
            {
                throw CapabilityError();
            }

            text.Append(Required(block, "text").GetString());
        }

        if (text.Length == 0)
        {
            throw new ArgumentException("Prompt text is required.");
        }

        var thread = Require(await _sessions.GetThreadAsync(
            threadId,
            cancellationToken));
        var submitted = Require(await _sessions.EnqueueInputAsync(
            new EnqueueInputRequest(
                threadId,
                Guid.CreateVersion7(),
                thread.CurrentSequence,
                text.ToString(),
                TurnAdmission.StartOnly),
            cancellationToken));
        var turnId = submitted.TurnId ??
                     throw new InvalidOperationException(
                         "StartOnly did not start a turn.");
        var stopReason = await active.WaitForTurnAsync(turnId, cancellationToken);
        return new { stopReason };
    }

    private async Task<object> CancelAsync(
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var threadId = SessionId(parameters);
        var thread = Require(await _sessions.GetThreadAsync(
            threadId,
            cancellationToken));
        if (thread.ActiveTurnId is { } turnId)
        {
            _ = Require(await _sessions.CancelTurnAsync(
                new CancelTurnRequest(
                    threadId,
                    turnId,
                    Guid.CreateVersion7(),
                    thread.CurrentSequence),
                cancellationToken));
        }

        return new { };
    }

    private async Task<object> SetModeAsync(
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var threadId = SessionId(parameters);
        var mode = Required(parameters, "modeId").GetString() switch
        {
            "agent" => AgentMode.Agent,
            "plan" => AgentMode.Plan,
            _ => throw new ArgumentException("Mode is unsupported."),
        };
        var thread = Require(await _sessions.GetThreadAsync(
            threadId,
            cancellationToken));
        _ = Require(await _sessions.SetAgentModeAsync(
            new SetAgentModeRequest(
                threadId,
                Guid.CreateVersion7(),
                thread.CurrentSequence,
                mode),
            cancellationToken));
        return new { };
    }

    private async Task<ThreadSnapshot> AttachAsync(
        Guid threadId,
        bool replay,
        CancellationToken cancellationToken)
    {
        var subscription = await _sessions.SubscribeAsync(
            new SessionSubscriptionRequest(
                threadId,
                replay
                    ? SessionSubscriptionMode.ResumeAfterSequence
                    : SessionSubscriptionMode.SnapshotThenLive,
                replay ? 0 : null),
            cancellationToken);
        if (subscription.Disposition != SessionSubscriptionDisposition.Ready)
        {
            await subscription.DisposeAsync();
            throw SessionFailure(new SessionError(
                SessionErrorCodes.SubscriberLagged,
                "Session history cannot be resumed.",
                IsRetryable: true));
        }

        var active = new ActiveSession(this, subscription, replay, _lifetime.Token);
        if (_activeSessions.TryRemove(threadId, out var prior))
        {
            await prior.DisposeAsync();
        }

        _activeSessions[threadId] = active;
        active.Start();
        if (replay)
        {
            await active.WaitForCatchUpAsync(cancellationToken);
        }

        return subscription.Snapshot;
    }

    private async Task HandleEventAsync(
        ActiveSession active,
        SessionEvent sessionEvent,
        CancellationToken cancellationToken)
    {
        if (sessionEvent.Type == SessionEventType.ThreadModeChanged &&
            sessionEvent.Payload.Thread is { } changedThread)
        {
            await SendUpdateAsync(
                sessionEvent.ThreadId,
                new
                {
                    sessionUpdate = "current_mode_update",
                    currentModeId = Mode(changedThread.AgentMode),
                },
                cancellationToken);
        }

        if (sessionEvent.Type == SessionEventType.TurnStarted &&
            sessionEvent.Payload.Item is { Type: SessionItemType.UserMessage } user)
        {
            await SendTextUpdateAsync(
                sessionEvent.ThreadId,
                user,
                "user_message_chunk",
                active.Observe(user),
                cancellationToken);
        }
        else if ((sessionEvent.Type is
                  SessionEventType.ItemDeltaAppended or
                  SessionEventType.ItemCompleted) &&
                 sessionEvent.Payload.Item is { } item)
        {
            if (item.Type == SessionItemType.SystemNotice &&
                item.Content is SystemNoticeContent { Message: "response.truncated" })
            {
                active.MarkTruncated(item.TurnId);
            }
            else if (item.Type is
                     SessionItemType.AgentMessage or
                     SessionItemType.Reasoning)
            {
                await SendTextUpdateAsync(
                    sessionEvent.ThreadId,
                    item,
                    item.Type == SessionItemType.AgentMessage
                        ? "agent_message_chunk"
                        : "agent_thought_chunk",
                    active.Delta(item),
                    cancellationToken);
            }
        }

        if (sessionEvent.Type == SessionEventType.TurnWaitingApproval)
        {
            await HandleApprovalAsync(sessionEvent, cancellationToken);
        }
        else if (sessionEvent.Type == SessionEventType.TurnWaitingInput)
        {
            await HandleUnsupportedInputAsync(active, sessionEvent, cancellationToken);
        }
        else if (sessionEvent.Type == SessionEventType.TurnCompleted &&
                 sessionEvent.Payload.Turn is { } completed)
        {
            active.CompleteTurn(
                completed.TurnId,
                active.IsTruncated(completed.TurnId)
                    ? "max_tokens"
                    : "end_turn");
        }
        else if (sessionEvent.Type == SessionEventType.TurnCancelled &&
                 sessionEvent.Payload.Turn is { } cancelled)
        {
            active.CompleteTurn(cancelled.TurnId, "cancelled");
        }
        else if (sessionEvent.Type == SessionEventType.TurnFailed &&
                 sessionEvent.Payload.Turn is { } failed)
        {
            if (failed.Error?.Code == AgentErrorCodes.ProviderContentFiltered)
            {
                active.CompleteTurn(failed.TurnId, "refusal");
            }
            else
            {
                active.FailTurn(
                    failed.TurnId,
                    SessionFailure(failed.Error ?? new SessionError(
                        SessionErrorCodes.InvalidState,
                        "Turn failed.",
                        IsRetryable: false)));
            }
        }
    }

    private async Task HandleApprovalAsync(
        SessionEvent sessionEvent,
        CancellationToken cancellationToken)
    {
        if (sessionEvent.Payload is not
            {
                Turn: { } turn,
                Item.Content: ApprovalRequestContent approval,
                Interaction: { } interaction,
            })
        {
            throw new InvalidDataException("Approval event is incomplete.");
        }

        var result = await RequestClientAsync(
            "session/request_permission",
            new
            {
                sessionId = Id(sessionEvent.ThreadId),
                toolCall = new
                {
                    toolCallId = Id(
                        interaction.ToolInvocationId ??
                        sessionEvent.Payload.Item!.ItemId),
                    kind = "other",
                    status = "pending",
                    title = approval.Prompt,
                },
                options = new[]
                {
                    new
                    {
                        optionId = "allow-once",
                        name = "Allow once",
                        kind = "allow_once",
                    },
                    new
                    {
                        optionId = "reject-once",
                        name = "Reject once",
                        kind = "reject_once",
                    },
                },
            },
            cancellationToken);
        var outcome = Required(Required(result, "outcome"), "outcome").GetString();
        if (outcome == "cancelled")
        {
            return;
        }

        if (outcome != "selected")
        {
            throw new InvalidDataException("Permission outcome is invalid.");
        }

        var approved = Required(Required(result, "outcome"), "optionId")
            .GetString() switch
        {
            "allow-once" => true,
            "reject-once" => false,
            _ => throw new InvalidDataException("Permission option is invalid."),
        };
        var thread = Require(await _sessions.GetThreadAsync(
            sessionEvent.ThreadId,
            cancellationToken));
        _ = Require(await _sessions.ResolveInteractionAsync(
            new ResolveInteractionRequest(
                sessionEvent.ThreadId,
                turn.TurnId,
                interaction.InteractionId,
                new ApprovalResponseContent(approved, Comment: null),
                Guid.CreateVersion7(),
                thread.CurrentSequence),
            cancellationToken));
    }

    private async Task HandleUnsupportedInputAsync(
        ActiveSession active,
        SessionEvent sessionEvent,
        CancellationToken cancellationToken)
    {
        if (sessionEvent.Payload.Turn is not { } turn)
        {
            throw new InvalidDataException("User input event is incomplete.");
        }

        active.FailTurn(turn.TurnId, CapabilityError());
        var thread = Require(await _sessions.GetThreadAsync(
            sessionEvent.ThreadId,
            cancellationToken));
        _ = Require(await _sessions.CancelTurnAsync(
            new CancelTurnRequest(
                sessionEvent.ThreadId,
                turn.TurnId,
                Guid.CreateVersion7(),
                thread.CurrentSequence),
            cancellationToken));
    }

    private async Task SendTextUpdateAsync(
        Guid threadId,
        SessionItemSnapshot item,
        string updateType,
        string text,
        CancellationToken cancellationToken)
    {
        if (text.Length == 0)
        {
            return;
        }

        await SendUpdateAsync(
            threadId,
            new
            {
                sessionUpdate = updateType,
                content = new
                {
                    type = "text",
                    text,
                },
                messageId = Id(item.ItemId),
            },
            cancellationToken);
    }

    private Task SendUpdateAsync(
        Guid threadId,
        object update,
        CancellationToken cancellationToken) =>
        SendAsync(
            new
            {
                jsonrpc = "2.0",
                method = "session/update",
                @params = new
                {
                    sessionId = Id(threadId),
                    update,
                },
            },
            cancellationToken);

    private async Task<JsonElement> RequestClientAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        var id = string.Create(
            CultureInfo.InvariantCulture,
            $"opencowork-{Interlocked.Increment(ref _nextClientRequestId)}");
        var completion = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_clientRequests.TryAdd(id, completion))
        {
            throw new InvalidOperationException("Client request ID collision.");
        }

        try
        {
            await SendAsync(
                new
                {
                    jsonrpc = "2.0",
                    id,
                    method,
                    @params = parameters,
                },
                cancellationToken);
            return await completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            _clientRequests.TryRemove(id, out _);
        }
    }

    private bool TryCompleteClientRequest(JsonElement envelope)
    {
        if (envelope.ValueKind != JsonValueKind.Object ||
            envelope.TryGetProperty("method", out _) ||
            !envelope.TryGetProperty("jsonrpc", out var jsonrpc) ||
            jsonrpc.GetString() != "2.0" ||
            !envelope.TryGetProperty("id", out var id) ||
            id.ValueKind != JsonValueKind.String ||
            !_clientRequests.TryRemove(id.GetString()!, out var completion))
        {
            return false;
        }

        if (envelope.TryGetProperty("result", out var result))
        {
            completion.TrySetResult(result.Clone());
        }
        else
        {
            completion.TrySetException(
                new IOException("ACP client request failed."));
        }

        return true;
    }

    private Task SendResultAsync(
        JsonElement? id,
        object result,
        CancellationToken cancellationToken) =>
        SendAsync(
            new
            {
                jsonrpc = "2.0",
                id,
                result,
            },
            cancellationToken);

    private Task SendErrorAsync(
        JsonElement? id,
        int code,
        string message,
        object? data,
        CancellationToken cancellationToken) =>
        SendAsync(
            new
            {
                jsonrpc = "2.0",
                id,
                error = new
                {
                    code,
                    message,
                    data,
                },
            },
            cancellationToken);

    private async Task SendAsync(
        object message,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        await _send(bytes, cancellationToken);
    }

    private void ValidateSessionBoundary(JsonElement parameters)
    {
        var cwd = Required(parameters, "cwd").GetString();
        if (string.IsNullOrWhiteSpace(cwd) ||
            !Path.IsPathFullyQualified(cwd) ||
            !string.Equals(
                Path.GetFullPath(cwd),
                _workspacePath,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw SessionFailure(new SessionError(
                "protocol.workspaceMismatch",
                "Workspace does not match this process.",
                IsRetryable: false));
        }

        var mcpServers = Required(parameters, "mcpServers");
        if (mcpServers.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("mcpServers must be an array.");
        }

        if (mcpServers.GetArrayLength() != 0 ||
            parameters.TryGetProperty("additionalDirectories", out var directories) &&
            directories.ValueKind == JsonValueKind.Array &&
            directories.GetArrayLength() != 0)
        {
            throw CapabilityError();
        }
    }

    private static Guid SessionId(JsonElement parameters)
    {
        var value = Required(parameters, "sessionId").GetString();
        return Guid.TryParse(value, out var id) && id != Guid.Empty
            ? id
            : throw new ArgumentException("Session ID is invalid.");
    }

    private static JsonElement Required(JsonElement value, string property)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty(property, out var result))
        {
            throw new ArgumentException($"Required property '{property}' is missing.");
        }

        return result;
    }

    private static T Require<T>(SessionCommandResult<T> result)
    {
        if (result.Status == SessionCommandStatus.Rejected || result.Value is null)
        {
            throw SessionFailure(result.Error ?? new SessionError(
                SessionErrorCodes.InvalidState,
                "Session operation was rejected.",
                IsRetryable: false));
        }

        return result.Value;
    }

    private static T Require<T>(SessionQueryResult<T> result) =>
        result.Error is null && result.Value is not null
            ? result.Value
            : throw SessionFailure(result.Error ?? new SessionError(
                SessionErrorCodes.NotFound,
                "Session value was not found.",
                IsRetryable: false));

    private static object Modes(AgentMode mode) => new
    {
        currentModeId = Mode(mode),
        availableModes = new[]
        {
            new { id = "agent", name = "Agent" },
            new { id = "plan", name = "Plan" },
        },
    };

    private static string Mode(AgentMode mode) =>
        mode == AgentMode.Agent ? "agent" : "plan";

    private static string Id(Guid id) =>
        id.ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant();

    private static AcpRpcException CapabilityError() =>
        new(
            BusinessError,
            CapabilityNotSupported,
            new { code = CapabilityNotSupported });

    private static AcpRpcException SessionFailure(SessionError error) =>
        new(
            BusinessError,
            "Session operation failed.",
            new
            {
                code = error.Code,
                retryable = error.IsRetryable,
            });

    private sealed class AcpRpcException(
        int code,
        string message,
        object? errorData = null) : Exception(message)
    {
        public int Code { get; } = code;

        public object? ErrorData { get; } = errorData;
    }

    private sealed class ActiveSession : IAsyncDisposable
    {
        private readonly OpenCoWorkAcpConnection _owner;
        private readonly SessionSubscription _subscription;
        private readonly CancellationTokenSource _lifetime;
        private readonly TaskCompletionSource _caughtUp = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<string>>
            _waiters = [];
        private readonly ConcurrentDictionary<Guid, TurnCompletion> _terminals = [];
        private readonly Dictionary<Guid, int> _offsets = [];
        private readonly HashSet<Guid> _truncatedTurns = [];
        private readonly bool _replay;
        private Task _pump = Task.CompletedTask;
        private Exception? _failure;
        private long _lastSequence;

        public ActiveSession(
            OpenCoWorkAcpConnection owner,
            SessionSubscription subscription,
            bool replay,
            CancellationToken cancellationToken)
        {
            _owner = owner;
            _subscription = subscription;
            _replay = replay;
            _lastSequence = replay ? 0 : subscription.CurrentSequence;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            if (!replay || subscription.CurrentSequence == 0)
            {
                _caughtUp.TrySetResult();
            }
        }

        public void Start() => _pump = PumpAsync();

        public Task WaitForCatchUpAsync(CancellationToken cancellationToken) =>
            _caughtUp.Task.WaitAsync(cancellationToken);

        public async Task<string> WaitForTurnAsync(
            Guid turnId,
            CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _failure) is { } failure)
            {
                throw failure;
            }

            var waiter = _waiters.GetOrAdd(
                turnId,
                static _ => new TaskCompletionSource<string>(
                    TaskCreationOptions.RunContinuationsAsynchronously));
            if (_terminals.TryGetValue(turnId, out var terminal))
            {
                terminal.Apply(waiter);
            }
            else if (Volatile.Read(ref _failure) is { } laterFailure)
            {
                waiter.TrySetException(laterFailure);
            }

            return await waiter.Task.WaitAsync(cancellationToken);
        }

        public string Observe(SessionItemSnapshot item)
        {
            if (item.Content is not TextItemContent text)
            {
                return string.Empty;
            }

            _offsets[item.ItemId] = text.Text.Length;
            return text.Text;
        }

        public string Delta(SessionItemSnapshot item)
        {
            if (item.Content is not TextItemContent text)
            {
                return string.Empty;
            }

            var offset = _offsets.GetValueOrDefault(item.ItemId);
            if (offset > text.Text.Length)
            {
                offset = 0;
            }

            _offsets[item.ItemId] = text.Text.Length;
            return text.Text[offset..];
        }

        public void MarkTruncated(Guid turnId) => _truncatedTurns.Add(turnId);

        public bool IsTruncated(Guid turnId) => _truncatedTurns.Remove(turnId);

        public void CompleteTurn(Guid turnId, string stopReason) =>
            Complete(turnId, new TurnCompletion(stopReason, Error: null));

        public void FailTurn(Guid turnId, Exception error) =>
            Complete(turnId, new TurnCompletion(StopReason: null, error));

        public async ValueTask DisposeAsync()
        {
            await _lifetime.CancelAsync();
            try
            {
                await _pump;
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }

            await _subscription.DisposeAsync();
            foreach (var waiter in _waiters.Values)
            {
                waiter.TrySetCanceled(_lifetime.Token);
            }

            _lifetime.Dispose();
        }

        private async Task PumpAsync()
        {
            try
            {
                await foreach (var sessionEvent in _subscription.Events
                                   .WithCancellation(_lifetime.Token))
                {
                    if (sessionEvent.Sequence <= _lastSequence)
                    {
                        continue;
                    }

                    await _owner.HandleEventAsync(
                        this,
                        sessionEvent,
                        _lifetime.Token);
                    _lastSequence = sessionEvent.Sequence;
                    if (_replay &&
                        sessionEvent.Sequence >= _subscription.CurrentSequence)
                    {
                        _caughtUp.TrySetResult();
                    }
                }

                if (_replay && !_caughtUp.Task.IsCompleted)
                {
                    Fail(new IOException("ACP history catch-up ended early."));
                }
                else if (!_lifetime.IsCancellationRequested)
                {
                    Fail(new IOException("ACP session subscription ended."));
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }

        private void Complete(Guid turnId, TurnCompletion completion)
        {
            _terminals[turnId] = completion;
            if (_waiters.TryGetValue(turnId, out var waiter))
            {
                completion.Apply(waiter);
            }
        }

        private void Fail(Exception exception)
        {
            Interlocked.CompareExchange(ref _failure, exception, comparand: null);
            _caughtUp.TrySetException(exception);
            foreach (var waiter in _waiters.Values)
            {
                waiter.TrySetException(exception);
            }
        }
    }

    private sealed record TurnCompletion(string? StopReason, Exception? Error)
    {
        public void Apply(TaskCompletionSource<string> completion)
        {
            if (Error is not null)
            {
                completion.TrySetException(Error);
            }
            else
            {
                completion.TrySetResult(StopReason!);
            }
        }
    }
}
