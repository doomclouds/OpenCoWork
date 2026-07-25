using OpenCoWork.Abstractions;
using OpenCoWork.Core.Configuration;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class ConfigurationPipelineTests
{
    private static readonly ConfigSectionDescriptor[] Descriptors =
    [
        new(
            "runtime",
            typeof(RuntimeConfig),
            static () => new RuntimeConfig(),
            """
            {"type":"object","properties":{"state":{"type":"object","properties":{"busyTimeout":{"type":"string","format":"duration"}},"required":["busyTimeout"],"additionalProperties":false},"stopTimeout":{"type":"string","format":"duration"}},"required":["state"],"additionalProperties":false}
            """),
        new(
            "operations",
            typeof(OperationsConfig),
            static () => new OperationsConfig(),
            """
            {"type":"object","properties":{"minimumLogLevel":{"type":"string","pattern":"^(trace|debug|information|warning|error|critical|none)$"}},"required":["minimumLogLevel"],"additionalProperties":false}
            """),
        new(
            "sample",
            typeof(SampleConfig),
            static () => new SampleConfig(),
            """
            {"type":"object","properties":{"items":{"type":"array","items":{"type":"string"}},"token":{"type":"string","x-opencowork-secret":true}},"required":["items","token"],"additionalProperties":false}
            """),
        new(
            "bounded",
            typeof(BoundedConfig),
            static () => new BoundedConfig(),
            """
            {"type":"object","properties":{"count":{"type":"integer","minimum":1,"maximum":10}},"required":["count"],"additionalProperties":false}
            """),
    ];

    [Fact]
    public void Load_applies_frozen_priority_and_tracks_the_winning_source()
    {
        using var files = new TempDirectory();
        var user = files.Write("user.jsonc", """{"runtime":{"stopTimeout":"25s"}}""");
        var workspace = files.Write(
            "workspace.jsonc",
            """
            {
              // JSONC comments and trailing commas are allowed.
              "runtime": {
                "stopTimeout": "20s",
                "state": { "busyTimeout": "11s", },
              },
            }
            """);
        var local = files.Write("local.jsonc", """{"runtime":{"stopTimeout":"15s"}}""");
        var explicitFile = files.Write("explicit.jsonc", """{"runtime":{"stopTimeout":"10s"}}""");

        var result = ConfigLoader.Load(new ConfigLoadRequest(Descriptors)
        {
            UserConfigPath = user,
            WorkspaceConfigPath = workspace,
            LocalConfigPath = local,
            ExplicitConfigPath = explicitFile,
            Environment = new Dictionary<string, string>
            {
                ["OPENCOWORK__runtime__stopTimeout"] = "9s",
            },
            SetOverrides =
            [
                "runtime.stopTimeout=8s",
                "runtime.stopTimeout=7s",
            ],
            DedicatedOptions = new Dictionary<string, string>
            {
                ["runtime.stopTimeout"] = "6s",
            },
        });

        Assert.True(result.Validation.IsValid);
        Assert.NotNull(result.Snapshot);
        Assert.Equal(
            TimeSpan.FromSeconds(6),
            result.Snapshot.GetRequiredSection<RuntimeConfig>().StopTimeout);
        Assert.Equal(
            TimeSpan.FromSeconds(11),
            result.Snapshot.GetRequiredSection<RuntimeConfig>().State.BusyTimeout);
        Assert.Equal(
            ConfigSourceKind.DedicatedOption,
            result.Snapshot.GetRequiredSource("runtime.stopTimeout").Kind);
    }

    [Fact]
    public void Load_rejects_wrong_case_invalid_duration_null_and_invalid_json_value()
    {
        var request = new ConfigLoadRequest(Descriptors)
        {
            Environment = new Dictionary<string, string>
            {
                ["OPENCOWORK__Runtime__stopTimeout"] = "30",
            },
            SetOverrides = ["""runtime.state.busyTimeout={"broken":"""],
        };

        var result = ConfigLoader.Load(request);

        Assert.False(result.Validation.IsValid);
        Assert.Contains(result.Validation.Diagnostics, item => item.Path == "Runtime.stopTimeout");
        Assert.Contains(
            result.Validation.Diagnostics,
            item => item.Message.Contains("合法 JSON", StringComparison.Ordinal));

        var nullResult = ConfigLoader.Load(new ConfigLoadRequest(Descriptors)
        {
            SetOverrides = ["runtime.stopTimeout=null"],
        });

        Assert.False(nullResult.Validation.IsValid);
        Assert.Contains(nullResult.Validation.Diagnostics, item => item.Path == "runtime.stopTimeout");
    }

    [Fact]
    public void Unknown_fields_share_one_diagnostic_and_strict_only_changes_severity()
    {
        using var files = new TempDirectory();
        var config = files.Write(
            "unknown.jsonc",
            """{"runtime":{"unknown":true}}""");
        var normal = ConfigLoader.Load(new ConfigLoadRequest(Descriptors)
        {
            WorkspaceConfigPath = config,
        });
        var strict = ConfigLoader.Load(new ConfigLoadRequest(Descriptors)
        {
            WorkspaceConfigPath = config,
            Strict = true,
        });

        var warning = Assert.Single(
            normal.Validation.Diagnostics,
            item => item.Path == "runtime.unknown");
        var error = Assert.Single(
            strict.Validation.Diagnostics,
            item => item.Path == "runtime.unknown");

        Assert.Equal(warning.Code, error.Code);
        Assert.Equal(OpenCoWorkDiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal(OpenCoWorkDiagnosticSeverity.Error, error.Severity);
        Assert.NotNull(normal.Snapshot);
        Assert.Null(strict.Snapshot);
    }

    [Fact]
    public void Snapshot_returns_defensive_sections_and_never_exposes_secret_values_as_sources()
    {
        const string canary = "m1-secret-canary-7f5977";
        var result = ConfigLoader.Load(new ConfigLoadRequest(Descriptors)
        {
            SetOverrides =
            [
                "sample.items=[\"one\",\"two\"]",
                $"sample.token={canary}",
            ],
        });

        Assert.True(result.Validation.IsValid);
        var first = result.Snapshot!.GetRequiredSection<SampleConfig>();
        first.Items[0] = "changed";
        var second = result.Snapshot.GetRequiredSection<SampleConfig>();

        Assert.Equal("one", second.Items[0]);
        Assert.DoesNotContain(
            canary,
            string.Join(
                '|',
                result.Snapshot.Sources.Select(pair => $"{pair.Key}:{pair.Value.SourceId}")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Arrays_replace_across_files_and_explicit_missing_files_or_empty_paths_fail()
    {
        using var files = new TempDirectory();
        var user = files.Write(
            "array-user.jsonc",
            """{"sample":{"items":["user"]}}""");
        var workspace = files.Write(
            "array-workspace.jsonc",
            """{"sample":{"items":["workspace"]}}""");
        var result = ConfigLoader.Load(new ConfigLoadRequest(Descriptors)
        {
            UserConfigPath = user,
            WorkspaceConfigPath = workspace,
        });

        Assert.True(result.Validation.IsValid);
        Assert.Equal(
            ["workspace"],
            result.Snapshot!.GetRequiredSection<SampleConfig>().Items);

        var invalid = ConfigLoader.Load(new ConfigLoadRequest(Descriptors)
        {
            ExplicitConfigPath = Path.Combine(files.Path, "missing.jsonc"),
            SetOverrides = ["runtime..stopTimeout=1s"],
        });

        Assert.False(invalid.Validation.IsValid);
        Assert.Contains(
            invalid.Validation.Diagnostics,
            item => item.Message.Contains("显式配置文件不存在", StringComparison.Ordinal));
        Assert.Contains(
            invalid.Validation.Diagnostics,
            item => item.Path == "runtime..stopTimeout");

        var range = ConfigLoader.Load(new ConfigLoadRequest(Descriptors)
        {
            SetOverrides = ["bounded.count=11"],
        });
        Assert.False(range.Validation.IsValid);
        Assert.Contains(
            range.Validation.Diagnostics,
            item => item.Path == "bounded.count");
    }

    public sealed record SampleConfig
    {
        public string[] Items { get; init; } = ["default"];

        [Secret]
        public string Token { get; init; } = "default-secret";
    }

    public sealed record BoundedConfig
    {
        public int Count { get; init; } = 5;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"opencowork-config-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Write(string name, string contents)
        {
            var path = System.IO.Path.Combine(Path, name);
            File.WriteAllText(path, contents);
            return path;
        }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
