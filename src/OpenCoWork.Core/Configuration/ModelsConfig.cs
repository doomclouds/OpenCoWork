using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Agents;

namespace OpenCoWork.Core.Configuration;

[ConfigSection("models")]
public sealed record ModelsConfig : IValidatableObject
{
    [Required]
    public string DefaultProvider { get; init; } = string.Empty;

    [Required]
    public string DefaultModel { get; init; } = string.Empty;

    [Required]
    public Dictionary<string, ProviderConfig> Providers { get; init; } =
        new(StringComparer.Ordinal);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Providers.TryGetValue(DefaultProvider, out var provider))
        {
            yield return Invalid(
                "Default provider must reference an exact configured provider ID.",
                nameof(DefaultProvider));
        }
        else if (!provider.Models.ContainsKey(DefaultModel))
        {
            yield return Invalid(
                "Default model must reference an exact model ID on the default provider.",
                nameof(DefaultModel));
        }

        foreach (var (providerId, candidate) in Providers.OrderBy(
                     pair => pair.Key,
                     StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(providerId) ||
                !string.Equals(providerId, providerId.Trim(), StringComparison.Ordinal))
            {
                yield return Invalid(
                    "Provider IDs must be non-empty and must not contain outer whitespace.",
                    $"Providers.{providerId}");
            }

            foreach (var failure in candidate.Validate(providerId))
            {
                yield return failure;
            }
        }
    }

    private static ValidationResult Invalid(string message, string member) =>
        new(message, [member]);
}

public sealed record ProviderConfig
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

public sealed record ProviderApiKeyConfig
{
    [Required]
    [RegularExpression("^[A-Za-z_][A-Za-z0-9_]*$")]
    public string Environment { get; init; } = string.Empty;
}

public sealed record ModelConfig
{
    [Required]
    public string TokenizerProfileId { get; init; } = string.Empty;

    [Required]
    public string TokenizerProfileVersion { get; init; } = string.Empty;

    [Range(1, int.MaxValue)]
    public int ContextWindowTokens { get; init; }

    [Range(1, int.MaxValue)]
    public int MaxOutputTokens { get; init; }

    public string? TokenizerPath { get; init; }

    [RegularExpression("^[0-9a-f]{64}$")]
    public string? TokenizerSha256 { get; init; }

    internal IEnumerable<ValidationResult> Validate(string modelId, string path)
    {
        if (MaxOutputTokens > ContextWindowTokens)
        {
            yield return new ValidationResult(
                "Maximum output tokens cannot exceed the context window.",
                [$"{path}.MaxOutputTokens"]);
        }

        if (string.IsNullOrWhiteSpace(TokenizerPath) !=
            string.IsNullOrWhiteSpace(TokenizerSha256))
        {
            yield return new ValidationResult(
                "Custom tokenizer path and SHA-256 must be configured together.",
                [$"{path}.TokenizerPath", $"{path}.TokenizerSha256"]);
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

            if (!string.IsNullOrWhiteSpace(TokenizerPath) ||
                !string.IsNullOrWhiteSpace(TokenizerSha256))
            {
                yield return new ValidationResult(
                    "Built-in models cannot override their Tokenizer Profile asset.",
                    [$"{path}.TokenizerPath", $"{path}.TokenizerSha256"]);
            }
        }
        else if (string.IsNullOrWhiteSpace(TokenizerPath) ||
                 string.IsNullOrWhiteSpace(TokenizerSha256))
        {
            yield return new ValidationResult(
                "Custom models require a local tokenizer path and SHA-256.",
                [$"{path}.TokenizerPath", $"{path}.TokenizerSha256"]);
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
        FrozenProviderCredentials credentials,
        string providerId,
        string modelId,
        string bundledTokenizerBaseDirectory,
        string customTokenizerBaseDirectory)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(bundledTokenizerBaseDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(customTokenizerBaseDirectory);
        if (!models.Providers.TryGetValue(providerId, out var provider) ||
            !provider.Models.TryGetValue(modelId, out var model))
        {
            throw new InvalidOperationException(
                $"Provider/model selection '{providerId}/{modelId}' is not configured.");
        }

        if (model.Validate(modelId, "model").Any())
        {
            throw new InvalidOperationException(
                $"Provider/model selection '{providerId}/{modelId}' does not match its Tokenizer Profile.");
        }

        _ = credentials.GetRequired(providerId);
        if (TokenizerProfiles.TryGetForModel(modelId, out var builtIn))
        {
            return builtIn!.CreateTokenizer(bundledTokenizerBaseDirectory);
        }

        var tokenizerPath = model.TokenizerPath!;
        var resolvedPath = Path.IsPathFullyQualified(tokenizerPath)
            ? Path.GetFullPath(tokenizerPath)
            : Path.GetFullPath(tokenizerPath, customTokenizerBaseDirectory);
        return TokenizerProfiles.CreateCustomTokenizer(
            model.TokenizerProfileId,
            resolvedPath,
            model.TokenizerSha256!);
    }
}
