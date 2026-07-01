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
}
