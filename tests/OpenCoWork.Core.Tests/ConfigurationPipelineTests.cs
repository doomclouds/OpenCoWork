using OpenCoWork.Abstractions;
using OpenCoWork.Automations;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Teams;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class ConfigurationPipelineTests
{
    private static readonly ConfigSectionDescriptor ModelsDescriptor = new(
        "models",
        typeof(ModelsConfig),
        static () => new ModelsConfig(),
        """
        {"type":"object","properties":{"defaultModel":{"type":"string"},"reasoningEffort":{"type":"string"}},"required":["defaultModel","reasoningEffort"],"additionalProperties":false}
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
        new(
            "automations",
            typeof(AutomationsConfig),
            static () => new AutomationsConfig(),
            """
            {"type":"object","properties":{"enabled":{"type":"boolean"},"maxConcurrentRuns":{"type":"integer","minimum":1,"maximum":16},"maximumAttentionTimeout":{"type":"string","format":"duration"},"maximumRunTimeout":{"type":"string","format":"duration"}},"additionalProperties":false}
            """),
        new(
            "gateway",
            typeof(GatewayConfig),
            static () => new GatewayConfig(),
            """
            {"type":"object","properties":{"channels":{"type":"array","items":{"type":"object"}},"listenPort":{"type":"integer","minimum":1,"maximum":65535}},"additionalProperties":false}
            """),
        new(
            "teams",
            typeof(CoWorkConfig),
            static () => new CoWorkConfig(),
            """
            {"type":"object","properties":{"dispatchLease":{"type":"string","format":"duration"},"leaseRenewalInterval":{"type":"string","format":"duration"},"maxConcurrentAgentRuns":{"type":"integer","minimum":1,"maximum":64},"maxConcurrentAgentRunsPerMission":{"type":"integer","minimum":1,"maximum":16},"maxDepth":{"type":"integer","minimum":1,"maximum":4},"maximumArtifactBytes":{"type":"integer","minimum":1,"maximum":67108864},"maximumDispatchAttempts":{"type":"integer","minimum":5,"maximum":5},"maximumMailboxMessageBytes":{"type":"integer","minimum":1,"maximum":65536},"maximumMembersPerMission":{"type":"integer","minimum":1,"maximum":16},"maximumOwnedFileBytes":{"type":"integer","minimum":1,"maximum":536870912},"maximumTasksPerMission":{"type":"integer","minimum":1,"maximum":256}},"additionalProperties":false}
            """),
    ];

    [Fact]
    public void Gateway_configuration_is_strict_bounded_and_disabled_without_channels()
    {
        var defaults = ConfigLoader.Load(new ConfigLoadRequest(Descriptors));

        Assert.True(defaults.Validation.IsValid);
        var gateway = defaults.Snapshot!.GetRequiredSection<GatewayConfig>();
        Assert.Equal(9_200, gateway.ListenPort);
        Assert.Empty(gateway.Channels);

        using var files = new TempDirectory();
        var valid = files.Write(
            "gateway.jsonc",
            """
            {
              "gateway": {
                "listenPort": 9443,
                "channels": [{
                  "id": "build-bot",
                  "kind": "webhook",
                  "enabled": true,
                  "callbackUrl": "https://example.test/opencowork/result",
                  "credential": { "source": "environment", "environmentVariable": "BUILD_BOT_SECRET" },
                  "maxConcurrentSends": 4,
                  "minimumSendIntervalMs": 250
                }]
              }
            }
            """);
        var loaded = ConfigLoader.Load(new ConfigLoadRequest(Descriptors)
        {
            WorkspaceConfigPath = valid,
        });

        Assert.True(
            loaded.Validation.IsValid,
            string.Join(
                Environment.NewLine,
                loaded.Validation.Diagnostics.Select(item =>
                    $"{item.Code}:{item.Path}:{item.Message}")));
        var channel = Assert.Single(
            loaded.Snapshot!.GetRequiredSection<GatewayConfig>().Channels);
        Assert.Equal("build-bot", channel.Id);
        Assert.Equal(GatewayCredentialSource.Environment, channel.Credential.Source);
        Assert.Equal("BUILD_BOT_SECRET", channel.Credential.EnvironmentVariable);
        Assert.Equal(250, channel.MinimumSendIntervalMs);
        var configurationSha256 = GatewayConfig.ComputeChannelSha256(channel);
        Assert.Equal(64, configurationSha256.Length);
        Assert.Equal(
            configurationSha256,
            GatewayConfig.ComputeChannelSha256(channel with { }));
        Assert.NotEqual(
            configurationSha256,
            GatewayConfig.ComputeChannelSha256(
                channel with
                {
                    CallbackUrl = "https://example.test/changed",
                }));

        var invalid = files.Write(
            "invalid-gateway.jsonc",
            """
            {
              "gateway": {
                "listenPort": 9200,
                "channels": [
                  {
                    "id": "Build_Bot",
                    "kind": "future",
                    "callbackUrl": "http://example.test/result",
                    "credential": { "source": "environment" },
                    "maxConcurrentSends": 17,
                    "minimumSendIntervalMs": 60001
                  },
                  {
                    "id": "Build_Bot",
                    "kind": "webhook",
                    "callbackUrl": "https://example.test/result",
                    "credential": { "source": "osSecretStore" }
                  }
                ]
              }
            }
            """);
        var rejected = ConfigLoader.Load(new ConfigLoadRequest(Descriptors)
        {
            WorkspaceConfigPath = invalid,
        });

        Assert.False(rejected.Validation.IsValid);
        Assert.Contains(
            rejected.Validation.Diagnostics,
            item => item.Path == "gateway" &&
                    item.Message.Contains("Channel", StringComparison.Ordinal));

        var invalidPort = ConfigLoader.Load(new ConfigLoadRequest(Descriptors)
        {
            SetOverrides = ["gateway.listenPort=0"],
        });
        Assert.False(invalidPort.Validation.IsValid);
        Assert.Contains(
            invalidPort.Validation.Diagnostics,
            item => item.Path == "gateway.listenPort");
    }

    [Fact]
    public void Automations_configuration_is_disabled_by_default_and_enforces_workspace_caps()
    {
        var defaults = ConfigLoader.Load(new ConfigLoadRequest(Descriptors));

        Assert.True(defaults.Validation.IsValid);
        var automations = defaults.Snapshot!.GetRequiredSection<AutomationsConfig>();
        Assert.False(automations.Enabled);
        Assert.Equal(3, automations.MaxConcurrentRuns);
        Assert.Equal(TimeSpan.FromMinutes(30), automations.MaximumRunTimeout);
        Assert.Equal(TimeSpan.FromHours(24), automations.MaximumAttentionTimeout);

        using var files = new TempDirectory();
        var invalid = files.Write(
            "invalid-automations.jsonc",
            """
            {
              "automations": {
                "maxConcurrentRuns": 17
              }
            }
            """);
        var loaded = ConfigLoader.Load(new ConfigLoadRequest(Descriptors)
        {
            WorkspaceConfigPath = invalid,
        });

        Assert.False(loaded.Validation.IsValid);
        Assert.Contains(
            loaded.Validation.Diagnostics,
            diagnostic =>
                diagnostic.Code == "OCWCFG007" &&
                diagnostic.Path == "automations.maxConcurrentRuns");

        var invalidDurations = files.Write(
            "invalid-automation-durations.jsonc",
            """
            {
              "automations": {
                "maximumRunTimeout": "25h",
                "maximumAttentionTimeout": "169h"
              }
            }
            """);
        loaded = ConfigLoader.Load(new ConfigLoadRequest(Descriptors)
        {
            WorkspaceConfigPath = invalidDurations,
        });

        Assert.False(loaded.Validation.IsValid);
        Assert.Contains(
            loaded.Validation.Diagnostics,
            diagnostic =>
                diagnostic.Code == "OCWCFG008" &&
                diagnostic.Path == "automations" &&
                diagnostic.Message.StartsWith(
                    nameof(AutomationsConfig.MaximumRunTimeout),
                    StringComparison.Ordinal));
        Assert.Contains(
            loaded.Validation.Diagnostics,
            diagnostic =>
                diagnostic.Code == "OCWCFG008" &&
                diagnostic.Path == "automations" &&
                diagnostic.Message.StartsWith(
                    nameof(AutomationsConfig.MaximumAttentionTimeout),
                    StringComparison.Ordinal));
    }

    [Fact]
    public void Teams_configuration_uses_safe_defaults_and_enforces_hard_limits()
    {
        var defaults = ConfigLoader.Load(new ConfigLoadRequest(Descriptors));

        Assert.True(defaults.Validation.IsValid);
        var teams = defaults.Snapshot!.GetRequiredSection<CoWorkConfig>();
        Assert.Equal(1, teams.MaxDepth);
        Assert.Equal(16, teams.MaxConcurrentAgentRuns);
        Assert.Equal(4, teams.MaxConcurrentAgentRunsPerMission);
        Assert.Equal(TimeSpan.FromMinutes(2), teams.DispatchLease);

        using var files = new TempDirectory();
        var invalid = files.Write(
            "invalid-teams.jsonc",
            """
            {
              "teams": {
                "maximumMembersPerMission": 17
              }
            }
            """);
        var loaded = ConfigLoader.Load(new ConfigLoadRequest(Descriptors)
        {
            WorkspaceConfigPath = invalid,
        });

        Assert.False(loaded.Validation.IsValid);
        Assert.Contains(
            loaded.Validation.Diagnostics,
            diagnostic => diagnostic.Path?.Contains(
                "maximumMembersPerMission",
                StringComparison.OrdinalIgnoreCase) == true);

        var inconsistent = files.Write(
            "inconsistent-teams.jsonc",
            """
            {
              "teams": {
                "maxConcurrentAgentRuns": 4,
                "maxConcurrentAgentRunsPerMission": 5,
                "dispatchLease": "10s",
                "leaseRenewalInterval": "10s"
              }
            }
            """);
        var inconsistentLoaded = ConfigLoader.Load(new ConfigLoadRequest(Descriptors)
        {
            WorkspaceConfigPath = inconsistent,
        });
        Assert.False(inconsistentLoaded.Validation.IsValid);
        Assert.Contains(
            inconsistentLoaded.Validation.Diagnostics,
            diagnostic => diagnostic.Message.Contains(
                "MaxConcurrentAgentRunsPerMission",
                StringComparison.Ordinal));
        Assert.Contains(
            inconsistentLoaded.Validation.Diagnostics,
            diagnostic => diagnostic.Message.Contains(
                "LeaseRenewalInterval",
                StringComparison.Ordinal));
    }

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
    public void Models_configuration_only_accepts_DeepSeek_Flash_and_reasoning_effort()
    {
        var defaults = ConfigLoader.Load(new ConfigLoadRequest([ModelsDescriptor]));

        Assert.True(defaults.Validation.IsValid);
        var defaultModels = defaults.Snapshot!.GetRequiredSection<ModelsConfig>();
        Assert.Equal("deepseek-v4-flash", defaultModels.DefaultModel);
        Assert.Equal("high", defaultModels.ReasoningEffort);

        using var files = new TempDirectory();
        var valid = files.Write(
            "models.jsonc",
            """
            {
              "models": {
                "defaultModel": "deepseek-v4-flash",
                "reasoningEffort": "max"
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
        Assert.Equal("deepseek-v4-flash", models.DefaultModel);
        Assert.Equal("max", models.ReasoningEffort);

        var invalid = files.Write(
            "invalid-effort.jsonc",
            """{"models":{"reasoningEffort":"medium"}}""");
        var rejected = ConfigLoader.Load(new ConfigLoadRequest([ModelsDescriptor])
        {
            WorkspaceConfigPath = invalid,
        });
        Assert.False(rejected.Validation.IsValid);
        Assert.Contains(
            rejected.Validation.Diagnostics,
            item => item.Path == "models" &&
                    item.Message.Contains("reasoning", StringComparison.OrdinalIgnoreCase));

        var unsupported = files.Write(
            "unsupported-model.jsonc",
            """{"models":{"defaultModel":"deepseek-v4-pro"}}""");
        var unsupportedResult = ConfigLoader.Load(new ConfigLoadRequest([ModelsDescriptor])
        {
            WorkspaceConfigPath = unsupported,
        });
        Assert.False(unsupportedResult.Validation.IsValid);
        Assert.Contains(
            unsupportedResult.Validation.Diagnostics,
            item => item.Path == "models" &&
                    item.Message.Contains("deepseek-v4-flash", StringComparison.Ordinal));
    }

    [Fact]
    public void Legacy_provider_configuration_is_a_migration_error_even_without_strict_mode()
    {
        using var files = new TempDirectory();
        var legacy = files.Write(
            "legacy-models.jsonc",
            """
            {
              "models": {
                "defaultProvider": "token-plan",
                "providers": { "token-plan": { "baseUrl": "https://example.test" } }
              }
            }
            """);

        var loaded = ConfigLoader.Load(new ConfigLoadRequest([ModelsDescriptor])
        {
            WorkspaceConfigPath = legacy,
            Strict = false,
        });

        Assert.False(loaded.Validation.IsValid);
        Assert.Null(loaded.Snapshot);
        var diagnostics = loaded.Validation.Diagnostics.Where(item =>
            item.Path is "models.defaultProvider" or "models.providers").ToArray();
        Assert.Equal(2, diagnostics.Length);
        Assert.All(
            diagnostics,
            item => Assert.Equal("OCWCFG010", item.Code));
    }

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
