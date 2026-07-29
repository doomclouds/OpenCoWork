using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Workspaces;

namespace OpenCoWork.Core.Capabilities;

internal sealed class CapabilityPersistencePaths
{
    private readonly string _userAnchor;
    private readonly string _workspaceAnchor;

    public CapabilityPersistencePaths(
        OpenCoWorkPaths workspacePaths,
        string userProfileDirectory)
    {
        WorkspacePaths = workspacePaths ??
            throw new ArgumentNullException(nameof(workspacePaths));
        ArgumentException.ThrowIfNullOrWhiteSpace(userProfileDirectory);
        UserProfileDirectory = Path.GetFullPath(userProfileDirectory);
        if (!Directory.Exists(UserProfileDirectory))
        {
            throw new DirectoryNotFoundException(
                $"User profile directory does not exist: {UserProfileDirectory}");
        }

        UserOpenCoWorkDirectory = Path.Combine(UserProfileDirectory, ".opencowork");
        UserCapabilitiesPath = Path.Combine(UserOpenCoWorkDirectory, "capabilities.json");
        UserSkillsDirectory = Path.Combine(UserOpenCoWorkDirectory, "skills");
        UserPluginsDirectory = Path.Combine(UserOpenCoWorkDirectory, "plugins");
        PluginStoreDirectory = Path.Combine(UserPluginsDirectory, "store");
        TrustDecisionsPath = Path.Combine(
            UserOpenCoWorkDirectory,
            "trust",
            "decisions.json");
        _workspaceAnchor = Path.Combine(
            WorkspacePaths.WorkspaceRoot,
            ".opencowork-capability-anchor");
        _userAnchor = Path.Combine(
            UserProfileDirectory,
            ".opencowork-capability-anchor");
    }

    public OpenCoWorkPaths WorkspacePaths { get; }

    public string UserProfileDirectory { get; }

    public string UserOpenCoWorkDirectory { get; }

    public string UserCapabilitiesPath { get; }

    public string UserSkillsDirectory { get; }

    public string UserPluginsDirectory { get; }

    public string PluginStoreDirectory { get; }

    public string TrustDecisionsPath { get; }

    internal ResolvedWorkspacePath ResolvePluginLock() =>
        ResolveWorkspace(WorkspacePaths.PluginsLockPath);

    internal ResolvedWorkspacePath ResolveWorkspaceOverrides() =>
        ResolveWorkspace(WorkspacePaths.CapabilitiesPath);

    internal ResolvedWorkspacePath ResolveUserOverrides() =>
        ResolveUser(UserCapabilitiesPath);

    internal ResolvedWorkspacePath ResolveTrustDecisions() =>
        ResolveUser(TrustDecisionsPath);

    internal ResolvedWorkspacePath ResolveWorkspaceSkill(string relativePath) =>
        ResolveWorkspace(Path.Combine(WorkspacePaths.SkillsDirectory, relativePath));

    internal ResolvedWorkspacePath ResolveUserSkill(string relativePath) =>
        ResolveUser(Path.Combine(UserSkillsDirectory, relativePath));

    internal ResolvedWorkspacePath ResolvePluginStore(string sha256) =>
        ResolveUser(Path.Combine(PluginStoreDirectory, sha256));

    private ResolvedWorkspacePath ResolveWorkspace(string path) =>
        WorkspacePathGuard.ResolveContained(
            WorkspacePaths.WorkspaceRoot,
            _workspaceAnchor,
            Path.GetRelativePath(WorkspacePaths.WorkspaceRoot, path));

    private ResolvedWorkspacePath ResolveUser(string path) =>
        WorkspacePathGuard.ResolveContained(
            UserProfileDirectory,
            _userAnchor,
            Path.GetRelativePath(UserProfileDirectory, path));
}

internal sealed record PluginLockEntry(
    string Id,
    string Version,
    string Sha256,
    bool Enabled);

internal sealed record PluginLockDocument(
    int SchemaVersion,
    IReadOnlyList<PluginLockEntry> Plugins)
{
    public static PluginLockDocument Empty { get; } = new(1, []);
}

internal sealed record CapabilityTrustDecision(
    string WorkspacePath,
    CapabilitySourceKind SourceKind,
    string SourceId,
    string? SourceVersion,
    string Sha256,
    IReadOnlyList<CapabilityTrustScope> AllowedScopes,
    IReadOnlyList<CapabilityTrustScope> DeniedScopes)
{
    public bool Matches(
        string workspacePath,
        CapabilitySourceKind sourceKind,
        string sourceId,
        string? sourceVersion,
        string sha256) =>
        PathEquals(
            WorkspacePath,
            CapabilityFileStore.CanonicalWorkspacePath(workspacePath)) &&
        SourceKind == sourceKind &&
        string.Equals(SourceId, sourceId, StringComparison.Ordinal) &&
        string.Equals(SourceVersion, sourceVersion, StringComparison.Ordinal) &&
        string.Equals(Sha256, sha256, StringComparison.Ordinal);

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
}

internal sealed record TrustDecisionsDocument(
    int SchemaVersion,
    IReadOnlyList<CapabilityTrustDecision> Decisions)
{
    public static TrustDecisionsDocument Empty { get; } = new(1, []);
}

internal sealed record DisabledCapability(CapabilityKind Kind, string Id);

internal sealed record SkillVariantOverride(string BaseId, string VariantId);

internal sealed record CapabilityOverridesDocument(
    int SchemaVersion,
    IReadOnlyList<DisabledCapability> Disabled,
    IReadOnlyList<SkillVariantOverride> SkillVariants)
{
    public static CapabilityOverridesDocument Empty { get; } = new(1, [], []);

    public bool IsDisabled(CapabilityKind kind, string id) =>
        Disabled.Any(item =>
            item.Kind == kind &&
            string.Equals(item.Id, id, StringComparison.Ordinal));
}

internal enum CapabilityPersistenceFaultPoint
{
    BeforeReplace,
}

internal sealed class CapabilityPersistenceException : Exception
{
    public CapabilityPersistenceException(
        string code,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

internal sealed class CapabilityFileStore
{
    private const int SchemaVersion = 1;
    private const int MaximumFileBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly Regex PluginIdPattern = new(
        @"^[a-z0-9](?:[a-z0-9.-]{0,62})/[a-z0-9](?:[a-z0-9.-]{0,62})$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex SemVerPattern = new(
        @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-((?:0|[1-9][0-9]*|[0-9]*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[a-zA-Z-][0-9a-zA-Z-]*))*))?(?:\+([0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*))?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private readonly Action<CapabilityPersistenceFaultPoint>? _fault;
    private readonly CapabilityPersistencePaths _paths;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public CapabilityFileStore(
        CapabilityPersistencePaths paths,
        Action<CapabilityPersistenceFaultPoint>? fault = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _fault = fault;
    }

    public Task<PluginLockDocument> LoadPluginLockAsync(
        CancellationToken cancellationToken = default) =>
        LoadAsync(
            _paths.ResolvePluginLock,
            PluginLockDocument.Empty,
            Normalize,
            cancellationToken);

    public Task<TrustDecisionsDocument> LoadTrustDecisionsAsync(
        CancellationToken cancellationToken = default) =>
        LoadAsync(
            _paths.ResolveTrustDecisions,
            TrustDecisionsDocument.Empty,
            Normalize,
            cancellationToken);

    public Task<CapabilityOverridesDocument> LoadWorkspaceOverridesAsync(
        CancellationToken cancellationToken = default) =>
        LoadAsync(
            _paths.ResolveWorkspaceOverrides,
            CapabilityOverridesDocument.Empty,
            Normalize,
            cancellationToken);

    public Task<CapabilityOverridesDocument> LoadUserOverridesAsync(
        CancellationToken cancellationToken = default) =>
        LoadAsync(
            _paths.ResolveUserOverrides,
            CapabilityOverridesDocument.Empty,
            Normalize,
            cancellationToken);

    public Task SavePluginLockAsync(
        PluginLockDocument document,
        CancellationToken cancellationToken = default) =>
        SaveAsync(
            _paths.ResolvePluginLock,
            Normalize(document),
            privateToCurrentUser: false,
            cancellationToken);

    public Task SaveTrustDecisionsAsync(
        TrustDecisionsDocument document,
        CancellationToken cancellationToken = default) =>
        SaveAsync(
            _paths.ResolveTrustDecisions,
            Normalize(document),
            privateToCurrentUser: true,
            cancellationToken);

    public Task SaveWorkspaceOverridesAsync(
        CapabilityOverridesDocument document,
        CancellationToken cancellationToken = default) =>
        SaveAsync(
            _paths.ResolveWorkspaceOverrides,
            Normalize(document),
            privateToCurrentUser: false,
            cancellationToken);

    public Task SaveUserOverridesAsync(
        CapabilityOverridesDocument document,
        CancellationToken cancellationToken = default) =>
        SaveAsync(
            _paths.ResolveUserOverrides,
            Normalize(document),
            privateToCurrentUser: true,
            cancellationToken);

    private static async Task<T> LoadAsync<T>(
        Func<ResolvedWorkspacePath> resolve,
        T empty,
        Func<T, T> normalize,
        CancellationToken cancellationToken)
    {
        try
        {
            var path = resolve();
            if (!File.Exists(path.PhysicalPath))
            {
                return empty;
            }

            var bytes = await ReadBoundedAsync(path.PhysicalPath, cancellationToken);
            using var json = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = JsonOptions.MaxDepth,
                });
            EnsureUniqueProperties(json.RootElement);
            var document = json.RootElement.Deserialize<T>(JsonOptions) ??
                throw Invalid();
            return normalize(document);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CapabilityPersistenceException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or ArgumentException or OverflowException)
        {
            throw Invalid(exception);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw Unavailable(exception);
        }
    }

    private async Task SaveAsync<T>(
        Func<ResolvedWorkspacePath> resolve,
        T document,
        bool privateToCurrentUser,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (bytes.Length + 1 > MaximumFileBytes)
        {
            throw Invalid();
        }

        await _writeLock.WaitAsync(cancellationToken);
        string? temporaryPath = null;
        try
        {
            var destination = resolve();
            var parent = Path.GetDirectoryName(destination.PhysicalPath)
                ?? throw Unavailable();
            Directory.CreateDirectory(parent);
            destination = WorkspacePathGuard.RevalidateForWrite(destination);
            parent = Path.GetDirectoryName(destination.PhysicalPath)
                ?? throw Unavailable();
            temporaryPath = Path.Combine(
                parent,
                $".opencowork-{Guid.NewGuid():N}.tmp");

            await using (var temporary = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (privateToCurrentUser && !OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(
                        temporaryPath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                await temporary.WriteAsync(bytes, cancellationToken);
                await temporary.WriteAsync("\n"u8.ToArray(), cancellationToken);
                await temporary.FlushAsync(cancellationToken);
                temporary.Flush(flushToDisk: true);
            }

            var revalidated = WorkspacePathGuard.RevalidateForWrite(destination);
            if (!PathEquals(destination.PhysicalPath, revalidated.PhysicalPath) ||
                !PathEquals(
                    parent,
                    Path.GetDirectoryName(revalidated.PhysicalPath) ?? string.Empty))
            {
                throw Unavailable();
            }

            _fault?.Invoke(CapabilityPersistenceFaultPoint.BeforeReplace);
            if (File.Exists(revalidated.PhysicalPath))
            {
                File.Replace(
                    temporaryPath,
                    revalidated.PhysicalPath,
                    destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, revalidated.PhysicalPath, overwrite: false);
            }

            temporaryPath = null;
            if (privateToCurrentUser && !OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    revalidated.PhysicalPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CapabilityPersistenceException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or InvalidOperationException)
        {
            throw Unavailable(exception);
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                }
            }

            _writeLock.Release();
        }
    }

    private static PluginLockDocument Normalize(PluginLockDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        EnsureSchema(document.SchemaVersion);
        if (document.Plugins is null || document.Plugins.Any(plugin => plugin is null))
        {
            throw Invalid();
        }

        var plugins = document.Plugins
            .OrderBy(plugin => plugin.Id, StringComparer.Ordinal)
            .ToArray();
        if (plugins.Any(plugin =>
                !IsClean(plugin.Id) ||
                !PluginIdPattern.IsMatch(plugin.Id) ||
                plugin.Id.StartsWith("opencowork/", StringComparison.Ordinal) ||
                !IsClean(plugin.Version) ||
                !SemVerPattern.IsMatch(plugin.Version) ||
                !IsSha256(plugin.Sha256)) ||
            HasDuplicates(plugins.Select(plugin => plugin.Id)))
        {
            throw Invalid();
        }

        return new PluginLockDocument(1, Array.AsReadOnly(plugins));
    }

    private static TrustDecisionsDocument Normalize(TrustDecisionsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        EnsureSchema(document.SchemaVersion);
        if (document.Decisions is null ||
            document.Decisions.Any(decision => decision is null))
        {
            throw Invalid();
        }

        var decisions = document.Decisions
            .Select(Normalize)
            .OrderBy(decision => decision.WorkspacePath, PathComparer)
            .ThenBy(decision => decision.SourceKind)
            .ThenBy(decision => decision.SourceId, StringComparer.Ordinal)
            .ThenBy(decision => decision.SourceVersion, StringComparer.Ordinal)
            .ThenBy(decision => decision.Sha256, StringComparer.Ordinal)
            .ToArray();
        if (HasDuplicates(decisions.Select(decision => (
                OperatingSystem.IsWindows()
                    ? decision.WorkspacePath.ToUpperInvariant()
                    : decision.WorkspacePath,
                decision.SourceKind,
                decision.SourceId,
                decision.SourceVersion,
                decision.Sha256))))
        {
            throw Invalid();
        }

        return new TrustDecisionsDocument(1, Array.AsReadOnly(decisions));
    }

    private static CapabilityTrustDecision Normalize(CapabilityTrustDecision decision)
    {
        if (!IsClean(decision.SourceId) ||
            !Enum.IsDefined(decision.SourceKind) ||
            decision.SourceKind is CapabilitySourceKind.Core or CapabilitySourceKind.Conflict ||
            decision.SourceVersion is not null && !IsClean(decision.SourceVersion) ||
            !IsSha256(decision.Sha256) ||
            decision.AllowedScopes is null ||
            decision.DeniedScopes is null)
        {
            throw Invalid();
        }

        var allowed = decision.AllowedScopes.Order().ToArray();
        var denied = decision.DeniedScopes.Order().ToArray();
        if (allowed.Any(scope => !Enum.IsDefined(scope)) ||
            denied.Any(scope => !Enum.IsDefined(scope)) ||
            allowed.Distinct().Count() != allowed.Length ||
            denied.Distinct().Count() != denied.Length ||
            allowed.Intersect(denied).Any())
        {
            throw Invalid();
        }

        var workspacePath = CanonicalWorkspacePath(decision.WorkspacePath);
        return new CapabilityTrustDecision(
            workspacePath,
            decision.SourceKind,
            decision.SourceId,
            decision.SourceVersion,
            decision.Sha256,
            Array.AsReadOnly(allowed),
            Array.AsReadOnly(denied));
    }

    private static CapabilityOverridesDocument Normalize(
        CapabilityOverridesDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        EnsureSchema(document.SchemaVersion);
        if (document.Disabled is null ||
            document.SkillVariants is null ||
            document.Disabled.Any(item => item is null) ||
            document.SkillVariants.Any(item => item is null))
        {
            throw Invalid();
        }

        var disabled = document.Disabled
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        var variants = document.SkillVariants
            .OrderBy(item => item.BaseId, StringComparer.Ordinal)
            .ToArray();
        if (disabled.Any(item => !Enum.IsDefined(item.Kind) || !IsClean(item.Id)) ||
            variants.Any(item =>
                !IsClean(item.BaseId) ||
                !IsClean(item.VariantId) ||
                string.Equals(item.BaseId, item.VariantId, StringComparison.Ordinal)) ||
            HasDuplicates(disabled.Select(item => (item.Kind, item.Id))) ||
            HasDuplicates(variants.Select(item => item.BaseId)))
        {
            throw Invalid();
        }

        return new CapabilityOverridesDocument(
            1,
            Array.AsReadOnly(disabled),
            Array.AsReadOnly(variants));
    }

    private static async Task<byte[]> ReadBoundedAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[MaximumFileBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (read == 0)
            {
                return buffer[..total];
            }

            total += read;
        }

        throw Invalid();
    }

    private static void EnsureUniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw Invalid();
                }

                EnsureUniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                EnsureUniqueProperties(item);
            }
        }
    }

    internal static string CanonicalWorkspacePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
        {
            throw Invalid();
        }

        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        var resolved = WorkspacePathGuard.ResolveContained(
            fullPath,
            Path.Combine(fullPath, ".opencowork-trust-anchor"),
            ".");
        return Path.TrimEndingDirectorySeparator(resolved.PhysicalPath);
    }

    private static bool HasDuplicates<T>(IEnumerable<T> values) =>
        values.GroupBy(value => value).Any(group => group.Skip(1).Any());

    private static bool IsClean(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 256 &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Any(char.IsControl);

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void EnsureSchema(int schemaVersion)
    {
        if (schemaVersion != SchemaVersion)
        {
            throw Invalid();
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            AllowTrailingCommas = false,
            MaxDepth = 64,
            PropertyNameCaseInsensitive = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = true,
        };
        options.Converters.Add(
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false));
        return options;
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static CapabilityPersistenceException Invalid(
        Exception? exception = null) =>
        new(
            CapabilityErrorCodes.PersistenceInvalid,
            "Capability persistence file is invalid.",
            exception);

    private static CapabilityPersistenceException Unavailable(
        Exception? exception = null) =>
        new(
            CapabilityErrorCodes.PersistenceUnavailable,
            "Capability persistence file is unavailable.",
            exception);
}
