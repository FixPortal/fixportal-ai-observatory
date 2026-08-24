using AiObservatory.Data.Entities;
using NodaTime;

namespace AiObservatory.Data.Pricing;

public sealed record PricingSnapshotCandidate(
    Provider Provider,
    string SourceId,
    Instant RetrievedAt,
    string SourceUrl,
    string ContentHash,
    string RawEvidence,
    string NormalizedCatalog
);

public static class PricingSourceIds
{
    public const string OpenAi = "openai-pricing";
    public const string Claude = "claude-pricing";
    public const string Kimi = "kimi-pricing";
    public const string GoogleCloudCatalog = "google-cloud-catalog";
}

public enum PricingActivationResult
{
    Activated,
    Unchanged,
}
