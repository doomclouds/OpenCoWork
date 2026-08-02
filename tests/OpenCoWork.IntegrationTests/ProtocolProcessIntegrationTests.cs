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
          "automations": {
            "enabled": true
          },
          "models": {
            "defaultModel": "deepseek-v4-flash",
            "reasoningEffort": "high"
          }
        }
        """;

    [Fact]
    public async Task App_server_stdio_exposes_wire_13_automations_and_wire_12_cowork()
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
            var automationsDirectory = Path.Combine(
                paths.OpenCoWorkDirectory,
                "automations",
                "definitions");
            Directory.CreateDirectory(automationsDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(automationsDirectory, "process-smoke.yaml"),
                """
                schemaVersion: 1
                id: process-smoke
                displayName: Process Smoke
                enabled: true
                schedule:
                  cron: "0 2 * * *"
                  timeZone: UTC
                workspace:
                  mode: project
                prompt: Inspect the workspace.
                inputSchema:
                  type: object
                  additionalProperties: false
                defaults: {}
                allow:
                  effects: []
                runTimeout: 30m
                attentionTimeout: 24h
                """,
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
            start.Environment["OPENCOWORK_VALIDATION_USER_PROFILE"] =
                Directory.CreateDirectory(Path.Combine(root, "user-profile")).FullName;
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
                        wireVersions = new[] { "1.3", "1.2", "1.1", "1.0" },
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
                "1.3",
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

            await process.StandardInput.WriteLineAsync(
                """{"jsonrpc":"2.0","id":20,"method":"automation/list","params":{"pageSize":1}}""");
            await process.StandardInput.FlushAsync(cancellationToken);
            var automationMessages = new List<JsonElement>();
            var automations = await ReadResponseAsync(
                process,
                id: 20,
                cancellationToken,
                automationMessages);
            Assert.Equal(
                "process-smoke",
                automations.GetProperty("result").GetProperty("value")
                    .GetProperty("items")[0].GetProperty("automationId").GetString());

            var commandId = Guid.CreateVersion7();
            var upsert = new
            {
                commandId,
                expectedRevision = (long?)null,
                profileId = (Guid?)null,
                name = "M7 Process Profile",
                description = "Wire 1.2 process test.",
                instructions = "Return concise results.",
                providerId = "deepseek",
                modelId = "deepseek-v4-flash",
                skillAllowlist = Array.Empty<string>(),
                toolAllowlist = Array.Empty<string>(),
            };
            var outOfBand = new List<JsonElement>();
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 3,
                method = "agent/profile/upsert",
                @params = upsert,
            }));
            await process.StandardInput.FlushAsync(cancellationToken);
            var created = await ReadResponseAsync(
                process,
                id: 3,
                cancellationToken,
                outOfBand);
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 4,
                method = "agent/profile/upsert",
                @params = upsert,
            }));
            await process.StandardInput.FlushAsync(cancellationToken);
            var replayed = await ReadResponseAsync(
                process,
                id: 4,
                cancellationToken,
                outOfBand);
            Assert.Equal(
                created.GetProperty("result").GetProperty("coWorkRevision")
                    .GetInt64(),
                replayed.GetProperty("result").GetProperty("coWorkRevision")
                    .GetInt64());
            Assert.Single(outOfBand, message =>
                message.TryGetProperty("method", out var method) &&
                method.GetString() == "agent/changed");

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
            start.Environment["OPENCOWORK_VALIDATION_USER_PROFILE"] =
                Directory.CreateDirectory(Path.Combine(root, "user-profile")).FullName;
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
        CancellationToken cancellationToken,
        ICollection<JsonElement>? outOfBand = null)
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

            outOfBand?.Add(message);
        }
    }
}
