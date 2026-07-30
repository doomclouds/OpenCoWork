using System.Diagnostics;
using System.Text.Json;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Capabilities;
using OpenCoWork.Core.Logging;
using OpenCoWork.Core.Tools;
using OpenCoWork.Core.Workspaces;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class SourceControlToolTests
{
    [Fact]
    public async Task Contextual_git_reads_only_the_calling_thread_worktree()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workspace = new GitWorkspace();
        await workspace.InitializeAsync(cancellationToken);
        var worker = Path.Combine(workspace.Root, ".opencowork", "worker");
        await workspace.RunGitAsync(
            ["worktree", "add", "--detach", worker, "HEAD"],
            cancellationToken);
        var (tool, files) = CreateTool(workspace);
        await TrustAsync(tool, files, workspace.Root, cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(worker, "worker-only.txt"),
            "worker",
            cancellationToken);
        var context = SourceControlContext(
            new { },
            new ExecutionWorkspaceDescriptor(
                CoWorkWorkspaceMode.Worktree,
                workspace.Root,
                Path.Combine(worker, "scratchpad"),
                Guid.CreateVersion7(),
                worker,
                await workspace.HeadAsync(cancellationToken)));

        var status = await tool.StatusAsync(context, cancellationToken);
        var escape = await tool.DiffAsync(
            context with
            {
                Arguments = JsonSerializer.SerializeToElement(
                    new { path = "../tracked.txt" }),
            },
            cancellationToken);

        Assert.True(status.IsSuccess, status.Error?.ToString());
        Assert.Contains("worker-only.txt", Output(status));
        Assert.Equal(ToolErrorCodes.PathDenied, escape.Error!.Code);

        File.Delete(Path.Combine(worker, "worker-only.txt"));
        await workspace.RunGitAsync(
            ["worktree", "remove", worker],
            cancellationToken);
    }

    [Fact]
    public async Task Managed_worktree_is_detached_base_fixed_and_retains_dirty_content()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workspace = new GitWorkspace();
        await workspace.InitializeAsync(cancellationToken);
        var (tool, files) = CreateTool(workspace);
        await TrustAsync(tool, files, workspace.Root, cancellationToken);
        var service = new ManagedWorktreeService(
            new OpenCoWorkPaths(workspace.Root),
            tool);
        var baseSha = await workspace.HeadAsync(cancellationToken);
        var agentRunId = Guid.CreateVersion7();

        var created = await service.CreateAsync(
            new ManagedWorktreeCreateRequest(agentRunId, baseSha),
            cancellationToken);
        var recovered = await new ManagedWorktreeService(
                new OpenCoWorkPaths(workspace.Root),
                tool)
            .CreateAsync(
                new ManagedWorktreeCreateRequest(agentRunId, baseSha),
                cancellationToken);
        await File.AppendAllTextAsync(
            Path.Combine(workspace.Root, "tracked.txt"),
            "origin-next\n",
            cancellationToken);
        await workspace.RunGitAsync(["add", "--", "tracked.txt"], cancellationToken);
        await workspace.RunGitAsync(["commit", "-m", "origin next"], cancellationToken);

        Assert.Equal(CoWorkWorktreeStatus.Ready, created.Status);
        Assert.Equal(created, recovered);
        Assert.Equal(
            baseSha,
            (await workspace.RunGitAsync(
                ["rev-parse", "HEAD"],
                cancellationToken,
                created.WorktreeRoot)).Trim());
        Assert.Equal(
            "HEAD",
            (await workspace.RunGitAsync(
                ["rev-parse", "--abbrev-ref", "HEAD"],
                cancellationToken,
                created.WorktreeRoot)).Trim());

        await File.AppendAllTextAsync(
            Path.Combine(created.WorktreeRoot, "tracked.txt"),
            "dirty\n",
            cancellationToken);
        var retained = await service.RemoveAsync(
            created.WorktreeId,
            cancellationToken);
        Assert.Equal(CoWorkWorktreeStatus.RetainedDirty, retained.Status);
        Assert.True(retained.IsDirty);
        Assert.True(Directory.Exists(created.WorktreeRoot));

        await workspace.RunGitAsync(
            ["restore", "--", "tracked.txt"],
            cancellationToken,
            created.WorktreeRoot);
        await File.AppendAllTextAsync(
            Path.Combine(created.WorktreeRoot, "tracked.txt"),
            "committed drift\n",
            cancellationToken);
        await workspace.RunGitAsync(
            ["add", "--", "tracked.txt"],
            cancellationToken,
            created.WorktreeRoot);
        await workspace.RunGitAsync(
            ["commit", "-m", "worktree drift"],
            cancellationToken,
            created.WorktreeRoot);
        var drifted = await service.RemoveAsync(created.WorktreeId, cancellationToken);
        Assert.Equal(CoWorkWorktreeStatus.RetainedDirty, drifted.Status);
        Assert.True(drifted.IsDirty);
        Assert.True(Directory.Exists(created.WorktreeRoot));

        await workspace.RunGitAsync(
            ["reset", "--hard", baseSha],
            cancellationToken,
            created.WorktreeRoot);
        var removed = await service.RemoveAsync(created.WorktreeId, cancellationToken);
        Assert.Equal(CoWorkWorktreeStatus.Removed, removed.Status);
        Assert.False(Directory.Exists(created.WorktreeRoot));

        await File.WriteAllTextAsync(
            Path.Combine(workspace.Root, "dirty-origin.txt"),
            "dirty",
            cancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateAsync(
                    new ManagedWorktreeCreateRequest(Guid.CreateVersion7(), baseSha),
                    cancellationToken)
                .AsTask());
    }

    [Fact]
    public async Task Untrusted_git_requires_trust()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workspace = new GitWorkspace();
        var (tool, _) = CreateTool(workspace);

        var identity = await tool.InspectAsync(cancellationToken);
        var result = await tool.StatusAsync(
            JsonSerializer.SerializeToElement(new { }),
            cancellationToken);

        Assert.True(File.Exists(identity.ExecutablePath));
        Assert.False(result.IsSuccess);
        Assert.Equal(ToolErrorCodes.TrustRequired, result.Error!.Code);
    }

    [Fact]
    public async Task Trusted_git_reads_dirty_repository()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workspace = new GitWorkspace();
        await workspace.InitializeAsync(cancellationToken);
        var (tool, files) = CreateTool(workspace);
        await TrustAsync(tool, files, workspace.Root, cancellationToken);
        await File.AppendAllTextAsync(
            Path.Combine(workspace.Root, "tracked.txt"),
            "dirty\n",
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Root, "untracked.txt"),
            "new\n",
            cancellationToken);

        var status = await tool.StatusAsync(
            JsonSerializer.SerializeToElement(new { }),
            cancellationToken);
        var diff = await tool.DiffAsync(
            JsonSerializer.SerializeToElement(new { path = "tracked.txt" }),
            cancellationToken);
        var log = await tool.LogAsync(
            JsonSerializer.SerializeToElement(new { maxCount = 1 }),
            cancellationToken);
        var show = await tool.ShowAsync(
            JsonSerializer.SerializeToElement(new { revision = "HEAD" }),
            cancellationToken);

        Assert.True(status.IsSuccess);
        Assert.True(diff.IsSuccess, diff.Error?.ToString());
        Assert.True(log.IsSuccess, log.Error?.ToString());
        Assert.True(show.IsSuccess, show.Error?.ToString());
        Assert.Contains("tracked.txt", Output(status));
        Assert.Contains("untracked.txt", Output(status));
        Assert.Contains("+dirty", Output(diff));
        Assert.Contains("initial", Output(log));
        Assert.Contains("tracked.txt", Output(show));
    }

    [Fact]
    public async Task Git_arguments_reject_escape_and_option_injection()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workspace = new GitWorkspace();
        await workspace.InitializeAsync(cancellationToken);
        var (tool, files) = CreateTool(workspace);
        await TrustAsync(tool, files, workspace.Root, cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Root, "--output=owned"),
            "safe\n",
            cancellationToken);

        var optionLikePath = await tool.StatusAsync(
            JsonSerializer.SerializeToElement(new { path = "--output=owned" }),
            cancellationToken);
        var escape = await tool.DiffAsync(
            JsonSerializer.SerializeToElement(new { path = "../outside" }),
            cancellationToken);
        var revision = await tool.ShowAsync(
            JsonSerializer.SerializeToElement(new { revision = "--output=owned" }),
            cancellationToken);

        Assert.True(optionLikePath.IsSuccess, optionLikePath.Error?.ToString());
        Assert.Contains("--output=owned", Output(optionLikePath));
        Assert.Equal(ToolErrorCodes.PathDenied, escape.Error!.Code);
        Assert.Equal(ToolErrorCodes.InputInvalid, revision.Error!.Code);
    }

    [Fact]
    public void Runtime_registers_only_the_four_read_only_git_operations()
    {
        using var workspace = new GitWorkspace();
        var (tool, _) = CreateTool(workspace);
        var runtime = new ToolRuntime(
            new OpenCoWorkPaths(workspace.Root),
            models: null,
            tool);

        var registrations = runtime.Registrations
            .Where(registration =>
                registration.Definition.Name.Namespace == "source_control")
            .OrderBy(registration => registration.Definition.Name.Name)
            .ToArray();

        Assert.Equal(
            ["diff", "log", "show", "status"],
            registrations.Select(registration => registration.Definition.Name.Name));
        Assert.All(registrations, registration =>
        {
            Assert.Equal(
                ToolEffect.WorkspaceRead | ToolEffect.ProcessExecution,
                registration.Definition.Effects);
            Assert.Equal(
                ToolInvocationAudience.Model | ToolInvocationAudience.Host,
                registration.Audience);
        });
    }

    private static string Output(ToolBindingResult result) =>
        result.Output!.Value.GetProperty("stdout").GetString()!;

    private static ToolInvocationContext SourceControlContext(
        object arguments,
        ExecutionWorkspaceDescriptor workspace)
    {
        var element = JsonSerializer.SerializeToElement(arguments);
        return new ToolInvocationContext(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            0,
            "call-source-control",
            "source_control__status",
            element,
            new string('a', 64),
            SensitiveInputDetected: false,
            new ToolRuntime().BuildSnapshot(
                AgentMode.Agent,
                new OpenCoWork.Core.Configuration.ToolsConfig()),
            ExecutionWorkspace: workspace);
    }

    private static (CoreSourceControlTool Tool, CapabilityFileStore Files) CreateTool(
        GitWorkspace workspace)
    {
        var paths = new OpenCoWorkPaths(workspace.Root);
        var persistence = new CapabilityPersistencePaths(paths, workspace.UserProfile);
        var files = new CapabilityFileStore(persistence);
        return (
            new CoreSourceControlTool(paths, files, new SecretRedactor([])),
            files);
    }

    private static async Task TrustAsync(
        CoreSourceControlTool tool,
        CapabilityFileStore files,
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        var identity = await tool.InspectAsync(cancellationToken);
        await files.SaveTrustDecisionsAsync(
            new TrustDecisionsDocument(
                1,
                [
                    new CapabilityTrustDecision(
                        workspaceRoot,
                        CapabilitySourceKind.Workspace,
                        identity.SourceId,
                        identity.Version,
                        identity.Sha256,
                        [CapabilityTrustScope.OutOfProcess],
                        []),
                ]),
            cancellationToken);
    }

    private sealed class GitWorkspace : IDisposable
    {
        public GitWorkspace()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"opencowork-git-{Guid.NewGuid():N}");
            UserProfile = Path.Combine(Root, "user");
            Directory.CreateDirectory(Path.Combine(Root, ".opencowork"));
            Directory.CreateDirectory(UserProfile);
        }

        public string Root { get; }

        public string UserProfile { get; }

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            await RunGitAsync(["init"], cancellationToken);
            await RunGitAsync(["config", "user.name", "OpenCoWork Test"], cancellationToken);
            await RunGitAsync(
                ["config", "user.email", "opencowork@example.invalid"],
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(Root, "tracked.txt"),
                "clean\n",
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(Root, ".gitignore"),
                ".opencowork/\n",
                cancellationToken);
            await RunGitAsync(
                ["add", "--", "tracked.txt", ".gitignore"],
                cancellationToken);
            await RunGitAsync(["commit", "-m", "initial"], cancellationToken);
        }

        public async Task<string> HeadAsync(CancellationToken cancellationToken) =>
            (await RunGitAsync(["rev-parse", "HEAD"], cancellationToken)).Trim();

        public void Dispose()
        {
            foreach (var file in Directory.EnumerateFiles(
                         Root,
                         "*",
                         SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Root, recursive: true);
        }

        public async Task<string> RunGitAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken,
            string? workingDirectory = null)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory ?? Root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo) ??
                throw new InvalidOperationException("Git did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"{await stdout}\n{await stderr}");
            }

            return await stdout;
        }
    }
}
