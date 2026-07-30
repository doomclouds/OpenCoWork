using System.Text.Json;
using OpenCoWork.Abstractions;
using Xunit;

namespace OpenCoWork.IntegrationTests;

public sealed class AutomationConcurrencyTests
{
    private static readonly AutomationActorContext Host =
        new(AutomationActorKind.Host, "wire:concurrency");

    [Fact]
    public async Task Sixty_four_concurrent_starts_do_not_oversell_sixteen_slots()
    {
        const int limit = 16;
        await using var fixture = await AutomationServiceTests.Fixture.CreateAsync(
            maxConcurrentRuns: limit);
        var ids = Enumerable.Range(0, 64)
            .Select(index => $"parallel-{index:D2}")
            .ToArray();
        foreach (var id in ids)
        {
            await fixture.WriteAsync(id, enabled: true, scheduled: false);
        }

        await fixture.ScanAsync();
        var revisions = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            var definition = await fixture.Service.GetDefinitionAsync(
                new GetAutomationDefinitionRequest(Host, id),
                TestContext.Current.CancellationToken);
            revisions.Add(id, definition.Value!.Summary.Revision);
        }

        var starts = await Task.WhenAll(ids.Select(id => StartAsync(
            fixture,
            id,
            revisions[id])));

        Assert.Equal(limit, starts.Count(result => result.IsSuccess));
        Assert.All(
            starts.Where(result => !result.IsSuccess),
            result => Assert.Equal(
                AutomationErrorCodes.RunConflict,
                result.Error!.Code));
    }

    private static async Task<AutomationResult<AutomationRunSnapshot>> StartAsync(
        AutomationServiceTests.Fixture fixture,
        string id,
        long revision)
    {
        using var inputs = JsonDocument.Parse("{}");
        return await fixture.Service.StartRunAsync(
            new StartAutomationRunRequest(
                Host,
                id,
                inputs.RootElement.Clone(),
                Guid.CreateVersion7(),
                revision),
            TestContext.Current.CancellationToken);
    }
}
