using System.Diagnostics;
using System.Text.Json;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Capabilities;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Logging;
using OpenCoWork.Core.Workspaces;
using Xunit;
using LspFixtureMarker = OpenCoWork.LspFixture.FixtureMarker;

namespace OpenCoWork.IntegrationTests;

public sealed class LspCapabilityIntegrationTests
{
    [Fact]
    public async Task Stdio_lsp_reads_disk_and_stops_process()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateTemporaryRoot("stdio");
        try
        {
            var pidPath = Path.Combine(root, "fixture.pid");
            var tracePath = Path.Combine(root, "fixture.trace");
            var (paths, files, source) = CreateRuntime(root);
            await WriteConfigAsync(
                paths,
                new Dictionary<string, object>
                {
                    ["OPENCOWORK_LSP_PID_FILE"] = new { literal = pidPath },
                    ["OPENCOWORK_LSP_TRACE_FILE"] = new { literal = tracePath },
                },
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(paths.WorkspaceRoot, "Program.cs"),
                "class DiskFactV1 { }\n",
                cancellationToken);

            var pending = await source.DiscoverAsync(cancellationToken);
            Assert.Equal(
                CapabilityStatus.PendingTrust,
                Assert.Single(pending.Contributions[0].Items).Status);
            await TrustAsync(
                paths,
                files,
                await source.InspectAsync("workspace/csharp", cancellationToken),
                cancellationToken);

            var ready = await source.DiscoverAsync(cancellationToken);

            var server = Assert.Single(ready.Contributions[0].Items);
            Assert.Equal(CapabilityStatus.Ready, server.Status);
            await WaitForFileAsync(pidPath, cancellationToken);
            using var process = Process.GetProcessById(int.Parse(
                await File.ReadAllTextAsync(pidPath, cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture));
            var hover = await source.RequestAsync(
                new LspRequest(
                    "workspace/csharp",
                    "hover",
                    "Program.cs",
                    Line: 0,
                    Character: 6),
                cancellationToken);
            Assert.Contains("fixture hover", hover.GetRawText());
            await File.WriteAllTextAsync(
                Path.Combine(paths.WorkspaceRoot, "Program.cs"),
                "class DiskFactV2 { }\n",
                cancellationToken);
            _ = await source.RequestAsync(
                new LspRequest(
                    "workspace/csharp",
                    "documentSymbol",
                    "Program.cs"),
                cancellationToken);
            var trace = await File.ReadAllTextAsync(tracePath, cancellationToken);
            Assert.Contains("DiskFactV1", trace);
            Assert.Contains("DiskFactV2", trace);
            Assert.DoesNotContain("textDocument/rename", trace);
            Assert.DoesNotContain("workspace/executeCommand", trace);

            await source.StopAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken);
            Assert.True(process.HasExited);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task External_file_uri_is_rejected_and_restart_advances_generation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateTemporaryRoot("external-uri");
        var outside = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-lsp-outside-{Guid.NewGuid():N}.cs");
        try
        {
            var (paths, files, source) = CreateRuntime(root);
            await File.WriteAllTextAsync(outside, "class Outside { }\n", cancellationToken);
            await WriteConfigAsync(
                paths,
                new Dictionary<string, object>
                {
                    ["OPENCOWORK_LSP_EXTERNAL_URI"] = new
                    {
                        literal = new Uri(outside).AbsoluteUri,
                    },
                },
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(paths.WorkspaceRoot, "Program.cs"),
                "class Program { }\n",
                cancellationToken);
            _ = await source.DiscoverAsync(cancellationToken);
            await TrustAsync(
                paths,
                files,
                await source.InspectAsync("workspace/csharp", cancellationToken),
                cancellationToken);
            var ready = await source.DiscoverAsync(cancellationToken);
            var firstGeneration = Assert.Single(ready.Contributions[0].Items).Generation;

            var invalid = await Assert.ThrowsAsync<LspCapabilityException>(() =>
                source.RequestAsync(
                    new LspRequest(
                        "workspace/csharp",
                        "definition",
                        "Program.cs",
                        Line: 0,
                        Character: 0),
                    cancellationToken));
            Assert.Equal(LspCapabilityErrorCodes.InvalidResponse, invalid.Code);

            await source.RestartAsync(
                "workspace/csharp",
                firstGeneration,
                cancellationToken);
            var refreshed = await source.DiscoverAsync(cancellationToken);
            Assert.True(
                Assert.Single(refreshed.Contributions[0].Items).Generation >
                firstGeneration);
            await source.StopAsync(cancellationToken);
        }
        finally
        {
            File.Delete(outside);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Half_frame_faults_and_leaves_no_running_process()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateTemporaryRoot("half-frame");
        try
        {
            var pidPath = Path.Combine(root, "fixture.pid");
            var (paths, files, source) = CreateRuntime(root);
            await WriteConfigAsync(
                paths,
                new Dictionary<string, object>
                {
                    ["OPENCOWORK_LSP_PID_FILE"] = new { literal = pidPath },
                    ["OPENCOWORK_LSP_HALF_FRAME"] = new { literal = "1" },
                },
                cancellationToken);
            _ = await source.DiscoverAsync(cancellationToken);
            await TrustAsync(
                paths,
                files,
                await source.InspectAsync("workspace/csharp", cancellationToken),
                cancellationToken);

            var result = await source.DiscoverAsync(cancellationToken);

            Assert.Equal(
                CapabilityStatus.Faulted,
                Assert.Single(result.Contributions[0].Items).Status);
            await WaitForFileAsync(pidPath, cancellationToken);
            var pid = int.Parse(
                await File.ReadAllTextAsync(pidPath, cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
            try
            {
                using var process = Process.GetProcessById(pid);
                await process.WaitForExitAsync(cancellationToken);
                Assert.True(process.HasExited);
            }
            catch (ArgumentException)
            {
                // The fixture can exit before Process.GetProcessById observes it.
            }

            await source.StopAsync(cancellationToken);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static (
        OpenCoWorkPaths Paths,
        CapabilityFileStore Files,
        LspCapabilitySource Source) CreateRuntime(string root)
    {
        var workspace = Path.Combine(root, "workspace");
        var user = Path.Combine(root, "user");
        Directory.CreateDirectory(Path.Combine(workspace, ".opencowork"));
        Directory.CreateDirectory(user);
        var paths = new OpenCoWorkPaths(workspace);
        var files = new CapabilityFileStore(
            new CapabilityPersistencePaths(paths, user));
        var auth = new ProviderAuthService(
            new ProviderDeclarationCatalog(paths),
            new InMemoryOsSecretStore(),
            new SecretRedactor([]),
            paths: paths);
        return (paths, files, new LspCapabilitySource(paths, files, auth));
    }

    private static Task WriteConfigAsync(
        OpenCoWorkPaths paths,
        IReadOnlyDictionary<string, object> environment,
        CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(
            paths.LspPath,
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                servers = new[]
                {
                    new
                    {
                        id = "workspace/csharp",
                        enabled = true,
                        selectors = new[]
                        {
                            new
                            {
                                languageId = "csharp",
                                extensions = new[] { ".cs" },
                            },
                        },
                        command = "dotnet",
                        arguments = new[]
                        {
                            typeof(LspFixtureMarker).Assembly.Location,
                        },
                        workingDirectory = "workspace",
                        environment,
                    },
                },
            }),
            cancellationToken);

    private static Task TrustAsync(
        OpenCoWorkPaths paths,
        CapabilityFileStore files,
        LspTrustIdentity identity,
        CancellationToken cancellationToken) =>
        files.SaveTrustDecisionsAsync(
            new TrustDecisionsDocument(
                1,
                [
                    new CapabilityTrustDecision(
                        paths.WorkspaceRoot,
                        CapabilitySourceKind.Workspace,
                        identity.SourceId,
                        identity.Version,
                        identity.Sha256,
                        [CapabilityTrustScope.OutOfProcess],
                        []),
                ]),
            cancellationToken);

    private static string CreateTemporaryRoot(string kind)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-lsp-{kind}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task WaitForFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!File.Exists(path))
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail($"Timed out waiting for {path}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }
    }
}
