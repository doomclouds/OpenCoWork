using System.IO.Compression;
using System.Security.Cryptography;
using OpenCoWork.Core.Agents;
using OpenCoWork.Core.Configuration;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class AgentFactoryTests
{
    private static string TokenizerBaseDirectory =>
        Environment.GetEnvironmentVariable("OPENCOWORK_TEST_TOKENIZER_BASE_DIRECTORY")
        ?? AppContext.BaseDirectory;

    public static TheoryData<string, string, int[]> TokenizerCorpus => new()
    {
        {
            "qwen3.8-max-preview",
            "你好，小陌。",
            [177519, 137380, 130887, 788]
        },
        {
            "deepseek-v4-pro",
            "public static int Add(int a, int b) => a + b;",
            [3978, 4911, 688, 7043, 5047, 260, 14, 688, 291, 11, 2705, 260, 940, 291, 29]
        },
        {
            "deepseek-v4-flash",
            "reasoning: verify -> execute -> persist",
            [86512, 288, 28, 23393, 6248, 22218, 6248, 37746]
        },
        {
            "glm-5.2",
            "Hello, OpenCoWork!",
            [9703, 11, 5264, 7339, 6776, 0]
        },
    };

    [Theory]
    [MemberData(nameof(TokenizerCorpus))]
    public void Built_in_tokenizer_profiles_match_reference_token_ids(
        string modelId,
        string text,
        int[] expected)
    {
        var profile = TokenizerProfiles.GetRequiredForModel(modelId);
        var tokenizer = profile.CreateTokenizer(TokenizerBaseDirectory);

        Assert.Equal(expected, tokenizer.Encode(text));
        Assert.Equal(expected.Length, tokenizer.CountTokens(text));
    }

    [Fact]
    public void Built_in_profiles_are_exact_versioned_and_cover_only_the_frozen_models()
    {
        Assert.Equal(
            [
                "deepseek-v4-flash",
                "deepseek-v4-pro",
                "glm-5.2",
                "qwen3.8-max-preview",
            ],
            TokenizerProfiles.BuiltIn
                .SelectMany(profile => profile.ModelIds)
                .Order(StringComparer.Ordinal));
        Assert.All(
            TokenizerProfiles.BuiltIn,
            profile =>
            {
                Assert.NotEmpty(profile.Id);
                Assert.NotEmpty(profile.Version);
                Assert.NotEmpty(profile.ChatTemplateId);
                Assert.NotEmpty(profile.ChatTemplateVersion);
            });
    }

    [Fact]
    public void Custom_tokenizer_is_local_sha_pinned_and_uses_the_same_tiktoken_engine()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-tokenizer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var tokenizerPath = Path.Combine(directory, "tokenizer.json");
            using (var source = File.OpenRead(Path.Combine(
                       TokenizerBaseDirectory,
                       "tokenizers",
                       "glm-5.2.tokenizer.json.gz")))
            using (var compressed = new GZipStream(source, CompressionMode.Decompress))
            using (var target = File.Create(tokenizerPath))
            {
                compressed.CopyTo(target);
            }

            var sha256 = Convert.ToHexString(
                    SHA256.HashData(File.ReadAllBytes(tokenizerPath)))
                .ToLowerInvariant();
            var models = new ModelsConfig
            {
                Providers = new Dictionary<string, ProviderConfig>(StringComparer.Ordinal)
                {
                    ["custom"] = new()
                    {
                        BaseUrl = "https://example.test/v1",
                        ApiKey = new ProviderApiKeyConfig
                        {
                            Environment = "CUSTOM_KEY",
                        },
                        Models = new Dictionary<string, ModelConfig>(StringComparer.Ordinal)
                        {
                            ["custom-model"] = new()
                            {
                                TokenizerProfileId = "custom-profile",
                                TokenizerProfileVersion = "1",
                                ContextWindowTokens = 1_048_576,
                                MaxOutputTokens = 131_072,
                                TokenizerPath = "tokenizer.json",
                                TokenizerSha256 = sha256,
                            },
                        },
                    },
                },
            };
            var credentials = FrozenProviderCredentials.Capture(
                models,
                name => name == "CUSTOM_KEY" ? "secret" : null);

            var tokenizer = ModelSelectionPreflight.Validate(
                models,
                credentials,
                "custom",
                "custom-model",
                TokenizerBaseDirectory,
                directory);

            Assert.Equal(
                [9703, 11, 5264, 7339, 6776, 0],
                tokenizer.Encode("Hello, OpenCoWork!"));

            models.Providers["custom"].Models["custom-model"] =
                models.Providers["custom"].Models["custom-model"] with
                {
                    TokenizerSha256 = new string('0', 64),
                };
            Assert.Throws<InvalidDataException>(() =>
                ModelSelectionPreflight.Validate(
                    models,
                    credentials,
                    "custom",
                    "custom-model",
                    TokenizerBaseDirectory,
                    directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
