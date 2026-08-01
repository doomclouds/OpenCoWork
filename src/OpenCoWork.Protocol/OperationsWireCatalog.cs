using OpenCoWork.Abstractions;

namespace OpenCoWork.Protocol;

internal static class OperationsWireCatalog
{
    [OpenCoWorkWireMethod(
        "channel/changed", OpenCoWorkWire.ServerToClient, "channel",
        OpenCoWorkWire.OperationsVersion,
        typeof(WireOperationsChangedNotification), typeof(WireEmpty),
        OpenCoWorkWire.ConnectionAuthority, false, OpenCoWorkWire.NoIdempotency)]
    private static void ChannelChanged()
    {
    }

    [OpenCoWorkWireMethod(
        "heartbeat/changed", OpenCoWorkWire.ServerToClient, "heartbeat",
        OpenCoWorkWire.OperationsVersion,
        typeof(WireOperationsChangedNotification), typeof(WireEmpty),
        OpenCoWorkWire.ConnectionAuthority, false, OpenCoWorkWire.NoIdempotency)]
    private static void HeartbeatChanged()
    {
    }

    [OpenCoWorkWireMethod(
        "insight/changed", OpenCoWorkWire.ServerToClient, "insight",
        OpenCoWorkWire.OperationsVersion,
        typeof(WireOperationsChangedNotification), typeof(WireEmpty),
        OpenCoWorkWire.ConnectionAuthority, false, OpenCoWorkWire.NoIdempotency)]
    private static void InsightChanged()
    {
    }
}
