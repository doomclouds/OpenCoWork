using System.Net;
using OpenCoWork.Abstractions;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class AgentContractTests
{
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
                    name.Contains("Raw", StringComparison.Ordinal) ||
                    name.Contains("Tool", StringComparison.Ordinal));
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
}
