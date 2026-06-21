using NodaTime;

namespace AiObservatory.Data.Entities;

public sealed class DailyAggregate
{
    public LocalDate Date { get; init; }
    public Provider Provider { get; init; }
    public string Model { get; set; } = "";
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheWriteTokens { get; set; }
    public decimal CostUsd { get; set; }
    public int RequestCount { get; set; }
}
