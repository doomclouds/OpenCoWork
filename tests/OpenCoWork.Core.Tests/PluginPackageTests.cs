using System.IO.Compression;
using System.Text;
using OpenCoWork.Core.Capabilities;
using OpenCoWork.Core.Workspaces;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class PluginPackageTests
{
    [Fact]
    public async Task Local_package_is_content_addressed_and_revalidated()
    {
        var (root, workspace, user) = CreateDirectories();
        try
        {
            var archive = Path.Combine(root, "plugin.zip");
            CreateArchive(
                archive,
                ("opencowork.plugin.json", Manifest("1.2.3", includeEntryPoint: false)));
            var paths = new CapabilityPersistencePaths(
                new OpenCoWorkPaths(workspace),
                user);
            var store = new PluginPackageStore(paths);

            var package = await store.StoreLocalAsync(
                archive,
                TestContext.Current.CancellationToken);
            var reused = await store.StoreLocalAsync(
                archive,
                TestContext.Current.CancellationToken);

            Assert.Equal("acme/echo", package.Manifest.Id);
            Assert.Equal("1.2.3", package.Manifest.Version);
            Assert.Equal(package.ContentSha256, reused.ContentSha256);
            Assert.True(File.Exists(Path.Combine(
                package.PackageDirectory,
                "opencowork.plugin.json")));
            Assert.Equal(
                package.ContentSha256,
                await store.ValidateStoredAsync(
                    package.ContentSha256,
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("folder\\file.txt")]
    public async Task Unsafe_archive_path_is_rejected(string entryName)
    {
        var (root, workspace, user) = CreateDirectories();
        try
        {
            var archive = Path.Combine(root, "unsafe.zip");
            CreateArchive(
                archive,
                ("opencowork.plugin.json", Manifest("1.0.0", includeEntryPoint: false)),
                (entryName, "unsafe"));
            var store = new PluginPackageStore(
                new CapabilityPersistencePaths(
                    new OpenCoWorkPaths(workspace),
                    user));

            var exception = await Assert.ThrowsAsync<PluginPackageException>(() =>
                store.StoreLocalAsync(
                    archive,
                    TestContext.Current.CancellationToken));

            Assert.Equal(PluginErrorCodes.PackageInvalid, exception.Code);
            Assert.False(File.Exists(Path.Combine(root, "escape.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Case_colliding_entries_are_rejected()
    {
        var (root, workspace, user) = CreateDirectories();
        try
        {
            var archive = Path.Combine(root, "collision.zip");
            CreateArchive(
                archive,
                ("opencowork.plugin.json", Manifest("1.0.0", includeEntryPoint: false)),
                ("skills/A.txt", "a"),
                ("skills/a.txt", "b"));
            var store = new PluginPackageStore(
                new CapabilityPersistencePaths(
                    new OpenCoWorkPaths(workspace),
                    user));

            await Assert.ThrowsAsync<PluginPackageException>(() =>
                store.StoreLocalAsync(
                    archive,
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    internal static string CreateToolPackage(
        string root,
        string version,
        string assemblyPath)
    {
        var archive = Path.Combine(root, $"plugin-{version}.zip");
        CreateArchive(
            archive,
            ("opencowork.plugin.json", Manifest(version, includeEntryPoint: true)),
            ("lib/net10.0/OpenCoWork.PluginFixture.dll", File.ReadAllBytes(assemblyPath)),
            ("tools/echo.json",
                """
                {
                  "id": "echo",
                  "description": "Echo text.",
                  "inputSchema": {
                    "$schema": "https://json-schema.org/draft/2020-12/schema",
                    "type": "object",
                    "properties": { "text": { "type": "string" } },
                    "required": ["text"],
                    "additionalProperties": false
                  },
                  "effects": [],
                  "replaySafety": "safe",
                  "exposure": "direct",
                  "audience": ["model", "host"],
                  "defaultTimeoutMs": 30000,
                  "executor": "echo"
                }
                """));
        return archive;
    }

    internal static string Manifest(string version, bool includeEntryPoint) =>
        $$"""
        {
          "schemaVersion": 1,
          "hostApiVersion": 1,
          "id": "acme/echo",
          "version": "{{version}}",
          "displayName": "Echo",
          {{(includeEntryPoint
              ? """
                "entryPoint": {
                  "assembly": "lib/net10.0/OpenCoWork.PluginFixture.dll",
                  "type": "OpenCoWork.PluginFixture.EchoPlugin"
                },
                """
              : string.Empty)}}
          "contributions": {
            "skills": [],
            "providers": [],
            "authProfiles": [],
            "mcpServers": [],
            "lspServers": [],
            "tools": [{{(includeEntryPoint ? "\"tools/echo.json\"" : string.Empty)}}],
            "hooks": []
          }
        }
        """;

    internal static void CreateArchive(
        string path,
        params (string Name, object Content)[] entries)
    {
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
            using var output = entry.Open();
            var bytes = content is byte[] raw
                ? raw
                : Encoding.UTF8.GetBytes((string)content);
            output.Write(bytes);
        }
    }

    internal static (string Root, string Workspace, string User) CreateDirectories()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-plugin-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "workspace");
        var user = Path.Combine(root, "user");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(user);
        return (root, workspace, user);
    }
}
