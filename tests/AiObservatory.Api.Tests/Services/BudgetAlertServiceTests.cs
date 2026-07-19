using AiObservatory.Api.Services;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

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

    // AIO-H2 -------------------------------------------------------------------------

    [Fact]
    public async Task CheckAndAlert_WhenNotifierThrows_RuleStaysUntriggeredAndInsightNotPersisted()
    {
        // Deliver-before-persist ordering: if NotifyAsync throws, the rule must stay
        // un-triggered (so it retries next cycle) instead of being marked
        // fired-and-forgotten with the notification silently lost.
        var rule = new BudgetRule { Id = Guid.NewGuid(), Period = BillingPeriod.Daily, ThresholdUsd = 10m };
        _repo.GetBudgetRulesAsync(Arg.Any<CancellationToken>()).Returns([rule]);
        _repo.GetAggregatesAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns([
                new DailyAggregate { Date = new LocalDate(2026, 6, 2), Provider = Provider.Anthropic,
                    Model = "claude-opus-4-8", CostUsd = 15m, InputTokens = 0, OutputTokens = 0, RequestCount = 1 }
            ]);
        _notifier.NotifyAsync(Arg.Any<BudgetAlertPayload>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("SMTP unreachable")));

        var sut = new BudgetAlertService(_repo, _clock, _notifier, NullLogger<BudgetAlertService>.Instance);

        // The failure must not escape CheckAndAlertAsync — one rule's delivery failure
        // must not abort evaluation of the remaining rules this cycle.
        await sut.CheckAndAlertAsync(TestContext.Current.CancellationToken);

        await _repo.DidNotReceive().AddInsightAsync(Arg.Any<Insight>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().SetBudgetRuleTriggeredAsync(
            Arg.Any<Guid>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndAlert_WhenOneRulesNotifierThrows_SiblingRulesStillEvaluated()
    {
        var failing = new BudgetRule { Id = Guid.NewGuid(), Period = BillingPeriod.Daily, ThresholdUsd = 10m };
        var healthy = new BudgetRule
        {
            Id = Guid.NewGuid(), Period = BillingPeriod.Daily, ThresholdUsd = 10m, Provider = Provider.Google
        };
        _repo.GetBudgetRulesAsync(Arg.Any<CancellationToken>()).Returns([failing, healthy]);
        _repo.GetAggregatesAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns([
                new DailyAggregate { Date = new LocalDate(2026, 6, 2), Provider = Provider.Anthropic,
                    Model = "claude-opus-4-8", CostUsd = 15m, InputTokens = 0, OutputTokens = 0, RequestCount = 1 },
                new DailyAggregate { Date = new LocalDate(2026, 6, 2), Provider = Provider.Google,
                    Model = "gemini-1.5-pro", CostUsd = 15m, InputTokens = 0, OutputTokens = 0, RequestCount = 1 }
            ]);

        // Fail only for the "all providers" (Provider == null) payload; the Google-scoped
        // rule's notification succeeds.
        _notifier.NotifyAsync(Arg.Is<BudgetAlertPayload>(p => p.Provider == "all"), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("SMTP unreachable")));
        _notifier.NotifyAsync(Arg.Is<BudgetAlertPayload>(p => p.Provider != "all"), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var sut = new BudgetAlertService(_repo, _clock, _notifier, NullLogger<BudgetAlertService>.Instance);
        await sut.CheckAndAlertAsync(TestContext.Current.CancellationToken);

        await _repo.DidNotReceive().SetBudgetRuleTriggeredAsync(failing.Id, Arg.Any<Instant>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).SetBudgetRuleTriggeredAsync(healthy.Id, Arg.Any<Instant>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndAlert_WhenRuleIsProviderScoped_OnlyThatProvidersSpendCounts()
    {
        var rule = new BudgetRule
        {
            Id = Guid.NewGuid(), Period = BillingPeriod.Daily, ThresholdUsd = 10m, Provider = Provider.Anthropic
        };
        _repo.GetBudgetRulesAsync(Arg.Any<CancellationToken>()).Returns([rule]);
        // Anthropic alone is under threshold; Google's spend would push a combined total
        // over threshold, but must not count towards an Anthropic-scoped rule.
        _repo.GetAggregatesAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns([
                new DailyAggregate { Date = new LocalDate(2026, 6, 2), Provider = Provider.Anthropic,
                    Model = "claude-opus-4-8", CostUsd = 4m, InputTokens = 0, OutputTokens = 0, RequestCount = 1 },
                new DailyAggregate { Date = new LocalDate(2026, 6, 2), Provider = Provider.Google,
                    Model = "gemini-1.5-pro", CostUsd = 20m, InputTokens = 0, OutputTokens = 0, RequestCount = 1 }
            ]);

        var sut = new BudgetAlertService(_repo, _clock, _notifier, NullLogger<BudgetAlertService>.Instance);
        await sut.CheckAndAlertAsync(TestContext.Current.CancellationToken);

        await _repo.DidNotReceive().AddInsightAsync(Arg.Any<Insight>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndAlert_skips_weekly_rule_already_triggered_within_window()
    {
        // Clock is 2026-06-02; weekly window is [today-6, today] = [2026-05-27, 2026-06-02].
        var rule = new BudgetRule
        {
            Id = Guid.NewGuid(),
            Period = BillingPeriod.Weekly,
            ThresholdUsd = 10m,
            LastTriggeredAt = Instant.FromUtc(2026, 5, 28, 8, 0) // inside the current weekly window
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

    [Fact]
    public async Task CheckAndAlert_fires_weekly_rule_last_triggered_before_current_window()
    {
        var rule = new BudgetRule
        {
            Id = Guid.NewGuid(),
            Period = BillingPeriod.Weekly,
            ThresholdUsd = 10m,
            LastTriggeredAt = Instant.FromUtc(2026, 5, 20, 8, 0) // before the current weekly window starts
        };
        _repo.GetBudgetRulesAsync(Arg.Any<CancellationToken>()).Returns([rule]);
        _repo.GetAggregatesAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns([
                new DailyAggregate { Date = new LocalDate(2026, 6, 2), Provider = Provider.Anthropic,
                    Model = "claude-opus-4-8", CostUsd = 15m, InputTokens = 0, OutputTokens = 0, RequestCount = 1 }
            ]);

        var sut = new BudgetAlertService(_repo, _clock, _notifier, NullLogger<BudgetAlertService>.Instance);
        await sut.CheckAndAlertAsync(TestContext.Current.CancellationToken);

        await _repo.Received(1).AddInsightAsync(Arg.Any<Insight>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndAlert_skips_monthly_rule_already_triggered_this_month()
    {
        // Clock is 2026-06-02; monthly window starts 2026-06-01.
        var rule = new BudgetRule
        {
            Id = Guid.NewGuid(),
            Period = BillingPeriod.Monthly,
            ThresholdUsd = 10m,
            LastTriggeredAt = Instant.FromUtc(2026, 6, 1, 8, 0)
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

    [Fact]
    public async Task CheckAndAlert_fires_monthly_rule_last_triggered_in_a_prior_month()
    {
        var rule = new BudgetRule
        {
            Id = Guid.NewGuid(),
            Period = BillingPeriod.Monthly,
            ThresholdUsd = 10m,
            LastTriggeredAt = Instant.FromUtc(2026, 5, 15, 8, 0) // last month
        };
        _repo.GetBudgetRulesAsync(Arg.Any<CancellationToken>()).Returns([rule]);
        _repo.GetAggregatesAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns([
                new DailyAggregate { Date = new LocalDate(2026, 6, 2), Provider = Provider.Anthropic,
                    Model = "claude-opus-4-8", CostUsd = 15m, InputTokens = 0, OutputTokens = 0, RequestCount = 1 }
            ]);

        var sut = new BudgetAlertService(_repo, _clock, _notifier, NullLogger<BudgetAlertService>.Instance);
        await sut.CheckAndAlertAsync(TestContext.Current.CancellationToken);

        await _repo.Received(1).AddInsightAsync(Arg.Any<Insight>(), Arg.Any<CancellationToken>());
    }
}
