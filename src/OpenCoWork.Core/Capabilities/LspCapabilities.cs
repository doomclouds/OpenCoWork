using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Tools;
using OpenCoWork.Core.Workspaces;

namespace OpenCoWork.Core.Capabilities;

internal static class LspCapabilityErrorCodes
{
    public const string ConfigurationInvalid = "lsp.configurationInvalid";
    public const string ConnectionFailed = "lsp.connectionFailed";
    public const string Disconnected = "lsp.disconnected";
    public const string MethodDenied = "lsp.methodDenied";
    public const string SelectorMismatch = "lsp.selectorMismatch";
    public const string InvalidResponse = "lsp.invalidResponse";
    public const string RequestFailed = "lsp.requestFailed";
}

internal enum LspSessionStatus
{
    Starting,
    Initializing,
    Ready,
    Faulted,
    Stopped,
}

internal sealed record LspEnvironmentValue(string? Literal, string? SecretRef);

internal sealed record LspSelector(
    string LanguageId,
    IReadOnlyList<string> Extensions);

internal sealed record LspServerDefinition(
    string Id,
    bool Enabled,
    IReadOnlyList<LspSelector> Selectors,
    string Command,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, LspEnvironmentValue> Environment,
    string ConfigurationSha256,
    string? ResolvedCommand = null,
    string? TrustVersion = null,
    string? ExecutableSha256 = null,
    string? DiagnosticCode = null);

internal sealed record LspDiscoveryResult(
    IReadOnlyList<CapabilityContributionSet> Contributions);

internal sealed record LspRequest(
    string ServerId,
    string Method,
    string? Path = null,
    int? Line = null,
    int? Character = null,
    string? Query = null);

internal sealed record LspTrustIdentity(
    string ServerId,
    string SourceId,
    string Version,
    string ExecutablePath,
    string Sha256);

internal sealed class LspCapabilityException(string code, string message)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

internal sealed partial class LspCapabilitySource
{
    private const int MaximumFileBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Regex ExternalIdPattern = new(
        @"^[a-z0-9][a-z0-9.-]{0,62}/[a-z0-9][a-z0-9.-]{0,62}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex LanguageIdPattern = new(
        @"^[A-Za-z0-9][A-Za-z0-9.+-]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex ExtensionPattern = new(
        @"^\.[A-Za-z0-9][A-Za-z0-9.+-]{0,31}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private readonly ProviderAuthService? _auth;
    private readonly CapabilityFileStore _files;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, long> _generations =
        new(StringComparer.Ordinal);
    private readonly OpenCoWorkPaths _paths;
    private readonly ConcurrentDictionary<string, LspServerSession> _sessions =
        new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, LspServerDefinition> _definitions =
        new Dictionary<string, LspServerDefinition>(StringComparer.Ordinal);

    public LspCapabilitySource(
        OpenCoWorkPaths paths,
        CapabilityFileStore files,
        ProviderAuthService? auth)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _auth = auth;
    }

    internal Func<CancellationToken, Task>? Changed { get; set; }

    public async Task<LspDiscoveryResult> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            IReadOnlyList<LspServerDefinition> definitions;
            try
            {
                definitions = await LoadDefinitionsAsync(cancellationToken);
            }
            catch (LspCapabilityException exception)
            {
                await StopAllAsync(cancellationToken);
                _definitions = new Dictionary<string, LspServerDefinition>(
                    StringComparer.Ordinal);
                return InvalidDiscovery(exception.Code);
            }

            var resolved = new List<LspServerDefinition>();
            var contributions = new List<CapabilityContributionSet>();
            var trust = await _files.LoadTrustDecisionsAsync(cancellationToken);
            foreach (var configured in definitions.OrderBy(
                         definition => definition.Id,
                         StringComparer.Ordinal))
            {
                if (configured.DiagnosticCode is { } diagnosticCode)
                {
                    await StopAndRemoveAsync(configured.Id, cancellationToken);
                    contributions.Add(Contribution(
                        configured,
                        CapabilityStatus.Faulted,
                        generation: 0,
                        [diagnosticCode],
                        []));
                    continue;
                }

                LspServerDefinition definition;
                try
                {
                    definition = ResolveCommand(configured);
                    resolved.Add(definition);
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
                        [LspCapabilityErrorCodes.ConfigurationInvalid]));
                    continue;
                }

                if (!definition.Enabled)
                {
                    await StopAndRemoveAsync(definition.Id, cancellationToken);
                    contributions.Add(Contribution(
                        definition,
                        CapabilityStatus.Disabled,
                        generation: 0,
                        []));
                    continue;
                }

                if (!IsTrusted(definition, trust))
                {
                    await StopAndRemoveAsync(definition.Id, cancellationToken);
                    contributions.Add(Contribution(
                        definition,
                        CapabilityStatus.PendingTrust,
                        generation: 0,
                        [ToolErrorCodes.TrustRequired],
                        [CapabilityTrustScope.OutOfProcess]));
                    continue;
                }

                if (_sessions.TryGetValue(definition.Id, out var current) &&
                    string.Equals(
                        current.Definition.ConfigurationSha256,
                        definition.ConfigurationSha256,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        current.Definition.TrustVersion,
                        definition.TrustVersion,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        current.Definition.ExecutableSha256,
                        definition.ExecutableSha256,
                        StringComparison.Ordinal) &&
                    current.Status == LspSessionStatus.Ready)
                {
                    contributions.Add(current.CreateContribution());
                    continue;
                }

                await StopAndRemoveAsync(definition.Id, cancellationToken);
                var session = new LspServerSession(
                    definition,
                    NextGeneration(definition.Id),
                    _paths,
                    ConnectAsync)
                {
                    Changed = NotifyChangedAsync,
                };
                _sessions[definition.Id] = session;
                await session.StartAsync(cancellationToken);
                contributions.Add(session.CreateContribution());
            }

            var retained = resolved
                .Select(definition => definition.Id)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var removed in _sessions.Keys
                         .Where(id => !retained.Contains(id))
                         .ToArray())
            {
                await StopAndRemoveAsync(removed, cancellationToken);
            }

            _definitions = resolved.ToDictionary(
                definition => definition.Id,
                StringComparer.Ordinal);
            return new LspDiscoveryResult(
                Array.AsReadOnly(contributions.ToArray()));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LspTrustIdentity> InspectAsync(
        string serverId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        var definition = (await LoadDefinitionsAsync(cancellationToken))
            .SingleOrDefault(item =>
                string.Equals(item.Id, serverId, StringComparison.Ordinal)) ??
            throw Error(
                LspCapabilityErrorCodes.ConfigurationInvalid,
                "LSP Server definition was not found.");
        var resolved = ResolveCommand(definition);
        return new LspTrustIdentity(
            resolved.Id,
            resolved.Id,
            resolved.TrustVersion!,
            resolved.ResolvedCommand!,
            resolved.ExecutableSha256!);
    }

    public async Task<JsonElement> RequestAsync(
        LspRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_definitions.TryGetValue(request.ServerId, out var definition))
        {
            throw Error(
                LspCapabilityErrorCodes.Disconnected,
                "LSP Server is disconnected.");
        }

        var prepared = await PrepareRequestAsync(
            definition,
            request,
            cancellationToken);
        if (!_sessions.TryGetValue(request.ServerId, out var session) ||
            session.Status != LspSessionStatus.Ready)
        {
            throw Error(
                LspCapabilityErrorCodes.Disconnected,
                "LSP Server is disconnected.");
        }

        var result = await session.RequestAsync(prepared, cancellationToken);
        ValidateFileUris(result);
        return result;
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
                throw Error(
                    LspCapabilityErrorCodes.Disconnected,
                    "LSP Server generation no longer matches.");
            }

            var definition = current.Definition;
            await StopAndRemoveAsync(serverId, cancellationToken);
            var replacement = new LspServerSession(
                definition,
                NextGeneration(serverId),
                _paths,
                ConnectAsync)
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

    private async Task<IReadOnlyList<LspServerDefinition>> LoadDefinitionsAsync(
        CancellationToken cancellationToken)
    {
        var resolved = WorkspacePathGuard.ResolveContained(
            _paths.WorkspaceRoot,
            Path.Combine(_paths.WorkspaceRoot, ".opencowork-lsp-anchor"),
            Path.GetRelativePath(_paths.WorkspaceRoot, _paths.LspPath));
        if (!File.Exists(resolved.PhysicalPath))
        {
            return [];
        }

        try
        {
            var info = new FileInfo(resolved.PhysicalPath);
            if (info.Length > MaximumFileBytes)
            {
                throw InvalidConfiguration();
            }

            var bytes = await File.ReadAllBytesAsync(
                resolved.PhysicalPath,
                cancellationToken);
            if (bytes.Length > MaximumFileBytes)
            {
                throw InvalidConfiguration();
            }

            _ = StrictUtf8.GetString(bytes);
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

            var definitions = servers.EnumerateArray()
                .Select((server, index) =>
                {
                    try
                    {
                        return ParseServer(server);
                    }
                    catch (LspCapabilityException)
                    {
                        return InvalidDefinition(server, index);
                    }
                })
                .ToArray();
            var duplicates = definitions
                .GroupBy(definition => definition.Id, StringComparer.Ordinal)
                .Where(group => group.Skip(1).Any())
                .SelectMany(group => group)
                .ToHashSet();
            if (duplicates.Count != 0)
            {
                definitions = definitions
                    .Select((definition, index) =>
                        duplicates.Contains(definition)
                            ? definition with
                            {
                                Id = $"workspace/lsp-duplicate-{index + 1}",
                                DiagnosticCode =
                                    LspCapabilityErrorCodes.ConfigurationInvalid,
                            }
                            : definition)
                    .ToArray();
            }

            return Array.AsReadOnly(definitions);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LspCapabilityException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                JsonException or DecoderFallbackException or
                ArgumentException or InvalidOperationException)
        {
            throw InvalidConfiguration();
        }
    }

    private LspServerDefinition ParseServer(JsonElement element)
    {
        RequireObject(
            element,
            [
                "id",
                "enabled",
                "selectors",
                "command",
                "arguments",
                "workingDirectory",
                "environment",
            ]);
        var id = RequireString(element, "id");
        if (!ExternalIdPattern.IsMatch(id) ||
            !string.Equals(
                RequireString(element, "workingDirectory"),
                "workspace",
                StringComparison.Ordinal))
        {
            throw InvalidConfiguration();
        }

        var selectors = RequireArray(element, "selectors")
            .EnumerateArray()
            .Select(ParseSelector)
            .ToArray();
        if (selectors.Length is < 1 or > 32)
        {
            throw InvalidConfiguration();
        }

        var command = RequireString(element, "command");
        if (command.Length > 4096 ||
            command.Contains('\0', StringComparison.Ordinal))
        {
            throw InvalidConfiguration();
        }

        var arguments = RequireArray(element, "arguments")
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

        var environment = ParseEnvironment(element.GetProperty("environment"));
        return new LspServerDefinition(
            id,
            RequireBoolean(element, "enabled"),
            Array.AsReadOnly(selectors),
            command,
            Array.AsReadOnly(arguments),
            environment,
            Hash(JsonSerializer.SerializeToUtf8Bytes(element)));
    }

    private static LspServerDefinition InvalidDefinition(
        JsonElement element,
        int index)
    {
        var id = element.ValueKind == JsonValueKind.Object &&
                 element.TryGetProperty("id", out var configuredId) &&
                 configuredId.ValueKind == JsonValueKind.String &&
                 ExternalIdPattern.IsMatch(configuredId.GetString() ?? string.Empty)
            ? configuredId.GetString()!
            : $"workspace/lsp-invalid-{index + 1}";
        return new LspServerDefinition(
            id,
            Enabled: false,
            [],
            string.Empty,
            [],
            new Dictionary<string, LspEnvironmentValue>(StringComparer.Ordinal),
            Hash(JsonSerializer.SerializeToUtf8Bytes(element)),
            DiagnosticCode: LspCapabilityErrorCodes.ConfigurationInvalid);
    }

    private static LspSelector ParseSelector(JsonElement element)
    {
        RequireObject(element, ["languageId", "extensions"]);
        var languageId = RequireString(element, "languageId");
        var extensions = RequireArray(element, "extensions")
            .EnumerateArray()
            .Select(extension =>
                extension.ValueKind == JsonValueKind.String
                    ? extension.GetString()!
                    : throw InvalidConfiguration())
            .ToArray();
        if (!LanguageIdPattern.IsMatch(languageId) ||
            extensions.Length is < 1 or > 32 ||
            extensions.Any(extension => !ExtensionPattern.IsMatch(extension)) ||
            extensions.Distinct(StringComparer.Ordinal).Count() != extensions.Length)
        {
            throw InvalidConfiguration();
        }

        return new LspSelector(languageId, Array.AsReadOnly(extensions));
    }

    private static IReadOnlyDictionary<string, LspEnvironmentValue> ParseEnvironment(
        JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            element.EnumerateObject().Count() > 64)
        {
            throw InvalidConfiguration();
        }

        var environment =
            new Dictionary<string, LspEnvironmentValue>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
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
                new LspEnvironmentValue(literal, secretRef));
        }

        return environment;
    }

    private LspServerDefinition ResolveCommand(LspServerDefinition definition)
    {
        string? resolved;
        if (definition.Command.IndexOfAny(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            resolved = WorkspacePathGuard.ResolveContained(
                _paths.WorkspaceRoot,
                Path.Combine(_paths.WorkspaceRoot, ".opencowork-lsp-source"),
                definition.Command).PhysicalPath;
        }
        else
        {
            resolved = FindOnPath(definition.Command);
        }

        if (resolved is null || !File.Exists(resolved))
        {
            throw new FileNotFoundException("LSP executable was not found.");
        }

        using var stream = File.OpenRead(resolved);
        var digest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        var info = new FileInfo(resolved);
        var versionInfo = FileVersionInfo.GetVersionInfo(resolved);
        var version = versionInfo.ProductVersion ??
                      versionInfo.FileVersion ??
                      $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        return definition with
        {
            ResolvedCommand = resolved,
            TrustVersion = $"{resolved}|{version}",
            ExecutableSha256 = digest,
        };
    }

    private bool IsTrusted(
        LspServerDefinition definition,
        TrustDecisionsDocument trust)
    {
        var decision = trust.Decisions.SingleOrDefault(item =>
            item.Matches(
                _paths.WorkspaceRoot,
                CapabilitySourceKind.Workspace,
                definition.Id,
                definition.TrustVersion,
                definition.ExecutableSha256!));
        return decision is not null &&
               decision.AllowedScopes.Contains(CapabilityTrustScope.OutOfProcess) &&
               !decision.DeniedScopes.Contains(CapabilityTrustScope.OutOfProcess);
    }

    private async Task<LspConnection> ConnectAsync(
        LspServerDefinition definition,
        CancellationToken cancellationToken)
    {
        var leases = new List<IDisposable>();
        Process? process = null;
        try
        {
            definition = ResolveCommand(definition);
            if (!IsTrusted(
                    definition,
                    await _files.LoadTrustDecisionsAsync(cancellationToken)))
            {
                throw Error(
                    ToolErrorCodes.TrustRequired,
                    "LSP executable requires trust.");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = definition.ResolvedCommand!,
                WorkingDirectory = _paths.WorkspaceRoot,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
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
                    if (_auth is null)
                    {
                        throw InvalidConfiguration();
                    }

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
                throw new IOException("LSP process did not start.");
            }

            return new LspConnection(
                process,
                process.StandardInput.BaseStream,
                process.StandardOutput.BaseStream,
                DrainAsync(process.StandardError.BaseStream),
                leases);
        }
        catch
        {
            if (process is not null)
            {
                await LspConnection.KillAsync(process);
                process.Dispose();
            }

            foreach (var lease in leases)
            {
                lease.Dispose();
            }

            throw;
        }
    }

    private async Task<LspPreparedRequest> PrepareRequestAsync(
        LspServerDefinition definition,
        LspRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ServerId) ||
            string.IsNullOrWhiteSpace(request.Method))
        {
            throw Error(
                LspCapabilityErrorCodes.MethodDenied,
                "LSP request is invalid.");
        }

        if (request.Method == "workspaceSymbol")
        {
            if (request.Path is not null ||
                request.Line is not null ||
                request.Character is not null ||
                request.Query is null ||
                request.Query.Length > 256)
            {
                throw Error(
                    LspCapabilityErrorCodes.MethodDenied,
                    "LSP workspace symbol request is invalid.");
            }

            return new LspPreparedRequest(
                "workspace/symbol",
                JsonSerializer.SerializeToElement(new { query = request.Query }),
                DidOpen: null);
        }

        if (request.Method is not (
                "hover" or
                "definition" or
                "references" or
                "documentSymbol" or
                "diagnostic"))
        {
            throw Error(
                LspCapabilityErrorCodes.MethodDenied,
                "LSP method is denied.");
        }

        if (string.IsNullOrWhiteSpace(request.Path) ||
            Path.IsPathRooted(request.Path))
        {
            throw Error(
                ToolErrorCodes.PathDenied,
                "LSP document path is denied.");
        }

        ResolvedWorkspacePath resolved;
        try
        {
            resolved = WorkspacePathGuard.ResolveContained(
                _paths.WorkspaceRoot,
                Path.Combine(_paths.WorkspaceRoot, ".opencowork-lsp-request"),
                request.Path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException)
        {
            throw Error(
                ToolErrorCodes.PathDenied,
                "LSP document path is denied.");
        }

        if (!File.Exists(resolved.PhysicalPath))
        {
            throw Error(
                ToolErrorCodes.PathNotFound,
                "LSP document was not found.");
        }

        var extension = Path.GetExtension(resolved.PhysicalPath);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var selector = definition.Selectors.SingleOrDefault(candidate =>
            candidate.Extensions.Any(configured =>
                string.Equals(configured, extension, comparison)));
        if (selector is null)
        {
            throw Error(
                LspCapabilityErrorCodes.SelectorMismatch,
                "LSP selector does not match the document.");
        }

        var info = new FileInfo(resolved.PhysicalPath);
        if (info.Length > MaximumFileBytes)
        {
            throw Error(
                ToolErrorCodes.OutputLimitExceeded,
                "LSP document exceeds the size limit.");
        }

        string text;
        try
        {
            var bytes = await File.ReadAllBytesAsync(
                resolved.PhysicalPath,
                cancellationToken);
            if (bytes.Length > MaximumFileBytes)
            {
                throw Error(
                    ToolErrorCodes.OutputLimitExceeded,
                    "LSP document exceeds the size limit.");
            }

            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw Error(
                ToolErrorCodes.ContentUnsupported,
                "LSP document is not valid UTF-8 text.");
        }

        var uri = new Uri(resolved.PhysicalPath).AbsoluteUri;
        var didOpen = JsonSerializer.SerializeToElement(new
        {
            textDocument = new
            {
                uri,
                languageId = selector.LanguageId,
                version = 1,
                text,
            },
        });
        if (request.Method is "hover" or "definition" or "references")
        {
            if (request.Line is null or < 0 ||
                request.Character is null or < 0 ||
                request.Query is not null)
            {
                throw Error(
                    LspCapabilityErrorCodes.MethodDenied,
                    "LSP position request is invalid.");
            }

            var position = new
            {
                line = request.Line.Value,
                character = request.Character.Value,
            };
            return new LspPreparedRequest(
                request.Method switch
                {
                    "hover" => "textDocument/hover",
                    "definition" => "textDocument/definition",
                    _ => "textDocument/references",
                },
                request.Method == "references"
                    ? JsonSerializer.SerializeToElement(new
                    {
                        textDocument = new { uri },
                        position,
                        context = new { includeDeclaration = true },
                    })
                    : JsonSerializer.SerializeToElement(new
                    {
                        textDocument = new { uri },
                        position,
                    }),
                didOpen);
        }

        if (request.Line is not null ||
            request.Character is not null ||
            request.Query is not null)
        {
            throw Error(
                LspCapabilityErrorCodes.MethodDenied,
                "LSP document request is invalid.");
        }

        return new LspPreparedRequest(
            request.Method == "documentSymbol"
                ? "textDocument/documentSymbol"
                : "textDocument/diagnostic",
            JsonSerializer.SerializeToElement(new
            {
                textDocument = new { uri },
            }),
            didOpen);
    }

    private void ValidateFileUris(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (property.Name is "uri" or "targetUri" &&
                    property.Value.ValueKind == JsonValueKind.String &&
                    Uri.TryCreate(
                        property.Value.GetString(),
                        UriKind.Absolute,
                        out var uri) &&
                    uri.IsFile)
                {
                    ValidateFileUri(uri);
                }

                ValidateFileUris(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                ValidateFileUris(item);
            }
        }
    }

    private void ValidateFileUri(Uri uri)
    {
        try
        {
            var root = WorkspacePathGuard.ResolveContained(
                _paths.WorkspaceRoot,
                Path.Combine(_paths.WorkspaceRoot, ".opencowork-lsp-uri"),
                ".").PhysicalRoot;
            var relative = Path.GetRelativePath(root, uri.LocalPath);
            _ = WorkspacePathGuard.ResolveContained(
                _paths.WorkspaceRoot,
                Path.Combine(_paths.WorkspaceRoot, ".opencowork-lsp-uri"),
                relative);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException)
        {
            throw Error(
                LspCapabilityErrorCodes.InvalidResponse,
                "LSP response contains an external File URI.");
        }
    }

    private static CapabilityContributionSet Contribution(
        LspServerDefinition definition,
        CapabilityStatus status,
        long generation,
        IReadOnlyList<string> diagnostics,
        IReadOnlyList<CapabilityTrustScope>? requiredTrust = null) =>
        new(
            new CapabilitySourceDescriptor(
                CapabilitySourceKind.Lsp,
                $"lsp:{definition.Id}",
                version: null,
                definition.ConfigurationSha256),
            [
                new CapabilityContribution(
                    CapabilityKind.LspServer,
                    definition.Id,
                    definition.Id,
                    $"Read-only LSP for {string.Join(
                        ", ",
                        definition.Selectors.Select(selector =>
                            $"{selector.LanguageId} ({string.Join(
                                ", ",
                                selector.Extensions)})"))}.",
                    status,
                    requiredTrust ?? [CapabilityTrustScope.OutOfProcess],
                    generation,
                    diagnostics),
            ]);

    private static LspDiscoveryResult InvalidDiscovery(string code)
    {
        var definition = new LspServerDefinition(
            "workspace/lsp-config",
            Enabled: false,
            [],
            string.Empty,
            [],
            new Dictionary<string, LspEnvironmentValue>(StringComparer.Ordinal),
            new string('0', 64));
        return new LspDiscoveryResult(
        [
            Contribution(
                definition,
                CapabilityStatus.Faulted,
                generation: 0,
                [code],
                []),
        ]);
    }

    private long NextGeneration(string serverId)
    {
        var next = checked(_generations.GetValueOrDefault(serverId) + 1);
        _generations[serverId] = next;
        return next;
    }

    private async Task StopAndRemoveAsync(
        string serverId,
        CancellationToken cancellationToken)
    {
        if (_sessions.TryRemove(serverId, out var session))
        {
            await session.StopAsync(cancellationToken);
        }
    }

    private async Task StopAllAsync(CancellationToken cancellationToken)
    {
        foreach (var serverId in _sessions.Keys.Order(StringComparer.Ordinal).ToArray())
        {
            await StopAndRemoveAsync(serverId, cancellationToken);
        }
    }

    private Task NotifyChangedAsync(CancellationToken cancellationToken) =>
        Changed?.Invoke(cancellationToken) ?? Task.CompletedTask;

    private static async Task DrainAsync(Stream stream)
    {
        try
        {
            await stream.CopyToAsync(Stream.Null, CancellationToken.None);
        }
        catch (Exception exception) when (
            exception is IOException or ObjectDisposedException)
        {
        }
    }

    private static string? FindOnPath(string fileName)
    {
        var extensions = OperatingSystem.IsWindows() &&
                         Path.GetExtension(fileName).Length == 0
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE")
                .Split(
                    ';',
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries)
            : [string.Empty];
        foreach (var directory in (
                     Environment.GetEnvironmentVariable("PATH") ??
                     string.Empty)
                 .Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries |
                     StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.GetFullPath(
                    Path.Combine(directory, fileName + extension));
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static bool IsSensitiveEnvironmentName(string name)
    {
        var normalized = new string(
            name.Where(char.IsLetterOrDigit)
                .Select(char.ToUpperInvariant)
                .ToArray());
        return new[]
        {
            "PASSWORD",
            "TOKEN",
            "SECRET",
            "APIKEY",
            "CREDENTIAL",
            "AUTHORIZATION",
        }.Any(marker => normalized.Contains(marker, StringComparison.Ordinal));
    }

    private static bool IsEnvironmentName(string value) =>
        value is { Length: >= 1 and <= 128 } &&
        (char.IsLetter(value[0]) || value[0] == '_') &&
        value.Skip(1).All(character =>
            char.IsLetterOrDigit(character) || character == '_');

    internal static void EnsureUniqueProperties(JsonElement element)
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

    private static void RequireObject(
        JsonElement element,
        IReadOnlyCollection<string> allowed)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw InvalidConfiguration();
        }

        var names = element.EnumerateObject()
            .Select(property => property.Name)
            .ToArray();
        if (names.Any(name => !allowed.Contains(name, StringComparer.Ordinal)) ||
            allowed.Any(name => !names.Contains(name, StringComparer.Ordinal) &&
                name is not ("literal" or "secretRef")))
        {
            throw InvalidConfiguration();
        }
    }

    private static JsonElement RequireArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            throw InvalidConfiguration();
        }

        return value;
    }

    private static string RequireString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()) ||
            value.GetString()!.Any(char.IsControl))
        {
            throw InvalidConfiguration();
        }

        return value.GetString()!;
    }

    private static bool RequireBoolean(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw InvalidConfiguration();
        }

        return value.GetBoolean();
    }

    private static int RequireInt32(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) ||
            !value.TryGetInt32(out var result))
        {
            throw InvalidConfiguration();
        }

        return result;
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static LspCapabilityException InvalidConfiguration() =>
        Error(
            LspCapabilityErrorCodes.ConfigurationInvalid,
            "LSP configuration is invalid.");

    private static LspCapabilityException Error(string code, string message) =>
        new(code, message);
}

internal sealed record LspPreparedRequest(
    string Method,
    JsonElement Parameters,
    JsonElement? DidOpen);

internal sealed class LspServerSession
{
    private readonly Func<
        LspServerDefinition,
        CancellationToken,
        Task<LspConnection>> _connect;
    private readonly OpenCoWorkPaths _paths;
    private LspConnection? _connection;

    public LspServerSession(
        LspServerDefinition definition,
        long generation,
        OpenCoWorkPaths paths,
        Func<LspServerDefinition, CancellationToken, Task<LspConnection>> connect)
    {
        Definition = definition;
        Generation = generation;
        _paths = paths;
        _connect = connect;
    }

    public LspServerDefinition Definition { get; }

    public long Generation { get; }

    public LspSessionStatus Status { get; private set; } = LspSessionStatus.Starting;

    internal Func<CancellationToken, Task>? Changed { get; set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Status = LspSessionStatus.Starting;
            _connection = await _connect(Definition, cancellationToken);
            Status = LspSessionStatus.Initializing;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeout.Token);
            var rootUri = new Uri(
                WorkspacePathGuard.ResolveContained(
                    _paths.WorkspaceRoot,
                    Path.Combine(_paths.WorkspaceRoot, ".opencowork-lsp-root"),
                    ".").PhysicalPath).AbsoluteUri;
            var initialized = await _connection.SendRequestAsync(
                "initialize",
                JsonSerializer.SerializeToElement(new
                {
                    processId = Environment.ProcessId,
                    clientInfo = new
                    {
                        name = "OpenCoWork",
                        version = "1.0",
                    },
                    rootUri,
                    capabilities = new
                    {
                        workspace = new
                        {
                            workspaceFolders = true,
                        },
                        textDocument = new
                        {
                            hover = new { },
                            definition = new { },
                            references = new { },
                            documentSymbol = new { },
                            diagnostic = new { },
                        },
                    },
                    workspaceFolders = new[]
                    {
                        new
                        {
                            uri = rootUri,
                            name = Path.GetFileName(
                                Path.TrimEndingDirectorySeparator(
                                    _paths.WorkspaceRoot)),
                        },
                    },
                }),
                linked.Token);
            if (initialized.ValueKind != JsonValueKind.Object ||
                !initialized.TryGetProperty("capabilities", out var capabilities) ||
                capabilities.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("LSP initialize response is invalid.");
            }

            await _connection.SendNotificationAsync(
                "initialized",
                JsonSerializer.SerializeToElement(new { }),
                linked.Token);
            Status = LspSessionStatus.Ready;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Status = LspSessionStatus.Stopped;
            await DisposeConnectionAsync();
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or InvalidDataException or
                InvalidOperationException or UnauthorizedAccessException)
        {
            Status = LspSessionStatus.Faulted;
            await DisposeConnectionAsync();
        }
    }

    public async Task<JsonElement> RequestAsync(
        LspPreparedRequest request,
        CancellationToken cancellationToken)
    {
        var connection = _connection;
        if (Status != LspSessionStatus.Ready || connection is null)
        {
            throw new LspCapabilityException(
                LspCapabilityErrorCodes.Disconnected,
                "LSP Server is disconnected.");
        }

        try
        {
            if (request.DidOpen is { } didOpen)
            {
                await connection.SendNotificationAsync(
                    "textDocument/didOpen",
                    didOpen,
                    cancellationToken);
            }

            return await connection.SendRequestAsync(
                request.Method,
                request.Parameters,
                cancellationToken);
        }
        catch (LspCapabilityException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or InvalidDataException or
                InvalidOperationException)
        {
            Status = LspSessionStatus.Faulted;
            await DisposeConnectionAsync();
            if (Changed is not null)
            {
                await Changed(cancellationToken);
            }

            throw new LspCapabilityException(
                LspCapabilityErrorCodes.ConnectionFailed,
                "LSP connection failed.");
        }
    }

    public CapabilityContributionSet CreateContribution() =>
        new(
            new CapabilitySourceDescriptor(
                CapabilitySourceKind.Lsp,
                $"lsp:{Definition.Id}",
                version: null,
                Definition.ConfigurationSha256),
            [
                new CapabilityContribution(
                    CapabilityKind.LspServer,
                    Definition.Id,
                    Definition.Id,
                    "Read-only workspace LSP Server.",
                    Status switch
                    {
                        LspSessionStatus.Starting or LspSessionStatus.Initializing =>
                            CapabilityStatus.Starting,
                        LspSessionStatus.Ready => CapabilityStatus.Ready,
                        LspSessionStatus.Faulted => CapabilityStatus.Faulted,
                        _ => CapabilityStatus.Unavailable,
                    },
                    [CapabilityTrustScope.OutOfProcess],
                    Generation,
                    Status == LspSessionStatus.Faulted
                        ? [LspCapabilityErrorCodes.ConnectionFailed]
                        : []),
            ]);

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Status = LspSessionStatus.Stopped;
        var connection = Interlocked.Exchange(ref _connection, null);
        if (connection is not null)
        {
            await connection.DisposeAsync(cancellationToken);
        }
    }

    private async Task DisposeConnectionAsync()
    {
        var connection = Interlocked.Exchange(ref _connection, null);
        if (connection is not null)
        {
            await connection.DisposeAsync(CancellationToken.None);
        }
    }
}

internal sealed class LspConnection(
    Process process,
    Stream input,
    Stream output,
    Task stderr,
    IReadOnlyList<IDisposable> leases)
{
    private const int MaximumHeaderBytes = 8 * 1024;
    private readonly SemaphoreSlim _io = new(1, 1);
    private long _nextRequestId;

    public async Task<JsonElement> SendRequestAsync(
        string method,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        await _io.WaitAsync(cancellationToken);
        var requestId = Interlocked.Increment(ref _nextRequestId);
        try
        {
            await WriteMessageAsync(
                JsonSerializer.SerializeToElement(new
                {
                    jsonrpc = "2.0",
                    id = requestId,
                    method,
                    @params = parameters,
                }),
                cancellationToken);
            while (true)
            {
                var message = await ReadMessageAsync(cancellationToken);
                if (message.TryGetProperty("method", out _) &&
                    message.TryGetProperty("id", out var serverRequestId))
                {
                    await WriteMessageAsync(
                        JsonSerializer.SerializeToElement(new
                        {
                            jsonrpc = "2.0",
                            id = serverRequestId,
                            error = new
                            {
                                code = -32601,
                                message = "Method not supported.",
                            },
                        }),
                        cancellationToken);
                    continue;
                }

                if (!message.TryGetProperty("id", out var responseId) ||
                    !responseId.TryGetInt64(out var value) ||
                    value != requestId)
                {
                    continue;
                }

                if (message.TryGetProperty("error", out _))
                {
                    throw new LspCapabilityException(
                        LspCapabilityErrorCodes.RequestFailed,
                        "LSP request failed.");
                }

                if (!message.TryGetProperty("result", out var result))
                {
                    throw new InvalidDataException("LSP response is invalid.");
                }

                return result.Clone();
            }
        }
        catch (OperationCanceledException)
        {
            try
            {
                await WriteMessageAsync(
                    JsonSerializer.SerializeToElement(new
                    {
                        jsonrpc = "2.0",
                        method = "$/cancelRequest",
                        @params = new { id = requestId },
                    }),
                    CancellationToken.None);
            }
            catch (Exception exception) when (
                exception is IOException or ObjectDisposedException)
            {
            }

            throw;
        }
        finally
        {
            _io.Release();
        }
    }

    public async Task SendNotificationAsync(
        string method,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        await _io.WaitAsync(cancellationToken);
        try
        {
            await WriteMessageAsync(
                JsonSerializer.SerializeToElement(new
                {
                    jsonrpc = "2.0",
                    method,
                    @params = parameters,
                }),
                cancellationToken);
        }
        finally
        {
            _io.Release();
        }
    }

    public async Task DisposeAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!process.HasExited)
            {
                using var timeout = new CancellationTokenSource(
                    TimeSpan.FromSeconds(2));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    timeout.Token);
                try
                {
                    _ = await SendRequestAsync(
                        "shutdown",
                        JsonSerializer.SerializeToElement<object?>(null),
                        linked.Token);
                    await SendNotificationAsync(
                        "exit",
                        JsonSerializer.SerializeToElement<object?>(null),
                        linked.Token);
                    input.Close();
                    await process.WaitForExitAsync(linked.Token);
                }
                catch (Exception exception) when (
                    exception is OperationCanceledException or IOException or
                        InvalidDataException or InvalidOperationException or
                        LspCapabilityException)
                {
                    await KillAsync(process);
                }
            }
        }
        finally
        {
            input.Dispose();
            output.Dispose();
            process.Dispose();
            foreach (var lease in leases)
            {
                lease.Dispose();
            }

            await stderr;
            _io.Dispose();
        }
    }

    internal static async Task KillAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
                System.ComponentModel.Win32Exception)
        {
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private async Task WriteMessageAsync(
        JsonElement message,
        CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message);
        if (body.Length > ToolRuntimeLimits.MaximumBindingResultBytes)
        {
            throw new InvalidDataException("LSP message exceeds the size limit.");
        }

        var header = Encoding.ASCII.GetBytes(
            $"Content-Length: {body.Length}\r\n\r\n");
        await input.WriteAsync(header, cancellationToken);
        await input.WriteAsync(body, cancellationToken);
        await input.FlushAsync(cancellationToken);
    }

    private async Task<JsonElement> ReadMessageAsync(
        CancellationToken cancellationToken)
    {
        var header = new List<byte>();
        while (header.Count < MaximumHeaderBytes)
        {
            var next = new byte[1];
            var read = await output.ReadAsync(next, cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("LSP stream ended.");
            }

            header.Add(next[0]);
            if (header.Count >= 4 &&
                header[^4] == '\r' &&
                header[^3] == '\n' &&
                header[^2] == '\r' &&
                header[^1] == '\n')
            {
                break;
            }
        }

        if (header.Count >= MaximumHeaderBytes)
        {
            throw new InvalidDataException("LSP header exceeds the size limit.");
        }

        var lines = Encoding.ASCII.GetString(header.ToArray())
            .Split(
                "\r\n",
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries);
        var lengthHeaders = lines.Where(line =>
                line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (lengthHeaders.Length != 1 ||
            !int.TryParse(
                lengthHeaders[0]["Content-Length:".Length..].Trim(),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var length) ||
            length is < 1 or > ToolRuntimeLimits.MaximumBindingResultBytes)
        {
            throw new InvalidDataException("LSP Content-Length is invalid.");
        }

        var body = new byte[length];
        var offset = 0;
        while (offset < body.Length)
        {
            var read = await output.ReadAsync(
                body.AsMemory(offset),
                cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("LSP frame ended early.");
            }

            offset += read;
        }

        _ = new UTF8Encoding(false, true).GetString(body);
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = ToolRuntimeLimits.MaximumJsonDepth,
        });
        LspCapabilitySource.EnsureUniqueProperties(document.RootElement);
        return document.RootElement.Clone();
    }
}
