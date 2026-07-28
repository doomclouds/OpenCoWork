using System.Reflection;
using System.Text.Json;
using OpenCoWork.Abstractions;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class ToolContractTests
{
    [Fact]
    public void M4_tool_contracts_use_the_frozen_closed_sets_and_limits()
    {
        Assert.Equal(
            [
                ToolSourceKind.CoreNative,
                ToolSourceKind.PluginNative,
                ToolSourceKind.Mcp,
                ToolSourceKind.RuntimeDynamic,
            ],
            Enum.GetValues<ToolSourceKind>());
        Assert.Equal(
            [
                ToolAuthorityDecision.Deny,
                ToolAuthorityDecision.RequireApproval,
                ToolAuthorityDecision.Allow,
            ],
            Enum.GetValues<ToolAuthorityDecision>());
        Assert.Equal(
            [
                ToolInvocationStatus.Started,
                ToolInvocationStatus.WaitingApproval,
                ToolInvocationStatus.Completed,
                ToolInvocationStatus.Rejected,
                ToolInvocationStatus.Failed,
                ToolInvocationStatus.Cancelled,
                ToolInvocationStatus.TimedOut,
                ToolInvocationStatus.OutcomeUnknown,
            ],
            Enum.GetValues<ToolInvocationStatus>());
        Assert.Equal(
            [ToolReplaySafety.Unsafe, ToolReplaySafety.Safe],
            Enum.GetValues<ToolReplaySafety>());
        Assert.Equal(
            [
                ToolEffect.None,
                ToolEffect.WorkspaceRead,
                ToolEffect.WorkspaceWrite,
                ToolEffect.ProcessExecution,
                ToolEffect.NetworkRead,
                ToolEffect.ExternalMutation,
            ],
            Enum.GetValues<ToolEffect>());
        Assert.True(typeof(ToolEffect).IsDefined(typeof(FlagsAttribute)));
        Assert.True(typeof(ToolInvocationAudience).IsDefined(typeof(FlagsAttribute)));

        Assert.Equal(64 * 1024, ToolRuntimeLimits.MaximumSchemaBytes);
        Assert.Equal(1024 * 1024, ToolRuntimeLimits.MaximumSnapshotBytes);
        Assert.Equal(512 * 1024, ToolRuntimeLimits.MaximumArgumentsBytes);
        Assert.Equal(64, ToolRuntimeLimits.MaximumJsonDepth);
        Assert.Equal(1024 * 1024, ToolRuntimeLimits.MaximumBindingResultBytes);
        Assert.Equal(256 * 1024, ToolRuntimeLimits.MaximumResultEnvelopeBytes);
        Assert.Equal(TimeSpan.FromMinutes(30), ToolRuntimeLimits.TurnExecutionBudget);
    }

    [Fact]
    public void Tool_error_codes_match_the_frozen_wire_contract()
    {
        Assert.Equal(
            [
                "tool.definitionInvalid",
                "tool.nameConflict",
                "tool.snapshotTooLarge",
                "tool.iterationLimitExceeded",
                "tool.notFound",
                "tool.callIdConflict",
                "tool.audienceDenied",
                "tool.exposureDenied",
                "tool.modeDenied",
                "tool.bindingUnavailable",
                "tool.leaseExpired",
                "tool.authorityDenied",
                "tool.inputInvalid",
                "tool.inputTooLarge",
                "tool.sensitiveInputRejected",
                "tool.policyDenied",
                "tool.hookDenied",
                "tool.hookFailed",
                "tool.approvalDenied",
                "tool.executionFailed",
                "tool.resultInvalid",
                "tool.outputLimitExceeded",
                "tool.timeout",
                "tool.cancelled",
                "tool.outcomeUnknown",
                "tool.pathDenied",
                "tool.pathNotFound",
                "tool.contentUnsupported",
                "tool.preconditionFailed",
                "tool.networkTargetDenied",
            ],
            typeof(ToolErrorCodes)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(field => field.IsLiteral && !field.IsInitOnly)
                .Select(field => (string)field.GetRawConstantValue()!)
                .ToArray());
    }

    [Fact]
    public void Tool_identity_snapshot_and_results_are_immutable_and_secret_free()
    {
        using var document = JsonDocument.Parse(
            """{"type":"object","additionalProperties":false}""");
        var definition = new ToolDefinition(
            new ToolDefinitionId(
                ToolSourceKind.CoreNative,
                "opencowork.core",
                "file.read"),
            new ToolName("file", "read"),
            "Read a UTF-8 file.",
            document.RootElement,
            ToolEffect.WorkspaceRead);
        var registration = new ToolRegistration(
            definition,
            new RuntimeBindingId("core.file.read.v1"),
            ToolExposure.Direct,
            ToolInvocationAudience.Model);
        var registrations = new[] { registration };
        var canonicalToProvider = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["file.read"] = "file__read",
        };
        var providerToCanonical = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["file__read"] = "file.read",
        };
        var snapshot = new EffectiveToolSnapshot(
            schemaVersion: 1,
            effectiveAgentMode: AgentMode.Agent,
            registrations,
            canonicalToProvider,
            providerToCanonical,
            diagnostics: [],
            snapshotSha256: new string('a', 64));

        registrations[0] = registration with
        {
            RuntimeBindingId = new RuntimeBindingId("changed"),
        };
        canonicalToProvider["file.read"] = "changed";

        Assert.Equal("core.file.read.v1", snapshot.Registrations[0].RuntimeBindingId.Value);
        Assert.Equal(ToolReplaySafety.Unsafe, definition.ReplaySafety);
        Assert.Equal("file__read", snapshot.CanonicalToProviderNames["file.read"]);
        Assert.Equal(JsonValueKind.Object, definition.InputSchema.ValueKind);
        Assert.All(
            new[]
            {
                typeof(EffectiveToolSnapshot),
                typeof(ToolInvocationContext),
                typeof(ToolResultSnapshot),
            }.SelectMany(type => type.GetProperties()),
            property => Assert.DoesNotContain(
                new[] { "Secret", "ApiKey", "Header", "AbsolutePath", "Command", "Url" },
                value => property.Name.Contains(value, StringComparison.OrdinalIgnoreCase)));
    }
}
