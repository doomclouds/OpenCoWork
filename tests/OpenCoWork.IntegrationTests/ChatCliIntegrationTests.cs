using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using OpenCoWork.Abstractions;
using OpenCoWork.App;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Workspaces;
using Xunit;

namespace OpenCoWork.IntegrationTests;

public sealed class ChatCliIntegrationTests
{
    [Fact]
    public async Task Redirected_chat_runs_multiple_turns_and_resumes_the_exact_thread()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-chat-{Guid.NewGuid():N}");
        var userProfile = Path.Combine(root, "user");
        var secretName = $"OPENCOWORK_TEST_KEY_{Guid.NewGuid():N}";
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(userProfile);
        Environment.SetEnvironmentVariable(secretName, "chat-test-secret");
        try
        {
            var paths = new OpenCoWorkPaths(root);
            await WorkspaceInitializer.InitializeAsync(
                paths,
                TimeSpan.FromSeconds(2),
                cancellationToken);
            await File.WriteAllTextAsync(
                paths.ConfigPath,
                Config(secretName),
                cancellationToken);
            var output = new StringWriter();
            var error = new StringWriter();
            var executor = new EchoExecutor();

            var firstExitCode = await OpenCoWorkCli.RunAsync(
                ["chat", "--workspace", root],
                new StringReader("one\ntwo\n"),
                output,
                error,
                root,
                userProfile,
                isInteractive: false,
                services => services.AddSingleton<ISessionExecutor>(executor),
                cancellationToken);

            Assert.Equal(0, firstExitCode);
            Assert.Equal("reply:one\nreply:two\n", Normalize(output.ToString()));
            var threadId = Guid.Parse(
                Normalize(error.ToString())
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Single(line => line.StartsWith("thread ", StringComparison.Ordinal))
                    ["thread ".Length..]);

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();
            var resumedExitCode = await OpenCoWorkCli.RunAsync(
                [
                    "chat",
                    "--workspace",
                    root,
                    "--thread",
                    threadId.ToString("D"),
                    "--provider",
                    "fake",
                    "--model",
                    "glm-5.2",
                ],
                new StringReader("three\n"),
                output,
                error,
                root,
                userProfile,
                isInteractive: false,
                services => services.AddSingleton<ISessionExecutor>(executor),
                cancellationToken);

            Assert.True(resumedExitCode == 0, error.ToString());
            Assert.Equal("reply:three\n", Normalize(output.ToString()));
            Assert.Contains(
                $"thread {threadId:D}",
                Normalize(error.ToString()),
                StringComparison.Ordinal);
            Assert.Contains(
                "provider fake",
                error.ToString(),
                StringComparison.Ordinal);
            Assert.Contains(
                "model glm-5.2",
                error.ToString(),
                StringComparison.Ordinal);
            Assert.Equal(
                [
                    (AgentMode.Agent, "qwen3.8-max-preview"),
                    (AgentMode.Agent, "qwen3.8-max-preview"),
                    (AgentMode.Agent, "glm-5.2"),
                ],
                executor.Selections);
            Assert.DoesNotContain(
                "chat-test-secret",
                output.ToString() + error.ToString(),
                StringComparison.Ordinal);

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();
            var failedExitCode = await OpenCoWorkCli.RunAsync(
                ["chat", "--workspace", root, "--thread", threadId.ToString("D")],
                new StringReader("fail\n"),
                output,
                error,
                root,
                userProfile,
                isInteractive: false,
                services => services.AddSingleton<ISessionExecutor>(
                    new ThrowingExecutor("apiKey=chat-test-secret")),
                cancellationToken);

            Assert.Equal(1, failedExitCode);
            Assert.DoesNotContain(
                "chat-test-secret",
                output.ToString() + error.ToString(),
                StringComparison.Ordinal);
            Assert.Contains(
                "[REDACTED]",
                error.ToString(),
                StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secretName, null);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Interactive_chat_handles_modes_escaping_output_and_invalid_redirected_input()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-chat-interactive-{Guid.NewGuid():N}");
        var userProfile = Path.Combine(root, "user");
        var secretName = $"OPENCOWORK_TEST_KEY_{Guid.NewGuid():N}";
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(userProfile);
        Environment.SetEnvironmentVariable(secretName, "interactive-test-secret");
        try
        {
            var paths = new OpenCoWorkPaths(root);
            await WorkspaceInitializer.InitializeAsync(
                paths,
                TimeSpan.FromSeconds(2),
                cancellationToken);
            await File.WriteAllTextAsync(
                paths.ConfigPath,
                Config(secretName),
                cancellationToken);
            var output = new StringWriter();
            var error = new StringWriter();
            var executor = new EchoExecutor(includeReasoning: true);

            var exitCode = await OpenCoWorkCli.RunAsync(
                ["chat", "--workspace", root],
                new StringReader("/mode plan\n//exit\n/mode agent\n/exit\n"),
                output,
                error,
                root,
                userProfile,
                isInteractive: true,
                services => services.AddSingleton<ISessionExecutor>(executor),
                cancellationToken);

            Assert.Equal(0, exitCode);
            Assert.Equal("reply:/exit\n", Normalize(output.ToString()));
            Assert.DoesNotContain(
                "thought:/exit",
                output.ToString(),
                StringComparison.Ordinal);
            Assert.Contains(
                "thought:/exit",
                error.ToString(),
                StringComparison.Ordinal);
            Assert.Contains("mode plan", error.ToString(), StringComparison.Ordinal);
            Assert.Contains("mode agent", error.ToString(), StringComparison.Ordinal);
            Assert.Equal([(AgentMode.Plan, "qwen3.8-max-preview")], executor.Selections);

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();
            var oversizedExitCode = await OpenCoWorkCli.RunAsync(
                ["chat", "--workspace", root],
                new StringReader(new string('x', (256 * 1024) + 1) + "\n"),
                output,
                error,
                root,
                userProfile,
                isInteractive: false,
                services => services.AddSingleton<ISessionExecutor>(executor),
                cancellationToken);

            Assert.Equal(1, oversizedExitCode);
            Assert.Empty(output.ToString());
            Assert.Contains(
                AgentErrorCodes.ContextInputTooLarge,
                error.ToString(),
                StringComparison.Ordinal);
            Assert.Single(executor.Selections);

            foreach (var invalidInput in new[] { "bad\0input\n", "\ud800\n" })
            {
                output.GetStringBuilder().Clear();
                error.GetStringBuilder().Clear();
                var invalidExitCode = await OpenCoWorkCli.RunAsync(
                    ["chat", "--workspace", root],
                    new StringReader(invalidInput),
                    output,
                    error,
                    root,
                    userProfile,
                    isInteractive: false,
                    services => services.AddSingleton<ISessionExecutor>(executor),
                    cancellationToken);

                Assert.Equal(1, invalidExitCode);
                Assert.Empty(output.ToString());
                Assert.Contains(
                    AgentErrorCodes.ContextInputInvalid,
                    error.ToString(),
                    StringComparison.Ordinal);
                Assert.Single(executor.Selections);
            }

            error.GetStringBuilder().Clear();
            var pairExitCode = await OpenCoWorkCli.RunAsync(
                ["chat", "--workspace", root, "--provider", "fake"],
                new StringReader(string.Empty),
                output,
                error,
                root,
                userProfile,
                isInteractive: false,
                services => services.AddSingleton<ISessionExecutor>(executor),
                cancellationToken);

            Assert.Equal(2, pairExitCode);
            Assert.Contains(
                "--provider and --model",
                error.ToString(),
                StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secretName, null);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Redirected_cancellation_cancels_the_current_turn()
    {
        var testCancellation = TestContext.Current.CancellationToken;
        var root = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-chat-cancel-{Guid.NewGuid():N}");
        var userProfile = Path.Combine(root, "user");
        var secretName = $"OPENCOWORK_TEST_KEY_{Guid.NewGuid():N}";
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(userProfile);
        Environment.SetEnvironmentVariable(secretName, "cancel-test-secret");
        using var cancellation =
            CancellationTokenSource.CreateLinkedTokenSource(testCancellation);
        try
        {
            var paths = new OpenCoWorkPaths(root);
            await WorkspaceInitializer.InitializeAsync(
                paths,
                TimeSpan.FromSeconds(2),
                testCancellation);
            await File.WriteAllTextAsync(
                paths.ConfigPath,
                Config(secretName),
                testCancellation);
            var output = new StringWriter();
            var error = new StringWriter();
            var executor = new BlockingExecutor();
            var run = OpenCoWorkCli.RunAsync(
                ["chat", "--workspace", root],
                new StringReader("wait\n"),
                output,
                error,
                root,
                userProfile,
                isInteractive: false,
                services => services.AddSingleton<ISessionExecutor>(executor),
                cancellation.Token);

            await executor.Started.Task.WaitAsync(testCancellation);
            cancellation.Cancel();
            var exitCode = await run.WaitAsync(
                TimeSpan.FromSeconds(5),
                testCancellation);

            Assert.Equal(1, exitCode);
            Assert.Empty(output.ToString());
            Assert.Contains("cancelled", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            cancellation.Cancel();
            Environment.SetEnvironmentVariable(secretName, null);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static string Config(string secretName) =>
        $$"""
        {
          "models": {
            "defaultProvider": "fake",
            "defaultModel": "qwen3.8-max-preview",
            "providers": {
              "fake": {
                "baseUrl": "https://example.test/v1",
                "apiKey": {
                  "environment": "{{secretName}}"
                },
                "models": {
                  "qwen3.8-max-preview": {
                    "tokenizerProfileId": "qwen-o200k",
                    "tokenizerProfileVersion": "1",
                    "contextWindowTokens": 983616,
                    "maxOutputTokens": 131072
                  },
                  "glm-5.2": {
                    "tokenizerProfileId": "glm-5.2",
                    "tokenizerProfileVersion": "1",
                    "contextWindowTokens": 1048576,
                    "maxOutputTokens": 131072
                  }
                }
              }
            }
          }
        }
        """;

    private static string Normalize(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal);

    private sealed class EchoExecutor(bool includeReasoning = false) : ISessionExecutor
    {
        public ConcurrentQueue<(AgentMode Mode, string Model)> Selections { get; } = [];

        public async ValueTask ExecuteAsync(
            AgentSession context,
            ISessionExecutionSink sink,
            CancellationToken cancellationToken)
        {
            Selections.Enqueue(
                (context.Turn.EffectiveAgentMode, context.Thread.ModelId!));
            var user = context.ModelHistory
                .Where(item =>
                    item.TurnId == context.Turn.TurnId &&
                    item.Type == SessionItemType.UserMessage)
                .Select(item => ((TextItemContent)item.Content).Text)
                .Single();
            if (includeReasoning)
            {
                var reasoningId = Guid.CreateVersion7();
                await sink.EmitAsync(
                    new StartItemIntent(
                        reasoningId,
                        SessionItemType.Reasoning,
                        new TextItemContent(string.Empty)),
                    cancellationToken);
                await sink.EmitAsync(
                    new AppendItemDeltaIntent(
                        reasoningId,
                        "thought:" + user,
                        Flush: true),
                    cancellationToken);
                await sink.EmitAsync(
                    new CompleteItemIntent(reasoningId),
                    cancellationToken);
            }

            var itemId = Guid.CreateVersion7();
            await sink.EmitAsync(
                new StartItemIntent(
                    itemId,
                    SessionItemType.AgentMessage,
                    new TextItemContent(string.Empty)),
                cancellationToken);
            await sink.EmitAsync(
                new AppendItemDeltaIntent(itemId, "reply:" + user, Flush: true),
                cancellationToken);
            await sink.EmitAsync(
                new CompleteItemIntent(itemId),
                cancellationToken);
            await sink.EmitAsync(
                new CompleteTurnIntent(),
                cancellationToken);
        }
    }

    private sealed class BlockingExecutor : ISessionExecutor
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask ExecuteAsync(
            AgentSession context,
            ISessionExecutionSink sink,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class ThrowingExecutor(string message) : ISessionExecutor
    {
        public ValueTask ExecuteAsync(
            AgentSession context,
            ISessionExecutionSink sink,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(message);
    }
}
