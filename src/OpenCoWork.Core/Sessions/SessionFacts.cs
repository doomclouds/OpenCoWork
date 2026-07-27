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
    AgentMode AgentMode = AgentMode.Agent);

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
    IReadOnlyList<HistoryCheckpointItemFact> Items);

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
    AgentMode AgentMode = AgentMode.Agent);

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
    string RequestSha256);

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
