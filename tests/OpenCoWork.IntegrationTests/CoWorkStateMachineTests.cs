using OpenCoWork.Abstractions;
using OpenCoWork.Teams;
using Xunit;

namespace OpenCoWork.IntegrationTests;

public sealed class CoWorkStateMachineTests
{
    [Fact]
    public void Mission_state_machine_accepts_only_frozen_edges()
    {
        AssertEdges(
            Enum.GetValues<CoWorkMissionStatus>(),
            [
                (CoWorkMissionStatus.Planning, CoWorkMissionStatus.Active),
                (CoWorkMissionStatus.Active, CoWorkMissionStatus.AwaitingLeaderReview),
                (CoWorkMissionStatus.Active, CoWorkMissionStatus.Failed),
                (CoWorkMissionStatus.Active, CoWorkMissionStatus.Cancelled),
                (CoWorkMissionStatus.AwaitingLeaderReview, CoWorkMissionStatus.Active),
                (CoWorkMissionStatus.AwaitingLeaderReview, CoWorkMissionStatus.Completed),
                (CoWorkMissionStatus.AwaitingLeaderReview, CoWorkMissionStatus.Failed),
                (CoWorkMissionStatus.AwaitingLeaderReview, CoWorkMissionStatus.Cancelled),
                (CoWorkMissionStatus.Planning, CoWorkMissionStatus.Cancelled),
            ],
            CoWorkStateMachine.CanTransition);
    }

    [Fact]
    public void Task_state_machine_accepts_only_frozen_edges()
    {
        AssertEdges(
            Enum.GetValues<CoWorkTaskStatus>(),
            [
                (CoWorkTaskStatus.Pending, CoWorkTaskStatus.WaitingDependencies),
                (CoWorkTaskStatus.Pending, CoWorkTaskStatus.Ready),
                (CoWorkTaskStatus.WaitingDependencies, CoWorkTaskStatus.Ready),
                (CoWorkTaskStatus.Ready, CoWorkTaskStatus.Running),
                (CoWorkTaskStatus.Ready, CoWorkTaskStatus.Blocked),
                (CoWorkTaskStatus.Running, CoWorkTaskStatus.Blocked),
                (CoWorkTaskStatus.Running, CoWorkTaskStatus.Review),
                (CoWorkTaskStatus.Running, CoWorkTaskStatus.Completed),
                (CoWorkTaskStatus.Running, CoWorkTaskStatus.Failed),
                (CoWorkTaskStatus.Blocked, CoWorkTaskStatus.Ready),
                (CoWorkTaskStatus.Review, CoWorkTaskStatus.Ready),
                (CoWorkTaskStatus.Review, CoWorkTaskStatus.Completed),
                (CoWorkTaskStatus.Failed, CoWorkTaskStatus.Ready),
            ],
            CoWorkStateMachine.CanTransition);
    }

    private static void AssertEdges<T>(
        IReadOnlyList<T> values,
        IReadOnlyList<(T From, T To)> valid,
        Func<T, T, bool> canTransition)
        where T : struct, Enum
    {
        foreach (var from in values)
        {
            foreach (var to in values)
            {
                var expected = valid.Contains((from, to)) ||
                               !EqualityComparer<T>.Default.Equals(from, to) &&
                               to.ToString() == nameof(CoWorkTaskStatus.Cancelled) &&
                               from.ToString() is not (
                                   nameof(CoWorkTaskStatus.Completed) or
                                   nameof(CoWorkTaskStatus.Failed) or
                                   nameof(CoWorkTaskStatus.Cancelled));
                Assert.Equal(expected, canTransition(from, to));
            }
        }
    }
}
