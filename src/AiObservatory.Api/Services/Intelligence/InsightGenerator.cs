using AiObservatory.Data.Repositories;
using NodaTime;

namespace AiObservatory.Api.Services.Intelligence;

/// <summary>
/// Runs one insight-generation pass for a given analysis date: load the trailing
/// 30-day aggregates + active subscriptions, build the prompt, call the model,
/// parse and persist the insights, then re-check budget alerts.
/// Shared by the daily <see cref="IntelligenceWorkerService"/> and the on-demand
/// <c>POST /api/insights/generate</c> endpoint so both follow the exact same path.
/// </summary>
public interface IInsightGenerator
{
    Task<int> GenerateForDateAsync(LocalDate analysisDate, CancellationToken ct = default);
}

public sealed class InsightGenerator(
    IUsageRepository repository,
    AnthropicIntelligenceClient client,
    PromptBuilder promptBuilder,
    InsightResponseParser parser,
    Fx.FxRateProvider fx,
    BudgetAlertService budgetAlertService,
    IClock clock) : IInsightGenerator
{
    public async Task<int> GenerateForDateAsync(LocalDate analysisDate, CancellationToken ct = default)
    {
        var today = clock.GetCurrentInstant().InUtc().Date;
        var from = analysisDate.PlusDays(-29);

        var aggregates = await repository.GetAggregatesAsync(from, analysisDate, ct);
        var subscriptions = await repository.GetActiveSubscriptionsAsync(today, ct);
        var usdToGbp = await fx.GetUsdToGbpAsync(ct);

        var prompt = promptBuilder.Build(aggregates, subscriptions, from, analysisDate, usdToGbp);
        var json = await client.GenerateInsightsJsonAsync(prompt, ct);
        var insights = parser.Parse(json, from, analysisDate, clock.GetCurrentInstant());

        foreach (var insight in insights)
        {
            await repository.AddInsightAsync(insight, ct);
        }

        await budgetAlertService.CheckAndAlertAsync(ct);
        return insights.Count;
    }
}
