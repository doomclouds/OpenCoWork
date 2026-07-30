using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.State;
using OpenCoWork.Core.Tools;
using OpenCoWork.Core.Workspaces;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class BackgroundTerminalTests
{
    [Fact]
    public async Task Start_uses_the_calling_thread_execution_root()
    {
        await using var fixture = await TerminalFixture.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var worker = Path.Combine(fixture.Workspace.Root, "worker");
        Directory.CreateDirectory(worker);
        var command = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
        var arguments = OperatingSystem.IsWindows()
            ? new[] { "/d", "/s", "/c", "echo worker>marker.txt" }
            : ["-c", "printf worker > marker.txt"];
        var sessionId = Guid.CreateVersion7();
        var workspace = new ExecutionWorkspaceDescriptor(
            CoWorkWorkspaceMode.Project,
            worker,
            Path.Combine(worker, "scratchpad"),
            WorktreeId: null,
            WorktreeRoot: null,
            BaseCommitSha: null);

        var result = await fixture.Runtime.StartAsync(
            Context(
                fixture.ThreadId,
                new
                {
                    sessionId,
                    command,
                    arguments,
                    maxDurationSeconds = 60,
                },
                workspace),
            cancellationToken);
        var marker = Path.Combine(worker, "marker.txt");
        for (var attempt = 0; attempt < 100 && !File.Exists(marker); attempt++)
        {
            await Task.Delay(10, cancellationToken);
        }

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.True(File.Exists(marker));
        Assert.False(File.Exists(Path.Combine(fixture.Workspace.Root, "marker.txt")));
        await fixture.Runtime.StopAsync(
            Context(fixture.ThreadId, new { sessionId }),
            cancellationToken);
        await fixture.Runtime.ReleaseAsync(
            Context(fixture.ThreadId, new { sessionId }),
            cancellationToken);
    }

    [Fact]
    public async Task Session_is_idempotent_bounded_readable_and_releasable()
    {
        await using var fixture = await TerminalFixture.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var sessionId = Guid.CreateVersion7();
        var (command, arguments) = LongRunningCommand();
        var start = Context(
            fixture.ThreadId,
            new
            {
                sessionId,
                command,
                arguments,
                maxDurationSeconds = 60,
            });

        var first = await fixture.Runtime.StartAsync(start, cancellationToken);
        var retry = await fixture.Runtime.StartAsync(start, cancellationToken);
        var conflict = await fixture.Runtime.StartAsync(
            start with
            {
                Arguments = JsonSerializer.SerializeToElement(new
                {
                    sessionId,
                    command,
                    arguments = arguments.Append("different").ToArray(),
                    maxDurationSeconds = 60,
                }),
            },
            cancellationToken);

        Assert.True(first.IsSuccess, first.Error?.ToString());
        Assert.True(retry.IsSuccess, retry.Error?.ToString());
        Assert.Equal(
            BackgroundTerminalErrorCodes.SessionConflict,
            conflict.Error!.Code);

        ToolBindingResult read;
        do
        {
            await Task.Delay(25, cancellationToken);
            read = await fixture.Runtime.ReadAsync(
                Context(
                    fixture.ThreadId,
                    new { sessionId, offset = 0, maxBytes = 64 * 1024 }),
                cancellationToken);
        }
        while (read.IsSuccess &&
               !read.Output!.Value.GetProperty("content")
                   .GetString()!
                   .Contains("ready", StringComparison.Ordinal));

        Assert.True(read.IsSuccess, read.Error?.ToString());
        Assert.Contains(
            "ready",
            read.Output!.Value.GetProperty("content").GetString());

        var runningRelease = await fixture.Runtime.ReleaseAsync(
            Context(fixture.ThreadId, new { sessionId }),
            cancellationToken);
        var stop = await fixture.Runtime.StopAsync(
            Context(fixture.ThreadId, new { sessionId }),
            cancellationToken);
        var release = await fixture.Runtime.ReleaseAsync(
            Context(fixture.ThreadId, new { sessionId }),
            cancellationToken);

        Assert.Equal(
            ToolErrorCodes.PreconditionFailed,
            runningRelease.Error!.Code);
        Assert.True(stop.IsSuccess, stop.Error?.ToString());
        Assert.True(release.IsSuccess, release.Error?.ToString());
        Assert.Empty(await fixture.ReadStatusesAsync(cancellationToken));
    }

    [Fact]
    public async Task Lagging_reader_gets_reset_required_after_ring_rollover()
    {
        await using var fixture = await TerminalFixture.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var sessionId = Guid.CreateVersion7();
        var (command, arguments) = LargeOutputCommand();
        var start = await fixture.Runtime.StartAsync(
            Context(
                fixture.ThreadId,
                new
                {
                    sessionId,
                    command,
                    arguments,
                    maxDurationSeconds = 60,
                }),
            cancellationToken);
        Assert.True(start.IsSuccess, start.Error?.ToString());

        ToolBindingResult read;
        var attempts = 0;
        do
        {
            await Task.Delay(25, cancellationToken);
            read = await fixture.Runtime.ReadAsync(
                Context(
                    fixture.ThreadId,
                    new { sessionId, offset = 0, maxBytes = 64 * 1024 }),
                cancellationToken);
        }
        while (read.Error?.Code != BackgroundTerminalErrorCodes.ResetRequired &&
               ++attempts < 200);

        Assert.Equal(
            BackgroundTerminalErrorCodes.ResetRequired,
            read.Error!.Code);
        await fixture.Runtime.StopAsync(
            Context(fixture.ThreadId, new { sessionId }),
            cancellationToken);
    }

    [Fact]
    public async Task Start_enforces_the_per_thread_limit_before_spawning()
    {
        await using var fixture = await TerminalFixture.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        for (var index = 0; index < 4; index++)
        {
            await fixture.InsertTerminalAsync(
                Guid.CreateVersion7(),
                "running",
                cancellationToken);
        }

        var (command, arguments) = LongRunningCommand();
        var result = await fixture.Runtime.StartAsync(
            Context(
                fixture.ThreadId,
                new
                {
                    sessionId = Guid.CreateVersion7(),
                    command,
                    arguments,
                    maxDurationSeconds = 60,
                }),
            cancellationToken);

        Assert.Equal(
            BackgroundTerminalErrorCodes.LimitExceeded,
            result.Error!.Code);
    }

    [Fact]
    public async Task Start_rejects_a_working_directory_outside_the_workspace()
    {
        await using var fixture = await TerminalFixture.CreateAsync();
        var (command, arguments) = LongRunningCommand();
        var result = await fixture.Runtime.StartAsync(
            Context(
                fixture.ThreadId,
                new
                {
                    sessionId = Guid.CreateVersion7(),
                    command,
                    arguments,
                    workingDirectory = "../outside",
                    maxDurationSeconds = 60,
                }),
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolErrorCodes.PathDenied, result.Error!.Code);
    }

    [Fact]
    public async Task Startup_marks_residual_running_sessions_lost()
    {
        await using var fixture = await TerminalFixture.CreateAsync(
            initializeTerminal: false);
        var cancellationToken = TestContext.Current.CancellationToken;
        var sessionId = Guid.CreateVersion7();
        await fixture.InsertTerminalAsync(
            sessionId,
            "running",
            cancellationToken);

        await fixture.Runtime.InitializeAsync(cancellationToken);

        Assert.Equal(
            ["lost"],
            await fixture.ReadStatusesAsync(cancellationToken));
    }

    [Fact]
    public void Runtime_registers_the_thread_scoped_terminal_surface()
    {
        using var workspace = new TemporaryWorkspace();
        var paths = new OpenCoWorkPaths(workspace.Root);
        var state = new StateRuntime(paths, TimeSpan.FromSeconds(1));
        var terminal = new BackgroundTerminalRuntime(paths, state);
        var runtime = new ToolRuntime(
            paths,
            models: null,
            sourceControl: null,
            terminal,
            memory: null);

        var registrations = runtime.Registrations
            .Where(item => item.Definition.Name.Namespace == "terminal")
            .OrderBy(item => item.Definition.Name.Name)
            .ToArray();

        Assert.Equal(
            ["list", "read", "release", "start", "stop", "write"],
            registrations.Select(item => item.Definition.Name.Name));
        Assert.All(
            registrations,
            item => Assert.Equal(
                ToolInvocationAudience.Model | ToolInvocationAudience.Host,
                item.Audience));
        Assert.Contains(
            registrations,
            item => item.Definition.Name.Name == "start" &&
                    item.Definition.Effects.HasFlag(ToolEffect.ExternalMutation));
        Assert.Contains(
            registrations,
            item => item.Definition.Name.Name == "write" &&
                    item.Definition.Effects.HasFlag(ToolEffect.ExternalMutation));
    }

    private static ToolInvocationContext Context(
        Guid threadId,
        object arguments,
        ExecutionWorkspaceDescriptor? workspace = null)
    {
        var element = arguments is JsonElement json
            ? json
            : JsonSerializer.SerializeToElement(arguments);
        var snapshot = new ToolRuntime().BuildSnapshot(
            AgentMode.Agent,
            new ToolsConfig());
        return new ToolInvocationContext(
            threadId,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            0,
            $"call-{Guid.NewGuid():N}",
            "terminal",
            element,
            new string('0', 64),
            SensitiveInputDetected: false,
            snapshot,
            ExecutionWorkspace: workspace);
    }

    private static (string Command, string[] Arguments) LongRunningCommand() =>
        OperatingSystem.IsWindows()
            ? (
                "cmd.exe",
                ["/d", "/s", "/c", "echo ready & ping -n 60 127.0.0.1 >nul"])
            : (
                "/bin/sh",
                ["-c", "printf ready; sleep 60"]);

    private static (string Command, string[] Arguments) LargeOutputCommand() =>
        OperatingSystem.IsWindows()
            ? (
                "powershell.exe",
                [
                    "-NoProfile",
                    "-Command",
                    "[Console]::Out.Write(('x' * 1100000)); Start-Sleep -Seconds 60",
                ])
            : (
                "/bin/sh",
                [
                    "-c",
                    "i=0; while [ $i -lt 70000 ]; do printf 1234567890123456; i=$((i+1)); done; sleep 60",
                ]);

    private sealed class TerminalFixture : IAsyncDisposable
    {
        private TerminalFixture(
            TemporaryWorkspace workspace,
            StateRuntime state,
            BackgroundTerminalRuntime runtime,
            Guid threadId)
        {
            Workspace = workspace;
            State = state;
            Runtime = runtime;
            ThreadId = threadId;
        }

        public TemporaryWorkspace Workspace { get; }

        public StateRuntime State { get; }

        public BackgroundTerminalRuntime Runtime { get; }

        public Guid ThreadId { get; }

        public static async Task<TerminalFixture> CreateAsync(
            bool initializeTerminal = true)
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var workspace = new TemporaryWorkspace();
            var paths = new OpenCoWorkPaths(workspace.Root);
            var state = new StateRuntime(paths, TimeSpan.FromSeconds(1));
            await state.InitializeAsync(cancellationToken);
            var threadId = Guid.CreateVersion7();
            await InsertThreadAsync(state, threadId, cancellationToken);
            var runtime = new BackgroundTerminalRuntime(paths, state);
            if (initializeTerminal)
            {
                await runtime.InitializeAsync(cancellationToken);
            }

            return new TerminalFixture(workspace, state, runtime, threadId);
        }

        public async Task InsertTerminalAsync(
            Guid sessionId,
            string status,
            CancellationToken cancellationToken)
        {
            await State.WriteCoordinator.ExecuteAsync(
                async (connection, transaction, token) =>
                {
                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText =
                        """
                        INSERT INTO terminal_sessions (
                            terminal_session_id,
                            thread_id,
                            request_sha256,
                            status,
                            started_utc,
                            updated_utc)
                        VALUES ($session_id, $thread_id, $request_sha256, $status, 1, 1);
                        """;
                    command.Parameters.AddWithValue("$session_id", sessionId.ToString("D"));
                    command.Parameters.AddWithValue("$thread_id", ThreadId.ToString("D"));
                    command.Parameters.AddWithValue("$request_sha256", new string('a', 64));
                    command.Parameters.AddWithValue("$status", status);
                    await command.ExecuteNonQueryAsync(token);
                },
                cancellationToken);
        }

        public async Task<string[]> ReadStatusesAsync(
            CancellationToken cancellationToken)
        {
            await using var connection =
                await State.OpenReadOnlyConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT status FROM terminal_sessions ORDER BY terminal_session_id;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var statuses = new List<string>();
            while (await reader.ReadAsync(cancellationToken))
            {
                statuses.Add(reader.GetString(0));
            }

            return statuses.ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            await Runtime.StopAllAsync(CancellationToken.None);
            SqliteConnection.ClearAllPools();
            Workspace.Dispose();
        }
    }

    private static Task InsertThreadAsync(
        StateRuntime state,
        Guid threadId,
        CancellationToken cancellationToken) =>
        state.WriteCoordinator.ExecuteAsync(
            async (connection, transaction, token) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO threads (
                        thread_id,
                        display_name,
                        display_name_search,
                        status,
                        availability,
                        history_mode,
                        current_sequence,
                        last_applied_sequence,
                        created_utc,
                        updated_utc)
                    VALUES ($thread_id, 'terminal', 'terminal', 'active',
                            'available', 'server', 0, 0, 1, 1);
                    """;
                command.Parameters.AddWithValue("$thread_id", threadId.ToString("D"));
                await command.ExecuteNonQueryAsync(token);
            },
            cancellationToken);

    private sealed class TemporaryWorkspace : IDisposable
    {
        public TemporaryWorkspace()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"opencowork-terminal-{Guid.NewGuid():N}");
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
