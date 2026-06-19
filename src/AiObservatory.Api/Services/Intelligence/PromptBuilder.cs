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
        decimal usdToGbp)
    {
        ArgumentNullException.ThrowIfNull(aggregates);
        ArgumentNullException.ThrowIfNull(subscriptions);

        // Costs are stored USD-native; present them in GBP so the generated narrative is in £.
        string Gbp(decimal usd) => "£" + (usd * usdToGbp).ToString("F2", CultureInfo.InvariantCulture);

        var sb = new StringBuilder();
        sb.AppendLine($"Analyse this AI usage data for {periodStart} to {periodEnd} and produce insights.");
        sb.AppendLine();

        var totalSpend = aggregates.Sum(a => a.CostUsd);
        sb.AppendLine($"Total API spend: {Gbp(totalSpend)}");

        var byProvider = aggregates.GroupBy(a => a.Provider)
            .Select(g => new { Provider = g.Key, Spend = g.Sum(a => a.CostUsd) });
        sb.AppendLine("Spend by provider:");
        foreach (var p in byProvider)
        {
            sb.AppendLine($"  {p.Provider}: {Gbp(p.Spend)}");
        }

        sb.AppendLine("Model breakdown:");
        var byModel = aggregates.GroupBy(a => a.Model)
            .Select(g => new
            {
                Model = g.Key,
                Spend = g.Sum(a => a.CostUsd),
                Requests = g.Sum(a => a.RequestCount),
                InputTokens = g.Sum(a => a.InputTokens),
                OutputTokens = g.Sum(a => a.OutputTokens),
                CacheReadTokens = g.Sum(a => a.CacheReadTokens),
                CacheWriteTokens = g.Sum(a => a.CacheWriteTokens)
            })
            .OrderByDescending(m => m.Spend);
        foreach (var m in byModel)
        {
            var efficiency = m.InputTokens > 0
                ? $"{((double)m.OutputTokens / m.InputTokens).ToString("P0", CultureInfo.InvariantCulture)} output/input ratio"
                : "no token data";
            var cacheInfo = m.CacheReadTokens > 0 || m.CacheWriteTokens > 0
                ? $", Cache: {m.CacheReadTokens} read, {m.CacheWriteTokens} write"
                : "";
            sb.AppendLine($"  {m.Model}: {Gbp(m.Spend)}, {m.Requests} requests, {efficiency}{cacheInfo}");
        }

        if (subscriptions.Any())
        {
            sb.AppendLine("Flat-rate subscriptions:");
            foreach (var s in subscriptions)
            {
                var costInGbp = s.Currency.Equals("USD", StringComparison.OrdinalIgnoreCase)
                    ? s.CostAmount * usdToGbp
                    : s.CostAmount;
                sb.AppendLine($"  {s.Name}: GBP {costInGbp.ToString("F2", CultureInfo.InvariantCulture)}/month (~{(costInGbp / 30).ToString("F2", CultureInfo.InvariantCulture)}/day)");
            }
        }

        if (aggregates.Count >= 2)
        {
            var yesterday = aggregates.Where(a => a.Date == periodEnd).Sum(a => a.CostUsd);
            var priorPeriod = aggregates.Where(a => a.Date < periodEnd).Sum(a => a.CostUsd);
            var avgPerDay = priorPeriod / Math.Max(1, Period.Between(periodStart, periodEnd, PeriodUnits.Days).Days);
            sb.AppendLine($"Yesterday spend: {Gbp(yesterday)} vs 30-day average: {Gbp(avgPerDay)}/day");
        }

        sb.AppendLine();
        sb.AppendLine("All monetary figures above are in GBP (£). Report every monetary value in your insights in GBP using the £ symbol — never US dollars.");
        sb.AppendLine("Note: Include analysis of cache hit rates where relevant to Anthropic usage.");
        sb.AppendLine("Produce 3-5 insights covering: summary, efficiency opportunities, anomalies, and recommendations.");
        sb.AppendLine("Format insight body text as markdown: use numbered lists for steps or ranked items, bold for key terms, and concise paragraphs. Keep each body under 200 words.");

        return sb.ToString();
    }
}
