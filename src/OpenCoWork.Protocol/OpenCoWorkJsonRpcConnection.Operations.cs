using OpenCoWork.Abstractions;

namespace OpenCoWork.Protocol;

public sealed partial class OpenCoWorkJsonRpcConnection
{
    [OpenCoWorkWireMethod(
        "channel/list", OpenCoWorkWire.ClientToServer, "channel",
        OpenCoWorkWire.OperationsVersion,
        typeof(ChannelListQuery), typeof(ChannelPage<ChannelSnapshot>),
        OpenCoWorkWire.WorkspaceAuthority, false, OpenCoWorkWire.NoIdempotency)]
    public Task<ChannelPage<ChannelSnapshot>> ListChannelsAsync(
        ChannelListQuery request,
        CancellationToken cancellationToken)
    {
        RequireWire14();
        return _channels!.ListChannelsAsync(request, cancellationToken);
    }

    [OpenCoWorkWireMethod(
        "channel/get", OpenCoWorkWire.ClientToServer, "channel",
        OpenCoWorkWire.OperationsVersion,
        typeof(WireChannelGetRequest), typeof(ChannelSnapshot),
        OpenCoWorkWire.WorkspaceAuthority, false, OpenCoWorkWire.NoIdempotency)]
    public async Task<ChannelSnapshot> GetChannelAsync(
        WireChannelGetRequest request,
        CancellationToken cancellationToken)
    {
        RequireWire14();
        return await _channels!.GetChannelAsync(request.ChannelId, cancellationToken)
               ?? throw NotFound(ChannelErrorCodes.NotFound, "Channel was not found.");
    }

    [OpenCoWorkWireMethod(
        "channel/inbound/list", OpenCoWorkWire.ClientToServer, "channel",
        OpenCoWorkWire.OperationsVersion,
        typeof(ChannelInboundQuery), typeof(ChannelPage<ChannelInboundSummary>),
        OpenCoWorkWire.WorkspaceAuthority, false, OpenCoWorkWire.NoIdempotency)]
    public Task<ChannelPage<ChannelInboundSummary>> ListChannelInboundAsync(
        ChannelInboundQuery request,
        CancellationToken cancellationToken)
    {
        RequireWire14();
        return _channels!.ListInboundAsync(request, cancellationToken);
    }

    [OpenCoWorkWireMethod(
        "channel/outbox/list", OpenCoWorkWire.ClientToServer, "channel",
        OpenCoWorkWire.OperationsVersion,
        typeof(ChannelOutboxQuery), typeof(ChannelPage<ChannelOutboxSummary>),
        OpenCoWorkWire.WorkspaceAuthority, false, OpenCoWorkWire.NoIdempotency)]
    public Task<ChannelPage<ChannelOutboxSummary>> ListChannelOutboxAsync(
        ChannelOutboxQuery request,
        CancellationToken cancellationToken)
    {
        RequireWire14();
        return _channels!.ListOutboxAsync(request, cancellationToken);
    }

    [OpenCoWorkWireMethod(
        "channel/media/read", OpenCoWorkWire.ClientToServer, "channel",
        OpenCoWorkWire.OperationsVersion,
        typeof(WireChannelMediaReadRequest), typeof(ChannelMediaChunk),
        OpenCoWorkWire.WorkspaceAuthority, false, OpenCoWorkWire.NoIdempotency)]
    public Task<ChannelMediaChunk> ReadChannelMediaAsync(
        WireChannelMediaReadRequest request,
        CancellationToken cancellationToken)
    {
        RequireWire14();
        return _channels!.ReadMediaAsync(
            new ChannelMediaReadRequest(request.MediaId, request.Offset, request.Length),
            cancellationToken);
    }

    [OpenCoWorkWireMethod(
        "channel/deadLetter/retry", OpenCoWorkWire.ClientToServer, "channel",
        OpenCoWorkWire.OperationsVersion,
        typeof(WireChannelDeadLetterRetryRequest), typeof(ChannelOutboxSummary),
        OpenCoWorkWire.WorkspaceAuthority, true, OpenCoWorkWire.RequiredIdempotency)]
    public Task<ChannelOutboxSummary> RetryChannelDeadLetterAsync(
        WireChannelDeadLetterRetryRequest request,
        CancellationToken cancellationToken)
    {
        RequireWire14();
        RequireVersionSeven(request.CommandId, nameof(request.CommandId));
        return _channels!.RetryDeadLetterAsync(
            new ChannelDeadLetterRetryRequest(
                request.OutboxMessageId,
                request.CommandId,
                request.ExpectedRevision),
            cancellationToken);
    }

    [OpenCoWorkWireMethod(
        "hub/workspace/list", OpenCoWorkWire.ClientToServer, "hub",
        OpenCoWorkWire.OperationsVersion,
        typeof(WireHubWorkspaceListRequest), typeof(OperationsPage<HubWorkspaceSummary>),
        OpenCoWorkWire.UserAuthority, false, OpenCoWorkWire.NoIdempotency)]
    public Task<OperationsPage<HubWorkspaceSummary>> ListHubWorkspacesAsync(
        WireHubWorkspaceListRequest request,
        CancellationToken cancellationToken)
    {
        RequireWire14();
        return _hub!.ListWorkspacesAsync(
            new HubWorkspaceQuery(request.PageSize, request.Cursor),
            cancellationToken);
    }

    [OpenCoWorkWireMethod(
        "hub/workspace/get", OpenCoWorkWire.ClientToServer, "hub",
        OpenCoWorkWire.OperationsVersion,
        typeof(WireHubWorkspaceGetRequest), typeof(HubWorkspaceSummary),
        OpenCoWorkWire.UserAuthority, false, OpenCoWorkWire.NoIdempotency)]
    public async Task<HubWorkspaceSummary> GetHubWorkspaceAsync(
        WireHubWorkspaceGetRequest request,
        CancellationToken cancellationToken)
    {
        RequireWire14();
        return await _hub!.GetWorkspaceAsync(request.WorkspaceId, cancellationToken)
               ?? throw NotFound(
                   OperationsErrorCodes.HubWorkspaceNotFound,
                   "Workspace was not found.");
    }

    [OpenCoWorkWireMethod(
        "hub/dashboard/get", OpenCoWorkWire.ClientToServer, "hub",
        OpenCoWorkWire.OperationsVersion,
        typeof(WireHubWorkspaceGetRequest), typeof(OperationsDashboardSnapshot),
        OpenCoWorkWire.UserAuthority, false, OpenCoWorkWire.NoIdempotency)]
    public async Task<OperationsDashboardSnapshot> GetHubDashboardAsync(
        WireHubWorkspaceGetRequest request,
        CancellationToken cancellationToken)
    {
        RequireWire14();
        return await _hub!.GetDashboardAsync(request.WorkspaceId, cancellationToken)
               ?? throw NotFound(
                   OperationsErrorCodes.HubWorkspaceNotFound,
                   "Workspace was not found.");
    }

    [OpenCoWorkWireMethod(
        "usage/query", OpenCoWorkWire.ClientToServer, "operations",
        OpenCoWorkWire.OperationsVersion,
        typeof(UsageQuery), typeof(UsageAggregate[]),
        OpenCoWorkWire.WorkspaceAuthority, false, OpenCoWorkWire.NoIdempotency)]
    public async Task<UsageAggregate[]> QueryUsageAsync(
        UsageQuery request,
        CancellationToken cancellationToken)
    {
        RequireWire14();
        return [.. await _operations!.QueryUsageAsync(request, cancellationToken)];
    }

    [OpenCoWorkWireMethod(
        "trace/list", OpenCoWorkWire.ClientToServer, "operations",
        OpenCoWorkWire.OperationsVersion,
        typeof(TraceListQuery), typeof(OperationsPage<TraceSummary>),
        OpenCoWorkWire.WorkspaceAuthority, false, OpenCoWorkWire.NoIdempotency)]
    public Task<OperationsPage<TraceSummary>> ListTracesAsync(
        TraceListQuery request,
        CancellationToken cancellationToken)
    {
        RequireWire14();
        return _operations!.ListTracesAsync(request, cancellationToken);
    }

    [OpenCoWorkWireMethod(
        "trace/get", OpenCoWorkWire.ClientToServer, "operations",
        OpenCoWorkWire.OperationsVersion,
        typeof(WireTraceGetRequest), typeof(TraceSpanSnapshot[]),
        OpenCoWorkWire.WorkspaceAuthority, false, OpenCoWorkWire.NoIdempotency)]
    public async Task<TraceSpanSnapshot[]> GetTraceAsync(
        WireTraceGetRequest request,
        CancellationToken cancellationToken)
    {
        RequireWire14();
        var trace = await _operations!.GetTraceAsync(request.TraceId, cancellationToken);
        return trace.Count == 0
            ? throw NotFound(OperationsErrorCodes.TraceNotFound, "Trace was not found.")
            : [.. trace];
    }

    [OpenCoWorkWireMethod(
        "heartbeat/get", OpenCoWorkWire.ClientToServer, "operations",
        OpenCoWorkWire.OperationsVersion,
        typeof(WireHeartbeatGetRequest), typeof(OperationsHeartbeatSnapshot),
        OpenCoWorkWire.WorkspaceAuthority, false, OpenCoWorkWire.NoIdempotency)]
    public async Task<OperationsHeartbeatSnapshot> GetHeartbeatAsync(
        WireHeartbeatGetRequest request,
        CancellationToken cancellationToken)
    {
        RequireWire14();
        var heartbeat = request.WorkspaceId is { } workspaceId
            ? (await _hub!.GetDashboardAsync(workspaceId, cancellationToken))?.Heartbeat
            : await _operations!.GetHeartbeatAsync(cancellationToken);
        return heartbeat ?? throw NotFound(
            OperationsErrorCodes.HeartbeatUnavailable,
            "Workspace heartbeat is unavailable.");
    }

    [OpenCoWorkWireMethod(
        "insight/run", OpenCoWorkWire.ClientToServer, "insight",
        OpenCoWorkWire.OperationsVersion,
        typeof(WireInsightRunRequest), typeof(InsightRunSnapshot),
        OpenCoWorkWire.WorkspaceAuthority, true, OpenCoWorkWire.RequiredIdempotency)]
    public Task<InsightRunSnapshot> RunInsightAsync(
        WireInsightRunRequest request,
        CancellationToken cancellationToken)
    {
        RequireWire14();
        RequireVersionSeven(request.CommandId, nameof(request.CommandId));
        return _insights!.RunAsync(
            new InsightRunRequest(request.CommandId, InsightRunTrigger.Manual),
            cancellationToken);
    }

    [OpenCoWorkWireMethod(
        "insight/list", OpenCoWorkWire.ClientToServer, "insight",
        OpenCoWorkWire.OperationsVersion,
        typeof(WireInsightListRequest), typeof(WireInsightListResponse),
        OpenCoWorkWire.WorkspaceAuthority, false, OpenCoWorkWire.NoIdempotency)]
    public async Task<WireInsightListResponse> ListInsightsAsync(
        WireInsightListRequest request,
        CancellationToken cancellationToken)
    {
        RequireWire14();
        if (request.Kind == "runs")
        {
            var page = await _insights!.ListRunsAsync(
                request.PageSize,
                request.Cursor,
                cancellationToken);
            return new WireInsightListResponse("runs", page.Items, [], page.NextCursor);
        }
        if (request.Kind == "proposals")
        {
            var page = await _insights!.ListAsync(
                request.PageSize,
                request.Cursor,
                cancellationToken);
            return new WireInsightListResponse(
                "proposals", [], page.Items, page.NextCursor);
        }
        throw new ArgumentException("Insight list kind is invalid.", nameof(request));
    }

    [OpenCoWorkWireMethod(
        "insight/get", OpenCoWorkWire.ClientToServer, "insight",
        OpenCoWorkWire.OperationsVersion,
        typeof(WireInsightGetRequest), typeof(ImprovementProposalSnapshot),
        OpenCoWorkWire.WorkspaceAuthority, false, OpenCoWorkWire.NoIdempotency)]
    public async Task<ImprovementProposalSnapshot> GetInsightAsync(
        WireInsightGetRequest request,
        CancellationToken cancellationToken)
    {
        RequireWire14();
        return await _insights!.GetAsync(request.ProposalId, cancellationToken)
               ?? throw NotFound(
                   OperationsErrorCodes.InsightNotFound,
                   "Insight was not found.");
    }

    [OpenCoWorkWireMethod(
        "insight/archive", OpenCoWorkWire.ClientToServer, "insight",
        OpenCoWorkWire.OperationsVersion,
        typeof(WireInsightArchiveRequest), typeof(ImprovementProposalSnapshot),
        OpenCoWorkWire.WorkspaceAuthority, true, OpenCoWorkWire.RequiredIdempotency)]
    public Task<ImprovementProposalSnapshot> ArchiveInsightAsync(
        WireInsightArchiveRequest request,
        CancellationToken cancellationToken)
    {
        RequireWire14();
        RequireVersionSeven(request.CommandId, nameof(request.CommandId));
        return _insights!.ArchiveAsync(
            request.ProposalId,
            request.ExpectedRevision,
            cancellationToken);
    }

    private void RequireWire14()
    {
        if (_wireVersion != OpenCoWorkWire.OperationsVersion ||
            _channels is null || _hub is null || _operations is null || _insights is null)
        {
            throw new WireMethodNotFoundException();
        }
    }

    private static WireRpcException NotFound(string code, string message) =>
        new(new SessionError(code, message, IsRetryable: false));

    private static void RequireVersionSeven(Guid value, string parameterName)
    {
        if (value.Version != 7)
        {
            throw new ArgumentException("Command id must be UUIDv7.", parameterName);
        }
    }

    private void OnOperationsChanged(object? sender, OperationsChangedEvent change) =>
        _ = SendOperationsChangedAsync(change);

    private async Task SendOperationsChangedAsync(OperationsChangedEvent change)
    {
        if (_wireVersion != OpenCoWorkWire.OperationsVersion)
        {
            return;
        }

        var method = change.Kind switch
        {
            OperationsChangeKind.Channel => "channel/changed",
            OperationsChangeKind.Heartbeat => "heartbeat/changed",
            OperationsChangeKind.Insight => "insight/changed",
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        try
        {
            await _send(
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                    new JsonRpcNotification(
                        "2.0",
                        method,
                        new WireOperationsChangedNotification(
                            change.ChangeKind,
                            change.EntityId)),
                    JsonOptions),
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
}
