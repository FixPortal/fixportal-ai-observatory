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
            .GetOrCreateBudgetAlertAsync(
                rule.Id,
                new LocalDate(2026, 6, 1),
                new LocalDate(2026, 6, 1),
                rule.ThresholdGbp,
                10.01m,
                Arg.Is<Insight>(i =>
                    i.InsightType == InsightType.BudgetAlert
                    && i.Title.Contains("billed spend")
                    && i.Data.Contains("thresholdGbp")
                ),
                Arg.Any<Instant>(),
                Arg.Any<CancellationToken>()
            );
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
    public async Task CheckAndAlert_evaluates_all_completed_daily_periods_from_rule_boundary_in_one_read()
    {
        var evaluationStartsOn = new LocalDate(2026, 4, 1);
        var rule = Rule(BillingPeriod.Daily, evaluationStartsOn: evaluationStartsOn);
        StubRules(rule);
        StubBilledSpend(rule, 0m);

        await Sut().CheckAndAlertAsync(TestContext.Current.CancellationToken);

        await _repo
            .Received(1)
            .GetDailyBilledSpendGbpAsync(
                evaluationStartsOn,
                new LocalDate(2026, 6, 1),
                null,
                Arg.Any<CancellationToken>()
            );
        await _repo
            .DidNotReceive()
            .GetBilledSpendGbpAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndAlert_reconsiders_a_late_daily_entry_older_than_seven_days()
    {
        var lateDate = new LocalDate(2026, 5, 1);
        var rule = Rule(BillingPeriod.Daily, evaluationStartsOn: lateDate);
        StubRules(rule);
        _repo
            .GetDailyBilledSpendGbpAsync(
                Arg.Any<LocalDate>(),
                Arg.Any<LocalDate>(),
                rule.Provider,
                Arg.Any<CancellationToken>()
            )
            .Returns([new DailyBilledSpend(lateDate, 15m)]);
        StubSuccessfulDelivery();

        await Sut().CheckAndAlertAsync(TestContext.Current.CancellationToken);

        await _repo
            .Received(1)
            .GetOrCreateBudgetAlertAsync(
                rule.Id,
                lateDate,
                lateDate,
                10m,
                15m,
                Arg.Any<Insight>(),
                Arg.Any<Instant>(),
                Arg.Any<CancellationToken>()
            );
        await _notifier.Received(1).NotifyAsync(Arg.Any<BudgetAlertPayload>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndAlert_does_not_replay_daily_history_before_deployment_boundary()
    {
        var rule = Rule(
            BillingPeriod.Daily,
            lastTriggeredAt: Instant.FromUtc(2026, 5, 20, 8, 0),
            evaluationStartsOn: new LocalDate(2026, 6, 2)
        );
        StubRules(rule);

        await Sut().CheckAndAlertAsync(TestContext.Current.CancellationToken);

        await _repo
            .DidNotReceive()
            .GetDailyBilledSpendGbpAsync(
                Arg.Any<LocalDate>(),
                Arg.Any<LocalDate>(),
                Arg.Any<Provider?>(),
                Arg.Any<CancellationToken>()
            );
        await _repo
            .DidNotReceive()
            .GetOrCreateBudgetAlertAsync(
                Arg.Any<Guid>(),
                Arg.Any<LocalDate>(),
                Arg.Any<LocalDate>(),
                Arg.Any<decimal>(),
                Arg.Any<decimal>(),
                Arg.Any<Insight>(),
                Arg.Any<Instant>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task CheckAndAlert_WhenNotifierTemporarilyFails_RetriesSameMessageThenMarksSent()
    {
        var claimId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var ruleId = Guid.Parse("20000000-0000-0000-0000-000000000001");
        var email = new BudgetAlertEmail(
            claimId,
            ruleId,
            Provider.Anthropic,
            BillingPeriod.Daily,
            new LocalDate(2026, 6, 1),
            new LocalDate(2026, 6, 1),
            10m,
            15m,
            Instant.FromUtc(2026, 6, 2, 0, 1)
        );
        _repo.GetDeliverableBudgetAlertEmailsAsync(Arg.Any<Instant>(), Arg.Any<CancellationToken>()).Returns([email]);
        _repo
            .TryAcquireBudgetAlertEmailLeaseAsync(
                claimId,
                Arg.Any<Guid>(),
                Arg.Any<Instant>(),
                Arg.Any<Instant>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(true);
        _notifier
            .NotifyAsync(Arg.Any<BudgetAlertPayload>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("SMTP unreachable")), Task.CompletedTask);

        await Sut().CheckAndAlertAsync(TestContext.Current.CancellationToken);
        await Sut().CheckAndAlertAsync(TestContext.Current.CancellationToken);

        var expectedMessageId = $"budget-alert-{claimId:N}@observatory.fixportal.com";
        await _notifier
            .Received(2)
            .NotifyAsync(
                Arg.Is<BudgetAlertPayload>(payload => payload.MessageId == expectedMessageId),
                Arg.Any<CancellationToken>()
            );
        await _repo
            .Received(1)
            .ReleaseBudgetAlertEmailLeaseAsync(claimId, Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _repo
            .Received(1)
            .MarkBudgetAlertEmailSentAsync(claimId, Arg.Any<Guid>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndAlert_replays_a_pending_email_from_durable_state_before_period_suppression()
    {
        var claimId = Guid.NewGuid();
        var rule = Rule(BillingPeriod.Weekly, lastTriggeredAt: Instant.FromUtc(2026, 6, 1, 8, 0));
        StubRules(rule);
        _repo
            .GetDeliverableBudgetAlertEmailsAsync(Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns([
                new BudgetAlertEmail(
                    claimId,
                    rule.Id,
                    Provider.Anthropic,
                    BillingPeriod.Weekly,
                    new LocalDate(2026, 5, 27),
                    new LocalDate(2026, 6, 2),
                    10m,
                    15m,
                    Instant.FromUtc(2026, 6, 2, 0, 1)
                ),
            ]);
        _repo
            .TryAcquireBudgetAlertEmailLeaseAsync(
                claimId,
                Arg.Any<Guid>(),
                Arg.Any<Instant>(),
                Arg.Any<Instant>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(true);
        _notifier.NotifyAsync(Arg.Any<BudgetAlertPayload>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await Sut().CheckAndAlertAsync(TestContext.Current.CancellationToken);

        await _notifier
            .Received(1)
            .NotifyAsync(
                Arg.Is<BudgetAlertPayload>(payload =>
                    payload.Provider == "Anthropic"
                    && payload.Period == "Weekly"
                    && payload.ThresholdGbp == 10m
                    && payload.ActualSpendGbp == 15m
                ),
                Arg.Any<CancellationToken>()
            );
        await _repo
            .DidNotReceive()
            .GetBilledSpendGbpAsync(
                Arg.Any<LocalDate>(),
                Arg.Any<LocalDate>(),
                Arg.Any<Provider?>(),
                Arg.Any<CancellationToken>()
            );
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
            .Received(1)
            .GetOrCreateBudgetAlertAsync(
                healthy.Id,
                Arg.Any<LocalDate>(),
                Arg.Any<LocalDate>(),
                Arg.Any<decimal>(),
                Arg.Any<decimal>(),
                Arg.Any<Insight>(),
                Arg.Any<Instant>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task CheckAndAlert_WhenRuleIsProviderScoped_OnlyThatProvidersBilledSpendCounts()
    {
        var rule = Rule(BillingPeriod.Daily, provider: Provider.Anthropic);
        StubRules(rule);
        StubBilledSpend(rule, 4m);
        _repo
            .GetDailyBilledSpendGbpAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), null, Arg.Any<CancellationToken>())
            .Returns([new DailyBilledSpend(new LocalDate(2026, 6, 1), 20m)]);

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

        await _notifier.Received(1).NotifyAsync(Arg.Any<BudgetAlertPayload>(), Arg.Any<CancellationToken>());
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

        await _notifier.Received(1).NotifyAsync(Arg.Any<BudgetAlertPayload>(), Arg.Any<CancellationToken>());
    }

    private BudgetAlertService Sut() => new(_repo, _clock, _notifier, NullLogger<BudgetAlertService>.Instance);

    private static BudgetRule Rule(
        BillingPeriod period,
        Provider? provider = null,
        Instant? lastTriggeredAt = null,
        LocalDate? evaluationStartsOn = null
    ) =>
        new()
        {
            Id = Guid.NewGuid(),
            Period = period,
            Provider = provider,
            ThresholdGbp = 10m,
            EvaluationStartsOn = evaluationStartsOn ?? new LocalDate(2026, 6, 1),
            LastTriggeredAt = lastTriggeredAt,
        };

    private void StubRules(params BudgetRule[] rules) =>
        _repo.GetBudgetRulesAsync(Arg.Any<CancellationToken>()).Returns(rules);

    private void StubBilledSpend(BudgetRule rule, decimal amount)
    {
        if (rule.Period == BillingPeriod.Daily)
        {
            _repo
                .GetDailyBilledSpendGbpAsync(
                    Arg.Any<LocalDate>(),
                    Arg.Any<LocalDate>(),
                    rule.Provider,
                    Arg.Any<CancellationToken>()
                )
                .Returns(amount == 0m ? [] : [new DailyBilledSpend(new LocalDate(2026, 6, 1), amount)]);
            return;
        }

        _repo
            .GetBilledSpendGbpAsync(
                Arg.Any<LocalDate>(),
                Arg.Any<LocalDate>(),
                rule.Provider,
                Arg.Any<CancellationToken>()
            )
            .Returns(amount);
    }

    private void StubSuccessfulDelivery()
    {
        StubDurableAlert();
        _repo
            .TryAcquireBudgetAlertEmailLeaseAsync(
                Arg.Any<Guid>(),
                Arg.Any<Guid>(),
                Arg.Any<Instant>(),
                Arg.Any<Instant>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(true);
        _notifier.NotifyAsync(Arg.Any<BudgetAlertPayload>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
    }

    private void StubDurableAlert() =>
        _repo
            .GetOrCreateBudgetAlertAsync(
                Arg.Any<Guid>(),
                Arg.Any<LocalDate>(),
                Arg.Any<LocalDate>(),
                Arg.Any<decimal>(),
                Arg.Any<decimal>(),
                Arg.Any<Insight>(),
                Arg.Any<Instant>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(call => new BudgetAlertClaimResult(
                Guid.NewGuid(),
                true,
                call.ArgAt<decimal>(3),
                call.ArgAt<decimal>(4),
                call.ArgAt<Instant>(6)
            ));
}
