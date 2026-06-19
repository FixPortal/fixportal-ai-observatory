using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using NodaTime;

namespace AiObservatory.Api.Services;

public class BudgetAlertService(IUsageRepository repository, IClock clock)
{
    // virtual to match the other de-interfaced services (FxRateProvider, AnthropicIntelligenceClient):
    // overridable for subclass-mocking now that IBudgetAlertService is gone.
    public virtual async Task CheckAndAlertAsync(CancellationToken ct = default)
    {
        var rules = await repository.GetBudgetRulesAsync(ct);
        var now = clock.GetCurrentInstant();
        var today = now.InUtc().Date;

        var monthStart = new LocalDate(today.Year, today.Month, 1);

        foreach (var rule in rules)
        {
            // Skip if already triggered within this period
            if (rule.LastTriggeredAt.HasValue)
            {
                var lastDate = rule.LastTriggeredAt.Value.InUtc().Date;
                var alreadyFired = rule.Period switch
                {
                    BillingPeriod.Daily => lastDate == today,
                    BillingPeriod.Weekly => lastDate >= today.PlusDays(-6),
                    BillingPeriod.Monthly => lastDate >= monthStart,
                    _ => false
                };
                if (alreadyFired)
                {
                    continue;
                }
            }

            var (from, to) = rule.Period switch
            {
                BillingPeriod.Daily => (today, today),
                BillingPeriod.Weekly => (today.PlusDays(-6), today),
                BillingPeriod.Monthly => (monthStart, today),
                _ => (today, today)
            };

            var aggregates = await repository.GetAggregatesAsync(from, to, ct);
            var relevantAggregates = rule.Provider.HasValue
                ? aggregates.Where(a => a.Provider == rule.Provider)
                : aggregates;

            var totalSpend = relevantAggregates.Sum(a => a.CostUsd);
            if (totalSpend <= rule.ThresholdUsd)
            {
                continue;
            }

            var insight = new Insight
            {
                GeneratedAt = now,
                PeriodStart = from,
                PeriodEnd = to,
                InsightType = InsightType.Anomaly,
                Title = $"Budget alert: {rule.Period} spend exceeded ${rule.ThresholdUsd:F2}",
                Body = $"Total {rule.Period.ToString().ToLower()} spend reached ${totalSpend:F2}, exceeding your ${rule.ThresholdUsd:F2} threshold.",
                Data = System.Text.Json.JsonSerializer.Serialize(new { threshold = rule.ThresholdUsd, actual = totalSpend })
            };

            await repository.AddInsightAsync(insight, ct);
            await repository.SetBudgetRuleTriggeredAsync(rule.Id, now, ct);
        }
    }
}
