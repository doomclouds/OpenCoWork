using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Capabilities;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Logging;
using OpenCoWork.Core.Sessions;
using OpenCoWork.Core.Tools;
using OpenCoWork.Core.Workspaces;

namespace OpenCoWork.Core.Agents;

public static class OpenCoWorkAgentExtensions
{
    public static void ValidateOpenCoWorkAgentModel(
        this IServiceProvider services,
        string providerId,
        string modelId)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.GetRequiredService<ProviderRegistry>()
            .Resolve(providerId, modelId);
    }

    public static IServiceCollection AddOpenCoWorkAgentRuntime(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ModelsConfig>();
        services.TryAddSingleton<ToolsConfig>();
        services.TryAddSingleton(serviceProvider =>
            new ProviderDeclarationCatalog(
                serviceProvider.GetRequiredService<OpenCoWorkPaths>()));
        services.TryAddSingleton(serviceProvider =>
            new ToolRuntime(
                serviceProvider.GetRequiredService<OpenCoWorkPaths>(),
                serviceProvider.GetRequiredService<ModelsConfig>(),
                serviceProvider.GetService<CoreSourceControlTool>(),
                serviceProvider.GetService<BackgroundTerminalRuntime>(),
                serviceProvider.GetService<WorkspaceMemoryRuntime>(),
                serviceProvider.GetServices<ToolRegistrationContribution>()));
        services.TryAddSingleton(serviceProvider =>
        {
            var snapshot =
                serviceProvider.GetService<EffectiveConfigSnapshot>();
            return snapshot is null
                ? new SecretRedactor([])
                : SecretRedactor.FromSnapshot(snapshot);
        });
        services.TryAddSingleton<ISensitiveDataService>(serviceProvider =>
            serviceProvider.GetRequiredService<SecretRedactor>());
        services.TryAddSingleton<IProviderOsSecretStore>(
            static _ => ProviderOsSecretStore.Create());
        services.TryAddSingleton(serviceProvider =>
            new ProviderAuthService(
                serviceProvider.GetRequiredService<ProviderDeclarationCatalog>(),
                serviceProvider.GetRequiredService<IProviderOsSecretStore>(),
                serviceProvider.GetRequiredService<SecretRedactor>(),
                paths: serviceProvider.GetRequiredService<OpenCoWorkPaths>()));
        services.TryAddSingleton(serviceProvider =>
            new ProviderRegistry(
                serviceProvider.GetRequiredService<ModelsConfig>(),
                AppContext.BaseDirectory,
                serviceProvider.GetRequiredService<OpenCoWorkPaths>().WorkspaceRoot,
                serviceProvider.GetRequiredService<ProviderDeclarationCatalog>()));
        services.TryAddSingleton(serviceProvider =>
            new AgentFactory(
                serviceProvider.GetRequiredService<ProviderRegistry>(),
                serviceProvider.GetRequiredService<OpenCoWorkPaths>(),
                serviceProvider.GetRequiredService<ToolRuntime>(),
                serviceProvider.GetRequiredService<ToolsConfig>(),
                serviceProvider.GetService<WorkspaceCapabilityRuntime>()));
        services.TryAddSingleton<IToolInvocationPipeline>(serviceProvider =>
        {
            var hooks = serviceProvider.GetService<CapabilityHookRuntime>();
            return new ToolInvocationPipeline(
                serviceProvider.GetRequiredService<ToolRuntime>(),
                serviceProvider.GetRequiredService<SecretRedactor>(),
                timeProvider: serviceProvider.GetService<TimeProvider>(),
                preToolUse: hooks is null ? null : hooks.PreToolUseAsync,
                terminal: hooks is null ? null : hooks.ToolTerminalAsync);
        });
        services.TryAddSingleton(static _ =>
            DeepSeekResponsesClient.CreateSharedHttpClient());
        services.TryAddSingleton(serviceProvider =>
            new AgentRuntimeExecutor(
                serviceProvider.GetRequiredService<AgentFactory>(),
                serviceProvider.GetRequiredService<OpenCoWorkPaths>(),
                serviceProvider.GetRequiredService<HttpClient>(),
                serviceProvider.GetRequiredService<IToolInvocationPipeline>(),
                serviceProvider.GetRequiredService<SecretRedactor>(),
                serviceProvider.GetRequiredService<ProviderAuthService>(),
                serviceProvider.GetService<TimeProvider>()));
        services.TryAddSingleton<ISessionExecutor>(serviceProvider =>
            serviceProvider.GetRequiredService<AgentRuntimeExecutor>());
        return services;
    }
}

internal sealed record ProviderModelRegistration(
    string ProviderId,
    string ModelId,
    Uri BaseUri,
    string? AuthProfileId,
    ProviderAuthPlacement AuthPlacement,
    string TokenizerProfileId,
    string TokenizerProfileVersion,
    string ChatTemplateId,
    string ChatTemplateVersion,
    int ContextWindowTokens,
    int MaxOutputTokens,
    string ReasoningEffort,
    string ConfigurationSha256,
    ModelTokenizer Tokenizer,
    bool SupportsToolCalls,
    TimeSpan ResponseHeaderTimeout,
    TimeSpan StreamIdleTimeout,
    string? LegacyApiKey = null);

internal sealed class ProviderRegistry
{
    private readonly object _gate = new();
    private readonly ModelsConfig _models;
    private readonly FrozenProviderCredentials? _legacyCredentials;
    private readonly bool _unsupportedProviderConfiguration;
    private readonly string _bundledTokenizerBaseDirectory;
    private readonly string _customTokenizerBaseDirectory;
    private readonly Dictionary<string, ProviderModelRegistration> _resolved =
        new(StringComparer.Ordinal);

    public ProviderRegistry(
        ModelsConfig models,
        FrozenProviderCredentials credentials,
        string bundledTokenizerBaseDirectory,
        string customTokenizerBaseDirectory)
        : this(
            models,
            bundledTokenizerBaseDirectory,
            customTokenizerBaseDirectory,
            declarations: null,
            credentials)
    {
    }

    public ProviderRegistry(
        ModelsConfig models,
        string bundledTokenizerBaseDirectory,
        string customTokenizerBaseDirectory,
        ProviderDeclarationCatalog? declarations = null)
        : this(
            models,
            bundledTokenizerBaseDirectory,
            customTokenizerBaseDirectory,
            declarations,
            legacyCredentials: null)
    {
    }

    private ProviderRegistry(
        ModelsConfig models,
        string bundledTokenizerBaseDirectory,
        string customTokenizerBaseDirectory,
        ProviderDeclarationCatalog? declarations,
        FrozenProviderCredentials? legacyCredentials)
    {
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _legacyCredentials = legacyCredentials;
        _unsupportedProviderConfiguration =
            declarations?.HasUnsupportedProviderConfiguration == true;
        ArgumentException.ThrowIfNullOrWhiteSpace(bundledTokenizerBaseDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(customTokenizerBaseDirectory);
        _bundledTokenizerBaseDirectory =
            Path.GetFullPath(bundledTokenizerBaseDirectory);
        _customTokenizerBaseDirectory =
            Path.GetFullPath(customTokenizerBaseDirectory);
    }

    public ProviderModelRegistration Resolve(string providerId, string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        var key = providerId + "\0" + modelId;
        lock (_gate)
        {
            if (_resolved.TryGetValue(key, out var existing))
            {
                return existing;
            }

            if (_unsupportedProviderConfiguration)
            {
                throw new AgentPreparationException(
                    AgentErrorCodes.ContextInputInvalid,
                    "Legacy workspace Provider configuration is unsupported; remove .opencowork/providers.json.");
            }

            ProviderModelRegistration registration;
            if (_models.Providers.TryGetValue(providerId, out var provider) &&
                provider.Models.TryGetValue(modelId, out var model))
            {
                registration = ResolveBuiltIn(providerId, modelId, provider, model);
            }
            else
            {
                throw new AgentPreparationException(
                    AgentErrorCodes.ContextInputInvalid,
                    "The configured provider/model selection is unavailable.");
            }

            _resolved.Add(key, registration);
            return registration;
        }
    }

    internal CapabilityContributionSet CreateCoreContributions()
    {
        CapabilityContribution[] items =
        [
            new(
                CapabilityKind.AuthProfile,
                ModelsConfig.AuthProfileId,
                "DeepSeek authentication",
                "DeepSeek API key from the process environment or workspace-scoped OS secret store.",
                CapabilityStatus.Ready,
                [],
                generation: 1,
                []),
            new(
                CapabilityKind.Provider,
                ModelsConfig.ProviderId,
                "DeepSeek",
                "Built-in DeepSeek Responses provider.",
                CapabilityStatus.Ready,
                [],
                generation: 1,
                []),
            new(
                CapabilityKind.Model,
                $"{ModelsConfig.ProviderId}/{ModelsConfig.FlashModelId}",
                ModelsConfig.FlashModelId,
                "DeepSeek V4 Flash.",
                CapabilityStatus.Ready,
                [],
                generation: 1,
                []),
        ];

        var digest = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(
                    '\n',
                    items.Select(item => $"{item.Kind}\0{item.Id}")))))
            .ToLowerInvariant();
        return new CapabilityContributionSet(
            new CapabilitySourceDescriptor(
                CapabilitySourceKind.Core,
                "opencowork.providers",
                "1",
                digest),
            items);
    }

    private ProviderModelRegistration ResolveBuiltIn(
        string providerId,
        string modelId,
        ProviderConfig provider,
        ModelConfig model)
    {
        var tokenizer = ModelSelectionPreflight.Validate(
            _models,
            providerId,
            modelId,
            _bundledTokenizerBaseDirectory,
            _customTokenizerBaseDirectory);
        var profile = TokenizerProfiles.TryGetForModel(modelId, out var builtIn)
            ? builtIn
            : null;
        var legacyApiKey = _legacyCredentials?.GetRequired(providerId);
        return new ProviderModelRegistration(
            providerId,
            modelId,
            new Uri(
                string.Equals(providerId, ModelsConfig.ProviderId, StringComparison.Ordinal)
                    ? ModelsConfig.BaseUrl + "/"
                    : provider.BaseUrl.TrimEnd('/') + "/",
                UriKind.Absolute),
            string.Equals(providerId, ModelsConfig.ProviderId, StringComparison.Ordinal)
                ? ModelsConfig.AuthProfileId
                : $"core/{providerId}",
            ProviderAuthPlacement.Bearer,
            model.TokenizerProfileId,
            model.TokenizerProfileVersion,
            profile?.ChatTemplateId ?? "openai-compatible-chat",
            profile?.ChatTemplateVersion ?? "1",
            model.ContextWindowTokens,
            model.MaxOutputTokens,
            _models.ReasoningEffort,
            ConfigurationHash(
                providerId,
                modelId,
                provider,
                model,
                _models.ReasoningEffort),
            tokenizer,
            SupportsToolCalls: true,
            TimeSpan.FromSeconds(120),
            TimeSpan.FromSeconds(120),
            legacyApiKey);
    }

    private static string ConfigurationHash(
        string providerId,
        string modelId,
        ProviderConfig provider,
        ModelConfig model,
        string reasoningEffort)
    {
        var canonical = string.Join(
            '\n',
            providerId,
            modelId,
            provider.BaseUrl.TrimEnd('/'),
            provider.ApiKey.Environment,
            reasoningEffort,
            model.TokenizerProfileId,
            model.TokenizerProfileVersion,
            model.ContextWindowTokens,
            model.MaxOutputTokens,
            model.TokenizerSha256 ?? string.Empty);
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

}

internal enum AgentInvocationDraftDisposition
{
    Ready,
    CompactionRequired,
}

internal static class ProviderResponsesHistory
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static IReadOnlyList<JsonElement> Build(
        IEnumerable<SessionItemSnapshot> source,
        Guid? activeTurnId = null,
        bool allowIncompleteFinalToolGroup = false)
    {
        var items = source
            .Where(item =>
                item.Status == SessionItemStatus.Completed &&
                item.Type is
                    SessionItemType.UserMessage or
                    SessionItemType.AgentMessage or
                    SessionItemType.Reasoning or
                    SessionItemType.ToolCall or
                    SessionItemType.ToolResult)
            .OrderBy(item => item.Sequence)
            .ThenBy(item => item.ItemId)
            .ToArray();
        var byId = items.ToDictionary(item => item.ItemId);
        var linkedAgentIds = items
            .Where(item => item.Type == SessionItemType.ToolCall)
            .Select(item => item.Content)
            .OfType<ToolCallItemContent>()
            .Where(content => content.AgentMessageItemId is not null)
            .Select(content => content.AgentMessageItemId!.Value)
            .ToHashSet();
        var consumedResults = new HashSet<Guid>();
        var input = new List<JsonElement>();
        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            switch (item.Type)
            {
                case SessionItemType.UserMessage:
                    input.Add(Message("user", Text(item)));
                    break;
                case SessionItemType.AgentMessage:
                    if (!linkedAgentIds.Contains(item.ItemId))
                    {
                        input.Add(Message("assistant", Text(item)));
                    }

                    break;
                case SessionItemType.Reasoning:
                    if (item.TurnId == activeTurnId)
                    {
                        input.Add(Reasoning(Text(item)));
                    }

                    break;
                case SessionItemType.ToolCall:
                    var allowIncomplete = allowIncompleteFinalToolGroup &&
                                          !items.Skip(index + 1).Any(candidate =>
                                              candidate.Type is
                                                  SessionItemType.UserMessage or
                                                  SessionItemType.AgentMessage or
                                                  SessionItemType.ToolCall);
                    AddToolGroup(
                        items,
                        byId,
                        item,
                        index,
                        consumedResults,
                        input,
                        allowIncomplete);
                    break;
                case SessionItemType.ToolResult:
                    if (!consumedResults.Contains(item.ItemId))
                    {
                        throw InvalidHistory();
                    }

                    break;
            }
        }

        return input.AsReadOnly();
    }

    public static string ToolResultEnvelope(ToolResultSnapshot result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var value = JsonSerializer.SerializeToElement(result, JsonOptions);
        return Encoding.UTF8.GetString(ThreadJournal.Canonicalize(value));
    }

    private static void AddToolGroup(
        IReadOnlyList<SessionItemSnapshot> items,
        IReadOnlyDictionary<Guid, SessionItemSnapshot> byId,
        SessionItemSnapshot item,
        int itemIndex,
        HashSet<Guid> consumedResults,
        List<JsonElement> input,
        bool allowIncomplete)
    {
        if (item.Content is not ToolCallItemContent toolCall ||
            toolCall.Calls.Count == 0 ||
            toolCall.Calls
                .Select(call => call.ProviderToolCallId)
                .Distinct(StringComparer.Ordinal)
                .Count() != toolCall.Calls.Count)
        {
            throw InvalidHistory();
        }

        var content = string.Empty;
        if (toolCall.AgentMessageItemId is { } agentItemId)
        {
            if (!byId.TryGetValue(agentItemId, out var agentItem) ||
                agentItem.TurnId != item.TurnId ||
                agentItem.Sequence >= item.Sequence ||
                agentItem.Type != SessionItemType.AgentMessage)
            {
                throw InvalidHistory();
            }

            content = Text(agentItem);
        }

        if (content.Length != 0)
        {
            input.Add(Message("assistant", content));
        }

        foreach (var call in toolCall.Calls)
        {
            input.Add(JsonSerializer.SerializeToElement(new
            {
                type = "function_call",
                call_id = call.ProviderToolCallId,
                name = call.ProviderToolName,
                arguments = Encoding.UTF8.GetString(
                    ThreadJournal.Canonicalize(call.Arguments)),
            }));
        }

        var resultIndex = 0;
        for (var index = itemIndex + 1;
             index < items.Count && resultIndex < toolCall.Calls.Count;
             index++)
        {
            var candidate = items[index];
            if (candidate.Type == SessionItemType.ToolResult)
            {
                if (candidate.TurnId != item.TurnId ||
                    candidate.Content is not ToolResultItemContent result ||
                    !string.Equals(
                        result.Result.ProviderToolCallId,
                        toolCall.Calls[resultIndex].ProviderToolCallId,
                        StringComparison.Ordinal))
                {
                    throw InvalidHistory();
                }

                input.Add(JsonSerializer.SerializeToElement(new
                {
                    type = "function_call_output",
                    call_id = result.Result.ProviderToolCallId,
                    output = ToolResultEnvelope(result.Result),
                }));
                consumedResults.Add(candidate.ItemId);
                resultIndex++;
            }
            else if (candidate.Type is
                     SessionItemType.UserMessage or
                     SessionItemType.AgentMessage or
                     SessionItemType.ToolCall)
            {
                break;
            }
        }

        if (resultIndex != toolCall.Calls.Count && !allowIncomplete)
        {
            throw InvalidHistory();
        }
    }

    private static string Text(SessionItemSnapshot item) =>
        item.Content is TextItemContent text
            ? text.Text
            : throw InvalidHistory();

    private static JsonElement Message(string role, string content) =>
        JsonSerializer.SerializeToElement(new
        {
            type = "message",
            role,
            content,
        });

    private static JsonElement Reasoning(string content) =>
        JsonSerializer.SerializeToElement(new
        {
            type = "reasoning",
            content,
        });

    private static AgentPreparationException InvalidHistory() =>
        new(
            AgentErrorCodes.ContextInputInvalid,
            "Model history contains an invalid tool message group.");
}

internal sealed record AgentInvocationDraft(
    AgentInvocationDraftDisposition Disposition,
    ProviderModelRegistration Provider,
    AgentInvocationSnapshot Snapshot,
    AgentPromptMaterialization ResponsePrompt,
    AgentPromptMaterialization CompactionPrompt,
    IReadOnlyList<JsonElement> Input,
    IReadOnlyList<DeepSeekResponsesTool> Tools,
    int InputTokenCount,
    int UsableInputBudgetTokens);

internal sealed record AgentExecutionDraft(
    AgentInvocationDraft Draft,
    IDisposable? PluginSnapshotLease);

internal sealed class AgentFactory(
    ProviderRegistry providers,
    OpenCoWorkPaths paths,
    ToolRuntime? tools = null,
    ToolsConfig? toolsConfig = null,
    WorkspaceCapabilityRuntime? capabilities = null)
{
    private readonly ProviderRegistry _providers =
        providers ?? throw new ArgumentNullException(nameof(providers));
    private readonly OpenCoWorkPaths _paths =
        paths ?? throw new ArgumentNullException(nameof(paths));
    private readonly ToolRuntime _tools = tools ?? new ToolRuntime();
    private readonly ToolsConfig _toolsConfig = toolsConfig ?? new ToolsConfig();
    private readonly WorkspaceCapabilityRuntime? _capabilities = capabilities;

    public AgentInvocationDraft Create(
        AgentSession session,
        Guid invocationId,
        WorkspaceInstructionDocument? instructions)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentOutOfRangeException.ThrowIfEqual(invocationId, Guid.Empty);
        if (session.Turn.ThreadId != session.Thread.ThreadId ||
            session.Thread.ActiveTurnId != session.Turn.TurnId ||
            session.Turn.Status != TurnStatus.Running ||
            string.IsNullOrWhiteSpace(session.Thread.ProviderId) ||
            string.IsNullOrWhiteSpace(session.Thread.ModelId))
        {
            throw new AgentPreparationException(
                AgentErrorCodes.ContextInputInvalid,
                "The agent session snapshot is invalid.");
        }

        var provider = _providers.Resolve(
            session.Thread.ProviderId,
            session.Thread.ModelId);
        var frozen = session.Invocation;
        using var capabilityLease = frozen is null
            ? AcquireCapabilitySnapshot()
            : null;
        var capabilityRevision =
            frozen?.CapabilityRevision ?? capabilityLease?.Catalog.Revision ?? 0;
        var skillSnapshot =
            frozen?.Skills ?? capabilityLease?.Skills ?? EffectiveSkillSnapshot.Empty;
        EffectiveToolSnapshot toolSnapshot;
        if (frozen is not null)
        {
            if (frozen.InvocationId != invocationId || frozen.Tools is null)
            {
                throw new AgentPreparationException(
                    AgentErrorCodes.ContextInputInvalid,
                    "The frozen agent invocation is invalid.");
            }

            toolSnapshot = frozen.Tools;
        }
        else
        {
            try
            {
                toolSnapshot = _tools.BuildSnapshot(
                    session.Turn.EffectiveAgentMode,
                    _toolsConfig,
                    session.Thread);
            }
            catch (ToolRuntimeException exception)
            {
                throw new AgentPreparationException(exception.Code, exception.Message);
            }
        }

        var tools = provider.SupportsToolCalls
            ? _tools.CreateProviderDefinitions(
                toolSnapshot,
                session.ActivatedDeferredTools)
            : [];
        var workspaceName = new DirectoryInfo(_paths.WorkspaceRoot).Name;
        if (string.IsNullOrEmpty(workspaceName))
        {
            workspaceName = "workspace";
        }

        var responsePrompt = AgentPrompts.CreateResponse(
            session.Turn.EffectiveAgentMode,
            workspaceName,
            instructions,
            provider.Tokenizer,
            skillSnapshot);
        var compactionPrompt =
            AgentPrompts.CreateCompaction(provider.Tokenizer);
        if (frozen is not null &&
            !FrozenInvocationMatches(
                frozen,
                session,
                provider,
                responsePrompt,
                compactionPrompt,
                toolSnapshot))
        {
            throw new AgentPreparationException(
                AgentErrorCodes.ContextInputInvalid,
                "The frozen agent invocation no longer matches the runtime.");
        }

        if (frozen is not null)
        {
            provider = provider with
            {
                ReasoningEffort = frozen.ReasoningEffort,
                ConfigurationSha256 = frozen.ConfigurationSha256,
            };
        }

        if (session.CompactionCheckpoint is { } checkpoint &&
            !IsValidCheckpoint(
                checkpoint,
                session,
                provider,
                compactionPrompt))
        {
            throw new AgentPreparationException(
                AgentErrorCodes.ContextInputInvalid,
                "The compaction checkpoint is invalid.");
        }

        var hasIncompleteToolInvocation = session.ModelHistory
            .Where(item =>
                item.TurnId == session.Turn.TurnId &&
                item.Type == SessionItemType.ToolCall &&
                item.Status == SessionItemStatus.Completed)
            .Any(item =>
                item.Content is ToolCallItemContent toolCall &&
                Enumerable.Range(0, toolCall.Calls.Count).Any(callIndex =>
                    !session.ToolInvocations.Any(state =>
                        state.ToolCallItemId == item.ItemId &&
                        state.CallIndex == callIndex &&
                        state.Invocation.CompletedAt is not null)));
        var input = BuildInput(
            session,
            hasIncompleteToolInvocation);
        var inputTokenCount = CountPromptTokens(
            provider.Tokenizer,
            responsePrompt.SystemMessage,
            input,
            tools);
        var usableInputBudget =
            provider.ContextWindowTokens - provider.MaxOutputTokens;
        var currentInput = ProviderResponsesHistory.Build(
            session.ModelHistory.Where(item =>
                item.TurnId == session.Turn.TurnId),
            session.Turn.TurnId,
            hasIncompleteToolInvocation);
        var fixedInputCount = CountPromptTokens(
            provider.Tokenizer,
            responsePrompt.SystemMessage,
            currentInput,
            tools);
        if (fixedInputCount > usableInputBudget)
        {
            throw new AgentPreparationException(
                AgentErrorCodes.ContextInputTooLarge,
                "The current input exceeds the model context budget.");
        }

        var disposition = inputTokenCount > usableInputBudget * 8L / 10L
            ? AgentInvocationDraftDisposition.CompactionRequired
            : AgentInvocationDraftDisposition.Ready;
        var snapshot = frozen ?? new AgentInvocationSnapshot(
            invocationId,
            provider.ProviderId,
            provider.ModelId,
            provider.TokenizerProfileId,
            provider.TokenizerProfileVersion,
            session.Turn.EffectiveAgentMode,
            responsePrompt.Snapshot,
            compactionPrompt.Snapshot,
            responsePrompt.WorkspaceInstructions,
            provider.ContextWindowTokens,
            provider.MaxOutputTokens,
            provider.ConfigurationSha256,
            toolSnapshot,
            capabilityRevision,
            skillSnapshot,
            provider.ReasoningEffort);
        return new AgentInvocationDraft(
            disposition,
            provider,
            snapshot,
            responsePrompt,
            compactionPrompt,
            Array.AsReadOnly(input),
            tools,
            inputTokenCount,
            usableInputBudget);
    }

    public AgentExecutionDraft CreateForExecution(
        AgentSession session,
        Guid invocationId,
        WorkspaceInstructionDocument? instructions)
    {
        var draft = Create(session, invocationId, instructions);
        return new AgentExecutionDraft(
            draft,
            AcquirePluginSnapshot(
                draft.Snapshot.Tools ??
                throw new AgentPreparationException(
                    AgentErrorCodes.ContextInputInvalid,
                    "The frozen tool snapshot is missing.")));
    }

    internal IReadOnlyList<DeepSeekResponsesTool> CreateProviderDefinitions(
        AgentInvocationDraft draft,
        IReadOnlyCollection<ToolDefinitionId> activatedDeferredTools) =>
        draft.Provider.SupportsToolCalls
            ? _tools.CreateProviderDefinitions(
                draft.Snapshot.Tools ??
                throw new AgentPreparationException(
                    AgentErrorCodes.ContextInputInvalid,
                    "The frozen tool snapshot is missing."),
                activatedDeferredTools)
            : [];

    private CapabilitySnapshotLease? AcquireCapabilitySnapshot()
    {
        try
        {
            return _capabilities?.AcquireSnapshot();
        }
        catch (CapabilityRuntimeException exception)
        {
            throw new AgentPreparationException(exception.Code, exception.Message);
        }
    }

    private IDisposable? AcquirePluginSnapshot(EffectiveToolSnapshot snapshot)
    {
        try
        {
            return _capabilities?.AcquirePluginSnapshot(snapshot);
        }
        catch (CapabilityRuntimeException exception)
        {
            throw new AgentPreparationException(exception.Code, exception.Message);
        }
    }

    private static bool FrozenInvocationMatches(
        AgentInvocationSnapshot frozen,
        AgentSession session,
        ProviderModelRegistration provider,
        AgentPromptMaterialization responsePrompt,
        AgentPromptMaterialization compactionPrompt,
        EffectiveToolSnapshot tools) =>
        frozen.CapabilityRevision >= 0 &&
        string.Equals(frozen.ProviderId, provider.ProviderId, StringComparison.Ordinal) &&
        string.Equals(frozen.ModelId, provider.ModelId, StringComparison.Ordinal) &&
        string.Equals(
            frozen.TokenizerProfileId,
            provider.TokenizerProfileId,
            StringComparison.Ordinal) &&
        string.Equals(
            frozen.TokenizerProfileVersion,
            provider.TokenizerProfileVersion,
            StringComparison.Ordinal) &&
        frozen.EffectiveAgentMode == session.Turn.EffectiveAgentMode &&
        frozen.ContextWindowTokens == provider.ContextWindowTokens &&
        frozen.MaxOutputTokens == provider.MaxOutputTokens &&
        string.Equals(
            frozen.ResponsePrompt.Version,
            responsePrompt.Snapshot.Version,
            StringComparison.Ordinal) &&
        string.Equals(
            frozen.ResponsePrompt.SystemMessageSha256,
            responsePrompt.Snapshot.SystemMessageSha256,
            StringComparison.Ordinal) &&
        string.Equals(
            frozen.CompactionPrompt.Version,
            compactionPrompt.Snapshot.Version,
            StringComparison.Ordinal) &&
        string.Equals(
            frozen.CompactionPrompt.SystemMessageSha256,
            compactionPrompt.Snapshot.SystemMessageSha256,
            StringComparison.Ordinal) &&
        frozen.WorkspaceInstructions == responsePrompt.WorkspaceInstructions &&
        string.Equals(
            tools.SnapshotSha256,
            frozen.Tools?.SnapshotSha256,
            StringComparison.Ordinal);

    private static JsonElement[] BuildInput(
        AgentSession session,
        bool allowIncompleteFinalToolGroup)
    {
        var input = new List<JsonElement>();
        var sourceEndSequence =
            session.CompactionCheckpoint?.SourceEndSequence ?? long.MinValue;
        if (session.CompactionCheckpoint is { } checkpoint)
        {
            input.Add(JsonSerializer.SerializeToElement(new
            {
                type = "message",
                role = "assistant",
                content = "Conversation summary of earlier turns:\n" + checkpoint.Summary,
            }));
        }

        var currentUserMessageCount = 0;
        var history = session.ModelHistory
            .Where(item =>
                item.Status == SessionItemStatus.Completed &&
                item.Sequence > sourceEndSequence)
            .OrderBy(item => item.Sequence)
            .ThenBy(item => item.ItemId)
            .ToArray();
        foreach (var item in history)
        {
            if (item.TurnId == session.Turn.TurnId &&
                item.Type == SessionItemType.UserMessage)
            {
                currentUserMessageCount++;
            }
        }

        input.AddRange(ProviderResponsesHistory.Build(
            history,
            session.Turn.TurnId,
            allowIncompleteFinalToolGroup));
        if (currentUserMessageCount != 1 ||
            !IsRole(input[^1], "user") &&
            !IsType(input[^1], "function_call_output") &&
            !(allowIncompleteFinalToolGroup &&
              IsType(input[^1], "function_call")))
        {
            throw new AgentPreparationException(
                AgentErrorCodes.ContextInputInvalid,
                "The current user input is missing or duplicated.");
        }

        return [.. input];
    }

    private static bool IsType(JsonElement item, string expected) =>
        item.TryGetProperty("type", out var type) &&
        string.Equals(type.GetString(), expected, StringComparison.Ordinal);

    private static bool IsRole(JsonElement item, string expected) =>
        IsType(item, "message") &&
        item.TryGetProperty("role", out var role) &&
        string.Equals(role.GetString(), expected, StringComparison.Ordinal);

    private static bool IsValidCheckpoint(
        CompactionCheckpointSnapshot checkpoint,
        AgentSession session,
        ProviderModelRegistration provider,
        AgentPromptMaterialization compactionPrompt)
    {
        try
        {
            return checkpoint.SchemaVersion is 1 or 2 &&
                   !string.IsNullOrWhiteSpace(checkpoint.Summary) &&
                   checkpoint.SourceStartSequence > 0 &&
                   checkpoint.SourceEndSequence >= checkpoint.SourceStartSequence &&
                   CompactionCheckpointIntegrity.SourceRangeIsClosed(
                       session.ModelHistory,
                       checkpoint.SourceStartSequence,
                       checkpoint.SourceEndSequence) &&
                   checkpoint.SummaryTokenCount ==
                   provider.Tokenizer.CountTokens(checkpoint.Summary) &&
                   string.Equals(
                       checkpoint.SummarySha256,
                       CompactionCheckpointIntegrity.Sha256(checkpoint.Summary),
                       StringComparison.Ordinal) &&
                   CompactionCheckpointIntegrity.IsLowerSha256(
                       checkpoint.SourceMessagesSha256) &&
                   string.Equals(
                       checkpoint.SourceMessagesSha256,
                       CompactionCheckpointIntegrity.SourceMessagesSha256(
                           session.ModelHistory,
                           checkpoint.SourceStartSequence,
                           checkpoint.SourceEndSequence,
                           checkpoint.SchemaVersion),
                       StringComparison.Ordinal) &&
                   CompactionCheckpointIntegrity.IsValidSummary(checkpoint.Summary) &&
                   (string.Equals(
                        checkpoint.SummaryPromptVersion,
                        compactionPrompt.Snapshot.Version,
                        StringComparison.Ordinal) ||
                    checkpoint.SchemaVersion == 1 &&
                    string.Equals(
                        checkpoint.SummaryPromptVersion,
                        AgentPrompts.LegacyCompactionVersion,
                        StringComparison.Ordinal)) &&
                   string.Equals(
                       checkpoint.TokenizerProfileId,
                       provider.TokenizerProfileId,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       checkpoint.TokenizerProfileVersion,
                       provider.TokenizerProfileVersion,
                       StringComparison.Ordinal);
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    internal static int CountPromptTokens(
        ModelTokenizer tokenizer,
        string instructions,
        IReadOnlyList<JsonElement> input,
        IReadOnlyList<DeepSeekResponsesTool>? tools = null)
    {
        var count = checked(
            8 +
            tokenizer.CountTokens("system") +
            tokenizer.CountTokens(instructions));
        foreach (var item in input)
        {
            count = checked(count + 5 + tokenizer.CountTokens(item.GetRawText()));
        }

        foreach (var tool in tools ?? [])
        {
            count = tool switch
            {
                DeepSeekFunctionTool function => checked(
                    count +
                    10 +
                    tokenizer.CountTokens(function.Name) +
                    tokenizer.CountTokens(function.Description) +
                    tokenizer.CountTokens(function.Parameters.GetRawText())),
                DeepSeekApplyPatchTool => checked(
                    count + 10 + tokenizer.CountTokens("apply_patch")),
                DeepSeekWebSearchTool => checked(
                    count + 10 + tokenizer.CountTokens("web_search")),
                _ => throw new AgentPreparationException(
                    AgentErrorCodes.ContextInputInvalid,
                    "The provider tool projection is invalid."),
            };
        }

        return count;
    }
}

internal sealed class AgentRuntimeExecutor : ISessionExecutor
{
    private static readonly TimeSpan InvocationTimeout = TimeSpan.FromMinutes(30);
    private readonly AgentFactory _factory;
    private readonly OpenCoWorkPaths _paths;
    private readonly Func<ProviderModelRegistration, string, DeepSeekResponseStream> _clients;
    private readonly IToolInvocationPipeline _toolPipeline;
    private readonly SecretRedactor _redactor;
    private readonly ProviderAuthService? _auth;
    private readonly TimeProvider _timeProvider;

    public AgentRuntimeExecutor(
        AgentFactory factory,
        OpenCoWorkPaths paths,
        HttpClient httpClient,
        IToolInvocationPipeline toolPipeline,
        SecretRedactor redactor,
        ProviderAuthService auth,
        TimeProvider? timeProvider = null)
        : this(
            factory,
            paths,
            (provider, secret) => new DeepSeekResponsesClient(
                httpClient,
                secret,
                redactor,
                provider.ResponseHeaderTimeout,
                provider.StreamIdleTimeout,
                timeProvider).StreamAsync,
            timeProvider,
            toolPipeline,
            redactor,
            auth)
    {
    }

    internal AgentRuntimeExecutor(
        AgentFactory factory,
        OpenCoWorkPaths paths,
        Func<ProviderModelRegistration, DeepSeekResponseStream> clients,
        TimeProvider? timeProvider = null,
        IToolInvocationPipeline? toolPipeline = null,
        SecretRedactor? redactor = null,
        ProviderAuthService? auth = null)
        : this(
            factory,
            paths,
            (provider, _) => clients(provider),
            timeProvider,
            toolPipeline,
            redactor,
            auth)
    {
        ArgumentNullException.ThrowIfNull(clients);
    }

    private AgentRuntimeExecutor(
        AgentFactory factory,
        OpenCoWorkPaths paths,
        Func<ProviderModelRegistration, string, DeepSeekResponseStream> clients,
        TimeProvider? timeProvider,
        IToolInvocationPipeline? toolPipeline,
        SecretRedactor? redactor,
        ProviderAuthService? auth)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _redactor = redactor ?? new SecretRedactor([]);
        _auth = auth;
        _toolPipeline = toolPipeline ??
                        new ToolInvocationPipeline(
                            new ToolRuntime(),
                            _redactor,
                            timeProvider: _timeProvider);
    }

    public async ValueTask ExecuteAsync(
        AgentSession context,
        ISessionExecutionSink sink,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(sink);
        ToolLoopCheckpoint? resumeCheckpoint;
        try
        {
            resumeCheckpoint = ReadToolCheckpoint(context.Checkpoint);
        }
        catch (AgentPreparationException exception)
        {
            await sink.EmitAsync(
                new FailTurnIntent(new SessionError(
                    exception.Code,
                    exception.Message,
                    IsRetryable: false)),
                cancellationToken);
            return;
        }

        var invocationBudget = resumeCheckpoint is null
            ? InvocationTimeout
            : TimeSpan.FromTicks(resumeCheckpoint.RemainingBudgetTicks);
        var activityStarted = _timeProvider.GetTimestamp();
        using var deadline = new CancellationTokenSource(
            invocationBudget,
            _timeProvider);
        using var invocationCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                deadline.Token);
        var invocationToken = invocationCancellation.Token;
        WorkspaceInstructionDocument? instructions;
        AgentInvocationDraft draft;
        IDisposable? pluginSnapshotLease = null;
        ProviderSecretLease providerSecret;
        Dictionary<string, KnownToolCall> knownToolCalls;
        PendingToolFrame? pendingToolFrame;
        try
        {
            instructions = WorkspaceInstructionDocument.Read(new OpenCoWorkPaths(
                WorkspacePathGuard.ResolveExecutionRoot(
                    context.Thread.ExecutionWorkspace,
                    _paths.WorkspaceRoot)));
            var execution = _factory.CreateForExecution(
                context,
                context.Invocation?.InvocationId ??
                Guid.CreateVersion7(_timeProvider.GetUtcNow()),
                instructions);
            draft = execution.Draft;
            pluginSnapshotLease = execution.PluginSnapshotLease;
            if (context.Invocation is null)
            {
                await sink.EmitAsync(
                    new RecordAgentInvocationSnapshotIntent(draft.Snapshot),
                    cancellationToken);
            }

            knownToolCalls = BuildKnownToolCalls(context, draft.Snapshot);
            pendingToolFrame = FindPendingToolFrame(context);
            ValidateToolCheckpoint(
                context,
                draft.Snapshot,
                pendingToolFrame,
                resumeCheckpoint);
            providerSecret = _auth?.Acquire(draft.Provider.AuthProfileId) ??
                             new ProviderSecretLease(draft.Provider.LegacyApiKey);
        }
        catch (AgentPreparationException exception)
        {
            pluginSnapshotLease?.Dispose();
            await sink.EmitAsync(
                new FailTurnIntent(new SessionError(
                    exception.Code,
                    exception.Message,
                    IsRetryable: false)),
                cancellationToken);
            return;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or
            UnauthorizedAccessException)
        {
            pluginSnapshotLease?.Dispose();
            await sink.EmitAsync(
                new FailTurnIntent(new SessionError(
                    AgentErrorCodes.ContextInputInvalid,
                    "Agent invocation preparation failed.",
                    IsRetryable: false)),
                cancellationToken);
            return;
        }
        catch
        {
            pluginSnapshotLease?.Dispose();
            throw;
        }

        using var frozenPluginSnapshot = pluginSnapshotLease;
        using var providerSecretLease = providerSecret;
        var activeContentItemId = Guid.Empty;
        var activeReasoningItemId = Guid.Empty;
        try
        {
            var session = context;
            var activatedDeferredTools =
                context.ActivatedDeferredTools.ToHashSet();
            var nextAttempt = context.ProviderUsage
                .Where(item =>
                    item.InvocationId == draft.Snapshot.InvocationId)
                .Select(item => item.AttemptNumber)
                .DefaultIfEmpty()
                .Max() + 1;
            var providerRound = context.ModelHistory
                .Where(item =>
                    item.TurnId == context.Turn.TurnId &&
                    item.Content is ToolCallItemContent)
                .Select(item => ((ToolCallItemContent)item.Content).ProviderRound)
                .DefaultIfEmpty()
                .Max();
            if (draft.Disposition == AgentInvocationDraftDisposition.CompactionRequired &&
                pendingToolFrame is null)
            {
                var maximumAttempt = providerRound == 0
                    ? 3
                    : checked(nextAttempt + 2);
                var compacted = await CompactAsync(
                    session,
                    draft,
                    instructions,
                    sink,
                    nextAttempt,
                    maximumAttempt,
                    targetPercent: 60,
                    providerSecretLease,
                    invocationToken,
                    cancellationToken);
                if (compacted is null)
                {
                    await FailCompactionAsync(sink, cancellationToken);
                    return;
                }

                session = compacted.Session;
                draft = compacted.Draft;
                nextAttempt = compacted.NextAttempt;
                if (providerRound == 0 && nextAttempt > 3)
                {
                    await FailCompactionAsync(sink, cancellationToken);
                    return;
                }
            }

            var input = draft.Input.ToList();
            var anyToolAttempted = false;
            if (pendingToolFrame is not null)
            {
                anyToolAttempted = await ResumeToolFrameAsync(
                    context,
                    draft,
                    activatedDeferredTools,
                    pendingToolFrame,
                    resumeCheckpoint,
                    knownToolCalls,
                    input,
                    nextAttempt,
                    activityStarted,
                    invocationBudget,
                    sink,
                    invocationToken);
                if (providerRound >= 64)
                {
                    await FailToolIterationLimitAsync(sink, cancellationToken);
                    return;
                }
            }

            while (true)
            {
                var toolFrameCompleted = false;
                for (var stepAttempt = 1; stepAttempt <= 3; stepAttempt++)
                {
                    var providerTools = _factory.CreateProviderDefinitions(
                        draft,
                        activatedDeferredTools);
                    var attempt = nextAttempt++;
                    var stepVisible = false;
                    var content = new StringBuilder();
                    var reasoning = new StringBuilder();
                    var toolCalls =
                        new List<DeepSeekFunctionCallCompletedEvent>();
                    activeContentItemId = Guid.Empty;
                    activeReasoningItemId = Guid.Empty;
                    DeepSeekTerminalEvent? terminal = null;
                    var inputTokenCount = AgentFactory.CountPromptTokens(
                        draft.Provider.Tokenizer,
                        draft.ResponsePrompt.SystemMessage,
                        input,
                        providerTools);
                    if (inputTokenCount > draft.UsableInputBudgetTokens)
                    {
                        await sink.EmitAsync(
                            new FailTurnIntent(new SessionError(
                                AgentErrorCodes.ContextInputTooLarge,
                                "The current input exceeds the model context budget.",
                                IsRetryable: false)),
                            cancellationToken);
                        return;
                    }

                    try
                    {
                        var request = new DeepSeekResponsesRequest(
                            draft.Provider.ModelId,
                            draft.ResponsePrompt.SystemMessage,
                            input,
                            draft.Provider.MaxOutputTokens,
                            draft.Snapshot.ReasoningEffort,
                            providerTools,
                            draft.Snapshot.InvocationId,
                            attempt,
                            ProviderInvocationPurpose.Response);
                        await foreach (var item in Stream(
                                           draft.Provider,
                                           providerSecretLease)(request, invocationToken)
                                           .WithCancellation(invocationToken))
                        {
                            switch (item)
                            {
                                case DeepSeekTextDeltaEvent
                                    {
                                        Kind: DeepSeekTextKind.Output,
                                        Delta.Length: > 0,
                                    } delta:
                                    if (activeContentItemId == Guid.Empty)
                                    {
                                        activeContentItemId =
                                            Guid.CreateVersion7(
                                                _timeProvider.GetUtcNow());
                                        await sink.EmitAsync(
                                            new StartItemIntent(
                                                activeContentItemId,
                                                SessionItemType.AgentMessage,
                                                new TextItemContent(string.Empty)),
                                            cancellationToken);
                                    }

                                    await sink.EmitAsync(
                                        new AppendItemDeltaIntent(
                                            activeContentItemId,
                                            delta.Delta,
                                            Flush: !stepVisible),
                                        cancellationToken);
                                    content.Append(delta.Delta);
                                    stepVisible = true;
                                    break;
                                case DeepSeekTextDeltaEvent
                                    {
                                        Kind: DeepSeekTextKind.Reasoning,
                                        Delta.Length: > 0,
                                    } delta:
                                    if (activeReasoningItemId == Guid.Empty)
                                    {
                                        activeReasoningItemId =
                                            Guid.CreateVersion7(
                                                _timeProvider.GetUtcNow());
                                        await sink.EmitAsync(
                                            new StartItemIntent(
                                                activeReasoningItemId,
                                                SessionItemType.Reasoning,
                                                new TextItemContent(string.Empty)),
                                            cancellationToken);
                                    }

                                    await sink.EmitAsync(
                                        new AppendItemDeltaIntent(
                                            activeReasoningItemId,
                                            delta.Delta,
                                            Flush: !stepVisible),
                                        cancellationToken);
                                    reasoning.Append(delta.Delta);
                                    stepVisible = true;
                                    break;
                                case DeepSeekFunctionCallCompletedEvent toolCall:
                                    toolCalls.Add(toolCall);
                                    break;
                                case DeepSeekTextCompletedEvent:
                                    break;
                                case DeepSeekTerminalEvent completed:
                                    terminal = completed;
                                    break;
                                case DeepSeekCustomToolCallCompletedEvent or
                                    DeepSeekWebSearchEvent:
                                    throw new ProviderException(
                                        AgentErrorCodes.ProviderUnsupportedToolCall,
                                        "Provider returned a tool call that is not enabled yet.");
                            }
                        }

                        if (terminal is null)
                        {
                            throw new ProviderException(
                                AgentErrorCodes.ProviderInvalidStream,
                                "Provider returned no terminal response.");
                        }

                        if (terminal.Usage is { } usage)
                        {
                            await RecordUsageAsync(
                                sink,
                                draft.Snapshot.InvocationId,
                                attempt,
                                ProviderInvocationPurpose.Response,
                                usage,
                                cancellationToken);
                        }

                        else
                        {
                            var completionTokens =
                                draft.Provider.Tokenizer.CountTokens(
                                    content.ToString() + reasoning);
                            await RecordEstimatedUsageAsync(
                                sink,
                                draft.Snapshot.InvocationId,
                                attempt,
                                ProviderInvocationPurpose.Response,
                                inputTokenCount,
                                completionTokens,
                                cancellationToken);
                        }

                        if (terminal.Status == DeepSeekTerminalStatus.Failed)
                        {
                            await FailAsync(
                                sink,
                                activeContentItemId,
                                activeReasoningItemId,
                                new SessionError(
                                    terminal.ErrorCode ?? AgentErrorCodes.ProviderResponseFailed,
                                    terminal.ErrorDetail ?? "Provider response failed.",
                                    IsRetryable: false),
                                cancellationToken);
                            return;
                        }

                        if (toolCalls.Count != 0 &&
                            terminal.Status == DeepSeekTerminalStatus.Completed)
                        {
                            if (activeReasoningItemId != Guid.Empty)
                            {
                                await sink.EmitAsync(
                                    new CompleteItemIntent(activeReasoningItemId),
                                    cancellationToken);
                            }

                            if (activeContentItemId != Guid.Empty)
                            {
                                await sink.EmitAsync(
                                    new CompleteItemIntent(activeContentItemId),
                                    cancellationToken);
                            }

                            providerRound++;
                            var frame = PreflightToolFrame(
                                providerRound,
                                activeContentItemId == Guid.Empty
                                    ? null
                                    : activeContentItemId,
                                toolCalls);
                            var toolCallItemId =
                                Guid.CreateVersion7(_timeProvider.GetUtcNow());
                            await sink.EmitAsync(
                                new RecordToolCallIntent(toolCallItemId, frame),
                                cancellationToken);
                            if (reasoning.Length != 0)
                            {
                                input.Add(JsonSerializer.SerializeToElement(new
                                {
                                    type = "reasoning",
                                    content = reasoning.ToString(),
                                }));
                            }

                            if (content.Length != 0)
                            {
                                input.Add(JsonSerializer.SerializeToElement(new
                                {
                                    type = "message",
                                    role = "assistant",
                                    content = content.ToString(),
                                }));
                            }

                            foreach (var call in frame.Calls)
                            {
                                input.Add(JsonSerializer.SerializeToElement(new
                                {
                                    type = "function_call",
                                    call_id = call.ProviderToolCallId,
                                    name = call.ProviderToolName,
                                    arguments = Encoding.UTF8.GetString(
                                        ThreadJournal.Canonicalize(call.Arguments)),
                                }));
                            }
                            for (var callIndex = 0;
                                 callIndex < frame.Calls.Count;
                                 callIndex++)
                            {
                                var call = frame.Calls[callIndex];
                                knownToolCalls.TryGetValue(
                                    call.ProviderToolCallId,
                                    out var known);
                                var sameCall = known is not null &&
                                               string.Equals(
                                                   known.ProviderToolName,
                                                   call.ProviderToolName,
                                                   StringComparison.Ordinal) &&
                                               string.Equals(
                                                   known.ArgumentsSha256,
                                                   call.ArgumentsSha256,
                                                   StringComparison.Ordinal);
                                var toolInvocationId =
                                    Guid.CreateVersion7(_timeProvider.GetUtcNow());
                                var remaining = RemainingActivityBudget(
                                    activityStarted,
                                    invocationBudget);
                                var checkpoint = CreateToolCheckpoint(
                                    draft.Snapshot,
                                    providerRound,
                                    nextAttempt,
                                    toolCallItemId,
                                    callIndex,
                                    toolInvocationId,
                                    call.ArgumentsSha256,
                                    remaining);
                                var result = await _toolPipeline.InvokeAsync(
                                    new ToolInvocationContext(
                                        context.Thread.ThreadId,
                                        context.Turn.TurnId,
                                        toolInvocationId,
                                        toolCallItemId,
                                        callIndex,
                                        call.ProviderToolCallId,
                                        call.ProviderToolName,
                                        call.Arguments,
                                        call.ArgumentsSha256,
                                        call.SensitiveInputDetected,
                                        draft.Snapshot.Tools!,
                                        checkpoint,
                                        ApprovalTimeoutAt: null,
                                        ApprovalGranted: null,
                                        PriorAttemptCount: 0,
                                        RemainingExecutionBudget: remaining,
                                        ReplayResult: sameCall
                                            ? known?.Result
                                            : null,
                                        ProviderCallIdConflict:
                                        known is not null && !sameCall,
                                        Skills: draft.Snapshot.Skills,
                                        ActivatedDeferredTools:
                                        activatedDeferredTools.ToArray(),
                                        ExecutionWorkspace:
                                        context.Thread.ExecutionWorkspace,
                                        CoWorkProvenance:
                                        context.Thread.CoWorkProvenance),
                                    sink,
                                    invocationToken);
                                ActivateDeferredTools(
                                    draft.Snapshot.Tools!,
                                    call.ProviderToolName,
                                    result,
                                    activatedDeferredTools);
                                anyToolAttempted |= result.AttemptCount > 0;
                                if (known is null)
                                {
                                    knownToolCalls.Add(
                                        call.ProviderToolCallId,
                                        new KnownToolCall(
                                            call.ProviderToolName,
                                            call.ArgumentsSha256,
                                            result));
                                }

                                input.Add(JsonSerializer.SerializeToElement(new
                                {
                                    type = "function_call_output",
                                    call_id = call.ProviderToolCallId,
                                    output = ProviderResponsesHistory.ToolResultEnvelope(result),
                                }));
                            }

                            if (providerRound >= 64)
                            {
                                await FailToolIterationLimitAsync(
                                    sink,
                                    cancellationToken);
                                return;
                            }

                            toolFrameCompleted = true;
                            break;
                        }

                        if (toolCalls.Count != 0)
                        {
                            throw new ProviderException(
                                AgentErrorCodes.ProviderInvalidStream,
                                "Provider returned tool calls with an invalid terminal status.");
                        }

                        if (content.Length == 0)
                        {
                            await FailAsync(
                                sink,
                                activeContentItemId,
                                activeReasoningItemId,
                                new SessionError(
                                    AgentErrorCodes.ProviderEmptyResponse,
                                    "Provider returned no response content.",
                                    IsRetryable: false),
                                cancellationToken);
                            return;
                        }

                        if (activeReasoningItemId != Guid.Empty)
                        {
                            await sink.EmitAsync(
                                new CompleteItemIntent(activeReasoningItemId),
                                cancellationToken);
                        }

                        if (activeContentItemId != Guid.Empty)
                        {
                            await sink.EmitAsync(
                                new CompleteItemIntent(activeContentItemId),
                                cancellationToken);
                        }

                        if (terminal.Status == DeepSeekTerminalStatus.Incomplete)
                        {
                            var noticeId =
                                Guid.CreateVersion7(_timeProvider.GetUtcNow());
                            await sink.EmitAsync(
                                new StartItemIntent(
                                    noticeId,
                                    SessionItemType.SystemNotice,
                                    new SystemNoticeContent("response.truncated")),
                                cancellationToken);
                            await sink.EmitAsync(
                                new CompleteItemIntent(noticeId),
                                cancellationToken);
                        }

                        await sink.EmitAsync(
                            new CompleteTurnIntent(),
                            cancellationToken);
                        return;
                    }
                    catch (ProviderException exception)
                    {
                        if (!stepVisible &&
                            !anyToolAttempted &&
                            exception.IsTransient &&
                            stepAttempt < 3)
                        {
                            await DelayRetryAsync(
                                exception,
                                attempt,
                                invocationToken);
                            continue;
                        }

                        await FailAsync(
                            sink,
                            activeContentItemId,
                            activeReasoningItemId,
                            new SessionError(
                                exception.Code,
                                exception.Message,
                                exception.IsTransient),
                            cancellationToken);
                        return;
                    }
                }

                if (toolFrameCompleted)
                {
                    continue;
                }
            }
        }
        catch (ToolInvocationSuspendedException)
        {
            return;
        }
        catch (OperationCanceledException) when (
            deadline.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            await FailAsync(
                sink,
                activeContentItemId,
                activeReasoningItemId,
                new SessionError(
                    AgentErrorCodes.ProviderTimeout,
                    "Provider invocation timed out.",
                    IsRetryable: false),
                cancellationToken);
        }
    }

    private static ToolLoopCheckpoint? ReadToolCheckpoint(
        SessionExecutionCheckpoint? checkpoint)
    {
        if (checkpoint is null)
        {
            return null;
        }

        try
        {
            var executorKind = typeof(AgentRuntimeExecutor).FullName!;
            if (checkpoint.SchemaVersion != 1 ||
                !SessionExecutionCheckpointCodec.IsValid(
                    checkpoint,
                    executorKind))
            {
                throw InvalidToolCheckpoint();
            }

            var value = JsonSerializer.Deserialize<ToolLoopCheckpoint>(
                checkpoint.Payload);
            if (value is null ||
                value.AgentInvocationId == Guid.Empty ||
                value.ProviderRound is < 1 or > 64 ||
                value.NextAttemptNumber <= 0 ||
                value.ToolCallItemId == Guid.Empty ||
                value.CallIndex < 0 ||
                value.ToolInvocationId == Guid.Empty ||
                !CompactionCheckpointIntegrity.IsLowerSha256(
                    value.ToolSnapshotSha256) ||
                !CompactionCheckpointIntegrity.IsLowerSha256(
                    value.ArgumentsSha256) ||
                !string.Equals(
                    value.NextPipelineStage,
                    "approval",
                    StringComparison.Ordinal) ||
                value.RemainingBudgetTicks < 0 ||
                value.RemainingBudgetTicks > InvocationTimeout.Ticks)
            {
                throw InvalidToolCheckpoint();
            }

            return value;
        }
        catch (AgentPreparationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException or
                InvalidOperationException or OverflowException)
        {
            throw InvalidToolCheckpoint();
        }
    }

    private static void ValidateToolCheckpoint(
        AgentSession session,
        AgentInvocationSnapshot invocation,
        PendingToolFrame? pending,
        ToolLoopCheckpoint? checkpoint)
    {
        if (checkpoint is null)
        {
            return;
        }

        var expectedAttempt = session.ProviderUsage
            .Where(item => item.InvocationId == invocation.InvocationId)
            .Select(item => item.AttemptNumber)
            .DefaultIfEmpty()
            .Max() + 1;
        var state = pending is not null &&
                    checkpoint.CallIndex < pending.States.Count
            ? pending.States[checkpoint.CallIndex]
            : null;
        if (pending is null ||
            checkpoint.AgentInvocationId != invocation.InvocationId ||
            !string.Equals(
                checkpoint.ToolSnapshotSha256,
                invocation.Tools?.SnapshotSha256,
                StringComparison.Ordinal) ||
            checkpoint.ProviderRound != pending.Content.ProviderRound ||
            checkpoint.NextAttemptNumber != expectedAttempt ||
            checkpoint.ToolCallItemId != pending.Item.ItemId ||
            state is null ||
            state.Invocation.ToolInvocationId != checkpoint.ToolInvocationId ||
            state.Invocation.Status != ToolInvocationStatus.WaitingApproval ||
            !string.Equals(
                checkpoint.ArgumentsSha256,
                state.Invocation.ArgumentsSha256,
                StringComparison.Ordinal))
        {
            throw InvalidToolCheckpoint();
        }

        _ = ApprovalDecision(session, state.Invocation, required: true);
    }

    private static PendingToolFrame? FindPendingToolFrame(AgentSession session)
    {
        var frames = session.ModelHistory
            .Where(item =>
                item.TurnId == session.Turn.TurnId &&
                item.Type == SessionItemType.ToolCall &&
                item.Status == SessionItemStatus.Completed)
            .OrderBy(item => item.Sequence)
            .ThenBy(item => item.ItemId)
            .ToArray();
        var duplicateState = session.ToolInvocations
            .GroupBy(state => (state.ToolCallItemId, state.CallIndex))
            .Any(group => group.Count() != 1);
        if (duplicateState)
        {
            throw InvalidKnownToolCall();
        }

        PendingToolFrame? pending = null;
        foreach (var item in frames)
        {
            if (item.Content is not ToolCallItemContent content ||
                content.Calls.Count == 0)
            {
                throw InvalidKnownToolCall();
            }

            var states = Enumerable.Range(0, content.Calls.Count)
                .Select(callIndex => session.ToolInvocations.SingleOrDefault(
                    state =>
                        state.ToolCallItemId == item.ItemId &&
                        state.CallIndex == callIndex))
                .ToArray();
            if (states.Any(state =>
                    state is null ||
                    state.Invocation.CompletedAt is null))
            {
                if (pending is not null || item != frames[^1])
                {
                    throw InvalidKnownToolCall();
                }

                pending = new PendingToolFrame(item, content, states);
            }
        }

        return pending;
    }

    private async ValueTask<bool> ResumeToolFrameAsync(
        AgentSession session,
        AgentInvocationDraft draft,
        HashSet<ToolDefinitionId> activatedDeferredTools,
        PendingToolFrame frame,
        ToolLoopCheckpoint? resumeCheckpoint,
        Dictionary<string, KnownToolCall> knownToolCalls,
        List<JsonElement> input,
        int nextAttempt,
        long activityStarted,
        TimeSpan activityBudget,
        ISessionExecutionSink sink,
        CancellationToken cancellationToken)
    {
        var anyToolAttempted = false;
        for (var callIndex = 0;
             callIndex < frame.Content.Calls.Count;
             callIndex++)
        {
            var state = frame.States[callIndex];
            if (state?.Invocation.CompletedAt is not null)
            {
                continue;
            }

            var call = frame.Content.Calls[callIndex];
            knownToolCalls.TryGetValue(
                call.ProviderToolCallId,
                out var known);
            var sameCall = known is not null &&
                           string.Equals(
                               known.ProviderToolName,
                               call.ProviderToolName,
                               StringComparison.Ordinal) &&
                           string.Equals(
                               known.ArgumentsSha256,
                               call.ArgumentsSha256,
                               StringComparison.Ordinal);
            var toolInvocationId =
                state?.Invocation.ToolInvocationId ??
                Guid.CreateVersion7(_timeProvider.GetUtcNow());
            var remaining = RemainingActivityBudget(
                activityStarted,
                activityBudget);
            var checkpoint = CreateToolCheckpoint(
                draft.Snapshot,
                frame.Content.ProviderRound,
                nextAttempt,
                frame.Item.ItemId,
                callIndex,
                toolInvocationId,
                call.ArgumentsSha256,
                remaining);
            var priorAttemptCount =
                state?.Invocation.AttemptCount ?? 0;
            var result = await _toolPipeline.InvokeAsync(
                new ToolInvocationContext(
                    session.Thread.ThreadId,
                    session.Turn.TurnId,
                    toolInvocationId,
                    frame.Item.ItemId,
                    callIndex,
                    call.ProviderToolCallId,
                    call.ProviderToolName,
                    call.Arguments,
                    call.ArgumentsSha256,
                    call.SensitiveInputDetected,
                    draft.Snapshot.Tools!,
                    checkpoint,
                    ApprovalTimeoutAt: null,
                    ApprovalGranted: state is null
                        ? null
                        : ApprovalDecision(
                            session,
                            state.Invocation,
                            required: resumeCheckpoint is not null &&
                                      resumeCheckpoint.CallIndex == callIndex),
                    PriorAttemptCount: priorAttemptCount,
                    RemainingExecutionBudget: remaining,
                    ReplayResult: sameCall ? known?.Result : null,
                    ProviderCallIdConflict: known is not null && !sameCall,
                    Skills: draft.Snapshot.Skills,
                    ActivatedDeferredTools:
                    activatedDeferredTools.ToArray(),
                    ExecutionWorkspace:
                    session.Thread.ExecutionWorkspace,
                    CoWorkProvenance:
                    session.Thread.CoWorkProvenance),
                sink,
                cancellationToken);
            ActivateDeferredTools(
                draft.Snapshot.Tools!,
                call.ProviderToolName,
                result,
                activatedDeferredTools);
            anyToolAttempted |= result.AttemptCount > 0;
            if (known is null)
            {
                knownToolCalls.Add(
                    call.ProviderToolCallId,
                    new KnownToolCall(
                        call.ProviderToolName,
                        call.ArgumentsSha256,
                        result));
            }
            else if (known.State?.Invocation.ToolInvocationId ==
                     toolInvocationId)
            {
                knownToolCalls[call.ProviderToolCallId] =
                    known with { Result = result };
            }

            input.Add(JsonSerializer.SerializeToElement(new
            {
                type = "function_call_output",
                call_id = call.ProviderToolCallId,
                output = ProviderResponsesHistory.ToolResultEnvelope(result),
            }));
        }

        return anyToolAttempted;
    }

    private static void ActivateDeferredTools(
        EffectiveToolSnapshot snapshot,
        string providerToolName,
        ToolResultSnapshot result,
        HashSet<ToolDefinitionId> activated)
    {
        if (result.Status != ToolInvocationStatus.Completed ||
            result.Output is not { ValueKind: JsonValueKind.Object } output ||
            !snapshot.ProviderToCanonicalNames.TryGetValue(
                providerToolName,
                out var canonicalName) ||
            !string.Equals(canonicalName, "tool.search", StringComparison.Ordinal) ||
            !output.TryGetProperty("activated", out var values) ||
            values.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var value in values.EnumerateArray())
        {
            if (!value.TryGetProperty("sourceKind", out var sourceKind) ||
                !value.TryGetProperty("sourceId", out var sourceId) ||
                !value.TryGetProperty("sourceToolId", out var sourceToolId) ||
                !Enum.TryParse<ToolSourceKind>(
                    sourceKind.GetString(),
                    ignoreCase: true,
                    out var kind))
            {
                throw new InvalidDataException(
                    "Deferred Tool activation result is invalid.");
            }

            var id = new ToolDefinitionId(
                kind,
                sourceId.GetString() ?? string.Empty,
                sourceToolId.GetString() ?? string.Empty);
            if (!snapshot.Registrations.Any(registration =>
                    registration.Exposure == ToolExposure.Deferred &&
                    registration.Definition.Id == id) ||
                !activated.Add(id) ||
                activated.Count > 32)
            {
                throw new InvalidDataException(
                    "Deferred Tool activation result does not match its snapshot.");
            }
        }
    }

    private static bool? ApprovalDecision(
        AgentSession session,
        ToolInvocationSnapshot invocation,
        bool required)
    {
        var request = session.ModelHistory
            .Where(item =>
                item.TurnId == session.Turn.TurnId &&
                item.Status == SessionItemStatus.Completed &&
                item.Content is ToolApprovalRequestContent content &&
                content.ToolInvocationId == invocation.ToolInvocationId)
            .OrderByDescending(item => item.Sequence)
            .ThenByDescending(item => item.ItemId)
            .FirstOrDefault();
        if (request is null)
        {
            if (required)
            {
                throw InvalidToolCheckpoint();
            }

            return null;
        }

        var content = (ToolApprovalRequestContent)request.Content;
        if (content.ToolDefinitionId != invocation.ToolDefinitionId ||
            !string.Equals(
                content.SnapshotSha256,
                invocation.SnapshotSha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                content.ArgumentsSha256,
                invocation.ArgumentsSha256,
                StringComparison.Ordinal))
        {
            throw InvalidToolCheckpoint();
        }

        var response = session.ModelHistory
            .Where(item =>
                item.TurnId == session.Turn.TurnId &&
                item.Sequence > request.Sequence &&
                item.Status == SessionItemStatus.Completed &&
                item.Content is ApprovalResponseContent)
            .OrderByDescending(item => item.Sequence)
            .ThenByDescending(item => item.ItemId)
            .FirstOrDefault();
        if (response is null && required)
        {
            throw InvalidToolCheckpoint();
        }

        return response?.Content is ApprovalResponseContent decision
            ? decision.Approved
            : null;
    }

    private static AgentPreparationException InvalidToolCheckpoint() =>
        new(
            AgentErrorCodes.ContextInputInvalid,
            "Tool continuation checkpoint is invalid.");

    private static ValueTask FailToolIterationLimitAsync(
        ISessionExecutionSink sink,
        CancellationToken cancellationToken) =>
        sink.EmitAsync(
            new FailTurnIntent(new SessionError(
                ToolErrorCodes.IterationLimitExceeded,
                "Tool call iteration limit exceeded.",
                IsRetryable: false)),
            cancellationToken);

    private ToolCallItemContent PreflightToolFrame(
        int providerRound,
        Guid? agentMessageItemId,
        IReadOnlyList<DeepSeekFunctionCallCompletedEvent> calls)
    {
        if (calls.Count == 0 ||
            calls.Select(call => call.CallId)
                .Distinct(StringComparer.Ordinal)
                .Count() != calls.Count)
        {
            throw InvalidToolFrame();
        }

        var entries = new ToolCallItemEntry[calls.Count];
        for (var index = 0; index < calls.Count; index++)
        {
            var call = calls[index];
            if (string.IsNullOrWhiteSpace(call.CallId) ||
                string.IsNullOrWhiteSpace(call.Name))
            {
                throw InvalidToolFrame();
            }

            try
            {
                using var document = JsonDocument.Parse(
                    call.Arguments,
                    new JsonDocumentOptions
                    {
                        MaxDepth = ToolRuntimeLimits.MaximumJsonDepth,
                    });
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw InvalidToolFrame();
                }

                var arguments = _redactor.RedactJson(
                    document.RootElement,
                    out var sensitiveInputDetected);
                var canonical = ThreadJournal.Canonicalize(arguments);
                if (canonical.Length > ToolRuntimeLimits.MaximumArgumentsBytes)
                {
                    throw InvalidToolFrame();
                }

                entries[index] = new ToolCallItemEntry(
                    call.CallId,
                    call.Name,
                    arguments,
                    Convert.ToHexString(SHA256.HashData(canonical))
                        .ToLowerInvariant(),
                    sensitiveInputDetected);
            }
            catch (ProviderException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is JsonException or InvalidOperationException or
                    OverflowException)
            {
                throw InvalidToolFrame();
            }
        }

        return new ToolCallItemContent(
            providerRound,
            agentMessageItemId,
            entries);
    }

    private static Dictionary<string, KnownToolCall> BuildKnownToolCalls(
        AgentSession session,
        AgentInvocationSnapshot invocation)
    {
        var items = session.ModelHistory.ToDictionary(item => item.ItemId);
        var known = new Dictionary<string, KnownToolCall>(StringComparer.Ordinal);
        foreach (var state in session.ToolInvocations)
        {
            var snapshot = state.Invocation;
            if (snapshot.TurnId != session.Turn.TurnId ||
                !string.Equals(
                    snapshot.SnapshotSha256,
                    invocation.Tools?.SnapshotSha256,
                    StringComparison.Ordinal) ||
                !items.TryGetValue(state.ToolCallItemId, out var item) ||
                item.Content is not ToolCallItemContent toolCall ||
                state.CallIndex < 0 ||
                state.CallIndex >= toolCall.Calls.Count)
            {
                throw InvalidKnownToolCall();
            }

            var call = toolCall.Calls[state.CallIndex];
            if (!string.Equals(
                    snapshot.ProviderToolCallId,
                    call.ProviderToolCallId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    snapshot.ProviderToolName,
                    call.ProviderToolName,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    snapshot.ArgumentsSha256,
                    call.ArgumentsSha256,
                    StringComparison.Ordinal))
            {
                throw InvalidKnownToolCall();
            }

            ToolResultSnapshot? result = null;
            if (snapshot.CompletedAt is not null)
            {
                if (snapshot.ResultItemId is not { } resultItemId ||
                    !items.TryGetValue(resultItemId, out var resultItem) ||
                    resultItem.Content is not ToolResultItemContent resultContent ||
                    resultContent.Result.ToolInvocationId != snapshot.ToolInvocationId)
                {
                    throw InvalidKnownToolCall();
                }

                result = resultContent.Result;
            }

            known.TryAdd(
                call.ProviderToolCallId,
                new KnownToolCall(
                    call.ProviderToolName,
                    call.ArgumentsSha256,
                    result,
                    state));
        }

        return known;
    }

    private static AgentPreparationException InvalidKnownToolCall() =>
        new(
            AgentErrorCodes.ContextInputInvalid,
            "Tool Invocation history is invalid.");

    private TimeSpan RemainingActivityBudget(
        long activityStarted,
        TimeSpan budget)
    {
        var remaining = budget - _timeProvider.GetElapsedTime(activityStarted);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static SessionExecutionCheckpoint CreateToolCheckpoint(
        AgentInvocationSnapshot invocation,
        int providerRound,
        int nextAttemptNumber,
        Guid toolCallItemId,
        int callIndex,
        Guid toolInvocationId,
        string argumentsSha256,
        TimeSpan remainingBudget)
    {
        var payload = JsonSerializer.Serialize(new ToolLoopCheckpoint(
            invocation.InvocationId,
            invocation.Tools?.SnapshotSha256 ?? string.Empty,
            providerRound,
            nextAttemptNumber,
            toolCallItemId,
            callIndex,
            toolInvocationId,
            argumentsSha256,
            "approval",
            Math.Max(0, remainingBudget.Ticks)));
        return SessionExecutionCheckpointCodec.Create(
            typeof(AgentRuntimeExecutor).FullName!,
            schemaVersion: 1,
            payload);
    }

    private static ProviderException InvalidToolFrame() =>
        new(
            AgentErrorCodes.ProviderInvalidStream,
            "Provider returned an invalid tool call frame.");

    private async ValueTask<CompactionResult?> CompactAsync(
        AgentSession session,
        AgentInvocationDraft draft,
        WorkspaceInstructionDocument? instructions,
        ISessionExecutionSink sink,
        int nextAttempt,
        int maximumAttempt,
        int targetPercent,
        ProviderSecretLease providerSecret,
        CancellationToken invocationToken,
        CancellationToken cancellationToken)
    {
        var selection = SelectCompaction(session, draft, targetPercent);
        if (selection is null)
        {
            return null;
        }

        var input = new[]
        {
            JsonSerializer.SerializeToElement(new
            {
                type = "message",
                role = "user",
                content = CompactionInput(session.CompactionCheckpoint, selection.Items),
            }),
        };
        var promptTokens = AgentFactory.CountPromptTokens(
            draft.Provider.Tokenizer,
            draft.CompactionPrompt.SystemMessage,
            input);
        var maxSummaryTokens = Math.Min(
            8192,
            checked((draft.UsableInputBudgetTokens + 9) / 10));
        if (promptTokens >
            draft.Provider.ContextWindowTokens - maxSummaryTokens)
        {
            return null;
        }

        while (nextAttempt <= maximumAttempt)
        {
            var attempt = nextAttempt++;
            var summary = new StringBuilder();
            DeepSeekTerminalEvent? terminal = null;
            try
            {
                var request = new DeepSeekResponsesRequest(
                    draft.Provider.ModelId,
                    draft.CompactionPrompt.SystemMessage,
                    input,
                    maxSummaryTokens,
                    draft.Snapshot.ReasoningEffort,
                    Tools: [],
                    draft.Snapshot.InvocationId,
                    attempt,
                    ProviderInvocationPurpose.Compaction);
                await foreach (var item in Stream(draft.Provider, providerSecret)(
                                   request,
                                   invocationToken)
                                   .WithCancellation(invocationToken))
                {
                    switch (item)
                    {
                        case DeepSeekTextDeltaEvent
                            {
                                Kind: DeepSeekTextKind.Output,
                            } delta:
                            summary.Append(delta.Delta);
                            break;
                        case DeepSeekTextCompletedEvent:
                            break;
                        case DeepSeekTerminalEvent completed:
                            terminal = completed;
                            break;
                        default:
                            throw new ProviderException(
                                AgentErrorCodes.ProviderInvalidStream,
                                "Provider returned invalid compaction output.");
                    }
                }

                var normalizedSummary = NormalizeLf(summary.ToString());
                var summaryTokens =
                    draft.Provider.Tokenizer.CountTokens(normalizedSummary);
                if (terminal?.Usage is { } usage)
                {
                    await RecordUsageAsync(
                        sink,
                        draft.Snapshot.InvocationId,
                        attempt,
                        ProviderInvocationPurpose.Compaction,
                        usage,
                        cancellationToken);
                }
                else
                {
                    await RecordEstimatedUsageAsync(
                        sink,
                        draft.Snapshot.InvocationId,
                        attempt,
                        ProviderInvocationPurpose.Compaction,
                        promptTokens,
                        summaryTokens,
                        cancellationToken);
                }

                if (terminal?.Status != DeepSeekTerminalStatus.Completed ||
                    summaryTokens > maxSummaryTokens ||
                    !CompactionCheckpointIntegrity.IsValidSummary(
                        normalizedSummary))
                {
                    return null;
                }

                var checkpoint = new CompactionCheckpointSnapshot(
                    SchemaVersion: 2,
                    normalizedSummary,
                    CompactionCheckpointIntegrity.Sha256(normalizedSummary),
                    session.CompactionCheckpoint?.SourceStartSequence ??
                    selection.Items[0].Sequence,
                    selection.SourceEndSequence,
                    CompactionCheckpointIntegrity.SourceMessagesSha256(
                        session.ModelHistory,
                        session.CompactionCheckpoint?.SourceStartSequence ??
                        selection.Items[0].Sequence,
                        selection.SourceEndSequence,
                        schemaVersion: 2),
                    draft.CompactionPrompt.Snapshot.Version,
                    draft.Provider.TokenizerProfileId,
                    draft.Provider.TokenizerProfileVersion,
                    summaryTokens);
                var compactedSession = new AgentSession(
                    session.Thread,
                    session.Turn,
                    session.ModelHistory,
                    session.Checkpoint,
                    checkpoint,
                    session.Invocation,
                    session.ToolInvocations,
                    session.ProviderUsage,
                    session.ActivatedDeferredTools);
                var compactedDraft = _factory.Create(
                    compactedSession,
                    draft.Snapshot.InvocationId,
                    instructions);
                if (compactedDraft.InputTokenCount >
                    compactedDraft.UsableInputBudgetTokens * (long)targetPercent / 100)
                {
                    return null;
                }

                await sink.EmitAsync(
                    new RecordCompactionCheckpointIntent(checkpoint),
                    cancellationToken);
                return new CompactionResult(
                    compactedSession,
                    compactedDraft,
                    nextAttempt);
            }
            catch (ProviderException exception)
            {
                if (!exception.IsTransient || nextAttempt > maximumAttempt)
                {
                    return null;
                }

                await DelayRetryAsync(exception, attempt, invocationToken);
            }
        }

        return null;
    }

    private DeepSeekResponseStream Stream(
        ProviderModelRegistration provider,
        ProviderSecretLease secret) =>
        _clients(provider, secret.Secret!);

    private static CompactionSelection? SelectCompaction(
        AgentSession session,
        AgentInvocationDraft draft,
        int targetPercent)
    {
        var sourceEnd =
            session.CompactionCheckpoint?.SourceEndSequence ?? long.MinValue;
        var turns = session.ModelHistory
            .Where(item =>
                item.Status == SessionItemStatus.Completed &&
                item.TurnId != session.Turn.TurnId &&
                item.Sequence > sourceEnd &&
                item.Type is SessionItemType.UserMessage or
                    SessionItemType.AgentMessage or
                    SessionItemType.ToolCall or
                    SessionItemType.ToolResult)
            .OrderBy(item => item.Sequence)
            .ThenBy(item => item.ItemId)
            .GroupBy(item => item.TurnId)
            .Select(group => group.ToArray())
            .ToArray();
        var selected = new List<SessionItemSnapshot>();
        foreach (var turn in turns)
        {
            selected.AddRange(turn);
            var nextSequence = session.ModelHistory
                .Where(item =>
                    item.Status == SessionItemStatus.Completed &&
                    item.Sequence > selected[^1].Sequence &&
                    item.Type is SessionItemType.UserMessage or
                        SessionItemType.AgentMessage or
                        SessionItemType.ToolCall or
                        SessionItemType.ToolResult)
                .Select(item => (long?)item.Sequence)
                .Min();
            var sourceEndSequence =
                (nextSequence ?? selected[^1].Sequence + 1) - 1;
            if (ProjectedResponseTokens(
                    session,
                    draft,
                    sourceEndSequence) <=
                draft.UsableInputBudgetTokens * (long)targetPercent / 100)
            {
                return new CompactionSelection(
                    selected.ToArray(),
                    sourceEndSequence);
            }
        }

        return null;
    }

    private static int ProjectedResponseTokens(
        AgentSession session,
        AgentInvocationDraft draft,
        long sourceEndSequence)
    {
        const string summaryPrefix =
            "Conversation summary of earlier turns:\n";
        var input = new List<JsonElement>
        {
            JsonSerializer.SerializeToElement(new
            {
                type = "message",
                role = "assistant",
                content = summaryPrefix,
            }),
        };
        input.AddRange(
            ProviderResponsesHistory.Build(
                session.ModelHistory.Where(item =>
                    item.Status == SessionItemStatus.Completed &&
                    item.Sequence > sourceEndSequence)));
        return checked(
            AgentFactory.CountPromptTokens(
                draft.Provider.Tokenizer,
                draft.ResponsePrompt.SystemMessage,
                input,
                draft.Tools) +
            Math.Min(
                8192,
                (draft.UsableInputBudgetTokens + 9) / 10));
    }

    private static string CompactionInput(
        CompactionCheckpointSnapshot? checkpoint,
        IReadOnlyList<SessionItemSnapshot> items)
    {
        var result = new StringBuilder();
        if (checkpoint is not null)
        {
            result.Append("Previous authoritative summary:\n");
            result.Append(checkpoint.Summary);
            result.Append("\n\n");
        }

        foreach (var item in ProviderResponsesHistory.Build(items))
        {
            var type = item.GetProperty("type").GetString();
            if (type == "message")
            {
                result.Append(item.GetProperty("role").GetString());
                result.Append(":\n");
                result.Append(NormalizeLf(item.GetProperty("content").GetString()!));
            }
            else if (type == "function_call")
            {
                result.Append("ToolCall ");
                result.Append(item.GetProperty("call_id").GetString());
                result.Append(' ');
                result.Append(item.GetProperty("name").GetString());
                result.Append(' ');
                result.Append(item.GetProperty("arguments").GetString());
            }
            else if (type == "function_call_output")
            {
                result.Append("ToolCallOutput ");
                result.Append(item.GetProperty("call_id").GetString());
                result.Append(' ');
                result.Append(item.GetProperty("output").GetString());
            }

            result.Append("\n\n");
        }

        return result.ToString().TrimEnd('\n');
    }

    private async ValueTask DelayRetryAsync(
        ProviderException exception,
        int attempt,
        CancellationToken cancellationToken)
    {
        var delay = exception.RetryAfter ??
                    (attempt == 1
                        ? TimeSpan.FromMilliseconds(250)
                        : TimeSpan.FromSeconds(1));
        await Task.Delay(
            delay > TimeSpan.FromSeconds(30)
                ? TimeSpan.FromSeconds(30)
                : delay,
            _timeProvider,
            cancellationToken);
    }

    private static ValueTask RecordUsageAsync(
        ISessionExecutionSink sink,
        Guid invocationId,
        int attempt,
        ProviderInvocationPurpose purpose,
        DeepSeekResponsesUsage usage,
        CancellationToken cancellationToken) =>
        sink.EmitAsync(
            new RecordProviderUsageIntent(
                new ProviderUsageSnapshot(
                    invocationId,
                    attempt,
                    purpose,
                    usage.InputTokens,
                    usage.OutputTokens,
                    usage.TotalTokens,
                    ProviderUsageSource.Provider,
                    IsEstimate: false,
                    usage.CachedInputTokens,
                    usage.ReasoningOutputTokens)),
            cancellationToken);

    private static ValueTask RecordEstimatedUsageAsync(
        ISessionExecutionSink sink,
        Guid invocationId,
        int attempt,
        ProviderInvocationPurpose purpose,
        int promptTokens,
        int completionTokens,
        CancellationToken cancellationToken) =>
        sink.EmitAsync(
            new RecordProviderUsageIntent(
                new ProviderUsageSnapshot(
                    invocationId,
                    attempt,
                    purpose,
                    promptTokens,
                    completionTokens,
                    checked(promptTokens + completionTokens),
                    ProviderUsageSource.LocalEstimate,
                    IsEstimate: true)),
            cancellationToken);

    private static ValueTask FailCompactionAsync(
        ISessionExecutionSink sink,
        CancellationToken cancellationToken) =>
        sink.EmitAsync(
            new FailTurnIntent(new SessionError(
                AgentErrorCodes.ContextCompactionFailed,
                "Conversation compaction failed.",
                IsRetryable: false)),
            cancellationToken);

    private static string NormalizeLf(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

    private sealed record CompactionSelection(
        SessionItemSnapshot[] Items,
        long SourceEndSequence);

    private sealed record CompactionResult(
        AgentSession Session,
        AgentInvocationDraft Draft,
        int NextAttempt);

    private sealed record ToolLoopCheckpoint(
        Guid AgentInvocationId,
        string ToolSnapshotSha256,
        int ProviderRound,
        int NextAttemptNumber,
        Guid ToolCallItemId,
        int CallIndex,
        Guid ToolInvocationId,
        string ArgumentsSha256,
        string NextPipelineStage,
        long RemainingBudgetTicks);

    private sealed record KnownToolCall(
        string ProviderToolName,
        string ArgumentsSha256,
        ToolResultSnapshot? Result,
        AgentToolInvocationSnapshot? State = null);

    private sealed record PendingToolFrame(
        SessionItemSnapshot Item,
        ToolCallItemContent Content,
        IReadOnlyList<AgentToolInvocationSnapshot?> States);

    private static async ValueTask FailAsync(
        ISessionExecutionSink sink,
        Guid contentItemId,
        Guid reasoningItemId,
        SessionError error,
        CancellationToken cancellationToken)
    {
        if (reasoningItemId != Guid.Empty)
        {
            await sink.EmitAsync(
                new FailItemIntent(reasoningItemId, error),
                cancellationToken);
        }

        if (contentItemId != Guid.Empty)
        {
            await sink.EmitAsync(
                new FailItemIntent(contentItemId, error),
                cancellationToken);
        }

        await sink.EmitAsync(
            new FailTurnIntent(error),
            cancellationToken);
    }
}
