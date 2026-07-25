using System.Text;
using OpenCoWork.Core.State;

namespace OpenCoWork.Core.Workspaces;

public static class WorkspaceInitializer
{
    private const string ManagedBegin = "# <opencowork-managed>";
    private const string ManagedEnd = "# </opencowork-managed>";
    private const string DefaultConfig =
        "// OpenCoWork workspace configuration.\n{}\n";
    private const string ManagedIgnoreBlock =
        """
        # <opencowork-managed>
        config.local.jsonc
        runtime/
        # </opencowork-managed>

        """;
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static Task InitializeAsync(
        OpenCoWorkPaths paths,
        TimeSpan busyTimeout,
        CancellationToken cancellationToken = default) =>
        InitializeAsync(
            paths,
            busyTimeout,
            static (statePaths, timeout, token) =>
                new StateRuntime(statePaths, timeout).InitializeAsync(token),
            cancellationToken);

    internal static Task InitializeAsync(
        OpenCoWorkPaths paths,
        TimeSpan busyTimeout,
        Func<OpenCoWorkPaths, CancellationToken, Task> initializeState,
        CancellationToken cancellationToken) =>
        InitializeAsync(
            paths,
            busyTimeout,
            (statePaths, _, token) => initializeState(statePaths, token),
            cancellationToken);

    private static async Task InitializeAsync(
        OpenCoWorkPaths paths,
        TimeSpan busyTimeout,
        Func<OpenCoWorkPaths, TimeSpan, CancellationToken, Task> initializeState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(initializeState);

        if (Directory.Exists(paths.OpenCoWorkDirectory))
        {
            await RepairExistingAsync(
                paths,
                busyTimeout,
                initializeState,
                cancellationToken);
            return;
        }

        await InitializeFirstAsync(
            paths,
            busyTimeout,
            initializeState,
            cancellationToken);
    }

    private static async Task InitializeFirstAsync(
        OpenCoWorkPaths paths,
        TimeSpan busyTimeout,
        Func<OpenCoWorkPaths, TimeSpan, CancellationToken, Task> initializeState,
        CancellationToken cancellationToken)
    {
        var temporaryDirectory = Path.Combine(
            paths.WorkspaceRoot,
            $".opencowork.init-{Guid.NewGuid():N}");
        Guard(paths.WorkspaceRoot, temporaryDirectory);
        Directory.CreateDirectory(temporaryDirectory);
        var temporaryPaths = new OpenCoWorkPaths(
            paths.WorkspaceRoot,
            temporaryDirectory);

        try
        {
            await WriteAtomicAsync(
                paths.WorkspaceRoot,
                temporaryPaths.ConfigPath,
                DefaultConfig,
                cancellationToken);
            await WriteAtomicAsync(
                paths.WorkspaceRoot,
                Path.Combine(temporaryDirectory, ".gitignore"),
                ManagedIgnoreBlock,
                cancellationToken);
            Directory.CreateDirectory(temporaryPaths.RuntimeDirectory);
            Guard(paths.WorkspaceRoot, temporaryPaths.StateDatabasePath);
            await initializeState(temporaryPaths, busyTimeout, cancellationToken);

            if (!File.Exists(temporaryPaths.StateDatabasePath))
            {
                throw new InvalidDataException(
                    "State initialization completed without creating state.db.");
            }

            Guard(paths.WorkspaceRoot, temporaryDirectory);
            Guard(paths.WorkspaceRoot, paths.OpenCoWorkDirectory);
            Directory.Move(temporaryDirectory, paths.OpenCoWorkDirectory);
        }
        catch (Exception initializationException)
        {
            try
            {
                if (Directory.Exists(temporaryDirectory))
                {
                    Guard(paths.WorkspaceRoot, temporaryDirectory);
                    Directory.Delete(temporaryDirectory, recursive: true);
                }
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(
                    "Workspace initialization and temporary cleanup both failed.",
                    initializationException,
                    cleanupException);
            }

            throw;
        }
    }

    private static async Task RepairExistingAsync(
        OpenCoWorkPaths paths,
        TimeSpan busyTimeout,
        Func<OpenCoWorkPaths, TimeSpan, CancellationToken, Task> initializeState,
        CancellationToken cancellationToken)
    {
        var ignorePath = Path.Combine(paths.OpenCoWorkDirectory, ".gitignore");
        Guard(paths.WorkspaceRoot, paths.OpenCoWorkDirectory);
        if (!IsRecognized(paths, ignorePath))
        {
            throw new InvalidDataException(
                $"Existing '{paths.OpenCoWorkDirectory}' is not recognized as OpenCoWork metadata. " +
                "Move or inspect it before retrying init.");
        }

        if (!File.Exists(paths.ConfigPath))
        {
            await WriteAtomicAsync(
                paths.WorkspaceRoot,
                paths.ConfigPath,
                DefaultConfig,
                cancellationToken);
        }

        var existingIgnore = File.Exists(ignorePath)
            ? await File.ReadAllTextAsync(ignorePath, cancellationToken)
            : string.Empty;
        var updatedIgnore = UpdateManagedBlock(existingIgnore);
        if (!string.Equals(existingIgnore, updatedIgnore, StringComparison.Ordinal))
        {
            await WriteAtomicAsync(
                paths.WorkspaceRoot,
                ignorePath,
                updatedIgnore,
                cancellationToken);
        }

        Guard(paths.WorkspaceRoot, paths.StateDatabasePath);
        await initializeState(paths, busyTimeout, cancellationToken);
    }

    private static bool IsRecognized(OpenCoWorkPaths paths, string ignorePath)
    {
        if (File.Exists(paths.ConfigPath) || File.Exists(paths.StateDatabasePath))
        {
            return true;
        }

        if (!File.Exists(ignorePath))
        {
            return false;
        }

        var ignore = File.ReadAllText(ignorePath);
        return ignore.Contains(ManagedBegin, StringComparison.Ordinal) &&
               ignore.Contains(ManagedEnd, StringComparison.Ordinal);
    }

    private static string UpdateManagedBlock(string contents)
    {
        var begin = contents.IndexOf(ManagedBegin, StringComparison.Ordinal);
        var end = contents.IndexOf(ManagedEnd, StringComparison.Ordinal);

        if ((begin < 0) != (end < 0) || (begin >= 0 && end < begin))
        {
            throw new InvalidDataException(
                "The OpenCoWork .gitignore managed block is incomplete.");
        }

        if (begin < 0)
        {
            var separator = contents.Length == 0 ||
                            contents.EndsWith('\n') ||
                            contents.EndsWith('\r')
                ? string.Empty
                : "\n";
            return contents + separator + ManagedIgnoreBlock;
        }

        var suffix = contents[(end + ManagedEnd.Length)..];
        if (suffix.StartsWith("\r\n", StringComparison.Ordinal))
        {
            suffix = suffix[2..];
        }
        else if (suffix.StartsWith('\n') || suffix.StartsWith('\r'))
        {
            suffix = suffix[1..];
        }

        return contents[..begin] + ManagedIgnoreBlock + suffix;
    }

    private static async Task WriteAtomicAsync(
        string workspaceRoot,
        string destination,
        string contents,
        CancellationToken cancellationToken)
    {
        var temporary = $"{destination}.tmp-{Guid.NewGuid():N}";
        Guard(workspaceRoot, destination);
        Directory.CreateDirectory(
            Path.GetDirectoryName(destination)
            ?? throw new InvalidDataException($"Path has no parent: {destination}"));

        try
        {
            Guard(workspaceRoot, temporary);
            await File.WriteAllTextAsync(
                temporary,
                contents,
                Utf8NoBom,
                cancellationToken);
            Guard(workspaceRoot, destination);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void Guard(string workspaceRoot, string path)
    {
        var declaration = Path.Combine(workspaceRoot, ".opencowork-path-anchor");
        var relative = Path.GetRelativePath(workspaceRoot, path);
        var resolved = WorkspacePathGuard.ResolveContained(
            workspaceRoot,
            declaration,
            relative);
        WorkspacePathGuard.RevalidateForWrite(resolved);
    }
}
