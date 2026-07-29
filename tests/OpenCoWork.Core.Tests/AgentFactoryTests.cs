using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Agents;
using OpenCoWork.Core.Capabilities;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Tools;
using OpenCoWork.Core.Workspaces;
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

    [Fact]
    public void Response_and_compaction_prompts_are_byte_stable_and_normalize_workspace_instructions()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-prompts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllBytes(
                Path.Combine(directory, "AGENTS.md"),
                [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("Use C#.\r\nBe exact.\r\n\r\n")]);
            var instructions = WorkspaceInstructionDocument.Read(
                new OpenCoWorkPaths(directory));
            var tokenizer = TokenizerProfiles
                .GetRequiredForModel("qwen3.8-max-preview")
                .CreateTokenizer(TokenizerBaseDirectory);

            var agent = AgentPrompts.CreateResponse(
                AgentMode.Agent,
                "golden-workspace",
                instructions,
                tokenizer);
            var plan = AgentPrompts.CreateResponse(
                AgentMode.Plan,
                "golden-workspace",
                instructions,
                tokenizer);
            var agentWithoutInstructions = AgentPrompts.CreateResponse(
                AgentMode.Agent,
                "golden-workspace",
                instructions: null,
                tokenizer);
            var planWithoutInstructions = AgentPrompts.CreateResponse(
                AgentMode.Plan,
                "golden-workspace",
                instructions: null,
                tokenizer);
            var compaction = AgentPrompts.CreateCompaction(tokenizer);

            Assert.Equal(
                ReadSnapshot("agent-with-instructions.txt"),
                agent.SystemMessage);
            Assert.Equal(
                ReadSnapshot("plan-with-instructions.txt"),
                plan.SystemMessage);
            Assert.Equal(
                ReadSnapshot("agent-no-instructions.txt"),
                agentWithoutInstructions.SystemMessage);
            Assert.Equal(
                ReadSnapshot("plan-no-instructions.txt"),
                planWithoutInstructions.SystemMessage);
            Assert.Equal(
                ReadSnapshot("compaction.txt"),
                compaction.SystemMessage);
            Assert.Equal("Use C#.\nBe exact.\n", instructions!.Content);
            Assert.Equal(25, instructions.RawByteCount);
            Assert.Equal(
                ["builtin:opencowork.response.v2", "mode:agent", "workspace:AGENTS.md", "runtime:workspaceName"],
                agent.Snapshot.Sources);
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(agent.SystemMessage)))
                    .ToLowerInvariant(),
                agent.Snapshot.SystemMessageSha256);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Response_prompt_injects_active_skills_before_catalog_and_runtime_facts()
    {
        var tokenizer = TokenizerProfiles
            .GetRequiredForModel("qwen3.8-max-preview")
            .CreateTokenizer(TokenizerBaseDirectory);
        const string body = "Always review correctness first.";
        var source = new CapabilitySourceDescriptor(
            CapabilitySourceKind.Workspace,
            "workspace.skills",
            "1",
            new string('a', 64));
        var skills = new EffectiveSkillSnapshot(
            1,
            [
                new EffectiveSkillSnapshotItem(
                    "acme/review",
                    source,
                    "Review changes.",
                    body,
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))
                        .ToLowerInvariant(),
                    IsActive: true,
                    SelectedVariantId: null),
                new EffectiveSkillSnapshotItem(
                    "acme/test",
                    source,
                    "Run tests.",
                    "Run focused tests.",
                    Convert.ToHexString(
                            SHA256.HashData(Encoding.UTF8.GetBytes("Run focused tests.")))
                        .ToLowerInvariant(),
                    IsActive: false,
                    SelectedVariantId: null),
            ],
            new string('b', 64));

        var response = AgentPrompts.CreateResponse(
            AgentMode.Agent,
            "workspace",
            instructions: null,
            tokenizer,
            skills);
        var compaction = AgentPrompts.CreateCompaction(tokenizer);

        var activeIndex = response.SystemMessage.IndexOf(
            "<active_skill id=\"acme/review\">",
            StringComparison.Ordinal);
        var catalogIndex = response.SystemMessage.IndexOf(
            "<skill_catalog>",
            StringComparison.Ordinal);
        var runtimeIndex = response.SystemMessage.IndexOf(
            "Runtime facts:",
            StringComparison.Ordinal);
        Assert.True(activeIndex >= 0);
        Assert.True(activeIndex < catalogIndex);
        Assert.True(catalogIndex < runtimeIndex);
        Assert.Contains(body, response.SystemMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Run focused tests.",
            response.SystemMessage,
            StringComparison.Ordinal);
        Assert.DoesNotContain("acme/review", compaction.SystemMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Agent_factory_freezes_capability_revision_per_turn()
    {
        const string secret = "factory-secret-9127af";
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-factory-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var models = new ModelsConfig
            {
                Providers = new Dictionary<string, ProviderConfig>(StringComparer.Ordinal)
                {
                    ["token-plan"] = new()
                    {
                        BaseUrl = "https://example.test/v1",
                        ApiKey = new ProviderApiKeyConfig
                        {
                            Environment = "TOKEN_PLAN_KEY",
                        },
                        Models = new Dictionary<string, ModelConfig>(StringComparer.Ordinal)
                        {
                            ["qwen3.8-max-preview"] = new()
                            {
                                TokenizerProfileId = "qwen-o200k",
                                TokenizerProfileVersion = "1",
                                ContextWindowTokens = 983_616,
                                MaxOutputTokens = 131_072,
                            },
                        },
                    },
                },
            };
            var credentials = FrozenProviderCredentials.Capture(
                models,
                name => name == "TOKEN_PLAN_KEY" ? secret : null);
            var paths = new OpenCoWorkPaths(directory);
            var tools = new ToolRuntime();
            var capabilities = new WorkspaceCapabilityRuntime(
            [
                WorkspaceCapabilityRuntime.CreateCoreContributions(tools),
            ]);
            await capabilities.StartAsync(TestContext.Current.CancellationToken);
            var factory = new AgentFactory(
                new ProviderRegistry(
                    models,
                    credentials,
                    TokenizerBaseDirectory,
                    directory),
                paths,
                tools,
                new ToolsConfig(),
                capabilities);
            var currentTurnId = Guid.Parse("019f2f95-7b3f-7b5f-8f39-8398ffb2bd85");
            var priorTurnId = Guid.Parse("019f2f95-7b3f-78aa-88e6-817282335c72");
            var timestamp = new DateTimeOffset(
                2026,
                7,
                27,
                12,
                0,
                0,
                TimeSpan.Zero);
            var priorAgent = Item(
                priorTurnId,
                SessionItemType.AgentMessage,
                "Earlier answer",
                6);
            using var arguments = JsonDocument.Parse("""{"path":"src"}""");
            using var output = JsonDocument.Parse("""{"entries":[]}""");
            var toolInvocationId = Guid.CreateVersion7(timestamp.AddTicks(7));
            var toolCall = new SessionItemSnapshot(
                Guid.CreateVersion7(timestamp.AddTicks(8)),
                priorTurnId,
                SessionItemType.ToolCall,
                SessionItemStatus.Completed,
                new ToolCallItemContent(
                    providerRound: 1,
                    priorAgent.ItemId,
                    [
                        new ToolCallItemEntry(
                            "call-1",
                            "file__list",
                            arguments.RootElement,
                            new string('a', 64),
                            sensitiveInputDetected: false),
                    ]),
                Sequence: 7,
                timestamp,
                timestamp);
            var toolResult = new SessionItemSnapshot(
                Guid.CreateVersion7(timestamp.AddTicks(9)),
                priorTurnId,
                SessionItemType.ToolResult,
                SessionItemStatus.Completed,
                new ToolResultItemContent(new ToolResultSnapshot(
                    toolInvocationId,
                    "call-1",
                    ToolInvocationStatus.Completed,
                    output.RootElement,
                    Error: null,
                    IsTruncated: false,
                    OriginalByteCount: 14,
                    new string('b', 64),
                    AttemptCount: 1)),
                Sequence: 8,
                timestamp,
                timestamp);
            var session = new AgentSession(
                new ThreadSnapshot(
                    Guid.Parse("019f2f95-7b3f-75e9-b71a-ed15bcf17054"),
                    "Deterministic thread",
                    ThreadStatus.Active,
                    ThreadAvailability.Available,
                    HistoryMode.Server,
                    20,
                    currentTurnId,
                    [],
                    timestamp,
                    timestamp,
                    SessionProjectionState.Ready,
                    diagnostic: null,
                    "token-plan",
                    "qwen3.8-max-preview",
                    AgentMode.Agent),
                new TurnSnapshot(
                    currentTurnId,
                    Guid.Parse("019f2f95-7b3f-75e9-b71a-ed15bcf17054"),
                    TurnStatus.Running,
                    timestamp,
                    timestamp,
                    CompletedAt: null,
                    Error: null,
                    AgentMode.Agent),
                [
                    Item(priorTurnId, SessionItemType.UserMessage, "Earlier question", 2),
                    Item(priorTurnId, SessionItemType.Reasoning, "private reasoning", 4),
                    priorAgent,
                    toolCall,
                    toolResult,
                    Item(currentTurnId, SessionItemType.UserMessage, "Current question", 19),
                ]);
            var invocationId =
                Guid.Parse("019f2f95-7b3f-7d80-bd45-a7727cb2aabd");

            var first = factory.Create(session, invocationId, instructions: null);
            var second = factory.Create(session, invocationId, instructions: null);
            var firstJson = JsonSerializer.Serialize(first.Snapshot);
            var secondJson = JsonSerializer.Serialize(second.Snapshot);

            Assert.Equal(AgentInvocationDraftDisposition.Ready, first.Disposition);
            Assert.Equal(firstJson, secondJson);
            Assert.Equal(
                [
                    ChatCompletionMessageRole.System,
                    ChatCompletionMessageRole.User,
                    ChatCompletionMessageRole.Assistant,
                    ChatCompletionMessageRole.Tool,
                    ChatCompletionMessageRole.User,
                ],
                first.Messages.Select(message => message.Role));
            var assistantToolCall = Assert.Single(
                first.Messages,
                message => message.ToolCalls is not null);
            Assert.Equal("Earlier answer", assistantToolCall.Content);
            Assert.Equal("call-1", Assert.Single(assistantToolCall.ToolCalls!).Id);
            Assert.Equal(
                "call-1",
                Assert.Single(
                    first.Messages,
                    message => message.Role == ChatCompletionMessageRole.Tool)
                    .ToolCallId);
            Assert.Single(
                first.Messages,
                message => message.Content == "Current question");
            Assert.Equal(11, first.Tools.Count);
            Assert.NotNull(first.Snapshot.Tools);
            Assert.Equal(capabilities.CurrentCatalog.Revision, first.Snapshot.CapabilityRevision);
            Assert.Empty(first.Snapshot.Skills!.Items);
            Assert.Equal(
                first.Tools.Select(tool => tool.ProviderName),
                first.Snapshot.Tools!.CanonicalToProviderNames.Values.Order());
            Assert.DoesNotContain(secret, firstJson, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, first.ResponsePrompt.SystemMessage, StringComparison.Ordinal);
            Assert.Equal(64, first.Snapshot.ConfigurationSha256.Length);
            Assert.True(
                first.InputTokenCount >
                AgentFactory.CountPromptTokens(
                    first.Provider.Tokenizer,
                    first.Messages));

            await capabilities.RefreshAsync(
                [
                    new CapabilityContributionSet(
                        new CapabilitySourceDescriptor(
                            CapabilitySourceKind.Plugin,
                            "acme/provider",
                            "1.0.0",
                            new string('c', 64)),
                        [
                            new CapabilityContribution(
                                CapabilityKind.Provider,
                                "acme/provider",
                                "Acme Provider",
                                "Test provider contribution.",
                                CapabilityStatus.Ready,
                                [],
                                generation: 1,
                                []),
                        ]),
                ],
                TestContext.Current.CancellationToken);
            var next = factory.Create(
                session,
                Guid.CreateVersion7(),
                instructions: null);
            var restoredSession = new AgentSession(
                session.Thread,
                session.Turn,
                session.ModelHistory,
                session.Checkpoint,
                session.CompactionCheckpoint,
                first.Snapshot,
                session.ToolInvocations,
                session.ProviderUsage);
            var restored = factory.Create(
                restoredSession,
                invocationId,
                instructions: null);

            Assert.True(next.Snapshot.CapabilityRevision > first.Snapshot.CapabilityRevision);
            Assert.Equal(
                first.Snapshot.CapabilityRevision,
                restored.Snapshot.CapabilityRevision);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Workspace_instruction_reader_enforces_utf8_size_nul_and_physical_boundary()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-instructions-{Guid.NewGuid():N}");
        var outside = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-instructions-outside-{Guid.NewGuid():N}.md");
        Directory.CreateDirectory(directory);
        try
        {
            var paths = new OpenCoWorkPaths(directory);
            var instructionsPath = Path.Combine(directory, "AGENTS.md");
            Assert.Null(WorkspaceInstructionDocument.Read(paths));

            File.WriteAllBytes(instructionsPath, [0xC3, 0x28]);
            AssertInstructionsInvalid(paths);
            File.WriteAllBytes(instructionsPath, [0x61, 0x00, 0x62]);
            AssertInstructionsInvalid(paths);
            File.WriteAllBytes(instructionsPath, new byte[(64 * 1024) + 1]);
            AssertInstructionsInvalid(paths);
            File.WriteAllBytes(instructionsPath, Enumerable.Repeat((byte)'a', 64 * 1024).ToArray());
            Assert.Equal(
                64 * 1024,
                WorkspaceInstructionDocument.Read(paths)!.RawByteCount);

            if (!OperatingSystem.IsWindows())
            {
                File.Delete(instructionsPath);
                File.WriteAllText(outside, "outside");
                File.CreateSymbolicLink(instructionsPath, outside);
                AssertInstructionsInvalid(paths);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
            File.Delete(outside);
        }
    }

    private static SessionItemSnapshot Item(
        Guid turnId,
        SessionItemType type,
        string text,
        long sequence)
    {
        var timestamp = new DateTimeOffset(
            2026,
            7,
            27,
            12,
            0,
            0,
            TimeSpan.Zero);
        return new SessionItemSnapshot(
            Guid.CreateVersion7(timestamp),
            turnId,
            type,
            SessionItemStatus.Completed,
            new TextItemContent(text),
            sequence,
            timestamp,
            timestamp);
    }

    private static string ReadSnapshot(
        string name,
        [CallerFilePath] string sourceFile = "") =>
        File.ReadAllText(Path.Combine(
            Path.GetDirectoryName(sourceFile)!,
            "Snapshots",
            name));

    private static void AssertInstructionsInvalid(OpenCoWorkPaths paths)
    {
        var exception = Assert.Throws<AgentPreparationException>(
            () => WorkspaceInstructionDocument.Read(paths));
        Assert.Equal(AgentErrorCodes.ContextInstructionsInvalid, exception.Code);
    }
}
