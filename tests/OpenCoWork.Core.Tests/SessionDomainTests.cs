using OpenCoWork.Abstractions;
using OpenCoWork.Core.Sessions;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class SessionDomainTests
{
    [Fact]
    public void Thread_allows_only_frozen_lifecycle_transitions()
    {
        var now = DateTimeOffset.UtcNow;
        var thread = SessionThreadState.Create(
            Guid.CreateVersion7(),
            "thread",
            HistoryMode.Server,
            now);

        thread.Pause(now.AddSeconds(1));
        Assert.Equal(ThreadStatus.Paused, thread.Status);

        thread.Resume(now.AddSeconds(2));
        var turn = thread.StartTurn(Guid.CreateVersion7(), now.AddSeconds(3));

        Assert.Equal(ThreadStatus.Active, thread.Status);
        Assert.Equal(turn.TurnId, thread.ActiveTurn?.TurnId);
        Assert.Throws<SessionStateException>(() => thread.Pause(now.AddSeconds(4)));
        Assert.Throws<SessionStateException>(() => thread.Archive(now.AddSeconds(4)));

        turn.TransitionTo(TurnStatus.Completed, now.AddSeconds(5));
        thread.EndTurn(turn, now.AddSeconds(5));
        thread.Archive(now.AddSeconds(6));
        Assert.Equal(ThreadStatus.Archived, thread.Status);

        thread.Unarchive(now.AddSeconds(7));
        Assert.Equal(ThreadStatus.Active, thread.Status);
        Assert.Throws<SessionStateException>(() => thread.Unarchive(now.AddSeconds(8)));
    }

    [Fact]
    public void Turn_waiting_resume_and_terminal_states_are_strict()
    {
        var now = DateTimeOffset.UtcNow;
        var turn = SessionTurnState.Start(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            now);

        turn.TransitionTo(TurnStatus.WaitingApproval, now.AddSeconds(1));
        turn.TransitionTo(TurnStatus.Running, now.AddSeconds(2));
        turn.TransitionTo(TurnStatus.WaitingInput, now.AddSeconds(3));
        turn.TransitionTo(TurnStatus.Cancelled, now.AddSeconds(4));

        Assert.Equal(TurnStatus.Cancelled, turn.Status);
        Assert.Throws<SessionStateException>(
            () => turn.TransitionTo(TurnStatus.Running, now.AddSeconds(5)));
    }

    [Fact]
    public void Item_streaming_and_terminal_states_are_strict()
    {
        var now = DateTimeOffset.UtcNow;
        var streaming = SessionItemState.Start(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            SessionItemType.AgentMessage,
            now);
        streaming.TransitionTo(SessionItemStatus.Streaming, now.AddSeconds(1));
        streaming.TransitionTo(SessionItemStatus.Completed, now.AddSeconds(2));

        var nonStreaming = SessionItemState.Start(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            SessionItemType.UserMessage,
            now);
        nonStreaming.TransitionTo(SessionItemStatus.Completed, now.AddSeconds(1));

        Assert.Throws<SessionStateException>(
            () => streaming.TransitionTo(SessionItemStatus.Failed, now.AddSeconds(3)));
        Assert.Throws<SessionStateException>(
            () => nonStreaming.TransitionTo(SessionItemStatus.Streaming, now.AddSeconds(2)));
    }
}
