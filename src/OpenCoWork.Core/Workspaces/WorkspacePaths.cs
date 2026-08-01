using OpenCoWork.Abstractions;

namespace OpenCoWork.Core.Workspaces;

public sealed class OpenCoWorkPaths
{
    public OpenCoWorkPaths(string workspaceRoot)
        : this(
            workspaceRoot,
            Path.Combine(Path.GetFullPath(workspaceRoot), ".opencowork"))
    {
    }

    internal OpenCoWorkPaths(string workspaceRoot, string openCoWorkDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(openCoWorkDirectory);
        WorkspaceRoot = Path.GetFullPath(workspaceRoot);

        if (!Directory.Exists(WorkspaceRoot))
        {
            throw new DirectoryNotFoundException(
                $"Workspace root does not exist: {WorkspaceRoot}");
        }

        OpenCoWorkDirectory = Path.GetFullPath(openCoWorkDirectory);
        ConfigPath = Path.Combine(OpenCoWorkDirectory, "config.jsonc");
        LocalConfigPath = Path.Combine(OpenCoWorkDirectory, "config.local.jsonc");
        PluginsLockPath = Path.Combine(OpenCoWorkDirectory, "plugins.lock.json");
        CapabilitiesPath = Path.Combine(OpenCoWorkDirectory, "capabilities.json");
        SkillsDirectory = Path.Combine(OpenCoWorkDirectory, "skills");
        AuthPath = Path.Combine(OpenCoWorkDirectory, "auth.json");
        ProvidersPath = Path.Combine(OpenCoWorkDirectory, "providers.json");
        McpPath = Path.Combine(OpenCoWorkDirectory, "mcp.json");
        LspPath = Path.Combine(OpenCoWorkDirectory, "lsp.json");
        RuntimeDirectory = Path.Combine(OpenCoWorkDirectory, "runtime");
        StateDatabasePath = Path.Combine(RuntimeDirectory, "state.db");
        LogsDirectory = Path.Combine(RuntimeDirectory, "logs");
        ThreadsDirectory = Path.Combine(RuntimeDirectory, "threads");
        ActiveThreadsDirectory = Path.Combine(ThreadsDirectory, "active");
        ArchivedThreadsDirectory = Path.Combine(ThreadsDirectory, "archived");
        DeletingThreadsDirectory = Path.Combine(ThreadsDirectory, "deleting");
        ThreadRecoveryDirectory = Path.Combine(RuntimeDirectory, "recovery", "threads");
        TeamsRuntimeDirectory = Path.Combine(RuntimeDirectory, "teams");
        MissionsDirectory = Path.Combine(TeamsRuntimeDirectory, "missions");
        SubAgentsDirectory = Path.Combine(TeamsRuntimeDirectory, "subagents");
        WorktreesDirectory = Path.Combine(RuntimeDirectory, "worktrees");
        ExternalChannelMediaDirectory =
            Path.Combine(RuntimeDirectory, "external-channel-media");
    }

    public string WorkspaceRoot { get; }

    public string OpenCoWorkDirectory { get; }

    public string ConfigPath { get; }

    public string LocalConfigPath { get; }

    public string PluginsLockPath { get; }

    public string CapabilitiesPath { get; }

    public string SkillsDirectory { get; }

    public string AuthPath { get; }

    public string ProvidersPath { get; }

    public string McpPath { get; }

    public string LspPath { get; }

    public string RuntimeDirectory { get; }

    public string StateDatabasePath { get; }

    public string LogsDirectory { get; }

    public string ThreadsDirectory { get; }

    public string ActiveThreadsDirectory { get; }

    public string ArchivedThreadsDirectory { get; }

    public string DeletingThreadsDirectory { get; }

    public string ThreadRecoveryDirectory { get; }

    public string TeamsRuntimeDirectory { get; }

    public string MissionsDirectory { get; }

    public string SubAgentsDirectory { get; }

    public string WorktreesDirectory { get; }

    public string ExternalChannelMediaDirectory { get; }
}

public static class WorkspaceDiscovery
{
    public static OpenCoWorkPaths Discover(
        string startupDirectory,
        string? explicitWorkspace = null) =>
        Discover(
            startupDirectory,
            explicitWorkspace,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    internal static OpenCoWorkPaths Discover(
        string startupDirectory,
        string? explicitWorkspace,
        string userProfileDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startupDirectory);
        var startupRoot = Path.GetFullPath(startupDirectory);
        var userProfileRoot = string.IsNullOrWhiteSpace(userProfileDirectory)
            ? null
            : Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(userProfileDirectory));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!Directory.Exists(startupRoot))
        {
            throw new DirectoryNotFoundException(
                $"Startup directory does not exist: {startupRoot}");
        }

        if (!string.IsNullOrWhiteSpace(explicitWorkspace))
        {
            var explicitRoot = Path.GetFullPath(explicitWorkspace, startupRoot);
            if (!Directory.Exists(explicitRoot))
            {
                throw new DirectoryNotFoundException(
                    $"Explicit workspace does not exist: {explicitRoot}");
            }

            if (string.Equals(
                    Path.GetFileName(Path.TrimEndingDirectorySeparator(explicitRoot)),
                    ".opencowork",
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "--workspace expects the workspace root, not the .opencowork directory.",
                    nameof(explicitWorkspace));
            }

            return new OpenCoWorkPaths(explicitRoot);
        }

        var current = new DirectoryInfo(startupRoot);
        while (current is not null)
        {
            if (!string.Equals(
                    Path.TrimEndingDirectorySeparator(current.FullName),
                    userProfileRoot,
                    comparison) &&
                Directory.Exists(Path.Combine(current.FullName, ".opencowork")))
            {
                return new OpenCoWorkPaths(current.FullName);
            }

            current = current.Parent;
        }

        current = new DirectoryInfo(startupRoot);
        while (current is not null)
        {
            var git = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
            {
                return new OpenCoWorkPaths(current.FullName);
            }

            current = current.Parent;
        }

        return new OpenCoWorkPaths(startupRoot);
    }
}

public sealed record ResolvedWorkspacePath
{
    internal ResolvedWorkspacePath(
        string allowedRoot,
        string declaringFile,
        string configuredPath,
        string logicalPath,
        string physicalPath,
        string physicalRoot)
    {
        AllowedRoot = allowedRoot;
        DeclaringFile = declaringFile;
        ConfiguredPath = configuredPath;
        LogicalPath = logicalPath;
        PhysicalPath = physicalPath;
        PhysicalRoot = physicalRoot;
    }

    public string LogicalPath { get; }

    public string PhysicalPath { get; }

    public string PhysicalRoot { get; }

    internal string AllowedRoot { get; }

    internal string DeclaringFile { get; }

    internal string ConfiguredPath { get; }
}

public sealed class WorkspacePathEscapeException : IOException
{
    public WorkspacePathEscapeException(
        string logicalPath,
        string physicalPath,
        string allowedRoot)
        : base(
            $"Workspace path escapes its allowed root. " +
            $"Logical='{logicalPath}', Physical='{physicalPath}', Root='{allowedRoot}'.")
    {
        LogicalPath = logicalPath;
        PhysicalPath = physicalPath;
        AllowedRoot = allowedRoot;
    }

    public string LogicalPath { get; }

    public string PhysicalPath { get; }

    public string AllowedRoot { get; }
}

public static class WorkspacePathGuard
{
    public static string ResolveExecutionRoot(
        ExecutionWorkspaceDescriptor? workspace,
        string fallbackRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackRoot);
        if (workspace is null)
        {
            return Path.GetFullPath(fallbackRoot);
        }

        var root = workspace.Mode switch
        {
            CoWorkWorkspaceMode.Project => workspace.WorkspaceRoot,
            CoWorkWorkspaceMode.Worktree
                when workspace.WorktreeId is not null &&
                     !string.IsNullOrWhiteSpace(workspace.WorktreeRoot) &&
                     !string.IsNullOrWhiteSpace(workspace.BaseCommitSha) =>
                workspace.WorktreeRoot,
            _ => throw new InvalidOperationException(
                "Execution Workspace descriptor is invalid."),
        };
        return Path.GetFullPath(root!);
    }

    public static ResolvedWorkspacePath ResolveContained(
        string allowedRoot,
        string declaringFile,
        string configuredPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(allowedRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(declaringFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredPath);

        var root = Path.GetFullPath(allowedRoot);
        var declaration = Path.GetFullPath(declaringFile);
        var declarationDirectory = Path.GetDirectoryName(declaration)
            ?? throw new ArgumentException(
                "Declaring file must have a parent directory.",
                nameof(declaringFile));
        var logical = Path.GetFullPath(configuredPath, declarationDirectory);
        var physicalRoot = ResolvePhysical(root);

        if (!IsContained(root, logical))
        {
            throw new WorkspacePathEscapeException(logical, logical, root);
        }

        var physical = ResolvePhysical(logical);
        if (!IsContained(physicalRoot, physical))
        {
            throw new WorkspacePathEscapeException(logical, physical, physicalRoot);
        }

        return new ResolvedWorkspacePath(
            root,
            declaration,
            configuredPath,
            logical,
            physical,
            physicalRoot);
    }

    public static ResolvedWorkspacePath RevalidateForWrite(ResolvedWorkspacePath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return ResolveContained(path.AllowedRoot, path.DeclaringFile, path.ConfiguredPath);
    }

    private static string ResolvePhysical(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var pathRoot = Path.GetPathRoot(fullPath)
            ?? throw new IOException($"Path has no root: {fullPath}");
        var relative = fullPath[pathRoot.Length..];
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var current = pathRoot;

        for (var index = 0; index < segments.Length; index++)
        {
            var candidate = Path.Combine(current, segments[index]);
            var isDirectory = Directory.Exists(candidate);
            var isFile = !isDirectory && File.Exists(candidate);

            if (!isDirectory && !isFile)
            {
                return Path.GetFullPath(
                    segments[index..].Aggregate(current, Path.Combine));
            }

            FileSystemInfo info = isDirectory
                ? new DirectoryInfo(candidate)
                : new FileInfo(candidate);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true)
                    ?? throw new IOException(
                        $"Reparse point cannot be resolved safely: {candidate}");
                current = ResolvePhysical(target.FullName);
            }
            else
            {
                current = candidate;
            }
        }

        return Path.GetFullPath(current);
    }

    private static bool IsContained(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedCandidate =
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));

        if (string.Equals(normalizedRoot, normalizedCandidate, comparison))
        {
            return true;
        }

        return normalizedCandidate.StartsWith(
            normalizedRoot + Path.DirectorySeparatorChar,
            comparison);
    }
}
