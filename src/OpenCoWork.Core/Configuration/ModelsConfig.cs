using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Agents;

namespace OpenCoWork.Core.Configuration;

[ConfigSection("models")]
public sealed record ModelsConfig : IValidatableObject
{
    public const string ProviderId = "deepseek";
    public const string FlashModelId = "deepseek-v4-flash";
    public const string AuthProfileId = "auth/deepseek";
    public const string ApiKeyEnvironmentVariable = "DEEPSEEK_API_KEY";
    public const string BaseUrl = "https://api.deepseek.com";

    [Required]
    [RegularExpression("^deepseek-v4-flash$")]
    public string DefaultModel { get; init; } = FlashModelId;

    [Required]
    [RegularExpression("^(low|high|max)$")]
    public string ReasoningEffort { get; init; } = "high";

    internal string DefaultProvider { get; init; } = ProviderId;

    internal Dictionary<string, ProviderConfig> Providers { get; init; } =
        BuiltInProviders();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!string.Equals(DefaultModel, FlashModelId, StringComparison.Ordinal))
        {
            yield return Invalid(
                $"Default model must be '{FlashModelId}'.",
                nameof(DefaultModel));
        }

        if (ReasoningEffort is not ("low" or "high" or "max"))
        {
            yield return Invalid(
                "Reasoning effort must be 'low', 'high', or 'max'.",
                nameof(ReasoningEffort));
        }

        if (!string.Equals(DefaultProvider, ProviderId, StringComparison.Ordinal) ||
            !Providers.TryGetValue(ProviderId, out var provider) ||
            Providers.Count != 1 ||
            !provider.Models.ContainsKey(FlashModelId) ||
            provider.Models.Count != 1)
        {
            yield return Invalid(
                "Only the built-in DeepSeek Flash provider is supported.",
                nameof(DefaultModel));
        }
    }

    private static Dictionary<string, ProviderConfig> BuiltInProviders()
    {
        var profile = TokenizerProfiles.GetRequiredForModel(FlashModelId);
        return new Dictionary<string, ProviderConfig>(StringComparer.Ordinal)
        {
            [ProviderId] = new()
            {
                BaseUrl = BaseUrl,
                ApiKey = new ProviderApiKeyConfig
                {
                    Environment = ApiKeyEnvironmentVariable,
                },
                Models = new Dictionary<string, ModelConfig>(StringComparer.Ordinal)
                {
                    [FlashModelId] = new()
                    {
                        TokenizerProfileId = profile.Id,
                        TokenizerProfileVersion = profile.Version,
                        ContextWindowTokens = profile.ContextWindowTokens,
                        MaxOutputTokens = profile.MaxOutputTokens,
                    },
                },
            },
        };
    }

    private static ValidationResult Invalid(string message, string member) =>
        new(message, [member]);
}

internal sealed record ProviderConfig
{
    [Required]
    public string BaseUrl { get; init; } = string.Empty;

    [Required]
    public ProviderApiKeyConfig ApiKey { get; init; } = new();

    [Required]
    public Dictionary<string, ModelConfig> Models { get; init; } =
        new(StringComparer.Ordinal);

    internal IEnumerable<ValidationResult> Validate(string providerId)
    {
        var path = $"Providers.{providerId}";
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            (uri.Scheme != Uri.UriSchemeHttps &&
             (uri.Scheme != Uri.UriSchemeHttp || !uri.IsLoopback)))
        {
            yield return new ValidationResult(
                "Provider Base URL must be absolute HTTPS without user info, query, or fragment; HTTP is allowed only for loopback.",
                [$"{path}.BaseUrl"]);
        }

        if (Models.Count == 0)
        {
            yield return new ValidationResult(
                "A provider must configure at least one exact model ID.",
                [$"{path}.Models"]);
        }

        foreach (var (modelId, model) in Models.OrderBy(
                     pair => pair.Key,
                     StringComparer.Ordinal))
        {
            var modelPath = $"{path}.Models.{modelId}";
            if (string.IsNullOrWhiteSpace(modelId) ||
                !string.Equals(modelId, modelId.Trim(), StringComparison.Ordinal))
            {
                yield return new ValidationResult(
                    "Model IDs must be non-empty and must not contain outer whitespace.",
                    [modelPath]);
            }

            foreach (var failure in model.Validate(modelId, modelPath))
            {
                yield return failure;
            }
        }
    }
}

internal sealed record ProviderApiKeyConfig
{
    [Required]
    [RegularExpression("^[A-Za-z_][A-Za-z0-9_]*$")]
    public string Environment { get; init; } = string.Empty;
}

internal sealed record ModelConfig
{
    [Required]
    public string TokenizerProfileId { get; init; } = string.Empty;

    [Required]
    public string TokenizerProfileVersion { get; init; } = string.Empty;

    [Range(1, int.MaxValue)]
    public int ContextWindowTokens { get; init; }

    [Range(1, int.MaxValue)]
    public int MaxOutputTokens { get; init; }

    internal IEnumerable<ValidationResult> Validate(string modelId, string path)
    {
        if (MaxOutputTokens > ContextWindowTokens)
        {
            yield return new ValidationResult(
                "Maximum output tokens cannot exceed the context window.",
                [$"{path}.MaxOutputTokens"]);
        }

        if (TokenizerProfiles.TryGetForModel(modelId, out var builtIn))
        {
            var profile = builtIn!;
            if (!string.Equals(
                    TokenizerProfileId,
                    profile.Id,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    TokenizerProfileVersion,
                    profile.Version,
                    StringComparison.Ordinal) ||
                ContextWindowTokens != profile.ContextWindowTokens ||
                MaxOutputTokens != profile.MaxOutputTokens)
            {
                yield return new ValidationResult(
                    "Built-in model limits and Tokenizer Profile must match the frozen profile exactly.",
                    [$"{path}.TokenizerProfileId"]);
            }

        }
    }
}

internal sealed class FrozenProviderCredentials
{
    private readonly IReadOnlyDictionary<string, string> _values;

    private FrozenProviderCredentials(Dictionary<string, string> values)
    {
        _values = new ReadOnlyDictionary<string, string>(values);
    }

    public static FrozenProviderCredentials Capture(ModelsConfig models) =>
        Capture(models, Environment.GetEnvironmentVariable);

    internal static FrozenProviderCredentials Capture(
        ModelsConfig models,
        Func<string, string?> readEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(readEnvironmentVariable);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (providerId, provider) in models.Providers)
        {
            var value = readEnvironmentVariable(provider.ApiKey.Environment);
            if (!string.IsNullOrWhiteSpace(value))
            {
                values.Add(providerId, value);
            }
        }

        return new FrozenProviderCredentials(values);
    }

    public string GetRequired(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        return _values.TryGetValue(providerId, out var value)
            ? value
            : throw new InvalidOperationException(
                $"API key environment variable for provider '{providerId}' is missing or empty.");
    }

    internal IEnumerable<string> GetSecretValues() => _values.Values;
}

internal static class ModelSelectionPreflight
{
    public static ModelTokenizer Validate(
        ModelsConfig models,
        string providerId,
        string modelId,
        string bundledTokenizerBaseDirectory,
        string customTokenizerBaseDirectory) =>
        ValidateCore(
            models,
            providerId,
            modelId,
            bundledTokenizerBaseDirectory,
            customTokenizerBaseDirectory);

    public static ModelTokenizer Validate(
        ModelsConfig models,
        FrozenProviderCredentials credentials,
        string providerId,
        string modelId,
        string bundledTokenizerBaseDirectory,
        string customTokenizerBaseDirectory)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        _ = credentials.GetRequired(providerId);
        return ValidateCore(
            models,
            providerId,
            modelId,
            bundledTokenizerBaseDirectory,
            customTokenizerBaseDirectory);
    }

    private static ModelTokenizer ValidateCore(
        ModelsConfig models,
        string providerId,
        string modelId,
        string bundledTokenizerBaseDirectory,
        string customTokenizerBaseDirectory)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(bundledTokenizerBaseDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(customTokenizerBaseDirectory);
        if (!string.Equals(providerId, ModelsConfig.ProviderId, StringComparison.Ordinal) ||
            !string.Equals(modelId, ModelsConfig.FlashModelId, StringComparison.Ordinal) ||
            !models.Providers.TryGetValue(ModelsConfig.ProviderId, out var provider) ||
            !provider.Models.TryGetValue(ModelsConfig.FlashModelId, out var model))
        {
            throw new InvalidOperationException(
                $"Provider/model selection '{providerId}/{modelId}' is not configured.");
        }

        if (model.Validate(modelId, "model").Any())
        {
            throw new InvalidOperationException(
                $"Provider/model selection '{providerId}/{modelId}' does not match its Tokenizer Profile.");
        }

        return TokenizerProfiles.GetRequiredForModel(ModelsConfig.FlashModelId)
            .CreateTokenizer(bundledTokenizerBaseDirectory);
    }
}
