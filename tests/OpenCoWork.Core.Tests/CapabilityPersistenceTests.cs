using System.Diagnostics;
using System.Text;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Capabilities;
using OpenCoWork.Core.Workspaces;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class CapabilityPersistenceTests
{
    private const string ShaA =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ShaB =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void Paths_are_fixed_and_fully_qualified()
    {
        using var files = new TempDirectory();
        var workspace = files.CreateDirectory("workspace");
        var userProfile = files.CreateDirectory("user");
        var workspacePaths = new OpenCoWorkPaths(workspace);
        var paths = new CapabilityPersistencePaths(workspacePaths, userProfile);

        Assert.Equal(
            Path.Combine(workspace, ".opencowork", "plugins.lock.json"),
            workspacePaths.PluginsLockPath);
        Assert.Equal(
            Path.Combine(workspace, ".opencowork", "capabilities.json"),
            workspacePaths.CapabilitiesPath);
        Assert.Equal(
            Path.Combine(userProfile, ".opencowork", "capabilities.json"),
            paths.UserCapabilitiesPath);
        Assert.Equal(
            Path.Combine(userProfile, ".opencowork", "trust", "decisions.json"),
            paths.TrustDecisionsPath);
        Assert.All(
            [
                workspacePaths.PluginsLockPath,
                workspacePaths.CapabilitiesPath,
                paths.UserCapabilitiesPath,
                paths.TrustDecisionsPath,
            ],
            path => Assert.True(Path.IsPathFullyQualified(path)));
    }

    [Fact]
    public async Task Missing_files_load_as_empty_schema_one_documents()
    {
        using var files = new TempDirectory();
        var store = files.CreateStore();
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.Empty((await store.LoadPluginLockAsync(cancellationToken)).Plugins);
        Assert.Empty((await store.LoadTrustDecisionsAsync(cancellationToken)).Decisions);
        Assert.Empty((await store.LoadWorkspaceOverridesAsync(cancellationToken)).Disabled);
        Assert.Empty((await store.LoadUserOverridesAsync(cancellationToken)).SkillVariants);
    }

    [Fact]
    public async Task External_channel_trust_is_bound_to_workspace_source_and_digest()
    {
        using var files = new TempDirectory();
        var store = files.CreateStore();
        var workspace = Path.GetFullPath(files.Workspace);
        var cancellationToken = TestContext.Current.CancellationToken;
        var trusted = new CapabilityTrustDecision(
            workspace,
            CapabilitySourceKind.Workspace,
            "channel/build-bot",
            "1",
            ShaA,
            [CapabilityTrustScope.ExternalChannel],
            []);

        await store.SaveTrustDecisionsAsync(
            new TrustDecisionsDocument(1, [trusted]),
            cancellationToken);

        var decision = Assert.Single(
            (await store.LoadTrustDecisionsAsync(cancellationToken)).Decisions);
        Assert.True(decision.Matches(
            workspace,
            CapabilitySourceKind.Workspace,
            "channel/build-bot",
            "1",
            ShaA));
        Assert.False(decision.Matches(
            workspace,
            CapabilitySourceKind.Workspace,
            "channel/build-bot",
            "1",
            ShaB));
        Assert.Equal([CapabilityTrustScope.ExternalChannel], decision.AllowedScopes);
    }

    [Theory]
    [InlineData(
        """
        {"schemaVersion":1,"plugins":[],"unexpected":true}
        """)]
    [InlineData(
        """
        {"schemaVersion":1,"plugins":[
          {"id":"acme/git","version":"1.2.3","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","enabled":true},
          {"id":"acme/git","version":"1.2.4","sha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","enabled":true}
        ]}
        """)]
    [InlineData(
        """
        {"schemaVersion":1,"plugins":[
          {"id":"acme/git","version":">=1.2.3","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","enabled":true}
        ]}
        """)]
    [InlineData(
        """
        {"schemaVersion":1,"plugins":[
          {"id":"acme/git","version":"1.2.3","sha256":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","enabled":true}
        ]}
        """)]
    [InlineData(
        """
        {"schemaVersion":1,"plugins":[
          {"id":"acme/git","version":"1.2.3","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}
        ]}
        """)]
    [InlineData(
        """
        {"schemaVersion":1,"schemaVersion":1,"plugins":[]}
        """)]
    public async Task Plugin_lock_rejects_noncanonical_or_ambiguous_content(string json)
    {
        using var files = new TempDirectory();
        var store = files.CreateStore();
        files.WriteWorkspace(".opencowork", "plugins.lock.json", json);
        var cancellationToken = TestContext.Current.CancellationToken;

        var error = await Assert.ThrowsAsync<CapabilityPersistenceException>(
            () => store.LoadPluginLockAsync(cancellationToken));

        Assert.Equal(CapabilityErrorCodes.PersistenceInvalid, error.Code);
    }

    [Fact]
    public async Task Documents_round_trip_with_deterministic_order_and_digest_bound_trust()
    {
        using var files = new TempDirectory();
        var store = files.CreateStore();
        var workspace = Path.GetFullPath(files.Workspace);
        var cancellationToken = TestContext.Current.CancellationToken;
        var trusted = new CapabilityTrustDecision(
            workspace,
            CapabilitySourceKind.Plugin,
            "acme/git",
            "1.2.3",
            ShaA,
            [CapabilityTrustScope.PromptContribution],
            [CapabilityTrustScope.InProcessCode]);

        await store.SavePluginLockAsync(
            new PluginLockDocument(
                1,
                [
                    new PluginLockEntry("zeta/tools", "2.0.0", ShaB, false),
                    new PluginLockEntry("acme/git", "1.2.3", ShaA, true),
                ]),
            cancellationToken);
        await store.SaveTrustDecisionsAsync(
            new TrustDecisionsDocument(1, [trusted]),
            cancellationToken);
        await store.SaveWorkspaceOverridesAsync(new CapabilityOverridesDocument(
            1,
            [
                new DisabledCapability(CapabilityKind.Tool, "zeta/status"),
                new DisabledCapability(CapabilityKind.Skill, "unknown/preserved"),
            ],
            [new SkillVariantOverride("acme/review", "acme/review-strict")]),
            cancellationToken);
        await store.SaveUserOverridesAsync(
            new CapabilityOverridesDocument(
                1,
                [new DisabledCapability(CapabilityKind.Plugin, "acme/git")],
                []),
            cancellationToken);

        var pluginLock = await store.LoadPluginLockAsync(cancellationToken);
        var trust = await store.LoadTrustDecisionsAsync(cancellationToken);
        var workspaceOverrides = await store.LoadWorkspaceOverridesAsync(cancellationToken);
        var userOverrides = await store.LoadUserOverridesAsync(cancellationToken);

        Assert.Equal(["acme/git", "zeta/tools"], pluginLock.Plugins.Select(plugin => plugin.Id));
        Assert.True(trust.Decisions.Single().Matches(
            workspace,
            CapabilitySourceKind.Plugin,
            "acme/git",
            "1.2.3",
            ShaA));
        Assert.False(trust.Decisions.Single().Matches(
            workspace,
            CapabilitySourceKind.Plugin,
            "acme/git",
            "1.2.3",
            ShaB));
        Assert.Contains(
            workspaceOverrides.Disabled,
            item => item.Id == "unknown/preserved");
        Assert.Equal("acme/review-strict", workspaceOverrides.SkillVariants.Single().VariantId);
        Assert.Equal("acme/git", userOverrides.Disabled.Single().Id);
    }

    [Fact]
    public async Task Trust_and_overrides_reject_ambiguous_entries()
    {
        using var files = new TempDirectory();
        var store = files.CreateStore();
        var cancellationToken = TestContext.Current.CancellationToken;
        var workspaceJsonPath = files.WriteUser(
            ".opencowork",
            "trust",
            "decisions.json",
            $$"""
            {
              "schemaVersion": 1,
              "decisions": [{
                "workspacePath": "{{Escape(files.Workspace)}}",
                "sourceKind": "plugin",
                "sourceId": "acme/git",
                "sourceVersion": "1.2.3",
                "sha256": "{{ShaA}}",
                "allowedScopes": ["promptContribution"],
                "deniedScopes": ["promptContribution"]
              }]
            }
            """);

        var trustError = await Assert.ThrowsAsync<CapabilityPersistenceException>(
            () => store.LoadTrustDecisionsAsync(cancellationToken));
        Assert.Equal(CapabilityErrorCodes.PersistenceInvalid, trustError.Code);

        File.Delete(workspaceJsonPath);
        files.WriteWorkspace(
            ".opencowork",
            "capabilities.json",
            """
            {
              "schemaVersion": 1,
              "disabled": [
                {"kind":"skill","id":"acme/review"},
                {"kind":"skill","id":"acme/review"}
              ],
              "skillVariants": []
            }
            """);

        var overrideError = await Assert.ThrowsAsync<CapabilityPersistenceException>(
            () => store.LoadWorkspaceOverridesAsync(cancellationToken));
        Assert.Equal(CapabilityErrorCodes.PersistenceInvalid, overrideError.Code);
    }

    [Fact]
    public async Task Failed_atomic_replace_preserves_the_previous_file_and_removes_temporary_files()
    {
        using var files = new TempDirectory();
        var originalStore = files.CreateStore();
        var cancellationToken = TestContext.Current.CancellationToken;
        await originalStore.SavePluginLockAsync(
            new PluginLockDocument(
                1,
                [new PluginLockEntry("acme/git", "1.2.3", ShaA, true)]),
            cancellationToken);
        var original = await File.ReadAllTextAsync(
            Path.Combine(files.Workspace, ".opencowork", "plugins.lock.json"),
            cancellationToken);
        var failingStore = files.CreateStore(point =>
        {
            if (point == CapabilityPersistenceFaultPoint.BeforeReplace)
            {
                throw new IOException("Injected failure.");
            }
        });

        await Assert.ThrowsAsync<CapabilityPersistenceException>(
            () => failingStore.SavePluginLockAsync(
                new PluginLockDocument(
                    1,
                    [new PluginLockEntry("acme/git", "2.0.0", ShaB, true)]),
                cancellationToken));

        Assert.Equal(
            original,
            await File.ReadAllTextAsync(
                Path.Combine(files.Workspace, ".opencowork", "plugins.lock.json"),
                cancellationToken));
        Assert.Empty(Directory.EnumerateFiles(
            Path.Combine(files.Workspace, ".opencowork"),
            ".opencowork-*.tmp"));
    }

    [Fact]
    public async Task User_security_files_are_private_on_unix_and_symlink_escapes_are_rejected()
    {
        using var files = new TempDirectory();
        var store = files.CreateStore();
        var cancellationToken = TestContext.Current.CancellationToken;
        await store.SaveUserOverridesAsync(
            CapabilityOverridesDocument.Empty,
            cancellationToken);
        await store.SaveTrustDecisionsAsync(
            TrustDecisionsDocument.Empty,
            cancellationToken);

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(
                    Path.Combine(files.UserProfile, ".opencowork", "capabilities.json")));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(
                    Path.Combine(
                        files.UserProfile,
                        ".opencowork",
                        "trust",
                        "decisions.json")));
        }

        var outside = files.CreateDirectory("outside");
        files.ReplaceWorkspaceMetadataWithDirectoryLink(outside);

        var error = await Assert.ThrowsAsync<CapabilityPersistenceException>(
            () => store.SavePluginLockAsync(
                PluginLockDocument.Empty,
                cancellationToken));

        Assert.Equal(CapabilityErrorCodes.PersistenceUnavailable, error.Code);
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal);

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"opencowork-capability-persistence-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            Workspace = CreateDirectory("workspace");
            UserProfile = CreateDirectory("user");
        }

        public string Path { get; }

        public string Workspace { get; }

        public string UserProfile { get; }

        public string CreateDirectory(params string[] segments)
        {
            var path = segments.Aggregate(Path, System.IO.Path.Combine);
            Directory.CreateDirectory(path);
            return path;
        }

        public CapabilityFileStore CreateStore(
            Action<CapabilityPersistenceFaultPoint>? fault = null) =>
            new(
                new CapabilityPersistencePaths(
                    new OpenCoWorkPaths(Workspace),
                    UserProfile),
                fault);

        public string WriteWorkspace(params string[] segmentsAndContents) =>
            Write(Workspace, segmentsAndContents);

        public string WriteUser(params string[] segmentsAndContents) =>
            Write(UserProfile, segmentsAndContents);

        public void ReplaceWorkspaceMetadataWithDirectoryLink(string target)
        {
            var metadata = System.IO.Path.Combine(Workspace, ".opencowork");
            Directory.CreateDirectory(metadata);
            Directory.Delete(metadata, recursive: true);
            if (!OperatingSystem.IsWindows() ||
                Environment.GetEnvironmentVariable(
                    "OPENCOWORK_VALIDATE_WINDOWS_SYMLINKS") == "1")
            {
                Directory.CreateSymbolicLink(metadata, target);
                return;
            }

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/d /c mklink /J \"{metadata}\" \"{target}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }) ?? throw new InvalidOperationException("Could not start mklink.");
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new IOException(process.StandardError.ReadToEnd());
            }
        }

        public void Dispose()
        {
            var metadata = System.IO.Path.Combine(Workspace, ".opencowork");
            if (Directory.Exists(metadata) &&
                (File.GetAttributes(metadata) & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(metadata);
            }

            Directory.Delete(Path, recursive: true);
        }

        private static string Write(string root, string[] segmentsAndContents)
        {
            var path = segmentsAndContents
                .SkipLast(1)
                .Aggregate(root, System.IO.Path.Combine);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, segmentsAndContents[^1], Encoding.UTF8);
            return path;
        }
    }
}
