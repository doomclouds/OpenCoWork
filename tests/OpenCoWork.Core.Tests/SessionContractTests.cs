using System.Reflection;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Configuration;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class SessionContractTests
{
    [Fact]
    public void M2_session_contracts_have_frozen_names_defaults_and_public_boundary()
    {
        var config = new SessionConfig();
        var id = Guid.CreateVersion7();
        var result = new SessionCommandResult<string>(
            SessionCommandStatus.Committed,
            "value",
            Sequence: 1,
            CurrentSequence: null,
            Error: null);

        Assert.Equal(
            "session",
            typeof(SessionConfig).GetCustomAttribute<ConfigSectionAttribute>()?.Name);
        Assert.Equal(256, config.EventBufferCapacity);
        Assert.Equal(TimeSpan.FromMilliseconds(50), config.StreamFlushInterval);
        Assert.Equal(8192, config.StreamFlushBytes);
        Assert.Equal(7, id.Version);
        Assert.Equal(SessionCommandStatus.Committed, result.Status);
        Assert.Equal(1, result.Sequence);

        var serviceMethods = typeof(ISessionService)
            .GetMethods()
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Contains(nameof(ISessionService.CreateThreadAsync), serviceMethods);
        Assert.Contains(nameof(ISessionService.GetThreadAsync), serviceMethods);
        Assert.Contains(nameof(ISessionService.SetAgentModeAsync), serviceMethods);
        Assert.Contains(nameof(ISessionService.SetThreadModelAsync), serviceMethods);
        Assert.Contains(nameof(ISessionService.SubscribeAsync), serviceMethods);
        Assert.Contains(nameof(ISessionService.RollbackThreadAsync), serviceMethods);

        Assert.All(
            typeof(ThreadSnapshot).GetProperties(),
            property => Assert.False(property.CanWrite));
        Assert.Equal(
            AgentMode.Agent,
            new CreateThreadRequest(id, ExpectedSequence: 0).AgentMode);
    }

    [Fact]
    public void M2_item_and_state_enums_match_the_frozen_closed_sets()
    {
        Assert.Equal(
            [
                ThreadStatus.Active,
                ThreadStatus.Paused,
                ThreadStatus.Archived,
            ],
            Enum.GetValues<ThreadStatus>());
        Assert.Equal(
            [
                TurnStatus.Running,
                TurnStatus.WaitingApproval,
                TurnStatus.WaitingInput,
                TurnStatus.Completed,
                TurnStatus.Failed,
                TurnStatus.Cancelled,
            ],
            Enum.GetValues<TurnStatus>());
        Assert.Equal(
            [
                SessionItemType.UserMessage,
                SessionItemType.AgentMessage,
                SessionItemType.Reasoning,
                SessionItemType.ApprovalRequest,
                SessionItemType.ApprovalResponse,
                SessionItemType.UserInputRequest,
                SessionItemType.UserInputResponse,
                SessionItemType.Error,
                SessionItemType.SystemNotice,
                SessionItemType.ToolCall,
                SessionItemType.ToolResult,
            ],
            Enum.GetValues<SessionItemType>());
        Assert.Equal(
            [
                SessionItemStatus.Started,
                SessionItemStatus.Streaming,
                SessionItemStatus.Completed,
                SessionItemStatus.Failed,
                SessionItemStatus.Cancelled,
            ],
            Enum.GetValues<SessionItemStatus>());
    }

    [Fact]
    public void M4_session_contracts_add_tool_items_events_intents_and_approval_correlation()
    {
        Assert.Contains(SessionEventType.ToolCallRecorded, Enum.GetValues<SessionEventType>());
        Assert.Contains(SessionEventType.ToolInvocationStarted, Enum.GetValues<SessionEventType>());
        Assert.Contains(
            SessionEventType.ToolInvocationAttemptStarted,
            Enum.GetValues<SessionEventType>());
        Assert.Contains(SessionEventType.ToolInvocationTerminal, Enum.GetValues<SessionEventType>());

        Assert.True(typeof(RecordToolCallIntent).IsSubclassOf(typeof(SessionExecutionIntent)));
        Assert.True(
            typeof(RecordToolInvocationStartedIntent)
                .IsSubclassOf(typeof(SessionExecutionIntent)));
        Assert.True(
            typeof(RecordToolInvocationAttemptStartedIntent)
                .IsSubclassOf(typeof(SessionExecutionIntent)));
        Assert.True(
            typeof(RecordToolInvocationTerminalIntent)
                .IsSubclassOf(typeof(SessionExecutionIntent)));

        var interaction = new PendingInteractionSnapshot(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            SessionInteractionType.Approval,
            IsResolved: false,
            DateTimeOffset.UtcNow,
            TimeoutAt: null);
        Assert.Null(interaction.ToolInvocationId);
        Assert.Contains(
            typeof(ToolInvocationSnapshot),
            typeof(SessionEventPayload).GetProperties().Select(property => property.PropertyType));
    }
}
