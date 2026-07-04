using AiObservatory.Api.Endpoints;
using AwesomeAssertions;
using NodaTime;
using Xunit;

namespace AiObservatory.Api.Tests;

public class GitHubActivityEndpointsTests
{
    [Fact]
    public void ComputeTurnaroundHours_WhenFirstReviewAtIsNull_ReturnsNull()
    {
        var result = GitHubActivityEndpoints.ComputeTurnaroundHours(Instant.FromUtc(2026, 7, 1, 9, 0), null);
        result.Should().BeNull();
    }

    [Fact]
    public void ComputeTurnaroundHours_WhenReviewedThreeHoursLater_ReturnsThree()
    {
        var result = GitHubActivityEndpoints.ComputeTurnaroundHours(
            Instant.FromUtc(2026, 7, 1, 9, 0), Instant.FromUtc(2026, 7, 1, 12, 0));
        result.Should().Be(3.0);
    }

    [Fact]
    public void ComputeTurnaroundHours_RoundsToOneDecimalPlace()
    {
        var result = GitHubActivityEndpoints.ComputeTurnaroundHours(
            Instant.FromUtc(2026, 7, 1, 9, 0), Instant.FromUtc(2026, 7, 1, 9, 40));
        result.Should().Be(0.7); // 40 minutes = 0.666...h, rounds to 0.7
    }
}
