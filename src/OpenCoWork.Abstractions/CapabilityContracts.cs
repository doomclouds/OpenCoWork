using System.Text.Json;

namespace OpenCoWork.Abstractions;

public enum CapabilityKind
{
    Plugin,
    Skill,
    Tool,
    Provider,
    Model,
    AuthProfile,
    McpServer,
    LspServer,
    Hook,
}

public enum CapabilitySourceKind
{
    Core,
    User,
    Workspace,
    Thread,
    Plugin,
    Mcp,
    Lsp,
    RuntimeDynamic,
    Conflict,
}

public enum CapabilityStatus
{
    Ready,
    Disabled,
    PendingTrust,
    Starting,
    Authenticating,
    Unavailable,
    Disconnected,
    Faulted,
    Conflict,
}

public enum CapabilityRuntimeState
{
    Stopped,
    Starting,
    Ready,
    Degraded,
    Stopping,
    Faulted,
}

public enum CapabilityTrustScope
{
    PromptContribution,
    OutOfProcess,
    InProcessCode,
    TrustedHook,
}

public static class CapabilityErrorCodes
{
    public const string DefinitionInvalid = "capability.definitionInvalid";
    public const string Conflict = "capability.conflict";
    public const string CursorInvalid = "capability.cursorInvalid";
    public const string NotFound = "capability.notFound";
    public const string RevisionConflict = "capability.revisionConflict";
    public const string RuntimeUnavailable = "capability.runtimeUnavailable";
    public const string PersistenceInvalid = "capability.persistenceInvalid";
    public const string PersistenceUnavailable = "capability.persistenceUnavailable";
}

public sealed record CapabilitySourceDescriptor
{
    public CapabilitySourceDescriptor(
        CapabilitySourceKind kind,
        string id,
        string? version,
        string sha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (version is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(version);
        }

        if (!IsSha256(sha256))
        {
            throw new ArgumentException(
                "Capability source SHA-256 must contain 64 lowercase hexadecimal characters.",
                nameof(sha256));
        }

        Kind = kind;
        Id = id;
        Version = version;
        Sha256 = sha256;
    }

    public CapabilitySourceKind Kind { get; }

    public string Id { get; }

    public string? Version { get; }

    public string Sha256 { get; }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed class CapabilityContribution
{
    public CapabilityContribution(
        CapabilityKind kind,
        string id,
        string displayName,
        string description,
        CapabilityStatus status,
        IEnumerable<CapabilityTrustScope> requiredTrustScopes,
        long generation,
        IEnumerable<string> diagnosticCodes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(requiredTrustScopes);
        ArgumentOutOfRangeException.ThrowIfNegative(generation);
        ArgumentNullException.ThrowIfNull(diagnosticCodes);

        var diagnostics = diagnosticCodes.ToArray();
        if (diagnostics.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "Capability diagnostic codes cannot be empty.",
                nameof(diagnosticCodes));
        }

        Kind = kind;
        Id = id;
        DisplayName = displayName;
        Description = description;
        Status = status;
        RequiredTrustScopes = Array.AsReadOnly(requiredTrustScopes.Distinct().ToArray());
        Generation = generation;
        DiagnosticCodes = Array.AsReadOnly(
            diagnostics.Distinct(StringComparer.Ordinal).ToArray());
    }

    public CapabilityKind Kind { get; }

    public string Id { get; }

    public string DisplayName { get; }

    public string Description { get; }

    public CapabilityStatus Status { get; }

    public IReadOnlyList<CapabilityTrustScope> RequiredTrustScopes { get; }

    public long Generation { get; }

    public IReadOnlyList<string> DiagnosticCodes { get; }
}

public sealed class CapabilityContributionSet
{
    public CapabilityContributionSet(
        CapabilitySourceDescriptor source,
        IEnumerable<CapabilityContribution> items)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        ArgumentNullException.ThrowIfNull(items);
        Items = Array.AsReadOnly(items.ToArray());
        if (Items.Any(item => item is null))
        {
            throw new ArgumentException(
                "Capability contribution items cannot contain null.",
                nameof(items));
        }
    }

    public CapabilitySourceDescriptor Source { get; }

    public IReadOnlyList<CapabilityContribution> Items { get; }
}

public sealed class CapabilityCatalogItem
{
    public CapabilityCatalogItem(
        CapabilityKind kind,
        string id,
        string displayName,
        string description,
        CapabilitySourceDescriptor source,
        CapabilityStatus status,
        IEnumerable<CapabilityTrustScope> requiredTrustScopes,
        long generation,
        IEnumerable<string> diagnosticCodes,
        IEnumerable<CapabilitySourceDescriptor>? conflictingSources = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(requiredTrustScopes);
        ArgumentOutOfRangeException.ThrowIfNegative(generation);
        ArgumentNullException.ThrowIfNull(diagnosticCodes);

        Kind = kind;
        Id = id;
        DisplayName = displayName;
        Description = description;
        Source = source;
        Status = status;
        RequiredTrustScopes = Array.AsReadOnly(requiredTrustScopes.ToArray());
        Generation = generation;
        DiagnosticCodes = Array.AsReadOnly(diagnosticCodes.ToArray());
        ConflictingSources = Array.AsReadOnly((conflictingSources ?? []).ToArray());
    }

    public CapabilityKind Kind { get; }

    public string Id { get; }

    public string DisplayName { get; }

    public string Description { get; }

    public CapabilitySourceDescriptor Source { get; }

    public CapabilityStatus Status { get; }

    public IReadOnlyList<CapabilityTrustScope> RequiredTrustScopes { get; }

    public long Generation { get; }

    public IReadOnlyList<string> DiagnosticCodes { get; }

    public IReadOnlyList<CapabilitySourceDescriptor> ConflictingSources { get; }
}

public sealed class CapabilityCatalog
{
    public CapabilityCatalog(
        int schemaVersion,
        long revision,
        string catalogSha256,
        CapabilityRuntimeState runtimeState,
        IEnumerable<CapabilityCatalogItem> items)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(schemaVersion);
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogSha256);
        ArgumentNullException.ThrowIfNull(items);

        SchemaVersion = schemaVersion;
        Revision = revision;
        CatalogSha256 = catalogSha256;
        RuntimeState = runtimeState;
        Items = Array.AsReadOnly(items.ToArray());
    }

    public int SchemaVersion { get; }

    public long Revision { get; }

    public string CatalogSha256 { get; }

    public CapabilityRuntimeState RuntimeState { get; }

    public IReadOnlyList<CapabilityCatalogItem> Items { get; }
}

public sealed record CapabilityCatalogQuery(int Limit = 50, string? Cursor = null);

public sealed record CapabilityCatalogPage(
    int SchemaVersion,
    long Revision,
    string CatalogSha256,
    CapabilityRuntimeState RuntimeState,
    IReadOnlyList<CapabilityCatalogItem> Items,
    string? NextCursor);

public sealed record CapabilityIdentity(CapabilityKind Kind, string Id);

public sealed record CapabilityCatalogEntry(
    long Revision,
    CapabilityCatalogItem Item);

public sealed record CapabilitySetEnabledRequest(
    CapabilityKind Kind,
    string Id,
    bool Enabled,
    long ExpectedRevision);

public sealed record CapabilityCatalogChange(
    long Revision,
    CapabilityRuntimeState RuntimeState,
    bool Changed);

public sealed record CapabilityCatalogChangedEventArgs(
    long Revision,
    CapabilityRuntimeState RuntimeState);

public sealed record CapabilityDomainRequest(
    string Operation,
    JsonElement Arguments,
    Guid ConnectionId);

public sealed record CapabilityDomainResult(
    JsonElement Result,
    long? Revision = null);

public sealed record CapabilityDynamicToolDefinition(
    string Name,
    string Description,
    JsonElement InputSchema,
    ToolEffect Effects,
    ToolReplaySafety ReplaySafety);

public sealed record CapabilityDynamicToolRegistrationRequest(
    Guid ThreadId,
    Guid RegistrationId,
    CapabilityDynamicToolDefinition Definition,
    string DefinitionSha256,
    TimeSpan? LeaseDuration = null);

public sealed record CapabilityDynamicToolRegistration(
    Guid ConnectionId,
    Guid ThreadId,
    Guid RegistrationId,
    string DefinitionSha256,
    CapabilityStatus Status,
    string RuntimeBindingId,
    DateTimeOffset ExpiresAt);

public sealed class CapabilityServiceException(
    string code,
    string message,
    bool isRetryable = false,
    long? currentRevision = null,
    long? currentGeneration = null,
    long? currentVersion = null) : InvalidOperationException(message)
{
    public string Code { get; } = code;

    public bool IsRetryable { get; } = isRetryable;

    public long? CurrentRevision { get; } = currentRevision;

    public long? CurrentGeneration { get; } = currentGeneration;

    public long? CurrentVersion { get; } = currentVersion;
}

public interface ICapabilityService
{
    event EventHandler<CapabilityCatalogChangedEventArgs>? CatalogChanged;

    ValueTask<CapabilityCatalogPage> GetCatalogAsync(
        CapabilityCatalogQuery query,
        CancellationToken cancellationToken = default);

    ValueTask<CapabilityCatalogEntry> ReadAsync(
        CapabilityIdentity identity,
        CancellationToken cancellationToken = default);

    ValueTask<CapabilityCatalogChange> RefreshAsync(
        long expectedRevision,
        CancellationToken cancellationToken = default);

    ValueTask<CapabilityCatalogChange> SetEnabledAsync(
        CapabilitySetEnabledRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<CapabilityDomainResult> ExecuteDomainAsync(
        CapabilityDomainRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<CapabilityDynamicToolRegistration> RegisterDynamicToolAsync(
        Guid connectionId,
        CapabilityDynamicToolRegistrationRequest request,
        ToolExecutor executor,
        CancellationToken cancellationToken = default);

    ValueTask<CapabilityDynamicToolRegistration> RenewDynamicToolAsync(
        Guid connectionId,
        Guid threadId,
        Guid registrationId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    ValueTask UnregisterDynamicToolAsync(
        Guid connectionId,
        Guid threadId,
        Guid registrationId,
        CancellationToken cancellationToken = default);

    void DisconnectDynamicTools(Guid connectionId);
}
