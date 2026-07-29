using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Tools;
using OpenCoWork.Core.Workspaces;

namespace OpenCoWork.Core.Capabilities;

internal static class McpCapabilityErrorCodes
{
    public const string ConfigurationInvalid = "mcp.configurationInvalid";
    public const string ConnectionFailed = "mcp.connectionFailed";
    public const string AuthenticationRequired = "mcp.authenticationRequired";
}

internal enum McpTransportKind
{
    Stdio,
    StreamableHttp,
}

internal enum McpSessionStatus
{
    Starting,
    Authenticating,
    Ready,
    Disconnected,
    Faulted,
    Stopped,
}

internal sealed record McpEnvironmentValue(
    string? Literal,
    string? SecretRef);

internal sealed record McpServerDefinition(
    string Id,
    bool Enabled,
    McpTransportKind TransportKind,
    string? Command,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, McpEnvironmentValue> Environment,
    Uri? Endpoint,
    string? AuthProfileId,
    string ConfigurationSha256,
    string? ResolvedCommand = null,
    string? ExecutableSha256 = null);

internal sealed record McpResourceSummary(
    string ServerId,
    string Uri,
    string Name,
    string? Description,
    string? MimeType,
    long Generation);

internal sealed record McpDiscoveryResult(
    IReadOnlyList<CapabilityContributionSet> Contributions);

internal sealed class McpCapabilityException(string code, string message)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

internal sealed class McpCapabilitySource
{
    private const int MaximumFileBytes = 1024 * 1024;
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly OpenCoWorkPaths _paths;
    private readonly CapabilityFileStore _files;
    private readonly ToolRuntime _tools;
    private readonly ProviderAuthService _auth;
    private readonly Func<McpServerDefinition, CancellationToken, Task<McpConnection>>
        _connect;
    private readonly Dictionary<string, McpServerSession> _sessions =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _generations =
        new(StringComparer.Ordinal);

    public McpCapabilitySource(
        OpenCoWorkPaths paths,
        CapabilityFileStore files,
        ToolRuntime tools,
        ProviderAuthService auth,
        Func<McpServerDefinition, CancellationToken, Task<McpConnection>>? connect = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _connect = connect ?? ConnectAsync;
    }

    internal Func<CancellationToken, Task>? Changed { get; set; }

    public async Task<McpDiscoveryResult> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var definitions = await LoadDefinitionsAsync(cancellationToken);
            var trust = await _files.LoadTrustDecisionsAsync(cancellationToken);
            var retained = definitions
                .Select(definition => definition.Id)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var removed in _sessions.Keys
                         .Where(id => !retained.Contains(id))
                         .ToArray())
            {
                await StopAndRemoveAsync(removed, cancellationToken);
            }

            var contributions = new List<CapabilityContributionSet>();
            foreach (var configuredEntry in definitions.OrderBy(
                         definition => definition.Id,
                         StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var configured = configuredEntry;
                if (configured.TransportKind == McpTransportKind.Stdio)
                {
                    try
                    {
                        configured = ResolveCommand(configured);
                    }
                    catch (Exception exception) when (
                        exception is IOException or UnauthorizedAccessException or
                            ArgumentException)
                    {
                        await StopAndRemoveAsync(configured.Id, cancellationToken);
                        contributions.Add(Contribution(
                            configured,
                            CapabilityStatus.Faulted,
                            generation: 0,
                            [McpCapabilityErrorCodes.ConfigurationInvalid]));
                        continue;
                    }
                }

                var requiredTrust = RequiredTrust(configured);
                if (!configured.Enabled)
                {
                    await StopAndRemoveAsync(configured.Id, cancellationToken);
                    contributions.Add(Contribution(
                        configured,
                        CapabilityStatus.Disabled,
                        generation: 0,
                        []));
                    continue;
                }

                if (!IsTrusted(configured, requiredTrust, trust))
                {
                    await StopAndRemoveAsync(configured.Id, cancellationToken);
                    contributions.Add(Contribution(
                        configured,
                        CapabilityStatus.PendingTrust,
                        generation: 0,
                        [ToolErrorCodes.TrustRequired],
                        requiredTrust));
                    continue;
                }

                if (_sessions.TryGetValue(configured.Id, out var current) &&
                    string.Equals(
                        current.Definition.ConfigurationSha256,
                        configured.ConfigurationSha256,
                        StringComparison.Ordinal) &&
                    current.Status == McpSessionStatus.Ready)
                {
                    contributions.Add(current.CreateContribution(requiredTrust));
                    continue;
                }

                await StopAndRemoveAsync(configured.Id, cancellationToken);
                var session = new McpServerSession(
                    configured,
                    NextGeneration(configured.Id),
                    _tools,
                    _connect)
                {
                    Changed = NotifyChangedAsync,
                };
                _sessions[configured.Id] = session;
                await session.StartAsync(cancellationToken);
                contributions.Add(session.CreateContribution(requiredTrust));
            }

            return new McpDiscoveryResult(
                Array.AsReadOnly(contributions.ToArray()));
        }
        catch (McpCapabilityException exception)
        {
            await StopAllAsync(cancellationToken);
            var invalid = new McpServerDefinition(
                "workspace/mcp-config",
                Enabled: false,
                McpTransportKind.StreamableHttp,
                Command: null,
                Arguments: [],
                Environment: new Dictionary<string, McpEnvironmentValue>(
                    StringComparer.Ordinal),
                Endpoint: null,
                AuthProfileId: null,
                new string('0', 64));
            return new McpDiscoveryResult(
            [
                Contribution(
                    invalid,
                    CapabilityStatus.Faulted,
                    generation: 0,
                    [exception.Code]),
            ]);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RestartAsync(
        string serverId,
        long expectedGeneration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_sessions.TryGetValue(serverId, out var current) ||
                current.Generation != expectedGeneration)
            {
                throw new McpCapabilityException(
                    McpToolErrorCodes.Disconnected,
                    "MCP Server generation no longer matches.");
            }

            var definition = current.Definition;
            await StopAndRemoveAsync(serverId, cancellationToken);
            var replacement = new McpServerSession(
                definition,
                NextGeneration(serverId),
                _tools,
                _connect)
            {
                Changed = NotifyChangedAsync,
            };
            _sessions[serverId] = replacement;
            await replacement.StartAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        await NotifyChangedAsync(cancellationToken);
    }

    public IReadOnlyList<McpResourceSummary> ListResources(string serverId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        return _sessions.TryGetValue(serverId, out var session) &&
               session.Status == McpSessionStatus.Ready
            ? session.Resources
            : throw new McpCapabilityException(
                McpToolErrorCodes.Disconnected,
                "MCP Server is disconnected.");
    }

    public Task<JsonElement> ReadResourceAsync(
        string serverId,
        string uri,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        return _sessions.TryGetValue(serverId, out var session)
            ? session.ReadResourceAsync(uri, cancellationToken)
            : throw new McpCapabilityException(
                McpToolErrorCodes.Disconnected,
                "MCP Server is disconnected.");
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await StopAllAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<McpServerDefinition>> LoadDefinitionsAsync(
        CancellationToken cancellationToken)
    {
        var resolved = WorkspacePathGuard.ResolveContained(
            _paths.WorkspaceRoot,
            Path.Combine(_paths.WorkspaceRoot, ".opencowork-mcp-anchor"),
            Path.GetRelativePath(_paths.WorkspaceRoot, _paths.McpPath));
        if (!File.Exists(resolved.PhysicalPath))
        {
            return [];
        }

        var info = new FileInfo(resolved.PhysicalPath);
        if (info.Length > MaximumFileBytes)
        {
            throw InvalidConfiguration();
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(
                resolved.PhysicalPath,
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw InvalidConfiguration();
        }

        try
        {
            _ = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true).GetString(bytes);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });
            EnsureUniqueProperties(document.RootElement);
            RequireObject(document.RootElement, ["schemaVersion", "servers"]);
            if (RequireInt32(document.RootElement, "schemaVersion") != 1)
            {
                throw InvalidConfiguration();
            }

            var servers = RequireArray(document.RootElement, "servers");
            if (servers.GetArrayLength() > 32)
            {
                throw InvalidConfiguration();
            }

            var definitions = servers
                .EnumerateArray()
                .Select(ParseServer)
                .ToArray();
            if (definitions
                .GroupBy(definition => definition.Id, StringComparer.Ordinal)
                .Any(group => group.Skip(1).Any()))
            {
                throw InvalidConfiguration();
            }

            return Array.AsReadOnly(definitions);
        }
        catch (Exception exception) when (
            exception is JsonException or DecoderFallbackException or
                ArgumentException or InvalidOperationException or
                McpCapabilityException)
        {
            throw InvalidConfiguration();
        }
    }

    private McpServerDefinition ParseServer(JsonElement element)
    {
        RequireObject(element, ["id", "enabled", "transport"]);
        var id = RequireString(element, "id");
        if (!IsExternalId(id))
        {
            throw InvalidConfiguration();
        }

        var enabled = RequireBoolean(element, "enabled");
        var transport = element.GetProperty("transport");
        var kind = RequireString(transport, "kind");
        var sha256 = Hash(JsonSerializer.SerializeToUtf8Bytes(element));
        return kind switch
        {
            "stdio" => ParseStdio(id, enabled, transport, sha256),
            "streamableHttp" => ParseHttp(id, enabled, transport, sha256),
            _ => throw InvalidConfiguration(),
        };
    }

    private McpServerDefinition ParseStdio(
        string id,
        bool enabled,
        JsonElement transport,
        string sha256)
    {
        RequireObject(
            transport,
            ["kind", "command", "arguments", "workingDirectory", "environment"]);
        var command = RequireString(transport, "command");
        if (command.Length > 4096 ||
            command.Contains('\0', StringComparison.Ordinal) ||
            !string.Equals(
                RequireString(transport, "workingDirectory"),
                "workspace",
                StringComparison.Ordinal))
        {
            throw InvalidConfiguration();
        }

        var arguments = RequireArray(transport, "arguments")
            .EnumerateArray()
            .Select(argument =>
                argument.ValueKind == JsonValueKind.String &&
                argument.GetString() is { Length: <= 4096 } value &&
                !value.Contains('\0', StringComparison.Ordinal)
                    ? value
                    : throw InvalidConfiguration())
            .ToArray();
        if (arguments.Length > 128)
        {
            throw InvalidConfiguration();
        }

        var environmentElement = transport.GetProperty("environment");
        if (environmentElement.ValueKind != JsonValueKind.Object ||
            environmentElement.EnumerateObject().Count() > 64)
        {
            throw InvalidConfiguration();
        }

        var environment =
            new Dictionary<string, McpEnvironmentValue>(StringComparer.Ordinal);
        foreach (var property in environmentElement.EnumerateObject())
        {
            if (!IsEnvironmentName(property.Name))
            {
                throw InvalidConfiguration();
            }

            RequireObject(property.Value, ["literal", "secretRef"]);
            var hasLiteral = property.Value.TryGetProperty(
                "literal",
                out var literalElement);
            var hasSecret = property.Value.TryGetProperty(
                "secretRef",
                out var secretElement);
            if (hasLiteral == hasSecret ||
                hasLiteral && literalElement.ValueKind != JsonValueKind.String ||
                hasSecret && secretElement.ValueKind != JsonValueKind.String)
            {
                throw InvalidConfiguration();
            }

            var literal = hasLiteral ? literalElement.GetString() : null;
            var secretRef = hasSecret ? secretElement.GetString() : null;
            if (literal is { Length: > 16 * 1024 } ||
                string.IsNullOrWhiteSpace(hasSecret ? secretRef : literal))
            {
                throw InvalidConfiguration();
            }

            environment.Add(
                property.Name,
                new McpEnvironmentValue(literal, secretRef));
        }

        return new McpServerDefinition(
            id,
            enabled,
            McpTransportKind.Stdio,
            command,
            Array.AsReadOnly(arguments),
            environment,
            Endpoint: null,
            AuthProfileId: null,
            sha256);
    }

    private McpServerDefinition ParseHttp(
        string id,
        bool enabled,
        JsonElement transport,
        string sha256)
    {
        RequireObject(transport, ["kind", "url", "authProfileId"]);
        if (!Uri.TryCreate(
                RequireString(transport, "url"),
                UriKind.Absolute,
                out var endpoint) ||
            endpoint.UserInfo.Length != 0 ||
            endpoint.Scheme != Uri.UriSchemeHttps &&
            !(endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback))
        {
            throw InvalidConfiguration();
        }

        var authProfileId = OptionalString(transport, "authProfileId");
        if (authProfileId is not null)
        {
            _ = _auth.GetProfile(authProfileId);
        }

        return new McpServerDefinition(
            id,
            enabled,
            McpTransportKind.StreamableHttp,
            Command: null,
            Arguments: [],
            Environment: new Dictionary<string, McpEnvironmentValue>(
                StringComparer.Ordinal),
            endpoint,
            authProfileId,
            sha256);
    }

    private McpServerDefinition ResolveCommand(McpServerDefinition definition)
    {
        var command = definition.Command!;
        string? resolved;
        if (command.IndexOfAny(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            resolved = WorkspacePathGuard.ResolveContained(
                _paths.WorkspaceRoot,
                Path.Combine(_paths.WorkspaceRoot, ".opencowork-mcp-source"),
                command).PhysicalPath;
        }
        else
        {
            resolved = FindOnPath(command);
        }

        if (resolved is null || !File.Exists(resolved))
        {
            throw new FileNotFoundException("MCP executable was not found.");
        }

        using var stream = File.OpenRead(resolved);
        var digest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return definition with
        {
            ResolvedCommand = resolved,
            ExecutableSha256 = digest,
        };
    }

    private static IReadOnlyList<CapabilityTrustScope> RequiredTrust(
        McpServerDefinition definition) =>
        definition.TransportKind == McpTransportKind.Stdio ||
        definition.Endpoint is { Scheme: "http" }
            ? [CapabilityTrustScope.OutOfProcess]
            : [];

    private bool IsTrusted(
        McpServerDefinition definition,
        IReadOnlyList<CapabilityTrustScope> requiredScopes,
        TrustDecisionsDocument trust)
    {
        if (requiredScopes.Count == 0)
        {
            return true;
        }

        var trustSha = definition.ExecutableSha256 ??
                       definition.ConfigurationSha256;
        var decision = trust.Decisions.SingleOrDefault(item =>
            item.Matches(
                _paths.WorkspaceRoot,
                CapabilitySourceKind.Workspace,
                definition.Id,
                sourceVersion: null,
                trustSha));
        return decision is not null &&
               requiredScopes.All(scope =>
                   decision.AllowedScopes.Contains(scope)) &&
               requiredScopes.All(scope =>
                   !decision.DeniedScopes.Contains(scope));
    }

    private async Task<McpConnection> ConnectAsync(
        McpServerDefinition definition,
        CancellationToken cancellationToken)
    {
        if (definition.TransportKind == McpTransportKind.StreamableHttp)
        {
            return ConnectHttp(definition);
        }

        var leases = new List<IDisposable>();
        Process? process = null;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = definition.ResolvedCommand!,
                WorkingDirectory = _paths.WorkspaceRoot,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false, true),
                StandardErrorEncoding = new UTF8Encoding(false, true),
            };
            foreach (var argument in definition.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            foreach (var name in startInfo.Environment.Keys.ToArray())
            {
                if (IsSensitiveEnvironmentName(name))
                {
                    startInfo.Environment.Remove(name);
                }
            }

            foreach (var (name, value) in definition.Environment)
            {
                if (value.SecretRef is { } secretRef)
                {
                    var lease = _auth.Acquire(secretRef);
                    leases.Add(lease);
                    startInfo.Environment[name] = lease.Secret;
                }
                else
                {
                    startInfo.Environment[name] = value.Literal;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true,
            };
            if (!process.Start())
            {
                throw new IOException("MCP process did not start.");
            }

            var stderr = DrainAsync(
                process.StandardError,
                CancellationToken.None);
            var transport = new StreamClientTransport(
                process.StandardInput.BaseStream,
                process.StandardOutput.BaseStream);
            return new McpConnection(
                transport,
                transportLifetime: null,
                process,
                stderr,
                leases);
        }
        catch
        {
            if (process is not null)
            {
                await McpConnection.KillAsync(process);
                process.Dispose();
            }

            foreach (var lease in leases)
            {
                lease.Dispose();
            }

            throw;
        }
    }

    private McpConnection ConnectHttp(McpServerDefinition definition)
    {
        var disposables = new List<IDisposable>();
        var client = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var options = new HttpClientTransportOptions
        {
            Name = definition.Id,
            Endpoint = definition.Endpoint!,
            TransportMode = HttpTransportMode.StreamableHttp,
            ConnectionTimeout = DefaultTimeout,
            MaxReconnectionAttempts = 0,
            EnableStandaloneGetStream = true,
        };
        if (definition.AuthProfileId is { } profileId)
        {
            var profile = _auth.GetProfile(profileId);
            if (profile.Kind == ProviderAuthKind.OAuth)
            {
                var cache = new McpOAuthTokenCache(_auth, profileId);
                disposables.Add(cache);
                options.OAuth = new ClientOAuthOptions
                {
                    RedirectUri = new Uri(
                        "http://127.0.0.1/opencowork/oauth/callback"),
                    Scopes = profile.Scopes,
                    TokenCache = cache,
                };
            }
            else if (profile.Kind == ProviderAuthKind.ApiKey)
            {
                var lease = _auth.Acquire(profileId);
                disposables.Add(lease);
                if (profile.Placement.Kind == ProviderAuthPlacementKind.Bearer)
                {
                    client.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue(
                            "Bearer",
                            lease.Secret);
                }
                else
                {
                    client.DefaultRequestHeaders.TryAddWithoutValidation(
                        profile.Placement.HeaderName!,
                        lease.Secret);
                }
            }
        }

        var transport = new HttpClientTransport(
            options,
            client,
            ownsHttpClient: true);
        return new McpConnection(
            transport,
            transport,
            process: null,
            stderr: null,
            disposables);
    }

    private async Task StopAndRemoveAsync(
        string serverId,
        CancellationToken cancellationToken)
    {
        if (_sessions.Remove(serverId, out var session))
        {
            await session.StopAsync(cancellationToken);
        }
    }

    private async Task StopAllAsync(CancellationToken cancellationToken)
    {
        foreach (var serverId in _sessions.Keys
                     .OrderDescending(StringComparer.Ordinal)
                     .ToArray())
        {
            await StopAndRemoveAsync(serverId, cancellationToken);
        }
    }

    private long NextGeneration(string serverId)
    {
        var generation = checked(_generations.GetValueOrDefault(serverId) + 1);
        _generations[serverId] = generation;
        return generation;
    }

    private Task NotifyChangedAsync(CancellationToken cancellationToken) =>
        Changed?.Invoke(cancellationToken) ?? Task.CompletedTask;

    private static CapabilityContributionSet Contribution(
        McpServerDefinition definition,
        CapabilityStatus status,
        long generation,
        IReadOnlyList<string> diagnostics,
        IReadOnlyList<CapabilityTrustScope>? requiredTrust = null) =>
        new(
            new CapabilitySourceDescriptor(
                CapabilitySourceKind.Workspace,
                $"mcp:{definition.Id}",
                version: null,
                definition.ConfigurationSha256),
            [
                new CapabilityContribution(
                    CapabilityKind.McpServer,
                    definition.Id,
                    definition.Id,
                    "Workspace MCP Server.",
                    status,
                    requiredTrust ?? [],
                    generation,
                    diagnostics),
            ]);

    private static string? FindOnPath(string command)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];
        foreach (var directory in path.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.GetFullPath(
                    Path.Combine(directory, command + extension));
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static async Task DrainAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        try
        {
            while (await reader.ReadAsync(buffer, cancellationToken) != 0)
            {
            }
        }
        catch (Exception exception) when (
            exception is IOException or DecoderFallbackException or
                OperationCanceledException)
        {
        }
    }

    private static void RequireObject(
        JsonElement element,
        IReadOnlyCollection<string> allowedProperties)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            element.EnumerateObject().Any(property =>
                !allowedProperties.Contains(property.Name)))
        {
            throw InvalidConfiguration();
        }
    }

    private static JsonElement RequireArray(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Array
            ? value
            : throw InvalidConfiguration();

    private static string RequireString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw InvalidConfiguration();

    private static string? OptionalString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String &&
               !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : throw InvalidConfiguration();
    }

    private static int RequireInt32(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.TryGetInt32(out var result)
            ? result
            : throw InvalidConfiguration();

    private static bool RequireBoolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw InvalidConfiguration();

    private static void EnsureUniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw InvalidConfiguration();
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

    private static bool IsExternalId(string value)
    {
        var separator = value.IndexOf('/');
        return separator is > 0 and < 64 &&
               separator == value.LastIndexOf('/') &&
               IsIdPart(value.AsSpan(0, separator)) &&
               IsIdPart(value.AsSpan(separator + 1)) &&
               !value.StartsWith("opencowork/", StringComparison.Ordinal);
    }

    private static bool IsIdPart(ReadOnlySpan<char> value)
    {
        if (value.Length is 0 or > 64 ||
            value[0] is < 'a' or > 'z')
        {
            return false;
        }

        foreach (var character in value[1..])
        {
            if (character is not (
                >= 'a' and <= 'z' or
                >= '0' and <= '9' or
                '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsEnvironmentName(string value) =>
        value.Length is > 0 and <= 128 &&
        (char.IsAsciiLetter(value[0]) || value[0] == '_') &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character == '_');

    private static bool IsSensitiveEnvironmentName(string name)
    {
        var normalized = name.ToUpperInvariant();
        return normalized.Contains("API_KEY", StringComparison.Ordinal) ||
               normalized.Contains("TOKEN", StringComparison.Ordinal) ||
               normalized.Contains("SECRET", StringComparison.Ordinal) ||
               normalized.Contains("PASSWORD", StringComparison.Ordinal) ||
               normalized.Contains("CREDENTIAL", StringComparison.Ordinal) ||
               normalized.Contains("AUTH", StringComparison.Ordinal);
    }

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static McpCapabilityException InvalidConfiguration() =>
        new(
            McpCapabilityErrorCodes.ConfigurationInvalid,
            "MCP configuration is invalid.");
}

internal sealed class McpServerSession
{
    private const int MaximumTools = 128;
    private const int MaximumResources = 256;
    private const ToolEffect ConservativeEffects =
        ToolEffect.WorkspaceRead |
        ToolEffect.WorkspaceWrite |
        ToolEffect.ProcessExecution |
        ToolEffect.NetworkRead |
        ToolEffect.ExternalMutation;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ToolRuntime _tools;
    private readonly Func<McpServerDefinition, CancellationToken, Task<McpConnection>>
        _connect;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<IAsyncDisposable> _notificationHandlers = [];
    private ToolRegistration[] _registrations = [];
    private ToolRuntimeBinding[] _bindings = [];
    private McpResourceSummary[] _resources = [];
    private McpConnection? _connection;
    private McpClient? _client;
    private long _nextRequestId;
    private int _status = (int)McpSessionStatus.Stopped;

    public McpServerSession(
        McpServerDefinition definition,
        long generation,
        ToolRuntime tools,
        Func<McpServerDefinition, CancellationToken, Task<McpConnection>> connect)
    {
        Definition = definition ??
            throw new ArgumentNullException(nameof(definition));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        Generation = generation;
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _connect = connect ?? throw new ArgumentNullException(nameof(connect));
    }

    public McpServerDefinition Definition { get; }

    public long Generation { get; }

    public McpSessionStatus Status =>
        (McpSessionStatus)Volatile.Read(ref _status);

    public IReadOnlyList<McpResourceSummary> Resources =>
        Array.AsReadOnly(Volatile.Read(ref _resources));

    internal Func<CancellationToken, Task>? Changed { get; init; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        SetStatus(McpSessionStatus.Starting);
        try
        {
            _connection = await _connect(Definition, cancellationToken);
            _client = await McpClient.CreateAsync(
                _connection.Transport,
                new McpClientOptions
                {
                    InitializationTimeout = McpCapabilitySource.DefaultTimeout,
                },
                cancellationToken: cancellationToken);
            _notificationHandlers.Add(_client.RegisterNotificationHandler(
                "notifications/tools/list_changed",
                (_, token) => RefreshFromNotificationAsync(token)));
            _notificationHandlers.Add(_client.RegisterNotificationHandler(
                "notifications/resources/list_changed",
                (_, token) => RefreshFromNotificationAsync(token)));
            await RefreshAsync(cancellationToken);
            SetStatus(McpSessionStatus.Ready);
            _ = MonitorCompletionAsync(_client);
        }
        catch (OperationCanceledException)
        {
            await CleanupAsync();
            SetStatus(McpSessionStatus.Stopped);
            throw;
        }
        catch (UrlElicitationRequiredException)
        {
            await CleanupAsync();
            SetStatus(McpSessionStatus.Authenticating);
        }
        catch (Exception exception) when (
            exception is McpException or IOException or JsonException or
                InvalidOperationException or ArgumentException or
                UnauthorizedAccessException or TimeoutException)
        {
            await CleanupAsync();
            SetStatus(McpSessionStatus.Faulted);
        }
    }

    public CapabilityContributionSet CreateContribution(
        IReadOnlyList<CapabilityTrustScope> requiredTrust)
    {
        var status = Status switch
        {
            McpSessionStatus.Starting => CapabilityStatus.Starting,
            McpSessionStatus.Authenticating => CapabilityStatus.Authenticating,
            McpSessionStatus.Ready => CapabilityStatus.Ready,
            McpSessionStatus.Disconnected => CapabilityStatus.Disconnected,
            McpSessionStatus.Faulted => CapabilityStatus.Faulted,
            _ => CapabilityStatus.Unavailable,
        };
        var diagnostics = Status switch
        {
            McpSessionStatus.Authenticating =>
                [McpCapabilityErrorCodes.AuthenticationRequired],
            McpSessionStatus.Disconnected => [McpToolErrorCodes.Disconnected],
            McpSessionStatus.Faulted => [McpCapabilityErrorCodes.ConnectionFailed],
            _ => Array.Empty<string>(),
        };
        var source = new CapabilitySourceDescriptor(
            CapabilitySourceKind.Workspace,
            $"mcp:{Definition.Id}",
            version: null,
            Definition.ConfigurationSha256);
        var contributions = new List<CapabilityContribution>
        {
            new(
                CapabilityKind.McpServer,
                Definition.Id,
                Definition.Id,
                "Workspace MCP Server.",
                status,
                requiredTrust,
                Generation,
                diagnostics),
        };
        if (status == CapabilityStatus.Ready)
        {
            contributions.AddRange(_registrations.Select(registration =>
                new CapabilityContribution(
                    CapabilityKind.Tool,
                    $"{Definition.Id}/{registration.Definition.Id.SourceToolId}",
                    registration.Definition.Id.SourceToolId,
                    registration.Definition.Description,
                    CapabilityStatus.Ready,
                    requiredTrust,
                    Generation,
                    [])));
        }

        return new CapabilityContributionSet(source, contributions);
    }

    public async Task<JsonElement> ReadResourceAsync(
        string uri,
        CancellationToken cancellationToken)
    {
        var client = _client;
        if (Status != McpSessionStatus.Ready ||
            client is null ||
            !_resources.Any(resource =>
                string.Equals(resource.Uri, uri, StringComparison.Ordinal)))
        {
            throw new McpCapabilityException(
                McpToolErrorCodes.Disconnected,
                "MCP Resource is unavailable.");
        }

        try
        {
            var result = await client.ReadResourceAsync(uri, cancellationToken: cancellationToken);
            var output = JsonSerializer.SerializeToElement(
                result,
                McpJsonUtilities.DefaultOptions);
            if (JsonSerializer.SerializeToUtf8Bytes(output).Length >
                ToolRuntimeLimits.MaximumBindingResultBytes)
            {
                throw new McpCapabilityException(
                    ToolErrorCodes.OutputLimitExceeded,
                    "MCP Resource exceeds the output limit.");
            }

            return output;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (McpCapabilityException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is McpException or JsonException or IOException or
                InvalidOperationException)
        {
            throw new McpCapabilityException(
                McpToolErrorCodes.InvalidResponse,
                "MCP Resource response is invalid.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _lifetime.CancelAsync();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            RemovePublished();
            await CleanupAsync();
            SetStatus(McpSessionStatus.Stopped);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask RefreshFromNotificationAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await RefreshAsync(cancellationToken);
            if (Changed is not null)
            {
                await Changed(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is McpException or JsonException or IOException or
                InvalidOperationException or ArgumentException)
        {
            await MarkDisconnectedAsync();
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var client = _client ?? throw new InvalidOperationException();
            var remoteTools = (await client.ListToolsAsync(
                    cancellationToken: cancellationToken))
                .OrderBy(tool => tool.ProtocolTool.Name, StringComparer.Ordinal)
                .Take(MaximumTools)
                .ToArray();
            var registrations = new List<ToolRegistration>();
            var bindings = new List<ToolRuntimeBinding>();
            foreach (var remote in remoteTools)
            {
                var definition = new ToolDefinition(
                    new ToolDefinitionId(
                        ToolSourceKind.Mcp,
                        Definition.Id,
                        remote.ProtocolTool.Name),
                    new ToolName(
                        Namespace(Definition.Id),
                        ToolNamePart(remote.ProtocolTool.Name)),
                    Limit(remote.Description, 4096),
                    NormalizeSchema(remote.JsonSchema),
                    ConservativeEffects,
                    ToolReplaySafety.Unsafe);
                if (!ToolRuntime.IsValidDefinition(definition))
                {
                    continue;
                }

                var bindingId = new RuntimeBindingId(
                    $"mcp:{Definition.Id}:{remote.ProtocolTool.Name}");
                registrations.Add(new ToolRegistration(
                    definition,
                    bindingId,
                    ToolExposure.Direct,
                    ToolInvocationAudience.Model |
                    ToolInvocationAudience.Host |
                    ToolInvocationAudience.App,
                    Generation));
                bindings.Add(new ToolRuntimeBinding(
                    bindingId,
                    ToolBindingAvailability.Available,
                    Lease: null,
                    TimeSpan.FromSeconds(30),
                    (arguments, token) => InvokeAsync(
                        remote.ProtocolTool.Name,
                        arguments,
                        token),
                    Generation,
                    IsTrusted: true));
            }

            var remoteResources = (await client.ListResourcesAsync(
                    cancellationToken: cancellationToken))
                .OrderBy(resource => resource.Uri, StringComparer.Ordinal)
                .Take(MaximumResources)
                .Select(resource => new McpResourceSummary(
                    Definition.Id,
                    resource.Uri,
                    resource.Name,
                    resource.Description,
                    resource.MimeType,
                    Generation))
                .ToArray();

            RemovePublished();
            if (registrations.Count != 0)
            {
                _tools.PublishMcp(
                    Definition.Id,
                    registrations,
                    bindings);
            }

            _registrations = registrations.ToArray();
            _bindings = bindings.ToArray();
            Volatile.Write(ref _resources, remoteResources);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<ToolBindingResult> InvokeAsync(
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var client = _client;
        if (Status != McpSessionStatus.Ready || client is null)
        {
            return Failure(
                McpToolErrorCodes.Disconnected,
                "MCP Server is disconnected.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        var requestId = new RequestId(
            Interlocked.Increment(ref _nextRequestId));
        var request = new JsonRpcRequest
        {
            Id = requestId,
            Method = "tools/call",
            Params = JsonSerializer.SerializeToNode(
                new CallToolRequestParams
                {
                    Name = toolName,
                    Arguments = arguments.EnumerateObject().ToDictionary(
                        property => property.Name,
                        property => property.Value.Clone(),
                        StringComparer.Ordinal),
                },
                McpJsonUtilities.DefaultOptions),
        };
        try
        {
            var response = await client.SendRequestAsync(request, linked.Token);
            var result = response.Result?.Deserialize<CallToolResult>(
                McpJsonUtilities.DefaultOptions);
            if (result is null)
            {
                return Failure(
                    McpToolErrorCodes.InvalidResponse,
                    "MCP Tool response is invalid.");
            }

            if (result.IsError == true)
            {
                return Failure(
                    McpToolErrorCodes.CallFailed,
                    "MCP Tool reported a failure.");
            }

            return ToolBindingResult.Success(JsonSerializer.SerializeToElement(
                result,
                McpJsonUtilities.DefaultOptions));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return Failure(
                McpToolErrorCodes.Disconnected,
                "MCP Server was disconnected.");
        }
        catch (OperationCanceledException)
        {
            await NotifyCancellationAsync(client, requestId);
            throw;
        }
        catch (Exception exception) when (
            exception is McpException or JsonException or IOException or
                InvalidOperationException)
        {
            return Failure(
                McpToolErrorCodes.CallFailed,
                "MCP Tool call failed.");
        }
    }

    private static async Task NotifyCancellationAsync(
        McpClient client,
        RequestId requestId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            await client.SendNotificationAsync(
                "notifications/cancelled",
                new CancelledNotificationParams
                {
                    RequestId = requestId,
                    Reason = "OpenCoWork Tool invocation was cancelled.",
                },
                McpJsonUtilities.DefaultOptions,
                timeout.Token);
        }
        catch (Exception exception) when (
            exception is McpException or IOException or
                OperationCanceledException or InvalidOperationException)
        {
        }
    }

    private async Task MonitorCompletionAsync(McpClient client)
    {
        try
        {
            await client.Completion;
        }
        catch (Exception exception) when (
            exception is McpException or IOException or InvalidOperationException)
        {
        }

        if (!_lifetime.IsCancellationRequested)
        {
            await MarkDisconnectedAsync();
        }
    }

    private async Task MarkDisconnectedAsync()
    {
        await _lifetime.CancelAsync();
        await _gate.WaitAsync();
        try
        {
            RemovePublished();
            SetStatus(McpSessionStatus.Disconnected);
        }
        finally
        {
            _gate.Release();
        }

        if (Changed is not null)
        {
            await Changed(CancellationToken.None);
        }
    }

    private void RemovePublished()
    {
        foreach (var binding in _bindings)
        {
            _tools.RemoveBinding(binding.Id, binding.Generation);
        }

        _tools.RemoveMcp(Definition.Id);
        _registrations = [];
        _bindings = [];
        Volatile.Write(ref _resources, []);
    }

    private async Task CleanupAsync()
    {
        foreach (var handler in _notificationHandlers)
        {
            await handler.DisposeAsync();
        }

        _notificationHandlers.Clear();
        if (_client is not null)
        {
            await _client.DisposeAsync();
            _client = null;
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }

    private void SetStatus(McpSessionStatus status) =>
        Volatile.Write(ref _status, (int)status);

    private static JsonElement NormalizeSchema(JsonElement schema)
    {
        var node = JsonNode.Parse(schema.GetRawText()) as JsonObject
            ?? new JsonObject();
        node["$schema"] ??=
            "https://json-schema.org/draft/2020-12/schema";
        node["type"] ??= "object";
        node["additionalProperties"] ??= false;
        return JsonSerializer.SerializeToElement(node);
    }

    private static string Namespace(string serverId) =>
        "mcp_" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(serverId)))
            .ToLowerInvariant()[..12];

    private static string ToolNamePart(string name)
    {
        var normalized = new string(name
            .Select(character =>
                char.IsAsciiLetterOrDigit(character)
                    ? char.ToLowerInvariant(character)
                    : '_')
            .Take(58)
            .ToArray());
        if (normalized.Length == 0 || !char.IsAsciiLetter(normalized[0]))
        {
            normalized = "tool_" + normalized;
        }

        return normalized;
    }

    private static string Limit(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];

    private static ToolBindingResult Failure(string code, string message) =>
        ToolBindingResult.Failure(new SessionError(
            code,
            message,
            IsRetryable: false));
}

internal sealed class McpConnection : IAsyncDisposable
{
    private readonly IAsyncDisposable? _transportLifetime;
    private readonly Process? _process;
    private readonly Task? _stderr;
    private readonly IReadOnlyList<IDisposable> _disposables;
    private int _disposed;

    public McpConnection(
        IClientTransport transport,
        IAsyncDisposable? transportLifetime,
        Process? process,
        Task? stderr,
        IReadOnlyList<IDisposable> disposables)
    {
        Transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _transportLifetime = transportLifetime;
        _process = process;
        _stderr = stderr;
        _disposables = disposables ??
            throw new ArgumentNullException(nameof(disposables));
    }

    public IClientTransport Transport { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_transportLifetime is not null)
        {
            await _transportLifetime.DisposeAsync();
        }

        if (_process is not null)
        {
            await KillAsync(_process);
            _process.Dispose();
        }

        if (_stderr is not null)
        {
            try
            {
                await _stderr;
            }
            catch (Exception exception) when (
                exception is IOException or DecoderFallbackException)
            {
            }
        }

        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    internal static async Task KillAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync()
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
                System.ComponentModel.Win32Exception or
                NotSupportedException or TimeoutException)
        {
        }
    }
}

internal sealed class McpOAuthTokenCache(
    ProviderAuthService auth,
    string profileId)
    : ITokenCache, IDisposable
{
    private ProviderSecretLease? _lease;

    public ValueTask StoreTokensAsync(
        TokenContainer tokens,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        auth.Set(
            profileId,
            JsonSerializer.Serialize(tokens, McpJsonUtilities.DefaultOptions));
        return ValueTask.CompletedTask;
    }

    public ValueTask<TokenContainer?> GetTokensAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _lease?.Dispose();
        _lease = auth.AcquireStored(profileId);
        if (string.IsNullOrWhiteSpace(_lease.Secret))
        {
            return ValueTask.FromResult<TokenContainer?>(null);
        }

        try
        {
            return ValueTask.FromResult(JsonSerializer.Deserialize<TokenContainer>(
                _lease.Secret,
                McpJsonUtilities.DefaultOptions));
        }
        catch (JsonException)
        {
            return ValueTask.FromResult<TokenContainer?>(null);
        }
    }

    public void Dispose()
    {
        _lease?.Dispose();
        _lease = null;
    }
}
