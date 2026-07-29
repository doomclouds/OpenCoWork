using System.Text.Json;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Tools;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class ToolSnapshotTests
{
    [Fact]
    public void Core_snapshot_is_deterministic_and_matches_the_frozen_tool_surface()
    {
        var runtime = new ToolRuntime();

        var first = runtime.BuildSnapshot(AgentMode.Agent, new ToolsConfig());
        var second = runtime.BuildSnapshot(AgentMode.Agent, new ToolsConfig());

        Assert.Equal(first.SnapshotSha256, second.SnapshotSha256);
        Assert.Equal(2, first.SchemaVersion);
        Assert.Equal(
            [
                "file.list",
                "file.read",
                "file.write",
                "shell.run",
                "skill.load",
                "source_control.diff",
                "source_control.log",
                "source_control.show",
                "source_control.status",
                "tool.search",
                "web.fetch",
            ],
            first.Registrations.Select(CanonicalName));
        Assert.Equal(
            [
                ToolReplaySafety.Safe,
                ToolReplaySafety.Safe,
                ToolReplaySafety.Unsafe,
                ToolReplaySafety.Unsafe,
                ToolReplaySafety.Safe,
                ToolReplaySafety.Safe,
                ToolReplaySafety.Safe,
                ToolReplaySafety.Safe,
                ToolReplaySafety.Safe,
                ToolReplaySafety.Safe,
                ToolReplaySafety.Unsafe,
            ],
            first.Registrations.Select(item => item.Definition.ReplaySafety));
        Assert.Equal(
            [
                ToolEffect.WorkspaceRead,
                ToolEffect.WorkspaceRead,
                ToolEffect.WorkspaceRead | ToolEffect.WorkspaceWrite,
                ToolEffect.WorkspaceRead |
                ToolEffect.WorkspaceWrite |
                ToolEffect.ProcessExecution |
                ToolEffect.NetworkRead |
                ToolEffect.ExternalMutation,
                ToolEffect.None,
                ToolEffect.WorkspaceRead | ToolEffect.ProcessExecution,
                ToolEffect.WorkspaceRead | ToolEffect.ProcessExecution,
                ToolEffect.WorkspaceRead | ToolEffect.ProcessExecution,
                ToolEffect.WorkspaceRead | ToolEffect.ProcessExecution,
                ToolEffect.None,
                ToolEffect.NetworkRead,
            ],
            first.Registrations.Select(item => item.Definition.Effects));
        Assert.All(
            first.Registrations,
            item =>
            {
                Assert.Equal(ToolSourceKind.CoreNative, item.Definition.Id.SourceKind);
                Assert.Equal("opencowork.core", item.Definition.Id.SourceId);
                Assert.Equal(ToolExposure.Direct, item.Exposure);
                Assert.Equal(
                    item.Definition.Name.Namespace == "source_control"
                        ? ToolInvocationAudience.Model | ToolInvocationAudience.Host
                        : ToolInvocationAudience.Model,
                    item.Audience);
            });
        Assert.Equal(
            [
                "file__list",
                "file__read",
                "file__write",
                "shell__run",
                "skill__load",
                "source_control__diff",
                "source_control__log",
                "source_control__show",
                "source_control__status",
                "tool__search",
                "web__fetch",
            ],
            first.Registrations.Select(item =>
                first.CanonicalToProviderNames[CanonicalName(item)]));
        Assert.Equal(
            first.CanonicalToProviderNames.OrderBy(pair => pair.Key),
            first.ProviderToCanonicalNames
                .Select(pair => new KeyValuePair<string, string>(pair.Value, pair.Key))
                .OrderBy(pair => pair.Key));
        Assert.Equal(64, first.SnapshotSha256.Length);
        Assert.All(
            first.SnapshotSha256,
            character => Assert.True(
                character is >= '0' and <= '9' or >= 'a' and <= 'f'));

        Assert.Equal(
            [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(2)],
            new[]
            {
                RequiredBinding(runtime, "core.file.list.v1").DefaultTimeout,
                RequiredBinding(runtime, "core.shell.run.v1").DefaultTimeout,
                RequiredBinding(runtime, "core.web.fetch.v1").DefaultTimeout,
            });
        Assert.Equal(
            first.Registrations.Count,
            runtime.CreateProviderDefinitions(first).Count);

        using var valid = JsonDocument.Parse("""{"path":"src"}""");
        using var invalid = JsonDocument.Parse("""{"path":1}""");
        Assert.True(runtime.ValidateArguments(
            first.Registrations[0].Definition.Id,
            valid.RootElement));
        Assert.False(runtime.ValidateArguments(
            first.Registrations[0].Definition.Id,
            invalid.RootElement));
    }

    [Fact]
    public async Task Parallel_registries_do_not_share_schema_registration_state()
    {
        var snapshots = await Task.WhenAll(
            Enumerable.Range(0, 16).Select(_ => Task.Run(
                () => new ToolRuntime().BuildSnapshot(
                    AgentMode.Agent,
                    new ToolsConfig()),
                TestContext.Current.CancellationToken)));

        Assert.All(snapshots, snapshot => Assert.Equal(11, snapshot.Registrations.Count));
        Assert.Single(snapshots.Select(snapshot => snapshot.SnapshotSha256).Distinct());
    }

    [Fact]
    public void Plan_mode_and_authority_can_only_narrow_the_snapshot()
    {
        var runtime = new ToolRuntime();

        var plan = runtime.BuildSnapshot(AgentMode.Plan, new ToolsConfig());
        var networkDenied = runtime.BuildSnapshot(
            AgentMode.Agent,
            new ToolsConfig
            {
                Effects = new ToolEffectPoliciesConfig
                {
                    NetworkRead = ToolAuthorityDecision.Deny,
                },
            });
        var invalidExternalAllow = runtime.BuildSnapshot(
            AgentMode.Agent,
            new ToolsConfig
            {
                Effects = new ToolEffectPoliciesConfig
                {
                    ExternalMutation = ToolAuthorityDecision.Allow,
                },
            });

        Assert.Equal(
            ["file.list", "file.read", "skill.load", "tool.search", "web.fetch"],
            plan.Registrations.Select(CanonicalName));
        Assert.Equal(
            [
                "file.list",
                "file.read",
                "file.write",
                "skill.load",
                "source_control.diff",
                "source_control.log",
                "source_control.show",
                "source_control.status",
                "tool.search",
            ],
            networkDenied.Registrations.Select(CanonicalName));
        Assert.Equal(
            ToolAuthorityDecision.RequireApproval,
            invalidExternalAllow.Authority.Single(
                item => item.Effect == ToolEffect.ExternalMutation).Decision);
        Assert.Contains(
            plan.Diagnostics,
            item => item.Code == ToolErrorCodes.ModeDenied);
        Assert.Contains(
            networkDenied.Diagnostics,
            item => item.Code == ToolErrorCodes.AuthorityDenied);
    }

    [Fact]
    public void External_source_and_binding_generation_are_frozen_into_the_snapshot()
    {
        var generationThree = Registration(
            "status",
            new ToolName("acme_git", "status"),
            ValidSchema(),
            sourceKind: ToolSourceKind.PluginNative,
            bindingGeneration: 3);
        var generationFour = generationThree with { BindingGeneration = 4 };

        var first = new ToolRuntime([generationThree]).BuildSnapshot(
            AgentMode.Agent,
            new ToolsConfig());
        var second = new ToolRuntime([generationFour]).BuildSnapshot(
            AgentMode.Agent,
            new ToolsConfig());

        var frozen = Assert.Single(first.Registrations);
        Assert.Equal(ToolSourceKind.PluginNative, frozen.Definition.Id.SourceKind);
        Assert.Equal(3, frozen.BindingGeneration);
        Assert.NotEqual(first.SnapshotSha256, second.SnapshotSha256);
    }

    [Fact]
    public void Schema_and_name_failures_are_isolated_independent_of_registration_order()
    {
        var registrations = new[]
        {
            Registration(
                "provider-a",
                new ToolName("a", "b__c"),
                ValidSchema()),
            Registration(
                "provider-b",
                new ToolName("a__b", "c"),
                ValidSchema()),
            Registration(
                "duplicate-a",
                new ToolName("duplicate", "name"),
                ValidSchema()),
            Registration(
                "duplicate-b",
                new ToolName("duplicate", "name"),
                ValidSchema()),
            Registration(
                "external-ref",
                new ToolName("invalid", "external_ref"),
                """
                {
                  "$schema":"https://json-schema.org/draft/2020-12/schema",
                  "type":"object",
                  "properties":{"value":{"$ref":"https://example.test/schema"}},
                  "additionalProperties":false
                }
                """),
            Registration(
                "wrong-dialect",
                new ToolName("invalid", "dialect"),
                """
                {
                  "$schema":"http://json-schema.org/draft-07/schema#",
                  "type":"object",
                  "additionalProperties":false
                }
                """),
            Registration(
                "nested-dialect",
                new ToolName("invalid", "nested_dialect"),
                """
                {
                  "$schema":"https://json-schema.org/draft/2020-12/schema",
                  "type":"object",
                  "properties":{
                    "value":{
                      "$schema":"http://json-schema.org/draft-07/schema#",
                      "type":"string"
                    }
                  },
                  "additionalProperties":false
                }
                """),
            Registration(
                "unknown-keyword",
                new ToolName("invalid", "keyword"),
                """
                {
                  "$schema":"https://json-schema.org/draft/2020-12/schema",
                  "type":"object",
                  "madeUpKeyword":true,
                  "additionalProperties":false
                }
                """),
            Registration(
                "duplicate-id",
                new ToolName("identity", "valid"),
                ValidSchema()),
            Registration(
                "duplicate-id",
                new ToolName("identity", "invalid"),
                """
                {
                  "$schema":"https://json-schema.org/draft/2020-12/schema",
                  "type":"object",
                  "madeUpKeyword":true,
                  "additionalProperties":false
                }
                """),
        };
        var first = new ToolRuntime(registrations).BuildSnapshot(
            AgentMode.Agent,
            new ToolsConfig());
        var second = new ToolRuntime(registrations.Reverse()).BuildSnapshot(
            AgentMode.Agent,
            new ToolsConfig());

        Assert.Equal(first.SnapshotSha256, second.SnapshotSha256);
        Assert.Equal(["a.b__c", "a__b.c"], first.Registrations.Select(CanonicalName));
        Assert.All(
            first.CanonicalToProviderNames.Values,
            name => Assert.Matches("^[a-z0-9_]{1,30}__[0-9a-f]{32}$", name));
        Assert.Equal(8, first.Diagnostics.Count);
        Assert.Equal(
            4,
            first.Diagnostics.Count(item => item.Code == ToolErrorCodes.NameConflict));
        Assert.Equal(
            4,
            first.Diagnostics.Count(item => item.Code == ToolErrorCodes.DefinitionInvalid));
    }

    [Fact]
    public void Oversized_snapshot_fails_before_provider_use()
    {
        var registration = Registration(
            "oversized",
            new ToolName("large", "description"),
            ValidSchema(),
            new string('x', ToolRuntimeLimits.MaximumSnapshotBytes));
        var runtime = new ToolRuntime([registration]);

        var error = Assert.Throws<ToolRuntimeException>(
            () => runtime.BuildSnapshot(AgentMode.Agent, new ToolsConfig()));

        Assert.Equal(ToolErrorCodes.SnapshotTooLarge, error.Code);
    }

    private static ToolRuntimeBinding RequiredBinding(ToolRuntime runtime, string value)
    {
        Assert.True(
            runtime.TryResolveBinding(new RuntimeBindingId(value), out var binding));
        return binding!;
    }

    private static ToolRegistration Registration(
        string sourceToolId,
        ToolName name,
        string schema,
        string description = "test",
        ToolSourceKind sourceKind = ToolSourceKind.CoreNative,
        long bindingGeneration = 1)
    {
        using var document = JsonDocument.Parse(schema);
        return new ToolRegistration(
            new ToolDefinition(
                new ToolDefinitionId(
                    sourceKind,
                    sourceKind == ToolSourceKind.CoreNative
                        ? "opencowork.core"
                        : "acme/git",
                    sourceToolId),
                name,
                description,
                document.RootElement,
                ToolEffect.None),
            new RuntimeBindingId($"test.{sourceToolId}"),
            ToolExposure.Direct,
            ToolInvocationAudience.Model,
            bindingGeneration);
    }

    private static string ValidSchema() =>
        """
        {
          "$schema":"https://json-schema.org/draft/2020-12/schema",
          "type":"object",
          "additionalProperties":false
        }
        """;

    private static string CanonicalName(ToolRegistration registration) =>
        $"{registration.Definition.Name.Namespace}.{registration.Definition.Name.Name}";
}
