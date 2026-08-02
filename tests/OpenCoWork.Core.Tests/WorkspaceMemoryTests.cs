using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.State;
using OpenCoWork.Core.Tools;
using OpenCoWork.Core.Workspaces;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class WorkspaceMemoryTests
{
    [Fact]
    public async Task Write_versions_immutable_blobs_and_reports_conflicts()
    {
        using var fixture = await MemoryFixture.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var memoryId = Guid.CreateVersion7();

        var first = await fixture.Runtime.WriteAsync(
            JsonSerializer.SerializeToElement(new
            {
                memoryId,
                expectedVersion = 0,
                title = "Release rule",
                summary = "Real host evidence",
                tags = new[] { "Release", "Windows" },
                body = "# Rule\nCross-publish is not real-host validation.\n",
            }),
            cancellationToken);
        var second = await fixture.Runtime.WriteAsync(
            JsonSerializer.SerializeToElement(new
            {
                memoryId,
                expectedVersion = 1,
                title = "Release rule",
                summary = "Real host evidence is required",
                tags = new[] { "Release", "Windows" },
                body = "# Rule\nKeep Windows pending until real-host validation.\n",
            }),
            cancellationToken);
        var conflict = await fixture.Runtime.WriteAsync(
            JsonSerializer.SerializeToElement(new
            {
                memoryId,
                expectedVersion = 1,
                title = "Stale update",
                summary = "Must fail",
                tags = Array.Empty<string>(),
                body = "# Stale\nThis blob is an orphan after the conflict.\n",
            }),
            cancellationToken);

        Assert.Equal(1, first.Output!.Value.GetProperty("version").GetInt32());
        Assert.Equal(2, second.Output!.Value.GetProperty("version").GetInt32());
        Assert.Equal(
            WorkspaceMemoryErrorCodes.VersionConflict,
            conflict.Error!.Code);
        Assert.Equal(
            3,
            Directory.GetFiles(fixture.ContentDirectory, "*.md").Length);
        Assert.Single(await fixture.Runtime.FindOrphanBlobNamesAsync(cancellationToken));

        var current = await fixture.Runtime.ReadAsync(
            JsonSerializer.SerializeToElement(new { memoryId }),
            cancellationToken);
        var original = await fixture.Runtime.ReadAsync(
            JsonSerializer.SerializeToElement(new { memoryId, version = 1 }),
            cancellationToken);

        Assert.Contains(
            "Keep Windows pending",
            current.Output!.Value.GetProperty("body").GetString());
        Assert.Contains(
            "Cross-publish",
            original.Output!.Value.GetProperty("body").GetString());
    }

    [Fact]
    public async Task Search_uses_only_metadata_and_archive_preserves_blobs()
    {
        using var fixture = await MemoryFixture.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var memoryId = Guid.CreateVersion7();
        await fixture.Runtime.WriteAsync(
            JsonSerializer.SerializeToElement(new
            {
                memoryId,
                expectedVersion = 0,
                title = "Provider validation",
                summary = "DeepSeek first",
                tags = new[] { "macOS" },
                body = "body-only-canary",
            }),
            cancellationToken);

        var metadataMatch = await fixture.Runtime.SearchAsync(
            JsonSerializer.SerializeToElement(new { query = "deepseek" }),
            cancellationToken);
        var bodyOnlyMiss = await fixture.Runtime.SearchAsync(
            JsonSerializer.SerializeToElement(new { query = "body-only-canary" }),
            cancellationToken);
        var archive = await fixture.Runtime.ArchiveAsync(
            JsonSerializer.SerializeToElement(new
            {
                memoryId,
                expectedVersion = 1,
            }),
            cancellationToken);
        var active = await fixture.Runtime.ListAsync(
            JsonSerializer.SerializeToElement(new { }),
            cancellationToken);
        var archived = await fixture.Runtime.ListAsync(
            JsonSerializer.SerializeToElement(new { includeArchived = true }),
            cancellationToken);

        Assert.Single(metadataMatch.Output!.Value.GetProperty("items").EnumerateArray());
        Assert.Empty(bodyOnlyMiss.Output!.Value.GetProperty("items").EnumerateArray());
        Assert.True(archive.IsSuccess, archive.Error?.ToString());
        Assert.Empty(active.Output!.Value.GetProperty("items").EnumerateArray());
        Assert.Single(archived.Output!.Value.GetProperty("items").EnumerateArray());
        Assert.Single(Directory.GetFiles(fixture.ContentDirectory, "*.md"));
    }

    [Fact]
    public async Task Write_rejects_oversized_body_without_truncation()
    {
        using var fixture = await MemoryFixture.CreateAsync();
        var result = await fixture.Runtime.WriteAsync(
            JsonSerializer.SerializeToElement(new
            {
                memoryId = Guid.CreateVersion7(),
                expectedVersion = 0,
                title = "Too large",
                summary = "No truncation",
                tags = Array.Empty<string>(),
                body = new string('x', 64 * 1024 + 1),
            }),
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolErrorCodes.InputTooLarge, result.Error!.Code);
        Assert.False(Directory.Exists(fixture.ContentDirectory));
    }

    [Fact]
    public async Task Write_rejects_a_content_directory_symlink_escape()
    {
        using var fixture = await MemoryFixture.CreateAsync();
        var outside = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-memory-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.ContentDirectory)!);
        CreateDirectoryLink(fixture.ContentDirectory, outside);

        try
        {
            var result = await fixture.Runtime.WriteAsync(
                JsonSerializer.SerializeToElement(new
                {
                    memoryId = Guid.CreateVersion7(),
                    expectedVersion = 0,
                    title = "Denied",
                    summary = "Denied",
                    tags = Array.Empty<string>(),
                    body = "must not escape",
                }),
                TestContext.Current.CancellationToken);

            Assert.Equal(ToolErrorCodes.PathDenied, result.Error!.Code);
            Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
        }
        finally
        {
            Directory.Delete(fixture.ContentDirectory);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void Runtime_registers_the_workspace_memory_surface()
    {
        using var workspace = new TemporaryWorkspace();
        var paths = new OpenCoWorkPaths(workspace.Root);
        var state = new StateRuntime(paths, TimeSpan.FromSeconds(1));
        var memory = new WorkspaceMemoryRuntime(paths, state);
        var runtime = new ToolRuntime(
            paths,
            models: null,
            sourceControl: null,
            terminal: null,
            memory);

        var registrations = runtime.Registrations
            .Where(item => item.Definition.Name.Namespace == "memory")
            .OrderBy(item => item.Definition.Name.Name)
            .ToArray();

        Assert.Equal(
            ["archive", "list", "read", "search", "write"],
            registrations.Select(item => item.Definition.Name.Name));
        Assert.All(
            registrations,
            item => Assert.Equal(
                ToolInvocationAudience.Model | ToolInvocationAudience.Host,
                item.Audience));
        Assert.Contains(
            registrations,
            item => item.Definition.Name.Name == "write" &&
                    item.Definition.Effects.HasFlag(ToolEffect.WorkspaceWrite));
    }

    private sealed class MemoryFixture : IDisposable
    {
        private MemoryFixture(
            TemporaryWorkspace workspace,
            WorkspaceMemoryRuntime runtime)
        {
            Workspace = workspace;
            Runtime = runtime;
        }

        public TemporaryWorkspace Workspace { get; }

        public WorkspaceMemoryRuntime Runtime { get; }

        public string ContentDirectory => Path.Combine(
            Workspace.Root,
            ".opencowork",
            "runtime",
            "memory",
            "content");

        public static async Task<MemoryFixture> CreateAsync()
        {
            var workspace = new TemporaryWorkspace();
            var paths = new OpenCoWorkPaths(workspace.Root);
            var state = new StateRuntime(paths, TimeSpan.FromSeconds(1));
            await state.InitializeAsync(TestContext.Current.CancellationToken);
            return new MemoryFixture(
                workspace,
                new WorkspaceMemoryRuntime(paths, state));
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Workspace.Dispose();
        }
    }

    private static void CreateDirectoryLink(string path, string target)
    {
        if (!OperatingSystem.IsWindows())
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
        Assert.Equal(0, process.ExitCode);
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        public TemporaryWorkspace()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"opencowork-memory-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(Root, ".opencowork"));
        }

        public string Root { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
