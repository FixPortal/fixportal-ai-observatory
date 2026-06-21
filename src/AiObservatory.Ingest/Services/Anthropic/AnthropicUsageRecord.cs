using NodaTime;

namespace AiObservatory.Ingest.Services.Anthropic;

public record AnthropicUsageRecord(
    LocalDate Date,
    string Model,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    decimal CostUsd,
    string RawJson);
