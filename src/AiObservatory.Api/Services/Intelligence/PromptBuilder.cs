using System.Globalization;
using System.Text;
using AiObservatory.Data.Entities;
using NodaTime;

namespace AiObservatory.Api.Services.Intelligence;

public class PromptBuilder
{
    public string Build(
        IReadOnlyList<DailyAggregate> aggregates,
        IReadOnlyList<Subscription> subscriptions,
        LocalDate periodStart,
        LocalDate periodEnd,
        decimal usdToGbp
    )
    {
        ArgumentNullException.ThrowIfNull(aggregates);
        ArgumentNullException.ThrowIfNull(subscriptions);

        // Costs are stored USD-native; present them in GBP so the generated narrative is in £.
        string Gbp(decimal usd) => "£" + (usd * usdToGbp).ToString("F2", CultureInfo.InvariantCulture);

        var sb = new StringBuilder();
        sb.AppendLine($"Analyse this AI usage data for {periodStart} to {periodEnd} and produce insights.");
        sb.AppendLine();

        var totalSpend = aggregates.Sum(a => a.CostUsd);
        var totalRequests = aggregates.Sum(a => a.RequestCount);
        var totalUnknownCosts = aggregates.Sum(a => a.UnknownCostCount);
        sb.AppendLine($"Total reported usage value: {FormatSpend(totalSpend, totalRequests, totalUnknownCosts)}");

        // Grouped by (entity, CostBasis) rather than just the entity, and every figure below
        // carries its own basis tag -- a provider or model can be a mix of real billed spend
        // and notional (subscription-covered, never billed) usage within the same period, and
        // a single blanket footnote at the end of the prompt isn't enough for the model to
        // correctly attribute basis back to a specific number it already wrote about.
        AppendProviderBreakdown(sb, aggregates, FormatSpend);
        AppendModelBreakdown(sb, aggregates, FormatSpend);

        if (subscriptions.Any())
        {
            sb.AppendLine("Flat-rate subscriptions:");
            decimal monthlySubscriptionTotalInGbp = 0;
            foreach (var s in subscriptions)
            {
                var costInGbp = s.Currency.Equals("USD", StringComparison.OrdinalIgnoreCase)
                    ? s.CostAmount * usdToGbp
                    : s.CostAmount;
                var isAnnual = s.BillingInterval == SubscriptionBillingInterval.Annual;
                monthlySubscriptionTotalInGbp += costInGbp / (isAnnual ? 12 : 1);
                sb.AppendLine(
                    $"  {s.Name}: GBP {costInGbp.ToString("F2", CultureInfo.InvariantCulture)}/{(isAnnual ? "year" : "month")} (~GBP {(costInGbp / (isAnnual ? 365 : 30)).ToString("F2", CultureInfo.InvariantCulture)}/day)"
                );
            }
            sb.AppendLine(
                $"Equivalent flat-rate subscription total (annual plans divided by 12): GBP {monthlySubscriptionTotalInGbp.ToString("F2", CultureInfo.InvariantCulture)}/month. Use this pre-calculated value for any monthly subscription total; do not add raw annual prices to monthly costs."
            );
        }

        if (aggregates.Count >= 2 && totalUnknownCosts == 0)
        {
            var yesterday = aggregates.Where(a => a.Date == periodEnd).Sum(a => a.CostUsd);
            var priorPeriod = aggregates.Where(a => a.Date < periodEnd).Sum(a => a.CostUsd);
            var avgPerDay = priorPeriod / Math.Max(1, Period.Between(periodStart, periodEnd, PeriodUnits.Days).Days);
            sb.AppendLine($"Yesterday reported usage value: {Gbp(yesterday)} vs 30-day average: {Gbp(avgPerDay)}/day");
        }

        sb.AppendLine();
        sb.AppendLine(
            "All monetary figures above are in GBP (£). Report every monetary value in your insights in GBP using the £ symbol — never US dollars."
        );
        sb.AppendLine(
            "Not reported means usage was recorded but no monetary value was available. Never describe it as zero cost or zero usage."
        );
        sb.AppendLine(
            "Every reported figure above is tagged with its cost basis. [BILLED] is a real invoice -- money "
                + "actually changed hands. [NOTIONAL] applies public API list prices to usage that was fully "
                + "covered by a flat-rate subscription -- no money changed hands for it; describe it as \"what "
                + "this usage would have cost outside the subscription\" or similar, never as \"spend\", "
                + "\"cost\", \"billed\", or \"API cost\" on its own. [PROVIDER ESTIMATE] and [LIST-PRICE "
                + "ESTIMATE] are estimates, not invoices, but do reflect money that was actually charged under "
                + "pay-per-token billing. Do not describe a NOTIONAL or ESTIMATE figure using billed-spend "
                + "language, and do not sum figures with different cost bases into one \"total spend\" without "
                + "saying which basis it is."
        );
        sb.AppendLine("Note: Include analysis of cache hit rates where relevant to Anthropic usage.");
        sb.AppendLine(
            "Produce 3-5 insights covering: summary, efficiency opportunities, anomalies, and recommendations."
        );
        sb.AppendLine(
            "Format insight body text as markdown: use numbered lists for steps or ranked items, bold for key terms, and concise paragraphs. Keep each body under 200 words."
        );

        return sb.ToString();

        string FormatSpend(decimal spend, int requests, int unknownCosts, CostBasis? costBasis = null)
        {
            if (requests <= unknownCosts)
            {
                return $"Not reported ({requests} requests)";
            }
            var tag = costBasis is { } basis ? $" {CostBasisTag(basis)}" : "";
            return unknownCosts == 0
                ? $"{Gbp(spend)}{tag}"
                : $"{Gbp(spend)}{tag} reported ({unknownCosts} of {requests} requests not reported)";
        }
    }

    private static void AppendProviderBreakdown(
        StringBuilder sb,
        IReadOnlyList<DailyAggregate> aggregates,
        Func<decimal, int, int, CostBasis?, string> formatSpend
    )
    {
        var byProvider = aggregates
            .GroupBy(a => (a.Provider, a.CostBasis))
            .Select(g => new
            {
                g.Key.Provider,
                g.Key.CostBasis,
                Spend = g.Sum(a => a.CostUsd),
                Requests = g.Sum(a => a.RequestCount),
                UnknownCosts = g.Sum(a => a.UnknownCostCount),
            })
            .OrderBy(p => p.Provider.ToString(), StringComparer.Ordinal)
            .ThenBy(p => p.CostBasis.ToString(), StringComparer.Ordinal);
        sb.AppendLine("Reported usage value by provider:");
        foreach (var p in byProvider)
        {
            sb.AppendLine($"  {p.Provider}: {formatSpend(p.Spend, p.Requests, p.UnknownCosts, p.CostBasis)}");
        }
    }

    private static void AppendModelBreakdown(
        StringBuilder sb,
        IReadOnlyList<DailyAggregate> aggregates,
        Func<decimal, int, int, CostBasis?, string> formatSpend
    )
    {
        sb.AppendLine("Model breakdown:");
        var byModel = aggregates
            .GroupBy(a => (a.Model, a.CostBasis))
            .Select(g => new
            {
                g.Key.Model,
                g.Key.CostBasis,
                Spend = g.Sum(a => a.CostUsd),
                Requests = g.Sum(a => a.RequestCount),
                UnknownCosts = g.Sum(a => a.UnknownCostCount),
                InputTokens = g.Sum(a => a.InputTokens),
                OutputTokens = g.Sum(a => a.OutputTokens),
                CacheReadTokens = g.Sum(a => a.CacheReadTokens),
                CacheWriteTokens = g.Sum(a => a.CacheWriteTokens),
            })
            .OrderByDescending(m => m.Spend);
        foreach (var m in byModel)
        {
            var efficiency =
                m.InputTokens > 0
                    ? $"{((double)m.OutputTokens / m.InputTokens).ToString("P0", CultureInfo.InvariantCulture)} output/input ratio"
                    : "no token data";
            var cacheInfo =
                m.CacheReadTokens > 0 || m.CacheWriteTokens > 0
                    ? $", Cache: {m.CacheReadTokens} read, {m.CacheWriteTokens} write"
                    : "";
            var spend =
                m.Requests <= m.UnknownCosts
                    ? "Not reported"
                    : formatSpend(m.Spend, m.Requests, m.UnknownCosts, m.CostBasis);
            sb.AppendLine($"  {m.Model}: {spend}, {m.Requests} requests, {efficiency}{cacheInfo}");
        }
    }

    private static string CostBasisTag(CostBasis costBasis) =>
        costBasis switch
        {
            CostBasis.Billed => "[BILLED]",
            CostBasis.ProviderEstimated => "[PROVIDER ESTIMATE]",
            CostBasis.ListPriceEstimate => "[LIST-PRICE ESTIMATE]",
            CostBasis.Notional => "[NOTIONAL]",
            CostBasis.None => "[NO COST]",
            _ => "[UNCLASSIFIED]",
        };
}
