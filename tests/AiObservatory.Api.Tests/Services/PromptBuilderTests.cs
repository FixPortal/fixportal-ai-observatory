using AiObservatory.Api.Services.Intelligence;
using AiObservatory.Data.Entities;
using AwesomeAssertions;
using NodaTime;

namespace AiObservatory.Api.Tests.Services;

public class PromptBuilderTests
{
    [Fact]
    public void Build_includes_total_spend_for_period()
    {
        var aggregates = new List<DailyAggregate>
        {
            new() { Date = new LocalDate(2026, 6, 1), Provider = Provider.Anthropic,
                    Model = "claude-opus-4-8", InputTokens = 10_000, OutputTokens = 2_000,
                    CostUsd = 5.00m, RequestCount = 10 },
            new() { Date = new LocalDate(2026, 6, 1), Provider = Provider.Anthropic,
                    Model = "claude-sonnet-4-6", InputTokens = 50_000, OutputTokens = 8_000,
                    CostUsd = 2.50m, RequestCount = 40 }
        };
        var subscriptions = new List<Subscription>
        {
            new() { Provider = Provider.Copilot, Name = "GitHub Copilot", CostAmount = 9.40m, Currency = "GBP", BillingDay = 1 }
        };

        var sut = new PromptBuilder();
        // USD-native $7.50 total at a 0.80 USD->GBP rate => £6.00.
        var prompt = sut.Build(aggregates, subscriptions, new LocalDate(2026, 6, 1), new LocalDate(2026, 6, 1), 0.80m);

        prompt.Should().Contain("£6.00"); // total API spend, converted to GBP
        prompt.Should().NotContain("$");   // never report in dollars
        prompt.Should().Contain("claude-opus-4-8");
        prompt.Should().Contain("cache");
    }

    [Fact]
    public void Build_includes_cache_hint_for_anthropic_data()
    {
        var aggregates = new List<DailyAggregate>
        {
            new() { Date = new LocalDate(2026, 6, 1), Provider = Provider.Anthropic,
                    Model = "claude-opus-4-8", InputTokens = 10_000, OutputTokens = 2_000,
                    CostUsd = 5.00m, RequestCount = 5 }
        };

        var sut = new PromptBuilder();
        var prompt = sut.Build(aggregates, [], new LocalDate(2026, 6, 1), new LocalDate(2026, 6, 1), 1m);

        prompt.Should().Contain("cache");
    }
}
