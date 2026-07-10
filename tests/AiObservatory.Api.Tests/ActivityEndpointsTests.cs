using AiObservatory.Api.Endpoints;
using AiObservatory.Data.Entities;
using AwesomeAssertions;
using NodaTime;

namespace AiObservatory.Api.Tests;

public class ActivityEndpointsTests
{
    private static ClaudeActivitySession ExistingSession(long activeSeconds, Instant lastSeenAt) =>
        new()
        {
            SessionId = "s1",
            Project = "fixportal-ai-observatory",
            StartedAt = Instant.FromUtc(2026, 7, 1, 9, 0),
            LastSeenAt = lastSeenAt,
            ActiveSeconds = activeSeconds,
        };

    [Fact]
    public void ShouldReplaceExisting_WhenNewActiveSecondsGreater_ReturnsTrue()
    {
        var existing = ExistingSession(100, Instant.FromUtc(2026, 7, 1, 9, 5));
        var result = ActivityEndpoints.ShouldReplaceExisting(existing, 150, Instant.FromUtc(2026, 7, 1, 9, 5));
        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldReplaceExisting_WhenNewLastSeenAtLater_ReturnsTrue()
    {
        var existing = ExistingSession(100, Instant.FromUtc(2026, 7, 1, 9, 5));
        var result = ActivityEndpoints.ShouldReplaceExisting(existing, 100, Instant.FromUtc(2026, 7, 1, 9, 6));
        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldReplaceExisting_WhenBothOlderOrEqual_ReturnsFalse()
    {
        // Out-of-order delivery: a stale sweep result arrives after a newer one
        // already recorded more time. Must not regress the stored total.
        var existing = ExistingSession(150, Instant.FromUtc(2026, 7, 1, 9, 6));
        var result = ActivityEndpoints.ShouldReplaceExisting(existing, 100, Instant.FromUtc(2026, 7, 1, 9, 5));
        result.Should().BeFalse();
    }

    [Fact]
    public void ShouldReplaceExisting_WhenIdentical_ReturnsFalse()
    {
        var lastSeenAt = Instant.FromUtc(2026, 7, 1, 9, 5);
        var existing = ExistingSession(100, lastSeenAt);
        var result = ActivityEndpoints.ShouldReplaceExisting(existing, 100, lastSeenAt);
        result.Should().BeFalse();
    }

    [Fact]
    public void MergeActivity_WhenIncomingActiveSecondsGreaterButLastSeenAtEarlier_KeepsExistingLastSeenAt()
    {
        // Regression case: writing both incoming fields unconditionally would move
        // LastSeenAt backwards even though ActiveSeconds genuinely improved.
        var existing = ExistingSession(100, Instant.FromUtc(2026, 7, 1, 9, 10));
        var (activeSeconds, lastSeenAt) = ActivityEndpoints.MergeActivity(
            existing, 150, Instant.FromUtc(2026, 7, 1, 9, 5));

        activeSeconds.Should().Be(150);
        lastSeenAt.Should().Be(Instant.FromUtc(2026, 7, 1, 9, 10));
    }

    [Fact]
    public void MergeActivity_WhenIncomingLastSeenAtLaterButActiveSecondsSmaller_KeepsExistingActiveSeconds()
    {
        var existing = ExistingSession(150, Instant.FromUtc(2026, 7, 1, 9, 5));
        var (activeSeconds, lastSeenAt) = ActivityEndpoints.MergeActivity(
            existing, 100, Instant.FromUtc(2026, 7, 1, 9, 6));

        activeSeconds.Should().Be(150);
        lastSeenAt.Should().Be(Instant.FromUtc(2026, 7, 1, 9, 6));
    }

    [Fact]
    public void MergeActivity_WhenBothIncomingGreater_TakesBothIncoming()
    {
        var existing = ExistingSession(100, Instant.FromUtc(2026, 7, 1, 9, 5));
        var (activeSeconds, lastSeenAt) = ActivityEndpoints.MergeActivity(
            existing, 150, Instant.FromUtc(2026, 7, 1, 9, 6));

        activeSeconds.Should().Be(150);
        lastSeenAt.Should().Be(Instant.FromUtc(2026, 7, 1, 9, 6));
    }

    [Fact]
    public void MergeIntervalSeconds_WhenEmpty_ReturnsZero()
    {
        ActivityEndpoints.MergeIntervalSeconds([]).Should().Be(0);
    }

    [Fact]
    public void MergeIntervalSeconds_WhenSingleSpan_ReturnsItsDuration()
    {
        var span = (Instant.FromUtc(2026, 7, 1, 9, 0), Instant.FromUtc(2026, 7, 1, 10, 0));
        ActivityEndpoints.MergeIntervalSeconds([span]).Should().Be(3600);
    }

    [Fact]
    public void MergeIntervalSeconds_WhenSpansDisjoint_SumsBoth()
    {
        var a = (Instant.FromUtc(2026, 7, 1, 9, 0), Instant.FromUtc(2026, 7, 1, 10, 0));
        var b = (Instant.FromUtc(2026, 7, 1, 11, 0), Instant.FromUtc(2026, 7, 1, 12, 0));
        ActivityEndpoints.MergeIntervalSeconds([a, b]).Should().Be(7200);
    }

    [Fact]
    public void MergeIntervalSeconds_WhenSpansOverlap_CountsUnionNotSum()
    {
        // Two parallel sessions covering the same hour must not double-count —
        // this is the fix for the >24h/day bar chart bug.
        var a = (Instant.FromUtc(2026, 7, 1, 9, 0), Instant.FromUtc(2026, 7, 1, 11, 0));
        var b = (Instant.FromUtc(2026, 7, 1, 10, 0), Instant.FromUtc(2026, 7, 1, 12, 0));
        ActivityEndpoints.MergeIntervalSeconds([a, b]).Should().Be(3 * 3600);
    }

    [Fact]
    public void MergeIntervalSeconds_WhenOneSpanFullyContainsAnother_CountsOuterOnly()
    {
        var outer = (Instant.FromUtc(2026, 7, 1, 9, 0), Instant.FromUtc(2026, 7, 1, 12, 0));
        var inner = (Instant.FromUtc(2026, 7, 1, 10, 0), Instant.FromUtc(2026, 7, 1, 11, 0));
        ActivityEndpoints.MergeIntervalSeconds([outer, inner]).Should().Be(3 * 3600);
    }

    [Fact]
    public void MergeIntervalSeconds_WhenSpansTouchExactly_MergesAdjacent()
    {
        var a = (Instant.FromUtc(2026, 7, 1, 9, 0), Instant.FromUtc(2026, 7, 1, 10, 0));
        var b = (Instant.FromUtc(2026, 7, 1, 10, 0), Instant.FromUtc(2026, 7, 1, 11, 0));
        ActivityEndpoints.MergeIntervalSeconds([a, b]).Should().Be(2 * 3600);
    }

    [Fact]
    public void MergeIntervalSeconds_WhenUnordered_StillMergesCorrectly()
    {
        var late = (Instant.FromUtc(2026, 7, 1, 11, 0), Instant.FromUtc(2026, 7, 1, 12, 0));
        var early = (Instant.FromUtc(2026, 7, 1, 9, 0), Instant.FromUtc(2026, 7, 1, 10, 0));
        ActivityEndpoints.MergeIntervalSeconds([late, early]).Should().Be(7200);
    }

    [Fact]
    public void MergeIntervalSeconds_WhenSpanIsZeroLengthOrInverted_IsIgnored()
    {
        var valid = (Instant.FromUtc(2026, 7, 1, 9, 0), Instant.FromUtc(2026, 7, 1, 10, 0));
        var zeroLength = (Instant.FromUtc(2026, 7, 1, 12, 0), Instant.FromUtc(2026, 7, 1, 12, 0));
        var inverted = (Instant.FromUtc(2026, 7, 1, 14, 0), Instant.FromUtc(2026, 7, 1, 13, 0));
        ActivityEndpoints.MergeIntervalSeconds([valid, zeroLength, inverted]).Should().Be(3600);
    }

    [Theory]
    [InlineData("fix-portal")]
    [InlineData("fix-portal/fixportal-ai-observatory")]
    [InlineData("chris-fixportal/tooling")]
    public void IsAllowedProject_WhenProjectMatchesAllowedOwner_ReturnsTrue(string project)
    {
        ActivityEndpoints.IsAllowedProject(project).Should().BeTrue();
    }

    [Theory]
    [InlineData("fix-portal-other/example")]
    [InlineData("other/fix-portal")]
    [InlineData("claude-review")]
    public void IsAllowedProject_WhenProjectDoesNotMatchAllowedOwner_ReturnsFalse(string project)
    {
        ActivityEndpoints.IsAllowedProject(project).Should().BeFalse();
    }

    [Fact]
    public void BuildDailyActivityResponses_WhenSessionCrossesUtcMidnight_SplitsWallClockAcrossBothDays()
    {
        var sessions = new[]
        {
            new ActivityEndpoints.ActivitySessionSlice(
                "fix-portal/example",
                Instant.FromUtc(2026, 7, 1, 23, 0),
                Instant.FromUtc(2026, 7, 2, 3, 0),
                ActiveSeconds: 14_400),
        };

        var result = ActivityEndpoints.BuildDailyActivityResponses(sessions, new LocalDate(2026, 7, 1), new LocalDate(2026, 7, 2));

        result.Should().HaveCount(2);
        result[0].Date.Should().Be("2026-07-01");
        result[0].WallClockSeconds.Should().Be(3_600);
        result[0].ActiveSeconds.Should().Be(3_600);
        result[1].Date.Should().Be("2026-07-02");
        result[1].WallClockSeconds.Should().Be(10_800);
        result[1].ActiveSeconds.Should().Be(10_800);
    }

    [Fact]
    public void BuildDailyActivityResponses_FiltersDisallowedProjects()
    {
        var sessions = new[]
        {
            new ActivityEndpoints.ActivitySessionSlice(
                "fix-portal/example",
                Instant.FromUtc(2026, 7, 1, 9, 0),
                Instant.FromUtc(2026, 7, 1, 10, 0),
                ActiveSeconds: 3_600),
            new ActivityEndpoints.ActivitySessionSlice(
                "other/example",
                Instant.FromUtc(2026, 7, 1, 11, 0),
                Instant.FromUtc(2026, 7, 1, 12, 0),
                ActiveSeconds: 3_600),
        };

        var result = ActivityEndpoints.BuildDailyActivityResponses(sessions, new LocalDate(2026, 7, 1), new LocalDate(2026, 7, 1));

        result.Single().ActiveSeconds.Should().Be(3_600);
    }

    private static readonly LocalDate Today = new(2026, 7, 1);

    [Fact]
    public void TryParseDateRange_WhenBothNull_DefaultsToLast30Days()
    {
        var result = ActivityEndpoints.TryParseDateRange(null, null, Today, out var start, out var end, out var error);

        result.Should().BeTrue();
        error.Should().BeNull();
        start.Should().Be(Today.PlusDays(-30));
        end.Should().Be(Today);
    }

    [Fact]
    public void TryParseDateRange_WhenFromAndToValid_ParsesBothDates()
    {
        var result = ActivityEndpoints.TryParseDateRange(
            "2026-06-01", "2026-06-15", Today, out var start, out var end, out var error);

        result.Should().BeTrue();
        error.Should().BeNull();
        start.Should().Be(new LocalDate(2026, 6, 1));
        end.Should().Be(new LocalDate(2026, 6, 15));
    }

    [Fact]
    public void TryParseDateRange_WhenFromInvalid_ReturnsFalseWithError()
    {
        var result = ActivityEndpoints.TryParseDateRange(
            "not-a-date", null, Today, out _, out _, out var error);

        result.Should().BeFalse();
        error.Should().NotBeNull();
    }

    [Fact]
    public void TryParseDateRange_WhenToInvalid_ReturnsFalseWithError()
    {
        var result = ActivityEndpoints.TryParseDateRange(
            null, "2026-13-99", Today, out _, out _, out var error);

        result.Should().BeFalse();
        error.Should().NotBeNull();
    }
}
