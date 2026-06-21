using NodaTime;

namespace AiObservatory.Ingest.Services.OpenAi;

public sealed record OpenAiUsageRecord(
    LocalDate Date,
    string Model,
    long InputTokens,
    long OutputTokens,
    long CachedInputTokens,
    decimal CostUsd,
    string RawJson);
