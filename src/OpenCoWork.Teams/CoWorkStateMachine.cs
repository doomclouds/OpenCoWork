using OpenCoWork.Abstractions;

namespace OpenCoWork.Teams;

internal static class CoWorkStateMachine
{
    public static bool CanTransition(
        CoWorkMissionStatus from,
        CoWorkMissionStatus to) =>
        (from, to) switch
        {
            (CoWorkMissionStatus.Planning, CoWorkMissionStatus.Active or
                CoWorkMissionStatus.Cancelled) => true,
            (CoWorkMissionStatus.Active, CoWorkMissionStatus.AwaitingLeaderReview or
                CoWorkMissionStatus.Failed or CoWorkMissionStatus.Cancelled) => true,
            (CoWorkMissionStatus.AwaitingLeaderReview, CoWorkMissionStatus.Active or
                CoWorkMissionStatus.Completed or CoWorkMissionStatus.Failed or
                CoWorkMissionStatus.Cancelled) => true,
            _ => false,
        };

    public static bool CanTransition(
        CoWorkTaskStatus from,
        CoWorkTaskStatus to)
    {
        if (to == CoWorkTaskStatus.Cancelled &&
            from is not (
                CoWorkTaskStatus.Completed or
                CoWorkTaskStatus.Failed or
                CoWorkTaskStatus.Cancelled))
        {
            return true;
        }

        return (from, to) switch
        {
            (CoWorkTaskStatus.Pending, CoWorkTaskStatus.WaitingDependencies or
                CoWorkTaskStatus.Ready) => true,
            (CoWorkTaskStatus.WaitingDependencies, CoWorkTaskStatus.Ready) => true,
            (CoWorkTaskStatus.Ready, CoWorkTaskStatus.Running or
                CoWorkTaskStatus.Blocked) => true,
            (CoWorkTaskStatus.Running, CoWorkTaskStatus.Blocked or
                CoWorkTaskStatus.Review or CoWorkTaskStatus.Completed or
                CoWorkTaskStatus.Failed) => true,
            (CoWorkTaskStatus.Blocked, CoWorkTaskStatus.Ready) => true,
            (CoWorkTaskStatus.Review, CoWorkTaskStatus.Ready or
                CoWorkTaskStatus.Completed) => true,
            (CoWorkTaskStatus.Failed, CoWorkTaskStatus.Ready) => true,
            _ => false,
        };
    }
}
