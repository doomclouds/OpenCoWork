using Microsoft.Extensions.Logging;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Logging;
using OpenCoWork.Core.State;
using OpenCoWork.Core.Workspaces;
using Xunit;

namespace OpenCoWork.IntegrationTests;

public sealed class DataFoundationIntegrationTests
{
    [Fact]
    public async Task Initialized_workspace_loads_config_opens_state_and_never_logs_secret_canary()
    {
        const string canary = "integration-secret-54d7ac";
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-integration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var paths = WorkspaceDiscovery.Discover(root, root);

        try
        {
            await WorkspaceInitializer.InitializeAsync(
                paths,
                TimeSpan.FromSeconds(2),
                cancellationToken);
            await File.WriteAllTextAsync(
                paths.ConfigPath,
                $$"""
                {
                  // Exercise the real JSONC path.
                  "runtime": {
                    "stopTimeout": "12s"
                  },
                  "credentials": {
                    "token": "{{canary}}"
                  },
                }
                """,
                cancellationToken);

            var config = ConfigLoader.Load(new ConfigLoadRequest(
            [
                new ConfigSectionDescriptor(
                    "runtime",
                    typeof(RuntimeConfig),
                    static () => new RuntimeConfig(),
                    """
                    {"type":"object","properties":{"state":{"type":"object","properties":{"busyTimeout":{"type":"string","format":"duration"}},"required":["busyTimeout"],"additionalProperties":false},"stopTimeout":{"type":"string","format":"duration"}},"required":["state"],"additionalProperties":false}
                    """),
                new ConfigSectionDescriptor(
                    "credentials",
                    typeof(CredentialsConfig),
                    static () => new CredentialsConfig(),
                    """
                    {"type":"object","properties":{"token":{"type":"string","x-opencowork-secret":true}},"required":["token"],"additionalProperties":false}
                    """),
            ])
            {
                WorkspaceConfigPath = paths.ConfigPath,
            });

            Assert.True(config.Validation.IsValid);
            Assert.Equal(
                TimeSpan.FromSeconds(12),
                config.Snapshot!.GetRequiredSection<RuntimeConfig>().StopTimeout);
            Assert.Equal(
                paths.WorkspaceRoot,
                WorkspaceDiscovery.Discover(
                    Path.Combine(paths.WorkspaceRoot, ".opencowork", "runtime"))
                    .WorkspaceRoot);

            var stateRuntime = new StateRuntime(paths, TimeSpan.FromSeconds(2));
            await using (var state = await stateRuntime.OpenReadOnlyConnectionAsync(
                             cancellationToken))
            {
                await using var command = state.CreateCommand();
                command.CommandText =
                    "SELECT schema_version FROM state_info WHERE id = 1;";
                Assert.Equal(1L, await command.ExecuteScalarAsync(cancellationToken));
            }

            var fileProvider = new JsonLinesFileLoggerProvider(
                paths.LogsDirectory,
                LogLevel.Information);
            using (var factory = LoggerFactory.Create(builder =>
                   {
                       builder.ClearProviders();
                       builder.AddProvider(new RedactingLoggerProvider(
                           fileProvider,
                           SecretRedactor.FromSnapshot(config.Snapshot)));
                   }))
            {
                factory.CreateLogger("Integration")
                    .LogInformation("token={Token}", canary);
            }

            fileProvider.Dispose();
            Assert.DoesNotContain(
                canary,
                await File.ReadAllTextAsync(
                    fileProvider.FilePath,
                    cancellationToken),
                StringComparison.Ordinal);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    public sealed record CredentialsConfig
    {
        [Secret]
        public string Token { get; init; } = string.Empty;
    }
}
