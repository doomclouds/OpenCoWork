using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Configuration;
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

internal sealed record ExternalProviderModel(
    string Id,
    IReadOnlySet<string> Capabilities,
    string TokenizerProfileId,
    string TokenizerProfileVersion,
    int ContextWindowTokens,
    int MaxOutputTokens,
    string? TokenizerPath,
    string? TokenizerSha256)
{
    public bool SupportsToolCalls => Capabilities.Contains("toolCalls");
}

internal sealed record ExternalProvider(
    string Id,
    Uri BaseUri,
    string? AuthProfileId,
    TimeSpan ResponseHeaderTimeout,
    TimeSpan StreamIdleTimeout,
    IReadOnlyDictionary<string, ExternalProviderModel> Models,
    string SourceSha256);

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
        var providers = LoadProviders(paths, auth.Profiles);
        Providers = providers.Providers;
        Contributions = Array.AsReadOnly(
            auth.Contributions.Concat(providers.Contributions).ToArray());
    }

    public IReadOnlyDictionary<string, ProviderAuthProfile> AuthProfiles { get; }

    public IReadOnlyDictionary<string, ExternalProvider> Providers { get; }

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

    private ProviderLoadResult LoadProviders(
        OpenCoWorkPaths paths,
        IReadOnlyDictionary<string, ProviderAuthProfile> authProfiles)
    {
        var resolved = WorkspacePathGuard.ResolveContained(
            paths.WorkspaceRoot,
            Path.Combine(paths.WorkspaceRoot, ".opencowork-provider-anchor"),
            Path.GetRelativePath(paths.WorkspaceRoot, paths.ProvidersPath));
        if (!File.Exists(resolved.PhysicalPath))
        {
            return new ProviderLoadResult(
                new Dictionary<string, ExternalProvider>(StringComparer.Ordinal),
                []);
        }

        try
        {
            var (root, sha256) = ReadStrictJson(resolved.PhysicalPath);
            RequireObject(root, ["schemaVersion", "providers"]);
            RequireSchemaVersion(root);
            var providersElement = RequireArray(root, "providers");
            var providers =
                new Dictionary<string, ExternalProvider>(StringComparer.Ordinal);
            var items = new List<CapabilityContribution>();
            foreach (var element in providersElement.EnumerateArray())
            {
                ParseProvider(
                    element,
                    sha256,
                    authProfiles,
                    providers,
                    items);
            }

            var source = new CapabilitySourceDescriptor(
                CapabilitySourceKind.Workspace,
                "workspace.providers",
                version: null,
                sha256);
            return new ProviderLoadResult(
                providers,
                [new CapabilityContributionSet(source, items)]);
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException or
                DecoderFallbackException or ArgumentException or InvalidDataException)
        {
            return new ProviderLoadResult(
                new Dictionary<string, ExternalProvider>(StringComparer.Ordinal),
                [FaultedSource("workspace.providers", CapabilityKind.Provider, "provider.invalid")]);
        }
    }

    private static void ParseProvider(
        JsonElement element,
        string sourceSha256,
        IReadOnlyDictionary<string, ProviderAuthProfile> authProfiles,
        Dictionary<string, ExternalProvider> providers,
        List<CapabilityContribution> contributions)
    {
        RequireObject(
            element,
            [
                "id",
                "protocol",
                "baseUrl",
                "authProfileId",
                "timeouts",
                "models",
            ]);
        var id = RequireString(element, "id");
        if (!ExternalIdPattern().IsMatch(id) ||
            id.StartsWith("opencowork/", StringComparison.Ordinal) ||
            !string.Equals(
                RequireString(element, "protocol"),
                "openaiCompatible",
                StringComparison.Ordinal))
        {
            throw Invalid();
        }

        var baseUri = ParseBaseUri(RequireString(element, "baseUrl"));
        var authProfileId = OptionalString(element, "authProfileId");
        var authAvailable = authProfileId is null ||
                            authProfiles.TryGetValue(authProfileId, out var auth) &&
                            auth.Kind is ProviderAuthKind.None or ProviderAuthKind.ApiKey &&
                            auth.Available;
        var timeouts = element.GetProperty("timeouts");
        RequireObject(timeouts, ["responseHeaderMs", "streamIdleMs"]);
        var responseHeaderTimeout =
            Timeout(RequireInt32(timeouts, "responseHeaderMs"));
        var streamIdleTimeout =
            Timeout(RequireInt32(timeouts, "streamIdleMs"));
        var models = new Dictionary<string, ExternalProviderModel>(StringComparer.Ordinal);
        var invalidModels = new List<string>();
        foreach (var modelElement in RequireArray(element, "models").EnumerateArray())
        {
            var modelId = TryReadId(modelElement);
            try
            {
                var model = ParseModel(modelElement);
                if (!models.TryAdd(model.Id, model))
                {
                    models.Remove(model.Id);
                    invalidModels.Add(model.Id);
                }
            }
            catch (Exception exception) when (
                exception is JsonException or ArgumentException or InvalidDataException)
            {
                invalidModels.Add(modelId);
            }
        }

        foreach (var modelId in invalidModels.Distinct(StringComparer.Ordinal))
        {
            contributions.Add(new CapabilityContribution(
                CapabilityKind.Model,
                $"{id}/{modelId}",
                modelId,
                "Provider model declaration is invalid.",
                CapabilityStatus.Faulted,
                [],
                generation: 0,
                ["provider.modelInvalid"]));
        }

        var providerStatus = models.Count == 0
            ? CapabilityStatus.Faulted
            : authAvailable
                ? CapabilityStatus.Ready
                : CapabilityStatus.Unavailable;
        contributions.Add(new CapabilityContribution(
            CapabilityKind.Provider,
            id,
            id,
            "OpenAI-compatible workspace provider.",
            providerStatus,
            [],
            generation: 1,
            providerStatus switch
            {
                CapabilityStatus.Faulted => ["provider.noValidModel"],
                CapabilityStatus.Unavailable => ["auth.unavailable"],
                _ => [],
            }));
        foreach (var model in models.Values.OrderBy(model => model.Id, StringComparer.Ordinal))
        {
            contributions.Add(new CapabilityContribution(
                CapabilityKind.Model,
                $"{id}/{model.Id}",
                model.Id,
                "OpenAI-compatible provider model.",
                providerStatus == CapabilityStatus.Ready
                    ? CapabilityStatus.Ready
                    : CapabilityStatus.Unavailable,
                [],
                generation: 1,
                providerStatus == CapabilityStatus.Ready ? [] : ["provider.unavailable"]));
        }

        if (models.Count != 0 &&
            !providers.TryAdd(
                id,
                new ExternalProvider(
                    id,
                    baseUri,
                    authProfileId,
                    responseHeaderTimeout,
                    streamIdleTimeout,
                    models,
                    sourceSha256)))
        {
            throw Invalid();
        }
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

    private static ExternalProviderModel ParseModel(JsonElement element)
    {
        RequireObject(
            element,
            [
                "id",
                "capabilities",
                "tokenizerProfileId",
                "tokenizerProfileVersion",
                "contextWindowTokens",
                "maxOutputTokens",
                "tokenizerPath",
                "tokenizerSha256",
            ]);
        var id = RequireString(element, "id");
        if (id.Any(char.IsControl))
        {
            throw Invalid();
        }

        var capabilities = RequireArray(element, "capabilities")
            .EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String
                ? item.GetString()!
                : throw Invalid())
            .ToArray();
        if (capabilities.Length != capabilities.Distinct(StringComparer.Ordinal).Count() ||
            capabilities.Any(item => item is not ("streaming" or "toolCalls" or "usage")) ||
            !capabilities.Contains("streaming", StringComparer.Ordinal) ||
            !capabilities.Contains("usage", StringComparer.Ordinal))
        {
            throw Invalid();
        }

        var tokenizerPath = OptionalString(element, "tokenizerPath");
        var tokenizerSha256 = OptionalString(element, "tokenizerSha256");
        if (tokenizerPath is not null &&
            (Path.IsPathFullyQualified(tokenizerPath) ||
             tokenizerPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                 .Any(part => part == "..")) ||
            (tokenizerPath is null) != (tokenizerSha256 is null) ||
            tokenizerSha256 is not null && !Sha256Pattern().IsMatch(tokenizerSha256))
        {
            throw Invalid();
        }

        var model = new ExternalProviderModel(
            id,
            capabilities.ToHashSet(StringComparer.Ordinal),
            RequireString(element, "tokenizerProfileId"),
            RequireString(element, "tokenizerProfileVersion"),
            RequireInt32(element, "contextWindowTokens"),
            RequireInt32(element, "maxOutputTokens"),
            tokenizerPath,
            tokenizerSha256);
        var config = new ModelConfig
        {
            TokenizerProfileId = model.TokenizerProfileId,
            TokenizerProfileVersion = model.TokenizerProfileVersion,
            ContextWindowTokens = model.ContextWindowTokens,
            MaxOutputTokens = model.MaxOutputTokens,
            TokenizerPath = model.TokenizerPath,
            TokenizerSha256 = model.TokenizerSha256,
        };
        if (config.Validate(id, "model").Any())
        {
            throw Invalid();
        }

        return model;
    }

    private static Uri ParseBaseUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 ||
            uri.Fragment.Length != 0 ||
            uri.Scheme != Uri.UriSchemeHttps &&
            !(uri.Scheme == Uri.UriSchemeHttp && IsLoopback(uri.Host)))
        {
            throw Invalid();
        }

        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
    }

    private static bool IsLoopback(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);

    private static TimeSpan Timeout(int milliseconds) =>
        milliseconds is >= 1_000 and <= 300_000
            ? TimeSpan.FromMilliseconds(milliseconds)
            : throw Invalid();

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

    private static string? OptionalString(JsonElement element, string name)
    {
        var value = element.GetProperty(name);
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => RequireString(element, name),
            _ => throw Invalid(),
        };
    }

    private static int RequireInt32(JsonElement element, string name)
    {
        var value = element.GetProperty(name);
        return value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt32(out var number)
            ? number
            : throw Invalid();
    }

    private static string TryReadId(JsonElement element)
    {
        try
        {
            return RequireString(element, "id");
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidDataException)
        {
            return $"invalid-{Hash(element.GetRawText())[..16]}";
        }
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

    [GeneratedRegex(
        "^[0-9a-f]{64}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Sha256Pattern();

    private sealed record AuthLoadResult(
        IReadOnlyDictionary<string, ProviderAuthProfile> Profiles,
        IReadOnlyList<CapabilityContributionSet> Contributions);

    private sealed record ProviderLoadResult(
        IReadOnlyDictionary<string, ExternalProvider> Providers,
        IReadOnlyList<CapabilityContributionSet> Contributions);
}
