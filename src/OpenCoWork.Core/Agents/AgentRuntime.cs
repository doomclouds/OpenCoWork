using System.Security.Cryptography;
using System.Text;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Workspaces;

namespace OpenCoWork.Core.Agents;

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
