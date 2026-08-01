using System.Text.Json;
using OpenCoWork.App;
using Xunit;

namespace OpenCoWork.IntegrationTests;

public sealed class OperationsCliIntegrationTests
{
    [Fact]
    public async Task ChannelCli_lists_through_the_channel_service()
    {
        await RunQueryAsync(
            ["channel", "list", "--json"],
            root =>
            {
                Assert.Equal(0, root.GetProperty("operationsRevision").GetInt64());
                Assert.Empty(root.GetProperty("items").EnumerateArray());
            });
    }

    [Fact]
    public async Task HubCli_lists_the_user_registry_without_current_directory_discovery()
    {
        await RunQueryAsync(
            ["hub", "list", "--json"],
            root => Assert.Empty(root.GetProperty("items").EnumerateArray()));
    }

    [Fact]
    public async Task OperationsCli_queries_usage_through_the_operations_service()
    {
        await RunQueryAsync(
            [
                "ops", "usage", "--json",
                "--from", "2026-08-01T00:00:00Z",
                "--to", "2026-08-02T00:00:00Z",
            ],
            root => Assert.Empty(root.EnumerateArray()));
    }

    private static async Task RunQueryAsync(
        IReadOnlyList<string> command,
        Action<JsonElement> assert)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-operations-cli-{Guid.NewGuid():N}");
        var userProfile = Path.Combine(root, "user");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(userProfile);
        try
        {
            var initialized = await InvokeAsync(
                ["init", "--workspace", root],
                root,
                userProfile,
                cancellationToken);
            Assert.Equal(0, initialized.ExitCode);

            var result = await InvokeAsync(
                [.. command, "--workspace", root],
                root,
                userProfile,
                cancellationToken);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(string.Empty, result.Error);
            using var document = JsonDocument.Parse(result.Output);
            assert(document.RootElement);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<CliResult> InvokeAsync(
        string[] args,
        string workingDirectory,
        string userProfile,
        CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await OpenCoWorkCli.RunAsync(
            args,
            output,
            error,
            workingDirectory,
            userProfile,
            cancellationToken);
        return new CliResult(exitCode, output.ToString(), error.ToString());
    }

    private sealed record CliResult(int ExitCode, string Output, string Error);
}
