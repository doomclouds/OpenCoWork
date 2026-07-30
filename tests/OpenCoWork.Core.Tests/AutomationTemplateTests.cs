using System.Text;
using System.Text.Json;
using OpenCoWork.Abstractions;
using OpenCoWork.Automations;
using OpenCoWork.Core.Tools;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class AutomationTemplateTests
{
    [Fact]
    public async Task Renderer_merges_defaults_validates_inputs_and_exposes_only_four_roots()
    {
        var loader = new AutomationDefinitionLoader(
            new JsonSchemaValidationService(),
            new NoSensitiveDataService());
        var loaded = loader.Load(
            "nightly-maintenance.yaml",
            Encoding.UTF8.GetBytes(
                AutomationDefinitionTests.ValidYaml().Replace(
                    "prompt: Do {{ inputs.task }}",
                    """
                    prompt: >-
                      {{ automation.id }}|{{ run.id }}|{{ inputs.task }}|{{ trigger.kind }}
                    """)));
        var renderer = new AutomationTemplateRenderer(
            new JsonSchemaValidationService(),
            new NoSensitiveDataService(),
            TimeProvider.System);
        var runId = Guid.CreateVersion7();
        using var manual = JsonDocument.Parse("""{"task":"manual"}""");

        var result = await renderer.RenderAsync(
            loaded.Definition!,
            runId,
            manual.RootElement,
            new AutomationTriggerContext("manual", null),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsValid);
        Assert.Equal(
            $"nightly-maintenance|{runId:D}|manual|manual",
            result.Prompt);
        Assert.Equal("manual", result.Inputs!.Value.GetProperty("task").GetString());
    }

    [Theory]
    [InlineData("""{"extra":true}""")]
    [InlineData("[]")]
    public async Task Invalid_manual_inputs_do_not_render(string inputs)
    {
        var (definition, renderer) = CreateRenderer();
        using var document = JsonDocument.Parse(inputs);

        var result = await renderer.RenderAsync(
            definition,
            Guid.CreateVersion7(),
            document.RootElement,
            new AutomationTriggerContext("manual", null),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Equal(
            AutomationDefinitionDiagnosticCodes.InvalidInputs,
            Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task Undefined_or_object_member_access_fails_closed()
    {
        foreach (var prompt in new[]
                 {
                     "{{ missing.value }}",
                     "{{ inputs.GetType }}",
                     "{{ inputs.task.GetType }}",
                     "{{ environment.PATH }}",
                     "{{ workspace.mode }}",
                 })
        {
            var (definition, renderer) = CreateRenderer(prompt);
            using var inputs = JsonDocument.Parse("""{"task":"manual"}""");
            var result = await renderer.RenderAsync(
                definition,
                Guid.CreateVersion7(),
                inputs.RootElement,
                new AutomationTriggerContext("manual", null),
                TestContext.Current.CancellationToken);
            Assert.False(result.IsValid, prompt);
            Assert.Equal(
                AutomationDefinitionDiagnosticCodes.TemplateRenderFailed,
                Assert.Single(result.Diagnostics).Code);
        }
    }

    [Fact]
    public async Task Output_limit_and_secret_detection_fail_before_run_creation()
    {
        var values = Enumerable.Repeat(new string('x', 40), 4096).ToArray();
        using var inputs = JsonDocument.Parse(JsonSerializer.Serialize(new { values }));
        var (definition, renderer) = CreateRenderer(
            "{% for value in inputs.values %}{{ value }}{{ value }}{% endfor %}",
            """
            type: object
            properties:
              values:
                type: array
                items:
                  type: string
            additionalProperties: false
            """,
            defaults: "{}");
        var oversized = await renderer.RenderAsync(
            definition,
            Guid.CreateVersion7(),
            inputs.RootElement,
            new AutomationTriggerContext("manual", null),
            TestContext.Current.CancellationToken);
        Assert.False(oversized.IsValid);
        Assert.Equal(
            AutomationDefinitionDiagnosticCodes.LimitExceeded,
            Assert.Single(oversized.Diagnostics).Code);

        var secret = "template-secret-canary";
        var (secretDefinition, _) = CreateRenderer("{{ inputs.task }}");
        var secretRenderer = new AutomationTemplateRenderer(
            new JsonSchemaValidationService(),
            new ExactSensitiveDataService(secret),
            TimeProvider.System);
        using var secretInputs =
            JsonDocument.Parse($$"""{"task":"{{secret}}"}""");
        var rejected = await secretRenderer.RenderAsync(
            secretDefinition,
            Guid.CreateVersion7(),
            secretInputs.RootElement,
            new AutomationTriggerContext("manual", null),
            TestContext.Current.CancellationToken);
        Assert.False(rejected.IsValid);
        Assert.Equal(
            AutomationDefinitionDiagnosticCodes.SecretDetected,
            Assert.Single(rejected.Diagnostics).Code);
        Assert.DoesNotContain(
            secret,
            rejected.Diagnostics[0].Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Render_budget_is_fixed_at_two_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), AutomationRuntimeLimits.RenderTimeout);
    }

    private static (
        AutomationDefinitionCandidate Definition,
        AutomationTemplateRenderer Renderer) CreateRenderer(
        string? prompt = null,
        string? inputSchema = null,
        string? defaults = null)
    {
        var renderedPrompt = (prompt ?? "{{ inputs.task }}")
            .Replace("\n", "\n  ", StringComparison.Ordinal);
        var yaml = AutomationDefinitionTests.ValidYaml()
            .Replace(
                "prompt: Do {{ inputs.task }}",
                $"prompt: >-\n  {renderedPrompt}",
                StringComparison.Ordinal);
        if (inputSchema is not null)
        {
            var start = yaml.IndexOf("inputSchema:", StringComparison.Ordinal);
            var end = yaml.IndexOf("defaults:", start, StringComparison.Ordinal);
            yaml = yaml[..start] +
                   "inputSchema:\n" +
                   string.Join('\n', inputSchema.Split('\n').Select(line => "  " + line)) +
                   "\n" +
                   yaml[end..];
        }

        if (defaults is not null)
        {
            var start = yaml.IndexOf("defaults:", StringComparison.Ordinal);
            var end = yaml.IndexOf("allow:", start, StringComparison.Ordinal);
            yaml = yaml[..start] + $"defaults: {defaults}\n" + yaml[end..];
        }

        var validator = new JsonSchemaValidationService();
        var loader = new AutomationDefinitionLoader(
            validator,
            new NoSensitiveDataService());
        var loaded = loader.Load(
            "nightly-maintenance.yaml",
            Encoding.UTF8.GetBytes(yaml));
        Assert.True(loaded.IsValid, string.Join(" | ", loaded.Diagnostics.Select(x => x.Code)));
        return (
            loaded.Definition!,
            new AutomationTemplateRenderer(
                validator,
                new NoSensitiveDataService(),
                TimeProvider.System));
    }
}

internal sealed class NoSensitiveDataService : ISensitiveDataService
{
    public bool ContainsSensitiveData(string value) => false;

    public string Redact(string value) => value;

    public ValueTask<bool> ContainsSensitiveDataAsync(
        Stream source,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(false);
}

internal sealed class ExactSensitiveDataService(string secret) : ISensitiveDataService
{
    public bool ContainsSensitiveData(string value) =>
        value.Contains(secret, StringComparison.Ordinal);

    public string Redact(string value) =>
        value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);

    public async ValueTask<bool> ContainsSensitiveDataAsync(
        Stream source,
        CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(source, leaveOpen: true);
        return ContainsSensitiveData(await reader.ReadToEndAsync(cancellationToken));
    }
}
