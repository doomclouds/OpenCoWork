using OpenCoWork.Abstractions;
using OpenCoWork.Core.Configuration;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class ConfigurationPipelineTests
{
    private static readonly ConfigSectionDescriptor ModelsDescriptor = new(
        "models",
        typeof(ModelsConfig),
        static () => new ModelsConfig(),
        """
        {"type":"object","additionalProperties":true}
        """);

    private static readonly ConfigSectionDescriptor[] Descriptors =
    [
        new(
            "tools",
            typeof(ToolsConfig),
            static () => new ToolsConfig(),
            """
            {"type":"object","properties":{"effects":{"type":"object","properties":{"externalMutation":{"type":"string","enum":["allow","deny","requireApproval"]},"networkRead":{"type":"string","enum":["allow","deny","requireApproval"]},"processExecution":{"type":"string","enum":["allow","deny","requireApproval"]},"workspaceWrite":{"type":"string","enum":["allow","deny","requireApproval"]}},"required":["externalMutation","networkRead","processExecution","workspaceWrite"],"additionalProperties":false}},"required":["effects"],"additionalProperties":false}
            """),
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
    public void Tool_effect_policies_use_safe_defaults_and_workspace_can_only_narrow_user_policy()
    {
        var defaults = ConfigLoader.Load(new ConfigLoadRequest(Descriptors));

        Assert.True(defaults.Validation.IsValid);
        var effects = defaults.Snapshot!.GetRequiredSection<ToolsConfig>().Effects;
        Assert.Equal(ToolAuthorityDecision.RequireApproval, effects.NetworkRead);
        Assert.Equal(ToolAuthorityDecision.RequireApproval, effects.WorkspaceWrite);
        Assert.Equal(ToolAuthorityDecision.RequireApproval, effects.ProcessExecution);
        Assert.Equal(ToolAuthorityDecision.RequireApproval, effects.ExternalMutation);

        using var files = new TempDirectory();
        var user = files.Write(
            "user-tools.jsonc",
            """{"tools":{"effects":{"networkRead":"allow"}}}""");
        var workspace = files.Write(
            "workspace-tools.jsonc",
            """{"tools":{"effects":{"networkRead":"requireApproval"}}}""");
        var narrowed = ConfigLoader.Load(new ConfigLoadRequest(Descriptors)
        {
            UserConfigPath = user,
            WorkspaceConfigPath = workspace,
        });

        Assert.True(narrowed.Validation.IsValid);
        Assert.Equal(
            ToolAuthorityDecision.RequireApproval,
            narrowed.Snapshot!.GetRequiredSection<ToolsConfig>().Effects.NetworkRead);

        var deniedUser = files.Write(
            "denied-user-tools.jsonc",
            """{"tools":{"effects":{"networkRead":"deny"}}}""");
        var widenedWorkspace = files.Write(
            "widened-workspace-tools.jsonc",
            """{"tools":{"effects":{"networkRead":"allow"}}}""");
        var widened = ConfigLoader.Load(new ConfigLoadRequest(Descriptors)
        {
            UserConfigPath = deniedUser,
            WorkspaceConfigPath = widenedWorkspace,
        });

        Assert.False(widened.Validation.IsValid);
        Assert.Contains(
            widened.Validation.Diagnostics,
            item => item.Path == "tools.effects.networkRead");

        var invalidExternal = ConfigLoader.Load(new ConfigLoadRequest(Descriptors)
        {
            SetOverrides = ["tools.effects.externalMutation=allow"],
        });
        Assert.False(invalidExternal.Validation.IsValid);
        Assert.Contains(
            invalidExternal.Validation.Diagnostics,
            item => item.Message.Contains("ExternalMutation", StringComparison.Ordinal));
    }

    [Fact]
    public void Models_configuration_uses_exact_named_ids_and_rejects_unsafe_endpoints()
    {
        using var files = new TempDirectory();
        var valid = files.Write(
            "models.jsonc",
            """
            {
              "models": {
                "defaultProvider": "token-plan",
                "defaultModel": "qwen3.8-max-preview",
                "providers": {
                  "token-plan": {
                    "baseUrl": "https://token-plan.example/compatible-mode/v1",
                    "apiKey": { "environment": "QWEN_TOKEN_PLAN_API_KEY" },
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
            """);

        var loaded = ConfigLoader.Load(new ConfigLoadRequest([ModelsDescriptor])
        {
            WorkspaceConfigPath = valid,
        });

        Assert.True(
            loaded.Validation.IsValid,
            string.Join(
                Environment.NewLine,
                loaded.Validation.Diagnostics.Select(item =>
                    $"{item.Code}:{item.Path}:{item.Message}")));
        var models = loaded.Snapshot!.GetRequiredSection<ModelsConfig>();
        Assert.Equal(
            "qwen3.8-max-preview",
            models.Providers["token-plan"].Models.Keys.Single());

        var invalid = files.Write(
            "unsafe.jsonc",
            File.ReadAllText(valid).Replace(
                "https://token-plan.example/compatible-mode/v1",
                "http://token-plan.example/compatible-mode/v1",
                StringComparison.Ordinal));
        var rejected = ConfigLoader.Load(new ConfigLoadRequest([ModelsDescriptor])
        {
            WorkspaceConfigPath = invalid,
        });
        Assert.False(rejected.Validation.IsValid);
        Assert.Contains(
            rejected.Validation.Diagnostics,
            item => item.Message.Contains("HTTPS", StringComparison.Ordinal));

        var drifted = files.Write(
            "drifted.jsonc",
            File.ReadAllText(valid).Replace(
                "\"contextWindowTokens\": 983616",
                "\"contextWindowTokens\": 983615",
                StringComparison.Ordinal));
        var driftRejected = ConfigLoader.Load(new ConfigLoadRequest([ModelsDescriptor])
        {
            WorkspaceConfigPath = drifted,
        });
        Assert.False(driftRejected.Validation.IsValid);
        Assert.Contains(
            driftRejected.Validation.Diagnostics,
            item => item.Message.Contains(
                "Tokenizer Profile",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Provider_credentials_are_captured_once_and_only_selected_missing_secret_fails()
    {
        const string frozenSecret = "provider-secret-6548d2";
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TOKEN_PLAN_KEY"] = frozenSecret,
        };
        var models = new ModelsConfig
        {
            DefaultProvider = "token-plan",
            DefaultModel = "qwen3.8-max-preview",
            Providers = new Dictionary<string, ProviderConfig>(StringComparer.Ordinal)
            {
                ["token-plan"] = Provider("TOKEN_PLAN_KEY"),
                ["deepseek"] = Provider("DEEPSEEK_KEY"),
            },
        };

        var credentials = FrozenProviderCredentials.Capture(
            models,
            name => environment.GetValueOrDefault(name));
        environment["TOKEN_PLAN_KEY"] = "rotated-secret";

        Assert.Equal(frozenSecret, credentials.GetRequired("token-plan"));
        Assert.Throws<InvalidOperationException>(
            () => credentials.GetRequired("deepseek"));
    }

    private static ProviderConfig Provider(string environmentVariable) =>
        new()
        {
            BaseUrl = "https://example.test/v1",
            ApiKey = new ProviderApiKeyConfig
            {
                Environment = environmentVariable,
            },
            Models = new Dictionary<string, ModelConfig>(StringComparer.Ordinal)
            {
                ["qwen3.8-max-preview"] = new()
                {
                    TokenizerProfileId = "qwen-o200k",
                    TokenizerProfileVersion = "1",
                    ContextWindowTokens = 983_616,
                    MaxOutputTokens = 131_072,
                },
            },
        };

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
