using System.Diagnostics;
using System.Text.Json;
using OpenCoWork.App;
using OpenCoWork.Core.Workspaces;
using Xunit;

namespace OpenCoWork.IntegrationTests;

public sealed class ProtocolProcessIntegrationTests
{
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
}
