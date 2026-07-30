using System.Text.Json;

namespace OpenCoWork.Abstractions;

public sealed record WireAutomationResponse<T>(
    long AutomationRevision,
    T Value);

public sealed record WireAutomationChangedNotification(
    long AutomationRevision,
    string ChangeKind,
    string EntityId);

public sealed record WireListAutomationDefinitionsRequest(
    int PageSize = AutomationRuntimeLimits.DefaultPageSize,
    string? Cursor = null);

public sealed record WireGetAutomationDefinitionRequest(string AutomationId);

public sealed record WireListAutomationSchedulesRequest(
    int PageSize = AutomationRuntimeLimits.DefaultPageSize,
    string? Cursor = null);

public sealed record WireGetAutomationScheduleRequest(string AutomationId);

public sealed record WireStartAutomationRunRequest(
    string AutomationId,
    JsonElement Inputs,
    Guid CommandId,
    long ExpectedRevision);

public sealed record WireListAutomationRunsRequest(
    string? AutomationId = null,
    string? Status = null,
    int PageSize = AutomationRuntimeLimits.DefaultPageSize,
    string? Cursor = null);

public sealed record WireGetAutomationRunRequest(Guid RunId);

public sealed record WireCancelAutomationRunRequest(
    Guid RunId,
    Guid CommandId,
    long ExpectedRevision);

public sealed record WireAutomationAttentionResolution(
    string Kind,
    string? Text = null);

public sealed record WireResolveAutomationAttentionRequest(
    Guid RunId,
    Guid AttentionId,
    WireAutomationAttentionResolution Resolution,
    Guid CommandId,
    long ExpectedRevision);
