using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenCoWork.Abstractions;
using OpenCoWork.App;
using Xunit;

namespace OpenCoWork.IntegrationTests;

public sealed class SessionCrashRecoveryIntegrationTests
{
    private const string ChildFlag = "OPENCOWORK_M2_CRASH_CHILD";
    private const string ChildWorkspace = "OPENCOWORK_M2_CRASH_WORKSPACE";
    private const int CrashExitCode = 73;

    [Fact]
    public async Task Process_interruption_after_turn_flush_recovers_to_terminal_state()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-process-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var startInfo = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add(typeof(SessionCrashRecoveryIntegrationTests).Assembly.Location);
            startInfo.ArgumentList.Add("-noLogo");
            startInfo.ArgumentList.Add("-noColor");
            startInfo.ArgumentList.Add("-method");
            startInfo.ArgumentList.Add(
                $"{typeof(SessionCrashRecoveryIntegrationTests).FullName}." +
                nameof(Child_commits_turn_then_terminates_process));
            startInfo.Environment[ChildFlag] = "1";
            startInfo.Environment[ChildWorkspace] = root;
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start crash child.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (
                timeout.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("Crash child did not exit within 15 seconds.");
            }

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            Assert.Equal(CrashExitCode, process.ExitCode);
            Assert.DoesNotContain(root, output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(root, error, StringComparison.OrdinalIgnoreCase);

            using var host = OpenCoWorkCompositionRoot.Build([], root);
            await host.StartAsync(cancellationToken);
            var service = host.Services.GetRequiredService<ISessionService>();
            var threads = await service.ListThreadsAsync(
                new ListThreadsRequest(
                    Cursor: null,
                    PageSize: 100,
                    IncludeArchived: true),
                cancellationToken);
            var thread = Assert.Single(threads.Value!.Items);
            Assert.Null(thread.ActiveTurnId);
            var history = await service.ReadHistoryAsync(
                new ReadHistoryRequest(
                    thread.ThreadId,
                    AfterSequence: 0,
                    PageSize: 100),
                cancellationToken);
            var terminal = history.Value!.Items[^1];
            Assert.Equal(SessionEventType.TurnFailed, terminal.Type);
            Assert.Equal(
                SessionErrorCodes.RuntimeInterrupted,
                terminal.Payload.Turn!.Error?.Code);
            await host.StopAsync(cancellationToken);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Child_commits_turn_then_terminates_process()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(ChildFlag),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var root = Environment.GetEnvironmentVariable(ChildWorkspace)
            ?? throw new InvalidOperationException("Crash workspace is missing.");
        using var host = OpenCoWorkCompositionRoot.Build(
            [],
            root,
            services => services.AddSingleton<ISessionExecutor, CrashExecutor>());
        await host.StartAsync(TestContext.Current.CancellationToken);
        var service = host.Services.GetRequiredService<ISessionService>();
        var thread = (await service.CreateThreadAsync(
            new CreateThreadRequest(
                Guid.CreateVersion7(),
                ExpectedSequence: 0,
                DisplayName: "crash recovery"),
            TestContext.Current.CancellationToken)).Value!;
        await service.EnqueueInputAsync(
            new EnqueueInputRequest(
                thread.ThreadId,
                Guid.CreateVersion7(),
                thread.CurrentSequence,
                "interrupt after turn flush"),
            TestContext.Current.CancellationToken);
        await Task.Delay(Timeout.InfiniteTimeSpan, TestContext.Current.CancellationToken);
    }

    private sealed class CrashExecutor : ISessionExecutor
    {
        public ValueTask ExecuteAsync(
            AgentSession context,
            ISessionExecutionSink sink,
            CancellationToken cancellationToken)
        {
            Environment.Exit(CrashExitCode);
            return ValueTask.CompletedTask;
        }
    }
}
