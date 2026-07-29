using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Capabilities;
using OpenCoWork.Core.Logging;
using OpenCoWork.Core.Workspaces;

namespace OpenCoWork.Core.Tools;

internal sealed class WorkspaceProcessHookSource
{
    internal const string TrustSourceId = ".opencowork/hooks.json";
    private const int MaximumFileBytes = 1024 * 1024;
    private const int MaximumOutputBytes = 64 * 1024;
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly string[] SensitiveEnvironmentMarkers =
    [
        "PASSWORD",
        "TOKEN",
        "SECRET",
        "APIKEY",
        "CREDENTIAL",
        "AUTHORIZATION",
    ];
    private readonly CapabilityFileStore _files;
    private readonly ILogger<WorkspaceProcessHookSource> _logger;
    private readonly OpenCoWorkPaths _paths;
    private readonly SecretRedactor _redactor;

    public WorkspaceProcessHookSource(
        OpenCoWorkPaths paths,
        CapabilityFileStore files,
        SecretRedactor redactor,
        ILogger<WorkspaceProcessHookSource>? logger = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        _logger = logger ?? NullLogger<WorkspaceProcessHookSource>.Instance;
    }

    public async ValueTask<IReadOnlyList<CapabilityHook>> LoadAsync(
        CancellationToken cancellationToken)
    {
        var configuredPath = Path.Combine(_paths.OpenCoWorkDirectory, "hooks.json");
        if (!File.Exists(configuredPath))
        {
            return [];
        }

        var resolved = WorkspacePathGuard.ResolveContained(
            _paths.WorkspaceRoot,
            configuredPath,
            configuredPath);
        var bytes = await ReadBoundedAsync(
            resolved.PhysicalPath,
            MaximumFileBytes,
            cancellationToken);
        _ = StrictUtf8.GetString(bytes);
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var trust = await _files.LoadTrustDecisionsAsync(cancellationToken);
        var decision = trust.Decisions.SingleOrDefault(item =>
            item.Matches(
                _paths.WorkspaceRoot,
                CapabilitySourceKind.Workspace,
                TrustSourceId,
                sourceVersion: null,
                sha256));
        var required = new[]
        {
            CapabilityTrustScope.OutOfProcess,
            CapabilityTrustScope.TrustedHook,
        };
        if (decision is null ||
            required.Any(scope => !decision.AllowedScopes.Contains(scope)) ||
            required.Any(decision.DeniedScopes.Contains))
        {
            return [];
        }

        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32,
        });
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Workspace Hook file must contain an array.");
        }

        var hooks = document.RootElement.EnumerateArray()
            .Select(Parse)
            .ToArray();
        if (hooks.GroupBy(hook => hook.Id, StringComparer.Ordinal)
            .Any(group => group.Skip(1).Any()))
        {
            throw new InvalidDataException("Workspace Hook IDs must be unique.");
        }

        return Array.AsReadOnly(hooks);
    }

    private CapabilityHook Parse(JsonElement value)
    {
        RequireProperties(value, ["id", "event", "execution", "timeoutMs"]);
        var id = RequireString(value, "id");
        var eventName = RequireString(value, "event");
        var timeoutMs = RequireInt32(value, "timeoutMs");
        if (timeoutMs is < 1 or > 10_000)
        {
            throw new InvalidDataException("Workspace Hook timeout is invalid.");
        }

        var execution = value.GetProperty("execution");
        RequireProperties(
            execution,
            ["kind", "command", "arguments", "workingDirectory", "environment"]);
        if (!string.Equals(
                RequireString(execution, "kind"),
                "process",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Workspace Hook execution kind is invalid.");
        }

        var hook = new ProcessHook(
            $"workspace/{id}",
            RequireString(execution, "command"),
            RequireStringArray(execution, "arguments"),
            ResolveWorkingDirectory(
                RequireString(execution, "workingDirectory")),
            RequireEnvironment(execution),
            TimeSpan.FromMilliseconds(timeoutMs));
        return eventName switch
        {
            "preToolUse" => new CapabilityHook(
                hook.Id,
                CapabilityHookEvent.PreToolUse,
                PluginSourceId: null,
                async (context, cancellationToken) =>
                    ParsePreDecision(await ExecuteAsync(
                        hook,
                        context,
                        result: null,
                        cancellationToken)),
                Terminal: null),
            "toolTerminal" => new CapabilityHook(
                hook.Id,
                CapabilityHookEvent.ToolTerminal,
                PluginSourceId: null,
                PreUse: null,
                async (context, result, cancellationToken) =>
                {
                    _ = await ExecuteAsync(
                        hook,
                        context,
                        result,
                        cancellationToken);
                }),
            _ => throw new InvalidDataException("Workspace Hook event is invalid."),
        };
    }

    private async ValueTask<JsonElement> ExecuteAsync(
        ProcessHook hook,
        ToolInvocationContext context,
        ToolResultSnapshot? result,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveCommand(hook.Command),
            WorkingDirectory = hook.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = StrictUtf8,
            StandardOutputEncoding = StrictUtf8,
            StandardErrorEncoding = StrictUtf8,
        };
        foreach (var argument in hook.Arguments)
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

        foreach (var (name, value) in hook.Environment)
        {
            startInfo.Environment[name] = value;
        }

        using var timeout = new CancellationTokenSource(hook.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        using var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Workspace Hook did not start.");
            }

            var payload = JsonSerializer.Serialize(new
            {
                @event = result is null ? "preToolUse" : "toolTerminal",
                tool = new
                {
                    context.ProviderToolName,
                    context.ProviderToolCallId,
                },
                arguments = context.Arguments,
                result,
            });
            if (StrictUtf8.GetByteCount(payload) > ToolRuntimeLimits.MaximumArgumentsBytes)
            {
                throw new InvalidDataException("Workspace Hook input is too large.");
            }

            await process.StandardInput.WriteLineAsync(
                payload.AsMemory(),
                linked.Token);
            process.StandardInput.Close();
            var stdout = ReadBoundedAsync(
                process.StandardOutput,
                MaximumOutputBytes,
                linked.Token);
            var stderr = ReadBoundedAsync(
                process.StandardError,
                MaximumOutputBytes,
                linked.Token);
            await process.WaitForExitAsync(linked.Token);
            var output = await stdout;
            var error = await stderr;
            if (process.ExitCode != 0)
            {
                if (error.Length != 0)
                {
                    _logger.LogWarning(
                        "Workspace Hook {HookId} failed: {Error}",
                        hook.Id,
                        _redactor.RedactText(error));
                }

                throw new InvalidOperationException("Workspace Hook failed.");
            }

            using var document = JsonDocument.Parse(output, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    "Workspace Hook output must be a JSON object.");
            }

            return document.RootElement.Clone();
        }
        catch
        {
            await KillAsync(process);
            throw;
        }
    }

    private static ToolPreUseDecision ParsePreDecision(JsonElement value)
    {
        var names = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (!names.Contains("authority", StringComparer.Ordinal) ||
            names.Any(name => name is not ("authority" or "timeoutCapMs")))
        {
            throw new InvalidDataException("Workspace Hook decision is invalid.");
        }

        TimeSpan? timeout = null;
        if (value.TryGetProperty("timeoutCapMs", out var timeoutValue))
        {
            if (!timeoutValue.TryGetInt32(out var timeoutMs) ||
                timeoutMs is < 1 or > 10_000)
            {
                throw new InvalidDataException("Workspace Hook timeout cap is invalid.");
            }

            timeout = TimeSpan.FromMilliseconds(timeoutMs);
        }

        return new ToolPreUseDecision(
            RequireString(value, "authority") switch
            {
                "allow" => ToolAuthorityDecision.Allow,
                "deny" => ToolAuthorityDecision.Deny,
                "requireApproval" => ToolAuthorityDecision.RequireApproval,
                _ => throw new InvalidDataException(
                    "Workspace Hook authority is invalid."),
            },
            timeout);
    }

    private string ResolveWorkingDirectory(string configured)
    {
        if (string.Equals(configured, "workspace", StringComparison.Ordinal))
        {
            return _paths.WorkspaceRoot;
        }

        var resolved = WorkspacePathGuard.ResolveContained(
            _paths.WorkspaceRoot,
            Path.Combine(_paths.OpenCoWorkDirectory, "hooks.json"),
            configured);
        return Directory.Exists(resolved.PhysicalPath)
            ? resolved.PhysicalPath
            : throw new DirectoryNotFoundException(
                "Workspace Hook working directory was not found.");
    }

    private string ResolveCommand(string configured)
    {
        if (!configured.Contains(Path.DirectorySeparatorChar) &&
            !configured.Contains(Path.AltDirectorySeparatorChar))
        {
            return configured;
        }

        var resolved = WorkspacePathGuard.ResolveContained(
            _paths.WorkspaceRoot,
            Path.Combine(_paths.OpenCoWorkDirectory, "hooks.json"),
            configured);
        return File.Exists(resolved.PhysicalPath)
            ? resolved.PhysicalPath
            : throw new FileNotFoundException("Workspace Hook command was not found.");
    }

    private static IReadOnlyDictionary<string, string> RequireEnvironment(
        JsonElement execution)
    {
        var value = execution.GetProperty("environment");
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Workspace Hook environment is invalid.");
        }

        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name) ||
                property.Value.ValueKind != JsonValueKind.String ||
                IsSensitiveEnvironmentName(property.Name) ||
                !environment.TryAdd(property.Name, property.Value.GetString()!))
            {
                throw new InvalidDataException("Workspace Hook environment is invalid.");
            }
        }

        return environment;
    }

    private static string[] RequireStringArray(JsonElement parent, string name)
    {
        var value = parent.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Workspace Hook arguments are invalid.");
        }

        return value.EnumerateArray().Select(item =>
                item.ValueKind == JsonValueKind.String &&
                item.GetString() is { } text &&
                !text.Contains('\0', StringComparison.Ordinal)
                    ? text
                    : throw new InvalidDataException(
                        "Workspace Hook argument is invalid."))
            .ToArray();
    }

    private static string RequireString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { } result ||
            string.IsNullOrWhiteSpace(result) ||
            result != result.Trim() ||
            result.Any(char.IsControl))
        {
            throw new InvalidDataException($"Workspace Hook '{name}' is invalid.");
        }

        return result;
    }

    private static int RequireInt32(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) &&
        value.TryGetInt32(out var result)
            ? result
            : throw new InvalidDataException($"Workspace Hook '{name}' is invalid.");

    private static void RequireProperties(
        JsonElement value,
        IReadOnlyCollection<string> names)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Workspace Hook definition is invalid.");
        }

        var properties = value.EnumerateObject().Select(item => item.Name).ToArray();
        if (properties.Length != names.Count ||
            properties.Distinct(StringComparer.Ordinal).Count() != properties.Length ||
            names.Any(name => !properties.Contains(name, StringComparer.Ordinal)))
        {
            throw new InvalidDataException("Workspace Hook definition is invalid.");
        }
    }

    private static bool IsSensitiveEnvironmentName(string name)
    {
        var normalized = new string(name.Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
        return SensitiveEnvironmentMarkers.Any(marker =>
            normalized.Contains(marker, StringComparison.Ordinal));
    }

    private static async Task<byte[]> ReadBoundedAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytes = new byte[maximumBytes + 1];
        var total = 0;
        while (total < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(total), cancellationToken);
            if (read == 0)
            {
                return bytes[..total];
            }

            total += read;
        }

        throw new InvalidDataException("Workspace Hook file is too large.");
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var buffer = new char[4096];
        var bytes = 0;
        while (true)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return output.ToString();
            }

            bytes = checked(bytes + StrictUtf8.GetByteCount(buffer.AsSpan(0, read)));
            if (bytes > maximumBytes)
            {
                throw new InvalidDataException("Workspace Hook output is too large.");
            }

            output.Append(buffer, 0, read);
        }
    }

    private static async Task KillAsync(Process process)
    {
        try
        {
            if (process.StartTime != default && !process.HasExited)
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
            if (process.StartTime != default)
            {
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private sealed record ProcessHook(
        string Id,
        string Command,
        IReadOnlyList<string> Arguments,
        string WorkingDirectory,
        IReadOnlyDictionary<string, string> Environment,
        TimeSpan Timeout);
}
