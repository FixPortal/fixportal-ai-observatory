namespace AiObservatory.Ingest;

public class IngestOptions
{
    public const string SectionName = "Ingest";
    public int PollingIntervalMinutes { get; set; } = 60;
    public TimeSpan PollingInterval => TimeSpan.FromMinutes(PollingIntervalMinutes);

    // Each poll re-requests this many trailing days (ending yesterday) so a poll that was
    // down across a midnight — or ran before a provider's daily totals settled — backfills
    // the missed day on a later cycle. Already-recorded days are cheap no-ops (dedup).
    public int LookbackDays { get; set; } = 3;

    // owner/repo pairs to poll for PR/commit/CI activity. Empty disables the
    // GitHub Activity client entirely (see Program.cs) — there is no out-of-repo
    // hook holding this filter, unlike Claude Activity's project allowlist.
    public string[] GitHubRepoAllowlist { get; set; } = [];
}
