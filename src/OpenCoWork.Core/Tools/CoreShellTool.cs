using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Workspaces;

namespace OpenCoWork.Core.Tools;

internal sealed class CoreShellTool
{
    private static readonly string[] SensitiveEnvironmentMarkers =
    [
        "PASSWORD",
        "TOKEN",
        "SECRET",
        "APIKEY",
        "CREDENTIAL",
        "AUTHORIZATION",
    ];
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly HashSet<string> _credentialEnvironmentNames;
    private readonly string _root;

    public CoreShellTool(
        OpenCoWorkPaths paths,
        IEnumerable<string> credentialEnvironmentNames)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(credentialEnvironmentNames);
        _root = paths.WorkspaceRoot;
        _credentialEnvironmentNames = credentialEnvironmentNames.ToHashSet(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
    }

    public async ValueTask<ToolBindingResult> RunAsync(
        JsonElement arguments,
        CancellationToken cancellationToken) =>
        await RunAsync(arguments, _root, cancellationToken);

    public async ValueTask<ToolBindingResult> RunAsync(
        ToolInvocationContext context,
        CancellationToken cancellationToken) =>
        await RunAsync(
            context.Arguments,
            WorkspacePathGuard.ResolveExecutionRoot(
                context.ExecutionWorkspace,
                _root),
            cancellationToken);

    private async ValueTask<ToolBindingResult> RunAsync(
        JsonElement arguments,
        string root,
        CancellationToken cancellationToken)
    {
        Process? process = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var command = RequiredString(arguments, "command");
            if (string.IsNullOrWhiteSpace(command) ||
                command.Contains('\0', StringComparison.Ordinal))
            {
                return Failure(
                    ToolErrorCodes.InputInvalid,
                    "Shell command is invalid.");
            }

            var workingDirectory = ResolveWorkingDirectory(arguments, root);
            var host = CreateHost(command);
            if (host is null)
            {
                return Failure(
                    ToolErrorCodes.ExecutionFailed,
                    "Shell host is unavailable.");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = host.Value.FileName,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = StrictUtf8,
                StandardErrorEncoding = StrictUtf8,
            };
            foreach (var argument in host.Value.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            foreach (var name in startInfo.Environment.Keys.ToArray())
            {
                if (_credentialEnvironmentNames.Contains(name) ||
                    IsSensitiveEnvironmentName(name))
                {
                    startInfo.Environment.Remove(name);
                }
            }

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
                    "Shell process did not start.");
            }

            var budget = new OutputBudget();
            var stdout = ReadAsync(
                process.StandardOutput,
                budget,
                cancellationToken);
            var stderr = ReadAsync(
                process.StandardError,
                budget,
                cancellationToken);
            var readers = Task.WhenAll(stdout, stderr);
            var exit = process.WaitForExitAsync(cancellationToken);
            var first = await Task.WhenAny(
                exit,
                readers,
                budget.LimitReached);
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
                    "Shell output exceeds the size limit.");
            }

            if (first == readers)
            {
                try
                {
                    await readers;
                }
                catch (OutputLimitException)
                {
                    await KillAsync(process);
                    return Failure(
                        ToolErrorCodes.OutputLimitExceeded,
                        "Shell output exceeds the size limit.");
                }
            }

            await exit;
            string[] output;
            try
            {
                output = await readers;
            }
            catch (OutputLimitException)
            {
                await KillAsync(process);
                return Failure(
                    ToolErrorCodes.OutputLimitExceeded,
                    "Shell output exceeds the size limit.");
            }

            var result = JsonSerializer.SerializeToElement(new
            {
                host = host.Value.DisplayName,
                exitCode = process.ExitCode,
                stdout = output[0],
                stderr = output[1],
                durationMilliseconds = (long)Stopwatch
                    .GetElapsedTime(startedAt)
                    .TotalMilliseconds,
            });
            if (JsonSerializer.SerializeToUtf8Bytes(result).Length >
                ToolRuntimeLimits.MaximumBindingResultBytes)
            {
                return Failure(
                    ToolErrorCodes.OutputLimitExceeded,
                    "Shell output exceeds the size limit.");
            }

            return ToolBindingResult.Success(result);
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
                "Shell output is not valid UTF-8 text.");
        }
        catch (CoreShellException exception)
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
                "Shell execution failed.");
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static string ResolveWorkingDirectory(
        JsonElement arguments,
        string root)
    {
        if (!arguments.TryGetProperty(
                "workingDirectory",
                out var configured))
        {
            return root;
        }

        if (configured.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(configured.GetString()) ||
            Path.IsPathRooted(configured.GetString()!))
        {
            throw new CoreShellException(
                ToolErrorCodes.PathDenied,
                "Shell working directory is denied.");
        }

        try
        {
            var path = WorkspacePathGuard.ResolveContained(
                root,
                Path.Combine(root, ".opencowork-anchor"),
                configured.GetString()!);
            if (!Directory.Exists(path.PhysicalPath))
            {
                throw new CoreShellException(
                    ToolErrorCodes.PathNotFound,
                    "Shell working directory was not found.");
            }

            return path.PhysicalPath;
        }
        catch (CoreShellException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException)
        {
            throw new CoreShellException(
                ToolErrorCodes.PathDenied,
                "Shell working directory is denied.");
        }
    }

    private static ShellHost? CreateHost(string command)
    {
        if (OperatingSystem.IsMacOS())
        {
            return new ShellHost(
                "/bin/zsh",
                "/bin/zsh",
                ["-lc", command]);
        }

        if (OperatingSystem.IsWindows())
        {
            var pwsh = FindOnPath("pwsh.exe");
            var fileName = pwsh ?? "powershell.exe";
            return new ShellHost(
                fileName,
                pwsh is null ? "powershell.exe" : "pwsh",
                [
                    "-NoLogo",
                    "-NoProfile",
                    "-NonInteractive",
                    "-Command",
                    command,
                ]);
        }

        return null;
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
            var candidate = Path.Combine(directory, fileName);
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

    private static string RequiredString(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            throw new CoreShellException(
                ToolErrorCodes.InputInvalid,
                "Shell arguments are invalid.");
        }

        return value.GetString()!;
    }

    private static ToolBindingResult Failure(string code, string message) =>
        ToolBindingResult.Failure(new SessionError(
            code,
            message,
            IsRetryable: false));

    private readonly record struct ShellHost(
        string FileName,
        string DisplayName,
        IReadOnlyList<string> Arguments);

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

    private sealed class CoreShellException(string code, string message)
        : Exception(message)
    {
        public string Code { get; } = code;
    }
}
