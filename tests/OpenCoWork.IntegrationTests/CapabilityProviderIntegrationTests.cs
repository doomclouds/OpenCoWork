using Microsoft.Extensions.DependencyInjection;
using OpenCoWork.App;
using OpenCoWork.Core.Workspaces;
using Xunit;

namespace OpenCoWork.IntegrationTests;

public sealed class CapabilityProviderIntegrationTests
{
    [Fact]
    public async Task Legacy_workspace_provider_is_rejected_before_thread_or_network()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-capability-provider-{Guid.NewGuid():N}");
        var userProfile = Path.Combine(root, "user");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(userProfile);
        try
        {
            var paths = new OpenCoWorkPaths(root);
            await WorkspaceInitializer.InitializeAsync(
                paths,
                TimeSpan.FromSeconds(2),
                cancellationToken);
            await File.WriteAllTextAsync(
                paths.ProvidersPath,
                """
                {
                  "schemaVersion": 1,
                  "providers": []
                }
                """,
                cancellationToken);
            var handler = new CountingHandler();
            var output = new StringWriter();
            var error = new StringWriter();

            var exitCode = await OpenCoWorkCli.RunAsync(
                ["chat", "--workspace", root],
                new StringReader("hello\n"),
                output,
                error,
                root,
                userProfile,
                isInteractive: false,
                services => services.AddSingleton(new HttpClient(handler)),
                cancellationToken);

            Assert.Equal(1, exitCode);
            Assert.Empty(output.ToString());
            Assert.Contains("providers.json", error.ToString(), StringComparison.Ordinal);
            Assert.Equal(0, handler.RequestCount);
            Assert.False(Directory.Exists(paths.ActiveThreadsDirectory) &&
                         Directory.EnumerateFiles(paths.ActiveThreadsDirectory).Any());
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

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            throw new InvalidOperationException("Network must not be reached.");
        }
    }

}
