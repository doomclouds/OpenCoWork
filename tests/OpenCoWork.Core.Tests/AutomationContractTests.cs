using System.Text.Json;
using OpenCoWork.Abstractions;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class AutomationContractTests
{
    [Fact]
    public void Automation_states_errors_and_limits_are_frozen()
    {
        Assert.Equal(["Host", "Scheduler"], Enum.GetNames<AutomationActorKind>());
        Assert.Equal(
            ["Ready", "Faulted", "Missing"],
            Enum.GetNames<AutomationDefinitionSourceStatus>());
        Assert.Equal(
            ["Project", "Worktree"],
            Enum.GetNames<AutomationWorkspaceMode>());
        Assert.Equal(["Manual", "Cron"], Enum.GetNames<AutomationTriggerKind>());
        Assert.Equal(
            [
                "Pending",
                "Running",
                "NeedsAttention",
                "Completed",
                "Failed",
                "Cancelled",
                "TimedOut",
            ],
            Enum.GetNames<AutomationRunStatus>());
        Assert.Equal(
            ["ApprovalRequired", "UserInputRequired", "OutcomeUnknown"],
            Enum.GetNames<AutomationAttentionKind>());
        Assert.Equal(
            ["Approve", "Reject", "ProvideInput", "Fail", "Cancel"],
            Enum.GetNames<AutomationAttentionResolutionKind>());
        Assert.Equal(
            CapabilityTrustScope.UnattendedAutomation,
            Enum.GetValues<CapabilityTrustScope>()[^1]);
        Assert.Equal(
            "opencowork.automations",
            AutomationTrustBoundary.Source.Id);

        Assert.Equal("automation.notFound", AutomationErrorCodes.NotFound);
        Assert.Equal("automation.conflict", AutomationErrorCodes.Conflict);
        Assert.Equal("automation.runConflict", AutomationErrorCodes.RunConflict);
        Assert.Equal("automation.invalidState", AutomationErrorCodes.InvalidState);
        Assert.Equal("automation.invalidCursor", AutomationErrorCodes.InvalidCursor);
        Assert.Equal("automation.definitionInvalid", AutomationErrorCodes.DefinitionInvalid);
        Assert.Equal("automation.inputInvalid", AutomationErrorCodes.InputInvalid);
        Assert.Equal("automation.permissionDenied", AutomationErrorCodes.PermissionDenied);
        Assert.Equal("automation.secretDetected", AutomationErrorCodes.SecretDetected);
        Assert.Equal(
            "automation.capabilityUnavailable",
            AutomationErrorCodes.CapabilityUnavailable);
        Assert.Equal("automation.pathEscape", AutomationErrorCodes.PathEscape);
        Assert.Equal("automation.worktreeDirty", AutomationErrorCodes.WorktreeDirty);
        Assert.Equal("automation.outcomeUnknown", AutomationErrorCodes.OutcomeUnknown);
        Assert.Equal("automation.leaseLost", AutomationErrorCodes.LeaseLost);
        Assert.Equal("automation.retryExhausted", AutomationErrorCodes.RetryExhausted);
        Assert.Equal("automation.schemaInvalid", AutomationErrorCodes.SchemaInvalid);
        Assert.Equal("automation.unavailable", AutomationErrorCodes.Unavailable);

        Assert.Equal(3, AutomationRuntimeLimits.DefaultMaxConcurrentRuns);
        Assert.Equal(16, AutomationRuntimeLimits.MaximumConcurrentRuns);
        Assert.Equal(TimeSpan.FromMinutes(30), AutomationRuntimeLimits.DefaultRunTimeout);
        Assert.Equal(TimeSpan.FromHours(24), AutomationRuntimeLimits.MaximumRunTimeout);
        Assert.Equal(
            TimeSpan.FromHours(24),
            AutomationRuntimeLimits.DefaultAttentionTimeout);
        Assert.Equal(
            TimeSpan.FromHours(168),
            AutomationRuntimeLimits.MaximumAttentionTimeout);
        Assert.Equal(5, AutomationRuntimeLimits.DispatchAttempts);
        Assert.Equal(TimeSpan.FromMinutes(2), AutomationRuntimeLimits.DispatchLease);
        Assert.Equal(TimeSpan.FromSeconds(30), AutomationRuntimeLimits.LeaseRenewal);
        Assert.Equal(100, AutomationRuntimeLimits.DefaultPageSize);
        Assert.Equal(100, AutomationRuntimeLimits.MaximumPageSize);
    }

    [Fact]
    public void Actor_revision_requests_and_service_operations_are_explicit()
    {
        using var inputs = JsonDocument.Parse("""{"scope":"focused"}""");
        var actor = new AutomationActorContext(AutomationActorKind.Host, "wire:connection");
        var start = new StartAutomationRunRequest(
            actor,
            "nightly-maintenance",
            inputs.RootElement.Clone(),
            Guid.CreateVersion7(),
            ExpectedRevision: 7);
        var resolution = new ResolveAutomationAttentionRequest(
            actor,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            new AutomationAttentionResolution(
                AutomationAttentionResolutionKind.ProvideInput,
                "continue"),
            Guid.CreateVersion7(),
            ExpectedRevision: 9);

        Assert.Equal(AutomationActorKind.Host, start.Actor.Kind);
        Assert.Equal(7, start.ExpectedRevision);
        Assert.Equal(
            AutomationAttentionResolutionKind.ProvideInput,
            resolution.Resolution.Kind);
        Assert.Equal(9, resolution.ExpectedRevision);
        Assert.Equal(
            [
                "CancelRunAsync",
                "GetDefinitionAsync",
                "GetRunAsync",
                "GetScheduleAsync",
                "ListDefinitionsAsync",
                "ListRunsAsync",
                "ListSchedulesAsync",
                "ResolveAttentionAsync",
                "StartRunAsync",
            ],
            typeof(IAutomationService)
                .GetMethods()
                .Select(method => method.Name)
                .Order()
                .ToArray());
    }

    [Fact]
    public void Results_keep_domain_errors_outside_wire_contracts()
    {
        var success = new AutomationResult<string>(
            "created",
            AutomationRevision: 3,
            Error: null,
            IsReplay: true);
        var failure = new AutomationResult<string>(
            Value: null,
            AutomationRevision: 4,
            new AutomationError(
                AutomationErrorCodes.RunConflict,
                "A nonterminal run already exists.",
                IsRetryable: true));

        Assert.True(success.IsSuccess);
        Assert.True(success.IsReplay);
        Assert.False(failure.IsSuccess);
        Assert.True(failure.Error!.IsRetryable);
        Assert.Equal(4, failure.AutomationRevision);
    }
}
