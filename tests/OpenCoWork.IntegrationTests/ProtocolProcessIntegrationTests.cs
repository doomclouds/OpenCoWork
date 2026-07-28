using System.Diagnostics;
using System.Text.Json;
using OpenCoWork.App;
using OpenCoWork.Core.Workspaces;
using Xunit;

namespace OpenCoWork.IntegrationTests;

public sealed class ProtocolProcessIntegrationTests
{
    private const string TestConfig =
        """
        {
          "models": {
            "defaultProvider": "test",
            "defaultModel": "qwen3.8-max-preview",
            "providers": {
              "test": {
                "baseUrl": "https://example.test/v1",
                "apiKey": { "environment": "OPENCOWORK_TEST_API_KEY" },
                "models": {
                  "qwen3.8-max-preview": {
                    "tokenizerProfileId": "qwen-o200k",
                    "tokenizerProfileVersion": "1",
                    "contextWindowTokens": 983616,
                    "maxOutputTokens": 131072
                  }
                }
              }
            }
          }
        }
        """;

    [Fact]
    public async Task App_server_stdio_is_a_protocol_only_child_process()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-protocol-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var paths = new OpenCoWorkPaths(root);
            await WorkspaceInitializer.InitializeAsync(
                paths,
                TimeSpan.FromSeconds(2),
                cancellationToken);
            await File.WriteAllTextAsync(
                paths.ConfigPath,
                TestConfig,
                cancellationToken);
            var executable = Path.Combine(
                Path.GetDirectoryName(typeof(OpenCoWorkCli).Assembly.Location)!,
                OperatingSystem.IsWindows() ? "opencowork.exe" : "opencowork");
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("app-server");
            start.ArgumentList.Add("--workspace");
            start.ArgumentList.Add(root);
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("Could not start app-server.");

            await process.StandardInput.WriteLineAsync(
                JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = 1,
                    method = "initialize",
                    @params = new
                    {
                        client = new { name = "test", version = "1" },
                        wireVersions = new[] { "1.0" },
                        workspace = new { path = root },
                    },
                }));
            await process.StandardInput.FlushAsync(cancellationToken);
            var initializeLine =
                await process.StandardOutput.ReadLineAsync(cancellationToken);
            if (initializeLine is null)
            {
                Assert.Fail(
                    await process.StandardError.ReadToEndAsync(cancellationToken));
            }

            using var initialized = JsonDocument.Parse(initializeLine);
            Assert.Equal(
                "1.0",
                initialized.RootElement
                    .GetProperty("result")
                    .GetProperty("wireVersion")
                    .GetString());

            await process.StandardInput.WriteLineAsync(
                """{"jsonrpc":"2.0","id":2,"method":"thread/list","params":{}}""");
            await process.StandardInput.FlushAsync(cancellationToken);
            var listLine =
                await process.StandardOutput.ReadLineAsync(cancellationToken);
            if (listLine is null)
            {
                Assert.Fail(
                    await process.StandardError.ReadToEndAsync(cancellationToken));
            }

            using var listed = JsonDocument.Parse(listLine);
            Assert.Equal(2, listed.RootElement.GetProperty("id").GetInt32());
            Assert.Empty(
                listed.RootElement
                    .GetProperty("result")
                    .GetProperty("threads")
                    .EnumerateArray());

            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken);
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(
                string.Empty,
                await process.StandardOutput.ReadToEndAsync(cancellationToken));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Acp_stdio_is_a_protocol_only_child_process()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-acp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var paths = new OpenCoWorkPaths(root);
            await WorkspaceInitializer.InitializeAsync(
                paths,
                TimeSpan.FromSeconds(2),
                cancellationToken);
            await File.WriteAllTextAsync(
                paths.ConfigPath,
                TestConfig,
                cancellationToken);
            var executable = Path.Combine(
                Path.GetDirectoryName(typeof(OpenCoWorkCli).Assembly.Location)!,
                OperatingSystem.IsWindows() ? "opencowork.exe" : "opencowork");
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("acp");
            start.ArgumentList.Add("--workspace");
            start.ArgumentList.Add(root);
            start.Environment["OPENCOWORK_TEST_API_KEY"] = "process-test-secret";
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("Could not start ACP.");

            await process.StandardInput.WriteLineAsync(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1}}""");
            await process.StandardInput.FlushAsync(cancellationToken);
            var initialized = await ReadResponseAsync(
                process,
                id: 1,
                cancellationToken);
            Assert.Equal(
                1,
                initialized.GetProperty("result")
                    .GetProperty("protocolVersion")
                    .GetInt32());

            await process.StandardInput.WriteLineAsync(
                JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = 2,
                    method = "session/new",
                    @params = new
                    {
                        cwd = root,
                        mcpServers = Array.Empty<object>(),
                    },
                }));
            await process.StandardInput.FlushAsync(cancellationToken);
            var created = await ReadResponseAsync(
                process,
                id: 2,
                cancellationToken);
            Assert.True(
                created.TryGetProperty("result", out var createdResult),
                created.GetRawText());
            var sessionId = createdResult
                .GetProperty("sessionId")
                .GetString();
            Assert.True(Guid.TryParse(sessionId, out _));

            await process.StandardInput.WriteLineAsync(
                JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = 3,
                    method = "session/set_mode",
                    @params = new
                    {
                        sessionId,
                        modeId = "plan",
                    },
                }));
            await process.StandardInput.FlushAsync(cancellationToken);
            var mode = await ReadResponseAsync(
                process,
                id: 3,
                cancellationToken);
            Assert.Equal(JsonValueKind.Object, mode.GetProperty("result").ValueKind);

            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken);
            Assert.Equal(0, process.ExitCode);
            var remainder =
                await process.StandardOutput.ReadToEndAsync(cancellationToken);
            foreach (var line in remainder.Split(
                         '\n',
                         StringSplitOptions.RemoveEmptyEntries))
            {
                using var message = JsonDocument.Parse(line);
                Assert.Equal(
                    "2.0",
                    message.RootElement.GetProperty("jsonrpc").GetString());
            }

            Assert.DoesNotContain(
                "process-test-secret",
                remainder +
                await process.StandardError.ReadToEndAsync(cancellationToken),
                StringComparison.Ordinal);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<JsonElement> ReadResponseAsync(
        Process process,
        int id,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                Assert.Fail(
                    await process.StandardError.ReadToEndAsync(cancellationToken));
            }

            using var document = JsonDocument.Parse(line);
            var message = document.RootElement.Clone();
            if (message.TryGetProperty("id", out var responseId) &&
                responseId.ValueKind == JsonValueKind.Number &&
                responseId.GetInt32() == id)
            {
                return message;
            }
        }
    }
}
