using AiObservatory.Api.Services;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace AiObservatory.Api.Tests.Services;

public class BudgetAlertServiceTests
{
    private readonly IUsageRepository _repo = Substitute.For<IUsageRepository>();
    private readonly IAlertNotifier _notifier = Substitute.For<IAlertNotifier>();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 6, 2, 10, 0));

    [Fact]
    public async Task CheckAndAlert_triggers_rule_when_daily_spend_exceeds_threshold()
    {
        var rule = new BudgetRule { Id = Guid.NewGuid(), Period = BillingPeriod.Daily, ThresholdUsd = 10m };
        _repo.GetBudgetRulesAsync(Arg.Any<CancellationToken>()).Returns([rule]);
        _repo.GetAggregatesAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns([
                new DailyAggregate { Date = new LocalDate(2026, 6, 2), Provider = Provider.Anthropic,
                    Model = "claude-opus-4-8", CostUsd = 15m, InputTokens = 0, OutputTokens = 0, RequestCount = 1 }
            ]);

        var sut = new BudgetAlertService(_repo, _clock, _notifier, NullLogger<BudgetAlertService>.Instance);
        await sut.CheckAndAlertAsync(TestContext.Current.CancellationToken);

        await _repo.Received(1).AddInsightAsync(
            Arg.Is<Insight>(i => i.InsightType == InsightType.BudgetAlert && i.Title.Contains("Budget")),
            Arg.Any<CancellationToken>());
        await _repo.Received(1).SetBudgetRuleTriggeredAsync(rule.Id, Arg.Any<Instant>(), Arg.Any<CancellationToken>());
        await _notifier.Received(1).NotifyAsync(
            Arg.Is<BudgetAlertPayload>(p => p.ThresholdUsd == rule.ThresholdUsd),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndAlert_does_not_trigger_when_spend_is_below_threshold()
    {
        var rule = new BudgetRule { Id = Guid.NewGuid(), Period = BillingPeriod.Daily, ThresholdUsd = 10m };
        _repo.GetBudgetRulesAsync(Arg.Any<CancellationToken>()).Returns([rule]);
        _repo.GetAggregatesAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns([
                new DailyAggregate { Date = new LocalDate(2026, 6, 2), Provider = Provider.Anthropic,
                    Model = "claude-sonnet-4-6", CostUsd = 3m, InputTokens = 0, OutputTokens = 0, RequestCount = 1 }
            ]);

        var sut = new BudgetAlertService(_repo, _clock, _notifier, NullLogger<BudgetAlertService>.Instance);
        await sut.CheckAndAlertAsync(TestContext.Current.CancellationToken);

        await _repo.DidNotReceive().AddInsightAsync(Arg.Any<Insight>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndAlert_evaluates_daily_rule_against_yesterday()
    {
        // The worker runs at UTC midnight, so a daily rule must be checked against the
        // completed day (yesterday), not the just-started one.
        var rule = new BudgetRule { Id = Guid.NewGuid(), Period = BillingPeriod.Daily, ThresholdUsd = 10m };
        _repo.GetBudgetRulesAsync(Arg.Any<CancellationToken>()).Returns([rule]);
        _repo.GetAggregatesAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var sut = new BudgetAlertService(_repo, _clock, _notifier, NullLogger<BudgetAlertService>.Instance);
        await sut.CheckAndAlertAsync(TestContext.Current.CancellationToken);

        // Clock is 2026-06-02, so the daily window is yesterday = 2026-06-01.
        await _repo.Received(1).GetAggregatesAsync(
            new LocalDate(2026, 6, 1), new LocalDate(2026, 6, 1), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndAlert_skips_rule_already_triggered_today()
    {
        var rule = new BudgetRule
        {
            Id = Guid.NewGuid(),
            Period = BillingPeriod.Daily,
            ThresholdUsd = 10m,
            LastTriggeredAt = Instant.FromUtc(2026, 6, 2, 8, 0) // same day as clock (2026-06-02)
        };
        _repo.GetBudgetRulesAsync(Arg.Any<CancellationToken>()).Returns([rule]);
        _repo.GetAggregatesAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns([
                new DailyAggregate { Date = new LocalDate(2026, 6, 2), Provider = Provider.Anthropic,
                    Model = "claude-opus-4-8", CostUsd = 15m, InputTokens = 0, OutputTokens = 0, RequestCount = 1 }
            ]);

        var sut = new BudgetAlertService(_repo, _clock, _notifier, NullLogger<BudgetAlertService>.Instance);
        await sut.CheckAndAlertAsync(TestContext.Current.CancellationToken);

        await _repo.DidNotReceive().AddInsightAsync(Arg.Any<Insight>(), Arg.Any<CancellationToken>());
    }
}
