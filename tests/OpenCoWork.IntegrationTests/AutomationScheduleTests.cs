using OpenCoWork.Automations;
using Xunit;

namespace OpenCoWork.IntegrationTests;

public sealed class AutomationScheduleTests
{
    [Fact]
    public void Cronos_uses_explicit_iana_and_vixie_dst_semantics()
    {
        var spring = AutomationScheduleCalculator.Next(
            "30 2 * * *",
            "America/New_York",
            new DateTimeOffset(2026, 3, 8, 0, 0, 0, TimeSpan.Zero));
        var fallFirst = AutomationScheduleCalculator.Next(
            "30 1 * * *",
            "America/New_York",
            new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        var fallNext = AutomationScheduleCalculator.Next(
            "30 1 * * *",
            "America/New_York",
            fallFirst!.Value);

        Assert.Equal(
            new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero),
            spring);
        Assert.Equal(
            new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero),
            fallFirst);
        Assert.Equal(
            new DateTimeOffset(2026, 11, 2, 6, 30, 0, TimeSpan.Zero),
            fallNext);
    }

    [Fact]
    public void Downtime_is_coalesced_to_latest_missed_occurrence()
    {
        var advance = AutomationScheduleCalculator.Advance(
            "0 * * * *",
            "UTC",
            new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 1, 5, 30, 0, TimeSpan.Zero));

        Assert.Equal(
            new DateTimeOffset(2026, 1, 1, 5, 0, 0, TimeSpan.Zero),
            advance.CoalescedOccurrenceUtc);
        Assert.Equal(
            new DateTimeOffset(2026, 1, 1, 6, 0, 0, TimeSpan.Zero),
            advance.NextOccurrenceUtc);
    }

    [Fact]
    public void Periodic_idempotency_key_includes_identity_version_and_utc_occurrence()
    {
        var occurrence = new DateTimeOffset(2026, 1, 1, 1, 2, 3, TimeSpan.Zero);

        var first = AutomationScheduleCalculator.IdempotencyKey(
            "nightly-maintenance",
            new string('a', 64),
            occurrence);
        var replay = AutomationScheduleCalculator.IdempotencyKey(
            "nightly-maintenance",
            new string('a', 64),
            occurrence);
        var changed = AutomationScheduleCalculator.IdempotencyKey(
            "nightly-maintenance",
            new string('b', 64),
            occurrence);

        Assert.Equal(first, replay);
        Assert.NotEqual(first, changed);
        Assert.DoesNotContain(" ", first, StringComparison.Ordinal);
    }
}
