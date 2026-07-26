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
        Assert.Contains(nameof(ISessionService.SubscribeAsync), serviceMethods);
        Assert.Contains(nameof(ISessionService.RollbackThreadAsync), serviceMethods);

        Assert.All(
            typeof(ThreadSnapshot).GetProperties(),
            property => Assert.False(property.CanWrite));
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
}
