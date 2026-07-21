using NodaTime;

namespace AiObservatory.Ingest.Services.Copilot;

// Provider payload properties are populated and consumed by JSON serialization.
// ReSharper disable NotAccessedPositionalProperty.Global

public record CopilotUsageRecord(
    LocalDate Date,
    int ActiveUsers,
    int TotalSuggestionsCount,
    int TotalAcceptancesCount,
    string RawJson);
