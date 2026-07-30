using System.Text.Json;

namespace OpenCoWork.Abstractions;

public static class OpenCoWorkWire
{
    public const string Version = "1.0";
    public const string CapabilityVersion = "1.1";
    public const string CoWorkVersion = "1.2";
    public const string AutomationVersion = "1.3";
    public const string LatestVersion = AutomationVersion;
    public const string ClientToServer = "clientToServer";
    public const string ServerToClient = "serverToClient";
    public const string SessionOwner = "session";
    public const string CapabilityOwner = "capability";
    public const string ConnectionAuthority = "connection";
    public const string ThreadAuthority = "thread";
    public const string WorkspaceAuthority = "workspace";
    public const string NoIdempotency = "none";
    public const string RequiredIdempotency = "required";
    public const int MaximumMessageBytes = 1024 * 1024;
    public const int MaximumInputBytes = 256 * 1024;
    public const int OutboundQueueCapacity = 256;
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class OpenCoWorkWireMethodAttribute : Attribute
{
    public OpenCoWorkWireMethodAttribute(
        string method,
        string direction,
        string owner,
        string since,
        Type request,
        Type response,
        string authority,
        bool mutates,
        string idempotency)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(direction);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(since);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(authority);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotency);
        Method = method;
        Direction = direction;
        Owner = owner;
        Since = since;
        Request = request;
        Response = response;
        Authority = authority;
        Mutates = mutates;
        Idempotency = idempotency;
    }

    public string Method { get; }

    public string Direction { get; }

    public string Owner { get; }

    public string Since { get; }

    public Type Request { get; }

    public Type Response { get; }

    public string Authority { get; }

    public bool Mutates { get; }

    public string Idempotency { get; }
}

public sealed record WireMethodDescriptor(
    string Method,
    string Direction,
    string Owner,
    string Since,
    Type Request,
    Type Response,
    string Authority,
    bool Mutates,
    string Idempotency);

public sealed record WireEmpty;

public sealed record WireClientInfo(string Name, string Version);

public sealed record WireServerInfo(string Name, string Version);

public sealed record WireWorkspaceRequest(string Path);

public sealed record WireWorkspaceInfo(string Path);

public sealed record WireProtocolLimits(
    int MaximumMessageBytes,
    int MaximumInputBytes,
    int OutboundQueueCapacity);

public sealed record WireInitializeRequest(
    WireClientInfo Client,
    string[] WireVersions,
    WireWorkspaceRequest Workspace,
    string[]? Capabilities = null);

public sealed record WireInitializeResponse(
    WireServerInfo Server,
    string WireVersion,
    WireWorkspaceInfo Workspace,
    string[] Capabilities,
    WireProtocolLimits Limits,
    string Transport,
    string CorrelationId);

public sealed record WireErrorData(
    string ErrorCode,
    bool Retryable,
    string CorrelationId,
    long? CurrentSequence = null,
    long? CurrentRevision = null,
    long? CurrentGeneration = null,
    long? CurrentVersion = null);

public sealed record WireCapabilityCatalogRequest(
    int Limit = 50,
    string? Cursor = null);

public sealed record WireCapabilityReadRequest(string Kind, string Id);

public sealed record WireCapabilityRefreshRequest(long ExpectedRevision);

public sealed record WireCapabilitySetEnabledRequest(
    string Kind,
    string Id,
    bool Enabled,
    long ExpectedRevision);

public sealed record WireCapabilitySource(
    string Kind,
    string Id,
    string? Version,
    string Sha256);

public sealed record WireCapabilityItem(
    string Kind,
    string Id,
    string DisplayName,
    string Description,
    WireCapabilitySource Source,
    string Status,
    string[] RequiredTrustScopes,
    long Generation,
    string[] DiagnosticCodes,
    WireCapabilitySource[] ConflictingSources);

public sealed record WireCapabilityCatalogResponse(
    int SchemaVersion,
    long Revision,
    string CatalogSha256,
    string RuntimeState,
    WireCapabilityItem[] Items,
    string? NextCursor);

public sealed record WireCapabilityReadResponse(
    long Revision,
    WireCapabilityItem Item);

public sealed record WireCapabilityMutationResponse(
    long Revision,
    string RuntimeState,
    bool Changed);

public sealed record WireCapabilityChangedNotification(
    long Revision,
    string RuntimeState);

public sealed record WireCapabilityOperationRequest(JsonElement Arguments);

public sealed record WireThreadCapabilityOperationRequest(
    Guid ThreadId,
    JsonElement Arguments);

public sealed record WireCapabilityOperationResponse(
    JsonElement Result,
    long? Revision = null);

public sealed record WireDynamicToolDefinition(
    string Name,
    string Description,
    JsonElement InputSchema,
    string[] Effects,
    string ReplaySafety);

public sealed record WireDynamicToolRegisterRequest(
    Guid ThreadId,
    Guid RegistrationId,
    WireDynamicToolDefinition Definition,
    string DefinitionSha256,
    int? LeaseSeconds = null);

public sealed record WireDynamicToolRenewRequest(
    Guid ThreadId,
    Guid RegistrationId,
    int LeaseSeconds);

public sealed record WireDynamicToolUnregisterRequest(
    Guid ThreadId,
    Guid RegistrationId);

public sealed record WireDynamicToolRegistrationResponse(
    Guid ConnectionId,
    Guid ThreadId,
    Guid RegistrationId,
    string DefinitionSha256,
    string Status,
    string RuntimeBindingId,
    DateTimeOffset ExpiresAt);

public sealed record WireToolInvokeRequest(
    Guid ThreadId,
    Guid RegistrationId,
    JsonElement Arguments);

public sealed record WireToolInvokeResponse(JsonElement Result);

public sealed record WireEventEnvelope(
    Guid EventId,
    Guid ThreadId,
    Guid? TurnId,
    Guid? ItemId,
    long Sequence,
    DateTimeOffset Timestamp,
    JsonElement Payload);

public sealed record WireEventErrorData(string ErrorCode, bool Retryable);

public sealed record WireThreadEventPayload(WireThreadSnapshot Thread);

public sealed record WireTurnEventPayload(WireTurnSnapshot Turn);

public sealed record WireItemSnapshot(
    Guid ItemId,
    Guid TurnId,
    string Type,
    string Status,
    JsonElement Content,
    long Sequence,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record WireItemEventPayload(WireItemSnapshot Item);

public sealed record WireItemDeltaEventPayload(string Delta);

public sealed record WireInteractionEventPayload(WireInteractionSnapshot Interaction);

public sealed record WireQueueEventPayload(WireQueuedTurnInputSnapshot[] Queue);

public sealed record WireSystemEventPayload(string Type);

public sealed record WireCreateThreadRequest(
    Guid IdempotencyKey,
    long ExpectedSequence,
    string? DisplayName = null,
    string? ProviderId = null,
    string? ModelId = null,
    string Mode = "agent");

public sealed record WireThreadRequest(Guid ThreadId);

public sealed record WireListThreadsRequest(
    string? Cursor = null,
    int PageSize = 100,
    bool IncludeArchived = false);

public sealed record WireReadHistoryRequest(
    Guid ThreadId,
    string? Cursor = null,
    int PageSize = 100);

public sealed record WireRenameThreadRequest(
    Guid ThreadId,
    Guid IdempotencyKey,
    long ExpectedSequence,
    string DisplayName);

public sealed record WireSetModelRequest(
    Guid ThreadId,
    Guid IdempotencyKey,
    long ExpectedSequence,
    string ProviderId,
    string ModelId);

public sealed record WireSetModeRequest(
    Guid ThreadId,
    Guid IdempotencyKey,
    long ExpectedSequence,
    string Mode);

public sealed record WireThreadMutationRequest(
    Guid ThreadId,
    Guid IdempotencyKey,
    long ExpectedSequence);

public sealed record WirePrepareDeleteRequest(Guid ThreadId, long ExpectedSequence);

public sealed record WireDeleteThreadRequest(
    Guid ThreadId,
    Guid IdempotencyKey,
    long ExpectedSequence,
    string Token);

public sealed record WireForkThreadRequest(
    Guid SourceThreadId,
    long SourceSequence,
    long ExpectedSequence,
    Guid IdempotencyKey,
    string? DisplayName = null);

public sealed record WireRollbackThreadRequest(
    Guid ThreadId,
    long TargetSequence,
    long ExpectedSequence,
    Guid IdempotencyKey);

public sealed record WireSubscribeThreadRequest(
    Guid ThreadId,
    string Mode,
    long? AfterSequence = null);

public sealed record WireUnsubscribeThreadRequest(string SubscriptionId);

public sealed record WireStartTurnRequest(
    Guid ThreadId,
    Guid IdempotencyKey,
    long ExpectedSequence,
    string Text);

public sealed record WireEnqueueTurnRequest(
    Guid ThreadId,
    Guid IdempotencyKey,
    long ExpectedSequence,
    string Text);

public sealed record WireRemoveQueuedInputRequest(
    Guid ThreadId,
    Guid QueueItemId,
    Guid IdempotencyKey,
    long ExpectedSequence);

public sealed record WireReorderQueuedInputsRequest(
    Guid ThreadId,
    Guid[] QueueItemIds,
    Guid IdempotencyKey,
    long ExpectedSequence);

public sealed record WireSteerTurnRequest(
    Guid ThreadId,
    Guid ExpectedTurnId,
    Guid QueueItemId,
    Guid IdempotencyKey,
    long ExpectedSequence);

public sealed record WireCancelTurnRequest(
    Guid ThreadId,
    Guid TurnId,
    Guid IdempotencyKey,
    long ExpectedSequence);

public sealed record WireResolveApprovalRequest(
    Guid ThreadId,
    Guid TurnId,
    Guid InteractionId,
    string Decision,
    Guid IdempotencyKey,
    long ExpectedSequence);

public sealed record WireResolveInputRequest(
    Guid ThreadId,
    Guid TurnId,
    Guid InteractionId,
    string Text,
    Guid IdempotencyKey,
    long ExpectedSequence);

public sealed record WireQueuedTurnInputSnapshot(
    Guid QueueItemId,
    Guid ThreadId,
    string Text,
    int Position,
    DateTimeOffset CreatedAt,
    string EffectiveMode);

public sealed record WireThreadSnapshot(
    Guid ThreadId,
    string DisplayName,
    string Status,
    string Availability,
    string HistoryMode,
    long CurrentSequence,
    Guid? ActiveTurnId,
    WireQueuedTurnInputSnapshot[] Queue,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string ProjectionState,
    string? Diagnostic,
    string? ProviderId,
    string? ModelId,
    string Mode);

public sealed record WireTurnSnapshot(
    Guid TurnId,
    Guid ThreadId,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    WireEventErrorData? Error,
    string EffectiveMode);

public sealed record WireInteractionSnapshot(
    Guid InteractionId,
    Guid ThreadId,
    Guid TurnId,
    string Type,
    bool IsResolved,
    DateTimeOffset CreatedAt,
    DateTimeOffset? TimeoutAt,
    Guid? ToolInvocationId);

public sealed record WireThreadResponse(
    WireThreadSnapshot Thread,
    long Sequence,
    long CurrentSequence);

public sealed record WireThreadPageResponse(
    WireThreadSnapshot[] Threads,
    string? NextCursor);

public sealed record WireHistoryPageResponse(
    WireHistoryEvent[] Events,
    string? NextCursor);

public sealed record WireHistoryEvent(
    string Method,
    WireEventEnvelope Event);

public sealed record WireDeletePreparationResponse(
    Guid ThreadId,
    long Sequence,
    string Token,
    DateTimeOffset ExpiresAt);

public sealed record WireDeleteThreadResponse(
    bool Deleted,
    long Sequence,
    long CurrentSequence);

public sealed record WireRollbackThreadResponse(
    WireThreadSnapshot Thread,
    bool ExternalSideEffectsReverted,
    long Sequence,
    long CurrentSequence);

public sealed record WireAcceptedTurnResponse(
    Guid ThreadId,
    Guid TurnId,
    long AcceptedSequence);

public sealed record WireQueuedTurnResponse(
    WireQueuedTurnInputSnapshot QueueItem,
    long Sequence,
    long CurrentSequence);

public sealed record WireTurnResponse(
    WireTurnSnapshot Turn,
    long Sequence,
    long CurrentSequence);

public sealed record WireInteractionResponse(
    WireInteractionSnapshot Interaction,
    long Sequence,
    long CurrentSequence);

public sealed record WireSubscribeThreadResponse(
    string SubscriptionId,
    string Disposition,
    WireThreadSnapshot Snapshot,
    long CurrentSequence);

public sealed record WireAcknowledgement(bool Acknowledged = true);
