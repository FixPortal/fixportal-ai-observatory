using NodaTime;

namespace AiObservatory.Ingest.Services.OpenAi;

// Provider payload properties are populated and consumed by JSON serialization.
// ReSharper disable NotAccessedPositionalProperty.Global

public sealed record OpenAiUsageRecord(
    LocalDate Date,
    string Model,
    long InputTokens,
    long OutputTokens,
    long CachedInputTokens,
    decimal CostUsd,
    string RawJson);
