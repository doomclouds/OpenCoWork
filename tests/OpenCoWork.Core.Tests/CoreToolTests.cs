using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Tools;
using OpenCoWork.Core.Workspaces;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed partial class CoreToolTests
{
    [Fact]
    public async Task File_tools_use_the_calling_thread_workspace_and_private_scratchpad()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var origin = CreateWorkspace();
        var worker = CreateWorkspace();
        var scratchpad = CreateWorkspace();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(origin, "origin.txt"),
                "origin",
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(worker, "worker.txt"),
                "worker",
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(scratchpad, "notes.txt"),
                "notes",
                cancellationToken);
            var runtime = new ToolRuntime(new OpenCoWorkPaths(origin));
            Assert.True(runtime.TryResolveBinding(
                new RuntimeBindingId("core.file.read.v1"),
                out var binding));
            var workspace = new ExecutionWorkspaceDescriptor(
                CoWorkWorkspaceMode.Project,
                worker,
                scratchpad,
                WorktreeId: null,
                WorktreeRoot: null,
                BaseCommitSha: null);
            var provenance = new CoWorkThreadProvenance(
                Guid.CreateVersion7(),
                CoWorkAgentRunKind.Direct);

            var read = await binding!.ContextualExecutor!(
                ContextualInvocation(
                    runtime,
                    new { path = "worker.txt" },
                    workspace,
                    provenance),
                cancellationToken);
            var scratch = await binding.ContextualExecutor(
                ContextualInvocation(
                    runtime,
                    new { path = "notes.txt", area = "scratchpad" },
                    workspace,
                    provenance),
                cancellationToken);
            var escape = await binding.ContextualExecutor(
                ContextualInvocation(
                    runtime,
                    new { path = "../origin.txt" },
                    workspace,
                    provenance),
                cancellationToken);
            var unauthorizedArea = await binding.ContextualExecutor(
                ContextualInvocation(
                    runtime,
                    new { path = "notes.txt", area = "scratchpad" },
                    workspace,
                    provenance: null),
                cancellationToken);

            Assert.Equal(
                "worker",
                read.Output!.Value.GetProperty("content").GetString());
            Assert.Equal(
                "notes",
                scratch.Output!.Value.GetProperty("content").GetString());
            Assert.Equal(ToolErrorCodes.PathDenied, escape.Error!.Code);
            Assert.Equal(ToolErrorCodes.PathDenied, unauthorizedArea.Error!.Code);
        }
        finally
        {
            Directory.Delete(origin, recursive: true);
            Directory.Delete(worker, recursive: true);
            Directory.Delete(scratchpad, recursive: true);
        }
    }

    [Fact]
    public async Task List_hides_blacklisted_entries_and_read_returns_a_line_window()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = CreateWorkspace();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, ".git"));
            Directory.CreateDirectory(
                Path.Combine(directory, ".opencowork", "runtime"));
            Directory.CreateDirectory(Path.Combine(directory, "sub"));
            await File.WriteAllTextAsync(
                Path.Combine(directory, ".opencowork", "config.local.jsonc"),
                "secret",
                cancellationToken);
            var bytes = Encoding.UTF8.GetBytes("one\r\ntwo\nthree\nfour\n");
            await File.WriteAllBytesAsync(
                Path.Combine(directory, "a.txt"),
                bytes,
                cancellationToken);
            var runtime = new ToolRuntime(new OpenCoWorkPaths(directory));

            var listed = await InvokeAsync(
                runtime,
                "core.file.list.v1",
                new { path = "." },
                cancellationToken);

            Assert.True(listed.IsSuccess);
            var entries = listed.Output!.Value
                .GetProperty("entries")
                .EnumerateArray()
                .ToArray();
            Assert.Equal(
                [".opencowork", "a.txt", "sub"],
                entries.Select(entry => entry.GetProperty("name").GetString()));
            Assert.Equal(
                ["directory", "file", "directory"],
                entries.Select(entry => entry.GetProperty("type").GetString()));
            Assert.Equal(
                bytes.Length,
                entries[1].GetProperty("byteCount").GetInt64());

            var openCoWork = await InvokeAsync(
                runtime,
                "core.file.list.v1",
                new { path = ".opencowork" },
                cancellationToken);
            Assert.Empty(
                openCoWork.Output!.Value
                    .GetProperty("entries")
                    .EnumerateArray());

            var read = await InvokeAsync(
                runtime,
                "core.file.read.v1",
                new { path = "a.txt", startLine = 2, lineCount = 2 },
                cancellationToken);

            Assert.True(read.IsSuccess);
            var output = read.Output!.Value;
            Assert.Equal(2, output.GetProperty("startLine").GetInt32());
            Assert.Equal(3, output.GetProperty("endLine").GetInt32());
            Assert.True(output.GetProperty("hasMore").GetBoolean());
            Assert.Equal("two\nthree", output.GetProperty("content").GetString());
            Assert.Equal(
                Sha256(bytes),
                output.GetProperty("sha256").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task File_paths_reject_escape_blacklist_and_external_links()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = CreateWorkspace();
        var outside = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-core-file-outside-{Guid.NewGuid():N}");
        var escape = Path.Combine(directory, "escape");
        var dangling = Path.Combine(directory, "dangling");
        Directory.CreateDirectory(outside);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(outside, "secret.txt"),
                "secret",
                cancellationToken);
            Directory.CreateDirectory(Path.Combine(directory, ".git"));
            await File.WriteAllTextAsync(
                Path.Combine(directory, ".git", "secret.txt"),
                "secret",
                cancellationToken);
            CreateDirectoryLink(escape, outside);
            CreateDirectoryLink(
                dangling,
                OperatingSystem.IsWindows()
                    ? outside
                    : Path.Combine(outside, "missing"));
            var runtime = new ToolRuntime(new OpenCoWorkPaths(directory));

            foreach (var path in new[]
                     {
                         "../secret.txt",
                         Path.Combine(outside, "secret.txt"),
                         ".git/secret.txt",
                         "escape/secret.txt",
                         "dangling/secret.txt",
                     })
            {
                var result = await InvokeAsync(
                    runtime,
                    "core.file.read.v1",
                    new { path },
                    cancellationToken);

                Assert.False(result.IsSuccess);
                Assert.Equal(ToolErrorCodes.PathDenied, result.Error!.Code);
                Assert.DoesNotContain(
                    directory,
                    result.Error.Message,
                    StringComparison.Ordinal);
                Assert.DoesNotContain(
                    outside,
                    result.Error.Message,
                    StringComparison.Ordinal);
            }
        }
        finally
        {
            foreach (var link in new[] { escape, dangling })
            {
                try
                {
                    Directory.Delete(link);
                }
                catch (DirectoryNotFoundException)
                {
                }
            }

            Directory.Delete(directory, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task Blacklist_uses_platform_path_comparison()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = CreateWorkspace();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, ".GIT"));
            await File.WriteAllTextAsync(
                Path.Combine(directory, ".GIT", "visible.txt"),
                "visible",
                cancellationToken);
            var runtime = new ToolRuntime(new OpenCoWorkPaths(directory));

            var result = await InvokeAsync(
                runtime,
                "core.file.read.v1",
                new { path = ".GIT/visible.txt" },
                cancellationToken);

            Assert.Equal(OperatingSystem.IsWindows(), !result.IsSuccess);
            if (!result.IsSuccess)
            {
                Assert.Equal(ToolErrorCodes.PathDenied, result.Error!.Code);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Read_rejects_directories_binary_invalid_utf8_and_oversized_files()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = CreateWorkspace();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "folder"));
            await File.WriteAllBytesAsync(
                Path.Combine(directory, "binary.bin"),
                [0x61, 0x01, 0x62],
                cancellationToken);
            await File.WriteAllBytesAsync(
                Path.Combine(directory, "invalid.txt"),
                [0xC3, 0x28],
                cancellationToken);
            await File.WriteAllBytesAsync(
                Path.Combine(directory, "large.txt"),
                new byte[ToolRuntimeLimits.MaximumBindingResultBytes + 1],
                cancellationToken);
            var runtime = new ToolRuntime(new OpenCoWorkPaths(directory));

            foreach (var path in new[] { "folder", "binary.bin", "invalid.txt" })
            {
                var result = await InvokeAsync(
                    runtime,
                    "core.file.read.v1",
                    new { path },
                    cancellationToken);
                Assert.Equal(
                    ToolErrorCodes.ContentUnsupported,
                    result.Error!.Code);
            }

            var oversized = await InvokeAsync(
                runtime,
                "core.file.read.v1",
                new { path = "large.txt" },
                cancellationToken);
            Assert.Equal(
                ToolErrorCodes.OutputLimitExceeded,
                oversized.Error!.Code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task List_stops_before_buffering_an_oversized_result()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = CreateWorkspace();
        try
        {
            for (var index = 0; index < 5_000; index++)
            {
                File.WriteAllText(
                    Path.Combine(
                        directory,
                        $"{index:D4}-{new string('x', 180)}.txt"),
                    string.Empty);
            }

            var result = await InvokeAsync(
                new ToolRuntime(new OpenCoWorkPaths(directory)),
                "core.file.list.v1",
                new { path = "." },
                cancellationToken);

            Assert.Equal(
                ToolErrorCodes.OutputLimitExceeded,
                result.Error!.Code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Apply_patch_adds_updates_deletes_moves_and_returns_only_metadata()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = CreateWorkspace();
        try
        {
            var edit = "one\ntwo\nthree\nfour\n";
            var delete = "delete me\n";
            var move = "old name\n";
            await File.WriteAllTextAsync(
                Path.Combine(directory, "edit.txt"),
                edit,
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(directory, "delete.txt"),
                delete,
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(directory, "move.txt"),
                move,
                cancellationToken);
            var patch = $$"""
                *** Begin Patch
                *** Add File: added.txt
                +added
                *** Update File: edit.txt
                *** Expected SHA256: {{Sha256(Encoding.UTF8.GetBytes(edit))}}
                @@
                 one
                -two
                +second
                @@
                 three
                -four
                +fourth
                *** Delete File: delete.txt
                *** Expected SHA256: {{Sha256(Encoding.UTF8.GetBytes(delete))}}
                *** Update File: move.txt
                *** Move to: moved.txt
                *** Expected SHA256: {{Sha256(Encoding.UTF8.GetBytes(move))}}
                @@
                -old name
                +new name
                *** End Patch
                """;

            var result = await InvokeAsync(
                new ToolRuntime(new OpenCoWorkPaths(directory)),
                "core.file.apply_patch.v1",
                new { patch },
                cancellationToken);

            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.Equal(
                "added\n",
                await File.ReadAllTextAsync(
                    Path.Combine(directory, "added.txt"),
                    cancellationToken));
            Assert.Equal(
                "one\nsecond\nthree\nfourth\n",
                await File.ReadAllTextAsync(
                    Path.Combine(directory, "edit.txt"),
                    cancellationToken));
            Assert.False(File.Exists(Path.Combine(directory, "delete.txt")));
            Assert.False(File.Exists(Path.Combine(directory, "move.txt")));
            Assert.Equal(
                "new name\n",
                await File.ReadAllTextAsync(
                    Path.Combine(directory, "moved.txt"),
                    cancellationToken));
            var output = result.Output!.Value.GetRawText();
            Assert.DoesNotContain("second", output, StringComparison.Ordinal);
            Assert.Equal(
                ["add", "delete", "move", "update"],
                result.Output.Value.GetProperty("operations")
                    .EnumerateArray()
                    .Select(item => item.GetProperty("operation").GetString())
                    .Order(StringComparer.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Apply_patch_preflights_the_whole_package_before_writing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = CreateWorkspace();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory, "keep.txt"),
                "old\n",
                cancellationToken);
            var result = await InvokeAsync(
                new ToolRuntime(new OpenCoWorkPaths(directory)),
                "core.file.apply_patch.v1",
                new
                {
                    patch = """
                        *** Begin Patch
                        *** Add File: added.txt
                        +added
                        *** Update File: keep.txt
                        *** Expected SHA256: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
                        @@
                        -old
                        +changed
                        *** End Patch
                        """,
                },
                cancellationToken);

            Assert.False(result.IsSuccess);
            Assert.Equal(ToolErrorCodes.PreconditionFailed, result.Error!.Code);
            Assert.False(File.Exists(Path.Combine(directory, "added.txt")));
            Assert.Equal(
                "old\n",
                await File.ReadAllTextAsync(
                    Path.Combine(directory, "keep.txt"),
                    cancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Apply_patch_reports_partial_cross_file_failure_as_outcome_unknown()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = CreateWorkspace();
        try
        {
            const string before = "before\n";
            await File.WriteAllTextAsync(
                Path.Combine(directory, "a.txt"),
                before,
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(directory, "b.txt"),
                before,
                cancellationToken);
            var hash = Sha256(Encoding.UTF8.GetBytes(before));
            var patch = $$"""
                *** Begin Patch
                *** Update File: a.txt
                *** Expected SHA256: {{hash}}
                @@
                -before
                +after a
                *** Update File: b.txt
                *** Expected SHA256: {{hash}}
                @@
                -before
                +after b
                *** End Patch
                """;

            var result = await InvokeFileAsync(
                new CoreFileTools(
                    new OpenCoWorkPaths(directory),
                    failPatchCommitAt: 1).ApplyPatchAsync,
                new { patch },
                cancellationToken);

            Assert.False(result.IsSuccess);
            Assert.Equal(ToolErrorCodes.OutcomeUnknown, result.Error!.Code);
            Assert.Contains("a.txt", result.Error.Message, StringComparison.Ordinal);
            Assert.Contains("b.txt", result.Error.Message, StringComparison.Ordinal);
            Assert.Equal(
                "after a\n",
                await File.ReadAllTextAsync(
                    Path.Combine(directory, "a.txt"),
                    cancellationToken));
            Assert.Equal(
                before,
                await File.ReadAllTextAsync(
                    Path.Combine(directory, "b.txt"),
                    cancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Apply_patch_preserves_crlf_supports_empty_files_and_rejects_invalid_utf8()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = CreateWorkspace();
        try
        {
            var crlf = Encoding.UTF8.GetBytes("one\r\ntwo\r\n");
            await File.WriteAllBytesAsync(
                Path.Combine(directory, "crlf.txt"),
                crlf,
                cancellationToken);
            var invalid = new byte[] { 0xC3, 0x28 };
            await File.WriteAllBytesAsync(
                Path.Combine(directory, "invalid.txt"),
                invalid,
                cancellationToken);
            var runtime = new ToolRuntime(new OpenCoWorkPaths(directory));
            var valid = await InvokeAsync(
                runtime,
                "core.file.apply_patch.v1",
                new
                {
                    patch = $$"""
                        *** Begin Patch
                        *** Add File: empty.txt
                        *** Update File: crlf.txt
                        *** Expected SHA256: {{Sha256(crlf)}}
                        @@
                         one
                        -two
                        +second
                        *** End Patch
                        """,
                },
                cancellationToken);
            Assert.True(valid.IsSuccess, valid.Error?.Message);
            Assert.Empty(await File.ReadAllBytesAsync(
                Path.Combine(directory, "empty.txt"),
                cancellationToken));
            Assert.Equal(
                Encoding.UTF8.GetBytes("one\r\nsecond\r\n"),
                await File.ReadAllBytesAsync(
                    Path.Combine(directory, "crlf.txt"),
                    cancellationToken));

            var rejected = await InvokeAsync(
                runtime,
                "core.file.apply_patch.v1",
                new
                {
                    patch = $$"""
                        *** Begin Patch
                        *** Update File: invalid.txt
                        *** Expected SHA256: {{Sha256(invalid)}}
                        @@
                        -x
                        +y
                        *** End Patch
                        """,
                },
                cancellationToken);
            Assert.Equal(ToolErrorCodes.ContentUnsupported, rejected.Error!.Code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Apply_patch_rejects_unsafe_duplicate_and_oversized_inputs()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = CreateWorkspace();
        try
        {
            var runtime = new ToolRuntime(new OpenCoWorkPaths(directory));
            var patches = new[]
            {
                "*** Begin Patch\n*** Add File: ../escape.txt\n+x\n*** End Patch",
                "*** Begin Patch\n*** Add File: /absolute.txt\n+x\n*** End Patch",
                "*** Begin Patch\n*** Add File: .opencowork/secret.txt\n+x\n*** End Patch",
                "*** Begin Patch\n*** Add File: same.txt\n+x\n*** Add File: same.txt\n+y\n*** End Patch",
                "*** Begin Patch\n*** Add File: bad.txt\nmissing-prefix\n*** End Patch",
            };
            foreach (var patch in patches)
            {
                var result = await InvokeAsync(
                    runtime,
                    "core.file.apply_patch.v1",
                    new { patch },
                    cancellationToken);
                Assert.False(result.IsSuccess);
            }

            var oversized = await InvokeFileAsync(
                new CoreFileTools(new OpenCoWorkPaths(directory)).ApplyPatchAsync,
                new { patch = new string('é', 300_000) },
                cancellationToken);
            Assert.Equal(ToolErrorCodes.InputInvalid, oversized.Error!.Code);
            Assert.Empty(
                Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Write_creates_and_overwrites_only_when_the_sha_precondition_matches()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = CreateWorkspace();
        try
        {
            var fileTools = new CoreFileTools(new OpenCoWorkPaths(directory));
            var unsupported = await InvokeFileAsync(
                fileTools.WriteAsync,
                new { path = "unsupported.txt", content = "a\0b" },
                cancellationToken);
            Assert.False(unsupported.IsSuccess);
            Assert.Equal(
                ToolErrorCodes.ContentUnsupported,
                unsupported.Error!.Code);

            var created = await InvokeFileAsync(
                fileTools.WriteAsync,
                new { path = "note.txt", content = "first" },
                cancellationToken);
            Assert.True(created.IsSuccess);
            Assert.Equal(
                "first",
                await File.ReadAllTextAsync(
                    Path.Combine(directory, "note.txt"),
                    cancellationToken));

            var missingPrecondition = await InvokeFileAsync(
                fileTools.WriteAsync,
                new { path = "note.txt", content = "second" },
                cancellationToken);
            Assert.Equal(
                ToolErrorCodes.PreconditionFailed,
                missingPrecondition.Error!.Code);

            var firstSha = Sha256(Encoding.UTF8.GetBytes("first"));
            await File.WriteAllTextAsync(
                Path.Combine(directory, "note.txt"),
                "changed",
                cancellationToken);
            var changedSinceRead = await InvokeFileAsync(
                fileTools.WriteAsync,
                new
                {
                    path = "note.txt",
                    content = "second",
                    expectedSha256 = firstSha,
                },
                cancellationToken);
            Assert.Equal(
                ToolErrorCodes.PreconditionFailed,
                changedSinceRead.Error!.Code);

            var currentSha = Sha256(Encoding.UTF8.GetBytes("changed"));
            var overwritten = await InvokeFileAsync(
                fileTools.WriteAsync,
                new
                {
                    path = "note.txt",
                    content = "second",
                    expectedSha256 = currentSha,
                },
                cancellationToken);
            Assert.True(overwritten.IsSuccess);
            Assert.Equal(
                "second",
                await File.ReadAllTextAsync(
                    Path.Combine(directory, "note.txt"),
                    cancellationToken));
            Assert.Equal(
                Sha256(Encoding.UTF8.GetBytes("second")),
                overwritten.Output!.Value.GetProperty("sha256").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Write_is_atomic_and_cancellation_cleans_temporary_files()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = CreateWorkspace();
        try
        {
            var path = Path.Combine(directory, "atomic.txt");
            var original = new string('a', 256 * 1024);
            var replacement = new string('b', 256 * 1024);
            await File.WriteAllTextAsync(path, original, cancellationToken);
            var fileTools = new CoreFileTools(new OpenCoWorkPaths(directory));
            var observed = new List<string>();
            using var finished = new CancellationTokenSource();
            var reader = Task.Run(
                async () =>
                {
                    try
                    {
                        while (true)
                        {
                            try
                            {
                                await using var stream = new FileStream(
                                    path,
                                    FileMode.Open,
                                    FileAccess.Read,
                                    FileShare.ReadWrite | FileShare.Delete,
                                    4096,
                                    FileOptions.Asynchronous);
                                using var reader = new StreamReader(stream);
                                observed.Add(await reader.ReadToEndAsync(finished.Token));
                            }
                            catch (IOException) when (!finished.IsCancellationRequested)
                            {
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                },
                CancellationToken.None);

            var result = await InvokeFileAsync(
                fileTools.WriteAsync,
                new
                {
                    path = "atomic.txt",
                    content = replacement,
                    expectedSha256 = Sha256(Encoding.UTF8.GetBytes(original)),
                },
                cancellationToken);
            finished.Cancel();
            await reader;

            Assert.True(
                result.IsSuccess,
                $"{result.Error?.Code}: {result.Error?.Message}");
            Assert.All(
                observed,
                value => Assert.True(
                    value == original || value == replacement));
            Assert.Equal(
                replacement,
                await File.ReadAllTextAsync(path, cancellationToken));
            Assert.Empty(Directory.EnumerateFiles(
                directory,
                ".opencowork-write-*.tmp",
                SearchOption.TopDirectoryOnly));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => InvokeFileAsync(
                    fileTools.WriteAsync,
                    new { path = "cancelled.txt", content = "cancelled" },
                    new CancellationToken(canceled: true)).AsTask());
            Assert.False(File.Exists(Path.Combine(directory, "cancelled.txt")));
            Assert.Empty(Directory.EnumerateFiles(
                directory,
                ".opencowork-write-*.tmp",
                SearchOption.TopDirectoryOnly));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateWorkspace()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-core-file-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, ".opencowork"));
        return directory;
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
        if (process.ExitCode != 0)
        {
            throw new IOException(process.StandardError.ReadToEnd());
        }
    }

    private static ValueTask<ToolBindingResult> InvokeAsync(
        ToolRuntime runtime,
        string bindingId,
        object arguments,
        CancellationToken cancellationToken)
    {
        Assert.True(runtime.TryResolveBinding(
            new RuntimeBindingId(bindingId),
            out var binding));
        return binding!.Executor(
            JsonSerializer.SerializeToElement(arguments),
            cancellationToken);
    }

    private static ValueTask<ToolBindingResult> InvokeFileAsync(
        Func<JsonElement, CancellationToken, ValueTask<ToolBindingResult>> operation,
        object arguments,
        CancellationToken cancellationToken) =>
        operation(JsonSerializer.SerializeToElement(arguments), cancellationToken);

    private static ToolInvocationContext ContextualInvocation(
        ToolRuntime runtime,
        object arguments,
        ExecutionWorkspaceDescriptor workspace,
        CoWorkThreadProvenance? provenance)
    {
        var element = JsonSerializer.SerializeToElement(arguments);
        return new ToolInvocationContext(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            CallIndex: 0,
            "call-contextual",
            "file__read",
            element,
            new string('a', 64),
            SensitiveInputDetected: false,
            runtime.BuildSnapshot(AgentMode.Agent, new ToolsConfig()),
            ExecutionWorkspace: workspace,
            CoWorkProvenance: provenance);
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
