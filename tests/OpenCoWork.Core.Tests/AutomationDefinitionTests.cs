using System.Text;
using System.Text.Json;
using OpenCoWork.Abstractions;
using OpenCoWork.Automations;
using OpenCoWork.Core.Tools;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class AutomationDefinitionTests
{
    [Fact]
    public void Input_schema_is_draft_2020_12_and_closed_to_external_references()
    {
        var validator = new JsonSchemaValidationService();
        using var supported = JsonDocument.Parse(
            """{"type":"object","additionalProperties":false}""");
        using var external = JsonDocument.Parse(
            """{"type":"object","properties":{"value":{"$ref":"https://example.invalid/schema"}}}""");

        Assert.True(validator.IsValidSchema(supported.RootElement));
        Assert.False(validator.IsValidSchema(external.RootElement));
    }

    [Fact]
    public void Yaml_input_schema_is_normalized_as_json()
    {
        var capture = new CapturingSchemaService();
        var result = new AutomationDefinitionLoader(capture, new NoSensitiveDataService())
            .Load(
                "nightly-maintenance.yaml",
                Encoding.UTF8.GetBytes(ValidYaml()));

        Assert.True(result.IsValid);
        Assert.Equal(
            """{"type":"object","properties":{"task":{"type":"string"}},"required":["task"],"additionalProperties":false}""",
            capture.Schema.GetRawText());
    }

    [Fact]
    public void Valid_definition_is_normalized_and_formatting_does_not_change_version()
    {
        var loader = CreateLoader();
        var compact = loader.Load(
            "nightly-maintenance.yaml",
            Encoding.UTF8.GetBytes(ValidYaml()));
        var formatted = loader.Load(
            "nightly-maintenance.yaml",
            Encoding.UTF8.GetBytes(
                "# comment\n" + ValidYaml()
                    .Replace("enabled: true", "enabled:    true")
                    .Replace("displayName: Nightly Maintenance",
                        "displayName: \"Nightly Maintenance\"")));

        Assert.True(
            compact.IsValid,
            string.Join(" | ", compact.Diagnostics.Select(x => $"{x.Code}:{x.Path}")));
        Assert.True(
            formatted.IsValid,
            string.Join(" | ", formatted.Diagnostics.Select(x => $"{x.Code}:{x.Path}")));
        Assert.NotNull(compact.Definition);
        Assert.Equal("nightly-maintenance", compact.Definition!.Id);
        Assert.Equal(compact.DefinitionVersion, formatted.DefinitionVersion);
        Assert.Equal(64, compact.DefinitionVersion!.Length);
    }

    [Theory]
    [MemberData(nameof(InvalidDefinitions))]
    public void Invalid_definition_is_faulted_with_stable_diagnostic(
        string fileName,
        string yaml,
        string expectedCode)
    {
        var result = CreateLoader().Load(fileName, Encoding.UTF8.GetBytes(yaml));

        Assert.False(result.IsValid);
        Assert.Null(result.Definition);
        Assert.Contains(result.Diagnostics, item => item.Code == expectedCode);
        Assert.InRange(result.Diagnostics.Count, 1, 32);
        Assert.All(result.Diagnostics, item =>
        {
            Assert.DoesNotContain(yaml, item.Message, StringComparison.Ordinal);
            Assert.False(Path.IsPathRooted(item.Path ?? string.Empty));
        });
    }

    [Fact]
    public void Every_semantic_field_changes_definition_version()
    {
        var loader = CreateLoader();
        var baseline = loader.Load(
            "nightly-maintenance.yaml",
            Encoding.UTF8.GetBytes(ValidYaml()));
        Assert.True(
            baseline.IsValid,
            string.Join(" | ", baseline.Diagnostics.Select(x => $"{x.Code}:{x.Path}")));
        var changes = new[]
        {
            ("displayName: Nightly Maintenance", "displayName: Changed"),
            ("description: Optional", "description: Changed"),
            ("enabled: true", "enabled: false"),
            ("cron: \"0 2 * * *\"", "cron: \"5 2 * * *\""),
            ("timeZone: Asia/Shanghai", "timeZone: UTC"),
            ("mode: worktree", "mode: project"),
            ("allowDirtyOrigin: false", "allowDirtyOrigin: true"),
            ("prompt: Do {{ inputs.task }}", "prompt: Check {{ inputs.task }}"),
            ("type: string", "type: string\n      minLength: 1"),
            ("task: cleanup", "task: inspect"),
            ("plugins: [sample]", "plugins: [other]"),
            ("skills: [review]", "skills: [other]"),
            ("tools: [file.read]", "tools: [file.write]"),
            ("effects: [workspaceRead]", "effects: [workspaceWrite]"),
            ("runTimeout: 30m", "runTimeout: 31m"),
            ("attentionTimeout: 24h", "attentionTimeout: 25h"),
        };

        foreach (var (before, after) in changes)
        {
            var changed = loader.Load(
                "nightly-maintenance.yaml",
                Encoding.UTF8.GetBytes(ValidYaml().Replace(before, after)));
            Assert.True(changed.IsValid, after);
            Assert.NotEqual(baseline.DefinitionVersion, changed.DefinitionVersion);
        }
    }

    public static TheoryData<string, string, string> InvalidDefinitions()
    {
        var data = new TheoryData<string, string, string>
        {
            {
                "nightly-maintenance.yaml",
                ValidYaml() + "\nunknown: true\n",
                AutomationDefinitionDiagnosticCodes.InvalidYaml
            },
            {
                "nightly-maintenance.yaml",
                ValidYaml().Replace(
                    "enabled: true",
                    "enabled: true\nenabled: false"),
                AutomationDefinitionDiagnosticCodes.InvalidYaml
            },
            {
                "nightly-maintenance.yaml",
                ValidYaml().Replace(
                    "displayName: Nightly Maintenance",
                    "displayName: &name Nightly Maintenance"),
                AutomationDefinitionDiagnosticCodes.UnsupportedYaml
            },
            {
                "nightly-maintenance.yaml",
                ValidYaml().Replace(
                    "description: Optional",
                    "description: *name"),
                AutomationDefinitionDiagnosticCodes.UnsupportedYaml
            },
            {
                "nightly-maintenance.yaml",
                ValidYaml().Replace(
                    "description: Optional",
                    "description: !custom Optional"),
                AutomationDefinitionDiagnosticCodes.UnsupportedYaml
            },
            {
                "different.yaml",
                ValidYaml(),
                AutomationDefinitionDiagnosticCodes.IdentityMismatch
            },
            {
                "nightly-maintenance.yaml",
                ValidYaml().Replace("schemaVersion: 1", "schemaVersion: \"1\""),
                AutomationDefinitionDiagnosticCodes.InvalidSchema
            },
            {
                "nightly-maintenance.yaml",
                ValidYaml().Replace("mode: worktree", "mode: scratchpad"),
                AutomationDefinitionDiagnosticCodes.InvalidWorkspace
            },
            {
                "nightly-maintenance.yaml",
                ValidYaml().Replace(
                    "mode: worktree\n  allowDirtyOrigin: false",
                    "mode: project\n  allowDirtyOrigin: true"),
                AutomationDefinitionDiagnosticCodes.InvalidWorkspace
            },
            {
                "nightly-maintenance.yaml",
                ValidYaml().Replace(
                    "effects: [workspaceRead]",
                    "effects: [unknownEffect]"),
                AutomationDefinitionDiagnosticCodes.InvalidSchema
            },
            {
                "nightly-maintenance.yaml",
                DeepYaml(),
                AutomationDefinitionDiagnosticCodes.LimitExceeded
            },
            {
                "nightly-maintenance.yaml",
                new string('a', 256 * 1024 + 1),
                AutomationDefinitionDiagnosticCodes.LimitExceeded
            },
        };
        return data;
    }

    internal static string ValidYaml() =>
        """
        schemaVersion: 1
        id: nightly-maintenance
        displayName: Nightly Maintenance
        description: Optional
        enabled: true
        schedule:
          cron: "0 2 * * *"
          timeZone: Asia/Shanghai
        workspace:
          mode: worktree
          allowDirtyOrigin: false
        prompt: Do {{ inputs.task }}
        inputSchema:
          type: object
          properties:
            task:
              type: string
          required: [task]
          additionalProperties: false
        defaults:
          task: cleanup
        allow:
          plugins: [sample]
          skills: [review]
          tools: [file.read]
          effects: [workspaceRead]
        runTimeout: 30m
        attentionTimeout: 24h
        """;

    private static string DeepYaml()
    {
        var lines = new List<string> { "schemaVersion: 1", "value:" };
        for (var depth = 1; depth <= 65; depth++)
        {
            lines.Add($"{new string(' ', depth * 2)}child:");
        }

        lines.Add($"{new string(' ', 132)}end: true");
        return string.Join('\n', lines);
    }

    private static AutomationDefinitionLoader CreateLoader() =>
        new(new JsonSchemaValidationService(), new NoSensitiveDataService());

    private sealed class CapturingSchemaService : IJsonSchemaValidationService
    {
        public JsonElement Schema { get; private set; }

        public bool IsValidSchema(JsonElement schema)
        {
            Schema = schema.Clone();
            return true;
        }

        public bool Evaluate(JsonElement schema, JsonElement value) => true;
    }
}
