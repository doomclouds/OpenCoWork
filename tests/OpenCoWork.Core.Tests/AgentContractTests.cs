using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OpenCoWork.Abstractions;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class AgentContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    [Fact]
    public void Chat_completion_contract_is_provider_neutral_and_streaming_only()
    {
        var requestProperties = typeof(ChatCompletionRequest)
            .GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                nameof(ChatCompletionRequest.AttemptNumber),
                nameof(ChatCompletionRequest.InvocationId),
                nameof(ChatCompletionRequest.MaxOutputTokens),
                nameof(ChatCompletionRequest.Messages),
                nameof(ChatCompletionRequest.ModelId),
                nameof(ChatCompletionRequest.Purpose),
                nameof(ChatCompletionRequest.Tools),
            ],
            requestProperties);
        Assert.Equal(
            typeof(IAsyncEnumerable<ChatCompletionEvent>),
            typeof(IChatCompletionClient)
                .GetMethod(nameof(IChatCompletionClient.StreamAsync))!
                .ReturnType);
        Assert.DoesNotContain(
            requestProperties,
            name => name.Contains("Provider", StringComparison.Ordinal) ||
                    name.Contains("Secret", StringComparison.Ordinal) ||
                    name.Contains("Raw", StringComparison.Ordinal));
    }

    [Fact]
    public void Agent_contracts_expose_only_normalized_events_and_stable_diagnostics()
    {
        Assert.Equal(
            [
                ChatCompletionFinishReason.Stop,
                ChatCompletionFinishReason.Length,
                ChatCompletionFinishReason.ContentFilter,
                ChatCompletionFinishReason.ToolCall,
                ChatCompletionFinishReason.Unknown,
            ],
            Enum.GetValues<ChatCompletionFinishReason>());
        Assert.Equal(
            [
                typeof(ChatCompletionContentDeltaEvent),
                typeof(ChatCompletionReasoningDeltaEvent),
                typeof(ChatCompletionToolCallDeltaEvent),
                typeof(ChatCompletionToolCallCompletedEvent),
                typeof(ChatCompletionUsageEvent),
                typeof(ChatCompletionCompletedEvent),
            ],
            typeof(ChatCompletionEvent).Assembly
                .GetTypes()
                .Where(type => type.BaseType == typeof(ChatCompletionEvent))
                .OrderBy(type => type.Name, StringComparer.Ordinal)
                .OrderBy(type => Array.IndexOf(
                    new[]
                    {
                        nameof(ChatCompletionContentDeltaEvent),
                        nameof(ChatCompletionReasoningDeltaEvent),
                        nameof(ChatCompletionToolCallDeltaEvent),
                        nameof(ChatCompletionToolCallCompletedEvent),
                        nameof(ChatCompletionUsageEvent),
                        nameof(ChatCompletionCompletedEvent),
                    },
                    type.Name))
                .ToArray());

        var exception = new ChatCompletionException(
            AgentErrorCodes.ProviderRateLimited,
            "Provider rate limit reached.",
            HttpStatusCode.TooManyRequests,
            TimeSpan.FromSeconds(2),
            isTransient: true);
        Assert.Equal(AgentErrorCodes.ProviderRateLimited, exception.Code);
        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(2), exception.RetryAfter);
        Assert.True(exception.IsTransient);
        Assert.False(exception.IsPromptTooLong);

        Assert.Equal(
            19,
            typeof(AgentErrorCodes)
                .GetFields()
                .Count(field => field.IsLiteral && !field.IsInitOnly));
        Assert.Equal("provider.invalidStream", AgentErrorCodes.ProviderInvalidStream);
        Assert.Equal("context.compactionFailed", AgentErrorCodes.ContextCompactionFailed);
        Assert.Contains(
            ChatCompletionMessageRole.Tool,
            Enum.GetValues<ChatCompletionMessageRole>());
    }

    [Fact]
    public void Invocation_usage_and_compaction_snapshots_do_not_store_secrets_or_prompt_text()
    {
        var forbidden = new[] { "Secret", "ApiKey", "PromptText", "InstructionsText", "Raw" };
        var snapshotTypes = new[]
        {
            typeof(AgentInvocationSnapshot),
            typeof(ProviderUsageSnapshot),
            typeof(CompactionCheckpointSnapshot),
        };

        Assert.All(
            snapshotTypes.SelectMany(type => type.GetProperties()),
            property => Assert.DoesNotContain(
                forbidden,
                value => property.Name.Contains(value, StringComparison.OrdinalIgnoreCase)));

        Assert.Equal(AgentMode.Agent, default);
        Assert.Equal(
            [
                AgentMode.Agent,
                AgentMode.Plan,
            ],
            Enum.GetValues<AgentMode>());
    }

    [Fact]
    public void Responses_snapshots_keep_old_json_values_and_compatibility_defaults()
    {
        Assert.Equal(
            "\"response\"",
            JsonSerializer.Serialize(ProviderInvocationPurpose.Response, JsonOptions));
        Assert.Equal(
            "\"compaction\"",
            JsonSerializer.Serialize(ProviderInvocationPurpose.Compaction, JsonOptions));

        var usage = JsonSerializer.Deserialize<ProviderUsageSnapshot>(
            """
            {
              "invocationId":"01991f55-0f32-7d8f-86d8-2efb8f48f18c",
              "attemptNumber":1,
              "purpose":"response",
              "promptTokens":10,
              "completionTokens":4,
              "totalTokens":14,
              "source":"provider",
              "isEstimate":false
            }
            """,
            JsonOptions);
        Assert.NotNull(usage);
        Assert.Equal(0, usage.CachedPromptTokens);
        Assert.Equal(0, usage.ReasoningCompletionTokens);

        var snapshot = new AgentInvocationSnapshot(
            Guid.CreateVersion7(),
            "deepseek",
            "deepseek-v4-flash",
            "deepseek-tokenizer",
            "1",
            AgentMode.Agent,
            new AgentPromptSnapshot("response-v1", new string('a', 64), 10),
            new AgentPromptSnapshot("compaction-v1", new string('b', 64), 8),
            WorkspaceInstructions: null,
            ContextWindowTokens: 128_000,
            MaxOutputTokens: 8_192,
            ConfigurationSha256: new string('c', 64));
        var oldJson = JsonSerializer.SerializeToNode(snapshot, JsonOptions)!.AsObject();
        oldJson.Remove("reasoningEffort");

        var restored = oldJson.Deserialize<AgentInvocationSnapshot>(JsonOptions);

        Assert.NotNull(restored);
        Assert.Equal("high", restored.ReasoningEffort);
    }
}
