using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Tiktoken;
using Tiktoken.Encodings;
using Rune = System.Text.Rune;

namespace OpenCoWork.Core.Agents;

internal sealed record TokenizerProfile(
    string Id,
    string Version,
    IReadOnlyList<string> ModelIds,
    string? BuiltInEncoding,
    string? AssetFileName,
    string? AssetSha256,
    string Source,
    string ChatTemplateId,
    string ChatTemplateVersion,
    int ContextWindowTokens,
    int MaxOutputTokens)
{
    public ModelTokenizer CreateTokenizer(string baseDirectory) =>
        TokenizerProfiles.CreateTokenizer(this, baseDirectory);
}

internal sealed class ModelTokenizer(Encoder encoder)
{
    public int CountTokens(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return encoder.CountTokens(text);
    }

    public int[] Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return encoder.Encode(text).ToArray();
    }
}

internal static class TokenizerProfiles
{
    private const int MaximumAssetBytes = 32 * 1024 * 1024;
    private const string DeepSeekSha256 =
        "8f9f37ca37fdc4f5fd36d5cf4d3b0e8392edb4e894fd10cc0d70b4957c8633cf";
    private const string GlmSha256 =
        "19e773648cb4e65de8660ea6365e10acca112d42a854923df93db4a6f333a82d";

    public static IReadOnlyList<TokenizerProfile> BuiltIn { get; } =
        Array.AsReadOnly<TokenizerProfile>(
        [
            new(
                "qwen-o200k",
                "1",
                ["qwen3.8-max-preview"],
                "o200k_base",
                AssetFileName: null,
                AssetSha256: null,
                "QwenCloud token-counting guidance, retrieved 2026-07-27",
                "qwen3.8-chat",
                "1",
                ContextWindowTokens: 983_616,
                MaxOutputTokens: 131_072),
            new(
                "glm-5.2",
                "1",
                ["glm-5.2"],
                BuiltInEncoding: null,
                "glm-5.2.tokenizer.json.gz",
                GlmSha256,
                "zai-org/GLM-5.2@b4734de4facf877f85769a911abafc5283eab3d9",
                "glm-5.2-chat",
                "1",
                ContextWindowTokens: 1_048_576,
                MaxOutputTokens: 131_072),
            new(
                "deepseek-v4-pro",
                "1",
                ["deepseek-v4-pro"],
                BuiltInEncoding: null,
                "deepseek-v4.tokenizer.json.gz",
                DeepSeekSha256,
                "deepseek-ai/DeepSeek-V4-Pro@b5968e9190ef611bbf34a7229255be88a0e937c1",
                "deepseek-v4-chat",
                "1",
                ContextWindowTokens: 1_048_576,
                MaxOutputTokens: 393_216),
            new(
                "deepseek-v4-flash",
                "1",
                ["deepseek-v4-flash"],
                BuiltInEncoding: null,
                "deepseek-v4.tokenizer.json.gz",
                DeepSeekSha256,
                "deepseek-ai/DeepSeek-V4-Flash@60d8d70770c6776ff598c94bb586a859a38244f1",
                "deepseek-v4-chat",
                "1",
                ContextWindowTokens: 1_048_576,
                MaxOutputTokens: 393_216),
        ]);

    public static TokenizerProfile GetRequiredForModel(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        return TryGetForModel(modelId, out var profile)
            ? profile!
            : throw new InvalidOperationException(
                $"No tokenizer profile is registered for model '{modelId}'.");
    }

    internal static bool TryGetForModel(
        string modelId,
        out TokenizerProfile? profile)
    {
        profile = BuiltIn.SingleOrDefault(candidate =>
            candidate.ModelIds.Contains(modelId, StringComparer.Ordinal));
        return profile is not null;
    }

    internal static ModelTokenizer CreateTokenizer(
        TokenizerProfile profile,
        string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        if (profile.BuiltInEncoding == "o200k_base")
        {
            return new ModelTokenizer(new Encoder(new O200KBase()));
        }

        var assetPath = Path.Combine(
            baseDirectory,
            "tokenizers",
            profile.AssetFileName
                ?? throw new InvalidOperationException(
                    $"Tokenizer profile '{profile.Id}' has no asset."));
        using var file = File.OpenRead(assetPath);
        using var compressed = new GZipStream(file, CompressionMode.Decompress);
        return CreateFromJson(
            compressed,
            profile.Id,
            profile.AssetSha256
                ?? throw new InvalidOperationException(
                    $"Tokenizer profile '{profile.Id}' has no asset SHA-256."));
    }

    internal static ModelTokenizer CreateCustomTokenizer(
        string profileId,
        string tokenizerPath,
        string expectedSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenizerPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        using var file = File.OpenRead(tokenizerPath);
        return CreateFromJson(file, profileId, expectedSha256);
    }

    private static ModelTokenizer CreateFromJson(
        Stream source,
        string profileId,
        string expectedSha256)
    {
        using var json = new MemoryStream();
        var buffer = new byte[81_920];
        while (true)
        {
            var read = source.Read(buffer);
            if (read == 0)
            {
                break;
            }

            if (json.Length + read > MaximumAssetBytes)
            {
                throw new InvalidDataException(
                    $"Tokenizer asset for profile '{profileId}' exceeds the size limit.");
            }

            json.Write(buffer, 0, read);
        }

        var actualSha256 = Convert.ToHexString(
                SHA256.HashData(json.GetBuffer().AsSpan(0, checked((int)json.Length))))
            .ToLowerInvariant();
        if (!string.Equals(
                actualSha256,
                expectedSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Tokenizer asset for profile '{profileId}' failed integrity validation.");
        }

        json.Position = 0;
        return new ModelTokenizer(
            new Encoder(TokenizerJsonEncodingLoader.FromStream(json, profileId)));
    }

    private static class TokenizerJsonEncodingLoader
    {
        private static readonly IReadOnlyDictionary<Rune, byte> ByteDecoder =
            CreateByteDecoder();

        public static Encoding FromStream(Stream stream, string name)
        {
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            var specialTokens = root.GetProperty("added_tokens")
                .EnumerateArray()
                .Where(token =>
                    token.TryGetProperty("special", out var special)
                    && special.GetBoolean())
                .ToDictionary(
                    token => token.GetProperty("content").GetString()!,
                    token => token.GetProperty("id").GetInt32(),
                    StringComparer.Ordinal);
            var mergeableRanks = new Dictionary<byte[], int>(ByteSequenceComparer.Instance);
            foreach (var token in root
                         .GetProperty("model")
                         .GetProperty("vocab")
                         .EnumerateObject())
            {
                if (!specialTokens.ContainsKey(token.Name))
                {
                    mergeableRanks.Add(
                        DecodeByteLevel(token.Name),
                        token.Value.GetInt32());
                }
            }

            var patterns = new List<string>();
            AddSplitPatterns(root.GetProperty("pre_tokenizer"), patterns);
            return new Encoding(name, patterns, mergeableRanks, specialTokens);
        }

        private static void AddSplitPatterns(
            JsonElement preTokenizer,
            ICollection<string> patterns)
        {
            var type = preTokenizer.GetProperty("type").GetString();
            if (type == "Split"
                && preTokenizer.GetProperty("pattern")
                    .TryGetProperty("Regex", out var regex))
            {
                patterns.Add(regex.GetString()!);
            }

            if (type == "Sequence")
            {
                foreach (var child in preTokenizer
                             .GetProperty("pretokenizers")
                             .EnumerateArray())
                {
                    AddSplitPatterns(child, patterns);
                }
            }
        }

        private static byte[] DecodeByteLevel(string token)
        {
            var bytes = new byte[token.EnumerateRunes().Count()];
            var index = 0;
            foreach (var rune in token.EnumerateRunes())
            {
                if (!ByteDecoder.TryGetValue(rune, out bytes[index++]))
                {
                    throw new InvalidDataException(
                        "Tokenizer vocabulary contains an unsupported byte-level token.");
                }
            }

            return bytes;
        }

        private static IReadOnlyDictionary<Rune, byte> CreateByteDecoder()
        {
            var direct = Enumerable.Range(33, 94)
                .Concat(Enumerable.Range(161, 12))
                .Concat(Enumerable.Range(174, 82))
                .ToHashSet();
            var result = new Dictionary<Rune, byte>(256);
            var next = 256;
            for (var value = 0; value < 256; value++)
            {
                result.Add(
                    new Rune(direct.Contains(value) ? value : next++),
                    checked((byte)value));
            }

            return result;
        }

        private sealed class ByteSequenceComparer : IEqualityComparer<byte[]>
        {
            public static ByteSequenceComparer Instance { get; } = new();

            public bool Equals(byte[]? x, byte[]? y) =>
                ReferenceEquals(x, y)
                || x is not null && y is not null && x.AsSpan().SequenceEqual(y);

            public int GetHashCode(byte[] value)
            {
                var hash = new HashCode();
                hash.AddBytes(value);
                return hash.ToHashCode();
            }
        }
    }
}
