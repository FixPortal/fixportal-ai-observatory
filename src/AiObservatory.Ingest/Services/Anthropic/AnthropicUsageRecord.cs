using NodaTime;

namespace AiObservatory.Ingest.Services.Anthropic;

public record AnthropicUsageRecord(
    LocalDate Date,
    string Model,
    string? ServiceTier,
    string? InferenceGeo,
    string? Speed,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWrite5mTokens,
    long CacheWrite1hTokens,
    string RawJson
)
{
    public long CacheWriteTokens => checked(CacheWrite5mTokens + CacheWrite1hTokens);
}
