using System.Text.Json;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Agents;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Logging;
using OpenCoWork.Core.Tools;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class DeferredToolTests
{
    [Fact]
    public async Task Search_activates_frozen_deferred_tools_eight_at_a_time()
    {
        var runtime = new ToolRuntime();
        var registrations = Enumerable.Range(0, 10)
            .Select(DeferredRegistration)
            .ToArray();
        runtime.PublishPlugin(
            "acme/tools",
            registrations,
            registrations.Select(registration => new ToolRuntimeBinding(
                registration.RuntimeBindingId,
                ToolBindingAvailability.Available,
                Lease: null,
                TimeSpan.FromSeconds(30),
                static (_, _) => ValueTask.FromResult(
                    ToolBindingResult.Success(JsonSerializer.SerializeToElement(
                        new { ok = true }))),
                registration.BindingGeneration,
                IsTrusted: true)).ToArray());
        var snapshot = runtime.BuildSnapshot(AgentMode.Agent, new ToolsConfig());
        var activated = new HashSet<ToolDefinitionId>();

        Assert.Equal(10, snapshot.Registrations.Count(
            registration => registration.Exposure == ToolExposure.Deferred));
        Assert.DoesNotContain(
            runtime.CreateProviderDefinitions(snapshot, activated)
                .OfType<DeepSeekFunctionTool>(),
            tool => tool.Name.StartsWith("plugin_acme_tools", StringComparison.Ordinal));

        var search = Assert.Single(
            snapshot.Registrations,
            registration => registration.Definition.Id.SourceToolId == "tool.search");
        using var arguments = JsonDocument.Parse("""{"query":"tool"}""");
        var sink = new RecordingSink();
        var result = await new ToolInvocationPipeline(
                runtime,
                new SecretRedactor([]))
            .InvokeAsync(
                new ToolInvocationContext(
                    Guid.CreateVersion7(),
                    Guid.CreateVersion7(),
                    Guid.CreateVersion7(),
                    Guid.CreateVersion7(),
                    0,
                    "call-search",
                    snapshot.CanonicalToProviderNames["tool.search"],
                    arguments.RootElement,
                    ToolTestHash(arguments.RootElement),
                    SensitiveInputDetected: false,
                    snapshot,
                    ActivatedDeferredTools: activated.ToArray()),
                sink,
                TestContext.Current.CancellationToken);
        var activation = Assert.Single(
            sink.Intents.OfType<RecordDeferredToolsActivatedIntent>());
        activated.UnionWith(activation.ToolDefinitionIds);

        Assert.Equal(ToolInvocationStatus.Completed, result.Status);
        Assert.Equal(8, activation.ToolDefinitionIds.Count);
        Assert.Equal(
            8,
            runtime.CreateProviderDefinitions(snapshot, activated)
                .OfType<DeepSeekFunctionTool>()
                .Count(
                tool => tool.Name.StartsWith(
                    "plugin_acme_tools",
                    StringComparison.Ordinal)));
    }

    private static ToolRegistration DeferredRegistration(int index)
    {
        using var schema = JsonDocument.Parse(
            """{"type":"object","additionalProperties":false}""");
        return new ToolRegistration(
            new ToolDefinition(
                new ToolDefinitionId(
                    ToolSourceKind.PluginNative,
                    "acme/tools",
                    $"tool-{index:00}"),
                new ToolName("plugin_acme_tools", $"tool_{index:00}"),
                $"Deferred tool {index:00}.",
                schema.RootElement,
                ToolEffect.WorkspaceRead,
                ToolReplaySafety.Safe),
            new RuntimeBindingId($"plugin.acme.tools.{index:00}"),
            ToolExposure.Deferred,
            ToolInvocationAudience.Model,
            BindingGeneration: 1);
    }

    private static string ToolTestHash(JsonElement arguments) =>
        Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    OpenCoWork.Core.Sessions.ThreadJournal.Canonicalize(arguments)))
            .ToLowerInvariant();

    private sealed class RecordingSink : ISessionExecutionSink
    {
        public List<SessionExecutionIntent> Intents { get; } = [];

        public ValueTask EmitAsync(
            SessionExecutionIntent intent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Intents.Add(intent);
            return ValueTask.CompletedTask;
        }
    }
}
