using System.Security.Cryptography;
using System.Text.Json;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Logging;
using OpenCoWork.Core.Sessions;
using OpenCoWork.Core.Tools;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class DynamicToolTests
{
    [Fact]
    public async Task Dynamic_tool_is_thread_scoped_and_disconnect_invalidates_old_snapshot()
    {
        var clock = new ManualTimerTimeProvider(
            new DateTimeOffset(2026, 7, 29, 0, 0, 0, TimeSpan.Zero));
        var runtime = new ToolRuntime([], [], clock);
        var registry = new DynamicToolRegistry(runtime, clock);
        var connectionId = Guid.CreateVersion7();
        var threadId = Guid.CreateVersion7();
        var request = Request();

        var pending = registry.Register(
            connectionId,
            threadId,
            request,
            static (_, _) => ValueTask.FromResult(
                ToolBindingResult.Success(
                    JsonSerializer.SerializeToElement(new { ok = true }))));

        Assert.Equal(CapabilityStatus.PendingTrust, pending.Status);
        Assert.DoesNotContain(
            runtime.BuildSnapshot(AgentMode.Agent, new ToolsConfig(), threadId)
                .Registrations,
            item => item.Definition.Id.SourceKind == ToolSourceKind.RuntimeDynamic);

        registry.GrantConnectionTrust(connectionId);
        var snapshot = runtime.BuildSnapshot(
            AgentMode.Agent,
            new ToolsConfig(),
            threadId);
        var registration = Assert.Single(
            snapshot.Registrations,
            item => item.Definition.Id.SourceKind == ToolSourceKind.RuntimeDynamic);
        Assert.DoesNotContain(
            runtime.BuildSnapshot(
                    AgentMode.Agent,
                    new ToolsConfig(),
                    Guid.CreateVersion7())
                .Registrations,
            item => item.Definition.Id == registration.Definition.Id);

        using var arguments = JsonDocument.Parse("""{"value":"hello"}""");
        var completed = await InvokeAsync(
            runtime,
            snapshot,
            registration,
            arguments.RootElement,
            threadId,
            clock);
        Assert.Equal(ToolInvocationStatus.Completed, completed.Status);

        registry.Disconnect(connectionId);
        var result = await InvokeAsync(
            runtime,
            snapshot,
            registration,
            arguments.RootElement,
            threadId,
            clock);

        Assert.Equal(
            DynamicToolErrorCodes.Disconnected,
            result.Error?.Code);
    }

    [Fact]
    public async Task Expired_dynamic_tool_returns_stable_lease_error()
    {
        var clock = new ManualTimerTimeProvider(
            new DateTimeOffset(2026, 7, 29, 0, 0, 0, TimeSpan.Zero));
        var runtime = new ToolRuntime([], [], clock);
        var registry = new DynamicToolRegistry(runtime, clock);
        var connectionId = Guid.CreateVersion7();
        var threadId = Guid.CreateVersion7();
        var request = Request(leaseDuration: TimeSpan.FromSeconds(30));
        registry.Register(
            connectionId,
            threadId,
            request,
            static (_, _) => ValueTask.FromResult(
                ToolBindingResult.Success(
                    JsonSerializer.SerializeToElement(new { ok = true }))));
        registry.GrantConnectionTrust(connectionId);
        var snapshot = runtime.BuildSnapshot(
            AgentMode.Agent,
            new ToolsConfig(),
            threadId);
        var registration = Assert.Single(
            snapshot.Registrations,
            item => item.Definition.Id.SourceKind == ToolSourceKind.RuntimeDynamic);

        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.DoesNotContain(
            runtime.BuildSnapshot(AgentMode.Agent, new ToolsConfig(), threadId)
                .Registrations,
            item => item.Definition.Id == registration.Definition.Id);
        using var arguments = JsonDocument.Parse("""{"value":"hello"}""");
        var result = await InvokeAsync(
            runtime,
            snapshot,
            registration,
            arguments.RootElement,
            threadId,
            clock);

        Assert.Equal(
            DynamicToolErrorCodes.LeaseExpired,
            result.Error?.Code);
    }

    private static DynamicToolRegistrationRequest Request(
        TimeSpan? leaseDuration = null)
    {
        using var schema = JsonDocument.Parse(
            """
            {
              "type":"object",
              "properties":{"value":{"type":"string"}},
              "required":["value"],
              "additionalProperties":false
            }
            """);
        var definition = new DynamicToolDefinition(
            "echo",
            "Echo a value.",
            schema.RootElement,
            ToolEffect.None,
            ToolReplaySafety.Safe);
        return new DynamicToolRegistrationRequest(
            Guid.CreateVersion7(),
            definition,
            DynamicToolRegistry.ComputeDefinitionSha256(definition),
            leaseDuration);
    }

    private static async Task<ToolResultSnapshot> InvokeAsync(
        ToolRuntime runtime,
        EffectiveToolSnapshot snapshot,
        ToolRegistration registration,
        JsonElement arguments,
        Guid threadId,
        TimeProvider clock)
    {
        var providerName = snapshot.CanonicalToProviderNames[
            $"{registration.Definition.Name.Namespace}." +
            registration.Definition.Name.Name];
        var context = new ToolInvocationContext(
            threadId,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            0,
            "call-dynamic",
            providerName,
            arguments,
            Convert.ToHexString(SHA256.HashData(
                    ThreadJournal.Canonicalize(arguments)))
                .ToLowerInvariant(),
            SensitiveInputDetected: false,
            snapshot);
        return await new ToolInvocationPipeline(
                runtime,
                new SecretRedactor([]),
                timeProvider: clock)
            .InvokeAsync(
                context,
                new RecordingSink(),
                TestContext.Current.CancellationToken);
    }

    private sealed class RecordingSink : ISessionExecutionSink
    {
        public ValueTask EmitAsync(
            SessionExecutionIntent intent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }
}
