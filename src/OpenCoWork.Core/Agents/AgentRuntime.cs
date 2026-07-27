using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Workspaces;

namespace OpenCoWork.Core.Agents;

public static class OpenCoWorkAgentExtensions
{
    public static IServiceCollection AddOpenCoWorkAgentRuntime(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ModelsConfig>();
        services.TryAddSingleton(serviceProvider =>
            FrozenProviderCredentials.Capture(
                serviceProvider.GetRequiredService<ModelsConfig>()));
        services.TryAddSingleton(serviceProvider =>
            new ProviderRegistry(
                serviceProvider.GetRequiredService<ModelsConfig>(),
                serviceProvider.GetRequiredService<FrozenProviderCredentials>(),
                AppContext.BaseDirectory,
                serviceProvider.GetRequiredService<OpenCoWorkPaths>().WorkspaceRoot));
        services.TryAddSingleton(serviceProvider =>
            new AgentFactory(
                serviceProvider.GetRequiredService<ProviderRegistry>(),
                serviceProvider.GetRequiredService<OpenCoWorkPaths>()));
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

internal sealed record AgentInvocationDraft(
    AgentInvocationDraftDisposition Disposition,
    ProviderModelRegistration Provider,
    AgentInvocationSnapshot Snapshot,
    AgentPromptMaterialization ResponsePrompt,
    AgentPromptMaterialization CompactionPrompt,
    IReadOnlyList<ChatCompletionMessage> Messages,
    IReadOnlyList<string> ToolIds,
    int InputTokenCount,
    int UsableInputBudgetTokens);

internal sealed class AgentFactory(
    ProviderRegistry providers,
    OpenCoWorkPaths paths)
{
    private readonly ProviderRegistry _providers =
        providers ?? throw new ArgumentNullException(nameof(providers));
    private readonly OpenCoWorkPaths _paths =
        paths ?? throw new ArgumentNullException(nameof(paths));

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
        var messages = BuildMessages(
            session,
            responsePrompt.SystemMessage);
        var inputTokenCount = CountPromptTokens(provider.Tokenizer, messages);
        var usableInputBudget =
            provider.ContextWindowTokens - provider.MaxOutputTokens;
        var currentInput = messages[^1];
        var fixedInputCount = CountPromptTokens(
            provider.Tokenizer,
            [messages[0], currentInput]);
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
            provider.ConfigurationSha256);
        return new AgentInvocationDraft(
            disposition,
            provider,
            snapshot,
            responsePrompt,
            compactionPrompt,
            Array.AsReadOnly(messages),
            Array.Empty<string>(),
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
        foreach (var item in session.ModelHistory
                     .Where(item =>
                         item.Status == SessionItemStatus.Completed &&
                         item.Sequence > sourceEndSequence)
                     .OrderBy(item => item.Sequence)
                     .ThenBy(item => item.ItemId))
        {
            var role = item.Type switch
            {
                SessionItemType.UserMessage => ChatCompletionMessageRole.User,
                SessionItemType.AgentMessage => ChatCompletionMessageRole.Assistant,
                _ => (ChatCompletionMessageRole?)null,
            };
            if (role is null)
            {
                continue;
            }

            if (item.Content is not TextItemContent text)
            {
                throw new AgentPreparationException(
                    AgentErrorCodes.ContextInputInvalid,
                    "Model history contains invalid message content.");
            }

            messages.Add(new ChatCompletionMessage(role.Value, text.Text));
            if (item.TurnId == session.Turn.TurnId &&
                item.Type == SessionItemType.UserMessage)
            {
                currentUserMessageCount++;
            }
        }

        if (currentUserMessageCount != 1 ||
            messages[^1].Role != ChatCompletionMessageRole.User)
        {
            throw new AgentPreparationException(
                AgentErrorCodes.ContextInputInvalid,
                "The current user input is missing or duplicated.");
        }

        return [.. messages];
    }

    private static int CountPromptTokens(
        ModelTokenizer tokenizer,
        IReadOnlyList<ChatCompletionMessage> messages)
    {
        var count = 3;
        foreach (var message in messages)
        {
            count = checked(
                count +
                5 +
                tokenizer.CountTokens(message.Role.ToString().ToLowerInvariant()) +
                tokenizer.CountTokens(message.Content));
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
        AgentInvocationDraft draft;
        try
        {
            var instructions = WorkspaceInstructionDocument.Read(_paths);
            draft = _factory.Create(
                context,
                Guid.CreateVersion7(_timeProvider.GetUtcNow()),
                instructions);
            await sink.EmitAsync(
                new RecordAgentInvocationSnapshotIntent(draft.Snapshot),
                cancellationToken);
            if (draft.Disposition == AgentInvocationDraftDisposition.CompactionRequired)
            {
                await sink.EmitAsync(
                    new FailTurnIntent(new SessionError(
                        AgentErrorCodes.ContextCompactionFailed,
                        "Conversation compaction is required.",
                        IsRetryable: false)),
                    cancellationToken);
                return;
            }
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
            for (var attempt = 1; attempt <= 3; attempt++)
            {
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
                        ChatCompletionInvocationPurpose.Response);
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
                    if (!visible && exception.IsTransient && attempt < 3)
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
                            invocationToken);
                        continue;
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
