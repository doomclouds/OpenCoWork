using OpenCoWork.Abstractions;

namespace OpenCoWork.Protocol;

internal static class CoWorkWireCatalog
{
    [OpenCoWorkWireMethod(
        "agent/changed", OpenCoWorkWire.ServerToClient, "agent",
        OpenCoWorkWire.CoWorkVersion, typeof(WireCoWorkChangedNotification),
        typeof(WireEmpty), OpenCoWorkWire.ConnectionAuthority, false,
        OpenCoWorkWire.NoIdempotency)]
    private static void AgentChanged()
    {
    }

    [OpenCoWorkWireMethod(
        "subagent/changed", OpenCoWorkWire.ServerToClient, "subagent",
        OpenCoWorkWire.CoWorkVersion, typeof(WireCoWorkChangedNotification),
        typeof(WireEmpty), OpenCoWorkWire.ConnectionAuthority, false,
        OpenCoWorkWire.NoIdempotency)]
    private static void SubAgentChanged()
    {
    }

    [OpenCoWorkWireMethod(
        "team/changed", OpenCoWorkWire.ServerToClient, "team",
        OpenCoWorkWire.CoWorkVersion, typeof(WireCoWorkChangedNotification),
        typeof(WireEmpty), OpenCoWorkWire.ConnectionAuthority, false,
        OpenCoWorkWire.NoIdempotency)]
    private static void TeamChanged()
    {
    }

    [OpenCoWorkWireMethod(
        "mission/changed", OpenCoWorkWire.ServerToClient, "mission",
        OpenCoWorkWire.CoWorkVersion, typeof(WireCoWorkChangedNotification),
        typeof(WireEmpty), OpenCoWorkWire.ConnectionAuthority, false,
        OpenCoWorkWire.NoIdempotency)]
    private static void MissionChanged()
    {
    }

    [OpenCoWorkWireMethod(
        "mailbox/changed", OpenCoWorkWire.ServerToClient, "mailbox",
        OpenCoWorkWire.CoWorkVersion, typeof(WireCoWorkChangedNotification),
        typeof(WireEmpty), OpenCoWorkWire.ConnectionAuthority, false,
        OpenCoWorkWire.NoIdempotency)]
    private static void MailboxChanged()
    {
    }

    [OpenCoWorkWireMethod(
        "artifact/changed", OpenCoWorkWire.ServerToClient, "artifact",
        OpenCoWorkWire.CoWorkVersion, typeof(WireCoWorkChangedNotification),
        typeof(WireEmpty), OpenCoWorkWire.ConnectionAuthority, false,
        OpenCoWorkWire.NoIdempotency)]
    private static void ArtifactChanged()
    {
    }

    [OpenCoWorkWireMethod(
        "worktree/changed", OpenCoWorkWire.ServerToClient, "worktree",
        OpenCoWorkWire.CoWorkVersion, typeof(WireCoWorkChangedNotification),
        typeof(WireEmpty), OpenCoWorkWire.ConnectionAuthority, false,
        OpenCoWorkWire.NoIdempotency)]
    private static void WorktreeChanged()
    {
    }
}
