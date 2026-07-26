using System.Text.Json;
using OpenCoWork.Abstractions;

namespace OpenCoWork.Core.Sessions;

internal sealed record ThreadCreatedFact(
    string DisplayName,
    HistoryMode HistoryMode,
    string? FirstUserMessage,
    string RequestSha256);

internal sealed record ThreadRenamedFact(
    string DisplayName,
    string RequestSha256);

internal sealed record ThreadStateFact(string RequestSha256);

internal sealed record TurnQueuedFact(
    Guid QueueItemId,
    string Text,
    int Position,
    string RequestSha256);

internal sealed record TurnQueueChangedFact(
    IReadOnlyList<Guid> QueueItemIds,
    string RequestSha256);

internal sealed record TurnStartedFact(
    Guid TurnId,
    string RequestSha256);

internal sealed record TurnWaitingFact(
    Guid TurnId,
    Guid InteractionId,
    Guid ItemId,
    SessionInteractionType InteractionType,
    JsonElement Request,
    SessionExecutionCheckpoint Checkpoint,
    DateTimeOffset? TimeoutAt,
    string RequestSha256);

internal sealed record InteractionResolvedFact(
    Guid InteractionId,
    JsonElement Resolution,
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
