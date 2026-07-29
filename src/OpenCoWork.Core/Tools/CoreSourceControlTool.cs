using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Capabilities;
using OpenCoWork.Core.Logging;
using OpenCoWork.Core.Workspaces;

namespace OpenCoWork.Core.Tools;

internal sealed record GitExecutableIdentity(
    string SourceId,
    string ExecutablePath,
    string Version,
    string Sha256);

internal sealed class CoreSourceControlTool
{
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
        "ASKPASS",
    ];
    private readonly string _anchor;
    private readonly CapabilityFileStore _files;
    private readonly OpenCoWorkPaths _paths;
    private readonly SecretRedactor _redactor;

    public CoreSourceControlTool(
        OpenCoWorkPaths paths,
        CapabilityFileStore files,
        SecretRedactor redactor)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        _anchor = Path.Combine(_paths.WorkspaceRoot, ".opencowork-source-control-anchor");
    }

    public async Task<GitExecutableIdentity> InspectAsync(
        CancellationToken cancellationToken = default)
    {
        var executable = FindOnPath(
            OperatingSystem.IsWindows() ? "git.exe" : "git") ??
            throw new SourceControlException(
                ToolErrorCodes.ExecutionFailed,
                "Git executable is unavailable.");
        var executableInfo = new FileInfo(executable);
        if ((executableInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            executable = executableInfo.ResolveLinkTarget(returnFinalTarget: true)
                ?.FullName ??
                throw new SourceControlException(
                    ToolErrorCodes.ExecutionFailed,
                    "Git executable could not be resolved.");
        }

        var info = new FileInfo(executable);
        var versionInfo = FileVersionInfo.GetVersionInfo(executable);
        var version = versionInfo.ProductVersion ??
                      versionInfo.FileVersion ??
                      $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        await using var stream = new FileStream(
            executable,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var sha256 = Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken))
            .ToLowerInvariant();
        return new GitExecutableIdentity(
            executable,
            executable,
            version,
            sha256);
    }

    public ValueTask<ToolBindingResult> StatusAsync(
        JsonElement arguments,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            "status",
            arguments,
            BuildStatus,
            cancellationToken);

    public ValueTask<ToolBindingResult> DiffAsync(
        JsonElement arguments,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            "diff",
            arguments,
            BuildDiff,
            cancellationToken);

    public ValueTask<ToolBindingResult> LogAsync(
        JsonElement arguments,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            "log",
            arguments,
            BuildLog,
            cancellationToken);

    public ValueTask<ToolBindingResult> ShowAsync(
        JsonElement arguments,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            "show",
            arguments,
            BuildShow,
            cancellationToken);

    private async ValueTask<ToolBindingResult> ExecuteAsync(
        string operation,
        JsonElement arguments,
        Func<JsonElement, IReadOnlyList<string>> buildArguments,
        CancellationToken cancellationToken)
    {
        Process? process = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = await InspectAsync(cancellationToken);
            var trust = await _files.LoadTrustDecisionsAsync(cancellationToken);
            var trusted = trust.Decisions.Any(decision =>
                decision.Matches(
                    _paths.WorkspaceRoot,
                    CapabilitySourceKind.Workspace,
                    identity.SourceId,
                    identity.Version,
                    identity.Sha256) &&
                decision.AllowedScopes.Contains(CapabilityTrustScope.OutOfProcess) &&
                !decision.DeniedScopes.Contains(CapabilityTrustScope.OutOfProcess));
            if (!trusted)
            {
                return Failure(
                    ToolErrorCodes.TrustRequired,
                    "Git executable requires trust.");
            }

            var revalidatedIdentity = await InspectAsync(cancellationToken);
            if (revalidatedIdentity != identity)
            {
                return Failure(
                    ToolErrorCodes.TrustRequired,
                    "Git executable changed after trust validation.");
            }

            if (!Directory.Exists(Path.Combine(_paths.WorkspaceRoot, ".git")) &&
                !File.Exists(Path.Combine(_paths.WorkspaceRoot, ".git")))
            {
                return Failure(
                    ToolErrorCodes.PreconditionFailed,
                    "Workspace root is not a Git repository root.");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = identity.ExecutablePath,
                WorkingDirectory = _paths.WorkspaceRoot,
                UseShellExecute = false,
                RedirectStandardInput = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = StrictUtf8,
                StandardErrorEncoding = StrictUtf8,
            };
            foreach (var argument in buildArguments(arguments))
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

            startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
            process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true,
            };
            var startedAt = Stopwatch.GetTimestamp();
            if (!process.Start())
            {
                return Failure(
                    ToolErrorCodes.ExecutionFailed,
                    "Git process did not start.");
            }

            var budget = new OutputBudget();
            var stdout = ReadAsync(process.StandardOutput, budget, cancellationToken);
            var stderr = ReadAsync(process.StandardError, budget, cancellationToken);
            var readers = Task.WhenAll(stdout, stderr);
            var exit = process.WaitForExitAsync(cancellationToken);
            var first = await Task.WhenAny(exit, readers, budget.LimitReached);
            if (first == budget.LimitReached)
            {
                await KillAsync(process);
                try
                {
                    await readers;
                }
                catch (OutputLimitException)
                {
                }

                return Failure(
                    ToolErrorCodes.OutputLimitExceeded,
                    "Git output exceeds the size limit.");
            }

            try
            {
                await Task.WhenAll(exit, readers);
            }
            catch (OutputLimitException)
            {
                await KillAsync(process);
                return Failure(
                    ToolErrorCodes.OutputLimitExceeded,
                    "Git output exceeds the size limit.");
            }

            var output = await readers;
            var result = JsonSerializer.SerializeToElement(new
            {
                operation,
                exitCode = process.ExitCode,
                stdout = _redactor.RedactText(output[0]),
                stderr = _redactor.RedactText(output[1]),
                durationMilliseconds = (long)Stopwatch
                    .GetElapsedTime(startedAt)
                    .TotalMilliseconds,
            });
            if (JsonSerializer.SerializeToUtf8Bytes(result).Length >
                ToolRuntimeLimits.MaximumBindingResultBytes)
            {
                return Failure(
                    ToolErrorCodes.OutputLimitExceeded,
                    "Git output exceeds the size limit.");
            }

            return process.ExitCode == 0
                ? ToolBindingResult.Success(result)
                : Failure(
                    ToolErrorCodes.ExecutionFailed,
                    string.IsNullOrWhiteSpace(output[1])
                        ? "Git command failed."
                        : $"Git command failed: {_redactor.RedactText(output[1]).Trim()}");
        }
        catch (OperationCanceledException)
        {
            if (process is not null)
            {
                await KillAsync(process);
            }

            throw;
        }
        catch (DecoderFallbackException)
        {
            if (process is not null)
            {
                await KillAsync(process);
            }

            return Failure(
                ToolErrorCodes.ContentUnsupported,
                "Git output is not valid UTF-8 text.");
        }
        catch (SourceControlException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                InvalidOperationException or ArgumentException)
        {
            if (process is not null)
            {
                await KillAsync(process);
            }

            return Failure(
                ToolErrorCodes.ExecutionFailed,
                "Git execution failed.");
        }
        finally
        {
            process?.Dispose();
        }
    }

    private IReadOnlyList<string> BuildStatus(JsonElement arguments)
    {
        RequireProperties(arguments, ["path"]);
        var result = GlobalArguments("status");
        result.Add("--porcelain=v1");
        result.Add("--untracked-files=all");
        AddPath(result, arguments);
        return result;
    }

    private IReadOnlyList<string> BuildDiff(JsonElement arguments)
    {
        RequireProperties(arguments, ["path"]);
        var result = GlobalArguments("diff");
        result.Add("--no-ext-diff");
        result.Add("--no-textconv");
        AddPath(result, arguments);
        return result;
    }

    private IReadOnlyList<string> BuildLog(JsonElement arguments)
    {
        RequireProperties(arguments, ["path", "maxCount"]);
        var maxCount = 20;
        if (arguments.TryGetProperty("maxCount", out var configured))
        {
            if (!configured.TryGetInt32(out maxCount) || maxCount is < 1 or > 100)
            {
                throw Invalid("Git log count is invalid.");
            }
        }

        var result = GlobalArguments("log");
        result.Add("--no-ext-diff");
        result.Add("--pretty=format:%H%x09%ct%x09%an%x09%s");
        result.Add("-n");
        result.Add(maxCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddPath(result, arguments);
        return result;
    }

    private IReadOnlyList<string> BuildShow(JsonElement arguments)
    {
        RequireProperties(arguments, ["revision", "path"]);
        var revision = RequiredString(arguments, "revision");
        if (revision.Length > 256 ||
            revision[0] == '-' ||
            revision.Any(char.IsWhiteSpace) ||
            revision.Any(char.IsControl))
        {
            throw Invalid("Git revision is invalid.");
        }

        var result = GlobalArguments("show");
        result.Add("--no-ext-diff");
        result.Add("--no-textconv");
        result.Add("--format=fuller");
        result.Add("--stat");
        result.Add(revision);
        AddPath(result, arguments);
        return result;
    }

    private static List<string> GlobalArguments(string operation) =>
    [
        "--no-pager",
        "-c",
        "color.ui=false",
        "-c",
        "core.quotepath=false",
        operation,
    ];

    private void AddPath(List<string> arguments, JsonElement value)
    {
        arguments.Add("--");
        if (!value.TryGetProperty("path", out var configured))
        {
            return;
        }

        if (configured.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(configured.GetString()) ||
            Path.IsPathRooted(configured.GetString()!))
        {
            throw Denied();
        }

        try
        {
            var resolved = WorkspacePathGuard.ResolveContained(
                _paths.WorkspaceRoot,
                _anchor,
                configured.GetString()!);
            arguments.Add(Path.GetRelativePath(
                resolved.PhysicalRoot,
                resolved.PhysicalPath));
        }
        catch (SourceControlException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException)
        {
            throw Denied();
        }
    }

    private static void RequireProperties(
        JsonElement value,
        IReadOnlyCollection<string> allowed)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            value.EnumerateObject().Any(property =>
                !allowed.Contains(property.Name, StringComparer.Ordinal)))
        {
            throw Invalid("Git arguments are invalid.");
        }
    }

    private static string RequiredString(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw Invalid("Git arguments are invalid.");
        }

        return property.GetString()!;
    }

    private static string? FindOnPath(string fileName)
    {
        foreach (var directory in (
                     Environment.GetEnvironmentVariable("PATH") ??
                     string.Empty)
                 .Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries |
                     StringSplitOptions.TrimEntries))
        {
            var candidate = Path.GetFullPath(Path.Combine(directory, fileName));
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static async Task<string> ReadAsync(
        StreamReader reader,
        OutputBudget budget,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var output = new StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return output.ToString();
            }

            budget.Add(StrictUtf8.GetByteCount(buffer.AsSpan(0, read)));
            output.Append(buffer, 0, read);
        }
    }

    private static async Task KillAsync(Process process)
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

    private static bool IsSensitiveEnvironmentName(string name)
    {
        var normalized = new string(
            name.Where(char.IsLetterOrDigit)
                .Select(char.ToUpperInvariant)
                .ToArray());
        return SensitiveEnvironmentMarkers.Any(marker =>
            normalized.Contains(marker, StringComparison.Ordinal));
    }

    private static ToolBindingResult Failure(string code, string message) =>
        ToolBindingResult.Failure(new SessionError(
            code,
            message,
            IsRetryable: false));

    private static SourceControlException Invalid(string message) =>
        new(ToolErrorCodes.InputInvalid, message);

    private static SourceControlException Denied() =>
        new(ToolErrorCodes.PathDenied, "Git path is denied.");

    private sealed class OutputBudget
    {
        private int _bytes;
        private readonly TaskCompletionSource _limitReached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task LimitReached => _limitReached.Task;

        public void Add(int bytes)
        {
            if (Interlocked.Add(ref _bytes, bytes) >
                ToolRuntimeLimits.MaximumBindingResultBytes)
            {
                _limitReached.TrySetResult();
                throw new OutputLimitException();
            }
        }
    }

    private sealed class OutputLimitException : Exception;

    private sealed class SourceControlException(string code, string message)
        : Exception(message)
    {
        public string Code { get; } = code;
    }
}
