using System.Text.Json;
using OpenCoWork.Abstractions;

namespace OpenCoWork.Protocol;

public sealed partial class OpenCoWorkJsonRpcConnection
{
    [OpenCoWorkWireMethod(
        "automation/list", OpenCoWorkWire.ClientToServer, "automation",
        OpenCoWorkWire.AutomationVersion,
        typeof(WireListAutomationDefinitionsRequest),
        typeof(WireAutomationResponse<AutomationPage<AutomationDefinitionSummary>>),
        OpenCoWorkWire.ConnectionAuthority, false, OpenCoWorkWire.NoIdempotency)]
    public async Task<WireAutomationResponse<
        AutomationPage<AutomationDefinitionSummary>>> ListAutomationsAsync(
            WireListAutomationDefinitionsRequest request,
            CancellationToken cancellationToken) =>
        ProjectAutomation(await RequireAutomations().ListDefinitionsAsync(
            new ListAutomationDefinitionsRequest(
                AutomationActor(),
                request.PageSize,
                request.Cursor),
            cancellationToken));

    [OpenCoWorkWireMethod(
        "automation/get", OpenCoWorkWire.ClientToServer, "automation",
        OpenCoWorkWire.AutomationVersion,
        typeof(WireGetAutomationDefinitionRequest),
        typeof(WireAutomationResponse<AutomationDefinitionSnapshot>),
        OpenCoWorkWire.ConnectionAuthority, false, OpenCoWorkWire.NoIdempotency)]
    public async Task<WireAutomationResponse<AutomationDefinitionSnapshot>>
        GetAutomationAsync(
            WireGetAutomationDefinitionRequest request,
            CancellationToken cancellationToken) =>
        ProjectAutomation(await RequireAutomations().GetDefinitionAsync(
            new GetAutomationDefinitionRequest(
                AutomationActor(),
                request.AutomationId),
            cancellationToken));

    [OpenCoWorkWireMethod(
        "schedule/list", OpenCoWorkWire.ClientToServer, "schedule",
        OpenCoWorkWire.AutomationVersion,
        typeof(WireListAutomationSchedulesRequest),
        typeof(WireAutomationResponse<AutomationPage<AutomationScheduleSnapshot>>),
        OpenCoWorkWire.ConnectionAuthority, false, OpenCoWorkWire.NoIdempotency)]
    public async Task<WireAutomationResponse<
        AutomationPage<AutomationScheduleSnapshot>>> ListAutomationSchedulesAsync(
            WireListAutomationSchedulesRequest request,
            CancellationToken cancellationToken) =>
        ProjectAutomation(await RequireAutomations().ListSchedulesAsync(
            new ListAutomationSchedulesRequest(
                AutomationActor(),
                request.PageSize,
                request.Cursor),
            cancellationToken));

    [OpenCoWorkWireMethod(
        "schedule/get", OpenCoWorkWire.ClientToServer, "schedule",
        OpenCoWorkWire.AutomationVersion,
        typeof(WireGetAutomationScheduleRequest),
        typeof(WireAutomationResponse<AutomationScheduleSnapshot>),
        OpenCoWorkWire.ConnectionAuthority, false, OpenCoWorkWire.NoIdempotency)]
    public async Task<WireAutomationResponse<AutomationScheduleSnapshot>>
        GetAutomationScheduleAsync(
            WireGetAutomationScheduleRequest request,
            CancellationToken cancellationToken) =>
        ProjectAutomation(await RequireAutomations().GetScheduleAsync(
            new GetAutomationScheduleRequest(
                AutomationActor(),
                request.AutomationId),
            cancellationToken));

    [OpenCoWorkWireMethod(
        "automationRun/start", OpenCoWorkWire.ClientToServer, "automationRun",
        OpenCoWorkWire.AutomationVersion, typeof(WireStartAutomationRunRequest),
        typeof(WireAutomationResponse<AutomationRunSnapshot>),
        OpenCoWorkWire.ConnectionAuthority, true,
        OpenCoWorkWire.RequiredIdempotency)]
    public Task<WireAutomationResponse<AutomationRunSnapshot>>
        StartAutomationRunAsync(
            WireStartAutomationRunRequest request,
            CancellationToken cancellationToken) =>
        MutateAutomationAsync(
            RequireAutomations().StartRunAsync(
                new StartAutomationRunRequest(
                    AutomationActor(),
                    request.AutomationId,
                    request.Inputs,
                    request.CommandId,
                    request.ExpectedRevision),
                cancellationToken),
            "started",
            cancellationToken);

    [OpenCoWorkWireMethod(
        "automationRun/list", OpenCoWorkWire.ClientToServer, "automationRun",
        OpenCoWorkWire.AutomationVersion, typeof(WireListAutomationRunsRequest),
        typeof(WireAutomationResponse<AutomationPage<AutomationRunSummary>>),
        OpenCoWorkWire.ConnectionAuthority, false, OpenCoWorkWire.NoIdempotency)]
    public async Task<WireAutomationResponse<AutomationPage<AutomationRunSummary>>>
        ListAutomationRunsAsync(
            WireListAutomationRunsRequest request,
            CancellationToken cancellationToken) =>
        ProjectAutomation(await RequireAutomations().ListRunsAsync(
            new ListAutomationRunsRequest(
                AutomationActor(),
                request.AutomationId,
                ParseOptionalEnum<AutomationRunStatus>(request.Status),
                request.PageSize,
                request.Cursor),
            cancellationToken));

    [OpenCoWorkWireMethod(
        "automationRun/get", OpenCoWorkWire.ClientToServer, "automationRun",
        OpenCoWorkWire.AutomationVersion, typeof(WireGetAutomationRunRequest),
        typeof(WireAutomationResponse<AutomationRunSnapshot>),
        OpenCoWorkWire.ConnectionAuthority, false, OpenCoWorkWire.NoIdempotency)]
    public async Task<WireAutomationResponse<AutomationRunSnapshot>>
        GetAutomationRunAsync(
            WireGetAutomationRunRequest request,
            CancellationToken cancellationToken) =>
        ProjectAutomation(await RequireAutomations().GetRunAsync(
            new GetAutomationRunRequest(AutomationActor(), request.RunId),
            cancellationToken));

    [OpenCoWorkWireMethod(
        "automationRun/cancel", OpenCoWorkWire.ClientToServer, "automationRun",
        OpenCoWorkWire.AutomationVersion, typeof(WireCancelAutomationRunRequest),
        typeof(WireAutomationResponse<AutomationRunSnapshot>),
        OpenCoWorkWire.ConnectionAuthority, true,
        OpenCoWorkWire.RequiredIdempotency)]
    public Task<WireAutomationResponse<AutomationRunSnapshot>>
        CancelAutomationRunAsync(
            WireCancelAutomationRunRequest request,
            CancellationToken cancellationToken) =>
        MutateAutomationAsync(
            RequireAutomations().CancelRunAsync(
                new CancelAutomationRunRequest(
                    AutomationActor(),
                    request.RunId,
                    request.CommandId,
                    request.ExpectedRevision),
                cancellationToken),
            "cancelled",
            cancellationToken);

    [OpenCoWorkWireMethod(
        "automationRun/resolveAttention", OpenCoWorkWire.ClientToServer,
        "automationRun", OpenCoWorkWire.AutomationVersion,
        typeof(WireResolveAutomationAttentionRequest),
        typeof(WireAutomationResponse<AutomationRunSnapshot>),
        OpenCoWorkWire.ConnectionAuthority, true,
        OpenCoWorkWire.RequiredIdempotency)]
    public Task<WireAutomationResponse<AutomationRunSnapshot>>
        ResolveAutomationAttentionAsync(
            WireResolveAutomationAttentionRequest request,
            CancellationToken cancellationToken) =>
        MutateAutomationAsync(
            RequireAutomations().ResolveAttentionAsync(
                new ResolveAutomationAttentionRequest(
                    AutomationActor(),
                    request.RunId,
                    request.AttentionId,
                    new AutomationAttentionResolution(
                        ParseEnum<AutomationAttentionResolutionKind>(
                            request.Resolution.Kind),
                        request.Resolution.Text),
                    request.CommandId,
                    request.ExpectedRevision),
                cancellationToken),
            "attentionResolved",
            cancellationToken);

    private IAutomationService RequireAutomations() =>
        _automations ?? throw new WireRpcException(
            new SessionError(
                AutomationErrorCodes.Unavailable,
                "Automations are unavailable.",
                IsRetryable: true));

    private AutomationActorContext AutomationActor() =>
        new(
            AutomationActorKind.Host,
            $"wire:{_connectionId:D}");

    private async Task<WireAutomationResponse<AutomationRunSnapshot>>
        MutateAutomationAsync(
            Task<AutomationResult<AutomationRunSnapshot>> operation,
            string changeKind,
            CancellationToken cancellationToken)
    {
        var result = await operation;
        var response = ProjectAutomation(result);
        if (!result.IsReplay)
        {
            await SendAutomationChangedAsync(
                "automationRun/changed",
                new WireAutomationChangedNotification(
                    response.AutomationRevision,
                    changeKind,
                    response.Value.Summary.RunId.ToString("D")),
                cancellationToken);
        }

        return response;
    }

    private static WireAutomationResponse<T> ProjectAutomation<T>(
        AutomationResult<T> result)
    {
        if (result.Error is { } error)
        {
            throw new WireRpcException(
                new SessionError(error.Code, error.Message, error.IsRetryable),
                currentRevision: result.AutomationRevision);
        }

        return new WireAutomationResponse<T>(
            result.AutomationRevision,
            result.Value ?? throw new InvalidDataException(
                "Automation returned a successful result without a value."));
    }

    private async ValueTask SendAutomationChangedAsync(
        string method,
        WireAutomationChangedNotification notification,
        CancellationToken cancellationToken)
    {
        if (VersionRank(_wireVersion) < VersionRank(OpenCoWorkWire.AutomationVersion))
        {
            return;
        }

        await _send(
            JsonSerializer.SerializeToUtf8Bytes(
                new JsonRpcNotification("2.0", method, notification),
                JsonOptions),
            cancellationToken);
    }

    private void OnAutomationChanged(
        object? sender,
        AutomationChangedEvent change) =>
        _ = SendAutomationServiceChangedAsync(change);

    private async Task SendAutomationServiceChangedAsync(
        AutomationChangedEvent change)
    {
        try
        {
            await SendAutomationChangedAsync(
                change.EntityKind + "/changed",
                new WireAutomationChangedNotification(
                    change.AutomationRevision,
                    change.ChangeKind,
                    change.EntityId),
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
