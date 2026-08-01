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
            "deepseek",
            "deepseek-v4-flash",
            "DEEPSEEK_API_KEY"),
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
            var apiKey =
                Environment.GetEnvironmentVariable(path.ApiKeyVariable);
            if (!string.IsNullOrEmpty(apiKey))
            {
                secrets.Add(apiKey);
            }

            if (!IsCommitSha(commitSha) ||
                string.IsNullOrWhiteSpace(apiKey))
            {
                results.Add(Evidence(path, commitSha, ReleaseStatus.NotRun));
                continue;
            }

            results.Add(await RunAsync(
                path,
                commitSha!,
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
                Config(path),
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
                            registration => new AuditedResponsesClient(
                                new DeepSeekResponsesClient(
                                    serviceProvider.GetRequiredService<HttpClient>(),
                                    apiKey,
                                    serviceProvider.GetRequiredService<OpenCoWork.Core.Logging.SecretRedactor>(),
                                    registration.ResponseHeaderTimeout,
                                    registration.StreamIdleTimeout,
                                    timeProvider).StreamAsync,
                                registration.Tokenizer,
                                probe).StreamAsync,
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
                { InputTokens: > 0, OutputTokens: > 0, TotalTokens: > 0 } usage &&
                probe.UsageCount == 1 &&
                probe.Status == DeepSeekTerminalStatus.Completed &&
                UsageMatches(probe.LocalPromptTokens, usage.InputTokens) &&
                reasoningCommitted &&
                !secretFound)
            {
                result = Evidence(
                    path,
                    commitSha,
                    ReleaseStatus.Pass,
                    timestamp,
                    usage,
                    probe.Status);
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

    private static string Config(ProviderCase path) =>
        JsonSerializer.Serialize(
            new
            {
                models = new
                {
                    defaultModel = path.ModelId,
                    reasoningEffort = "high",
                },
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true,
            });

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
        DeepSeekResponsesUsage? usage = null,
        DeepSeekTerminalStatus? terminalStatus = null) =>
        new(
            IsCommitSha(commitSha) ? commitSha! : "unavailable",
            RuntimeInformation.RuntimeIdentifier,
            path.ProviderPath,
            path.ModelId,
            timestamp ?? DateTimeOffset.UtcNow,
            usage,
            terminalStatus,
            status);

    private static bool IsCommitSha(string? value) =>
        value is { Length: 40 } &&
        value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed record ProviderCase(
        string ProviderPath,
        string ModelId,
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
        DeepSeekResponsesUsage? Usage,
        DeepSeekTerminalStatus? TerminalStatus,
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

        public DeepSeekResponsesUsage? Usage { get; private set; }

        public int UsageCount { get; private set; }

        public DeepSeekTerminalStatus? Status { get; private set; }

        public void Start(
            ModelTokenizer tokenizer,
            DeepSeekResponsesRequest request)
        {
            RequestCount++;
            LocalPromptTokens =
                AgentFactory.CountPromptTokens(
                    tokenizer,
                    request.Instructions,
                    request.Input,
                    request.Tools);
        }

        public void Observe(DeepSeekResponseEvent item)
        {
            switch (item)
            {
                case DeepSeekTextDeltaEvent
                    { Kind: DeepSeekTextKind.Output, Delta.Length: > 0 }:
                    HasContent = true;
                    break;
                case DeepSeekTextDeltaEvent
                    { Kind: DeepSeekTextKind.Reasoning } reasoning:
                    _reasoningDeltas.Add(reasoning.Delta);
                    break;
                case DeepSeekTerminalEvent terminal:
                    Completed = true;
                    Status = terminal.Status;
                    if (terminal.Usage is not null)
                    {
                        UsageCount++;
                        Usage = terminal.Usage;
                    }

                    break;
            }
        }
    }

    private sealed class AuditedResponsesClient(
        DeepSeekResponseStream inner,
        ModelTokenizer tokenizer,
        ProviderProbe probe)
    {
        public async IAsyncEnumerable<DeepSeekResponseEvent> StreamAsync(
            DeepSeekResponsesRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            probe.Start(tokenizer, request);
            await foreach (var item in inner
                               (request, cancellationToken)
                               .WithCancellation(cancellationToken))
            {
                probe.Observe(item);
                yield return item;
            }
        }
    }
}
