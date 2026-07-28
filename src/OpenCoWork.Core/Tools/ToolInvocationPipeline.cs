using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Logging;
using OpenCoWork.Core.Sessions;

namespace OpenCoWork.Core.Tools;

internal enum ToolInvocationStage
{
    SnapshotLookup,
    Started,
    AudienceExposureMode,
    BindingAvailabilityLease,
    Authority,
    InputSchema,
    Policy,
    PreToolUse,
    Approval,
    Invoke,
    ResultNormalize,
    Terminal,
    TerminalHook,
}

internal sealed class ToolInvocationTrace
{
    private readonly List<ToolInvocationStage> _stages = [];

    public IReadOnlyList<ToolInvocationStage> Stages => _stages.AsReadOnly();

    internal void Record(ToolInvocationStage stage) => _stages.Add(stage);
}

internal sealed record ToolPreUseDecision(
    ToolAuthorityDecision Authority = ToolAuthorityDecision.Allow,
    TimeSpan? TimeoutCap = null);

internal delegate ValueTask<ToolPreUseDecision> ToolPreUseHook(
    ToolInvocationContext context,
    CancellationToken cancellationToken);

internal delegate ValueTask ToolTerminalHook(
    ToolResultSnapshot result,
    CancellationToken cancellationToken);

internal sealed class ToolInvocationSuspendedException(Guid toolInvocationId)
    : Exception("Tool Invocation is waiting for approval.")
{
    public Guid ToolInvocationId { get; } = toolInvocationId;
}

internal sealed class ToolInvocationPipeline : IToolInvocationPipeline
{
    private const ToolEffect PlanEffects =
        ToolEffect.WorkspaceRead |
        ToolEffect.NetworkRead;
    private const int PreviewOverheadBytes = 4 * 1024;
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly ToolRuntime _runtime;
    private readonly SecretRedactor _redactor;
    private readonly IReadOnlyList<ToolAuthorityPolicy> _runtimeAuthority;
    private readonly ToolEffect _policyDeniedEffects;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ToolInvocationPipeline> _logger;
    private readonly ToolInvocationTrace? _trace;
    private readonly ToolPreUseHook? _preToolUse;
    private readonly ToolTerminalHook? _terminal;

    public ToolInvocationPipeline(
        ToolRuntime runtime,
        SecretRedactor redactor,
        IReadOnlyList<ToolAuthorityPolicy>? runtimeAuthority = null,
        ToolEffect policyDeniedEffects = ToolEffect.None,
        TimeProvider? timeProvider = null,
        ILogger<ToolInvocationPipeline>? logger = null,
        ToolInvocationTrace? trace = null,
        ToolPreUseHook? preToolUse = null,
        ToolTerminalHook? terminal = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        _runtimeAuthority = runtimeAuthority ?? [];
        _policyDeniedEffects = policyDeniedEffects;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<ToolInvocationPipeline>.Instance;
        _trace = trace;
        _preToolUse = preToolUse;
        _terminal = terminal;
    }

    public async ValueTask<ToolResultSnapshot> InvokeAsync(
        ToolInvocationContext context,
        ISessionExecutionSink sink,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(sink);

        Record(ToolInvocationStage.SnapshotLookup);
        var registration = ResolveRegistration(context);

        Record(ToolInvocationStage.Started);
        await sink.EmitAsync(
            new RecordToolInvocationStartedIntent(
                context.ToolInvocationId,
                context.ToolCallItemId,
                context.CallIndex,
                context.ProviderToolCallId,
                context.ProviderToolName,
                registration?.Definition.Id,
                registration?.RuntimeBindingId,
                context.Snapshot.SnapshotSha256,
                context.ArgumentsSha256),
            cancellationToken);

        if (context.ProviderCallIdConflict)
        {
            return await FinishErrorAsync(
                context,
                sink,
                ToolInvocationStatus.Rejected,
                ToolErrorCodes.CallIdConflict,
                "Provider Tool Call ID conflicts with an earlier call.",
                context.PriorAttemptCount);
        }

        if (context.ReplayResult is { } replay)
        {
            if (!string.Equals(
                    replay.ProviderToolCallId,
                    context.ProviderToolCallId,
                    StringComparison.Ordinal) ||
                replay.Status is
                    ToolInvocationStatus.Started or
                    ToolInvocationStatus.WaitingApproval)
            {
                return await FinishErrorAsync(
                    context,
                    sink,
                    ToolInvocationStatus.Rejected,
                    ToolErrorCodes.CallIdConflict,
                    "Persisted Tool Result does not match the provider call.",
                    context.PriorAttemptCount);
            }

            return await FinishAsync(
                context,
                sink,
                new ToolResultSnapshot(
                    context.ToolInvocationId,
                    context.ProviderToolCallId,
                    replay.Status,
                    replay.Output,
                    replay.Error,
                    replay.IsTruncated,
                    replay.OriginalByteCount,
                    replay.ResultSha256,
                    context.PriorAttemptCount));
        }

        if (registration is null)
        {
            return await FinishErrorAsync(
                context,
                sink,
                ToolInvocationStatus.Rejected,
                ToolErrorCodes.NotFound,
                "Tool is unavailable in the frozen snapshot.",
                context.PriorAttemptCount);
        }

        var definition = registration.Definition;
        Record(ToolInvocationStage.AudienceExposureMode);
        if ((registration.Audience & ToolInvocationAudience.Model) == 0)
        {
            return await RejectAsync(
                context,
                sink,
                ToolErrorCodes.AudienceDenied,
                "Tool audience does not allow model invocation.");
        }

        if (registration.Exposure != ToolExposure.Direct)
        {
            return await RejectAsync(
                context,
                sink,
                ToolErrorCodes.ExposureDenied,
                "Tool is not directly exposed.");
        }

        if (context.Snapshot.EffectiveAgentMode == AgentMode.Plan &&
            (definition.Effects & ~PlanEffects) != 0)
        {
            return await RejectAsync(
                context,
                sink,
                ToolErrorCodes.ModeDenied,
                "Tool effects are unavailable in Plan mode.");
        }

        Record(ToolInvocationStage.BindingAvailabilityLease);
        if (!_runtime.TryResolveBinding(
                registration.RuntimeBindingId,
                out var binding) ||
            binding is null ||
            binding.Availability != ToolBindingAvailability.Available ||
            binding.DefaultTimeout <= TimeSpan.Zero)
        {
            return await RejectAsync(
                context,
                sink,
                ToolErrorCodes.BindingUnavailable,
                "Runtime binding is unavailable.");
        }

        if (binding.Lease?.ExpiresAt is { } expiresAt &&
            expiresAt <= _timeProvider.GetUtcNow())
        {
            return await RejectAsync(
                context,
                sink,
                ToolErrorCodes.LeaseExpired,
                "Runtime binding lease has expired.");
        }

        Record(ToolInvocationStage.Authority);
        var snapshotAuthority = DecisionFor(
            definition.Effects,
            context.Snapshot.Authority);
        var runtimeAuthority = _runtimeAuthority.Count == 0
            ? ToolAuthorityDecision.Allow
            : DecisionFor(definition.Effects, _runtimeAuthority);
        var authority = Strictest(snapshotAuthority, runtimeAuthority);
        if (authority == ToolAuthorityDecision.Deny)
        {
            return await RejectAsync(
                context,
                sink,
                ToolErrorCodes.AuthorityDenied,
                "Tool authority is denied.");
        }

        Record(ToolInvocationStage.InputSchema);
        if (context.SensitiveInputDetected)
        {
            return await RejectAsync(
                context,
                sink,
                ToolErrorCodes.SensitiveInputRejected,
                "Tool arguments contain sensitive input.");
        }

        byte[] arguments;
        try
        {
            arguments = ThreadJournal.Canonicalize(context.Arguments);
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException)
        {
            return await RejectAsync(
                context,
                sink,
                ToolErrorCodes.InputInvalid,
                "Tool arguments are invalid.");
        }

        if (arguments.Length > ToolRuntimeLimits.MaximumArgumentsBytes)
        {
            return await RejectAsync(
                context,
                sink,
                ToolErrorCodes.InputTooLarge,
                "Tool arguments exceed the size limit.");
        }

        if (!string.Equals(
                Hash(arguments),
                context.ArgumentsSha256,
                StringComparison.Ordinal) ||
            !_runtime.ValidateArguments(definition, context.Arguments))
        {
            return await RejectAsync(
                context,
                sink,
                ToolErrorCodes.InputInvalid,
                "Tool arguments do not match the input schema.");
        }

        Record(ToolInvocationStage.Policy);
        if ((definition.Effects & _policyDeniedEffects) != 0)
        {
            return await RejectAsync(
                context,
                sink,
                ToolErrorCodes.PolicyDenied,
                "Runtime policy denied the tool.");
        }

        Record(ToolInvocationStage.PreToolUse);
        var hookDecision = new ToolPreUseDecision();
        if (_preToolUse is not null)
        {
            try
            {
                hookDecision = await _preToolUse(context, cancellationToken);
                if (!Enum.IsDefined(hookDecision.Authority) ||
                    hookDecision.TimeoutCap is { } timeoutCap &&
                    timeoutCap <= TimeSpan.Zero)
                {
                    return await RejectAsync(
                        context,
                        sink,
                        ToolErrorCodes.HookFailed,
                        "PreToolUse hook returned an invalid decision.");
                }
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                return await FinishErrorAsync(
                    context,
                    sink,
                    ToolInvocationStatus.Cancelled,
                    ToolErrorCodes.Cancelled,
                    "Tool invocation was cancelled.",
                    context.PriorAttemptCount);
            }
            catch (Exception)
            {
                return await RejectAsync(
                    context,
                    sink,
                    ToolErrorCodes.HookFailed,
                    "PreToolUse hook failed.");
            }
        }

        if (hookDecision.Authority == ToolAuthorityDecision.Deny)
        {
            return await RejectAsync(
                context,
                sink,
                ToolErrorCodes.HookDenied,
                "PreToolUse hook denied the tool.");
        }

        Record(ToolInvocationStage.Approval);
        var requiresApproval =
            authority == ToolAuthorityDecision.RequireApproval ||
            hookDecision.Authority == ToolAuthorityDecision.RequireApproval;
        if (requiresApproval)
        {
            if (context.ApprovalGranted == false)
            {
                return await RejectAsync(
                    context,
                    sink,
                    ToolErrorCodes.ApprovalDenied,
                    "Tool approval was denied.");
            }

            if (context.ApprovalGranted is null)
            {
                if (context.ApprovalCheckpoint is null)
                {
                    return await RejectAsync(
                        context,
                        sink,
                        ToolErrorCodes.ApprovalDenied,
                        "Tool approval continuation is unavailable.");
                }

                await sink.EmitAsync(
                    new WaitForInteractionIntent(
                        Guid.CreateVersion7(_timeProvider.GetUtcNow()),
                        SessionInteractionType.Approval,
                        new ToolApprovalRequestContent(
                            context.ToolInvocationId,
                            definition.Id,
                            context.Snapshot.SnapshotSha256,
                            context.ArgumentsSha256,
                            ApprovalPrompt(context, definition)),
                        context.ApprovalCheckpoint,
                        context.ApprovalTimeoutAt,
                        context.ToolInvocationId),
                    cancellationToken);
                throw new ToolInvocationSuspendedException(
                    context.ToolInvocationId);
            }
        }

        if (context.PriorAttemptCount is < 0 or > 2)
        {
            return await FinishErrorAsync(
                context,
                sink,
                ToolInvocationStatus.OutcomeUnknown,
                ToolErrorCodes.OutcomeUnknown,
                "Tool result is unknown.",
                Math.Clamp(context.PriorAttemptCount, 0, 2));
        }

        if (context.PriorAttemptCount > 0 &&
            (definition.ReplaySafety == ToolReplaySafety.Unsafe ||
             context.PriorAttemptCount >= 2))
        {
            return await FinishErrorAsync(
                context,
                sink,
                ToolInvocationStatus.OutcomeUnknown,
                ToolErrorCodes.OutcomeUnknown,
                "Tool result is unknown.",
                context.PriorAttemptCount);
        }

        Record(ToolInvocationStage.Invoke);
        if (cancellationToken.IsCancellationRequested)
        {
            return await FinishErrorAsync(
                context,
                sink,
                ToolInvocationStatus.Cancelled,
                ToolErrorCodes.Cancelled,
                "Tool invocation was cancelled.",
                context.PriorAttemptCount);
        }

        var timeout = binding.DefaultTimeout;
        timeout = Min(
            timeout,
            context.RemainingExecutionBudget ??
            ToolRuntimeLimits.TurnExecutionBudget);
        if (hookDecision.TimeoutCap is { } hookTimeout)
        {
            timeout = Min(timeout, hookTimeout);
        }

        if (timeout <= TimeSpan.Zero)
        {
            return await FinishErrorAsync(
                context,
                sink,
                ToolInvocationStatus.TimedOut,
                ToolErrorCodes.Timeout,
                "Tool invocation timed out.",
                context.PriorAttemptCount);
        }

        var attemptNumber = context.PriorAttemptCount + 1;
        await sink.EmitAsync(
            new RecordToolInvocationAttemptStartedIntent(
                context.ToolInvocationId,
                attemptNumber),
            cancellationToken);

        ToolBindingResult? bindingResult = null;
        ToolInvocationStatus? controlStatus = null;
        using (var deadline = new CancellationTokenSource(timeout, _timeProvider))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                   cancellationToken,
                   deadline.Token))
        {
            try
            {
                bindingResult = await binding.Executor(
                    context.Arguments,
                    linked.Token);
            }
            catch (OperationCanceledException)
            {
                controlStatus = cancellationToken.IsCancellationRequested
                    ? ToolInvocationStatus.Cancelled
                    : ToolInvocationStatus.TimedOut;
            }
            catch (Exception)
            {
                Record(ToolInvocationStage.ResultNormalize);
                return await FinishErrorAsync(
                    context,
                    sink,
                    ToolInvocationStatus.Failed,
                    ToolErrorCodes.ExecutionFailed,
                    "Tool execution failed.",
                    attemptNumber);
            }
        }

        Record(ToolInvocationStage.ResultNormalize);
        if (controlStatus is { } stopped)
        {
            return await FinishErrorAsync(
                context,
                sink,
                stopped,
                stopped == ToolInvocationStatus.Cancelled
                    ? ToolErrorCodes.Cancelled
                    : ToolErrorCodes.Timeout,
                stopped == ToolInvocationStatus.Cancelled
                    ? "Tool invocation was cancelled."
                    : "Tool invocation timed out.",
                attemptNumber);
        }

        if (bindingResult is null)
        {
            return await FinishErrorAsync(
                context,
                sink,
                ToolInvocationStatus.Failed,
                ToolErrorCodes.ResultInvalid,
                "Tool result is invalid.",
                attemptNumber);
        }

        if (!bindingResult.IsSuccess)
        {
            var error = bindingResult.Error!;
            var code = IsStableToolCode(error.Code)
                ? error.Code
                : ToolErrorCodes.ExecutionFailed;
            return await FinishErrorAsync(
                context,
                sink,
                code == ToolErrorCodes.OutcomeUnknown
                    ? ToolInvocationStatus.OutcomeUnknown
                    : ToolInvocationStatus.Failed,
                code,
                "Tool execution failed.",
                attemptNumber);
        }

        try
        {
            if (bindingResult.Output is not { } output ||
                output.ValueKind == JsonValueKind.Undefined)
            {
                return await FinishErrorAsync(
                    context,
                    sink,
                    ToolInvocationStatus.Failed,
                    ToolErrorCodes.ResultInvalid,
                    "Tool result is invalid.",
                    attemptNumber);
            }

            var redacted = _redactor.RedactJson(output, out _);
            var complete = ThreadJournal.Canonicalize(redacted);
            if (complete.Length > ToolRuntimeLimits.MaximumBindingResultBytes)
            {
                return await FinishErrorAsync(
                    context,
                    sink,
                    ToolInvocationStatus.Failed,
                    ToolErrorCodes.OutputLimitExceeded,
                    "Tool output exceeds the size limit.",
                    attemptNumber,
                    complete.Length,
                    Hash(complete));
            }

            var normalized = complete.Length <=
                             ToolRuntimeLimits.MaximumResultEnvelopeBytes
                ? redacted
                : CreatePreview(complete);
            return await FinishAsync(
                context,
                sink,
                new ToolResultSnapshot(
                    context.ToolInvocationId,
                    context.ProviderToolCallId,
                    ToolInvocationStatus.Completed,
                    normalized,
                    Error: null,
                    IsTruncated: complete.Length >
                                 ToolRuntimeLimits.MaximumResultEnvelopeBytes,
                    complete.Length,
                    Hash(complete),
                    attemptNumber));
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException or
                ObjectDisposedException or DecoderFallbackException)
        {
            return await FinishErrorAsync(
                context,
                sink,
                ToolInvocationStatus.Failed,
                ToolErrorCodes.ResultInvalid,
                "Tool result is invalid.",
                attemptNumber);
        }
    }

    private static string ApprovalPrompt(
        ToolInvocationContext context,
        ToolDefinition definition)
    {
        if (definition.Name is { Namespace: "shell", Name: "run" } &&
            context.Arguments.TryGetProperty("command", out var command) &&
            command.ValueKind == JsonValueKind.String)
        {
            return $"Approve shell command?{Environment.NewLine}{command.GetString()}";
        }

        return $"Approve tool '{context.ProviderToolName}'?";
    }

    private async ValueTask<ToolResultSnapshot> RejectAsync(
        ToolInvocationContext context,
        ISessionExecutionSink sink,
        string code,
        string message) =>
        await FinishErrorAsync(
            context,
            sink,
            ToolInvocationStatus.Rejected,
            code,
            message,
            context.PriorAttemptCount);

    private async ValueTask<ToolResultSnapshot> FinishErrorAsync(
        ToolInvocationContext context,
        ISessionExecutionSink sink,
        ToolInvocationStatus status,
        string code,
        string message,
        int attemptCount,
        int? originalByteCount = null,
        string? resultSha256 = null)
    {
        var error = new SessionError(
            code,
            _redactor.RedactText(message),
            IsRetryable: false);
        var canonical = ThreadJournal.Canonicalize(
            JsonSerializer.SerializeToElement(error));
        return await FinishAsync(
            context,
            sink,
            new ToolResultSnapshot(
                context.ToolInvocationId,
                context.ProviderToolCallId,
                status,
                Output: null,
                error,
                IsTruncated: false,
                originalByteCount ?? canonical.Length,
                resultSha256 ?? Hash(canonical),
                attemptCount));
    }

    private async ValueTask<ToolResultSnapshot> FinishAsync(
        ToolInvocationContext context,
        ISessionExecutionSink sink,
        ToolResultSnapshot result)
    {
        Record(ToolInvocationStage.Terminal);
        await sink.EmitAsync(
            new RecordToolInvocationTerminalIntent(
                Guid.CreateVersion7(_timeProvider.GetUtcNow()),
                result),
            CancellationToken.None);

        Record(ToolInvocationStage.TerminalHook);
        if (_terminal is not null)
        {
            try
            {
                await _terminal(result, CancellationToken.None);
            }
            catch (Exception)
            {
                _logger.LogWarning("Tool terminal hook failed.");
            }
        }

        return result;
    }

    private ToolRegistration? ResolveRegistration(ToolInvocationContext context)
    {
        if (!context.Snapshot.ProviderToCanonicalNames.TryGetValue(
                context.ProviderToolName,
                out var canonicalName))
        {
            return null;
        }

        var matches = context.Snapshot.Registrations
            .Where(registration => string.Equals(
                $"{registration.Definition.Name.Namespace}." +
                registration.Definition.Name.Name,
                canonicalName,
                StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static ToolAuthorityDecision DecisionFor(
        ToolEffect effects,
        IReadOnlyList<ToolAuthorityPolicy> authority)
    {
        var decision = ToolAuthorityDecision.Allow;
        foreach (var policy in authority)
        {
            if (policy.Effect == ToolEffect.None
                ? effects == ToolEffect.None
                : (effects & policy.Effect) != 0)
            {
                decision = Strictest(decision, policy.Decision);
            }
        }

        return decision;
    }

    private static ToolAuthorityDecision Strictest(
        ToolAuthorityDecision first,
        ToolAuthorityDecision second) =>
        (ToolAuthorityDecision)Math.Min((int)first, (int)second);

    private static TimeSpan Min(TimeSpan first, TimeSpan second) =>
        first <= second ? first : second;

    private static bool IsStableToolCode(string? code) =>
        code is { Length: > 5 and <= 128 } &&
        code.StartsWith("tool.", StringComparison.Ordinal) &&
        code.All(character =>
            char.IsAsciiLetterOrDigit(character) || character == '.');

    private static JsonElement CreatePreview(byte[] complete)
    {
        var available =
            ToolRuntimeLimits.MaximumResultEnvelopeBytes - PreviewOverheadBytes;
        while (available > 0)
        {
            var headBytes = available * 3 / 4;
            var tailBytes = available - headBytes;
            var head = Utf8Prefix(complete, headBytes);
            var tail = Utf8Suffix(complete, tailBytes);
            var preview = JsonSerializer.SerializeToElement(new
            {
                truncated = true,
                head,
                tail,
            });
            if (ThreadJournal.Canonicalize(preview).Length <=
                ToolRuntimeLimits.MaximumResultEnvelopeBytes -
                PreviewOverheadBytes)
            {
                return preview;
            }

            available /= 2;
        }

        return JsonSerializer.SerializeToElement(new { truncated = true });
    }

    private static string Utf8Prefix(byte[] bytes, int maximumBytes)
    {
        var count = Math.Min(maximumBytes, bytes.Length);
        while (count > 0)
        {
            try
            {
                return StrictUtf8.GetString(bytes, 0, count);
            }
            catch (DecoderFallbackException)
            {
                count--;
            }
        }

        return string.Empty;
    }

    private static string Utf8Suffix(byte[] bytes, int maximumBytes)
    {
        var start = Math.Max(0, bytes.Length - maximumBytes);
        while (start < bytes.Length)
        {
            try
            {
                return StrictUtf8.GetString(bytes, start, bytes.Length - start);
            }
            catch (DecoderFallbackException)
            {
                start++;
            }
        }

        return string.Empty;
    }

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private void Record(ToolInvocationStage stage) => _trace?.Record(stage);
}
