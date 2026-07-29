using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using OpenCoWork.Abstractions;
using OpenCoWork.App;
using OpenCoWork.Core.Agents;
using OpenCoWork.Core.Workspaces;
using Xunit;

namespace OpenCoWork.IntegrationTests;

public sealed class ProviderReleaseValidationTests(ITestOutputHelper output)
{
    private const string CommitVariable =
        "OPENCOWORK_RELEASE_COMMIT_SHA";
    private static readonly ProviderCase[] Paths =
    [
        new(
            "deepseek-official",
            "deepseek-v4-pro",
            "OPENCOWORK_RELEASE_DEEPSEEK_BASE_URL",
            "OPENCOWORK_RELEASE_DEEPSEEK_API_KEY"),
        new(
            "deepseek-official",
            "deepseek-v4-flash",
            "OPENCOWORK_RELEASE_DEEPSEEK_BASE_URL",
            "OPENCOWORK_RELEASE_DEEPSEEK_API_KEY"),
    ];

    [Fact(Explicit = true)]
    public async Task Real_provider_matrix_completes_the_full_runtime_without_secret_leaks()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var commitSha = Environment.GetEnvironmentVariable(CommitVariable);
        var results = new List<ProviderReleaseEvidence>();
        var secrets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Paths)
        {
            var baseUrl =
                Environment.GetEnvironmentVariable(path.BaseUrlVariable);
            var apiKey =
                Environment.GetEnvironmentVariable(path.ApiKeyVariable);
            if (!string.IsNullOrEmpty(apiKey))
            {
                secrets.Add(apiKey);
            }

            if (!IsCommitSha(commitSha) ||
                !TryGetBaseUri(baseUrl, out var baseUri) ||
                string.IsNullOrWhiteSpace(apiKey))
            {
                results.Add(Evidence(path, commitSha, ReleaseStatus.NotRun));
                continue;
            }

            results.Add(await RunAsync(
                path,
                commitSha!,
                baseUri!,
                apiKey,
                cancellationToken));
        }

        var evidence = JsonSerializer.Serialize(
            results,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Converters =
                {
                    new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
                },
                WriteIndented = true,
            });
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, evidence, StringComparison.Ordinal);
        }

        output.WriteLine(evidence);
        var incomplete = results
            .Where(result => result.Status != ReleaseStatus.Pass)
            .Select(result =>
                $"{result.ProviderPath}/{result.ModelId}:{result.Status}")
            .ToArray();
        Assert.True(
            incomplete.Length == 0,
            "Provider release matrix is incomplete: " +
            string.Join(", ", incomplete));
    }

    private static async Task<ProviderReleaseEvidence> RunAsync(
        ProviderCase path,
        string commitSha,
        Uri baseUri,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var root = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-provider-release-{Guid.NewGuid():N}");
        var userProfile = Path.Combine(root, "user");
        var result = Evidence(
            path,
            commitSha,
            ReleaseStatus.Fail,
            timestamp);
        var cleanupFailed = false;
        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(userProfile);
            var paths = new OpenCoWorkPaths(root);
            await WorkspaceInitializer.InitializeAsync(
                paths,
                TimeSpan.FromSeconds(5),
                cancellationToken);
            await File.WriteAllTextAsync(
                paths.ConfigPath,
                Config(path, baseUri),
                cancellationToken);

            var probe = new ProviderProbe();
            var standardOutput = new StringWriter();
            var standardError = new StringWriter();
            var exitCode = await OpenCoWorkCli.RunAsync(
                ["chat", "--workspace", root],
                new StringReader("Reply with exactly OK.\n"),
                standardOutput,
                standardError,
                root,
                userProfile,
                isInteractive: false,
                services => services.AddSingleton<ISessionExecutor>(
                    serviceProvider =>
                    {
                        var timeProvider =
                            serviceProvider.GetService<TimeProvider>();
                        return new AgentRuntimeExecutor(
                            serviceProvider.GetRequiredService<AgentFactory>(),
                            serviceProvider.GetRequiredService<OpenCoWorkPaths>(),
                            registration => new AuditedChatClient(
                                new OpenAiCompatibleChatClient(
                                    serviceProvider.GetRequiredService<HttpClient>(),
                                    registration.BaseUri,
                                    registration.LegacyApiKey!,
                                    timeProvider),
                                registration.Tokenizer,
                                probe),
                            timeProvider);
                    }),
                cancellationToken);

            var reasoningCommitted =
                !path.RequiresReasoningRepresentative ||
                !probe.HasReasoning ||
                probe.ReasoningDeltas.All(delta =>
                    standardError.ToString().Contains(
                        delta,
                        StringComparison.Ordinal));
            var secretFound =
                standardOutput.ToString().Contains(apiKey, StringComparison.Ordinal) ||
                standardError.ToString().Contains(apiKey, StringComparison.Ordinal) ||
                DirectoryContains(root, Encoding.UTF8.GetBytes(apiKey));
            if (exitCode == 0 &&
                probe.RequestCount == 1 &&
                probe.Completed &&
                probe.HasContent &&
                !string.IsNullOrWhiteSpace(standardOutput.ToString()) &&
                probe.Usage is
                { PromptTokens: > 0, CompletionTokens: > 0, TotalTokens: > 0 } usage &&
                probe.UsageCount == 1 &&
                probe.FinishReason is not null and
                    not ChatCompletionFinishReason.Unknown &&
                UsageMatches(probe.LocalPromptTokens, usage.PromptTokens) &&
                reasoningCommitted &&
                !secretFound)
            {
                result = Evidence(
                    path,
                    commitSha,
                    ReleaseStatus.Pass,
                    timestamp,
                    usage,
                    probe.FinishReason);
            }
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            result = Evidence(
                path,
                commitSha,
                ReleaseStatus.Fail,
                timestamp);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch
                {
                    cleanupFailed = true;
                }
            }
        }

        return cleanupFailed
            ? Evidence(path, commitSha, ReleaseStatus.Fail, timestamp)
            : result;
    }

    private static string Config(ProviderCase path, Uri baseUri)
    {
        var profile = TokenizerProfiles.GetRequiredForModel(path.ModelId);
        return JsonSerializer.Serialize(
            new
            {
                models = new
                {
                    defaultProvider = path.ProviderPath,
                    defaultModel = path.ModelId,
                    providers = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        [path.ProviderPath] = new
                        {
                            baseUrl = baseUri.AbsoluteUri.TrimEnd('/'),
                            apiKey = new
                            {
                                environment = path.ApiKeyVariable,
                            },
                            models = new Dictionary<string, object>(StringComparer.Ordinal)
                            {
                                [path.ModelId] = new
                                {
                                    tokenizerProfileId = profile.Id,
                                    tokenizerProfileVersion = profile.Version,
                                    contextWindowTokens = profile.ContextWindowTokens,
                                    maxOutputTokens = profile.MaxOutputTokens,
                                },
                            },
                        },
                    },
                },
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true,
            });
    }

    private static bool DirectoryContains(string root, byte[] value)
    {
        foreach (var file in Directory.EnumerateFiles(
                     root,
                     "*",
                     SearchOption.AllDirectories))
        {
            if (File.ReadAllBytes(file).AsSpan().IndexOf(value) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool UsageMatches(
        int localPromptTokens,
        int providerPromptTokens)
    {
        var maximumOverestimate = Math.Max(
            32,
            (int)Math.Ceiling(providerPromptTokens * 0.005));
        return localPromptTokens >= providerPromptTokens &&
               localPromptTokens - providerPromptTokens <= maximumOverestimate;
    }

    private static ProviderReleaseEvidence Evidence(
        ProviderCase path,
        string? commitSha,
        ReleaseStatus status,
        DateTimeOffset? timestamp = null,
        ChatCompletionUsage? usage = null,
        ChatCompletionFinishReason? finishReason = null) =>
        new(
            IsCommitSha(commitSha) ? commitSha! : "unavailable",
            RuntimeInformation.RuntimeIdentifier,
            path.ProviderPath,
            path.ModelId,
            timestamp ?? DateTimeOffset.UtcNow,
            usage,
            finishReason,
            status);

    private static bool IsCommitSha(string? value) =>
        value is { Length: 40 } &&
        value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool TryGetBaseUri(string? value, out Uri? uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed) &&
            parsed.Scheme == Uri.UriSchemeHttps &&
            string.IsNullOrEmpty(parsed.UserInfo) &&
            string.IsNullOrEmpty(parsed.Query) &&
            string.IsNullOrEmpty(parsed.Fragment))
        {
            uri = new Uri(parsed.AbsoluteUri.TrimEnd('/') + "/");
            return true;
        }

        uri = null;
        return false;
    }

    private sealed record ProviderCase(
        string ProviderPath,
        string ModelId,
        string BaseUrlVariable,
        string ApiKeyVariable)
    {
        public bool RequiresReasoningRepresentative =>
            ModelId == "deepseek-v4-flash";
    }

    private sealed record ProviderReleaseEvidence(
        string CommitSha,
        string Rid,
        string ProviderPath,
        string ModelId,
        DateTimeOffset TimestampUtc,
        ChatCompletionUsage? Usage,
        ChatCompletionFinishReason? FinishReason,
        ReleaseStatus Status);

    private enum ReleaseStatus
    {
        Pass,
        Fail,
        NotRun,
    }

    private sealed class ProviderProbe
    {
        private readonly List<string> _reasoningDeltas = [];

        public int RequestCount { get; private set; }

        public int LocalPromptTokens { get; private set; }

        public bool HasContent { get; private set; }

        public bool HasReasoning => _reasoningDeltas.Count != 0;

        public IReadOnlyList<string> ReasoningDeltas => _reasoningDeltas;

        public bool Completed { get; private set; }

        public ChatCompletionUsage? Usage { get; private set; }

        public int UsageCount { get; private set; }

        public ChatCompletionFinishReason? FinishReason { get; private set; }

        public void Start(
            ModelTokenizer tokenizer,
            ChatCompletionRequest request)
        {
            RequestCount++;
            LocalPromptTokens =
                AgentFactory.CountPromptTokens(tokenizer, request.Messages);
        }

        public void Observe(ChatCompletionEvent item)
        {
            switch (item)
            {
                case ChatCompletionContentDeltaEvent { Delta.Length: > 0 }:
                    HasContent = true;
                    break;
                case ChatCompletionReasoningDeltaEvent reasoning:
                    _reasoningDeltas.Add(reasoning.Delta);
                    break;
                case ChatCompletionUsageEvent usage:
                    UsageCount++;
                    Usage = usage.Usage;
                    break;
                case ChatCompletionCompletedEvent completed:
                    Completed = true;
                    FinishReason = completed.FinishReason;
                    break;
            }
        }
    }

    private sealed class AuditedChatClient(
        IChatCompletionClient inner,
        ModelTokenizer tokenizer,
        ProviderProbe probe) : IChatCompletionClient
    {
        public async IAsyncEnumerable<ChatCompletionEvent> StreamAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            probe.Start(tokenizer, request);
            await foreach (var item in inner
                               .StreamAsync(request, cancellationToken)
                               .WithCancellation(cancellationToken))
            {
                probe.Observe(item);
                yield return item;
            }
        }
    }
}
