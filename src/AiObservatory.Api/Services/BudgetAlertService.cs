using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using NodaTime;

namespace AiObservatory.Api.Services;

public class BudgetAlertService(
    IUsageRepository repository, IClock clock, IAlertNotifier notifier, ILogger<BudgetAlertService> logger)
{
    // virtual to match the other de-interfaced services (FxRateProvider, AnthropicIntelligenceClient):
    // overridable for subclass-mocking now that IBudgetAlertService is gone.
    public virtual async Task CheckAndAlertAsync(CancellationToken ct = default)
    {
        var rules = await repository.GetBudgetRulesAsync(ct);
        var now = clock.GetCurrentInstant();
        var today = now.InUtc().Date;
        var yesterday = today.PlusDays(-1);

        var monthStart = new LocalDate(today.Year, today.Month, 1);

        foreach (var rule in rules)
        {
            // Daily is evaluated against yesterday — the last COMPLETED day. The worker runs
            // at UTC midnight, when "today" has ~zero spend, so a today-window daily rule
            // would essentially never fire.
            var (from, to) = rule.Period switch
            {
                BillingPeriod.Daily => (yesterday, yesterday),
                BillingPeriod.Weekly => (today.PlusDays(-6), today),
                BillingPeriod.Monthly => (monthStart, today),
                _ => (yesterday, yesterday)
            };

            // Skip if already triggered within the window we're evaluating.
            if (rule.LastTriggeredAt.HasValue)
            {
                var lastDate = rule.LastTriggeredAt.Value.InUtc().Date;
                var alreadyFired = rule.Period switch
                {
                    BillingPeriod.Daily => lastDate >= yesterday,
                    BillingPeriod.Weekly => lastDate >= today.PlusDays(-6),
                    BillingPeriod.Monthly => lastDate >= monthStart,
                    _ => false
                };
                if (alreadyFired)
                {
                    continue;
                }
            }

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
                InsightType = InsightType.BudgetAlert,
                Title = $"Budget alert: {rule.Period} spend exceeded ${rule.ThresholdUsd:F2}",
                Body = $"Total {rule.Period.ToString().ToLower()} spend reached ${totalSpend:F2}, exceeding your ${rule.ThresholdUsd:F2} threshold.",
                Data = System.Text.Json.JsonSerializer.Serialize(new { threshold = rule.ThresholdUsd, actual = totalSpend })
            };

            var payload = new BudgetAlertPayload(
                rule.Provider?.ToString() ?? "all",
                rule.Period.ToString(),
                rule.ThresholdUsd,
                totalSpend,
                now.ToDateTimeOffset());

            try
            {
                // Deliver the alert BEFORE recording the trigger: if delivery throws, the
                // rule stays un-triggered and retries next cycle instead of being marked
                // fired-and-forgotten with the notification silently lost.
                await notifier.NotifyAsync(payload, ct);
                await repository.AddInsightAsync(insight, ct);
                await repository.SetBudgetRuleTriggeredAsync(rule.Id, now, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One rule's delivery failure must not abort the remaining rules this cycle.
                logger.LogError(ex,
                    "Budget alert delivery failed for rule {RuleId} ({Period}); will retry next cycle",
                    rule.Id, rule.Period);
            }
        }
    }
}
