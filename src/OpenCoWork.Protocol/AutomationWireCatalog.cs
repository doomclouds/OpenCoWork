using OpenCoWork.Abstractions;

namespace OpenCoWork.Protocol;

internal static class AutomationWireCatalog
{
    [OpenCoWorkWireMethod(
        "automation/changed", OpenCoWorkWire.ServerToClient, "automation",
        OpenCoWorkWire.AutomationVersion,
        typeof(WireAutomationChangedNotification), typeof(WireEmpty),
        OpenCoWorkWire.ConnectionAuthority, false, OpenCoWorkWire.NoIdempotency)]
    private static void AutomationChanged()
    {
    }

    [OpenCoWorkWireMethod(
        "schedule/changed", OpenCoWorkWire.ServerToClient, "schedule",
        OpenCoWorkWire.AutomationVersion,
        typeof(WireAutomationChangedNotification), typeof(WireEmpty),
        OpenCoWorkWire.ConnectionAuthority, false, OpenCoWorkWire.NoIdempotency)]
    private static void ScheduleChanged()
    {
    }

    [OpenCoWorkWireMethod(
        "automationRun/changed", OpenCoWorkWire.ServerToClient, "automationRun",
        OpenCoWorkWire.AutomationVersion,
        typeof(WireAutomationChangedNotification), typeof(WireEmpty),
        OpenCoWorkWire.ConnectionAuthority, false, OpenCoWorkWire.NoIdempotency)]
    private static void AutomationRunChanged()
    {
    }
}
