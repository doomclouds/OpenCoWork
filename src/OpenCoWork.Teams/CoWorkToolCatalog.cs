using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCoWork.Abstractions;

namespace OpenCoWork.Teams;

internal static class CoWorkToolCatalog
{
    private const string SourceId = "opencowork.teams";
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            Converters =
            {
                new JsonStringEnumConverter(
                    JsonNamingPolicy.CamelCase,
                    allowIntegerValues: false),
            },
        };

    public static ToolRegistrationContribution Create(
        Func<ICoWorkService> service,
        CoWorkModuleRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(runtime);
        var tools = new[]
        {
            Tool(
                "subagent",
                "spawn",
                "Spawn a durable direct SubAgent.",
                ToolInvocationAudience.CoWorkDirectParent,
                Schema(
                    ["profileId", "task", "tokenBudget", "workspaceMode"],
                    "expectedRevision"),
                (context, token) => SpawnAsync(service(), runtime, context, token)),
            Tool(
                "subagent",
                "list",
                "List durable direct SubAgents for this parent thread.",
                ToolInvocationAudience.CoWorkDirectParent,
                Schema([]),
                (context, token) => ListSubAgentsAsync(
                    service(),
                    runtime,
                    context,
                    token)),
            Tool(
                "subagent",
                "send",
                "Send a message to an owned direct SubAgent.",
                ToolInvocationAudience.CoWorkDirectParent,
                Schema(["childThreadId", "message"], "expectedRevision"),
                (context, token) => SendSubAgentAsync(
                    service(),
                    runtime,
                    context,
                    token)),
            Tool(
                "subagent",
                "followup",
                "Continue an owned direct SubAgent in its durable thread.",
                ToolInvocationAudience.CoWorkDirectParent,
                Schema(["childThreadId", "task"], "expectedRevision"),
                (context, token) => FollowUpAsync(
                    service(),
                    runtime,
                    context,
                    token)),
            Tool(
                "subagent",
                "cancel",
                "Cancel an owned direct SubAgent.",
                ToolInvocationAudience.CoWorkDirectParent,
                Schema(["childThreadId"], "expectedRevision"),
                (context, token) => CancelSubAgentAsync(
                    service(),
                    runtime,
                    context,
                    token)),
            Tool(
                "mission",
                "manage",
                "Read, activate, or cancel the current Mission.",
                ToolInvocationAudience.CoWorkLeader,
                ActionSchema([], "expectedRevision"),
                (context, token) => ManageMissionAsync(
                    service(),
                    runtime,
                    context,
                    token)),
            Tool(
                "mission",
                "task",
                "Manage Mission Tasks within the caller's role.",
                ToolInvocationAudience.CoWorkLeader |
                ToolInvocationAudience.CoWorkMember,
                ActionSchema(
                    ["taskId"],
                    "expectedRevision",
                    "reason",
                    "memberId"),
                (context, token) => ManageTaskAsync(
                    service(),
                    runtime,
                    context,
                    token)),
            Tool(
                "mission",
                "review",
                "Accept or reject a Mission Task in review.",
                ToolInvocationAudience.CoWorkLeader,
                Schema(
                    ["taskId", "accepted"],
                    "expectedRevision",
                    "comment"),
                (context, token) => ReviewTaskAsync(
                    service(),
                    runtime,
                    context,
                    token)),
            Tool(
                "mailbox",
                "manage",
                "List, send, acknowledge, or retry Mission Mailbox messages.",
                ToolInvocationAudience.CoWorkLeader |
                ToolInvocationAudience.CoWorkMember,
                ActionSchema(
                    [],
                    "expectedRevision",
                    "recipientId",
                    "kind",
                    "body",
                    "taskId",
                    "artifactId",
                    "messageId"),
                (context, token) => ManageMailboxAsync(
                    service(),
                    runtime,
                    context,
                    token)),
            Tool(
                "artifact",
                "manage",
                "List, read, publish, or promote Mission Artifacts.",
                ToolInvocationAudience.CoWorkLeader |
                ToolInvocationAudience.CoWorkMember,
                ActionSchema(
                    [],
                    "expectedRevision",
                    "artifactId",
                    "sourceArea",
                    "sourceRelativePath",
                    "displayName",
                    "mediaType"),
                (context, token) => ManageArtifactAsync(
                    service(),
                    runtime,
                    context,
                    token)),
        };
        return new ToolRegistrationContribution(
            tools.Select(item => item.Registration).ToArray(),
            tools.Select(item => item.Binding).ToArray());
    }

    private static Contribution Tool(
        string toolNamespace,
        string name,
        string description,
        ToolInvocationAudience audience,
        JsonElement schema,
        ContextualToolExecutor execute)
    {
        var id = new ToolDefinitionId(
            ToolSourceKind.CoreNative,
            SourceId,
            $"{toolNamespace}.{name}");
        var bindingId = new RuntimeBindingId($"cowork.{toolNamespace}.{name}");
        return new Contribution(
            new ToolRegistration(
                new ToolDefinition(
                    id,
                    new ToolName(toolNamespace, name),
                    description,
                    schema,
                    ToolEffect.WorkspaceWrite),
                bindingId,
                ToolExposure.Deferred,
                ToolInvocationAudience.Model | audience),
            new ToolRuntimeBinding(
                bindingId,
                ToolBindingAvailability.Available,
                Lease: null,
                TimeSpan.FromMinutes(2),
                static (_, _) => ValueTask.FromResult(
                    ToolBindingResult.Failure(new SessionError(
                        ToolErrorCodes.ExecutionFailed,
                        "Contextual execution is required.",
                        IsRetryable: false))),
                IsTrusted: true,
                ContextualExecutor: execute));
    }

    private static async ValueTask<ToolBindingResult> SpawnAsync(
        ICoWorkService service,
        CoWorkModuleRuntime runtime,
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        if (!TryActor(context, CoWorkActorKind.DirectParent, out var actor))
        {
            return Denied();
        }

        if (Unavailable(runtime) is { } unavailable)
        {
            return unavailable;
        }

        return Project(await service.SpawnSubAgentAsync(
            new SpawnSubAgentRequest(
                Command(context, actor),
                context.ThreadId,
                RequiredGuid(context.Arguments, "profileId"),
                RequiredString(context.Arguments, "task"),
                RequiredInt64(context.Arguments, "tokenBudget"),
                RequiredEnum<CoWorkWorkspaceMode>(
                    context.Arguments,
                    "workspaceMode")),
            cancellationToken), runtime);
    }

    private static async ValueTask<ToolBindingResult> ListSubAgentsAsync(
        ICoWorkService service,
        CoWorkModuleRuntime runtime,
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        if (!TryActor(context, CoWorkActorKind.DirectParent, out var actor))
        {
            return Denied();
        }

        if (Unavailable(runtime) is { } unavailable)
        {
            return unavailable;
        }

        return Project(await service.ListSubAgentsAsync(
            new SubAgentQueryRequest(actor, context.ThreadId),
            cancellationToken), runtime);
    }

    private static async ValueTask<ToolBindingResult> SendSubAgentAsync(
        ICoWorkService service,
        CoWorkModuleRuntime runtime,
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        if (!TryActor(context, CoWorkActorKind.DirectParent, out var actor))
        {
            return Denied();
        }

        if (Unavailable(runtime) is { } unavailable)
        {
            return unavailable;
        }

        return Project(await service.SendSubAgentMessageAsync(
            new SendSubAgentMessageRequest(
                Command(context, actor),
                RequiredGuid(context.Arguments, "childThreadId"),
                RequiredString(context.Arguments, "message")),
            cancellationToken), runtime);
    }

    private static async ValueTask<ToolBindingResult> FollowUpAsync(
        ICoWorkService service,
        CoWorkModuleRuntime runtime,
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        if (!TryActor(context, CoWorkActorKind.DirectParent, out var actor))
        {
            return Denied();
        }

        if (Unavailable(runtime) is { } unavailable)
        {
            return unavailable;
        }

        return Project(await service.FollowUpSubAgentAsync(
            new FollowUpSubAgentRequest(
                Command(context, actor),
                RequiredGuid(context.Arguments, "childThreadId"),
                RequiredString(context.Arguments, "task")),
            cancellationToken), runtime);
    }

    private static async ValueTask<ToolBindingResult> CancelSubAgentAsync(
        ICoWorkService service,
        CoWorkModuleRuntime runtime,
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        if (!TryActor(context, CoWorkActorKind.DirectParent, out var actor))
        {
            return Denied();
        }

        if (Unavailable(runtime) is { } unavailable)
        {
            return unavailable;
        }

        return Project(await service.CancelSubAgentAsync(
            new CancelSubAgentRequest(
                Command(context, actor),
                RequiredGuid(context.Arguments, "childThreadId")),
            cancellationToken), runtime);
    }

    private static async ValueTask<ToolBindingResult> ManageMissionAsync(
        ICoWorkService service,
        CoWorkModuleRuntime runtime,
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        if (!TryActor(context, CoWorkActorKind.Leader, out var actor) ||
            actor.MissionId is not { } missionId)
        {
            return Denied();
        }

        if (Unavailable(runtime) is { } unavailable)
        {
            return unavailable;
        }

        var action = RequiredString(context.Arguments, "action");
        return action switch
        {
            "get" => Project(await service.GetMissionAsync(
                new GetMissionRequest(actor, missionId),
                cancellationToken), runtime),
            "activate" => Project(await service.ActivateMissionAsync(
                new MissionCommandRequest(Command(context, actor), missionId),
                cancellationToken), runtime),
            "cancel" => Project(await service.CancelMissionAsync(
                new MissionCommandRequest(Command(context, actor), missionId),
                cancellationToken), runtime),
            _ => InvalidInput(),
        };
    }

    private static async ValueTask<ToolBindingResult> ManageTaskAsync(
        ICoWorkService service,
        CoWorkModuleRuntime runtime,
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        if (!TryMissionActor(context, out var actor) ||
            actor.MissionId is not { } missionId)
        {
            return Denied();
        }

        if (Unavailable(runtime) is { } unavailable)
        {
            return unavailable;
        }

        var taskId = RequiredGuid(context.Arguments, "taskId");
        var command = new MissionTaskCommandRequest(
            Command(context, actor),
            missionId,
            taskId);
        var action = RequiredString(context.Arguments, "action");
        return action switch
        {
            "block" => Project(await service.BlockMissionTaskAsync(
                new BlockMissionTaskRequest(
                    command.Command,
                    missionId,
                    taskId,
                    RequiredString(context.Arguments, "reason")),
                cancellationToken), runtime),
            "unblock" => Project(await service.UnblockMissionTaskAsync(
                command,
                cancellationToken), runtime),
            "retry" => Project(await service.RetryMissionTaskAsync(
                command,
                cancellationToken), runtime),
            "waive" => Project(await service.WaiveMissionTaskAsync(
                command,
                cancellationToken), runtime),
            "remove" => Project(await service.RemoveMissionTaskAsync(
                command,
                cancellationToken), runtime),
            "reassign" => Project(await service.ReassignMissionTaskAsync(
                new ReassignMissionTaskRequest(
                    command.Command,
                    missionId,
                    taskId,
                    RequiredGuid(context.Arguments, "memberId")),
                cancellationToken), runtime),
            _ => InvalidInput(),
        };
    }

    private static async ValueTask<ToolBindingResult> ReviewTaskAsync(
        ICoWorkService service,
        CoWorkModuleRuntime runtime,
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        if (!TryActor(context, CoWorkActorKind.Leader, out var actor) ||
            actor.MissionId is not { } missionId)
        {
            return Denied();
        }

        if (Unavailable(runtime) is { } unavailable)
        {
            return unavailable;
        }

        return Project(await service.ReviewMissionTaskAsync(
            new ReviewMissionTaskRequest(
                Command(context, actor),
                missionId,
                RequiredGuid(context.Arguments, "taskId"),
                RequiredBoolean(context.Arguments, "accepted"),
                OptionalString(context.Arguments, "comment")),
            cancellationToken), runtime);
    }

    private static async ValueTask<ToolBindingResult> ManageMailboxAsync(
        ICoWorkService service,
        CoWorkModuleRuntime runtime,
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        if (!TryMissionActor(context, out var actor) ||
            actor.MissionId is not { } missionId)
        {
            return Denied();
        }

        if (Unavailable(runtime) is { } unavailable)
        {
            return unavailable;
        }

        var action = RequiredString(context.Arguments, "action");
        return action switch
        {
            "list" => Project(await service.ListMailboxMessagesAsync(
                new ListMailboxMessagesRequest(actor, missionId),
                cancellationToken), runtime),
            "send" => Project(await service.SendMailboxMessageAsync(
                new SendMailboxMessageRequest(
                    Command(context, actor),
                    missionId,
                    RequiredGuid(context.Arguments, "recipientId"),
                    RequiredEnum<CoWorkMailboxKind>(context.Arguments, "kind"),
                    RequiredString(context.Arguments, "body"),
                    OptionalGuid(context.Arguments, "taskId"),
                    OptionalGuid(context.Arguments, "artifactId")),
                cancellationToken), runtime),
            "acknowledge" => Project(await service.AcknowledgeMailboxMessageAsync(
                new MailboxMessageCommandRequest(
                    Command(context, actor),
                    RequiredGuid(context.Arguments, "messageId")),
                cancellationToken), runtime),
            "retry" => Project(await service.RetryMailboxMessageAsync(
                new MailboxMessageCommandRequest(
                    Command(context, actor),
                    RequiredGuid(context.Arguments, "messageId")),
                cancellationToken), runtime),
            _ => InvalidInput(),
        };
    }

    private static async ValueTask<ToolBindingResult> ManageArtifactAsync(
        ICoWorkService service,
        CoWorkModuleRuntime runtime,
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        if (!TryMissionActor(context, out var actor) ||
            actor.MissionId is not { } missionId)
        {
            return Denied();
        }

        if (Unavailable(runtime) is { } unavailable)
        {
            return unavailable;
        }

        var action = RequiredString(context.Arguments, "action");
        return action switch
        {
            "list" => Project(await service.ListArtifactsAsync(
                new ListArtifactsRequest(actor, missionId),
                cancellationToken), runtime),
            "get" => Project(await service.GetArtifactAsync(
                new GetArtifactRequest(
                    actor,
                    RequiredGuid(context.Arguments, "artifactId")),
                cancellationToken), runtime),
            "publish" => Project(await service.PublishArtifactAsync(
                new PublishArtifactRequest(
                    Command(context, actor),
                    missionId,
                    context.CoWorkProvenance!.AgentRunId,
                    RequiredEnum<CoWorkFileArea>(context.Arguments, "sourceArea"),
                    RequiredString(context.Arguments, "sourceRelativePath"),
                    RequiredString(context.Arguments, "displayName"),
                    RequiredString(context.Arguments, "mediaType")),
                cancellationToken), runtime),
            "promote" => Project(await service.PromoteArtifactAsync(
                new PromoteArtifactRequest(
                    Command(context, actor),
                    RequiredGuid(context.Arguments, "artifactId")),
                cancellationToken), runtime),
            _ => InvalidInput(),
        };
    }

    private static ToolBindingResult Project<T>(
        CoWorkResult<T> result,
        CoWorkModuleRuntime runtime)
    {
        if (runtime.BindingAvailability != ToolBindingAvailability.Available)
        {
            return ToolBindingResult.Failure(new SessionError(
                ToolErrorCodes.BindingUnavailable,
                "CoWork tools are unavailable.",
                IsRetryable: true));
        }

        if (result.Error is { } error)
        {
            return ToolBindingResult.Failure(new SessionError(
                error.Code,
                "CoWork operation failed.",
                error.IsRetryable));
        }

        return ToolBindingResult.Success(JsonSerializer.SerializeToElement(
            new
            {
                result.CoWorkRevision,
                result.Value,
            },
            JsonOptions));
    }

    private static ToolBindingResult? Unavailable(CoWorkModuleRuntime runtime) =>
        runtime.BindingAvailability == ToolBindingAvailability.Available
            ? null
            : ToolBindingResult.Failure(new SessionError(
                ToolErrorCodes.BindingUnavailable,
                "CoWork tools are unavailable.",
                IsRetryable: true));

    private static bool TryMissionActor(
        ToolInvocationContext context,
        out CoWorkActorContext actor) =>
        TryActor(context, CoWorkActorKind.Leader, out actor) ||
        TryActor(context, CoWorkActorKind.Member, out actor);

    private static bool TryActor(
        ToolInvocationContext context,
        CoWorkActorKind expected,
        out CoWorkActorContext actor)
    {
        var provenance = context.CoWorkProvenance;
        var valid = expected switch
        {
            CoWorkActorKind.DirectParent =>
                provenance is null || provenance.RunKind == CoWorkAgentRunKind.Direct,
            CoWorkActorKind.Leader =>
                provenance?.RunKind is
                    CoWorkAgentRunKind.LeaderPlanning or
                    CoWorkAgentRunKind.LeaderReview or
                    CoWorkAgentRunKind.LeaderSynthesis,
            CoWorkActorKind.Member =>
                provenance?.RunKind == CoWorkAgentRunKind.MissionTask,
            _ => false,
        };
        actor = valid
            ? new CoWorkActorContext(
                expected,
                $"tool:{context.ThreadId:D}",
                context.ThreadId,
                provenance?.MissionId,
                provenance?.MemberId)
            : new CoWorkActorContext(expected, string.Empty);
        return valid;
    }

    private static CoWorkCommandContext Command(
        ToolInvocationContext context,
        CoWorkActorContext actor) =>
        new(
            context.ToolInvocationId,
            actor,
            OptionalInt64(context.Arguments, "expectedRevision"),
            context.CorrelationId);

    private static ToolBindingResult Denied() =>
        ToolBindingResult.Failure(new SessionError(
            CoWorkErrorCodes.PermissionDenied,
            "CoWork tool is not available to this actor.",
            IsRetryable: false));

    private static ToolBindingResult InvalidInput() =>
        ToolBindingResult.Failure(new SessionError(
            ToolErrorCodes.InputInvalid,
            "CoWork tool input is invalid.",
            IsRetryable: false));

    private static JsonElement Schema(
        string[] required,
        params string[] optional) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["type"] = "object",
            ["properties"] = required
                .Concat(optional)
                .Distinct(StringComparer.Ordinal)
                .ToDictionary(
                    name => name,
                    name => (object)new
                    {
                        type = name is "accepted"
                            ? "boolean"
                            : name is "tokenBudget" or "expectedRevision"
                                ? "integer"
                                : "string",
                    },
                    StringComparer.Ordinal),
            ["required"] = required,
            ["additionalProperties"] = false,
        });

    private static JsonElement ActionSchema(
        string[] required,
        params string[] optional) =>
        Schema(["action", .. required], optional);

    private static string RequiredString(JsonElement arguments, string name) =>
        arguments.GetProperty(name).GetString() is { } value &&
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"{name} is required.");

    private static string? OptionalString(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) &&
        value.ValueKind != JsonValueKind.Null
            ? value.GetString()
            : null;

    private static Guid RequiredGuid(JsonElement arguments, string name) =>
        arguments.GetProperty(name).GetGuid();

    private static Guid? OptionalGuid(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) &&
        value.ValueKind != JsonValueKind.Null
            ? value.GetGuid()
            : null;

    private static long RequiredInt64(JsonElement arguments, string name) =>
        arguments.GetProperty(name).GetInt64();

    private static long? OptionalInt64(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) &&
        value.ValueKind != JsonValueKind.Null
            ? value.GetInt64()
            : null;

    private static bool RequiredBoolean(JsonElement arguments, string name) =>
        arguments.GetProperty(name).GetBoolean();

    private static T RequiredEnum<T>(JsonElement arguments, string name)
        where T : struct, Enum
    {
        var text = RequiredString(arguments, name);
        foreach (var value in Enum.GetValues<T>())
        {
            var enumName = value.ToString();
            var wire = char.ToLowerInvariant(enumName[0]) + enumName[1..];
            if (string.Equals(wire, text, StringComparison.Ordinal))
            {
                return value;
            }
        }

        throw new ArgumentException($"{name} is invalid.");
    }

    private sealed record Contribution(
        ToolRegistration Registration,
        ToolRuntimeBinding Binding);
}
