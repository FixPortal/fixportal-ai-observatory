using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using AiObservatory.Data.Spend;
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
    FxRateProvider fx,
    BudgetAlertService budgetAlertService,
    IClock clock
) : IInsightGenerator
{
    public async Task<int> GenerateForDateAsync(LocalDate analysisDate, CancellationToken ct = default)
    {
        if (!client.IsConfigured)
        {
            return 0;
        }
        var today = clock.GetCurrentInstant().InUtc().Date;
        var from = analysisDate.PlusDays(-29);

        var aggregates = await repository.GetAggregatesAsync(from, analysisDate, ct);
        var subscriptions = await repository.GetActiveSubscriptionsAsync(today, ct);
        var usdToGbp = await fx.GetUsdToGbpAsync(ct);

        var prompt = promptBuilder.Build(aggregates, subscriptions, from, analysisDate, usdToGbp);
        var json = await client.GenerateInsightsJsonAsync(prompt, ct);
        var insights = parser.Parse(json, from, analysisDate, clock.GetCurrentInstant());

        // Recent, not-yet-dealt-with insights the new batch is checked against, so a
        // repeat of an already-open story is skipped -- see InsightDeduplicator. Grown
        // in place as insights are added, so two same-subject repeats generated in this
        // very run are also caught, not just repeats across days.
        var recent = (await repository.GetUnacknowledgedInsightsAsync(ct)).ToList();
        var knownSubjects = KnownSubjects(aggregates, subscriptions);
        var now = clock.GetCurrentInstant();

        var added = 0;
        foreach (var insight in insights)
        {
            if (InsightDeduplicator.ShouldSuppress(insight, recent, knownSubjects, now))
            {
                continue;
            }
            await repository.AddInsightAsync(insight, ct);
            recent.Add(insight);
            added++;
        }

        await budgetAlertService.CheckAndAlertAsync(ct);
        return added;
    }

    private static IReadOnlyCollection<string> KnownSubjects(
        IReadOnlyList<DailyAggregate> aggregates,
        IReadOnlyList<Subscription> subscriptions
    ) =>
        aggregates
            .Select(a => a.Model)
            .Concat(aggregates.Select(a => a.Provider.ToString()))
            .Concat(subscriptions.Select(s => s.Provider.ToString()))
            .Where(subject => !string.IsNullOrWhiteSpace(subject) && subject.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
