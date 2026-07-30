using System.Text.Json;
using OpenCoWork.Abstractions;

namespace OpenCoWork.Core.Sessions;

internal sealed record ThreadCreatedFact(
    string DisplayName,
    HistoryMode HistoryMode,
    string? FirstUserMessage,
    string RequestSha256,
    string? ProviderId = null,
    string? ModelId = null,
    AgentMode AgentMode = AgentMode.Agent,
    ExecutionWorkspaceDescriptor? ExecutionWorkspace = null,
    CoWorkThreadProvenance? CoWorkProvenance = null,
    AutomationThreadProvenance? AutomationProvenance = null);

internal sealed record ThreadRenamedFact(
    string DisplayName,
    string RequestSha256);

internal sealed record ThreadModelChangedFact(
    string ProviderId,
    string ModelId,
    string RequestSha256);

internal sealed record ThreadModeChangedFact(
    AgentMode AgentMode,
    string RequestSha256);

internal sealed record ThreadStateFact(string RequestSha256);

internal sealed record HistoryCheckpointFact(
    IReadOnlyList<TurnSnapshot> Turns,
    IReadOnlyList<HistoryCheckpointItemFact> Items,
    IReadOnlyList<HistoryCheckpointToolInvocationFact>? ToolInvocations = null);

internal sealed record HistoryCheckpointItemFact(
    Guid ItemId,
    Guid TurnId,
    SessionItemType ItemType,
    SessionItemStatus Status,
    JsonElement Content,
    string? ContentText,
    long Sequence,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

internal sealed record HistoryCheckpointToolInvocationFact(
    ToolInvocationSnapshot Snapshot,
    Guid ToolCallItemId,
    int CallIndex);

internal sealed record ThreadDeletionRequestedFact(
    string ThreadIdSha256,
    string IdempotencyKeySha256,
    long ExpectedSequence,
    string RequestSha256);

internal sealed record ThreadForkedFact(
    Guid SourceThreadId,
    long SourceSequence,
    string DisplayName,
    HistoryMode HistoryMode,
    HistoryCheckpointFact History,
    string RequestSha256,
    string? ProviderId = null,
    string? ModelId = null,
    AgentMode AgentMode = AgentMode.Agent,
    ExecutionWorkspaceDescriptor? ExecutionWorkspace = null,
    CoWorkThreadProvenance? CoWorkProvenance = null);

internal sealed record ThreadRolledBackFact(
    long TargetSequence,
    HistoryCheckpointFact History,
    string RequestSha256);

internal sealed record TurnQueuedFact(
    Guid QueueItemId,
    string Text,
    int Position,
    string RequestSha256,
    AgentMode EffectiveAgentMode = AgentMode.Agent);

internal sealed record TurnQueueChangedFact(
    IReadOnlyList<Guid> QueueItemIds,
    Guid? RemovedQueueItemId,
    string RequestSha256);

internal sealed record TurnStartedFact(
    Guid TurnId,
    Guid? QueueItemId,
    Guid? UserItemId,
    string? Text,
    string RequestSha256,
    AgentMode EffectiveAgentMode = AgentMode.Agent);

internal sealed record TurnSteeredFact(
    Guid TurnId,
    Guid QueueItemId,
    Guid UserItemId,
    string Text,
    string RequestSha256);

internal sealed record TurnWaitingFact(
    Guid TurnId,
    Guid InteractionId,
    Guid ItemId,
    SessionInteractionType InteractionType,
    SessionItemType RequestItemType,
    JsonElement Request,
    string? ContentText,
    int ContentLength,
    string ContentSha256,
    SessionExecutionCheckpoint Checkpoint,
    DateTimeOffset? TimeoutAt,
    string RequestSha256,
    Guid? ToolInvocationId = null);

internal sealed record InteractionResolvedFact(
    Guid InteractionId,
    Guid ResponseItemId,
    SessionItemType ResponseItemType,
    JsonElement Resolution,
    string? ContentText,
    int ContentLength,
    string ContentSha256,
    string RequestSha256);

internal sealed record TurnExecutionResumedFact(
    Guid TurnId,
    Guid InteractionId,
    string RequestSha256);

internal sealed record TurnTerminalFact(
    Guid TurnId,
    SessionError? Error,
    string RequestSha256);

internal sealed record ItemStartedFact(
    Guid ItemId,
    Guid TurnId,
    SessionItemType ItemType,
    JsonElement Content,
    string? ContentText,
    string RequestSha256);

internal sealed record ItemDeltaFact(
    Guid ItemId,
    string Delta,
    string RequestSha256);

internal sealed record ItemCompletedFact(
    Guid ItemId,
    int ContentLength,
    string ContentSha256,
    string RequestSha256);

internal sealed record ItemTerminalFact(
    Guid ItemId,
    SessionError? Error,
    string RequestSha256);

internal sealed record ToolCallRecordedFact(
    Guid ItemId,
    Guid TurnId,
    JsonElement Content,
    int ContentLength,
    string ContentSha256,
    string RequestSha256);

internal sealed record ToolInvocationStartedFact(
    Guid ToolInvocationId,
    Guid TurnId,
    Guid ToolCallItemId,
    int CallIndex,
    string ProviderToolCallId,
    string ProviderToolName,
    ToolDefinitionId? ToolDefinitionId,
    RuntimeBindingId? RuntimeBindingId,
    string SnapshotSha256,
    string ArgumentsSha256,
    string RequestSha256);

internal sealed record ToolInvocationAttemptStartedFact(
    Guid ToolInvocationId,
    int AttemptNumber,
    string RequestSha256);

internal sealed record ToolInvocationTerminalFact(
    Guid ToolInvocationId,
    ToolInvocationStatus Status,
    string? ErrorCode,
    string ResultSha256,
    Guid ResultItemId);

internal sealed record ToolResultItemFact(
    Guid ItemId,
    Guid TurnId,
    JsonElement Content,
    int ContentLength,
    string ContentSha256);

internal sealed record ToolInvocationTerminalJournalFact(
    ToolInvocationTerminalFact Invocation,
    ToolResultItemFact ResultItem,
    string RequestSha256);

internal sealed record DeferredToolsActivatedFact(
    Guid TurnId,
    IReadOnlyList<ToolDefinitionId> ToolDefinitionIds,
    string RequestSha256);

internal sealed record AgentInvocationSnapshotRecordedFact(
    Guid TurnId,
    AgentInvocationSnapshot Snapshot,
    string RequestSha256);

internal sealed record ProviderUsageRecordedFact(
    Guid TurnId,
    ProviderUsageSnapshot Usage,
    string RequestSha256);

internal sealed record CompactionCheckpointRecordedFact(
    Guid TurnId,
    CompactionCheckpointSnapshot Checkpoint,
    string RequestSha256);
