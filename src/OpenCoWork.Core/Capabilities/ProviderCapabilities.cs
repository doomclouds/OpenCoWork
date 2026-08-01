using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Workspaces;

namespace OpenCoWork.Core.Capabilities;

internal enum ProviderAuthKind
{
    None,
    ApiKey,
    OAuth,
}

internal enum ProviderAuthSourceKind
{
    None,
    Environment,
    OsSecretStore,
}

internal enum ProviderAuthPlacementKind
{
    None,
    Bearer,
    Header,
}

internal sealed record ProviderAuthPlacement(
    ProviderAuthPlacementKind Kind,
    string? HeaderName = null)
{
    public static ProviderAuthPlacement None { get; } =
        new(ProviderAuthPlacementKind.None);

    public static ProviderAuthPlacement Bearer { get; } =
        new(ProviderAuthPlacementKind.Bearer);
}

internal sealed record ProviderAuthProfile(
    string Id,
    ProviderAuthKind Kind,
    ProviderAuthSourceKind SourceKind,
    string? SourceName,
    ProviderAuthPlacement Placement,
    bool Available,
    IReadOnlyList<string>? Scopes = null);

internal sealed partial class ProviderDeclarationCatalog
{
    private const int MaximumFileBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly Func<string, string?> _readEnvironmentVariable;

    public ProviderDeclarationCatalog(
        OpenCoWorkPaths paths,
        Func<string, string?>? readEnvironmentVariable = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _readEnvironmentVariable =
            readEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        var auth = LoadAuth(paths);
        AuthProfiles = auth.Profiles;
        var providers = LoadProviders(paths);
        HasUnsupportedProviderConfiguration = providers.Unsupported;
        Contributions = Array.AsReadOnly(
            auth.Contributions.Concat(providers.Contributions).ToArray());
    }

    public IReadOnlyDictionary<string, ProviderAuthProfile> AuthProfiles { get; }

    public bool HasUnsupportedProviderConfiguration { get; }

    public IReadOnlyList<CapabilityContributionSet> Contributions { get; }

    private AuthLoadResult LoadAuth(OpenCoWorkPaths paths)
    {
        var resolved = WorkspacePathGuard.ResolveContained(
            paths.WorkspaceRoot,
            Path.Combine(paths.WorkspaceRoot, ".opencowork-provider-anchor"),
            Path.GetRelativePath(paths.WorkspaceRoot, paths.AuthPath));
        if (!File.Exists(resolved.PhysicalPath))
        {
            return new AuthLoadResult(
                new Dictionary<string, ProviderAuthProfile>(StringComparer.Ordinal),
                []);
        }

        try
        {
            var (root, sha256) = ReadStrictJson(resolved.PhysicalPath);
            RequireObject(root, ["schemaVersion", "profiles"]);
            RequireSchemaVersion(root);
            var profilesElement = RequireArray(root, "profiles");
            var profiles =
                new Dictionary<string, ProviderAuthProfile>(StringComparer.Ordinal);
            var items = new List<CapabilityContribution>();
            foreach (var element in profilesElement.EnumerateArray())
            {
                var profile = ParseAuthProfile(element);
                if (!profiles.TryAdd(profile.Id, profile))
                {
                    throw Invalid();
                }

                items.Add(new CapabilityContribution(
                    CapabilityKind.AuthProfile,
                    profile.Id,
                    profile.Id,
                    "Workspace authentication profile.",
                    profile.Available
                        ? CapabilityStatus.Ready
                        : CapabilityStatus.Unavailable,
                    [],
                    generation: 1,
                    profile.Available ? [] : ["auth.unavailable"]));
            }

            var source = new CapabilitySourceDescriptor(
                CapabilitySourceKind.Workspace,
                "workspace.auth",
                version: null,
                sha256);
            return new AuthLoadResult(
                profiles,
                [new CapabilityContributionSet(source, items)]);
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException or
                DecoderFallbackException or ArgumentException or InvalidDataException)
        {
            return new AuthLoadResult(
                new Dictionary<string, ProviderAuthProfile>(StringComparer.Ordinal),
                [FaultedSource("workspace.auth", CapabilityKind.AuthProfile, "auth.invalid")]);
        }
    }

    private static ProviderLoadResult LoadProviders(OpenCoWorkPaths paths)
    {
        var resolved = WorkspacePathGuard.ResolveContained(
            paths.WorkspaceRoot,
            Path.Combine(paths.WorkspaceRoot, ".opencowork-provider-anchor"),
            Path.GetRelativePath(paths.WorkspaceRoot, paths.ProvidersPath));
        if (!File.Exists(resolved.PhysicalPath))
        {
            return new ProviderLoadResult(
                [],
                Unsupported: false);
        }

        return new ProviderLoadResult(
            [FaultedSource(
                "workspace.providers",
                CapabilityKind.Provider,
                "provider.legacyConfigurationUnsupported")],
            Unsupported: true);
    }

    private ProviderAuthProfile ParseAuthProfile(JsonElement element)
    {
        RequireObject(element, ["id", "kind", "source", "placement", "scopes"]);
        var id = RequireString(element, "id");
        if (!ExternalIdPattern().IsMatch(id))
        {
            throw Invalid();
        }

        var kind = RequireString(element, "kind") switch
        {
            "none" => ProviderAuthKind.None,
            "apiKey" => ProviderAuthKind.ApiKey,
            "oauth" => ProviderAuthKind.OAuth,
            _ => throw Invalid(),
        };
        var source = kind == ProviderAuthKind.ApiKey
            ? element.GetProperty("source")
            : default;
        var (sourceKind, sourceName, available) = kind switch
        {
            ProviderAuthKind.None =>
                (ProviderAuthSourceKind.None, (string?)null, true),
            ProviderAuthKind.ApiKey => ParseAuthSource(source),
            ProviderAuthKind.OAuth =>
                (ProviderAuthSourceKind.OsSecretStore, (string?)null, false),
            _ => throw Invalid(),
        };
        var placement = kind == ProviderAuthKind.ApiKey
            ? ParsePlacement(element.GetProperty("placement"))
            : ProviderAuthPlacement.None;
        var scopes = kind == ProviderAuthKind.OAuth
            ? RequireArray(element, "scopes")
                .EnumerateArray()
                .Select(scope =>
                    scope.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(scope.GetString())
                        ? scope.GetString()!
                        : throw Invalid())
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray()
            : [];
        return new ProviderAuthProfile(
            id,
            kind,
            sourceKind,
            sourceName,
            placement,
            available,
            scopes);
    }

    private (ProviderAuthSourceKind Kind, string? Name, bool Available)
        ParseAuthSource(JsonElement element)
    {
        RequireObject(element, ["kind", "name"]);
        return RequireString(element, "kind") switch
        {
            "environment" =>
                EnvironmentSource(RequireString(element, "name")),
            "osSecretStore" =>
                (ProviderAuthSourceKind.OsSecretStore, (string?)null, false),
            _ => throw Invalid(),
        };
    }

    private (ProviderAuthSourceKind Kind, string? Name, bool Available)
        EnvironmentSource(string name)
    {
        if (!EnvironmentNamePattern().IsMatch(name))
        {
            throw Invalid();
        }

        return (
            ProviderAuthSourceKind.Environment,
            name,
            !string.IsNullOrWhiteSpace(_readEnvironmentVariable(name)));
    }

    private static ProviderAuthPlacement ParsePlacement(JsonElement element)
    {
        var kind = RequireString(element, "kind");
        if (string.Equals(kind, "bearer", StringComparison.Ordinal))
        {
            RequireObject(element, ["kind"]);
            return ProviderAuthPlacement.Bearer;
        }

        if (!string.Equals(kind, "header", StringComparison.Ordinal))
        {
            throw Invalid();
        }

        RequireObject(element, ["kind", "name"]);
        var name = RequireString(element, "name");
        if (!HeaderNamePattern().IsMatch(name) ||
            string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid();
        }

        return new ProviderAuthPlacement(ProviderAuthPlacementKind.Header, name);
    }

    private static (JsonElement Root, string Sha256) ReadStrictJson(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.SequentialScan);
        if (stream.Length > MaximumFileBytes)
        {
            throw Invalid();
        }

        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        _ = StrictUtf8.GetString(bytes);
        using var document = JsonDocument.Parse(
            bytes,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
        EnsureUniqueProperties(document.RootElement);
        return (
            document.RootElement.Clone(),
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    private static void RequireSchemaVersion(JsonElement root)
    {
        if (RequireInt32(root, "schemaVersion") != 1)
        {
            throw Invalid();
        }
    }

    private static void RequireObject(
        JsonElement element,
        IReadOnlyCollection<string> allowed)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid();
        }

        var actual = element.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Any(name => !allowed.Contains(name, StringComparer.Ordinal)))
        {
            throw Invalid();
        }
    }

    private static JsonElement RequireArray(JsonElement element, string name)
    {
        var value = element.GetProperty(name);
        return value.ValueKind == JsonValueKind.Array ? value : throw Invalid();
    }

    private static string RequireString(JsonElement element, string name)
    {
        var value = element.GetProperty(name);
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return !string.IsNullOrWhiteSpace(text) &&
               string.Equals(text, text.Trim(), StringComparison.Ordinal) &&
               !text.Any(char.IsControl)
            ? text
            : throw Invalid();
    }

    private static int RequireInt32(JsonElement element, string name)
    {
        var value = element.GetProperty(name);
        return value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt32(out var number)
            ? number
            : throw Invalid();
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

    private static CapabilityContributionSet FaultedSource(
        string sourceId,
        CapabilityKind kind,
        string diagnostic)
    {
        var digest = Hash(sourceId);
        return new CapabilityContributionSet(
            new CapabilitySourceDescriptor(
                CapabilitySourceKind.Workspace,
                sourceId,
                version: null,
                digest),
            [
                new CapabilityContribution(
                    kind,
                    $"invalid/{digest[..16]}",
                    "Invalid declaration",
                    "Capability declaration file is invalid.",
                    CapabilityStatus.Faulted,
                    [],
                    generation: 0,
                    [diagnostic]),
            ]);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static InvalidDataException Invalid() =>
        new("Capability provider declaration is invalid.");

    [GeneratedRegex(
        "^[a-z0-9](?:[a-z0-9.-]{0,62})/[a-z0-9](?:[a-z0-9.-]{0,62})$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex ExternalIdPattern();

    [GeneratedRegex(
        "^[A-Za-z_][A-Za-z0-9_]{0,127}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex EnvironmentNamePattern();

    [GeneratedRegex(
        "^[!#$%&'*+.^_`|~0-9A-Za-z-]{1,128}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex HeaderNamePattern();

    private sealed record AuthLoadResult(
        IReadOnlyDictionary<string, ProviderAuthProfile> Profiles,
        IReadOnlyList<CapabilityContributionSet> Contributions);

    private sealed record ProviderLoadResult(
        IReadOnlyList<CapabilityContributionSet> Contributions,
        bool Unsupported);
}
