namespace AiObservatory.Ingest;

public class IngestOptions
{
    public const string SectionName = "Ingest";
    public int PollingIntervalMinutes { get; set; } = 60;
    public TimeSpan PollingInterval => TimeSpan.FromMinutes(PollingIntervalMinutes);
}
