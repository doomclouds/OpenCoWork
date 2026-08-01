using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenCoWork.Abstractions;
using OpenCoWork.App;
using OpenCoWork.Core.Agents;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Logging;
using OpenCoWork.Core.Tools;
using OpenCoWork.Core.Workspaces;
using Xunit;

namespace OpenCoWork.IntegrationTests;

public sealed class ToolRuntimeIntegrationTests
{
    [Fact]
    public async Task Chat_cli_resolves_shell_approval_and_resumes_the_turn()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
        {
            return;
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-tool-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var paths = new OpenCoWorkPaths(root);
            var models = Models();
            var credentials = FrozenProviderCredentials.Capture(
                models,
                name => name == "DEEPSEEK_API_KEY" ? "test-secret" : null);
            var tools = new ToolRuntime(paths);
            var redactor = new SecretRedactor(["test-secret"]);
            var client = new ShellApprovalClient();
            var executor = new AgentRuntimeExecutor(
                new AgentFactory(
                    new ProviderRegistry(
                        models,
                        credentials,
                        AppContext.BaseDirectory,
                        root),
                    paths,
                    tools),
                paths,
                _ => client.StreamAsync,
                toolPipeline: new ToolInvocationPipeline(tools, redactor),
                redactor: redactor);
            using var host = OpenCoWorkCompositionRoot.Build(
                [],
                root,
                services =>
                {
                    services.AddSingleton(models);
                    services.AddSingleton(credentials);
                    services.AddSingleton(redactor);
                    services.AddSingleton<ISessionExecutor>(executor);
                });
            await host.StartAsync(cancellationToken);
            var output = new StringWriter();
            var error = new StringWriter();

            var exitCode = await ChatCommandRunner.RunAsync(
                host.Services,
                requestedThreadId: null,
                providerId: ModelsConfig.ProviderId,
                modelId: ModelsConfig.FlashModelId,
                new StringReader("run it\nyes\n/exit\n"),
                output,
                error,
                isInteractive: true,
                cancellationToken);

            Assert.Equal(0, exitCode);
            Assert.Equal("done" + Environment.NewLine, output.ToString());
            Assert.Contains(
                ShellApprovalClient.Command,
                error.ToString(),
                StringComparison.Ordinal);
            Assert.Contains(
                "approve [y/N]> ",
                error.ToString(),
                StringComparison.Ordinal);
            Assert.Equal(2, client.Requests.Count);
            Assert.Contains(
                "cli-approved",
                client.Requests[1].Input[^1].GetProperty("output").GetString()!,
                StringComparison.Ordinal);
            Assert.Contains(
                "\"exitCode\":0",
                client.Requests[1].Input[^1].GetProperty("output").GetString()!,
                StringComparison.Ordinal);
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
    public async Task Provider_tool_loop_commits_results_before_the_next_round()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-tool-runtime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "visible.txt"),
                "visible",
                cancellationToken);
            var paths = new OpenCoWorkPaths(root);
            var models = Models();
            var credentials = FrozenProviderCredentials.Capture(
                models,
                name => name == "DEEPSEEK_API_KEY" ? "test-secret" : null);
            var providers = new ProviderRegistry(
                models,
                credentials,
                AppContext.BaseDirectory,
                root);
            var tools = new ToolRuntime(paths);
            var redactor = new SecretRedactor(["test-secret"]);
            var client = new ToolLoopClient();
            var executor = new AgentRuntimeExecutor(
                new AgentFactory(
                    providers,
                    paths,
                    tools),
                paths,
                _ => client.StreamAsync,
                toolPipeline: new ToolInvocationPipeline(
                    tools,
                    redactor),
                redactor: redactor);
            using var host = OpenCoWorkCompositionRoot.Build(
                [],
                root,
                services =>
                {
                    services.AddSingleton(providers);
                    services.AddSingleton<ISessionExecutor>(executor);
                });
            await host.StartAsync(cancellationToken);
            var service = host.Services.GetRequiredService<ISessionService>();
            var created = await service.CreateThreadAsync(
                new CreateThreadRequest(
                    Guid.CreateVersion7(),
                    ExpectedSequence: 0,
                    DisplayName: "tool loop",
                    ProviderId: ModelsConfig.ProviderId,
                    ModelId: ModelsConfig.FlashModelId),
                cancellationToken);
            Assert.Null(created.Error);
            var thread = Assert.IsType<ThreadSnapshot>(created.Value);
            await service.EnqueueInputAsync(
                new EnqueueInputRequest(
                    thread.ThreadId,
                    Guid.CreateVersion7(),
                    thread.CurrentSequence,
                    "list files"),
                cancellationToken);
            thread = (await service.GetThreadAsync(
                thread.ThreadId,
                cancellationToken)).Value!;
            for (var attempt = 0;
                 attempt < 100 && thread.ActiveTurnId is not null;
                 attempt++)
            {
                await Task.Delay(10, cancellationToken);
                thread = (await service.GetThreadAsync(
                    thread.ThreadId,
                    cancellationToken)).Value!;
            }

            Assert.Null(thread.ActiveTurnId);
            Assert.Equal(2, client.Requests.Count);
            Assert.Equal(
                ["function_call", "function_call_output"],
                client.Requests[1].Input.TakeLast(2)
                    .Select(item => item.GetProperty("type").GetString()));
            Assert.Contains(
                "visible.txt",
                client.Requests[1].Input[^1].GetProperty("output").GetString()!,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "\"status\":\"registered\"",
                client.Requests[1].Input[^1].GetProperty("output").GetString()!,
                StringComparison.Ordinal);
            var history = (await service.ReadHistoryAsync(
                new ReadHistoryRequest(
                    thread.ThreadId,
                    AfterSequence: 0,
                    PageSize: 100),
                cancellationToken)).Value!.Items;
            Assert.Contains(
                history,
                item => item.Type == SessionEventType.ToolCallRecorded);
            Assert.Contains(
                history,
                item => item.Type == SessionEventType.ToolInvocationTerminal);
            Assert.Equal(SessionEventType.TurnCompleted, history[^1].Type);
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

    private static ModelsConfig Models() => new();

    private static DeepSeekTextDeltaEvent Output(string value) =>
        new("0:message-1", DeepSeekTextKind.Output, value);

    private static DeepSeekTerminalEvent Completed() =>
        new(DeepSeekTerminalStatus.Completed, Usage: null);

    private static DeepSeekFunctionCallCompletedEvent Function(
        string callId,
        string name,
        string arguments) =>
        new($"1:{callId}", callId, name, arguments);

    private sealed class ToolLoopClient
    {
        public List<DeepSeekResponsesRequest> Requests { get; } = [];

        public async IAsyncEnumerable<DeepSeekResponseEvent> StreamAsync(
            DeepSeekResponsesRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (Requests.Count == 1)
            {
                yield return Function(
                    "call-1",
                    "file__list",
                    """{"path":"."}""");
                yield return Completed();
                yield break;
            }

            yield return Output("done");
            yield return Completed();
        }
    }

    private sealed class ShellApprovalClient
    {
        public static string Command =>
            OperatingSystem.IsWindows()
                ? "[Console]::Out.Write('cli-approved')"
                : "printf cli-approved";

        public List<DeepSeekResponsesRequest> Requests { get; } = [];

        public async IAsyncEnumerable<DeepSeekResponseEvent> StreamAsync(
            DeepSeekResponsesRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (Requests.Count == 1)
            {
                yield return Function(
                    "shell-call-1",
                    "shell__run",
                    $$"""{"command":"{{Command}}"}""");
                yield return Completed();
                yield break;
            }

            yield return Output("done");
            yield return Completed();
        }
    }
}
