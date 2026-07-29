using System.Security.Cryptography;
using System.Text.Json;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Sessions;

namespace OpenCoWork.Core.Tools;

internal sealed class DynamicToolDefinition
{
    public DynamicToolDefinition(
        string name,
        string description,
        JsonElement inputSchema,
        ToolEffect effects,
        ToolReplaySafety replaySafety)
    {
        Name = name;
        Description = description;
        InputSchema = inputSchema.Clone();
        Effects = effects;
        ReplaySafety = replaySafety;
    }

    public string Name { get; }

    public string Description { get; }

    public JsonElement InputSchema { get; }

    public ToolEffect Effects { get; }

    public ToolReplaySafety ReplaySafety { get; }
}

internal sealed record DynamicToolRegistrationRequest(
    Guid RegistrationId,
    DynamicToolDefinition Definition,
    string DefinitionSha256,
    TimeSpan? LeaseDuration = null);

internal sealed record DynamicToolRegistrationSnapshot(
    Guid ConnectionId,
    Guid ThreadId,
    Guid RegistrationId,
    string DefinitionSha256,
    CapabilityStatus Status,
    RuntimeBindingId RuntimeBindingId,
    DateTimeOffset ExpiresAt);

internal sealed class DynamicToolException(string code, string message)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

internal sealed class DynamicToolRegistry
{
    private const int MaximumToolsPerConnectionThread = 64;
    private static readonly TimeSpan DefaultLease = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumLease = TimeSpan.FromMinutes(5);
    private const ToolEffect KnownEffects =
        ToolEffect.WorkspaceRead |
        ToolEffect.WorkspaceWrite |
        ToolEffect.ProcessExecution |
        ToolEffect.NetworkRead |
        ToolEffect.ExternalMutation;
    private readonly object _gate = new();
    private readonly ToolRuntime _runtime;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<RegistrationKey, Registration> _registrations = [];

    public DynamicToolRegistry(
        ToolRuntime runtime,
        TimeProvider? timeProvider = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public DynamicToolRegistrationSnapshot Register(
        Guid connectionId,
        Guid threadId,
        DynamicToolRegistrationRequest request,
        ToolExecutor executor)
    {
        SessionIds.RequireVersion7(connectionId, nameof(connectionId), "Connection ID");
        SessionIds.RequireVersion7(threadId, nameof(threadId), "Thread ID");
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(executor);
        SessionIds.RequireVersion7(
            request.RegistrationId,
            nameof(request.RegistrationId),
            "Registration ID");
        var duration = request.LeaseDuration ?? DefaultLease;
        Validate(request, duration);
        var key = new RegistrationKey(
            connectionId,
            threadId,
            request.RegistrationId);
        lock (_gate)
        {
            if (_registrations.TryGetValue(key, out var existing))
            {
                if (!string.Equals(
                        existing.DefinitionSha256,
                        request.DefinitionSha256,
                        StringComparison.Ordinal))
                {
                    throw Error(
                        DynamicToolErrorCodes.DefinitionInvalid,
                        "Dynamic Tool definition changes require a new Registration ID.");
                }

                return Snapshot(existing);
            }

            if (_registrations.Keys.Count(item =>
                    item.ConnectionId == connectionId &&
                    item.ThreadId == threadId) >=
                MaximumToolsPerConnectionThread)
            {
                throw Error(
                    DynamicToolErrorCodes.LimitExceeded,
                    "Dynamic Tool registration limit exceeded.");
            }

            var definitionId = new ToolDefinitionId(
                ToolSourceKind.RuntimeDynamic,
                $"{connectionId:D}/{threadId:D}",
                request.RegistrationId.ToString("D"));
            var bindingId = new RuntimeBindingId(
                $"dynamic.{Guid.CreateVersion7(_timeProvider.GetUtcNow()):N}");
            var registration = new Registration(
                key,
                request.DefinitionSha256,
                new ToolRegistration(
                    new ToolDefinition(
                        definitionId,
                        new ToolName(
                            $"dynamic_{connectionId:N}"[..20],
                            request.Definition.Name),
                        request.Definition.Description,
                        request.Definition.InputSchema,
                        request.Definition.Effects,
                        request.Definition.ReplaySafety),
                    bindingId,
                    ToolExposure.Direct,
                    ToolInvocationAudience.Model),
                executor,
                _timeProvider.GetUtcNow() + duration);
            _registrations.Add(key, registration);
            return Snapshot(registration);
        }
    }

    public void GrantConnectionTrust(Guid connectionId)
    {
        SessionIds.RequireVersion7(connectionId, nameof(connectionId), "Connection ID");
        lock (_gate)
        {
            foreach (var registration in _registrations.Values.Where(item =>
                         item.Key.ConnectionId == connectionId &&
                         !item.IsTrusted &&
                         item.ExpiresAt > _timeProvider.GetUtcNow()))
            {
                registration.IsTrusted = true;
                registration.BoundExecutor ??= (
                    arguments,
                    cancellationToken) =>
                    InvokeAsync(registration, arguments, cancellationToken);
                registration.ContextualExecutor ??= (
                    context,
                    cancellationToken) =>
                    context.ThreadId == registration.Key.ThreadId
                        ? InvokeAsync(
                            registration,
                            context.Arguments,
                            cancellationToken)
                        : ValueTask.FromResult(Failure(
                            DynamicToolErrorCodes.Disconnected,
                            "Dynamic Tool is unavailable for this Thread."));
                _runtime.PublishDynamic(
                    registration.Key.ThreadId,
                    registration.Tool,
                    Binding(registration));
            }
        }
    }

    public DynamicToolRegistrationSnapshot Renew(
        Guid connectionId,
        Guid threadId,
        Guid registrationId,
        TimeSpan leaseDuration)
    {
        ValidateLease(leaseDuration);
        lock (_gate)
        {
            var registration = Find(connectionId, threadId, registrationId);
            if (registration.ExpiresAt <= _timeProvider.GetUtcNow())
            {
                throw Error(
                    DynamicToolErrorCodes.LeaseExpired,
                    "Dynamic Tool lease has expired.");
            }

            registration.ExpiresAt = _timeProvider.GetUtcNow() + leaseDuration;
            if (registration.IsTrusted)
            {
                _runtime.PublishBinding(Binding(registration));
            }

            return Snapshot(registration);
        }
    }

    public void Unregister(
        Guid connectionId,
        Guid threadId,
        Guid registrationId)
    {
        Registration registration;
        lock (_gate)
        {
            var key = new RegistrationKey(connectionId, threadId, registrationId);
            if (!_registrations.Remove(key, out registration!))
            {
                throw Error(
                    DynamicToolErrorCodes.NotFound,
                    "Dynamic Tool registration was not found.");
            }
        }

        Remove(registration);
    }

    public void Disconnect(Guid connectionId)
    {
        Registration[] registrations;
        lock (_gate)
        {
            registrations = _registrations.Values
                .Where(item => item.Key.ConnectionId == connectionId)
                .ToArray();
            foreach (var registration in registrations)
            {
                _registrations.Remove(registration.Key);
            }
        }

        foreach (var registration in registrations)
        {
            Remove(registration);
        }
    }

    public void RevokeConnectionTrust(Guid connectionId) =>
        Disconnect(connectionId);

    internal static string ComputeDefinitionSha256(
        DynamicToolDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var value = JsonSerializer.SerializeToElement(new
        {
            definition.Name,
            definition.Description,
            definition.InputSchema,
            definition.Effects,
            definition.ReplaySafety,
        });
        return Convert.ToHexString(
                SHA256.HashData(ThreadJournal.Canonicalize(value)))
            .ToLowerInvariant();
    }

    private async ValueTask<ToolBindingResult> InvokeAsync(
        Registration registration,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var remaining = registration.ExpiresAt - _timeProvider.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            return Failure(
                DynamicToolErrorCodes.LeaseExpired,
                "Dynamic Tool lease has expired.");
        }

        using var lease = new CancellationTokenSource(remaining, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            registration.Lifetime.Token,
            lease.Token);
        try
        {
            return await registration.Executor(arguments, linked.Token);
        }
        catch (OperationCanceledException) when (
            registration.Lifetime.IsCancellationRequested)
        {
            return Failure(
                DynamicToolErrorCodes.Disconnected,
                "Dynamic Tool connection was disconnected.");
        }
        catch (OperationCanceledException) when (lease.IsCancellationRequested)
        {
            return Failure(
                DynamicToolErrorCodes.LeaseExpired,
                "Dynamic Tool lease has expired.");
        }
    }

    private ToolRuntimeBinding Binding(Registration registration) =>
        new(
            registration.Tool.RuntimeBindingId,
            ToolBindingAvailability.Available,
            new ToolBindingLease(
                registration.Key.RegistrationId.ToString("D"),
                registration.ExpiresAt),
            MaximumLease,
            registration.BoundExecutor!,
            registration.Tool.BindingGeneration,
            IsTrusted: true,
            registration.ContextualExecutor);

    private Registration Find(
        Guid connectionId,
        Guid threadId,
        Guid registrationId)
    {
        var key = new RegistrationKey(connectionId, threadId, registrationId);
        return _registrations.GetValueOrDefault(key) ??
               throw Error(
                   DynamicToolErrorCodes.NotFound,
                   "Dynamic Tool registration was not found.");
    }

    private void Remove(Registration registration)
    {
        _runtime.RemoveDynamic(
            registration.Tool.Definition.Id,
            registration.Tool.RuntimeBindingId,
            registration.Tool.BindingGeneration);
        registration.Lifetime.Cancel();
        registration.Lifetime.Dispose();
    }

    private DynamicToolRegistrationSnapshot Snapshot(Registration registration) =>
        new(
            registration.Key.ConnectionId,
            registration.Key.ThreadId,
            registration.Key.RegistrationId,
            registration.DefinitionSha256,
            registration.ExpiresAt <= _timeProvider.GetUtcNow()
                ? CapabilityStatus.Unavailable
                : registration.IsTrusted
                    ? CapabilityStatus.Ready
                    : CapabilityStatus.PendingTrust,
            registration.Tool.RuntimeBindingId,
            registration.ExpiresAt);

    private static void Validate(
        DynamicToolRegistrationRequest request,
        TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(request.Definition);
        ValidateLease(duration);
        if (!IsName(request.Definition.Name) ||
            string.IsNullOrWhiteSpace(request.Definition.Description) ||
            !Enum.IsDefined(request.Definition.ReplaySafety) ||
            (request.Definition.Effects & ~KnownEffects) != 0 ||
            request.DefinitionSha256 is not { Length: 64 } ||
            !request.DefinitionSha256.All(character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            !string.Equals(
                request.DefinitionSha256,
                ComputeDefinitionSha256(request.Definition),
                StringComparison.Ordinal))
        {
            throw Error(
                DynamicToolErrorCodes.DefinitionInvalid,
                "Dynamic Tool definition is invalid.");
        }
    }

    private static void ValidateLease(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || duration > MaximumLease)
        {
            throw Error(
                DynamicToolErrorCodes.DefinitionInvalid,
                "Dynamic Tool lease must be between zero and five minutes.");
        }
    }

    private static bool IsName(string? value) =>
        value is { Length: >= 1 and <= 64 } &&
        value[0] is >= 'a' and <= 'z' &&
        value.All(character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');

    private static ToolBindingResult Failure(string code, string message) =>
        ToolBindingResult.Failure(
            new SessionError(code, message, IsRetryable: false));

    private static DynamicToolException Error(string code, string message) =>
        new(code, message);

    private sealed class Registration(
        RegistrationKey key,
        string definitionSha256,
        ToolRegistration tool,
        ToolExecutor executor,
        DateTimeOffset expiresAt)
    {
        public RegistrationKey Key { get; } = key;

        public string DefinitionSha256 { get; } = definitionSha256;

        public ToolRegistration Tool { get; } = tool;

        public ToolExecutor Executor { get; } = executor;

        public CancellationTokenSource Lifetime { get; } = new();

        public ToolExecutor? BoundExecutor { get; set; }

        public ContextualToolExecutor? ContextualExecutor { get; set; }

        public DateTimeOffset ExpiresAt { get; set; } = expiresAt;

        public bool IsTrusted { get; set; }
    }

    private sealed record RegistrationKey(
        Guid ConnectionId,
        Guid ThreadId,
        Guid RegistrationId);
}
