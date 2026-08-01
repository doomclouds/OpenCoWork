using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text.Json;
using OpenCoWork.Abstractions;

namespace OpenCoWork.Core.Configuration;

[ConfigSection("gateway")]
public sealed record GatewayConfig : IValidatableObject
{
    [Range(1, 65_535)]
    public int ListenPort { get; init; } = 9_200;

    public GatewayChannelConfig[] Channels { get; init; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Channels is null)
        {
            yield return new ValidationResult(
                "Channels cannot be null.",
                [nameof(Channels)]);
            yield break;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var channel in Channels)
        {
            if (channel is null)
            {
                yield return new ValidationResult(
                    "Channel entries cannot be null.",
                    [nameof(Channels)]);
                continue;
            }

            foreach (var result in channel.Validate(validationContext))
            {
                yield return result;
            }

            if (!ids.Add(channel.Id))
            {
                yield return new ValidationResult(
                    $"Channel ID '{channel.Id}' is duplicated.",
                    [nameof(Channels)]);
            }
        }
    }

    internal static string ComputeChannelSha256(GatewayChannelConfig channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("callbackUrl", channel.CallbackUrl);
            writer.WriteStartObject("credential");
            writer.WriteString(
                "environmentVariable",
                channel.Credential.EnvironmentVariable);
            writer.WriteString(
                "source",
                JsonNamingPolicy.CamelCase.ConvertName(
                    channel.Credential.Source.ToString()));
            writer.WriteEndObject();
            writer.WriteBoolean("enabled", channel.Enabled);
            writer.WriteString("id", channel.Id);
            writer.WriteString("kind", channel.Kind);
            writer.WriteNumber("maxConcurrentSends", channel.MaxConcurrentSends);
            writer.WriteNumber(
                "minimumSendIntervalMs",
                channel.MinimumSendIntervalMs);
            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray()))
            .ToLowerInvariant();
    }
}

public sealed record GatewayChannelConfig : IValidatableObject
{
    [Required]
    [StringLength(64, MinimumLength = 1)]
    [RegularExpression("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    public string Id { get; init; } = string.Empty;

    [Required]
    [RegularExpression("^webhook$")]
    public string Kind { get; init; } = "webhook";

    public bool Enabled { get; init; } = true;

    [Required]
    public string CallbackUrl { get; init; } = string.Empty;

    [Required]
    public GatewayCredentialConfig Credential { get; init; } = new();

    [Range(1, 16)]
    public int MaxConcurrentSends { get; init; } = 4;

    [Range(0, 60_000)]
    public int MinimumSendIntervalMs { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Uri.TryCreate(CallbackUrl, UriKind.Absolute, out var callback) ||
            callback.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(callback.Host) ||
            !string.IsNullOrEmpty(callback.UserInfo))
        {
            yield return new ValidationResult(
                "Channel CallbackUrl must be an absolute HTTPS URL without user info.",
                [nameof(CallbackUrl)]);
        }

        if (Credential is null)
        {
            yield return new ValidationResult(
                "Channel Credential cannot be null.",
                [nameof(Credential)]);
            yield break;
        }

        foreach (var result in Credential.Validate(validationContext))
        {
            yield return result;
        }
    }
}

public enum GatewayCredentialSource
{
    Environment,
    OsSecretStore,
}

public sealed record GatewayCredentialConfig : IValidatableObject
{
    public GatewayCredentialSource Source { get; init; } =
        GatewayCredentialSource.OsSecretStore;

    public string? EnvironmentVariable { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Source == GatewayCredentialSource.Environment &&
            (string.IsNullOrWhiteSpace(EnvironmentVariable) ||
             EnvironmentVariable.Any(character =>
                 character is not (>= 'A' and <= 'Z' or >= '0' and <= '9' or '_'))))
        {
            yield return new ValidationResult(
                "Channel environment credentials require an uppercase variable name.",
                [nameof(EnvironmentVariable)]);
        }

        if (Source == GatewayCredentialSource.OsSecretStore &&
            EnvironmentVariable is not null)
        {
            yield return new ValidationResult(
                "Channel OS secret store credentials cannot name an environment variable.",
                [nameof(EnvironmentVariable)]);
        }
    }
}
