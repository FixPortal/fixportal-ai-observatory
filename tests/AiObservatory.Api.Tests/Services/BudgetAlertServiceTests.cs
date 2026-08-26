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
    public async Task CheckAndAlert_triggers_rule_when_billed_spend_exceeds_threshold()
    {
        var rule = Rule(BillingPeriod.Daily);
        StubRules(rule);
        StubBilledSpend(rule, 10.01m);
        StubSuccessfulDelivery();

        await Sut().CheckAndAlertAsync(TestContext.Current.CancellationToken);

        await _repo
            .Received(1)
            .AddInsightAsync(
                Arg.Is<Insight>(i =>
                    i.InsightType == InsightType.BudgetAlert
                    && i.Title.Contains("billed spend")
                    && i.Data.Contains("thresholdGbp")
                ),
                Arg.Any<CancellationToken>()
            );
        await _repo.Received(1).SetBudgetRuleTriggeredAsync(rule.Id, Arg.Any<Instant>(), Arg.Any<CancellationToken>());
        await _notifier
            .Received(1)
            .NotifyAsync(
                Arg.Is<BudgetAlertPayload>(p => p.ThresholdGbp == rule.ThresholdGbp && p.ActualSpendGbp == 10.01m),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task CheckAndAlert_does_not_alert_when_billed_spend_is_below_threshold_despite_higher_estimates()
    {
        var rule = Rule(BillingPeriod.Daily, provider: Provider.Anthropic);
        StubRules(rule);
        StubBilledSpend(rule, 4m);
        _repo
            .GetAggregatesAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns([
                new DailyAggregate
                {
                    Date = new LocalDate(2026, 6, 1),
                    Provider = Provider.Anthropic,
                    Model = "legacy-estimate",
                    CostUsd = 20m,
                    InputTokens = 0,
                    OutputTokens = 0,
                    RequestCount = 1,
                },
            ]);

        await Sut().CheckAndAlertAsync(TestContext.Current.CancellationToken);

        await _notifier.DidNotReceive().NotifyAsync(Arg.Any<BudgetAlertPayload>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().AddInsightAsync(Arg.Any<Insight>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndAlert_evaluates_daily_rule_against_yesterday()
    {
        var rule = Rule(BillingPeriod.Daily);
        StubRules(rule);
        StubBilledSpend(rule, 0m);

        await Sut().CheckAndAlertAsync(TestContext.Current.CancellationToken);

        await _repo
            .Received(1)
            .GetBilledSpendGbpAsync(
                new LocalDate(2026, 6, 1),
                new LocalDate(2026, 6, 1),
                null,
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task CheckAndAlert_skips_rule_already_triggered_today()
    {
        var rule = Rule(BillingPeriod.Daily, lastTriggeredAt: Instant.FromUtc(2026, 6, 2, 8, 0));
        StubRules(rule);

        await Sut().CheckAndAlertAsync(TestContext.Current.CancellationToken);

        await _notifier.DidNotReceive().NotifyAsync(Arg.Any<BudgetAlertPayload>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndAlert_WhenNotifierThrows_RuleStaysUntriggeredAndInsightNotPersisted()
    {
        var rule = Rule(BillingPeriod.Daily);
        StubRules(rule);
        StubBilledSpend(rule, 15m);
        _notifier
            .NotifyAsync(Arg.Any<BudgetAlertPayload>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("SMTP unreachable")));

        await Sut().CheckAndAlertAsync(TestContext.Current.CancellationToken);

        await _repo.DidNotReceive().AddInsightAsync(Arg.Any<Insight>(), Arg.Any<CancellationToken>());
        await _repo
            .DidNotReceive()
            .SetBudgetRuleTriggeredAsync(Arg.Any<Guid>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndAlert_WhenOneRulesNotifierThrows_SiblingRulesStillEvaluated()
    {
        var failing = Rule(BillingPeriod.Daily);
        var healthy = Rule(BillingPeriod.Daily, provider: Provider.Google);
        StubRules(failing, healthy);
        StubBilledSpend(failing, 15m);
        StubBilledSpend(healthy, 15m);
        _notifier
            .NotifyAsync(Arg.Is<BudgetAlertPayload>(p => p.Provider == "all"), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("SMTP unreachable")));
        _notifier
            .NotifyAsync(Arg.Is<BudgetAlertPayload>(p => p.Provider != "all"), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await Sut().CheckAndAlertAsync(TestContext.Current.CancellationToken);

        await _repo
            .DidNotReceive()
            .SetBudgetRuleTriggeredAsync(failing.Id, Arg.Any<Instant>(), Arg.Any<CancellationToken>());
        await _repo
            .Received(1)
            .SetBudgetRuleTriggeredAsync(healthy.Id, Arg.Any<Instant>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndAlert_WhenRuleIsProviderScoped_OnlyThatProvidersBilledSpendCounts()
    {
        var rule = Rule(BillingPeriod.Daily, provider: Provider.Anthropic);
        StubRules(rule);
        StubBilledSpend(rule, 4m);
        _repo
            .GetBilledSpendGbpAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), null, Arg.Any<CancellationToken>())
            .Returns(20m);

        await Sut().CheckAndAlertAsync(TestContext.Current.CancellationToken);

        await _notifier.DidNotReceive().NotifyAsync(Arg.Any<BudgetAlertPayload>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndAlert_skips_weekly_rule_already_triggered_within_window()
    {
        var rule = Rule(BillingPeriod.Weekly, lastTriggeredAt: Instant.FromUtc(2026, 5, 28, 8, 0));
        StubRules(rule);

        await Sut().CheckAndAlertAsync(TestContext.Current.CancellationToken);

        await _notifier.DidNotReceive().NotifyAsync(Arg.Any<BudgetAlertPayload>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndAlert_fires_weekly_rule_last_triggered_before_current_window()
    {
        var rule = Rule(BillingPeriod.Weekly, lastTriggeredAt: Instant.FromUtc(2026, 5, 20, 8, 0));
        StubRules(rule);
        StubBilledSpend(rule, 15m);
        StubSuccessfulDelivery();

        await Sut().CheckAndAlertAsync(TestContext.Current.CancellationToken);

        await _repo.Received(1).AddInsightAsync(Arg.Any<Insight>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndAlert_skips_monthly_rule_already_triggered_this_month()
    {
        var rule = Rule(BillingPeriod.Monthly, lastTriggeredAt: Instant.FromUtc(2026, 6, 1, 8, 0));
        StubRules(rule);

        await Sut().CheckAndAlertAsync(TestContext.Current.CancellationToken);

        await _notifier.DidNotReceive().NotifyAsync(Arg.Any<BudgetAlertPayload>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndAlert_fires_monthly_rule_last_triggered_in_a_prior_month()
    {
        var rule = Rule(BillingPeriod.Monthly, lastTriggeredAt: Instant.FromUtc(2026, 5, 15, 8, 0));
        StubRules(rule);
        StubBilledSpend(rule, 15m);
        StubSuccessfulDelivery();

        await Sut().CheckAndAlertAsync(TestContext.Current.CancellationToken);

        await _repo.Received(1).AddInsightAsync(Arg.Any<Insight>(), Arg.Any<CancellationToken>());
    }

    private BudgetAlertService Sut() => new(_repo, _clock, _notifier, NullLogger<BudgetAlertService>.Instance);

    private static BudgetRule Rule(BillingPeriod period, Provider? provider = null, Instant? lastTriggeredAt = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            Period = period,
            Provider = provider,
            ThresholdGbp = 10m,
            LastTriggeredAt = lastTriggeredAt,
        };

    private void StubRules(params BudgetRule[] rules) =>
        _repo.GetBudgetRulesAsync(Arg.Any<CancellationToken>()).Returns(rules);

    private void StubBilledSpend(BudgetRule rule, decimal amount) =>
        _repo
            .GetBilledSpendGbpAsync(
                Arg.Any<LocalDate>(),
                Arg.Any<LocalDate>(),
                rule.Provider,
                Arg.Any<CancellationToken>()
            )
            .Returns(amount);

    private void StubSuccessfulDelivery() =>
        _notifier.NotifyAsync(Arg.Any<BudgetAlertPayload>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
}
