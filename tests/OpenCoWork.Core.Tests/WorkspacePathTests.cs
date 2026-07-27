using System.Diagnostics;
using OpenCoWork.Core.Workspaces;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class WorkspacePathTests
{
    private static bool UseNativeSymbolicLinks =>
        !OperatingSystem.IsWindows()
        || Environment.GetEnvironmentVariable("OPENCOWORK_VALIDATE_WINDOWS_SYMLINKS") == "1";

    [Fact]
    public void Discovery_uses_explicit_then_nearest_opencowork_then_git_then_startup_directory()
    {
        using var files = new TempDirectory();
        var explicitRoot = files.CreateDirectory("explicit");
        var gitRoot = files.CreateDirectory("repo");
        File.WriteAllText(Path.Combine(gitRoot, ".git"), "gitdir: elsewhere");
        var outer = files.CreateDirectory("repo", "outer");
        Directory.CreateDirectory(Path.Combine(outer, ".opencowork"));
        var inner = files.CreateDirectory("repo", "outer", "inner");
        Directory.CreateDirectory(Path.Combine(inner, ".opencowork"));
        var start = files.CreateDirectory("repo", "outer", "inner", "src");

        Assert.Equal(
            Path.GetFullPath(explicitRoot),
            WorkspaceDiscovery.Discover(start, explicitRoot).WorkspaceRoot);
        Assert.Equal(
            Path.GetFullPath(inner),
            WorkspaceDiscovery.Discover(start).WorkspaceRoot);

        Directory.Delete(Path.Combine(inner, ".opencowork"));
        Directory.Delete(Path.Combine(outer, ".opencowork"));
        Assert.Equal(
            Path.GetFullPath(gitRoot),
            WorkspaceDiscovery.Discover(start).WorkspaceRoot);

        File.Delete(Path.Combine(gitRoot, ".git"));
        Assert.Equal(Path.GetFullPath(start), WorkspaceDiscovery.Discover(start).WorkspaceRoot);
    }

    [Fact]
    public void Explicit_workspace_must_be_an_existing_root_not_the_opencowork_directory()
    {
        using var files = new TempDirectory();
        var workspace = files.CreateDirectory("workspace");
        var metadata = Directory.CreateDirectory(Path.Combine(workspace, ".opencowork")).FullName;

        Assert.Throws<DirectoryNotFoundException>(
            () => WorkspaceDiscovery.Discover(files.Path, "missing"));
        Assert.Throws<ArgumentException>(
            () => WorkspaceDiscovery.Discover(files.Path, metadata));
    }

    [Fact]
    public void Paths_are_absolute_and_do_not_depend_on_later_current_directory_changes()
    {
        using var files = new TempDirectory();
        var root = files.CreateDirectory("workspace");
        var paths = new OpenCoWorkPaths(root);
        var expected = Path.Combine(Path.GetFullPath(root), ".opencowork", "runtime", "state.db");
        var threads = Path.Combine(Path.GetFullPath(root), ".opencowork", "runtime", "threads");

        Assert.Equal(expected, paths.StateDatabasePath);
        Assert.Equal(expected, paths.StateDatabasePath);
        Assert.Equal(threads, paths.ThreadsDirectory);
        Assert.Equal(Path.Combine(threads, "active"), paths.ActiveThreadsDirectory);
        Assert.Equal(Path.Combine(threads, "archived"), paths.ArchivedThreadsDirectory);
        Assert.Equal(Path.Combine(threads, "deleting"), paths.DeletingThreadsDirectory);
        Assert.Equal(
            Path.Combine(Path.GetFullPath(root), ".opencowork", "runtime", "recovery", "threads"),
            paths.ThreadRecoveryDirectory);
        Assert.True(Path.IsPathFullyQualified(paths.StateDatabasePath));
    }

    [Fact]
    public void Containment_allows_internal_links_and_missing_targets_but_rejects_escape()
    {
        using var files = new TempDirectory();
        var root = files.CreateDirectory("workspace");
        var config = files.Write("workspace", ".opencowork", "config.jsonc", "{}");
        var internalTarget = files.CreateDirectory("workspace", "actual");
        var outside = files.CreateDirectory("outside");
        var internalLink = Path.Combine(root, "inside-link");
        var externalLink = Path.Combine(root, "outside-link");
        files.CreateDirectoryLink(internalLink, internalTarget);
        files.CreateDirectoryLink(externalLink, outside);

        var internalResult = WorkspacePathGuard.ResolveContained(
            root,
            config,
            "../inside-link/new/file.txt");
        var missingResult = WorkspacePathGuard.ResolveContained(
            root,
            config,
            "../missing/child.txt");

        Assert.EndsWith(
            Path.Combine("workspace", "actual", "new", "file.txt"),
            internalResult.PhysicalPath);
        Assert.EndsWith(
            Path.Combine("workspace", "missing", "child.txt"),
            missingResult.PhysicalPath,
            StringComparison.OrdinalIgnoreCase);
        Assert.Throws<WorkspacePathEscapeException>(
            () => WorkspacePathGuard.ResolveContained(root, config, "../../outside.txt"));
        Assert.Throws<WorkspacePathEscapeException>(
            () => WorkspacePathGuard.ResolveContained(
                root,
                config,
                "../outside-link/file.txt"));

        if (UseNativeSymbolicLinks)
        {
            var outsideFile = files.Write("outside", "outside.txt", "outside");
            var outsideFileLink = Path.Combine(root, "outside-file.txt");
            File.CreateSymbolicLink(outsideFileLink, outsideFile);
            Assert.Throws<WorkspacePathEscapeException>(
                () => WorkspacePathGuard.ResolveContained(
                    root,
                    config,
                    "../outside-file.txt"));
        }

        if (OperatingSystem.IsWindows())
        {
            var caseVariant = WorkspacePathGuard.ResolveContained(
                root.ToUpperInvariant(),
                config,
                "../inside-link/new/file.txt");
            Assert.Equal(internalResult.PhysicalPath, caseVariant.PhysicalPath, ignoreCase: true);
        }
    }

    [Fact]
    public void Write_revalidation_detects_a_link_replaced_after_the_initial_check()
    {
        using var files = new TempDirectory();
        var root = files.CreateDirectory("workspace");
        var config = files.Write("workspace", ".opencowork", "config.jsonc", "{}");
        var inside = files.CreateDirectory("workspace", "inside");
        var outside = files.CreateDirectory("outside");
        var link = Path.Combine(root, "switch");
        files.CreateDirectoryLink(link, inside);
        var checkedPath = WorkspacePathGuard.ResolveContained(
            root,
            config,
            "../switch/result.txt");

        Directory.Delete(link);
        files.CreateDirectoryLink(link, outside);

        Assert.Throws<WorkspacePathEscapeException>(
            () => WorkspacePathGuard.RevalidateForWrite(checkedPath));
    }

    private sealed class TempDirectory : IDisposable
    {
        private readonly HashSet<string> _links = new(StringComparer.OrdinalIgnoreCase);

        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"opencowork-paths-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string CreateDirectory(params string[] segments)
        {
            var path = segments.Aggregate(Path, System.IO.Path.Combine);
            Directory.CreateDirectory(path);
            return path;
        }

        public string Write(params string[] segmentsAndContents)
        {
            var contents = segmentsAndContents[^1];
            var path = segmentsAndContents
                .SkipLast(1)
                .Aggregate(Path, System.IO.Path.Combine);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
            return path;
        }

        public void CreateDirectoryLink(string path, string target)
        {
            _links.Add(path);
            if (UseNativeSymbolicLinks)
            {
                Directory.CreateSymbolicLink(path, target);
                return;
            }

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/d /c mklink /J \"{path}\" \"{target}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }) ?? throw new InvalidOperationException("Could not start mklink.");

            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new IOException(process.StandardError.ReadToEnd());
            }
        }

        public void Dispose()
        {
            foreach (var link in _links.Where(Directory.Exists))
            {
                Directory.Delete(link);
            }

            Directory.Delete(Path, recursive: true);
        }
    }
}
