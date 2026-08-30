using AiObservatory.Api.Services.Intelligence;
using AiObservatory.Data.Entities;
using AwesomeAssertions;
using NodaTime;

namespace AiObservatory.Api.Tests.Services;

public class InsightDeduplicatorTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 8, 30, 0, 0);
    private static readonly string[] Subjects = ["gpt-5.6-sol", "OpenAI", "Anthropic", "claude-opus-5"];

    private static Insight Insight(InsightType type, string title, string body = "", Instant? generatedAt = null) =>
        new()
        {
            InsightType = type,
            Title = title,
            Body = body,
            GeneratedAt = generatedAt ?? Now,
        };

    [Fact]
    public void Suppresses_a_same_type_same_subject_repeat_within_the_staleness_window()
    {
        var existing = Insight(
            InsightType.Anomaly,
            "gpt-5.6-sol: Disproportionate Cost Per Request",
            generatedAt: Now - Duration.FromDays(1)
        );
        var candidate = Insight(InsightType.Anomaly, "gpt-5.6-sol Dominates Costs at 97% of API Spend");

        InsightDeduplicator.ShouldSuppress(candidate, [existing], Subjects, Now).Should().BeTrue();
    }

    [Fact]
    public void Does_not_suppress_a_different_subject_of_the_same_type()
    {
        var existing = Insight(
            InsightType.Anomaly,
            "gpt-5.6-sol: Disproportionate Cost Per Request",
            generatedAt: Now - Duration.FromDays(1)
        );
        // A genuinely new anomaly about a different model must not be blocked just
        // because an unrelated anomaly of the same type is still unacknowledged.
        var candidate = Insight(InsightType.Anomaly, "claude-opus-5 cache write volume dropped sharply");

        InsightDeduplicator.ShouldSuppress(candidate, [existing], Subjects, Now).Should().BeFalse();
    }

    [Fact]
    public void Does_not_suppress_a_different_insight_type_about_the_same_subject()
    {
        var existing = Insight(
            InsightType.Anomaly,
            "gpt-5.6-sol: Disproportionate Cost Per Request",
            generatedAt: Now - Duration.FromDays(1)
        );
        var candidate = Insight(InsightType.Recommendation, "Route gpt-5.6-sol workloads to a cheaper tier");

        InsightDeduplicator.ShouldSuppress(candidate, [existing], Subjects, Now).Should().BeFalse();
    }

    [Fact]
    public void Does_not_suppress_once_the_existing_insight_is_outside_the_staleness_window()
    {
        var existing = Insight(
            InsightType.Anomaly,
            "gpt-5.6-sol: Disproportionate Cost Per Request",
            generatedAt: Now - InsightDeduplicator.StalenessWindow - Duration.FromDays(1)
        );
        var candidate = Insight(InsightType.Anomaly, "gpt-5.6-sol Dominates Costs at 97% of API Spend");

        InsightDeduplicator.ShouldSuppress(candidate, [existing], Subjects, Now).Should().BeFalse();
    }

    [Theory]
    [InlineData(InsightType.Summary)]
    [InlineData(InsightType.BudgetAlert)]
    public void Never_suppresses_periodic_or_event_triggered_types(InsightType type)
    {
        var existing = Insight(type, "gpt-5.6-sol summary", generatedAt: Now - Duration.FromHours(1));
        var candidate = Insight(type, "gpt-5.6-sol summary, restated");

        InsightDeduplicator.ShouldSuppress(candidate, [existing], Subjects, Now).Should().BeFalse();
    }

    [Fact]
    public void Never_suppresses_when_no_known_subject_is_mentioned_in_the_candidate()
    {
        // Can't confirm the topic matches, so the safe default is to let it through
        // rather than risk hiding a genuinely new story.
        var existing = Insight(
            InsightType.Anomaly,
            "gpt-5.6-sol: Disproportionate Cost Per Request",
            generatedAt: Now - Duration.FromDays(1)
        );
        var candidate = Insight(InsightType.Anomaly, "Something odd happened yesterday");

        InsightDeduplicator.ShouldSuppress(candidate, [existing], Subjects, Now).Should().BeFalse();
    }

    [Fact]
    public void Ignores_an_acknowledged_or_unrelated_insight_list_entirely()
    {
        InsightDeduplicator
            .ShouldSuppress(Insight(InsightType.Anomaly, "gpt-5.6-sol anomaly"), [], Subjects, Now)
            .Should()
            .BeFalse();
    }
}
