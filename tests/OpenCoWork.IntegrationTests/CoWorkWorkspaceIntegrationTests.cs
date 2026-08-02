using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenCoWork.Abstractions;
using OpenCoWork.App;
using Xunit;

namespace OpenCoWork.IntegrationTests;

public sealed class CoWorkWorkspaceIntegrationTests
{
    [Fact]
    public async Task Production_runtime_binds_threads_and_managed_worktrees_to_workspace()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-cowork-workspace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await RunGitAsync(root, ["init"], cancellationToken);
            await RunGitAsync(
                root,
                ["config", "user.name", "OpenCoWork Test"],
                cancellationToken);
            await RunGitAsync(
                root,
                ["config", "user.email", "opencowork@example.invalid"],
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(root, ".gitignore"),
                ".opencowork/\n",
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(root, "tracked.txt"),
                "clean\n",
                cancellationToken);
            await RunGitAsync(
                root,
                ["add", "--", ".gitignore", "tracked.txt"],
                cancellationToken);
            await RunGitAsync(root, ["commit", "-m", "initial"], cancellationToken);
            using var host = OpenCoWorkCompositionRoot.Build([], root);
            await host.StartAsync(cancellationToken);
            var session = host.Services.GetRequiredService<ISessionService>();
            var thread = Assert.IsType<ThreadSnapshot>(
                (await session.CreateThreadAsync(
                    new CreateThreadRequest(
                        Guid.CreateVersion7(),
                        ExpectedSequence: 0,
                        DisplayName: "workspace"),
                    cancellationToken)).Value);
            var worktrees =
                host.Services.GetRequiredService<IManagedWorktreeService>();

            Assert.Equal(root, thread.ExecutionWorkspace!.WorkspaceRoot);
            Assert.NotNull(worktrees);
            await host.StopAsync(cancellationToken);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                foreach (var file in Directory.EnumerateFiles(
                             root,
                             "*",
                             SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task<string> RunGitAsync(
        string root,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "git.exe" : "git",
            WorkingDirectory = root,
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
            throw new InvalidOperationException(await stderr);
        }

        return await stdout;
    }
}
