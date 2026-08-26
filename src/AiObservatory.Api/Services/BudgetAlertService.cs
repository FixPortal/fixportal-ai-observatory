using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using NodaTime;

namespace AiObservatory.Api.Services;

public class BudgetAlertService(
    IUsageRepository repository,
    IClock clock,
    IAlertNotifier notifier,
    ILogger<BudgetAlertService> logger
)
{
    private const int DailyCatchupDays = 7;

    // virtual to match the other de-interfaced services (FxRateProvider, AnthropicIntelligenceClient):
    // overridable for subclass-mocking now that IBudgetAlertService is gone.
    public virtual async Task CheckAndAlertAsync(CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        foreach (var pending in await repository.GetPendingBudgetAlertEmailsAsync(ct))
        {
            await DeliverEmailAsync(pending, now, ct);
        }

        var rules = await repository.GetBudgetRulesAsync(ct);
        var today = now.InUtc().Date;
        var yesterday = today.PlusDays(-1);

        var monthStart = new LocalDate(today.Year, today.Month, 1);

        foreach (var rule in rules)
        {
            if (rule.Period == BillingPeriod.Daily)
            {
                // Recheck a bounded set of completed days so late provider rows and
                // corrections can still alert. The per-rule/per-day claim makes replay safe.
                for (var daysAgo = DailyCatchupDays; daysAgo >= 1; daysAgo--)
                {
                    var date = today.PlusDays(-daysAgo);
                    await CheckRuleSafelyAsync(rule, date, date, now, ct);
                }
                continue;
            }

            if (AlreadyFired(rule, today, yesterday, monthStart))
            {
                continue;
            }

            var (from, to) = GetWindow(rule.Period, today, yesterday, monthStart);
            await CheckRuleSafelyAsync(rule, from, to, now, ct);
        }
    }

    private async Task CheckRuleSafelyAsync(
        BudgetRule rule,
        LocalDate from,
        LocalDate to,
        Instant now,
        CancellationToken ct
    )
    {
        try
        {
            await CheckRuleAsync(rule, from, to, now, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One rule/window must not abort sibling rules or the remaining catch-up days.
            logger.LogError(ex, "Budget alert check failed for rule {RuleId} ({Period})", rule.Id, rule.Period);
        }
    }

    private static (LocalDate From, LocalDate To) GetWindow(
        BillingPeriod period,
        LocalDate today,
        LocalDate yesterday,
        LocalDate monthStart
    ) =>
        // Daily uses yesterday, the last completed day. At the worker's UTC-midnight
        // run, today's spend is approximately zero.
        period switch
        {
            BillingPeriod.Daily => (yesterday, yesterday),
            BillingPeriod.Weekly => (today.PlusDays(-6), today),
            BillingPeriod.Monthly => (monthStart, today),
            _ => (yesterday, yesterday),
        };

    private static bool AlreadyFired(BudgetRule rule, LocalDate today, LocalDate yesterday, LocalDate monthStart)
    {
        if (!rule.LastTriggeredAt.HasValue)
        {
            return false;
        }

        var lastDate = rule.LastTriggeredAt.Value.InUtc().Date;
        return rule.Period switch
        {
            BillingPeriod.Daily => lastDate >= yesterday,
            BillingPeriod.Weekly => lastDate >= today.PlusDays(-6),
            BillingPeriod.Monthly => lastDate >= monthStart,
            _ => false,
        };
    }

    private async Task CheckRuleAsync(BudgetRule rule, LocalDate from, LocalDate to, Instant now, CancellationToken ct)
    {
        var totalSpendGbp = await repository.GetBilledSpendGbpAsync(from, to, rule.Provider, ct);
        if (totalSpendGbp <= rule.ThresholdGbp)
        {
            return;
        }

        var insight = new Insight
        {
            GeneratedAt = now,
            PeriodStart = from,
            PeriodEnd = to,
            InsightType = InsightType.BudgetAlert,
            Title = $"Budget alert: {rule.Period} billed spend exceeded £{rule.ThresholdGbp:F2}",
            Body =
                $"Total {rule.Period.ToString().ToLower()} billed spend reached £{totalSpendGbp:F2}, exceeding your £{rule.ThresholdGbp:F2} threshold.",
            Data = System.Text.Json.JsonSerializer.Serialize(
                new { thresholdGbp = rule.ThresholdGbp, actualSpendGbp = totalSpendGbp }
            ),
        };

        var claim = await repository.GetOrCreateBudgetAlertAsync(
            rule.Id,
            from,
            to,
            rule.ThresholdGbp,
            totalSpendGbp,
            insight,
            now,
            ct
        );

        await DeliverEmailAsync(
            new BudgetAlertEmail(
                rule.Id,
                rule.Provider,
                rule.Period,
                from,
                to,
                claim.ThresholdGbp,
                claim.ActualSpendGbp,
                claim.CreatedAt
            ),
            now,
            ct
        );
    }

    private async Task DeliverEmailAsync(BudgetAlertEmail email, Instant attemptedAt, CancellationToken ct)
    {
        if (
            !await repository.TryMarkBudgetAlertEmailAttemptedAsync(
                email.RuleId,
                email.PeriodStart,
                email.PeriodEnd,
                attemptedAt,
                ct
            )
        )
        {
            return;
        }

        var payload = new BudgetAlertPayload(
            email.Provider?.ToString() ?? "all",
            email.Period.ToString(),
            email.ThresholdGbp,
            email.ActualSpendGbp,
            email.CreatedAt.ToDateTimeOffset()
        );

        try
        {
            // At-most-once email: the attempt flag is durable before SMTP. A crash or
            // cancellation from here can lose email, but the in-app insight survives and
            // retry never sends a duplicate.
            await notifier.NotifyAsync(payload, ct);
            await repository.MarkBudgetAlertEmailSentAsync(
                email.RuleId,
                email.PeriodStart,
                email.PeriodEnd,
                attemptedAt,
                ct
            );
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            logger.LogWarning(
                ex,
                "Budget alert email for rule {RuleId} was claimed before cancellation; it will not retry. The in-app alert is durable",
                email.RuleId
            );
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                ex,
                "Budget alert email failed for rule {RuleId} ({Period}); it will not retry because the SMTP attempt was claimed first. The in-app alert is durable",
                email.RuleId,
                email.Period
            );
        }
    }
}
