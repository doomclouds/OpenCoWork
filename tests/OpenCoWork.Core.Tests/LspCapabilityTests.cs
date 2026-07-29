using System.Text.Json;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Capabilities;
using OpenCoWork.Core.Tools;
using OpenCoWork.Core.Workspaces;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class LspCapabilityTests
{
    [Fact]
    public async Task Untrusted_server_is_pending_trust()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workspace = new LspWorkspace();
        await workspace.WriteConfigAsync(
            """
            {
              "schemaVersion": 1,
              "servers": [{
                "id": "workspace/csharp",
                "enabled": true,
                "selectors": [{
                  "languageId": "csharp",
                  "extensions": [".cs"]
                }],
                "command": "git",
                "arguments": [],
                "workingDirectory": "workspace",
                "environment": {}
              }]
            }
            """,
            cancellationToken);
        var source = workspace.CreateSource();

        var discovered = await source.DiscoverAsync(cancellationToken);

        var item = Assert.Single(Assert.Single(discovered.Contributions).Items);
        Assert.Equal(CapabilityKind.LspServer, item.Kind);
        Assert.Equal(CapabilityStatus.PendingTrust, item.Status);
        Assert.Equal([CapabilityTrustScope.OutOfProcess], item.RequiredTrustScopes);
        Assert.Contains(ToolErrorCodes.TrustRequired, item.DiagnosticCodes);
    }

    [Fact]
    public async Task Strict_config_rejects_unknown_fields()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workspace = new LspWorkspace();
        await workspace.WriteConfigAsync(
            """
            {
              "schemaVersion": 1,
              "servers": [{
                "id": "workspace/csharp",
                "enabled": true,
                "selectors": [{
                  "languageId": "csharp",
                  "extensions": [".cs"],
                  "glob": "**/*.cs"
                }],
                "command": "git",
                "arguments": [],
                "workingDirectory": "workspace",
                "environment": {}
              }]
            }
            """,
            cancellationToken);

        var discovered = await workspace.CreateSource()
            .DiscoverAsync(cancellationToken);

        var item = Assert.Single(Assert.Single(discovered.Contributions).Items);
        Assert.Equal(CapabilityStatus.Faulted, item.Status);
        Assert.Contains(
            LspCapabilityErrorCodes.ConfigurationInvalid,
            item.DiagnosticCodes);
    }

    [Fact]
    public async Task Request_allowlist_and_selector_are_enforced_before_process_use()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workspace = new LspWorkspace();
        await workspace.WriteConfigAsync(
            """
            {
              "schemaVersion": 1,
              "servers": [{
                "id": "workspace/csharp",
                "enabled": true,
                "selectors": [{
                  "languageId": "csharp",
                  "extensions": [".cs"]
                }],
                "command": "git",
                "arguments": [],
                "workingDirectory": "workspace",
                "environment": {}
              }]
            }
            """,
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Root, "Program.cs"),
            "class Program { }\n",
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Root, "notes.txt"),
            "text\n",
            cancellationToken);
        var source = workspace.CreateSource();
        await source.DiscoverAsync(cancellationToken);

        var denied = await Assert.ThrowsAsync<LspCapabilityException>(() =>
            source.RequestAsync(
                new LspRequest(
                    "workspace/csharp",
                    "rename",
                    "Program.cs",
                    Line: 0,
                    Character: 0),
                cancellationToken));
        var selector = await Assert.ThrowsAsync<LspCapabilityException>(() =>
            source.RequestAsync(
                new LspRequest(
                    "workspace/csharp",
                    "hover",
                    "notes.txt",
                    Line: 0,
                    Character: 0),
                cancellationToken));
        var pending = await Assert.ThrowsAsync<LspCapabilityException>(() =>
            source.RequestAsync(
                new LspRequest(
                    "workspace/csharp",
                    "hover",
                    "Program.cs",
                    Line: 0,
                    Character: 0),
                cancellationToken));

        Assert.Equal(LspCapabilityErrorCodes.MethodDenied, denied.Code);
        Assert.Equal(LspCapabilityErrorCodes.SelectorMismatch, selector.Code);
        Assert.Equal(LspCapabilityErrorCodes.Disconnected, pending.Code);
    }

    [Fact]
    public async Task Invalid_server_is_isolated_from_valid_sibling()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workspace = new LspWorkspace();
        await workspace.WriteConfigAsync(
            """
            {
              "schemaVersion": 1,
              "servers": [
                {
                  "id": "workspace/invalid",
                  "enabled": true,
                  "selectors": [{
                    "languageId": "csharp",
                    "extensions": [".cs"],
                    "glob": "**/*.cs"
                  }],
                  "command": "git",
                  "arguments": [],
                  "workingDirectory": "workspace",
                  "environment": {}
                },
                {
                  "id": "workspace/disabled",
                  "enabled": false,
                  "selectors": [{
                    "languageId": "csharp",
                    "extensions": [".cs"]
                  }],
                  "command": "git",
                  "arguments": [],
                  "workingDirectory": "workspace",
                  "environment": {}
                }
              ]
            }
            """,
            cancellationToken);

        var discovered = await workspace.CreateSource()
            .DiscoverAsync(cancellationToken);
        var items = discovered.Contributions
            .SelectMany(contribution => contribution.Items)
            .ToDictionary(item => item.Id, StringComparer.Ordinal);

        Assert.Equal(CapabilityStatus.Faulted, items["workspace/invalid"].Status);
        Assert.Equal(CapabilityStatus.Disabled, items["workspace/disabled"].Status);
    }

    private sealed class LspWorkspace : IDisposable
    {
        private readonly CapabilityFileStore _files;

        public LspWorkspace()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"opencowork-lsp-{Guid.NewGuid():N}");
            var user = Path.Combine(Root, "user");
            Directory.CreateDirectory(Path.Combine(Root, ".opencowork"));
            Directory.CreateDirectory(user);
            var paths = new OpenCoWorkPaths(Root);
            _files = new CapabilityFileStore(
                new CapabilityPersistencePaths(paths, user));
        }

        public string Root { get; }

        public LspCapabilitySource CreateSource() =>
            new(new OpenCoWorkPaths(Root), _files, auth: null);

        public Task WriteConfigAsync(
            string json,
            CancellationToken cancellationToken) =>
            File.WriteAllTextAsync(
                Path.Combine(Root, ".opencowork", "lsp.json"),
                json,
                cancellationToken);

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
