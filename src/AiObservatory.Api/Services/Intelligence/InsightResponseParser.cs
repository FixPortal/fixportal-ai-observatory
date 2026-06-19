using System.Text.Json.Nodes;
using AiObservatory.Data.Entities;
using NodaTime;

namespace AiObservatory.Api.Services.Intelligence;

public class InsightResponseParser
{
    public IReadOnlyList<Insight> Parse(
        string json,
        LocalDate periodStart,
        LocalDate periodEnd,
        Instant generatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        var array = JsonNode.Parse(json)?.AsArray()
            ?? throw new InvalidOperationException("Intelligence response was not a JSON array.");

        return array.Select(node => new Insight
        {
            GeneratedAt = generatedAt,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            InsightType = ParseType(node?["type"]?.GetValue<string>() ?? "summary"),
            Title = node?["title"]?.GetValue<string>() ?? "",
            Body = node?["body"]?.GetValue<string>() ?? "",
            Data = node?["data"]?.ToJsonString() ?? "{}"
        }).ToList();
    }

    private static InsightType ParseType(string type) => type.ToLowerInvariant() switch
    {
        "summary" => InsightType.Summary,
        "efficiency" => InsightType.Efficiency,
        "anomaly" => InsightType.Anomaly,
        "recommendation" => InsightType.Recommendation,
        _ => InsightType.Summary
    };
}
