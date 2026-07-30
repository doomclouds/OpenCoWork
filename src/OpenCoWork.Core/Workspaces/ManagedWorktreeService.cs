using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Tools;

namespace OpenCoWork.Core.Workspaces;

internal sealed partial class ManagedWorktreeService : IManagedWorktreeService
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
    private readonly ConcurrentDictionary<Guid, ManagedWorktreeDescriptor> _items = [];
    private readonly OpenCoWorkPaths _paths;
    private readonly CoreSourceControlTool? _sourceControl;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ManagedWorktreeService(
        OpenCoWorkPaths paths,
        CoreSourceControlTool? sourceControl = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _sourceControl = sourceControl;
    }

    public async ValueTask<ManagedWorktreeDescriptor> CreateAsync(
        ManagedWorktreeCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.AgentRunId == Guid.Empty ||
            !CommitShaPattern().IsMatch(request.BaseCommitSha))
        {
            throw new ArgumentException("Managed Worktree request is invalid.", nameof(request));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var existing = _items.Values.SingleOrDefault(item =>
                string.Equals(
                    Path.GetFileName(item.WorktreeRoot),
                    request.AgentRunId.ToString("D"),
                    StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                return string.Equals(
                    existing.BaseCommitSha,
                    request.BaseCommitSha,
                    StringComparison.OrdinalIgnoreCase)
                    ? existing
                    : throw new InvalidOperationException(
                        "Agent Run is already bound to another Base SHA.");
            }

            var originStatus = await RunGitAsync(
                await GitExecutableAsync(cancellationToken),
                _paths.WorkspaceRoot,
                ["status", "--porcelain=v1", "--untracked-files=all"],
                cancellationToken);
            EnsureSuccess(originStatus, "Origin Git status failed.");
            if (!string.IsNullOrEmpty(originStatus.Stdout))
            {
                throw new InvalidOperationException(
                    "Origin workspace must be clean before creating a Worktree.");
            }

            var resolvedBase = await RunGitAsync(
                await GitExecutableAsync(cancellationToken),
                _paths.WorkspaceRoot,
                ["rev-parse", "--verify", request.BaseCommitSha + "^{commit}"],
                cancellationToken);
            EnsureSuccess(resolvedBase, "Base Commit is unavailable.");
            if (!string.Equals(
                    resolvedBase.Stdout.Trim(),
                    request.BaseCommitSha,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Base Commit SHA must be complete and exact.");
            }

            Directory.CreateDirectory(_paths.WorktreesDirectory);
            var root = Path.Combine(
                _paths.WorktreesDirectory,
                request.AgentRunId.ToString("D"));
            if (Directory.Exists(root) || File.Exists(root))
            {
                throw new InvalidOperationException(
                    "Managed Worktree path already exists.");
            }

            var containment = WorkspacePathGuard.ResolveContained(
                _paths.WorktreesDirectory,
                Path.Combine(_paths.WorktreesDirectory, ".managed-worktree-anchor"),
                request.AgentRunId.ToString("D"));
            var created = await RunGitAsync(
                await GitExecutableAsync(cancellationToken),
                _paths.WorkspaceRoot,
                [
                    "worktree", "add", "--detach",
                    containment.PhysicalPath,
                    request.BaseCommitSha,
                ],
                cancellationToken);
            EnsureSuccess(created, "Managed Worktree creation failed.");

            var descriptor = new ManagedWorktreeDescriptor(
                Guid.CreateVersion7(),
                containment.PhysicalPath,
                request.BaseCommitSha.ToLowerInvariant(),
                CoWorkWorktreeStatus.Ready,
                IsDirty: false);
            _items[descriptor.WorktreeId] = descriptor;
            return descriptor;
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask<ManagedWorktreeDescriptor?> GetAsync(
        Guid worktreeId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_items.GetValueOrDefault(worktreeId));
    }

    public async ValueTask<ManagedWorktreeDescriptor> RemoveAsync(
        Guid worktreeId,
        CancellationToken cancellationToken = default)
    {
        if (worktreeId == Guid.Empty)
        {
            throw new ArgumentException("Worktree ID is required.", nameof(worktreeId));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = _items.GetValueOrDefault(worktreeId) ??
                          throw new KeyNotFoundException("Managed Worktree was not found.");
            if (current.Status == CoWorkWorktreeStatus.Removed)
            {
                return current;
            }

            var status = await RunGitAsync(
                await GitExecutableAsync(cancellationToken),
                current.WorktreeRoot,
                ["status", "--porcelain=v1", "--untracked-files=all"],
                cancellationToken);
            EnsureSuccess(status, "Managed Worktree status failed.");
            if (!string.IsNullOrEmpty(status.Stdout))
            {
                var retained = current with
                {
                    Status = CoWorkWorktreeStatus.RetainedDirty,
                    IsDirty = true,
                };
                _items[worktreeId] = retained;
                return retained;
            }

            var removed = await RunGitAsync(
                await GitExecutableAsync(cancellationToken),
                _paths.WorkspaceRoot,
                ["worktree", "remove", current.WorktreeRoot],
                cancellationToken);
            EnsureSuccess(removed, "Managed Worktree removal failed.");
            var completed = current with
            {
                Status = CoWorkWorktreeStatus.Removed,
                IsDirty = false,
            };
            _items[worktreeId] = completed;
            return completed;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<GitResult> RunGitAsync(
        string executable,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = StrictUtf8,
            StandardErrorEncoding = StrictUtf8,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var name in startInfo.Environment.Keys.ToArray())
        {
            if (SensitiveEnvironmentMarkers.Any(marker =>
                    name.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                startInfo.Environment.Remove(name);
            }
        }

        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Git process did not start.");
        }

        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return new GitResult(process.ExitCode, await stdout, await stderr);
    }

    private async Task<string> GitExecutableAsync(CancellationToken cancellationToken) =>
        _sourceControl is null
            ? OperatingSystem.IsWindows() ? "git.exe" : "git"
            : (await _sourceControl.RequireTrustedIdentityAsync(cancellationToken))
            .ExecutablePath;

    private static void EnsureSuccess(GitResult result, string message)
    {
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(message);
        }
    }

    [GeneratedRegex("^[0-9a-fA-F]{40}([0-9a-fA-F]{24})?$")]
    private static partial Regex CommitShaPattern();

    private sealed record GitResult(int ExitCode, string Stdout, string Stderr);
}
