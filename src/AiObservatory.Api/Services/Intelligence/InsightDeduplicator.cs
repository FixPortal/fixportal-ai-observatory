using AiObservatory.Data.Entities;
using NodaTime;

namespace AiObservatory.Api.Services.Intelligence;

/// <summary>
/// Suppresses a newly generated insight when an unacknowledged insight of the same
/// <see cref="InsightType"/>, about at least one of the same models/providers, was
/// generated within <see cref="StalenessWindow"/>. The daily regeneration pass builds a
/// fresh prompt from a rolling 30-day window every time, so without this an ongoing
/// story (e.g. one model dominating spend) gets restated as a "new" card every single
/// day with only the numbers moved — observed in practice across 5+ consecutive days.
/// Subject matching is grounded in the real provider/model strings from that day's
/// aggregates (not text similarity on LLM-authored prose, which varies too much in
/// wording day to day to compare reliably), so a genuinely new topic of the same type
/// still gets through immediately.
/// </summary>
public static class InsightDeduplicator
{
    // Summary is a deliberate daily digest and BudgetAlert is already uniquely gated
    // per-rule by BudgetAlertService -- neither benefits from suppression here.
    private static readonly InsightType[] SuppressibleTypes =
    [
        InsightType.Anomaly,
        InsightType.Efficiency,
        InsightType.Recommendation,
    ];

    public static readonly Duration StalenessWindow = Duration.FromDays(4);

    public static bool ShouldSuppress(
        Insight candidate,
        IReadOnlyCollection<Insight> unacknowledged,
        IReadOnlyCollection<string> knownSubjects,
        Instant now
    )
    {
        if (!SuppressibleTypes.Contains(candidate.InsightType))
        {
            return false;
        }

        var candidateSubjects = MentionedSubjects(candidate, knownSubjects);
        if (candidateSubjects.Count == 0)
        {
            return false;
        }

        return unacknowledged.Any(existing =>
            existing.InsightType == candidate.InsightType
            && now - existing.GeneratedAt <= StalenessWindow
            && MentionedSubjects(existing, knownSubjects).Overlaps(candidateSubjects)
        );
    }

    private static HashSet<string> MentionedSubjects(Insight insight, IReadOnlyCollection<string> knownSubjects)
    {
        var haystack = $"{insight.Title} {insight.Body} {insight.Data}";
        return knownSubjects
            .Where(subject => haystack.Contains(subject, StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
