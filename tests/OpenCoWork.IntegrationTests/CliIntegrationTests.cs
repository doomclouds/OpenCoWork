using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using OpenCoWork.App;
using Xunit;

namespace OpenCoWork.IntegrationTests;

public sealed class CliIntegrationTests
{
    [Fact]
    public async Task Version_is_stable_and_does_not_touch_workspace()
    {
        var root = CreateTemporaryDirectory();
        var metadata = Path.Combine(root, ".opencowork");
        Directory.CreateDirectory(metadata);
        var corruptConfig = Path.Combine(metadata, "config.jsonc");
        await File.WriteAllTextAsync(
            corruptConfig,
            "{ definitely-not-json",
            TestContext.Current.CancellationToken);
        var before = SnapshotFiles(root);

        try
        {
            var result = await InvokeAsync(["--version"], root);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal($"opencowork 0.1.0{Environment.NewLine}", result.StandardOutput);
            Assert.Equal(string.Empty, result.StandardError);
            Assert.Equal(before, SnapshotFiles(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Init_creates_workspace_without_starting_host()
    {
        var root = CreateTemporaryDirectory("with spaces");

        try
        {
            var result = await InvokeAsync(["init", "--workspace", root], root);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal($"Initialized OpenCoWork workspace: {root}{Environment.NewLine}", result.StandardOutput);
            Assert.Equal(string.Empty, result.StandardError);
            Assert.True(File.Exists(Path.Combine(root, ".opencowork", "config.jsonc")));
            Assert.True(File.Exists(Path.Combine(root, ".opencowork", "runtime", "state.db")));
            Assert.False(Directory.Exists(Path.Combine(root, ".opencowork", "runtime", "logs")));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Doctor_json_is_read_only_and_uses_one_stable_result_model()
    {
        var root = CreateTemporaryDirectory();

        try
        {
            Assert.Equal(
                0,
                (await InvokeAsync(["init", "--workspace", root], root)).ExitCode);
            await ConfigureModelsAsync(root);
            var before = SnapshotFiles(root);

            var result = await InvokeAsync(
                [
                    "doctor",
                    "--workspace",
                    root,
                    "--set",
                    "runtime.stopTimeout=20s",
                    "--set",
                    "operations.minimumLogLevel=\"warning\"",
                    "--json",
                ],
                root);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(string.Empty, result.StandardError);
            using var document = JsonDocument.Parse(result.StandardOutput);
            Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(
                "0.1.0",
                document.RootElement.GetProperty("product").GetProperty("productVersion").GetString());
            Assert.Equal(
                [
                    "runtime",
                    "platform",
                    "workspace",
                    "paths",
                    "configuration",
                    "sqlite",
                    "memory",
                    "trust",
                ],
                document.RootElement
                    .GetProperty("checks")
                    .EnumerateArray()
                    .Select(check => check.GetProperty("id").GetString()));
            Assert.Equal(before, SnapshotFiles(root));
            Assert.False(Directory.Exists(Path.Combine(root, ".opencowork", "runtime", "logs")));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Strict_config_and_parser_failures_use_stable_exit_codes()
    {
        var root = CreateTemporaryDirectory();

        try
        {
            Assert.Equal(
                0,
                (await InvokeAsync(["init", "/workspace", root], root)).ExitCode);
            await File.WriteAllTextAsync(
                Path.Combine(root, ".opencowork", "config.jsonc"),
                """{"futureSetting": true}""",
                TestContext.Current.CancellationToken);

            var strict = await InvokeAsync(
                ["doctor", "-w", root, "--strict-config", "--json"],
                root);
            var invalid = await InvokeAsync(["doctor", "--not-an-option"], root);

            Assert.Equal(1, strict.ExitCode);
            Assert.Equal(2, invalid.ExitCode);
            Assert.Contains("--not-an-option", invalid.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task No_command_shows_help_without_creating_workspace()
    {
        var root = CreateTemporaryDirectory();

        try
        {
            var result = await InvokeAsync([], root);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("Usage:", result.StandardOutput, StringComparison.Ordinal);
            Assert.Equal(string.Empty, result.StandardError);
            Assert.False(Directory.Exists(Path.Combine(root, ".opencowork")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Doctor_without_initialized_workspace_skips_dependent_checks_without_writes()
    {
        var root = CreateTemporaryDirectory();

        try
        {
            var result = await InvokeAsync(["doctor", "--json"], root);

            Assert.Equal(0, result.ExitCode);
            using var document = JsonDocument.Parse(result.StandardOutput);
            var checks = document.RootElement
                .GetProperty("checks")
                .EnumerateArray()
                .ToDictionary(
                    check => check.GetProperty("id").GetString()!,
                    check => check.GetProperty("status").GetString()!,
                    StringComparer.Ordinal);
            Assert.Equal("Warning", checks["workspace"]);
            Assert.Equal("Skipped", checks["paths"]);
            Assert.Equal("Skipped", checks["configuration"]);
            Assert.Equal("Skipped", checks["sqlite"]);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Doctor_models_a_corrupt_trust_store_as_a_failure_without_rewriting_it()
    {
        var root = CreateTemporaryDirectory();
        var trustDirectory = Path.Combine(
            root,
            "user-profile",
            ".opencowork",
            "trust");
        Directory.CreateDirectory(trustDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(trustDirectory, "decisions.json"),
            """{"schemaVersion": 1, "decisions": {}}""",
            TestContext.Current.CancellationToken);

        try
        {
            Assert.Equal(
                0,
                (await InvokeAsync(["init", "--workspace", root], root)).ExitCode);
            await ConfigureModelsAsync(root);
            var before = SnapshotFiles(root);

            var result = await InvokeAsync(
                ["doctor", "--workspace", root, "--json"],
                root);

            Assert.Equal(1, result.ExitCode);
            using var document = JsonDocument.Parse(result.StandardOutput);
            var trust = document.RootElement
                .GetProperty("checks")
                .EnumerateArray()
                .Single(check =>
                    check.GetProperty("id").GetString() == "trust");
            Assert.Equal("Failed", trust.GetProperty("status").GetString());
            Assert.Equal(before, SnapshotFiles(root));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Doctor_accepts_a_valid_safely_permissioned_trust_store()
    {
        var root = CreateTemporaryDirectory();
        var trustDirectory = Path.Combine(
            root,
            "user-profile",
            ".opencowork",
            "trust");
        Directory.CreateDirectory(trustDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(trustDirectory, "decisions.json"),
            """{"schemaVersion": 1, "decisions": []}""",
            TestContext.Current.CancellationToken);

        try
        {
            Assert.Equal(
                0,
                (await InvokeAsync(["init", "--workspace", root], root)).ExitCode);
            await ConfigureModelsAsync(root);

            var result = await InvokeAsync(
                ["doctor", "--workspace", root, "--json"],
                root);

            Assert.Equal(0, result.ExitCode);
            using var document = JsonDocument.Parse(result.StandardOutput);
            var trust = document.RootElement
                .GetProperty("checks")
                .EnumerateArray()
                .Single(check =>
                    check.GetProperty("id").GetString() == "trust");
            Assert.Equal("Passed", trust.GetProperty("status").GetString());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Doctor_enforces_platform_trust_permissions()
    {
        var root = CreateTemporaryDirectory();
        var trustDirectory = Path.Combine(
            root,
            "user-profile",
            ".opencowork",
            "trust");
        var trustPath = Path.Combine(trustDirectory, "decisions.json");
        Directory.CreateDirectory(trustDirectory);
        await File.WriteAllTextAsync(
            trustPath,
            """{"schemaVersion": 1, "decisions": []}""",
            TestContext.Current.CancellationToken);

        try
        {
            Assert.Equal(
                0,
                (await InvokeAsync(["init", "--workspace", root], root)).ExitCode);
            await ConfigureModelsAsync(root);

            if (OperatingSystem.IsWindows())
            {
                var writable = FileSystemAclExtensions.GetAccessControl(new FileInfo(trustPath));
                writable.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                    FileSystemRights.Write,
                    AccessControlType.Allow));
                FileSystemAclExtensions.SetAccessControl(new FileInfo(trustPath), writable);

                var failed = await InvokeAsync(
                    ["doctor", "--workspace", root, "--json"],
                    root);
                Assert.Equal(1, failed.ExitCode);
                Assert.Equal("Failed", ReadTrustStatus(failed.StandardOutput));

                File.Delete(trustPath);
                await File.WriteAllTextAsync(
                    trustPath,
                    """{"schemaVersion": 1, "decisions": []}""",
                    TestContext.Current.CancellationToken);
                File.SetAttributes(trustPath, File.GetAttributes(trustPath) | FileAttributes.ReadOnly);
                var warning = await InvokeAsync(
                    ["doctor", "--workspace", root, "--json"],
                    root);
                Assert.Equal(0, warning.ExitCode);
                Assert.Equal("Warning", ReadTrustStatus(warning.StandardOutput));
                return;
            }

            if (OperatingSystem.IsMacOS())
            {
                File.SetUnixFileMode(
                    trustPath,
                    UnixFileMode.UserRead |
                    UnixFileMode.UserWrite |
                    UnixFileMode.GroupWrite);
                var failed = await InvokeAsync(
                    ["doctor", "--workspace", root, "--json"],
                    root);
                Assert.Equal(1, failed.ExitCode);
                Assert.Equal("Failed", ReadTrustStatus(failed.StandardOutput));

                File.SetUnixFileMode(trustPath, UnixFileMode.UserRead);
                var warning = await InvokeAsync(
                    ["doctor", "--workspace", root, "--json"],
                    root);
                Assert.Equal(0, warning.ExitCode);
                Assert.Equal("Warning", ReadTrustStatus(warning.StandardOutput));
            }
        }
        finally
        {
            File.SetAttributes(trustPath, FileAttributes.Normal);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Doctor_rejects_an_active_sqlite_journal_without_touching_it()
    {
        var root = CreateTemporaryDirectory();

        try
        {
            Assert.Equal(
                0,
                (await InvokeAsync(["init", "--workspace", root], root)).ExitCode);
            await ConfigureModelsAsync(root);
            await File.WriteAllTextAsync(
                Path.Combine(root, ".opencowork", "runtime", "state.db-wal"),
                "active-journal-canary",
                TestContext.Current.CancellationToken);
            var before = SnapshotFiles(root);

            var result = await InvokeAsync(
                ["doctor", "--workspace", root, "--json"],
                root);

            Assert.Equal(1, result.ExitCode);
            using var document = JsonDocument.Parse(result.StandardOutput);
            var sqlite = document.RootElement
                .GetProperty("checks")
                .EnumerateArray()
                .Single(check =>
                    check.GetProperty("id").GetString() == "sqlite");
            Assert.Equal("Failed", sqlite.GetProperty("status").GetString());
            Assert.Equal(before, SnapshotFiles(root));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static Task ConfigureModelsAsync(string root) =>
        File.WriteAllTextAsync(
            Path.Combine(root, ".opencowork", "config.jsonc"),
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
            TestContext.Current.CancellationToken);

    private static async Task<CliResult> InvokeAsync(string[] args, string workingDirectory)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await OpenCoWorkCli.RunAsync(
            args,
            output,
            error,
            workingDirectory,
            Path.Combine(workingDirectory, "user-profile"),
            TestContext.Current.CancellationToken);
        return new CliResult(exitCode, output.ToString(), error.ToString());
    }

    private static string ReadTrustStatus(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement
            .GetProperty("checks")
            .EnumerateArray()
            .Single(check => check.GetProperty("id").GetString() == "trust")
            .GetProperty("status")
            .GetString()!;
    }

    private static string CreateTemporaryDirectory(string? suffix = null)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-cli-{Guid.NewGuid():N}{(suffix is null ? string.Empty : $"-{suffix}")}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string SnapshotFiles(string root)
    {
        var builder = new StringBuilder();
        foreach (var path in Directory
                     .EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            builder.Append(Path.GetRelativePath(root, path).Replace('\\', '/'));
            builder.Append('=');
            builder.Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private sealed record CliResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
