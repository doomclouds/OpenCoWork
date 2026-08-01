using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Logging;
using OpenCoWork.Core.Sessions;
using OpenCoWork.Core.Tools;
using OpenCoWork.Core.Workspaces;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed partial class CoreToolTests
{
    [Fact]
    public async Task Shell_uses_the_calling_thread_execution_root()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
        {
            return;
        }

        var origin = CreateWorkspace();
        var worker = CreateWorkspace();
        try
        {
            var tool = new CoreShellTool(new OpenCoWorkPaths(origin), []);
            var workspace = new ExecutionWorkspaceDescriptor(
                CoWorkWorkspaceMode.Project,
                worker,
                Path.Combine(worker, "scratchpad"),
                WorktreeId: null,
                WorktreeRoot: null,
                BaseCommitSha: null);
            var command = OperatingSystem.IsWindows()
                ? "[Console]::Out.Write((Get-Location).Path)"
                : "pwd";
            var element = JsonSerializer.SerializeToElement(new { command });
            var context = new ToolInvocationContext(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                0,
                "call-shell-root",
                "shell__run",
                element,
                new string('a', 64),
                SensitiveInputDetected: false,
                new ToolRuntime().BuildSnapshot(AgentMode.Agent, new ToolsConfig()),
                ExecutionWorkspace: workspace);

            var result = await tool.RunAsync(
                context,
                TestContext.Current.CancellationToken);

            Assert.True(result.IsSuccess, result.Error?.ToString());
            Assert.Equal(
                WorkspacePathGuard.ResolveContained(
                    worker,
                    Path.Combine(worker, ".anchor"),
                    ".").PhysicalPath,
                Path.GetFullPath(
                    result.Output!.Value.GetProperty("stdout").GetString()!.Trim()));
        }
        finally
        {
            Directory.Delete(origin, recursive: true);
            Directory.Delete(worker, recursive: true);
        }
    }

    [Fact]
    public async Task Shell_records_process_result_and_removes_sensitive_environment()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
        {
            return;
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = CreateWorkspace();
        const string credentialName = "OPENCOWORK_PROVIDER_VALUE";
        const string visibleName = "OPENCOWORK_VISIBLE_VALUE";
        var previousCredential = Environment.GetEnvironmentVariable(credentialName);
        var previousVisible = Environment.GetEnvironmentVariable(visibleName);
        Environment.SetEnvironmentVariable(credentialName, "credential-secret");
        Environment.SetEnvironmentVariable(visibleName, "visible");
        try
        {
            var command = OperatingSystem.IsWindows()
                ? """
                  $credential = if ($env:OPENCOWORK_PROVIDER_VALUE) { $env:OPENCOWORK_PROVIDER_VALUE } else { 'missing' }
                  [Console]::Out.Write("$credential|$env:OPENCOWORK_VISIBLE_VALUE")
                  [Console]::Error.Write("problem")
                  exit 7
                  """
                : """
                  printf '%s|%s' "${OPENCOWORK_PROVIDER_VALUE-missing}" "$OPENCOWORK_VISIBLE_VALUE"
                  printf 'problem' >&2
                  exit 7
                  """;
            var result = await new CoreShellTool(
                    new OpenCoWorkPaths(directory),
                    [credentialName])
                .RunAsync(
                    JsonSerializer.SerializeToElement(new { command }),
                    cancellationToken);

            Assert.True(result.IsSuccess);
            var output = result.Output!.Value;
            Assert.Equal(7, output.GetProperty("exitCode").GetInt32());
            Assert.Equal(
                "missing|visible",
                output.GetProperty("stdout").GetString());
            Assert.Equal("problem", output.GetProperty("stderr").GetString());
            Assert.True(output.GetProperty("durationMilliseconds").GetInt64() >= 0);
            Assert.DoesNotContain(
                "credential-secret",
                output.GetRawText(),
                StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                credentialName,
                previousCredential);
            Environment.SetEnvironmentVariable(visibleName, previousVisible);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Shell_output_limit_kills_the_process()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = CreateWorkspace();
        try
        {
            var result = await new CoreShellTool(
                    new OpenCoWorkPaths(directory),
                    [])
                .RunAsync(
                    JsonSerializer.SerializeToElement(new
                    {
                        command = OperatingSystem.IsWindows()
                            ? "while ($true) { [Console]::Out.Write('0123456789') }"
                            : "while true; do printf '0123456789'; done",
                    }),
                    TestContext.Current.CancellationToken);

            Assert.False(result.IsSuccess);
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
    public async Task Shell_cancellation_kills_the_process_tree()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = CreateWorkspace();
        using var cancellation = new CancellationTokenSource();
        try
        {
            var tool = new CoreShellTool(new OpenCoWorkPaths(directory), []);
            var execution = tool.RunAsync(
                    JsonSerializer.SerializeToElement(new
                    {
                        command = OperatingSystem.IsWindows()
                            ? """
                              $child = Start-Process -FilePath $env:ComSpec -ArgumentList '/d','/c','ping -n 31 127.0.0.1 >nul' -WindowStyle Hidden -PassThru
                              [IO.File]::WriteAllText((Join-Path (Get-Location) 'child.pid'), $child.Id.ToString())
                              $child.WaitForExit()
                              """
                            : "sleep 30 & child=$!; printf '%s' \"$child\" > child.pid; wait",
                    }),
                    cancellation.Token)
                .AsTask();
            var pidPath = Path.Combine(directory, "child.pid");
            for (var attempt = 0;
                 attempt < 100 && !File.Exists(pidPath);
                 attempt++)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            Assert.True(File.Exists(pidPath));
            var childId = int.Parse(
                await File.ReadAllTextAsync(
                    pidPath,
                    TestContext.Current.CancellationToken));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => execution);

            for (var attempt = 0;
                 attempt < 100 && ProcessExists(childId);
                 attempt++)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            Assert.False(ProcessExists(childId));
        }
        finally
        {
            cancellation.Cancel();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Shell_approval_prompt_contains_the_complete_command()
    {
        var directory = CreateWorkspace();
        try
        {
            var runtime = new ToolRuntime(new OpenCoWorkPaths(directory));
            var snapshot = runtime.BuildSnapshot(
                AgentMode.Agent,
                new ToolsConfig());
            var registration = Assert.Single(
                snapshot.Registrations,
                item => item.Definition.Name is
                { Namespace: "shell", Name: "run" });
            const string command = "printf 'approval-bound-command'";
            var arguments = JsonSerializer.SerializeToElement(new { command });
            var context = new ToolInvocationContext(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                CallIndex: 0,
                "call-shell",
                snapshot.CanonicalToProviderNames["shell.run"],
                arguments,
                Sha256(ThreadJournal.Canonicalize(arguments)),
                SensitiveInputDetected: false,
                snapshot,
                ApprovalCheckpoint: new SessionExecutionCheckpoint(
                    "test",
                    1,
                    "{}",
                    new string('a', 64)));
            var sink = new CapturingSink();

            await Assert.ThrowsAsync<ToolInvocationSuspendedException>(
                () => new ToolInvocationPipeline(
                        runtime,
                        new SecretRedactor([]))
                    .InvokeAsync(
                        context,
                        sink,
                        TestContext.Current.CancellationToken)
                    .AsTask());

            var waiting = Assert.Single(
                sink.Intents.OfType<WaitForInteractionIntent>());
            var approval = Assert.IsType<ToolApprovalRequestContent>(
                waiting.Request);
            Assert.Equal(registration.Definition.Id, approval.ToolDefinitionId);
            Assert.Contains(command, approval.Prompt, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static bool ProcessExists(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(
                processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private sealed class CapturingSink : ISessionExecutionSink
    {
        public List<SessionExecutionIntent> Intents { get; } = [];

        public ValueTask EmitAsync(
            SessionExecutionIntent intent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Intents.Add(intent);
            return ValueTask.CompletedTask;
        }
    }

}
