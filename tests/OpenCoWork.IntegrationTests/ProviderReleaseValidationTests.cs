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
    private const int PromptUsageToleranceTokens = 1_536;
    private const int WebSearchPromptUsageToleranceTokens = 8_192;
    private static readonly ProviderCase[] Paths =
    [
        new(
            "deepseek",
            "deepseek-v4-flash",
            "DEEPSEEK_API_KEY"),
    ];
    private static readonly ProviderScenario[] Scenarios =
    [
        new("text", "Reply with exactly OK."),
        new("function", "Use file.list on release-fixture, then reply with exactly OK."),
        new("webSearch", "Use web search to find the official DeepSeek Responses API page, then reply with exactly OK.", NetworkRead: true),
        new("applyPatch", "Use apply_patch to create m9-release-patch.txt containing OK, then reply with exactly OK.", WorkspaceWrite: true),
        new("usage", "Reply with exactly USAGE."),
        new("secretCanary", "Reply with exactly CANARY-SAFE."),
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

    [Fact]
    public void Release_evidence_requires_commit_and_all_six_completed_usage_scenarios()
    {
        var path = Assert.Single(Paths);
        var usage = new DeepSeekResponsesUsage(10, 2, 4, 1, 14);
        var valid = Scenarios
            .Select(scenario => ScenarioEvidence(
                scenario,
                ReleaseStatus.Pass,
                usage: usage,
                terminalStatus: DeepSeekTerminalStatus.Completed))
            .ToArray();

        Assert.Equal(
            ReleaseStatus.NotRun,
            Evidence(path, null, ReleaseStatus.NotRun).Status);
        Assert.Equal(
            ReleaseStatus.Fail,
            Evidence(
                path,
                new string('a', 40),
                ReleaseStatus.Pass,
                scenarios: valid[..^1]).Status);
        var complete = Evidence(
            path,
            new string('a', 40),
            ReleaseStatus.Pass,
            scenarios: valid);
        Assert.Equal(ReleaseStatus.Pass, complete.Status);
        Assert.Equal(6, complete.Scenarios.Count);
    }

    [Theory]
    [InlineData(1_000, 2_536, false, true)]
    [InlineData(1_000, 2_537, false, false)]
    [InlineData(1_000, 9_192, true, true)]
    [InlineData(1_000, 9_193, true, false)]
    [InlineData(398_000, 400_000, false, true)]
    [InlineData(397_999, 400_000, false, false)]
    public void Prompt_usage_reconciliation_has_bounded_protocol_tolerance(
        int localPromptTokens,
        int providerPromptTokens,
        bool serverSearch,
        bool expected) =>
        Assert.Equal(
            expected,
            UsageMatches(
                localPromptTokens,
                providerPromptTokens,
                serverSearch));

    private static async Task<ProviderReleaseEvidence> RunAsync(
        ProviderCase path,
        string commitSha,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var scenarios = new List<ProviderReleaseScenarioEvidence>();
        foreach (var scenario in Scenarios)
        {
            scenarios.Add(await RunScenarioAsync(
                path,
                scenario,
                apiKey,
                cancellationToken));
        }

        return Evidence(
            path,
            commitSha,
            scenarios.All(item => item.Status == ReleaseStatus.Pass)
                ? ReleaseStatus.Pass
                : ReleaseStatus.Fail,
            scenarios: scenarios);
    }

    private static async Task<ProviderReleaseScenarioEvidence> RunScenarioAsync(
        ProviderCase path,
        ProviderScenario scenario,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var root = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-provider-release-{Guid.NewGuid():N}");
        var userProfile = Path.Combine(root, "user");
        var result = ScenarioEvidence(
            scenario,
            ReleaseStatus.Fail,
            timestamp);
        var cleanupFailed = false;
        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(userProfile);
            Directory.CreateDirectory(Path.Combine(root, "release-fixture"));
            var paths = new OpenCoWorkPaths(root);
            await WorkspaceInitializer.InitializeAsync(
                paths,
                TimeSpan.FromSeconds(5),
                cancellationToken);
            var userConfigDirectory = Path.Combine(userProfile, ".opencowork");
            Directory.CreateDirectory(userConfigDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(userConfigDirectory, "config.jsonc"),
                Config(path, scenario),
                cancellationToken);

            var probe = new ProviderProbe();
            var standardOutput = new StringWriter();
            var standardError = new StringWriter();
            var exitCode = await OpenCoWorkCli.RunAsync(
                ["chat", "--workspace", root],
                new StringReader(scenario.Prompt + "\n"),
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
                            timeProvider,
                            serviceProvider.GetRequiredService<IToolInvocationPipeline>());
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
                probe.RequestCount > 0 &&
                probe.Completed &&
                probe.HasContent &&
                !string.IsNullOrWhiteSpace(standardOutput.ToString()) &&
                probe.Usage is
                { InputTokens: > 0, OutputTokens: > 0, TotalTokens: > 0 } usage &&
                probe.Status == DeepSeekTerminalStatus.Completed &&
                probe.PromptUsageMatches &&
                reasoningCommitted &&
                !secretFound &&
                ScenarioPassed(scenario, probe, root))
            {
                result = ScenarioEvidence(
                    scenario,
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
            result = ScenarioEvidence(
                scenario,
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
            ? ScenarioEvidence(scenario, ReleaseStatus.Fail, timestamp)
            : result;
    }

    private static string Config(ProviderCase path, ProviderScenario scenario) =>
        JsonSerializer.Serialize(
            new
            {
                models = new
                {
                    defaultModel = path.ModelId,
                    reasoningEffort = "high",
                },
                tools = new
                {
                    effects = new
                    {
                        networkRead = scenario.NetworkRead ? "allow" : "deny",
                        workspaceWrite = scenario.WorkspaceWrite ? "allow" : "deny",
                    },
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
            using var stream = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (bytes.AsSpan().IndexOf(value) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool UsageMatches(
        int localPromptTokens,
        int providerPromptTokens,
        bool serverSearch)
    {
        var absoluteTolerance = serverSearch
            ? WebSearchPromptUsageToleranceTokens
            : PromptUsageToleranceTokens;
        var tolerance = Math.Max(
            absoluteTolerance,
            (providerPromptTokens + 199L) / 200L);
        return Math.Abs((long)localPromptTokens - providerPromptTokens) <= tolerance;
    }

    private static bool ScenarioPassed(
        ProviderScenario scenario,
        ProviderProbe probe,
        string root) =>
        scenario.Name switch
        {
            "text" or "secretCanary" => true,
            "function" => probe.HasFunctionCall,
            "webSearch" => probe.HasWebSearch,
            "applyPatch" =>
                probe.HasApplyPatch &&
                File.Exists(Path.Combine(root, "m9-release-patch.txt")) &&
                File.ReadAllText(Path.Combine(root, "m9-release-patch.txt"))
                    .Trim() == "OK",
            "usage" => probe.UsageCount > 0,
            _ => false,
        };

    private static ProviderReleaseEvidence Evidence(
        ProviderCase path,
        string? commitSha,
        ReleaseStatus status,
        DateTimeOffset? timestamp = null,
        IReadOnlyList<ProviderReleaseScenarioEvidence>? scenarios = null)
    {
        var values = scenarios ?? Scenarios
            .Select(scenario => ScenarioEvidence(scenario, ReleaseStatus.NotRun))
            .ToArray();
        var effectiveStatus = status == ReleaseStatus.Pass &&
                              (!IsCommitSha(commitSha) ||
                               values.Count != Scenarios.Length ||
                               !values.Select(item => item.Name)
                                   .SequenceEqual(
                                       Scenarios.Select(item => item.Name),
                                       StringComparer.Ordinal) ||
                               values.Any(item =>
                                   item.Status != ReleaseStatus.Pass ||
                                   item.TerminalStatus !=
                                   DeepSeekTerminalStatus.Completed ||
                                   item.Usage is null))
            ? ReleaseStatus.Fail
            : status;
        return new ProviderReleaseEvidence(
            IsCommitSha(commitSha) ? commitSha! : "unavailable",
            RuntimeInformation.RuntimeIdentifier,
            RuntimeInformation.OSDescription,
            Environment.Version.ToString(),
            path.ProviderPath,
            path.ModelId,
            "/v1/responses",
            timestamp ?? DateTimeOffset.UtcNow,
            values,
            effectiveStatus);
    }

    private static ProviderReleaseScenarioEvidence ScenarioEvidence(
        ProviderScenario scenario,
        ReleaseStatus status,
        DateTimeOffset? timestamp = null,
        DeepSeekResponsesUsage? usage = null,
        DeepSeekTerminalStatus? terminalStatus = null) =>
        new(
            scenario.Name,
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

    private sealed record ProviderScenario(
        string Name,
        string Prompt,
        bool NetworkRead = false,
        bool WorkspaceWrite = false);

    private sealed record ProviderReleaseEvidence(
        string CommitSha,
        string Rid,
        string Os,
        string Runtime,
        string ProviderPath,
        string ModelId,
        string Api,
        DateTimeOffset TimestampUtc,
        IReadOnlyList<ProviderReleaseScenarioEvidence> Scenarios,
        ReleaseStatus Status);

    private sealed record ProviderReleaseScenarioEvidence(
        string Name,
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
        private bool _currentRequestHasServerSearch;

        public int RequestCount { get; private set; }

        public int LocalPromptTokens { get; private set; }

        public bool PromptUsageMatches { get; private set; } = true;

        public bool HasContent { get; private set; }

        public bool HasReasoning => _reasoningDeltas.Count != 0;

        public IReadOnlyList<string> ReasoningDeltas => _reasoningDeltas;

        public bool Completed { get; private set; }

        public bool HasFunctionCall { get; private set; }

        public bool HasWebSearch { get; private set; }

        public bool HasApplyPatch { get; private set; }

        public DeepSeekResponsesUsage? Usage { get; private set; }

        public int UsageCount { get; private set; }

        public DeepSeekTerminalStatus? Status { get; private set; }

        public void Start(
            ModelTokenizer tokenizer,
            DeepSeekResponsesRequest request)
        {
            RequestCount++;
            _currentRequestHasServerSearch =
                request.Tools.Any(tool => tool is DeepSeekWebSearchTool);
            LocalPromptTokens = AgentFactory.CountPromptTokens(
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
                case DeepSeekFunctionCallCompletedEvent:
                    HasFunctionCall = true;
                    break;
                case DeepSeekCustomToolCallCompletedEvent:
                    HasApplyPatch = true;
                    break;
                case DeepSeekWebSearchEvent:
                    HasWebSearch = true;
                    break;
                case DeepSeekTerminalEvent terminal:
                    Completed = true;
                    Status = Status is null ||
                             Status == DeepSeekTerminalStatus.Completed
                        ? terminal.Status
                        : Status;
                    if (terminal.Usage is not null)
                    {
                        PromptUsageMatches &= UsageMatches(
                            LocalPromptTokens,
                            terminal.Usage.InputTokens,
                            _currentRequestHasServerSearch);
                        UsageCount++;
                        Usage = Usage is null
                            ? terminal.Usage
                            : new DeepSeekResponsesUsage(
                                Usage.InputTokens + terminal.Usage.InputTokens,
                                Usage.CachedInputTokens +
                                terminal.Usage.CachedInputTokens,
                                Usage.OutputTokens + terminal.Usage.OutputTokens,
                                Usage.ReasoningOutputTokens +
                                terminal.Usage.ReasoningOutputTokens,
                                Usage.TotalTokens + terminal.Usage.TotalTokens);
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
