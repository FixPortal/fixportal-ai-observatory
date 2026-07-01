using AiObservatory.Api.Endpoints;
using AiObservatory.Data.Entities;
using AwesomeAssertions;
using NodaTime;
using Xunit;

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
