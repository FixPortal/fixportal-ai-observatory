using NodaTime;

namespace AiObservatory.Ingest.Services.Copilot;

public record CopilotUsageRecord(
    LocalDate Date,
    int ActiveUsers,
    int TotalSuggestionsCount,
    int TotalAcceptancesCount,
    string RawJson);
