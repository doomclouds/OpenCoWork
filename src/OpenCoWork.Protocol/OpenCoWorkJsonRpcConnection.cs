using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCoWork.Abstractions;

namespace OpenCoWork.Protocol;

public sealed partial class OpenCoWorkJsonRpcConnection : IAsyncDisposable
{
    private const int ParseError = -32700;
    private const int InvalidRequest = -32600;
    private const int MethodNotFound = -32601;
    private const int InvalidParams = -32602;
    private const int InternalError = -32603;
    private const int BusinessError = -32000;
    private const int ConflictError = -32001;
    private const int NotFoundError = -32002;
    private const int InvalidStateError = -32003;
    private const int UnavailableError = -32004;
    private const int CancelledError = -32005;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 64,
        Converters =
        {
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false),
        },
    };
    private static readonly IReadOnlyDictionary<string, string> ClientMethodVersions =
        typeof(OpenCoWorkJsonRpcConnection).Assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods(
                BindingFlags.Instance |
                BindingFlags.Static |
                BindingFlags.Public |
                BindingFlags.NonPublic))
            .Select(method => method
                .GetCustomAttribute<OpenCoWorkWireMethodAttribute>())
            .Where(attribute =>
                attribute?.Direction == OpenCoWorkWire.ClientToServer)
            .Cast<OpenCoWorkWireMethodAttribute>()
            .ToDictionary(
                attribute => attribute.Method,
                attribute => attribute.Since,
                StringComparer.Ordinal);

    private readonly ISessionService _sessions;
    private readonly ICapabilityService? _capabilities;
    private readonly ICoWorkService? _coWork;
    private readonly IAutomationService? _automations;
    private readonly string _workspacePath;
    private readonly string _transport;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> _send;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Guid _connectionId = Guid.CreateVersion7();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlight = [];
    private readonly ConcurrentDictionary<string, PendingClientRequest> _clientRequests = [];
    private readonly ConcurrentDictionary<string, ActiveSubscription> _subscriptions = [];
    private HashSet<string> _clientCapabilities = new(StringComparer.Ordinal);
    private long _nextClientRequestId;
    private string _wireVersion = OpenCoWorkWire.Version;
    private int _capabilitySubscribed;
    private int _automationSubscribed;
    private int _initialized;
    private int _disposed;

    public OpenCoWorkJsonRpcConnection(
        ISessionService sessions,
        string workspacePath,
        string transport,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> send)
        : this(
            sessions,
            capabilities: null,
            coWork: null,
            automations: null,
            workspacePath,
            transport,
            send)
    {
    }

    public OpenCoWorkJsonRpcConnection(
        ISessionService sessions,
        ICapabilityService? capabilities,
        string workspacePath,
        string transport,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> send)
        : this(
            sessions,
            capabilities,
            coWork: null,
            automations: null,
            workspacePath,
            transport,
            send)
    {
    }

    public OpenCoWorkJsonRpcConnection(
        ISessionService sessions,
        ICapabilityService? capabilities,
        ICoWorkService? coWork,
        string workspacePath,
        string transport,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> send)
        : this(
            sessions,
            capabilities,
            coWork,
            automations: null,
            workspacePath,
            transport,
            send)
    {
    }

    public OpenCoWorkJsonRpcConnection(
        ISessionService sessions,
        ICapabilityService? capabilities,
        ICoWorkService? coWork,
        IAutomationService? automations,
        string workspacePath,
        string transport,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> send)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(transport);
        ArgumentNullException.ThrowIfNull(send);
        _sessions = sessions;
        _capabilities = capabilities;
        _coWork = coWork;
        _automations = automations;
        _workspacePath = Path.GetFullPath(workspacePath);
        _transport = transport;
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

        JsonElement request;
        try
        {
            using var document = JsonDocument.Parse(
                message,
                new JsonDocumentOptions { MaxDepth = JsonOptions.MaxDepth });
            request = document.RootElement.Clone();
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

        if (TryCompleteClientRequest(request))
        {
            return;
        }

        if (!TryReadRequest(
                request,
                out var id,
                out var hasId,
                out var method,
                out var parameters))
        {
            await SendErrorAsync(
                id: null,
                InvalidRequest,
                "Invalid Request.",
                data: null,
                cancellationToken);
            return;
        }

        var correlationId = Guid.CreateVersion7().ToString(
            "D",
            CultureInfo.InvariantCulture);
        if (string.Equals(method, "$/cancelRequest", StringComparison.Ordinal))
        {
            try
            {
                await CancelRequestAsync(parameters);
                if (hasId)
                {
                    await SendResultAsync(
                        id,
                        new WireAcknowledgement(),
                        cancellationToken);
                }
            }
            catch (ArgumentException)
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

            return;
        }

        CancellationTokenSource? requestCancellation = null;
        string? requestKey = null;
        if (hasId)
        {
            requestKey = id!.Value.GetRawText();
            requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetime.Token);
            if (!_inFlight.TryAdd(requestKey, requestCancellation))
            {
                requestCancellation.Dispose();
                await SendErrorAsync(
                    id,
                    InvalidRequest,
                    "Invalid Request.",
                    data: null,
                    cancellationToken);
                return;
            }
        }

        var effectiveToken = requestCancellation?.Token ?? cancellationToken;
        try
        {
            object result;
            if (string.Equals(method, "initialize", StringComparison.Ordinal))
            {
                result = Initialize(parameters, correlationId);
            }
            else
            {
                if (Volatile.Read(ref _initialized) == 0)
                {
                    throw new WireRpcException(
                        new SessionError(
                            SessionErrorCodes.InvalidState,
                            "Connection is not initialized.",
                            IsRetryable: false));
                }

                EnsureMethodAvailable(method);
                result = await DispatchAsync(method, parameters, effectiveToken);
            }

            if (hasId)
            {
                await SendResultAsync(id, result, cancellationToken);
            }
        }
        catch (WireMethodNotFoundException)
        {
            if (hasId)
            {
                await SendErrorAsync(
                    id,
                    MethodNotFound,
                    "Method not found.",
                    data: null,
                    cancellationToken);
            }
        }
        catch (WireRpcException exception)
        {
            if (hasId)
            {
                await SendBusinessErrorAsync(
                    id,
                    exception.Error,
                    exception.CurrentSequence,
                    exception.CurrentRevision,
                    correlationId,
                    cancellationToken);
            }
        }
        catch (CapabilityServiceException exception)
        {
            if (hasId)
            {
                await SendCapabilityErrorAsync(
                    id,
                    exception,
                    correlationId,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (
            requestCancellation?.IsCancellationRequested == true &&
            !cancellationToken.IsCancellationRequested &&
            !_lifetime.IsCancellationRequested)
        {
            if (hasId)
            {
                await SendErrorAsync(
                    id,
                    CancelledError,
                    "Request cancelled.",
                    new WireErrorData(
                        "request.cancelled",
                        Retryable: true,
                        correlationId),
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested ||
            _lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is JsonException
                or ArgumentException
                or FormatException
                or NotSupportedException)
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
        finally
        {
            if (requestKey is not null &&
                _inFlight.TryRemove(requestKey, out var cancellation))
            {
                cancellation.Dispose();
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
        _capabilities?.DisconnectDynamicTools(_connectionId);
        foreach (var pending in _clientRequests.Values)
        {
            pending.Completion.TrySetException(
                new IOException("Dynamic Tool client disconnected."));
        }

        _clientRequests.Clear();
        if (Interlocked.Exchange(ref _capabilitySubscribed, 0) != 0)
        {
            _capabilities!.CatalogChanged -= OnCapabilityCatalogChanged;
        }
        if (Interlocked.Exchange(ref _automationSubscribed, 0) != 0)
        {
            _automations!.Changed -= OnAutomationChanged;
        }

        foreach (var cancellation in _inFlight.Values)
        {
            await cancellation.CancelAsync();
        }

        var subscriptions = _subscriptions.ToArray();
        foreach (var pair in subscriptions)
        {
            if (_subscriptions.TryRemove(pair.Key, out var subscription))
            {
                await subscription.StopAsync();
            }
        }

        _lifetime.Dispose();
    }

    private WireInitializeResponse Initialize(
        JsonElement parameters,
        string correlationId)
    {
        var request = Deserialize<WireInitializeRequest>(parameters);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Client.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Client.Version);
        ArgumentNullException.ThrowIfNull(request.WireVersions);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Workspace.Path);
        var wireVersion =
            _automations is not null &&
            request.WireVersions.Contains(
                OpenCoWorkWire.LatestVersion,
                StringComparer.Ordinal)
                ? OpenCoWorkWire.LatestVersion
                : _coWork is not null &&
            request.WireVersions.Contains(
                OpenCoWorkWire.CoWorkVersion,
                StringComparer.Ordinal)
                ? OpenCoWorkWire.CoWorkVersion
                : _capabilities is not null &&
            request.WireVersions.Contains(
                OpenCoWorkWire.CapabilityVersion,
                StringComparer.Ordinal)
                ? OpenCoWorkWire.CapabilityVersion
                : request.WireVersions.Contains(
                    OpenCoWorkWire.Version,
                    StringComparer.Ordinal)
                    ? OpenCoWorkWire.Version
                    : null;
        if (wireVersion is null)
        {
            throw new WireRpcException(
                new SessionError(
                    "protocol.versionUnsupported",
                    "Wire version is unsupported.",
                    IsRetryable: false));
        }

        var requestedWorkspace = Path.GetFullPath(request.Workspace.Path);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(requestedWorkspace, _workspacePath, comparison))
        {
            throw new WireRpcException(
                new SessionError(
                    "protocol.workspaceMismatch",
                    "Workspace does not match this process.",
                    IsRetryable: false));
        }

        if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0)
        {
            throw new WireRpcException(
                new SessionError(
                    SessionErrorCodes.InvalidState,
                    "Connection is already initialized.",
                    IsRetryable: false));
        }

        _wireVersion = wireVersion;
        _clientCapabilities = request.Capabilities?
            .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(
            StringComparer.Ordinal);
        var dynamicToolExecution =
            _clientCapabilities.Contains("serverRequests") &&
            _clientCapabilities.Contains("dynamicToolExecution");
        if (SupportsCapabilityWire(wireVersion) &&
            _capabilities is not null &&
            Interlocked.CompareExchange(ref _capabilitySubscribed, 1, 0) == 0)
        {
            _capabilities!.CatalogChanged += OnCapabilityCatalogChanged;
        }
        if (wireVersion == OpenCoWorkWire.AutomationVersion &&
            _automations is not null &&
            Interlocked.CompareExchange(ref _automationSubscribed, 1, 0) == 0)
        {
            _automations.Changed += OnAutomationChanged;
        }

        var serverCapabilities = new List<string>
        {
            "requestCancellation",
            "semanticEvents",
            "threadSubscriptions",
        };
        if (SupportsCapabilityWire(wireVersion) && _capabilities is not null)
        {
            serverCapabilities.Add("capabilityCatalog");
            serverCapabilities.Add("capabilityNotifications");
            if (dynamicToolExecution)
            {
                serverCapabilities.Add("serverRequests");
                serverCapabilities.Add("dynamicToolExecution");
            }
        }

        if (VersionRank(wireVersion) >= VersionRank(OpenCoWorkWire.CoWorkVersion) &&
            _coWork is not null)
        {
            serverCapabilities.Add("coWorkManagement");
            serverCapabilities.Add("coWorkNotifications");
        }

        if (wireVersion == OpenCoWorkWire.AutomationVersion)
        {
            serverCapabilities.Add("automationManagement");
            serverCapabilities.Add("automationNotifications");
        }

        return new WireInitializeResponse(
            new WireServerInfo("OpenCoWork", wireVersion),
            wireVersion,
            new WireWorkspaceInfo(_workspacePath),
            [.. serverCapabilities],
            new WireProtocolLimits(
                OpenCoWorkWire.MaximumMessageBytes,
                OpenCoWorkWire.MaximumInputBytes,
                OpenCoWorkWire.OutboundQueueCapacity),
            _transport,
            correlationId);
    }

    private void EnsureMethodAvailable(string method)
    {
        if (ClientMethodVersions.TryGetValue(method, out var since) &&
            VersionRank(_wireVersion) < VersionRank(since))
        {
            throw new WireMethodNotFoundException();
        }
    }

    private static bool SupportsCapabilityWire(string version) =>
        VersionRank(version) >= VersionRank(OpenCoWorkWire.CapabilityVersion);

    private static int VersionRank(string version) => version switch
    {
        OpenCoWorkWire.Version => 0,
        OpenCoWorkWire.CapabilityVersion => 1,
        OpenCoWorkWire.CoWorkVersion => 2,
        OpenCoWorkWire.AutomationVersion => 3,
        _ => -1,
    };

    private async Task<object> DispatchAsync(
        string method,
        JsonElement parameters,
        CancellationToken cancellationToken) =>
        method switch
        {
            "thread/create" => await CreateThreadAsync(
                Deserialize<WireCreateThreadRequest>(parameters),
                cancellationToken),
            "thread/get" => await GetThreadAsync(
                Deserialize<WireThreadRequest>(parameters),
                cancellationToken),
            "thread/list" => await ListThreadsAsync(
                Deserialize<WireListThreadsRequest>(parameters),
                cancellationToken),
            "thread/history/read" => await ReadHistoryAsync(
                Deserialize<WireReadHistoryRequest>(parameters),
                cancellationToken),
            "thread/rename" => await RenameThreadAsync(
                Deserialize<WireRenameThreadRequest>(parameters),
                cancellationToken),
            "thread/model/set" => await SetThreadModelAsync(
                Deserialize<WireSetModelRequest>(parameters),
                cancellationToken),
            "thread/mode/set" => await SetThreadModeAsync(
                Deserialize<WireSetModeRequest>(parameters),
                cancellationToken),
            "thread/pause" => await PauseThreadAsync(
                Deserialize<WireThreadMutationRequest>(parameters),
                cancellationToken),
            "thread/resume" => await ResumeThreadAsync(
                Deserialize<WireThreadMutationRequest>(parameters),
                cancellationToken),
            "thread/archive" => await ArchiveThreadAsync(
                Deserialize<WireThreadMutationRequest>(parameters),
                cancellationToken),
            "thread/unarchive" => await UnarchiveThreadAsync(
                Deserialize<WireThreadMutationRequest>(parameters),
                cancellationToken),
            "thread/delete/prepare" => await PrepareDeleteAsync(
                Deserialize<WirePrepareDeleteRequest>(parameters),
                cancellationToken),
            "thread/delete" => await DeleteThreadAsync(
                Deserialize<WireDeleteThreadRequest>(parameters),
                cancellationToken),
            "thread/fork" => await ForkThreadAsync(
                Deserialize<WireForkThreadRequest>(parameters),
                cancellationToken),
            "thread/rollback" => await RollbackThreadAsync(
                Deserialize<WireRollbackThreadRequest>(parameters),
                cancellationToken),
            "thread/subscribe" => await SubscribeThreadAsync(
                Deserialize<WireSubscribeThreadRequest>(parameters),
                cancellationToken),
            "thread/unsubscribe" => await UnsubscribeThreadAsync(
                Deserialize<WireUnsubscribeThreadRequest>(parameters)),
            "turn/start" => await StartTurnAsync(
                Deserialize<WireStartTurnRequest>(parameters),
                cancellationToken),
            "turn/enqueue" => await EnqueueTurnAsync(
                Deserialize<WireEnqueueTurnRequest>(parameters),
                cancellationToken),
            "turn/queue/remove" => await RemoveQueuedInputAsync(
                Deserialize<WireRemoveQueuedInputRequest>(parameters),
                cancellationToken),
            "turn/queue/reorder" => await ReorderQueuedInputsAsync(
                Deserialize<WireReorderQueuedInputsRequest>(parameters),
                cancellationToken),
            "turn/steer" => await SteerTurnAsync(
                Deserialize<WireSteerTurnRequest>(parameters),
                cancellationToken),
            "turn/cancel" => await CancelTurnAsync(
                Deserialize<WireCancelTurnRequest>(parameters),
                cancellationToken),
            "item/approval/resolve" => await ResolveApprovalAsync(
                Deserialize<WireResolveApprovalRequest>(parameters),
                cancellationToken),
            "item/input/resolve" => await ResolveInputAsync(
                Deserialize<WireResolveInputRequest>(parameters),
                cancellationToken),
            "capability/catalog" => await GetCapabilityCatalogAsync(
                Deserialize<WireCapabilityCatalogRequest>(parameters),
                cancellationToken),
            "capability/read" => await ReadCapabilityAsync(
                Deserialize<WireCapabilityReadRequest>(parameters),
                cancellationToken),
            "capability/refresh" => await RefreshCapabilitiesAsync(
                Deserialize<WireCapabilityRefreshRequest>(parameters),
                cancellationToken),
            "capability/setEnabled" => await SetCapabilityEnabledAsync(
                Deserialize<WireCapabilitySetEnabledRequest>(parameters),
                cancellationToken),
            "plugin/install" => await ExecuteCapabilityOperationAsync(
                "plugin/install",
                Deserialize<WireCapabilityOperationRequest>(parameters),
                cancellationToken),
            "plugin/remove" => await ExecuteCapabilityOperationAsync(
                "plugin/remove",
                Deserialize<WireCapabilityOperationRequest>(parameters),
                cancellationToken),
            "plugin/setEnabled" => await ExecuteCapabilityOperationAsync(
                "plugin/setEnabled",
                Deserialize<WireCapabilityOperationRequest>(parameters),
                cancellationToken),
            "skill/read" => await ExecuteCapabilityOperationAsync(
                "skill/read",
                Deserialize<WireCapabilityOperationRequest>(parameters),
                cancellationToken),
            "skill/selectVariant" => await ExecuteCapabilityOperationAsync(
                "skill/selectVariant",
                Deserialize<WireCapabilityOperationRequest>(parameters),
                cancellationToken),
            "trust/decide" => await ExecuteCapabilityOperationAsync(
                "trust/decide",
                Deserialize<WireCapabilityOperationRequest>(parameters),
                cancellationToken),
            "trust/revoke" => await ExecuteCapabilityOperationAsync(
                "trust/revoke",
                Deserialize<WireCapabilityOperationRequest>(parameters),
                cancellationToken),
            "mcp/resource/list" => await ExecuteCapabilityOperationAsync(
                "mcp/resource/list",
                Deserialize<WireCapabilityOperationRequest>(parameters),
                cancellationToken),
            "mcp/resource/read" => await ExecuteCapabilityOperationAsync(
                "mcp/resource/read",
                Deserialize<WireCapabilityOperationRequest>(parameters),
                cancellationToken),
            "mcp/restart" => await ExecuteCapabilityOperationAsync(
                "mcp/restart",
                Deserialize<WireCapabilityOperationRequest>(parameters),
                cancellationToken),
            "lsp/request" => await ExecuteCapabilityOperationAsync(
                "lsp/request",
                Deserialize<WireCapabilityOperationRequest>(parameters),
                cancellationToken),
            "lsp/restart" => await ExecuteCapabilityOperationAsync(
                "lsp/restart",
                Deserialize<WireCapabilityOperationRequest>(parameters),
                cancellationToken),
            "auth/secret/set" => await ExecuteCapabilityOperationAsync(
                "auth/secret/set",
                Deserialize<WireCapabilityOperationRequest>(parameters),
                cancellationToken),
            "auth/secret/clear" => await ExecuteCapabilityOperationAsync(
                "auth/secret/clear",
                Deserialize<WireCapabilityOperationRequest>(parameters),
                cancellationToken),
            "sourceControl/inspect" => await ExecuteCapabilityOperationAsync(
                "sourceControl/inspect",
                Deserialize<WireCapabilityOperationRequest>(parameters),
                cancellationToken),
            "sourceControl/status" => await ExecuteCapabilityOperationAsync(
                "sourceControl/status",
                Deserialize<WireCapabilityOperationRequest>(parameters),
                cancellationToken),
            "sourceControl/diff" => await ExecuteCapabilityOperationAsync(
                "sourceControl/diff",
                Deserialize<WireCapabilityOperationRequest>(parameters),
                cancellationToken),
            "sourceControl/log" => await ExecuteCapabilityOperationAsync(
                "sourceControl/log",
                Deserialize<WireCapabilityOperationRequest>(parameters),
                cancellationToken),
            "sourceControl/show" => await ExecuteCapabilityOperationAsync(
                "sourceControl/show",
                Deserialize<WireCapabilityOperationRequest>(parameters),
                cancellationToken),
            "terminal/start" => await ExecuteThreadCapabilityOperationAsync(
                "terminal/start",
                Deserialize<WireThreadCapabilityOperationRequest>(parameters),
                cancellationToken),
            "terminal/list" => await ExecuteThreadCapabilityOperationAsync(
                "terminal/list",
                Deserialize<WireThreadCapabilityOperationRequest>(parameters),
                cancellationToken),
            "terminal/read" => await ExecuteThreadCapabilityOperationAsync(
                "terminal/read",
                Deserialize<WireThreadCapabilityOperationRequest>(parameters),
                cancellationToken),
            "terminal/write" => await ExecuteThreadCapabilityOperationAsync(
                "terminal/write",
                Deserialize<WireThreadCapabilityOperationRequest>(parameters),
                cancellationToken),
            "terminal/stop" => await ExecuteThreadCapabilityOperationAsync(
                "terminal/stop",
                Deserialize<WireThreadCapabilityOperationRequest>(parameters),
                cancellationToken),
            "terminal/release" => await ExecuteThreadCapabilityOperationAsync(
                "terminal/release",
                Deserialize<WireThreadCapabilityOperationRequest>(parameters),
                cancellationToken),
            "memory/list" => await ExecuteCapabilityOperationAsync(
                "memory/list",
                Deserialize<WireCapabilityOperationRequest>(parameters),
                cancellationToken),
            "memory/search" => await ExecuteCapabilityOperationAsync(
                "memory/search",
                Deserialize<WireCapabilityOperationRequest>(parameters),
                cancellationToken),
            "memory/read" => await ExecuteCapabilityOperationAsync(
                "memory/read",
                Deserialize<WireCapabilityOperationRequest>(parameters),
                cancellationToken),
            "memory/write" => await ExecuteCapabilityOperationAsync(
                "memory/write",
                Deserialize<WireCapabilityOperationRequest>(parameters),
                cancellationToken),
            "memory/archive" => await ExecuteCapabilityOperationAsync(
                "memory/archive",
                Deserialize<WireCapabilityOperationRequest>(parameters),
                cancellationToken),
            "tool/dynamic/register" => await RegisterDynamicToolAsync(
                Deserialize<WireDynamicToolRegisterRequest>(parameters),
                cancellationToken),
            "tool/dynamic/renew" => await RenewDynamicToolAsync(
                Deserialize<WireDynamicToolRenewRequest>(parameters),
                cancellationToken),
            "tool/dynamic/unregister" => await UnregisterDynamicToolAsync(
                Deserialize<WireDynamicToolUnregisterRequest>(parameters),
                cancellationToken),
            "agent/profile/list" => await ListAgentProfilesAsync(
                Deserialize<WireListAgentProfilesRequest>(parameters),
                cancellationToken),
            "agent/profile/get" => await GetAgentProfileAsync(
                Deserialize<WireGetAgentProfileRequest>(parameters),
                cancellationToken),
            "agent/profile/upsert" => await UpsertAgentProfileAsync(
                Deserialize<WireUpsertAgentProfileRequest>(parameters),
                cancellationToken),
            "agent/profile/setEnabled" => await SetAgentProfileEnabledAsync(
                Deserialize<WireSetAgentProfileEnabledRequest>(parameters),
                cancellationToken),
            "team/list" => await ListTeamsAsync(
                Deserialize<WireListTeamsRequest>(parameters),
                cancellationToken),
            "team/get" => await GetTeamAsync(
                Deserialize<WireGetTeamRequest>(parameters),
                cancellationToken),
            "team/upsert" => await UpsertTeamAsync(
                Deserialize<WireUpsertTeamRequest>(parameters),
                cancellationToken),
            "team/setEnabled" => await SetTeamEnabledAsync(
                Deserialize<WireSetTeamEnabledRequest>(parameters),
                cancellationToken),
            "subagent/spawn" => await SpawnSubAgentAsync(
                Deserialize<WireSpawnSubAgentRequest>(parameters),
                cancellationToken),
            "subagent/children" => await ListSubAgentChildrenAsync(
                Deserialize<WireSubAgentQueryRequest>(parameters),
                cancellationToken),
            "subagent/list" => await ListSubAgentsAsync(
                Deserialize<WireSubAgentQueryRequest>(parameters),
                cancellationToken),
            "subagent/send" => await SendSubAgentMessageAsync(
                Deserialize<WireSendSubAgentMessageRequest>(parameters),
                cancellationToken),
            "subagent/followup" => await FollowUpSubAgentAsync(
                Deserialize<WireFollowUpSubAgentRequest>(parameters),
                cancellationToken),
            "subagent/cancel" => await CancelSubAgentAsync(
                Deserialize<WireCancelSubAgentRequest>(parameters),
                cancellationToken),
            "mission/create" => await CreateMissionAsync(
                Deserialize<WireCreateMissionRequest>(parameters),
                cancellationToken),
            "mission/list" => await ListMissionsAsync(
                Deserialize<WireListMissionsRequest>(parameters),
                cancellationToken),
            "mission/get" => await GetMissionAsync(
                Deserialize<WireGetMissionRequest>(parameters),
                cancellationToken),
            "mission/activate" => await ActivateMissionAsync(
                Deserialize<WireMissionCommandRequest>(parameters),
                cancellationToken),
            "mission/cancel" => await CancelMissionAsync(
                Deserialize<WireMissionCommandRequest>(parameters),
                cancellationToken),
            "mission/task/add" => await AddMissionTaskAsync(
                Deserialize<WireAddMissionTaskRequest>(parameters),
                cancellationToken),
            "mission/task/update" => await UpdateMissionTaskAsync(
                Deserialize<WireUpdateMissionTaskRequest>(parameters),
                cancellationToken),
            "mission/task/remove" => await RemoveMissionTaskAsync(
                Deserialize<WireMissionTaskCommandRequest>(parameters),
                cancellationToken),
            "mission/task/block" => await BlockMissionTaskAsync(
                Deserialize<WireBlockMissionTaskRequest>(parameters),
                cancellationToken),
            "mission/task/unblock" => await UnblockMissionTaskAsync(
                Deserialize<WireMissionTaskCommandRequest>(parameters),
                cancellationToken),
            "mission/task/retry" => await RetryMissionTaskAsync(
                Deserialize<WireMissionTaskCommandRequest>(parameters),
                cancellationToken),
            "mission/task/reassign" => await ReassignMissionTaskAsync(
                Deserialize<WireReassignMissionTaskRequest>(parameters),
                cancellationToken),
            "mission/task/waive" => await WaiveMissionTaskAsync(
                Deserialize<WireMissionTaskCommandRequest>(parameters),
                cancellationToken),
            "mission/task/review" => await ReviewMissionTaskAsync(
                Deserialize<WireReviewMissionTaskRequest>(parameters),
                cancellationToken),
            "mailbox/list" => await ListMailboxMessagesAsync(
                Deserialize<WireListMailboxMessagesRequest>(parameters),
                cancellationToken),
            "mailbox/send" => await SendMailboxMessageAsync(
                Deserialize<WireSendMailboxMessageRequest>(parameters),
                cancellationToken),
            "mailbox/acknowledge" => await AcknowledgeMailboxMessageAsync(
                Deserialize<WireMailboxMessageCommandRequest>(parameters),
                cancellationToken),
            "mailbox/retry" => await RetryMailboxMessageAsync(
                Deserialize<WireMailboxMessageCommandRequest>(parameters),
                cancellationToken),
            "artifact/list" => await ListArtifactsAsync(
                Deserialize<WireListArtifactsRequest>(parameters),
                cancellationToken),
            "artifact/get" => await GetArtifactAsync(
                Deserialize<WireGetArtifactRequest>(parameters),
                cancellationToken),
            "artifact/publish" => await PublishArtifactAsync(
                Deserialize<WirePublishArtifactRequest>(parameters),
                cancellationToken),
            "artifact/promote" => await PromoteArtifactAsync(
                Deserialize<WirePromoteArtifactRequest>(parameters),
                cancellationToken),
            "worktree/list" => await ListWorktreesAsync(
                Deserialize<WireListWorktreesRequest>(parameters),
                cancellationToken),
            "worktree/get" => await GetWorktreeAsync(
                Deserialize<WireGetWorktreeRequest>(parameters),
                cancellationToken),
            "worktree/handoff" => await HandoffWorktreeAsync(
                Deserialize<WireWorktreeCommandRequest>(parameters),
                cancellationToken),
            "worktree/remove" => await RemoveWorktreeAsync(
                Deserialize<WireWorktreeCommandRequest>(parameters),
                cancellationToken),
            "automation/list" => await ListAutomationsAsync(
                Deserialize<WireListAutomationDefinitionsRequest>(parameters),
                cancellationToken),
            "automation/get" => await GetAutomationAsync(
                Deserialize<WireGetAutomationDefinitionRequest>(parameters),
                cancellationToken),
            "schedule/list" => await ListAutomationSchedulesAsync(
                Deserialize<WireListAutomationSchedulesRequest>(parameters),
                cancellationToken),
            "schedule/get" => await GetAutomationScheduleAsync(
                Deserialize<WireGetAutomationScheduleRequest>(parameters),
                cancellationToken),
            "automationRun/start" => await StartAutomationRunAsync(
                Deserialize<WireStartAutomationRunRequest>(parameters),
                cancellationToken),
            "automationRun/list" => await ListAutomationRunsAsync(
                Deserialize<WireListAutomationRunsRequest>(parameters),
                cancellationToken),
            "automationRun/get" => await GetAutomationRunAsync(
                Deserialize<WireGetAutomationRunRequest>(parameters),
                cancellationToken),
            "automationRun/cancel" => await CancelAutomationRunAsync(
                Deserialize<WireCancelAutomationRunRequest>(parameters),
                cancellationToken),
            "automationRun/resolveAttention" =>
                await ResolveAutomationAttentionAsync(
                    Deserialize<WireResolveAutomationAttentionRequest>(parameters),
                    cancellationToken),
            _ => throw new WireMethodNotFoundException(),
        };

    [OpenCoWorkWireMethod(
        "capability/catalog",
        OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.CapabilityOwner,
        OpenCoWorkWire.CapabilityVersion,
        typeof(WireCapabilityCatalogRequest),
        typeof(WireCapabilityCatalogResponse),
        OpenCoWorkWire.WorkspaceAuthority,
        false,
        OpenCoWorkWire.NoIdempotency)]
    public async Task<WireCapabilityCatalogResponse> GetCapabilityCatalogAsync(
        WireCapabilityCatalogRequest request,
        CancellationToken cancellationToken)
    {
        RequireWire11();
        var page = await _capabilities!.GetCatalogAsync(
            new CapabilityCatalogQuery(request.Limit, request.Cursor),
            cancellationToken);
        return new WireCapabilityCatalogResponse(
            page.SchemaVersion,
            page.Revision,
            page.CatalogSha256,
            WireName(page.RuntimeState),
            page.Items.Select(MapCapability).ToArray(),
            page.NextCursor);
    }

    [OpenCoWorkWireMethod(
        "capability/read",
        OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.CapabilityOwner,
        OpenCoWorkWire.CapabilityVersion,
        typeof(WireCapabilityReadRequest),
        typeof(WireCapabilityReadResponse),
        OpenCoWorkWire.WorkspaceAuthority,
        false,
        OpenCoWorkWire.NoIdempotency)]
    public async Task<WireCapabilityReadResponse> ReadCapabilityAsync(
        WireCapabilityReadRequest request,
        CancellationToken cancellationToken)
    {
        RequireWire11();
        var entry = await _capabilities!.ReadAsync(
            new CapabilityIdentity(
                ParseCapabilityKind(request.Kind),
                request.Id),
            cancellationToken);
        return new WireCapabilityReadResponse(
            entry.Revision,
            MapCapability(entry.Item));
    }

    [OpenCoWorkWireMethod(
        "capability/refresh",
        OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.CapabilityOwner,
        OpenCoWorkWire.CapabilityVersion,
        typeof(WireCapabilityRefreshRequest),
        typeof(WireCapabilityMutationResponse),
        OpenCoWorkWire.WorkspaceAuthority,
        true,
        OpenCoWorkWire.NoIdempotency)]
    public async Task<WireCapabilityMutationResponse> RefreshCapabilitiesAsync(
        WireCapabilityRefreshRequest request,
        CancellationToken cancellationToken)
    {
        RequireWire11();
        return MapCapabilityChange(await _capabilities!.RefreshAsync(
            request.ExpectedRevision,
            cancellationToken));
    }

    [OpenCoWorkWireMethod(
        "capability/setEnabled",
        OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.CapabilityOwner,
        OpenCoWorkWire.CapabilityVersion,
        typeof(WireCapabilitySetEnabledRequest),
        typeof(WireCapabilityMutationResponse),
        OpenCoWorkWire.WorkspaceAuthority,
        true,
        OpenCoWorkWire.NoIdempotency)]
    public async Task<WireCapabilityMutationResponse> SetCapabilityEnabledAsync(
        WireCapabilitySetEnabledRequest request,
        CancellationToken cancellationToken)
    {
        RequireWire11();
        return MapCapabilityChange(await _capabilities!.SetEnabledAsync(
            new CapabilitySetEnabledRequest(
                ParseCapabilityKind(request.Kind),
                request.Id,
                request.Enabled,
                request.ExpectedRevision),
            cancellationToken));
    }

    private async Task<WireCapabilityOperationResponse>
        ExecuteCapabilityOperationAsync(
            string operation,
            WireCapabilityOperationRequest request,
            CancellationToken cancellationToken)
    {
        RequireWire11();
        var result = await _capabilities!.ExecuteDomainAsync(
            new CapabilityDomainRequest(
                operation,
                RequireObject(request.Arguments),
                _connectionId),
            cancellationToken);
        return new WireCapabilityOperationResponse(
            result.Result,
            result.Revision);
    }

    private Task<WireCapabilityOperationResponse>
        ExecuteThreadCapabilityOperationAsync(
            string operation,
            WireThreadCapabilityOperationRequest request,
            CancellationToken cancellationToken) =>
        ExecuteCapabilityOperationAsync(
            operation,
            new WireCapabilityOperationRequest(
                JsonSerializer.SerializeToElement(new
                {
                    request.ThreadId,
                    arguments = RequireObject(request.Arguments),
                }, JsonOptions)),
            cancellationToken);

    private async Task<WireDynamicToolRegistrationResponse>
        RegisterDynamicToolAsync(
            WireDynamicToolRegisterRequest request,
            CancellationToken cancellationToken)
    {
        RequireDynamicToolExecution();
        var registration = await _capabilities!.RegisterDynamicToolAsync(
            _connectionId,
            new CapabilityDynamicToolRegistrationRequest(
                request.ThreadId,
                request.RegistrationId,
                new CapabilityDynamicToolDefinition(
                    request.Definition.Name,
                    request.Definition.Description,
                    RequireObject(request.Definition.InputSchema),
                    ParseToolEffects(request.Definition.Effects),
                    ParseWireEnum<ToolReplaySafety>(
                        request.Definition.ReplaySafety)),
                request.DefinitionSha256,
                request.LeaseSeconds is { } seconds
                    ? TimeSpan.FromSeconds(seconds)
                    : null),
            (arguments, token) => InvokeDynamicToolAsync(
                request.ThreadId,
                request.RegistrationId,
                arguments,
                token),
            cancellationToken);
        return MapDynamicTool(registration);
    }

    private async Task<WireDynamicToolRegistrationResponse> RenewDynamicToolAsync(
        WireDynamicToolRenewRequest request,
        CancellationToken cancellationToken)
    {
        RequireDynamicToolExecution();
        return MapDynamicTool(await _capabilities!.RenewDynamicToolAsync(
            _connectionId,
            request.ThreadId,
            request.RegistrationId,
            TimeSpan.FromSeconds(request.LeaseSeconds),
            cancellationToken));
    }

    private async Task<WireAcknowledgement> UnregisterDynamicToolAsync(
        WireDynamicToolUnregisterRequest request,
        CancellationToken cancellationToken)
    {
        RequireDynamicToolExecution();
        await _capabilities!.UnregisterDynamicToolAsync(
            _connectionId,
            request.ThreadId,
            request.RegistrationId,
            cancellationToken);
        return new WireAcknowledgement();
    }

    private async ValueTask<ToolBindingResult> InvokeDynamicToolAsync(
        Guid threadId,
        Guid registrationId,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var id = $"tool-{Interlocked.Increment(ref _nextClientRequestId)}";
        var pending = new PendingClientRequest();
        if (!_clientRequests.TryAdd(id, pending))
        {
            return DynamicFailure(
                DynamicToolErrorCodes.Disconnected,
                "Dynamic Tool request could not be created.");
        }

        try
        {
            var request = new JsonRpcClientRequest(
                "2.0",
                id,
                "tool/invoke",
                new WireToolInvokeRequest(
                    threadId,
                    registrationId,
                    arguments.Clone()));
            await _send(
                JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions),
                cancellationToken);
            using var registration = cancellationToken.Register(
                static state =>
                {
                    var (completion, token) =
                        ((TaskCompletionSource<ToolBindingResult>, CancellationToken))
                        state!;
                    completion.TrySetCanceled(token);
                },
                (pending.Completion, cancellationToken));
            return await pending.Completion.Task;
        }
        catch (OperationCanceledException)
        {
            await SendClientCancellationAsync(id);
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or ObjectDisposedException)
        {
            return DynamicFailure(
                DynamicToolErrorCodes.Disconnected,
                "Dynamic Tool client disconnected.");
        }
        finally
        {
            _clientRequests.TryRemove(id, out _);
        }
    }

    [OpenCoWorkWireMethod(
        "thread/create",
        OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.SessionOwner,
        OpenCoWorkWire.Version,
        typeof(WireCreateThreadRequest),
        typeof(WireThreadResponse),
        OpenCoWorkWire.ConnectionAuthority,
        true,
        OpenCoWorkWire.RequiredIdempotency)]
    public async Task<WireThreadResponse> CreateThreadAsync(
        WireCreateThreadRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _sessions.CreateThreadAsync(
            new CreateThreadRequest(
                request.IdempotencyKey,
                request.ExpectedSequence,
                request.DisplayName,
                HistoryMode.Server,
                request.ProviderId,
                request.ModelId,
                ParseMode(request.Mode)),
            cancellationToken);
        return ThreadResponse(result);
    }

    [OpenCoWorkWireMethod(
        "thread/get",
        OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.SessionOwner,
        OpenCoWorkWire.Version,
        typeof(WireThreadRequest),
        typeof(WireThreadResponse),
        OpenCoWorkWire.ThreadAuthority,
        false,
        OpenCoWorkWire.NoIdempotency)]
    public async Task<WireThreadResponse> GetThreadAsync(
        WireThreadRequest request,
        CancellationToken cancellationToken)
    {
        var thread = Require(await _sessions.GetThreadAsync(
            request.ThreadId,
            cancellationToken));
        return new WireThreadResponse(
            MapThread(thread),
            thread.CurrentSequence,
            thread.CurrentSequence);
    }

    [OpenCoWorkWireMethod(
        "thread/list",
        OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.SessionOwner,
        OpenCoWorkWire.Version,
        typeof(WireListThreadsRequest),
        typeof(WireThreadPageResponse),
        OpenCoWorkWire.ConnectionAuthority,
        false,
        OpenCoWorkWire.NoIdempotency)]
    public async Task<WireThreadPageResponse> ListThreadsAsync(
        WireListThreadsRequest request,
        CancellationToken cancellationToken)
    {
        var page = Require(await _sessions.ListThreadsAsync(
            new ListThreadsRequest(
                request.Cursor,
                request.PageSize,
                request.IncludeArchived),
            cancellationToken));
        return new WireThreadPageResponse(
            page.Items.Select(MapThread).ToArray(),
            page.NextCursor);
    }

    [OpenCoWorkWireMethod(
        "thread/history/read",
        OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.SessionOwner,
        OpenCoWorkWire.Version,
        typeof(WireReadHistoryRequest),
        typeof(WireHistoryPageResponse),
        OpenCoWorkWire.ThreadAuthority,
        false,
        OpenCoWorkWire.NoIdempotency)]
    public async Task<WireHistoryPageResponse> ReadHistoryAsync(
        WireReadHistoryRequest request,
        CancellationToken cancellationToken)
    {
        var afterSequence = DecodeHistoryCursor(request.Cursor);
        var projector = new WireEventProjector();
        await projector.PrimeAsync(
            _sessions,
            request.ThreadId,
            afterSequence,
            cancellationToken);
        var page = Require(await _sessions.ReadHistoryAsync(
            new ReadHistoryRequest(
                request.ThreadId,
                afterSequence,
                request.PageSize),
            cancellationToken));
        var events = page.Items
            .Select(projector.Project)
            .Select(projected => new WireHistoryEvent(
                projected.Method,
                projected.Envelope))
            .ToArray();
        return new WireHistoryPageResponse(
            events,
            page.NextCursor is null || events.Length == 0
                ? null
                : EncodeHistoryCursor(events[^1].Event.Sequence));
    }

    [OpenCoWorkWireMethod(
        "thread/rename",
        OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.SessionOwner,
        OpenCoWorkWire.Version,
        typeof(WireRenameThreadRequest),
        typeof(WireThreadResponse),
        OpenCoWorkWire.ThreadAuthority,
        true,
        OpenCoWorkWire.RequiredIdempotency)]
    public async Task<WireThreadResponse> RenameThreadAsync(
        WireRenameThreadRequest request,
        CancellationToken cancellationToken) =>
        ThreadResponse(await _sessions.RenameThreadAsync(
            new RenameThreadRequest(
                request.ThreadId,
                request.IdempotencyKey,
                request.ExpectedSequence,
                request.DisplayName),
            cancellationToken));

    [OpenCoWorkWireMethod(
        "thread/model/set",
        OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.SessionOwner,
        OpenCoWorkWire.Version,
        typeof(WireSetModelRequest),
        typeof(WireThreadResponse),
        OpenCoWorkWire.ThreadAuthority,
        true,
        OpenCoWorkWire.RequiredIdempotency)]
    public async Task<WireThreadResponse> SetThreadModelAsync(
        WireSetModelRequest request,
        CancellationToken cancellationToken) =>
        ThreadResponse(await _sessions.SetThreadModelAsync(
            new SetThreadModelRequest(
                request.ThreadId,
                request.IdempotencyKey,
                request.ExpectedSequence,
                request.ProviderId,
                request.ModelId),
            cancellationToken));

    [OpenCoWorkWireMethod(
        "thread/mode/set",
        OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.SessionOwner,
        OpenCoWorkWire.Version,
        typeof(WireSetModeRequest),
        typeof(WireThreadResponse),
        OpenCoWorkWire.ThreadAuthority,
        true,
        OpenCoWorkWire.RequiredIdempotency)]
    public async Task<WireThreadResponse> SetThreadModeAsync(
        WireSetModeRequest request,
        CancellationToken cancellationToken) =>
        ThreadResponse(await _sessions.SetAgentModeAsync(
            new SetAgentModeRequest(
                request.ThreadId,
                request.IdempotencyKey,
                request.ExpectedSequence,
                ParseMode(request.Mode)),
            cancellationToken));

    [OpenCoWorkWireMethod(
        "thread/pause",
        OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.SessionOwner,
        OpenCoWorkWire.Version,
        typeof(WireThreadMutationRequest),
        typeof(WireThreadResponse),
        OpenCoWorkWire.ThreadAuthority,
        true,
        OpenCoWorkWire.RequiredIdempotency)]
    public Task<WireThreadResponse> PauseThreadAsync(
        WireThreadMutationRequest request,
        CancellationToken cancellationToken) =>
        MutateThreadAsync(request, _sessions.PauseThreadAsync, cancellationToken);

    [OpenCoWorkWireMethod(
        "thread/resume",
        OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.SessionOwner,
        OpenCoWorkWire.Version,
        typeof(WireThreadMutationRequest),
        typeof(WireThreadResponse),
        OpenCoWorkWire.ThreadAuthority,
        true,
        OpenCoWorkWire.RequiredIdempotency)]
    public Task<WireThreadResponse> ResumeThreadAsync(
        WireThreadMutationRequest request,
        CancellationToken cancellationToken) =>
        MutateThreadAsync(request, _sessions.ResumeThreadAsync, cancellationToken);

    [OpenCoWorkWireMethod(
        "thread/archive",
        OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.SessionOwner,
        OpenCoWorkWire.Version,
        typeof(WireThreadMutationRequest),
        typeof(WireThreadResponse),
        OpenCoWorkWire.ThreadAuthority,
        true,
        OpenCoWorkWire.RequiredIdempotency)]
    public Task<WireThreadResponse> ArchiveThreadAsync(
        WireThreadMutationRequest request,
        CancellationToken cancellationToken) =>
        MutateThreadAsync(request, _sessions.ArchiveThreadAsync, cancellationToken);

    [OpenCoWorkWireMethod(
        "thread/unarchive",
        OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.SessionOwner,
        OpenCoWorkWire.Version,
        typeof(WireThreadMutationRequest),
        typeof(WireThreadResponse),
        OpenCoWorkWire.ThreadAuthority,
        true,
        OpenCoWorkWire.RequiredIdempotency)]
    public Task<WireThreadResponse> UnarchiveThreadAsync(
        WireThreadMutationRequest request,
        CancellationToken cancellationToken) =>
        MutateThreadAsync(request, _sessions.UnarchiveThreadAsync, cancellationToken);

    [OpenCoWorkWireMethod(
        "thread/delete/prepare",
        OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.SessionOwner,
        OpenCoWorkWire.Version,
        typeof(WirePrepareDeleteRequest),
        typeof(WireDeletePreparationResponse),
        OpenCoWorkWire.ThreadAuthority,
        false,
        OpenCoWorkWire.NoIdempotency)]
    public async Task<WireDeletePreparationResponse> PrepareDeleteAsync(
        WirePrepareDeleteRequest request,
        CancellationToken cancellationToken)
    {
        var prepared = Require(await _sessions.PrepareDeleteAsync(
            new PrepareDeleteRequest(request.ThreadId, request.ExpectedSequence),
            cancellationToken));
        return new WireDeletePreparationResponse(
            prepared.ThreadId,
            prepared.Sequence,
            prepared.Token,
            prepared.ExpiresAt);
    }

    [OpenCoWorkWireMethod(
        "thread/delete",
        OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.SessionOwner,
        OpenCoWorkWire.Version,
        typeof(WireDeleteThreadRequest),
        typeof(WireDeleteThreadResponse),
        OpenCoWorkWire.ThreadAuthority,
        true,
        OpenCoWorkWire.RequiredIdempotency)]
    public async Task<WireDeleteThreadResponse> DeleteThreadAsync(
        WireDeleteThreadRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _sessions.DeleteThreadAsync(
            new DeleteThreadRequest(
                request.ThreadId,
                request.IdempotencyKey,
                request.ExpectedSequence,
                request.Token),
            cancellationToken);
        var deleted = Require(result);
        var sequence = RequireSequence(result.Sequence);
        return new WireDeleteThreadResponse(
            deleted,
            sequence,
            result.CurrentSequence ?? sequence);
    }

    [OpenCoWorkWireMethod(
        "thread/fork",
        OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.SessionOwner,
        OpenCoWorkWire.Version,
        typeof(WireForkThreadRequest),
        typeof(WireThreadResponse),
        OpenCoWorkWire.ThreadAuthority,
        true,
        OpenCoWorkWire.RequiredIdempotency)]
    public async Task<WireThreadResponse> ForkThreadAsync(
        WireForkThreadRequest request,
        CancellationToken cancellationToken) =>
        ThreadResponse(await _sessions.ForkThreadAsync(
            new ForkThreadRequest(
                request.SourceThreadId,
                request.SourceSequence,
                request.ExpectedSequence,
                request.IdempotencyKey,
                request.DisplayName),
            cancellationToken));

    [OpenCoWorkWireMethod(
        "thread/rollback",
        OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.SessionOwner,
        OpenCoWorkWire.Version,
        typeof(WireRollbackThreadRequest),
        typeof(WireRollbackThreadResponse),
        OpenCoWorkWire.ThreadAuthority,
        true,
        OpenCoWorkWire.RequiredIdempotency)]
    public async Task<WireRollbackThreadResponse> RollbackThreadAsync(
        WireRollbackThreadRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _sessions.RollbackThreadAsync(
            new RollbackThreadRequest(
                request.ThreadId,
                request.TargetSequence,
                request.ExpectedSequence,
                request.IdempotencyKey),
            cancellationToken);
        var rollback = Require(result);
        var sequence = RequireSequence(result.Sequence);
        return new WireRollbackThreadResponse(
            MapThread(rollback.Thread),
            rollback.ExternalSideEffectsReverted,
            sequence,
            rollback.Thread.CurrentSequence);
    }

    [OpenCoWorkWireMethod(
        "thread/subscribe",
        OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.SessionOwner,
        OpenCoWorkWire.Version,
        typeof(WireSubscribeThreadRequest),
        typeof(WireSubscribeThreadResponse),
        OpenCoWorkWire.ThreadAuthority,
        false,
        OpenCoWorkWire.NoIdempotency)]
    public async Task<WireSubscribeThreadResponse> SubscribeThreadAsync(
        WireSubscribeThreadRequest request,
        CancellationToken cancellationToken)
    {
        var mode = ParseSubscriptionMode(request.Mode);
        if (mode == SessionSubscriptionMode.ResumeAfterSequence &&
            request.AfterSequence is null)
        {
            throw new ArgumentException("afterSequence is required.");
        }

        var subscription = await _sessions.SubscribeAsync(
            new SessionSubscriptionRequest(
                request.ThreadId,
                mode,
                request.AfterSequence),
            cancellationToken);
        var projector = new WireEventProjector();
        if (request.AfterSequence is > 0)
        {
            await projector.PrimeAsync(
                _sessions,
                request.ThreadId,
                request.AfterSequence.Value,
                cancellationToken);
        }

        var subscriptionId = Guid.CreateVersion7().ToString(
            "D",
            CultureInfo.InvariantCulture);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token);
        var active = new ActiveSubscription(subscription, cancellation);
        if (!_subscriptions.TryAdd(subscriptionId, active))
        {
            await active.StopAsync();
            throw new InvalidOperationException();
        }

        active.Completion = PumpSubscriptionAsync(
            subscriptionId,
            active,
            projector);
        return new WireSubscribeThreadResponse(
            subscriptionId,
            Wire(subscription.Disposition),
            MapThread(subscription.Snapshot),
            subscription.CurrentSequence);
    }

    [OpenCoWorkWireMethod(
        "thread/unsubscribe",
        OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.SessionOwner,
        OpenCoWorkWire.Version,
        typeof(WireUnsubscribeThreadRequest),
        typeof(WireAcknowledgement),
        OpenCoWorkWire.ConnectionAuthority,
        false,
        OpenCoWorkWire.NoIdempotency)]
    public async Task<WireAcknowledgement> UnsubscribeThreadAsync(
        WireUnsubscribeThreadRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SubscriptionId);
        if (_subscriptions.TryRemove(request.SubscriptionId, out var subscription))
        {
            await subscription.StopAsync();
        }

        return new WireAcknowledgement();
    }

    [OpenCoWorkWireMethod(
        "turn/start",
        OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.SessionOwner,
        OpenCoWorkWire.Version,
        typeof(WireStartTurnRequest),
        typeof(WireAcceptedTurnResponse),
        OpenCoWorkWire.ThreadAuthority,
        true,
        OpenCoWorkWire.RequiredIdempotency)]
    public async Task<WireAcceptedTurnResponse> StartTurnAsync(
        WireStartTurnRequest request,
        CancellationToken cancellationToken)
    {
        ValidateInput(request.Text);
        var result = await _sessions.EnqueueInputAsync(
            new EnqueueInputRequest(
                request.ThreadId,
                request.IdempotencyKey,
                request.ExpectedSequence,
                request.Text,
                TurnAdmission.StartOnly),
            cancellationToken);
        var submitted = Require(result);
        if (submitted.TurnId is null)
        {
            throw new InvalidOperationException();
        }

        return new WireAcceptedTurnResponse(
            request.ThreadId,
            submitted.TurnId.Value,
            RequireSequence(result.Sequence));
    }

    [OpenCoWorkWireMethod(
        "turn/enqueue",
        OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.SessionOwner,
        OpenCoWorkWire.Version,
        typeof(WireEnqueueTurnRequest),
        typeof(WireQueuedTurnResponse),
        OpenCoWorkWire.ThreadAuthority,
        true,
        OpenCoWorkWire.RequiredIdempotency)]
    public async Task<WireQueuedTurnResponse> EnqueueTurnAsync(
        WireEnqueueTurnRequest request,
        CancellationToken cancellationToken)
    {
        ValidateInput(request.Text);
        var result = await _sessions.EnqueueInputAsync(
            new EnqueueInputRequest(
                request.ThreadId,
                request.IdempotencyKey,
                request.ExpectedSequence,
                request.Text),
            cancellationToken);
        var submitted = Require(result);
        var sequence = RequireSequence(result.Sequence);
        var thread = Require(await _sessions.GetThreadAsync(
            request.ThreadId,
            cancellationToken));
        return new WireQueuedTurnResponse(
            MapQueueItem(submitted.QueueItem),
            sequence,
            thread.CurrentSequence);
    }

    [OpenCoWorkWireMethod(
        "turn/queue/remove",
        OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.SessionOwner,
        OpenCoWorkWire.Version,
        typeof(WireRemoveQueuedInputRequest),
        typeof(WireThreadResponse),
        OpenCoWorkWire.ThreadAuthority,
        true,
        OpenCoWorkWire.RequiredIdempotency)]
    public async Task<WireThreadResponse> RemoveQueuedInputAsync(
        WireRemoveQueuedInputRequest request,
        CancellationToken cancellationToken) =>
        ThreadResponse(await _sessions.RemoveQueuedInputAsync(
            new RemoveQueuedInputRequest(
                request.ThreadId,
                request.QueueItemId,
                request.IdempotencyKey,
                request.ExpectedSequence),
            cancellationToken));

    [OpenCoWorkWireMethod(
        "turn/queue/reorder",
        OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.SessionOwner,
        OpenCoWorkWire.Version,
        typeof(WireReorderQueuedInputsRequest),
        typeof(WireThreadResponse),
        OpenCoWorkWire.ThreadAuthority,
        true,
        OpenCoWorkWire.RequiredIdempotency)]
    public async Task<WireThreadResponse> ReorderQueuedInputsAsync(
        WireReorderQueuedInputsRequest request,
        CancellationToken cancellationToken) =>
        ThreadResponse(await _sessions.ReorderQueuedInputsAsync(
            new ReorderQueuedInputsRequest(
                request.ThreadId,
                request.QueueItemIds,
                request.IdempotencyKey,
                request.ExpectedSequence),
            cancellationToken));

    [OpenCoWorkWireMethod(
        "turn/steer",
        OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.SessionOwner,
        OpenCoWorkWire.Version,
        typeof(WireSteerTurnRequest),
        typeof(WireThreadResponse),
        OpenCoWorkWire.ThreadAuthority,
        true,
        OpenCoWorkWire.RequiredIdempotency)]
    public async Task<WireThreadResponse> SteerTurnAsync(
        WireSteerTurnRequest request,
        CancellationToken cancellationToken) =>
        ThreadResponse(await _sessions.SteerTurnAsync(
            new SteerTurnRequest(
                request.ThreadId,
                request.ExpectedTurnId,
                request.QueueItemId,
                request.IdempotencyKey,
                request.ExpectedSequence),
            cancellationToken));

    [OpenCoWorkWireMethod(
        "turn/cancel",
        OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.SessionOwner,
        OpenCoWorkWire.Version,
        typeof(WireCancelTurnRequest),
        typeof(WireTurnResponse),
        OpenCoWorkWire.ThreadAuthority,
        true,
        OpenCoWorkWire.RequiredIdempotency)]
    public async Task<WireTurnResponse> CancelTurnAsync(
        WireCancelTurnRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _sessions.CancelTurnAsync(
            new CancelTurnRequest(
                request.ThreadId,
                request.TurnId,
                request.IdempotencyKey,
                request.ExpectedSequence),
            cancellationToken);
        var turn = Require(result);
        var sequence = RequireSequence(result.Sequence);
        return new WireTurnResponse(
            MapTurn(turn),
            sequence,
            result.CurrentSequence ?? sequence);
    }

    [OpenCoWorkWireMethod(
        "item/approval/resolve",
        OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.SessionOwner,
        OpenCoWorkWire.Version,
        typeof(WireResolveApprovalRequest),
        typeof(WireInteractionResponse),
        OpenCoWorkWire.ThreadAuthority,
        true,
        OpenCoWorkWire.RequiredIdempotency)]
    public async Task<WireInteractionResponse> ResolveApprovalAsync(
        WireResolveApprovalRequest request,
        CancellationToken cancellationToken)
    {
        var approved = request.Decision switch
        {
            "approve" => true,
            "deny" => false,
            _ => throw new ArgumentException("Invalid approval decision."),
        };
        return await ResolveInteractionAsync(
            request.ThreadId,
            request.TurnId,
            request.InteractionId,
            new ApprovalResponseContent(approved, Comment: null),
            request.IdempotencyKey,
            request.ExpectedSequence,
            cancellationToken);
    }

    [OpenCoWorkWireMethod(
        "item/input/resolve",
        OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.SessionOwner,
        OpenCoWorkWire.Version,
        typeof(WireResolveInputRequest),
        typeof(WireInteractionResponse),
        OpenCoWorkWire.ThreadAuthority,
        true,
        OpenCoWorkWire.RequiredIdempotency)]
    public async Task<WireInteractionResponse> ResolveInputAsync(
        WireResolveInputRequest request,
        CancellationToken cancellationToken)
    {
        ValidateInput(request.Text);
        return await ResolveInteractionAsync(
            request.ThreadId,
            request.TurnId,
            request.InteractionId,
            new UserInputResponseContent(request.Text),
            request.IdempotencyKey,
            request.ExpectedSequence,
            cancellationToken);
    }

    private async Task<WireInteractionResponse> ResolveInteractionAsync(
        Guid threadId,
        Guid turnId,
        Guid interactionId,
        SessionItemContent response,
        Guid idempotencyKey,
        long expectedSequence,
        CancellationToken cancellationToken)
    {
        var result = await _sessions.ResolveInteractionAsync(
            new ResolveInteractionRequest(
                threadId,
                turnId,
                interactionId,
                response,
                idempotencyKey,
                expectedSequence),
            cancellationToken);
        var interaction = Require(result);
        var sequence = RequireSequence(result.Sequence);
        return new WireInteractionResponse(
            MapInteraction(interaction),
            sequence,
            result.CurrentSequence ?? sequence);
    }

    private async Task<WireThreadResponse> MutateThreadAsync(
        WireThreadMutationRequest request,
        Func<
            ThreadMutationRequest,
            CancellationToken,
            Task<SessionCommandResult<ThreadSnapshot>>> mutation,
        CancellationToken cancellationToken) =>
        ThreadResponse(await mutation(
            new ThreadMutationRequest(
                request.ThreadId,
                request.IdempotencyKey,
                request.ExpectedSequence),
            cancellationToken));

    private async Task PumpSubscriptionAsync(
        string subscriptionId,
        ActiveSubscription active,
        WireEventProjector projector)
    {
        await Task.Yield();
        try
        {
            await foreach (var sessionEvent in active.Subscription.Events
                               .WithCancellation(active.Cancellation.Token))
            {
                var projected = projector.Project(sessionEvent);
                var notification = new JsonRpcNotification(
                    "2.0",
                    projected.Method,
                    projected.Envelope);
                await _send(
                    JsonSerializer.SerializeToUtf8Bytes(
                        notification,
                        JsonOptions),
                    active.Cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (
            active.Cancellation.IsCancellationRequested)
        {
        }
        catch (SessionSubscriptionException)
        {
        }
        finally
        {
            if (_subscriptions.TryGetValue(subscriptionId, out var current) &&
                ReferenceEquals(current, active))
            {
                _subscriptions.TryRemove(subscriptionId, out _);
            }

            await active.DisposeAsync();
        }
    }

    private async Task CancelRequestAsync(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("id", out var id) ||
            id.ValueKind is not (
                JsonValueKind.String or JsonValueKind.Number or JsonValueKind.Null))
        {
            throw new ArgumentException("Cancellation ID is required.");
        }

        if (_inFlight.TryGetValue(id.GetRawText(), out var cancellation))
        {
            await cancellation.CancelAsync();
        }
    }

    private static bool TryReadRequest(
        JsonElement root,
        out JsonElement? id,
        out bool hasId,
        out string method,
        out JsonElement parameters)
    {
        id = null;
        hasId = false;
        method = string.Empty;
        parameters = default;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("jsonrpc", out var jsonrpc) ||
            jsonrpc.ValueKind != JsonValueKind.String ||
            !string.Equals(jsonrpc.GetString(), "2.0", StringComparison.Ordinal) ||
            !root.TryGetProperty("method", out var methodElement) ||
            methodElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(methodElement.GetString()))
        {
            return false;
        }

        method = methodElement.GetString()!;
        if (root.TryGetProperty("id", out var idElement))
        {
            if (idElement.ValueKind is not (
                    JsonValueKind.String or
                    JsonValueKind.Number or
                    JsonValueKind.Null))
            {
                return false;
            }

            id = idElement.Clone();
            hasId = true;
        }

        parameters = root.TryGetProperty("params", out var paramsElement)
            ? paramsElement.Clone()
            : JsonSerializer.SerializeToElement(new WireEmpty(), JsonOptions);
        return parameters.ValueKind is JsonValueKind.Object or JsonValueKind.Null;
    }

    private bool TryCompleteClientRequest(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("jsonrpc", out var jsonrpc) ||
            jsonrpc.ValueKind != JsonValueKind.String ||
            jsonrpc.GetString() != "2.0" ||
            root.TryGetProperty("method", out _) ||
            !root.TryGetProperty("id", out var id) ||
            id.ValueKind != JsonValueKind.String ||
            !_clientRequests.TryGetValue(id.GetString()!, out var pending))
        {
            return false;
        }

        if (root.TryGetProperty("result", out var result) &&
            !root.TryGetProperty("error", out _))
        {
            pending.Completion.TrySetResult(
                ToolBindingResult.Success(result.Clone()));
            return true;
        }

        var code = ToolErrorCodes.ExecutionFailed;
        if (root.TryGetProperty("error", out var error) &&
            error.ValueKind == JsonValueKind.Object &&
            error.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("errorCode", out var errorCode) &&
            errorCode.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(errorCode.GetString()))
        {
            code = errorCode.GetString()!;
        }

        pending.Completion.TrySetResult(DynamicFailure(
            code,
            "Dynamic Tool client returned an error."));
        return true;
    }

    private async ValueTask SendResultAsync(
        JsonElement? id,
        object result,
        CancellationToken cancellationToken)
    {
        var response = new JsonRpcResponse("2.0", id, result, Error: null);
        await _send(
            JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions),
            cancellationToken);
    }

    private ValueTask SendBusinessErrorAsync(
        JsonElement? id,
        SessionError error,
        long? currentSequence,
        long? currentRevision,
        string correlationId,
        CancellationToken cancellationToken) =>
        SendErrorAsync(
            id,
            ToRpcCode(error.Code),
            PublicMessage(error.Code),
            new WireErrorData(
                error.Code,
                error.IsRetryable,
                correlationId,
                currentSequence,
                currentRevision),
            cancellationToken);

    private ValueTask SendCapabilityErrorAsync(
        JsonElement? id,
        CapabilityServiceException error,
        string correlationId,
        CancellationToken cancellationToken) =>
        SendErrorAsync(
            id,
            error.Code switch
            {
                CapabilityErrorCodes.RevisionConflict => ConflictError,
                CapabilityErrorCodes.NotFound => NotFoundError,
                CapabilityErrorCodes.RuntimeUnavailable => UnavailableError,
                _ => BusinessError,
            },
            "Capability operation failed.",
            new WireErrorData(
                error.Code,
                error.IsRetryable,
                correlationId,
                CurrentRevision: error.CurrentRevision,
                CurrentGeneration: error.CurrentGeneration,
                CurrentVersion: error.CurrentVersion),
            cancellationToken);

    private async ValueTask SendErrorAsync(
        JsonElement? id,
        int code,
        string message,
        WireErrorData? data,
        CancellationToken cancellationToken)
    {
        var response = new JsonRpcResponse(
            "2.0",
            id,
            Result: null,
            new JsonRpcError(code, message, data));
        await _send(
            JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions),
            cancellationToken);
    }

    private static WireThreadResponse ThreadResponse(
        SessionCommandResult<ThreadSnapshot> result)
    {
        var thread = Require(result);
        return new WireThreadResponse(
            MapThread(thread),
            RequireSequence(result.Sequence),
            thread.CurrentSequence);
    }

    private static T Require<T>(SessionCommandResult<T> result)
    {
        if (result.Status == SessionCommandStatus.Rejected ||
            result.Value is null)
        {
            throw new WireRpcException(
                result.Error ?? new SessionError(
                    SessionErrorCodes.InvalidState,
                    "Session command failed.",
                    IsRetryable: false),
                result.CurrentSequence);
        }

        return result.Value;
    }

    private static T Require<T>(SessionQueryResult<T> result)
    {
        if (!result.IsSuccess || result.Value is null)
        {
            throw new WireRpcException(
                result.Error ?? new SessionError(
                    SessionErrorCodes.NotFound,
                    "Session query failed.",
                    IsRetryable: false));
        }

        return result.Value;
    }

    private void RequireWire11()
    {
        if (!SupportsCapabilityWire(_wireVersion) || _capabilities is null)
        {
            throw new WireMethodNotFoundException();
        }
    }

    private void RequireDynamicToolExecution()
    {
        RequireWire11();
        if (!_clientCapabilities.Contains("serverRequests") ||
            !_clientCapabilities.Contains("dynamicToolExecution"))
        {
            throw new CapabilityServiceException(
                DynamicToolErrorCodes.Disconnected,
                "Dynamic Tool execution was not negotiated.");
        }
    }

    private static CapabilityKind ParseCapabilityKind(string kind) =>
        Enum.TryParse<CapabilityKind>(kind, ignoreCase: true, out var value) &&
        string.Equals(WireName(value), kind, StringComparison.Ordinal)
            ? value
            : throw new ArgumentException("Invalid Capability kind.", nameof(kind));

    private static WireCapabilityMutationResponse MapCapabilityChange(
        CapabilityCatalogChange change) =>
        new(
            change.Revision,
            WireName(change.RuntimeState),
            change.Changed);

    private static WireDynamicToolRegistrationResponse MapDynamicTool(
        CapabilityDynamicToolRegistration registration) =>
        new(
            registration.ConnectionId,
            registration.ThreadId,
            registration.RegistrationId,
            registration.DefinitionSha256,
            WireName(registration.Status),
            registration.RuntimeBindingId,
            registration.ExpiresAt);

    private static ToolEffect ParseToolEffects(IEnumerable<string> effects)
    {
        ArgumentNullException.ThrowIfNull(effects);
        var result = ToolEffect.None;
        foreach (var effect in effects)
        {
            result |= ParseWireEnum<ToolEffect>(effect);
        }

        return result;
    }

    private static T ParseWireEnum<T>(string value)
        where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: true, out var parsed) &&
        string.Equals(WireName(parsed), value, StringComparison.Ordinal)
            ? parsed
            : throw new ArgumentException($"{typeof(T).Name} is invalid.");

    private static JsonElement RequireObject(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object
            ? value.Clone()
            : throw new ArgumentException("JSON object is required.");

    private static WireCapabilityItem MapCapability(CapabilityCatalogItem item) =>
        new(
            WireName(item.Kind),
            item.Id,
            item.DisplayName,
            item.Description,
            MapCapabilitySource(item.Source),
            WireName(item.Status),
            item.RequiredTrustScopes.Select(WireName).ToArray(),
            item.Generation,
            item.DiagnosticCodes.ToArray(),
            item.ConflictingSources.Select(MapCapabilitySource).ToArray());

    private static WireCapabilitySource MapCapabilitySource(
        CapabilitySourceDescriptor source) =>
        new(
            WireName(source.Kind),
            source.Id,
            source.Version,
            source.Sha256);

    private static string WireName<T>(T value)
        where T : struct, Enum =>
        JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    private void OnCapabilityCatalogChanged(
        object? sender,
        CapabilityCatalogChangedEventArgs args) =>
        _ = SendCapabilityChangedAsync(args);

    private async Task SendCapabilityChangedAsync(
        CapabilityCatalogChangedEventArgs args)
    {
        try
        {
            var notification = new JsonRpcNotification(
                "2.0",
                "capability/changed",
                new WireCapabilityChangedNotification(
                    args.Revision,
                    WireName(args.RuntimeState)));
            await _send(
                JsonSerializer.SerializeToUtf8Bytes(notification, JsonOptions),
                _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is IOException or ObjectDisposedException)
        {
        }
    }

    private async Task SendClientCancellationAsync(string id)
    {
        if (_lifetime.IsCancellationRequested)
        {
            return;
        }

        try
        {
            var notification = new JsonRpcNotification(
                "2.0",
                "$/cancelRequest",
                new { id });
            await _send(
                JsonSerializer.SerializeToUtf8Bytes(notification, JsonOptions),
                _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
    }

    private static ToolBindingResult DynamicFailure(string code, string message) =>
        ToolBindingResult.Failure(
            new SessionError(code, message, IsRetryable: false));

    private static long RequireSequence(long? sequence) =>
        sequence ?? throw new InvalidOperationException();

    private static string EncodeHistoryCursor(long sequence)
    {
        var value = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(
                    $"v1:{sequence.ToString(CultureInfo.InvariantCulture)}"))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return value;
    }

    private static long DecodeHistoryCursor(string? cursor)
    {
        if (cursor is null)
        {
            return 0;
        }

        try
        {
            var encoded = cursor.Replace('-', '+').Replace('_', '/');
            encoded += new string('=', (4 - encoded.Length % 4) % 4);
            var value = Encoding.UTF8.GetString(
                Convert.FromBase64String(encoded));
            return value.StartsWith("v1:", StringComparison.Ordinal) &&
                   long.TryParse(
                       value.AsSpan(3),
                       NumberStyles.None,
                       CultureInfo.InvariantCulture,
                       out var sequence) &&
                   sequence >= 0
                ? sequence
                : throw new FormatException();
        }
        catch (Exception exception) when (
            exception is FormatException or DecoderFallbackException)
        {
            throw new ArgumentException("History cursor is invalid.", nameof(cursor));
        }
    }

    private static T Deserialize<T>(JsonElement parameters) =>
        parameters.Deserialize<T>(JsonOptions)
        ?? throw new JsonException("Request body is required.");

    private static void ValidateInput(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Encoding.UTF8.GetByteCount(text) > OpenCoWorkWire.MaximumInputBytes)
        {
            throw new ArgumentException("Input is too large.", nameof(text));
        }
    }

    private static AgentMode ParseMode(string mode) =>
        mode switch
        {
            "agent" => AgentMode.Agent,
            "plan" => AgentMode.Plan,
            _ => throw new ArgumentException("Invalid mode.", nameof(mode)),
        };

    private static SessionSubscriptionMode ParseSubscriptionMode(string mode) =>
        mode switch
        {
            "snapshotThenLive" => SessionSubscriptionMode.SnapshotThenLive,
            "resumeAfterSequence" => SessionSubscriptionMode.ResumeAfterSequence,
            _ => throw new ArgumentException(
                "Invalid subscription mode.",
                nameof(mode)),
        };

    private static int ToRpcCode(string code) =>
        code switch
        {
            SessionErrorCodes.SequenceConflict or
            SessionErrorCodes.IdempotencyConflict or
            SessionErrorCodes.ThreadBusy or
            SessionErrorCodes.InteractionAlreadyResolved or
            AutomationErrorCodes.Conflict or
            AutomationErrorCodes.RunConflict => ConflictError,
            SessionErrorCodes.NotFound or
            SessionErrorCodes.QueueItemNotFound or
            AutomationErrorCodes.NotFound => NotFoundError,
            SessionErrorCodes.InvalidState or
            SessionErrorCodes.InvalidCursor or
            SessionErrorCodes.DeleteTokenInvalid or
            SessionErrorCodes.DeleteTokenExpired or
            SessionErrorCodes.UnsupportedHistoryMode or
            AutomationErrorCodes.InvalidState or
            AutomationErrorCodes.InvalidCursor => InvalidStateError,
            SessionErrorCodes.ProjectionUnavailable or
            SessionErrorCodes.RecoveryRequired or
            SessionErrorCodes.RuntimeExecutorUnavailable or
            SessionErrorCodes.RuntimeShuttingDown or
            SessionErrorCodes.SubscriberLagged or
            AutomationErrorCodes.Unavailable or
            AutomationErrorCodes.CapabilityUnavailable => UnavailableError,
            _ => BusinessError,
        };

    private static string PublicMessage(string code) =>
        code switch
        {
            SessionErrorCodes.NotFound => "Resource not found.",
            SessionErrorCodes.SequenceConflict => "Sequence conflict.",
            SessionErrorCodes.IdempotencyConflict => "Idempotency conflict.",
            SessionErrorCodes.ThreadBusy => "Thread is busy.",
            SessionErrorCodes.QueueFull => "Turn queue is full.",
            SessionErrorCodes.QueueItemNotFound => "Queued input not found.",
            SessionErrorCodes.InteractionAlreadyResolved =>
                "Interaction is already resolved.",
            SessionErrorCodes.InvalidCursor => "Cursor is invalid.",
            SessionErrorCodes.DeleteTokenInvalid or
            SessionErrorCodes.DeleteTokenExpired => "Delete token is invalid.",
            SessionErrorCodes.ProjectionUnavailable => "Session is unavailable.",
            SessionErrorCodes.RecoveryRequired => "Session recovery is required.",
            SessionErrorCodes.RuntimeExecutorUnavailable =>
                "Runtime executor is unavailable.",
            SessionErrorCodes.RuntimeShuttingDown => "Runtime is shutting down.",
            _ => "Request failed.",
        };

    private static WireThreadSnapshot MapThread(ThreadSnapshot thread) =>
        new(
            thread.ThreadId,
            thread.DisplayName,
            Wire(thread.Status),
            Wire(thread.Availability),
            Wire(thread.HistoryMode),
            thread.CurrentSequence,
            thread.ActiveTurnId,
            thread.Queue.Select(MapQueueItem).ToArray(),
            thread.CreatedAt,
            thread.UpdatedAt,
            Wire(thread.ProjectionState),
            thread.Diagnostic is null ? null : "recoveryRequired",
            thread.ProviderId,
            thread.ModelId,
            Wire(thread.AgentMode));

    private static WireQueuedTurnInputSnapshot MapQueueItem(
        QueuedTurnInputSnapshot item) =>
        new(
            item.QueueItemId,
            item.ThreadId,
            item.Text,
            item.Position,
            item.CreatedAt,
            Wire(item.EffectiveAgentMode));

    private static WireTurnSnapshot MapTurn(TurnSnapshot turn) =>
        new(
            turn.TurnId,
            turn.ThreadId,
            Wire(turn.Status),
            turn.CreatedAt,
            turn.UpdatedAt,
            turn.CompletedAt,
            turn.Error is null
                ? null
                : new WireEventErrorData(
                    turn.Error.Code,
                    turn.Error.IsRetryable),
            Wire(turn.EffectiveAgentMode));

    private static WireInteractionSnapshot MapInteraction(
        PendingInteractionSnapshot interaction) =>
        new(
            interaction.InteractionId,
            interaction.ThreadId,
            interaction.TurnId,
            Wire(interaction.Type),
            interaction.IsResolved,
            interaction.CreatedAt,
            interaction.TimeoutAt,
            interaction.ToolInvocationId);

    private static WireItemSnapshot MapItem(SessionItemSnapshot item) =>
        new(
            item.ItemId,
            item.TurnId,
            Wire(item.Type),
            Wire(item.Status),
            MapItemContent(item.Content),
            item.Sequence,
            item.CreatedAt,
            item.UpdatedAt);

    private static JsonElement MapItemContent(SessionItemContent content) =>
        content switch
        {
            TextItemContent text => JsonSerializer.SerializeToElement(
                new WireTextContent(text.Text),
                JsonOptions),
            ApprovalRequestContent approval => JsonSerializer.SerializeToElement(
                new WirePromptContent(approval.Prompt),
                JsonOptions),
            ApprovalResponseContent approval => JsonSerializer.SerializeToElement(
                new WireApprovalContent(approval.Approved),
                JsonOptions),
            UserInputRequestContent input => JsonSerializer.SerializeToElement(
                new WirePromptContent(input.Prompt),
                JsonOptions),
            UserInputResponseContent input => JsonSerializer.SerializeToElement(
                new WireTextContent(input.Text),
                JsonOptions),
            ErrorItemContent error => JsonSerializer.SerializeToElement(
                new WireCodeContent(error.Code),
                JsonOptions),
            _ => JsonSerializer.SerializeToElement(new WireEmpty(), JsonOptions),
        };

    private static string Wire<T>(T value)
        where T : struct, Enum
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private sealed class WireEventProjector
    {
        private readonly Dictionary<Guid, int> _offsets = [];

        public async Task PrimeAsync(
            ISessionService sessions,
            Guid threadId,
            long throughSequence,
            CancellationToken cancellationToken)
        {
            var cursor = 0L;
            while (cursor < throughSequence)
            {
                var page = Require(await sessions.ReadHistoryAsync(
                    new ReadHistoryRequest(threadId, cursor, PageSize: 100),
                    cancellationToken));
                var observed = page.Items
                    .TakeWhile(sessionEvent =>
                        sessionEvent.Sequence <= throughSequence)
                    .ToArray();
                foreach (var sessionEvent in observed)
                {
                    Observe(sessionEvent);
                }

                if (page.Items.Count == 0 ||
                    page.Items[^1].Sequence <= cursor ||
                    page.Items[^1].Sequence >= throughSequence)
                {
                    return;
                }

                cursor = page.Items[^1].Sequence;
            }
        }

        public ProjectedEvent Project(SessionEvent sessionEvent)
        {
            var method = EventMethod(sessionEvent);
            JsonElement payload;
            if (sessionEvent.Type == SessionEventType.ItemDeltaAppended &&
                sessionEvent.Payload.Item is { } deltaItem)
            {
                payload = JsonSerializer.SerializeToElement(
                    new WireItemDeltaEventPayload(Delta(deltaItem)),
                    JsonOptions);
            }
            else
            {
                Observe(sessionEvent);
                payload = EventPayload(method, sessionEvent);
            }

            return new ProjectedEvent(
                method,
                new WireEventEnvelope(
                    sessionEvent.EntryId,
                    sessionEvent.ThreadId,
                    sessionEvent.Payload.Turn?.TurnId ??
                    sessionEvent.Payload.Item?.TurnId ??
                    sessionEvent.Payload.Interaction?.TurnId,
                    sessionEvent.Payload.Item?.ItemId,
                    sessionEvent.Sequence,
                    sessionEvent.Timestamp,
                    payload));
        }

        private void Observe(SessionEvent sessionEvent)
        {
            if (sessionEvent.Payload.Item is not { } item)
            {
                return;
            }

            if (item.Content is TextItemContent text)
            {
                _offsets[item.ItemId] = text.Text.Length;
            }
            else if (sessionEvent.Type is
                     SessionEventType.ItemCompleted or
                     SessionEventType.ItemFailed or
                     SessionEventType.ItemCancelled)
            {
                _offsets.Remove(item.ItemId);
            }
        }

        private string Delta(SessionItemSnapshot item)
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

        private static string EventMethod(SessionEvent sessionEvent) =>
            sessionEvent.Type switch
            {
                SessionEventType.ThreadCreated => "thread/created",
                SessionEventType.ThreadForked => "thread/created",
                SessionEventType.ThreadRenamed or
                SessionEventType.ThreadModelChanged or
                SessionEventType.ThreadModeChanged or
                SessionEventType.ThreadRolledBack => "thread/updated",
                SessionEventType.ThreadPaused or
                SessionEventType.ThreadResumed or
                SessionEventType.ThreadArchived or
                SessionEventType.ThreadUnarchived => "thread/statusChanged",
                SessionEventType.ThreadDeletionRequested => "thread/deleted",
                SessionEventType.TurnQueued or
                SessionEventType.TurnQueueChanged or
                SessionEventType.TurnSteered => "thread/queueUpdated",
                SessionEventType.TurnStarted => "turn/started",
                SessionEventType.TurnCompleted => "turn/completed",
                SessionEventType.TurnFailed => "turn/failed",
                SessionEventType.TurnCancelled => "turn/cancelled",
                SessionEventType.TurnWaitingApproval =>
                    "item/approval/requested",
                SessionEventType.TurnWaitingInput =>
                    "item/input/requested",
                SessionEventType.ItemStarted
                    when sessionEvent.Payload.Item?.Type ==
                         SessionItemType.ApprovalRequest =>
                    "item/approval/requested",
                SessionEventType.ItemStarted
                    when sessionEvent.Payload.Item?.Type ==
                         SessionItemType.UserInputRequest =>
                    "item/input/requested",
                SessionEventType.ItemStarted => "item/started",
                SessionEventType.ItemDeltaAppended => "item/delta",
                SessionEventType.ItemCompleted or
                SessionEventType.ItemFailed or
                SessionEventType.ItemCancelled or
                SessionEventType.ToolCallRecorded or
                SessionEventType.ToolInvocationTerminal => "item/completed",
                SessionEventType.InteractionResolved
                    when sessionEvent.Payload.Item?.Type ==
                         SessionItemType.ApprovalResponse =>
                    "item/approval/resolved",
                SessionEventType.InteractionResolved
                    when sessionEvent.Payload.Item?.Type ==
                         SessionItemType.UserInputResponse =>
                    "item/input/resolved",
                _ => "system/event",
            };

        private static JsonElement EventPayload(
            string method,
            SessionEvent sessionEvent) =>
            method switch
            {
                "thread/created" or
                "thread/updated" or
                "thread/statusChanged" or
                "thread/deleted"
                    when sessionEvent.Payload.Thread is { } thread =>
                    JsonSerializer.SerializeToElement(
                        new WireThreadEventPayload(MapThread(thread)),
                        JsonOptions),
                "thread/queueUpdated"
                    when sessionEvent.Payload.Thread is { } thread =>
                    JsonSerializer.SerializeToElement(
                        new WireQueueEventPayload(
                            thread.Queue.Select(MapQueueItem).ToArray()),
                        JsonOptions),
                "turn/started" or
                "turn/completed" or
                "turn/failed" or
                "turn/cancelled"
                    when sessionEvent.Payload.Turn is { } turn =>
                    JsonSerializer.SerializeToElement(
                        new WireTurnEventPayload(MapTurn(turn)),
                        JsonOptions),
                "item/started" or
                "item/completed"
                    when sessionEvent.Payload.Item is { } item =>
                    JsonSerializer.SerializeToElement(
                        new WireItemEventPayload(MapItem(item)),
                        JsonOptions),
                "item/approval/requested" or
                "item/input/requested" or
                "item/approval/resolved" or
                "item/input/resolved"
                    when sessionEvent.Payload.Interaction is { } interaction =>
                    JsonSerializer.SerializeToElement(
                        new WireInteractionEventPayload(
                            MapInteraction(interaction)),
                        JsonOptions),
                _ => JsonSerializer.SerializeToElement(
                    new WireSystemEventPayload("internal"),
                    JsonOptions),
            };
    }

    private sealed class ActiveSubscription(
        SessionSubscription subscription,
        CancellationTokenSource cancellation)
    {
        private int _disposed;

        public SessionSubscription Subscription { get; } = subscription;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public Task Completion { get; set; } = Task.CompletedTask;

        public async Task StopAsync()
        {
            await Cancellation.CancelAsync();
            try
            {
                await Completion;
            }
            catch (OperationCanceledException)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await Subscription.DisposeAsync();
            Cancellation.Dispose();
        }
    }

    private sealed class WireRpcException(
        SessionError error,
        long? currentSequence = null,
        long? currentRevision = null) : Exception
    {
        public SessionError Error { get; } = error;

        public long? CurrentSequence { get; } = currentSequence;

        public long? CurrentRevision { get; } = currentRevision;
    }

    private sealed class WireMethodNotFoundException : Exception;

    private sealed record ProjectedEvent(
        string Method,
        WireEventEnvelope Envelope);

    private sealed record WireTextContent(string Text);

    private sealed record WirePromptContent(string Prompt);

    private sealed record WireApprovalContent(bool Approved);

    private sealed record WireCodeContent(string Code);

    private sealed record JsonRpcError(
        int Code,
        string Message,
        WireErrorData? Data);

    private sealed record JsonRpcResponse(
        string Jsonrpc,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        JsonElement? Id,
        object? Result,
        JsonRpcError? Error);

    private sealed record JsonRpcNotification(
        string Jsonrpc,
        string Method,
        [property: JsonPropertyName("params")] object Params);

    private sealed record JsonRpcClientRequest(
        string Jsonrpc,
        string Id,
        string Method,
        [property: JsonPropertyName("params")] object Params);

    private sealed class PendingClientRequest
    {
        public TaskCompletionSource<ToolBindingResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
