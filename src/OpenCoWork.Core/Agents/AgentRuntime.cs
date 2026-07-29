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
            new ToolRuntime(
                serviceProvider.GetRequiredService<OpenCoWorkPaths>(),
                serviceProvider.GetRequiredService<ModelsConfig>()));
        services.TryAddSingleton(serviceProvider =>
            FrozenProviderCredentials.Capture(
                serviceProvider.GetRequiredService<ModelsConfig>()));
        services.TryAddSingleton(serviceProvider =>
        {
            var credentials =
                serviceProvider.GetRequiredService<FrozenProviderCredentials>();
            var snapshot =
                serviceProvider.GetService<EffectiveConfigSnapshot>();
            return snapshot is null
                ? new SecretRedactor(credentials.GetSecretValues())
                : SecretRedactor.FromSnapshot(snapshot, credentials);
        });
        services.TryAddSingleton(serviceProvider =>
            new ProviderRegistry(
                serviceProvider.GetRequiredService<ModelsConfig>(),
                serviceProvider.GetRequiredService<FrozenProviderCredentials>(),
                AppContext.BaseDirectory,
                serviceProvider.GetRequiredService<OpenCoWorkPaths>().WorkspaceRoot));
        services.TryAddSingleton(serviceProvider =>
            new AgentFactory(
                serviceProvider.GetRequiredService<ProviderRegistry>(),
                serviceProvider.GetRequiredService<OpenCoWorkPaths>(),
                serviceProvider.GetRequiredService<ToolRuntime>(),
                serviceProvider.GetRequiredService<ToolsConfig>(),
                serviceProvider.GetService<WorkspaceCapabilityRuntime>()));
        services.TryAddSingleton<IToolInvocationPipeline>(serviceProvider =>
            new ToolInvocationPipeline(
                serviceProvider.GetRequiredService<ToolRuntime>(),
                serviceProvider.GetRequiredService<SecretRedactor>(),
                timeProvider: serviceProvider.GetService<TimeProvider>()));
        services.TryAddSingleton(static _ =>
            OpenAiCompatibleChatClient.CreateSharedHttpClient());
        services.TryAddSingleton(serviceProvider =>
            new AgentRuntimeExecutor(
                serviceProvider.GetRequiredService<AgentFactory>(),
                serviceProvider.GetRequiredService<OpenCoWorkPaths>(),
                serviceProvider.GetRequiredService<HttpClient>(),
                serviceProvider.GetRequiredService<IToolInvocationPipeline>(),
                serviceProvider.GetRequiredService<SecretRedactor>(),
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
    string ApiKey,
    string TokenizerProfileId,
    string TokenizerProfileVersion,
    string ChatTemplateId,
    string ChatTemplateVersion,
    int ContextWindowTokens,
    int MaxOutputTokens,
    string ConfigurationSha256,
    ModelTokenizer Tokenizer);

internal sealed class ProviderRegistry
{
    private readonly object _gate = new();
    private readonly ModelsConfig _models;
    private readonly FrozenProviderCredentials _credentials;
    private readonly string _bundledTokenizerBaseDirectory;
    private readonly string _customTokenizerBaseDirectory;
    private readonly Dictionary<string, ProviderModelRegistration> _resolved =
        new(StringComparer.Ordinal);

    public ProviderRegistry(
        ModelsConfig models,
        FrozenProviderCredentials credentials,
        string bundledTokenizerBaseDirectory,
        string customTokenizerBaseDirectory)
    {
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _credentials = credentials
            ?? throw new ArgumentNullException(nameof(credentials));
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

            if (!_models.Providers.TryGetValue(providerId, out var provider) ||
                !provider.Models.TryGetValue(modelId, out var model))
            {
                throw new AgentPreparationException(
                    AgentErrorCodes.ContextInputInvalid,
                    "The configured provider/model selection is unavailable.");
            }

            var tokenizer = ModelSelectionPreflight.Validate(
                _models,
                _credentials,
                providerId,
                modelId,
                _bundledTokenizerBaseDirectory,
                _customTokenizerBaseDirectory);
            var profile = TokenizerProfiles.TryGetForModel(modelId, out var builtIn)
                ? builtIn
                : null;
            var registration = new ProviderModelRegistration(
                providerId,
                modelId,
                new Uri(provider.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute),
                _credentials.GetRequired(providerId),
                model.TokenizerProfileId,
                model.TokenizerProfileVersion,
                profile?.ChatTemplateId ?? "openai-compatible-chat",
                profile?.ChatTemplateVersion ?? "1",
                model.ContextWindowTokens,
                model.MaxOutputTokens,
                ConfigurationHash(providerId, modelId, provider, model),
                tokenizer);
            _resolved.Add(key, registration);
            return registration;
        }
    }

    private static string ConfigurationHash(
        string providerId,
        string modelId,
        ProviderConfig provider,
        ModelConfig model)
    {
        var canonical = string.Join(
            '\n',
            providerId,
            modelId,
            provider.BaseUrl.TrimEnd('/'),
            provider.ApiKey.Environment,
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

internal static class ProviderMessageHistory
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static IReadOnlyList<ChatCompletionMessage> Build(
        IEnumerable<SessionItemSnapshot> source,
        bool allowIncompleteFinalToolGroup = false)
    {
        var items = source
            .Where(item =>
                item.Status == SessionItemStatus.Completed &&
                item.Type is
                    SessionItemType.UserMessage or
                    SessionItemType.AgentMessage or
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
        var messages = new List<ChatCompletionMessage>();
        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            switch (item.Type)
            {
                case SessionItemType.UserMessage:
                    messages.Add(new ChatCompletionMessage(
                        ChatCompletionMessageRole.User,
                        Text(item)));
                    break;
                case SessionItemType.AgentMessage:
                    if (!linkedAgentIds.Contains(item.ItemId))
                    {
                        messages.Add(new ChatCompletionMessage(
                            ChatCompletionMessageRole.Assistant,
                            Text(item)));
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
                        messages,
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

        return messages.AsReadOnly();
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
        List<ChatCompletionMessage> messages,
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

        messages.Add(new ChatCompletionMessage(
            ChatCompletionMessageRole.Assistant,
            content,
            toolCall.Calls
                .Select(call => new ChatCompletionToolCall(
                    call.ProviderToolCallId,
                    call.ProviderToolName,
                    Encoding.UTF8.GetString(
                        ThreadJournal.Canonicalize(call.Arguments))))
                .ToArray()));

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

                messages.Add(new ChatCompletionMessage(
                    ChatCompletionMessageRole.Tool,
                    ToolResultEnvelope(result.Result),
                    ToolCallId: result.Result.ProviderToolCallId));
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
    IReadOnlyList<ChatCompletionMessage> Messages,
    IReadOnlyList<ChatCompletionToolDefinition> Tools,
    int InputTokenCount,
    int UsableInputBudgetTokens);

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
        var skillSnapshot = frozen?.Skills ?? EffectiveSkillSnapshot.Empty;
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
                    _toolsConfig);
            }
            catch (ToolRuntimeException exception)
            {
                throw new AgentPreparationException(exception.Code, exception.Message);
            }
        }

        var tools = _tools.CreateProviderDefinitions(toolSnapshot);
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
        var messages = BuildMessages(
            session,
            responsePrompt.SystemMessage,
            hasIncompleteToolInvocation);
        var inputTokenCount = CountPromptTokens(
            provider.Tokenizer,
            messages,
            tools);
        var usableInputBudget =
            provider.ContextWindowTokens - provider.MaxOutputTokens;
        var currentMessages = ProviderMessageHistory.Build(
            session.ModelHistory.Where(item =>
                item.TurnId == session.Turn.TurnId),
            hasIncompleteToolInvocation);
        var fixedInputCount = CountPromptTokens(
            provider.Tokenizer,
            [messages[0], .. currentMessages],
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
            skillSnapshot);
        return new AgentInvocationDraft(
            disposition,
            provider,
            snapshot,
            responsePrompt,
            compactionPrompt,
            Array.AsReadOnly(messages),
            tools,
            inputTokenCount,
            usableInputBudget);
    }

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
            frozen.ConfigurationSha256,
            provider.ConfigurationSha256,
            StringComparison.Ordinal) &&
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

    private static ChatCompletionMessage[] BuildMessages(
        AgentSession session,
        string systemMessage,
        bool allowIncompleteFinalToolGroup)
    {
        var messages = new List<ChatCompletionMessage>
        {
            new(ChatCompletionMessageRole.System, systemMessage),
        };
        var sourceEndSequence =
            session.CompactionCheckpoint?.SourceEndSequence ?? long.MinValue;
        if (session.CompactionCheckpoint is { } checkpoint)
        {
            messages.Add(new ChatCompletionMessage(
                ChatCompletionMessageRole.Assistant,
                "Conversation summary of earlier turns:\n" +
                checkpoint.Summary));
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

        messages.AddRange(ProviderMessageHistory.Build(
            history,
            allowIncompleteFinalToolGroup));
        if (currentUserMessageCount != 1 ||
            messages[^1].Role is not (
                ChatCompletionMessageRole.User or
                ChatCompletionMessageRole.Tool) &&
            !(allowIncompleteFinalToolGroup &&
              messages[^1].Role == ChatCompletionMessageRole.Assistant &&
              messages[^1].ToolCalls is { Count: > 0 }))
        {
            throw new AgentPreparationException(
                AgentErrorCodes.ContextInputInvalid,
                "The current user input is missing or duplicated.");
        }

        return [.. messages];
    }

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
        IReadOnlyList<ChatCompletionMessage> messages,
        IReadOnlyList<ChatCompletionToolDefinition>? tools = null)
    {
        var count = 3;
        foreach (var message in messages)
        {
            count = checked(
                count +
                5 +
                tokenizer.CountTokens(message.Role.ToString().ToLowerInvariant()) +
                tokenizer.CountTokens(message.Content));
            if (message.ToolCallId is { } toolCallId)
            {
                count = checked(count + 2 + tokenizer.CountTokens(toolCallId));
            }

            foreach (var toolCall in message.ToolCalls ?? [])
            {
                count = checked(
                    count +
                    8 +
                    tokenizer.CountTokens(toolCall.Id) +
                    tokenizer.CountTokens(toolCall.Name) +
                    tokenizer.CountTokens(toolCall.Arguments));
            }
        }

        foreach (var tool in tools ?? [])
        {
            count = checked(
                count +
                10 +
                tokenizer.CountTokens(tool.ProviderName) +
                tokenizer.CountTokens(tool.Description) +
                tokenizer.CountTokens(tool.InputSchema.GetRawText()));
        }

        return count;
    }
}

internal sealed class AgentRuntimeExecutor : ISessionExecutor
{
    private static readonly TimeSpan InvocationTimeout = TimeSpan.FromMinutes(30);
    private readonly AgentFactory _factory;
    private readonly OpenCoWorkPaths _paths;
    private readonly Func<ProviderModelRegistration, IChatCompletionClient> _clients;
    private readonly IToolInvocationPipeline _toolPipeline;
    private readonly SecretRedactor _redactor;
    private readonly TimeProvider _timeProvider;

    public AgentRuntimeExecutor(
        AgentFactory factory,
        OpenCoWorkPaths paths,
        HttpClient httpClient,
        IToolInvocationPipeline toolPipeline,
        SecretRedactor redactor,
        TimeProvider? timeProvider = null)
        : this(
            factory,
            paths,
            provider => new OpenAiCompatibleChatClient(
                httpClient,
                provider.BaseUri,
                provider.ApiKey,
                timeProvider),
            timeProvider,
            toolPipeline,
            redactor)
    {
    }

    internal AgentRuntimeExecutor(
        AgentFactory factory,
        OpenCoWorkPaths paths,
        Func<ProviderModelRegistration, IChatCompletionClient> clients,
        TimeProvider? timeProvider = null,
        IToolInvocationPipeline? toolPipeline = null,
        SecretRedactor? redactor = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _redactor = redactor ?? new SecretRedactor([]);
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
        Dictionary<string, KnownToolCall> knownToolCalls;
        PendingToolFrame? pendingToolFrame;
        try
        {
            instructions = WorkspaceInstructionDocument.Read(_paths);
            draft = _factory.Create(
                context,
                context.Invocation?.InvocationId ??
                Guid.CreateVersion7(_timeProvider.GetUtcNow()),
                instructions);
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
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or
            UnauthorizedAccessException)
        {
            await sink.EmitAsync(
                new FailTurnIntent(new SessionError(
                    AgentErrorCodes.ContextInputInvalid,
                    "Agent invocation preparation failed.",
                    IsRetryable: false)),
                cancellationToken);
            return;
        }

        var activeContentItemId = Guid.Empty;
        var activeReasoningItemId = Guid.Empty;
        try
        {
            var session = context;
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

            var messages = draft.Messages.ToList();
            var anyToolAttempted = false;
            if (pendingToolFrame is not null)
            {
                anyToolAttempted = await ResumeToolFrameAsync(
                    context,
                    draft,
                    pendingToolFrame,
                    resumeCheckpoint,
                    knownToolCalls,
                    messages,
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

            var reactiveCompactionUsed = false;
            while (true)
            {
                var toolFrameCompleted = false;
                for (var stepAttempt = 1; stepAttempt <= 3; stepAttempt++)
                {
                    var attempt = nextAttempt++;
                    var usageRecorded = false;
                    var stepVisible = false;
                    var content = new StringBuilder();
                    var reasoning = new StringBuilder();
                    var toolCalls =
                        new List<ChatCompletionToolCallCompletedEvent>();
                    activeContentItemId = Guid.Empty;
                    activeReasoningItemId = Guid.Empty;
                    ChatCompletionFinishReason? finishReason = null;
                    var inputTokenCount = AgentFactory.CountPromptTokens(
                        draft.Provider.Tokenizer,
                        messages,
                        draft.Tools);
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
                        var request = new ChatCompletionRequest(
                            draft.Provider.ModelId,
                            messages,
                            draft.Provider.MaxOutputTokens,
                            draft.Snapshot.InvocationId,
                            attempt,
                            ChatCompletionInvocationPurpose.Response,
                            draft.Tools);
                        await foreach (var item in _clients(draft.Provider)
                                           .StreamAsync(request, invocationToken)
                                           .WithCancellation(invocationToken))
                        {
                            switch (item)
                            {
                                case ChatCompletionContentDeltaEvent delta
                                    when delta.Delta.Length != 0:
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
                                case ChatCompletionReasoningDeltaEvent delta
                                    when delta.Delta.Length != 0:
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
                                case ChatCompletionToolCallCompletedEvent toolCall:
                                    toolCalls.Add(toolCall);
                                    break;
                                case ChatCompletionUsageEvent usage:
                                    await RecordUsageAsync(
                                        sink,
                                        draft.Snapshot.InvocationId,
                                        attempt,
                                        ChatCompletionInvocationPurpose.Response,
                                        usage.Usage,
                                        cancellationToken);
                                    usageRecorded = true;
                                    break;
                                case ChatCompletionCompletedEvent completed:
                                    finishReason = completed.FinishReason;
                                    break;
                            }
                        }

                        if (!usageRecorded)
                        {
                            var completionTokens =
                                draft.Provider.Tokenizer.CountTokens(
                                    content.ToString() + reasoning);
                            await RecordEstimatedUsageAsync(
                                sink,
                                draft.Snapshot.InvocationId,
                                attempt,
                                ChatCompletionInvocationPurpose.Response,
                                inputTokenCount,
                                completionTokens,
                                cancellationToken);
                        }

                        if (finishReason == ChatCompletionFinishReason.ToolCall)
                        {
                            if (toolCalls.Count == 0)
                            {
                                throw new ChatCompletionException(
                                    AgentErrorCodes.ProviderInvalidStream,
                                    "Provider returned an incomplete tool call frame.");
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
                            messages.Add(new ChatCompletionMessage(
                                ChatCompletionMessageRole.Assistant,
                                content.ToString(),
                                frame.Calls
                                    .Select(call => new ChatCompletionToolCall(
                                        call.ProviderToolCallId,
                                        call.ProviderToolName,
                                        Encoding.UTF8.GetString(
                                            ThreadJournal.Canonicalize(
                                                call.Arguments))))
                                    .ToArray()));
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
                                        known is not null && !sameCall),
                                    sink,
                                    invocationToken);
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

                                messages.Add(new ChatCompletionMessage(
                                    ChatCompletionMessageRole.Tool,
                                    ProviderMessageHistory.ToolResultEnvelope(result),
                                    ToolCallId: call.ProviderToolCallId));
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
                            throw new ChatCompletionException(
                                AgentErrorCodes.ProviderInvalidStream,
                                "Provider returned tool calls with an invalid finish reason.");
                        }

                        var error = FinishError(
                            finishReason,
                            content.Length != 0);
                        if (error is not null)
                        {
                            await FailAsync(
                                sink,
                                activeContentItemId,
                                activeReasoningItemId,
                                error,
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

                        if (finishReason == ChatCompletionFinishReason.Length)
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
                    catch (ChatCompletionException exception)
                    {
                        if (!stepVisible &&
                            !anyToolAttempted &&
                            providerRound == 0 &&
                            exception.IsPromptTooLong &&
                            !reactiveCompactionUsed)
                        {
                            reactiveCompactionUsed = true;
                            var compacted = await CompactAsync(
                                session,
                                draft,
                                instructions,
                                sink,
                                nextAttempt,
                                maximumAttempt: 3,
                                targetPercent: 50,
                                invocationToken,
                                cancellationToken);
                            if (compacted is null)
                            {
                                await FailCompactionAsync(
                                    sink,
                                    cancellationToken);
                                return;
                            }

                            session = compacted.Session;
                            draft = compacted.Draft;
                            messages = draft.Messages.ToList();
                            nextAttempt = compacted.NextAttempt;
                            if (nextAttempt > 3)
                            {
                                await FailCompactionAsync(
                                    sink,
                                    cancellationToken);
                                return;
                            }

                            break;
                        }

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

                        if (!stepVisible && exception.IsPromptTooLong)
                        {
                            await FailCompactionAsync(sink, cancellationToken);
                            return;
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
        PendingToolFrame frame,
        ToolLoopCheckpoint? resumeCheckpoint,
        Dictionary<string, KnownToolCall> knownToolCalls,
        List<ChatCompletionMessage> messages,
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
                    ProviderCallIdConflict: known is not null && !sameCall),
                sink,
                cancellationToken);
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

            messages.Add(new ChatCompletionMessage(
                ChatCompletionMessageRole.Tool,
                ProviderMessageHistory.ToolResultEnvelope(result),
                ToolCallId: call.ProviderToolCallId));
        }

        return anyToolAttempted;
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
        IReadOnlyList<ChatCompletionToolCallCompletedEvent> calls)
    {
        if (calls.Count == 0 ||
            calls.Select(call => call.Id)
                .Distinct(StringComparer.Ordinal)
                .Count() != calls.Count)
        {
            throw InvalidToolFrame();
        }

        var entries = new ToolCallItemEntry[calls.Count];
        for (var index = 0; index < calls.Count; index++)
        {
            var call = calls[index];
            if (call.Index != index ||
                string.IsNullOrWhiteSpace(call.Id) ||
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
                    call.Id,
                    call.Name,
                    arguments,
                    Convert.ToHexString(SHA256.HashData(canonical))
                        .ToLowerInvariant(),
                    sensitiveInputDetected);
            }
            catch (ChatCompletionException)
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

    private static ChatCompletionException InvalidToolFrame() =>
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
        CancellationToken invocationToken,
        CancellationToken cancellationToken)
    {
        var selection = SelectCompaction(session, draft, targetPercent);
        if (selection is null)
        {
            return null;
        }

        var messages = new[]
        {
            new ChatCompletionMessage(
                ChatCompletionMessageRole.System,
                draft.CompactionPrompt.SystemMessage),
            new ChatCompletionMessage(
                ChatCompletionMessageRole.User,
                CompactionInput(session.CompactionCheckpoint, selection.Items)),
        };
        var promptTokens = AgentFactory.CountPromptTokens(
            draft.Provider.Tokenizer,
            messages);
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
            var usageRecorded = false;
            ChatCompletionFinishReason? finishReason = null;
            try
            {
                var request = new ChatCompletionRequest(
                    draft.Provider.ModelId,
                    messages,
                    maxSummaryTokens,
                    draft.Snapshot.InvocationId,
                    attempt,
                    ChatCompletionInvocationPurpose.Compaction);
                await foreach (var item in _clients(draft.Provider)
                                   .StreamAsync(request, invocationToken)
                                   .WithCancellation(invocationToken))
                {
                    switch (item)
                    {
                        case ChatCompletionContentDeltaEvent delta:
                            summary.Append(delta.Delta);
                            break;
                        case ChatCompletionUsageEvent usage:
                            await RecordUsageAsync(
                                sink,
                                draft.Snapshot.InvocationId,
                                attempt,
                                ChatCompletionInvocationPurpose.Compaction,
                                usage.Usage,
                                cancellationToken);
                            usageRecorded = true;
                            break;
                        case ChatCompletionCompletedEvent completed:
                            finishReason = completed.FinishReason;
                            break;
                    }
                }

                var normalizedSummary = NormalizeLf(summary.ToString());
                var summaryTokens =
                    draft.Provider.Tokenizer.CountTokens(normalizedSummary);
                if (!usageRecorded)
                {
                    await RecordEstimatedUsageAsync(
                        sink,
                        draft.Snapshot.InvocationId,
                        attempt,
                        ChatCompletionInvocationPurpose.Compaction,
                        promptTokens,
                        summaryTokens,
                        cancellationToken);
                }

                if (finishReason != ChatCompletionFinishReason.Stop ||
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
                    session.ProviderUsage);
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
            catch (ChatCompletionException exception)
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
        var messages = new List<ChatCompletionMessage>
        {
            new(
                ChatCompletionMessageRole.System,
                draft.ResponsePrompt.SystemMessage),
            new(
                ChatCompletionMessageRole.Assistant,
                summaryPrefix),
        };
        messages.AddRange(
            ProviderMessageHistory.Build(
                session.ModelHistory.Where(item =>
                    item.Status == SessionItemStatus.Completed &&
                    item.Sequence > sourceEndSequence)));
        return checked(
            AgentFactory.CountPromptTokens(
                draft.Provider.Tokenizer,
                messages,
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

        foreach (var message in ProviderMessageHistory.Build(items))
        {
            result.Append(message.Role);
            result.Append(":\n");
            result.Append(NormalizeLf(message.Content));
            foreach (var call in message.ToolCalls ?? [])
            {
                result.Append("\nToolCall ");
                result.Append(call.Id);
                result.Append(' ');
                result.Append(call.Name);
                result.Append(' ');
                result.Append(call.Arguments);
            }

            if (message.ToolCallId is { } toolCallId)
            {
                result.Append("\nToolCallId ");
                result.Append(toolCallId);
            }

            result.Append("\n\n");
        }

        return result.ToString().TrimEnd('\n');
    }

    private async ValueTask DelayRetryAsync(
        ChatCompletionException exception,
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
        ChatCompletionInvocationPurpose purpose,
        ChatCompletionUsage usage,
        CancellationToken cancellationToken) =>
        sink.EmitAsync(
            new RecordProviderUsageIntent(
                new ProviderUsageSnapshot(
                    invocationId,
                    attempt,
                    purpose,
                    usage.PromptTokens,
                    usage.CompletionTokens,
                    usage.TotalTokens,
                    ProviderUsageSource.Provider,
                    IsEstimate: false)),
            cancellationToken);

    private static ValueTask RecordEstimatedUsageAsync(
        ISessionExecutionSink sink,
        Guid invocationId,
        int attempt,
        ChatCompletionInvocationPurpose purpose,
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

    private static SessionError? FinishError(
        ChatCompletionFinishReason? finishReason,
        bool hasContent) =>
        finishReason switch
        {
            ChatCompletionFinishReason.Stop when hasContent => null,
            ChatCompletionFinishReason.Length when hasContent => null,
            ChatCompletionFinishReason.ContentFilter => new SessionError(
                AgentErrorCodes.ProviderContentFiltered,
                "Provider filtered the response.",
                IsRetryable: false),
            ChatCompletionFinishReason.ToolCall => new SessionError(
                AgentErrorCodes.ProviderUnsupportedToolCall,
                "Provider returned an unsupported tool call.",
                IsRetryable: false),
            _ when !hasContent => new SessionError(
                AgentErrorCodes.ProviderEmptyResponse,
                "Provider returned no response content.",
                IsRetryable: false),
            _ => new SessionError(
                AgentErrorCodes.ProviderInvalidStream,
                "Provider returned an invalid finish reason.",
                IsRetryable: false),
        };

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
