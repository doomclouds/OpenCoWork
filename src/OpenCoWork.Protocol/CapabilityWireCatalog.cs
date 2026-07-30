using OpenCoWork.Abstractions;

namespace OpenCoWork.Protocol;

internal static class CapabilityWireCatalog
{
    [OpenCoWorkWireMethod("plugin/install", OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.CapabilityOwner, OpenCoWorkWire.CapabilityVersion,
        typeof(WireCapabilityOperationRequest),
        typeof(WireCapabilityOperationResponse), OpenCoWorkWire.WorkspaceAuthority,
        true, OpenCoWorkWire.NoIdempotency)]
    private static void PluginInstall()
    {
    }

    [OpenCoWorkWireMethod("plugin/remove", OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.CapabilityOwner, OpenCoWorkWire.CapabilityVersion,
        typeof(WireCapabilityOperationRequest),
        typeof(WireCapabilityOperationResponse), OpenCoWorkWire.WorkspaceAuthority,
        true, OpenCoWorkWire.NoIdempotency)]
    private static void PluginRemove()
    {
    }

    [OpenCoWorkWireMethod("plugin/setEnabled", OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.CapabilityOwner, OpenCoWorkWire.CapabilityVersion,
        typeof(WireCapabilityOperationRequest),
        typeof(WireCapabilityOperationResponse), OpenCoWorkWire.WorkspaceAuthority,
        true, OpenCoWorkWire.NoIdempotency)]
    private static void PluginSetEnabled()
    {
    }

    [OpenCoWorkWireMethod("skill/read", OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.CapabilityOwner, OpenCoWorkWire.CapabilityVersion,
        typeof(WireCapabilityOperationRequest),
        typeof(WireCapabilityOperationResponse), OpenCoWorkWire.WorkspaceAuthority,
        false, OpenCoWorkWire.NoIdempotency)]
    private static void SkillRead()
    {
    }

    [OpenCoWorkWireMethod("skill/selectVariant", OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.CapabilityOwner, OpenCoWorkWire.CapabilityVersion,
        typeof(WireCapabilityOperationRequest),
        typeof(WireCapabilityOperationResponse), OpenCoWorkWire.WorkspaceAuthority,
        true, OpenCoWorkWire.NoIdempotency)]
    private static void SkillSelectVariant()
    {
    }

    [OpenCoWorkWireMethod("trust/decide", OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.CapabilityOwner, OpenCoWorkWire.CapabilityVersion,
        typeof(WireCapabilityOperationRequest),
        typeof(WireCapabilityOperationResponse), OpenCoWorkWire.WorkspaceAuthority,
        true, OpenCoWorkWire.NoIdempotency)]
    private static void TrustDecide()
    {
    }

    [OpenCoWorkWireMethod("trust/revoke", OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.CapabilityOwner, OpenCoWorkWire.CapabilityVersion,
        typeof(WireCapabilityOperationRequest),
        typeof(WireCapabilityOperationResponse), OpenCoWorkWire.WorkspaceAuthority,
        true, OpenCoWorkWire.NoIdempotency)]
    private static void TrustRevoke()
    {
    }

    [OpenCoWorkWireMethod("mcp/resource/list", OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.CapabilityOwner, OpenCoWorkWire.CapabilityVersion,
        typeof(WireCapabilityOperationRequest),
        typeof(WireCapabilityOperationResponse), OpenCoWorkWire.WorkspaceAuthority,
        false, OpenCoWorkWire.NoIdempotency)]
    private static void McpResourceList()
    {
    }

    [OpenCoWorkWireMethod("mcp/resource/read", OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.CapabilityOwner, OpenCoWorkWire.CapabilityVersion,
        typeof(WireCapabilityOperationRequest),
        typeof(WireCapabilityOperationResponse), OpenCoWorkWire.WorkspaceAuthority,
        false, OpenCoWorkWire.NoIdempotency)]
    private static void McpResourceRead()
    {
    }

    [OpenCoWorkWireMethod("mcp/restart", OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.CapabilityOwner, OpenCoWorkWire.CapabilityVersion,
        typeof(WireCapabilityOperationRequest),
        typeof(WireCapabilityOperationResponse), OpenCoWorkWire.WorkspaceAuthority,
        true, OpenCoWorkWire.NoIdempotency)]
    private static void McpRestart()
    {
    }

    [OpenCoWorkWireMethod("lsp/request", OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.CapabilityOwner, OpenCoWorkWire.CapabilityVersion,
        typeof(WireCapabilityOperationRequest),
        typeof(WireCapabilityOperationResponse), OpenCoWorkWire.WorkspaceAuthority,
        false, OpenCoWorkWire.NoIdempotency)]
    private static void LspRequest()
    {
    }

    [OpenCoWorkWireMethod("lsp/restart", OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.CapabilityOwner, OpenCoWorkWire.CapabilityVersion,
        typeof(WireCapabilityOperationRequest),
        typeof(WireCapabilityOperationResponse), OpenCoWorkWire.WorkspaceAuthority,
        true, OpenCoWorkWire.NoIdempotency)]
    private static void LspRestart()
    {
    }

    [OpenCoWorkWireMethod("auth/secret/set", OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.CapabilityOwner, OpenCoWorkWire.CapabilityVersion,
        typeof(WireCapabilityOperationRequest),
        typeof(WireCapabilityOperationResponse), OpenCoWorkWire.WorkspaceAuthority,
        true, OpenCoWorkWire.NoIdempotency)]
    private static void AuthSecretSet()
    {
    }

    [OpenCoWorkWireMethod("auth/secret/clear", OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.CapabilityOwner, OpenCoWorkWire.CapabilityVersion,
        typeof(WireCapabilityOperationRequest),
        typeof(WireCapabilityOperationResponse), OpenCoWorkWire.WorkspaceAuthority,
        true, OpenCoWorkWire.NoIdempotency)]
    private static void AuthSecretClear()
    {
    }

    [OpenCoWorkWireMethod("sourceControl/inspect", OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.CapabilityOwner, OpenCoWorkWire.CapabilityVersion,
        typeof(WireCapabilityOperationRequest),
        typeof(WireCapabilityOperationResponse), OpenCoWorkWire.WorkspaceAuthority,
        false, OpenCoWorkWire.NoIdempotency)]
    private static void SourceControlInspect()
    {
    }

    [OpenCoWorkWireMethod("sourceControl/status", OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.CapabilityOwner, OpenCoWorkWire.CapabilityVersion,
        typeof(WireCapabilityOperationRequest),
        typeof(WireCapabilityOperationResponse), OpenCoWorkWire.WorkspaceAuthority,
        false, OpenCoWorkWire.NoIdempotency)]
    private static void SourceControlStatus()
    {
    }

    [OpenCoWorkWireMethod("sourceControl/diff", OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.CapabilityOwner, OpenCoWorkWire.CapabilityVersion,
        typeof(WireCapabilityOperationRequest),
        typeof(WireCapabilityOperationResponse), OpenCoWorkWire.WorkspaceAuthority,
        false, OpenCoWorkWire.NoIdempotency)]
    private static void SourceControlDiff()
    {
    }

    [OpenCoWorkWireMethod("sourceControl/log", OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.CapabilityOwner, OpenCoWorkWire.CapabilityVersion,
        typeof(WireCapabilityOperationRequest),
        typeof(WireCapabilityOperationResponse), OpenCoWorkWire.WorkspaceAuthority,
        false, OpenCoWorkWire.NoIdempotency)]
    private static void SourceControlLog()
    {
    }

    [OpenCoWorkWireMethod("sourceControl/show", OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.CapabilityOwner, OpenCoWorkWire.CapabilityVersion,
        typeof(WireCapabilityOperationRequest),
        typeof(WireCapabilityOperationResponse), OpenCoWorkWire.WorkspaceAuthority,
        false, OpenCoWorkWire.NoIdempotency)]
    private static void SourceControlShow()
    {
    }

    [OpenCoWorkWireMethod("terminal/start", OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.CapabilityOwner, OpenCoWorkWire.CapabilityVersion,
        typeof(WireThreadCapabilityOperationRequest),
        typeof(WireCapabilityOperationResponse), OpenCoWorkWire.ThreadAuthority,
        true, OpenCoWorkWire.NoIdempotency)]
    private static void TerminalStart()
    {
    }

    [OpenCoWorkWireMethod("terminal/list", OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.CapabilityOwner, OpenCoWorkWire.CapabilityVersion,
        typeof(WireThreadCapabilityOperationRequest),
        typeof(WireCapabilityOperationResponse), OpenCoWorkWire.ThreadAuthority,
        false, OpenCoWorkWire.NoIdempotency)]
    private static void TerminalList()
    {
    }

    [OpenCoWorkWireMethod("terminal/read", OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.CapabilityOwner, OpenCoWorkWire.CapabilityVersion,
        typeof(WireThreadCapabilityOperationRequest),
        typeof(WireCapabilityOperationResponse), OpenCoWorkWire.ThreadAuthority,
        false, OpenCoWorkWire.NoIdempotency)]
    private static void TerminalRead()
    {
    }

    [OpenCoWorkWireMethod("terminal/write", OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.CapabilityOwner, OpenCoWorkWire.CapabilityVersion,
        typeof(WireThreadCapabilityOperationRequest),
        typeof(WireCapabilityOperationResponse), OpenCoWorkWire.ThreadAuthority,
        true, OpenCoWorkWire.NoIdempotency)]
    private static void TerminalWrite()
    {
    }

    [OpenCoWorkWireMethod("terminal/stop", OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.CapabilityOwner, OpenCoWorkWire.CapabilityVersion,
        typeof(WireThreadCapabilityOperationRequest),
        typeof(WireCapabilityOperationResponse), OpenCoWorkWire.ThreadAuthority,
        true, OpenCoWorkWire.NoIdempotency)]
    private static void TerminalStop()
    {
    }

    [OpenCoWorkWireMethod("terminal/release", OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.CapabilityOwner, OpenCoWorkWire.CapabilityVersion,
        typeof(WireThreadCapabilityOperationRequest),
        typeof(WireCapabilityOperationResponse), OpenCoWorkWire.ThreadAuthority,
        true, OpenCoWorkWire.NoIdempotency)]
    private static void TerminalRelease()
    {
    }

    [OpenCoWorkWireMethod("memory/list", OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.CapabilityOwner, OpenCoWorkWire.CapabilityVersion,
        typeof(WireCapabilityOperationRequest),
        typeof(WireCapabilityOperationResponse), OpenCoWorkWire.WorkspaceAuthority,
        false, OpenCoWorkWire.NoIdempotency)]
    private static void MemoryList()
    {
    }

    [OpenCoWorkWireMethod("memory/search", OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.CapabilityOwner, OpenCoWorkWire.CapabilityVersion,
        typeof(WireCapabilityOperationRequest),
        typeof(WireCapabilityOperationResponse), OpenCoWorkWire.WorkspaceAuthority,
        false, OpenCoWorkWire.NoIdempotency)]
    private static void MemorySearch()
    {
    }

    [OpenCoWorkWireMethod("memory/read", OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.CapabilityOwner, OpenCoWorkWire.CapabilityVersion,
        typeof(WireCapabilityOperationRequest),
        typeof(WireCapabilityOperationResponse), OpenCoWorkWire.WorkspaceAuthority,
        false, OpenCoWorkWire.NoIdempotency)]
    private static void MemoryRead()
    {
    }

    [OpenCoWorkWireMethod("memory/write", OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.CapabilityOwner, OpenCoWorkWire.CapabilityVersion,
        typeof(WireCapabilityOperationRequest),
        typeof(WireCapabilityOperationResponse), OpenCoWorkWire.WorkspaceAuthority,
        true, OpenCoWorkWire.NoIdempotency)]
    private static void MemoryWrite()
    {
    }

    [OpenCoWorkWireMethod("memory/archive", OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.CapabilityOwner, OpenCoWorkWire.CapabilityVersion,
        typeof(WireCapabilityOperationRequest),
        typeof(WireCapabilityOperationResponse), OpenCoWorkWire.WorkspaceAuthority,
        true, OpenCoWorkWire.NoIdempotency)]
    private static void MemoryArchive()
    {
    }

    [OpenCoWorkWireMethod("tool/dynamic/register", OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.CapabilityOwner, OpenCoWorkWire.CapabilityVersion,
        typeof(WireDynamicToolRegisterRequest),
        typeof(WireDynamicToolRegistrationResponse), OpenCoWorkWire.ThreadAuthority,
        true, OpenCoWorkWire.NoIdempotency)]
    private static void DynamicToolRegister()
    {
    }

    [OpenCoWorkWireMethod("tool/dynamic/renew", OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.CapabilityOwner, OpenCoWorkWire.CapabilityVersion,
        typeof(WireDynamicToolRenewRequest),
        typeof(WireDynamicToolRegistrationResponse), OpenCoWorkWire.ThreadAuthority,
        true, OpenCoWorkWire.NoIdempotency)]
    private static void DynamicToolRenew()
    {
    }

    [OpenCoWorkWireMethod("tool/dynamic/unregister", OpenCoWorkWire.ClientToServer,
        OpenCoWorkWire.CapabilityOwner, OpenCoWorkWire.CapabilityVersion,
        typeof(WireDynamicToolUnregisterRequest), typeof(WireAcknowledgement),
        OpenCoWorkWire.ThreadAuthority, true, OpenCoWorkWire.NoIdempotency)]
    private static void DynamicToolUnregister()
    {
    }

    [OpenCoWorkWireMethod("tool/invoke", OpenCoWorkWire.ServerToClient,
        OpenCoWorkWire.CapabilityOwner, OpenCoWorkWire.CapabilityVersion,
        typeof(WireToolInvokeRequest), typeof(WireToolInvokeResponse),
        OpenCoWorkWire.ConnectionAuthority, false, OpenCoWorkWire.NoIdempotency)]
    private static void ToolInvoke()
    {
    }

    [OpenCoWorkWireMethod("capability/changed", OpenCoWorkWire.ServerToClient,
        OpenCoWorkWire.CapabilityOwner, OpenCoWorkWire.CapabilityVersion,
        typeof(WireCapabilityChangedNotification), typeof(WireEmpty),
        OpenCoWorkWire.ConnectionAuthority, false, OpenCoWorkWire.NoIdempotency)]
    private static void CapabilityChanged()
    {
    }
}
