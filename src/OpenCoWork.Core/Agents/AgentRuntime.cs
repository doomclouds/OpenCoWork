using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenCoWork.Abstractions;
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
        services.TryAddSingleton(static _ => new ToolRuntime());
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
                serviceProvider.GetRequiredService<ToolsConfig>()));
        services.TryAddSingleton(static _ =>
            OpenAiCompatibleChatClient.CreateSharedHttpClient());
        services.TryAddSingleton(serviceProvider =>
            new AgentRuntimeExecutor(
                serviceProvider.GetRequiredService<AgentFactory>(),
                serviceProvider.GetRequiredService<OpenCoWorkPaths>(),
                serviceProvider.GetRequiredService<HttpClient>(),
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
        IEnumerable<SessionItemSnapshot> source)
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
                    AddToolGroup(
                        items,
                        byId,
                        item,
                        index,
                        consumedResults,
                        messages);
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
        List<ChatCompletionMessage> messages)
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

        if (resultIndex != toolCall.Calls.Count)
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
    ToolsConfig? toolsConfig = null)
{
    private readonly ProviderRegistry _providers =
        providers ?? throw new ArgumentNullException(nameof(providers));
    private readonly OpenCoWorkPaths _paths =
        paths ?? throw new ArgumentNullException(nameof(paths));
    private readonly ToolRuntime _tools = tools ?? new ToolRuntime();
    private readonly ToolsConfig _toolsConfig = toolsConfig ?? new ToolsConfig();

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
        EffectiveToolSnapshot toolSnapshot;
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
            provider.Tokenizer);
        var compactionPrompt =
            AgentPrompts.CreateCompaction(provider.Tokenizer);
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

        var messages = BuildMessages(
            session,
            responsePrompt.SystemMessage);
        var inputTokenCount = CountPromptTokens(
            provider.Tokenizer,
            messages,
            tools);
        var usableInputBudget =
            provider.ContextWindowTokens - provider.MaxOutputTokens;
        var currentMessages = ProviderMessageHistory.Build(
            session.ModelHistory.Where(item =>
                item.TurnId == session.Turn.TurnId));
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
        var snapshot = new AgentInvocationSnapshot(
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
            toolSnapshot);
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

    private static ChatCompletionMessage[] BuildMessages(
        AgentSession session,
        string systemMessage)
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

        messages.AddRange(ProviderMessageHistory.Build(history));
        if (currentUserMessageCount != 1 ||
            messages[^1].Role is not (
                ChatCompletionMessageRole.User or
                ChatCompletionMessageRole.Tool))
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
    private readonly TimeProvider _timeProvider;

    public AgentRuntimeExecutor(
        AgentFactory factory,
        OpenCoWorkPaths paths,
        HttpClient httpClient,
        TimeProvider? timeProvider = null)
        : this(
            factory,
            paths,
            provider => new OpenAiCompatibleChatClient(
                httpClient,
                provider.BaseUri,
                provider.ApiKey,
                timeProvider),
            timeProvider)
    {
    }

    internal AgentRuntimeExecutor(
        AgentFactory factory,
        OpenCoWorkPaths paths,
        Func<ProviderModelRegistration, IChatCompletionClient> clients,
        TimeProvider? timeProvider = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask ExecuteAsync(
        AgentSession context,
        ISessionExecutionSink sink,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(sink);
        using var deadline = new CancellationTokenSource(
            InvocationTimeout,
            _timeProvider);
        using var invocationCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                deadline.Token);
        var invocationToken = invocationCancellation.Token;
        WorkspaceInstructionDocument? instructions;
        AgentInvocationDraft draft;
        try
        {
            instructions = WorkspaceInstructionDocument.Read(_paths);
            draft = _factory.Create(
                context,
                Guid.CreateVersion7(_timeProvider.GetUtcNow()),
                instructions);
            await sink.EmitAsync(
                new RecordAgentInvocationSnapshotIntent(draft.Snapshot),
                cancellationToken);
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

        var contentItemId = Guid.Empty;
        var reasoningItemId = Guid.Empty;
        var visible = false;
        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        try
        {
            var session = context;
            var nextAttempt = 1;
            if (draft.Disposition == AgentInvocationDraftDisposition.CompactionRequired)
            {
                var compacted = await CompactAsync(
                    session,
                    draft,
                    instructions,
                    sink,
                    nextAttempt,
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
                if (nextAttempt > 3)
                {
                    await FailCompactionAsync(sink, cancellationToken);
                    return;
                }
            }

            var reactiveCompactionUsed = false;
            while (nextAttempt <= 3)
            {
                var attempt = nextAttempt++;
                var usageRecorded = false;
                ChatCompletionFinishReason? finishReason = null;
                try
                {
                    var request = new ChatCompletionRequest(
                        draft.Provider.ModelId,
                        draft.Messages,
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
                                if (contentItemId == Guid.Empty)
                                {
                                    contentItemId =
                                        Guid.CreateVersion7(_timeProvider.GetUtcNow());
                                    await sink.EmitAsync(
                                        new StartItemIntent(
                                            contentItemId,
                                            SessionItemType.AgentMessage,
                                            new TextItemContent(string.Empty)),
                                        cancellationToken);
                                }

                                await sink.EmitAsync(
                                    new AppendItemDeltaIntent(
                                        contentItemId,
                                        delta.Delta,
                                        Flush: !visible),
                                    cancellationToken);
                                content.Append(delta.Delta);
                                visible = true;
                                break;
                            case ChatCompletionReasoningDeltaEvent delta
                                when delta.Delta.Length != 0:
                                if (reasoningItemId == Guid.Empty)
                                {
                                    reasoningItemId =
                                        Guid.CreateVersion7(_timeProvider.GetUtcNow());
                                    await sink.EmitAsync(
                                        new StartItemIntent(
                                            reasoningItemId,
                                            SessionItemType.Reasoning,
                                            new TextItemContent(string.Empty)),
                                        cancellationToken);
                                }

                                await sink.EmitAsync(
                                    new AppendItemDeltaIntent(
                                        reasoningItemId,
                                        delta.Delta,
                                        Flush: !visible),
                                    cancellationToken);
                                reasoning.Append(delta.Delta);
                                visible = true;
                                break;
                            case ChatCompletionUsageEvent usage:
                                await sink.EmitAsync(
                                    new RecordProviderUsageIntent(
                                        new ProviderUsageSnapshot(
                                            draft.Snapshot.InvocationId,
                                            attempt,
                                            ChatCompletionInvocationPurpose.Response,
                                            usage.Usage.PromptTokens,
                                            usage.Usage.CompletionTokens,
                                            usage.Usage.TotalTokens,
                                            ProviderUsageSource.Provider,
                                            IsEstimate: false)),
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
                        var completionTokens = draft.Provider.Tokenizer.CountTokens(
                            content.ToString() + reasoning);
                        await sink.EmitAsync(
                            new RecordProviderUsageIntent(
                                new ProviderUsageSnapshot(
                                    draft.Snapshot.InvocationId,
                                    attempt,
                                    ChatCompletionInvocationPurpose.Response,
                                    draft.InputTokenCount,
                                    completionTokens,
                                    checked(draft.InputTokenCount + completionTokens),
                                    ProviderUsageSource.LocalEstimate,
                                    IsEstimate: true)),
                            cancellationToken);
                    }

                    var error = FinishError(finishReason, content.Length != 0);
                    if (error is not null)
                    {
                        await FailAsync(
                            sink,
                            contentItemId,
                            reasoningItemId,
                            error,
                            cancellationToken);
                        return;
                    }

                    if (reasoningItemId != Guid.Empty)
                    {
                        await sink.EmitAsync(
                            new CompleteItemIntent(reasoningItemId),
                            cancellationToken);
                    }

                    if (contentItemId != Guid.Empty)
                    {
                        await sink.EmitAsync(
                            new CompleteItemIntent(contentItemId),
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
                    if (!visible &&
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
                            targetPercent: 50,
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
                        if (nextAttempt > 3)
                        {
                            await FailCompactionAsync(sink, cancellationToken);
                            return;
                        }

                        continue;
                    }

                    if (!visible &&
                        exception.IsTransient &&
                        nextAttempt <= 3)
                    {
                        await DelayRetryAsync(
                            exception,
                            attempt,
                            invocationToken);
                        continue;
                    }

                    if (!visible && exception.IsPromptTooLong)
                    {
                        await FailCompactionAsync(sink, cancellationToken);
                        return;
                    }

                    await FailAsync(
                        sink,
                        contentItemId,
                        reasoningItemId,
                        new SessionError(
                            exception.Code,
                            exception.Message,
                            exception.IsTransient),
                        cancellationToken);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (
            deadline.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            await FailAsync(
                sink,
                contentItemId,
                reasoningItemId,
                new SessionError(
                    AgentErrorCodes.ProviderTimeout,
                    "Provider invocation timed out.",
                    IsRetryable: false),
                cancellationToken);
        }
    }

    private async ValueTask<CompactionResult?> CompactAsync(
        AgentSession session,
        AgentInvocationDraft draft,
        WorkspaceInstructionDocument? instructions,
        ISessionExecutionSink sink,
        int nextAttempt,
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

        while (nextAttempt <= 3)
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
                    checkpoint);
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
                if (!exception.IsTransient || nextAttempt > 3)
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
