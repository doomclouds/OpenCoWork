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
                name => name == "TOOL_RUNTIME_KEY" ? "test-secret" : null);
            var tools = new ToolRuntime(paths);
            var redactor = new SecretRedactor(["test-secret"]);
            var client = new ToolLoopClient();
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
                _ => client,
                toolPipeline: new ToolInvocationPipeline(
                    tools,
                    redactor),
                redactor: redactor);
            using var host = OpenCoWorkCompositionRoot.Build(
                [],
                root,
                services => services.AddSingleton<ISessionExecutor>(executor));
            await host.StartAsync(cancellationToken);
            var service = host.Services.GetRequiredService<ISessionService>();
            var thread = (await service.CreateThreadAsync(
                new CreateThreadRequest(
                    Guid.CreateVersion7(),
                    ExpectedSequence: 0,
                    DisplayName: "tool loop",
                    ProviderId: "token-plan",
                    ModelId: "qwen3.8-max-preview"),
                cancellationToken)).Value!;
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
                [ChatCompletionMessageRole.Assistant, ChatCompletionMessageRole.Tool],
                client.Requests[1].Messages.TakeLast(2)
                    .Select(message => message.Role));
            Assert.Contains(
                "visible.txt",
                client.Requests[1].Messages[^1].Content,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "\"status\":\"registered\"",
                client.Requests[1].Messages[^1].Content,
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

    private static ModelsConfig Models() =>
        new()
        {
            Providers = new Dictionary<string, ProviderConfig>(StringComparer.Ordinal)
            {
                ["token-plan"] = new()
                {
                    BaseUrl = "https://example.test/v1",
                    ApiKey = new ProviderApiKeyConfig
                    {
                        Environment = "TOOL_RUNTIME_KEY",
                    },
                    Models = new Dictionary<string, ModelConfig>(StringComparer.Ordinal)
                    {
                        ["qwen3.8-max-preview"] = new()
                        {
                            TokenizerProfileId = "qwen-o200k",
                            TokenizerProfileVersion = "1",
                            ContextWindowTokens = 983_616,
                            MaxOutputTokens = 131_072,
                        },
                    },
                },
            },
        };

    private sealed class ToolLoopClient : IChatCompletionClient
    {
        public List<ChatCompletionRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ChatCompletionEvent> StreamAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (Requests.Count == 1)
            {
                yield return new ChatCompletionToolCallCompletedEvent(
                    0,
                    "call-1",
                    "file__list",
                    """{"path":"."}""");
                yield return new ChatCompletionCompletedEvent(
                    ChatCompletionFinishReason.ToolCall);
                yield break;
            }

            yield return new ChatCompletionContentDeltaEvent("done");
            yield return new ChatCompletionCompletedEvent(
                ChatCompletionFinishReason.Stop);
        }
    }
}
