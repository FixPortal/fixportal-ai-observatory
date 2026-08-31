using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
)
{
    /// <summary>
    /// Snapshot identity: the SHA-256 of the raw evidence AND the normalized catalog content,
    /// excluding the catalog's retrieval stamp. Including the normalized content means a
    /// normaliser fix that produces a corrected catalog from unchanged provider evidence still
    /// counts as new content and is activated (and repriced from) instead of short-circuiting
    /// as <see cref="PricingActivationResult.Unchanged"/>; excluding <c>retrievedAt</c> means a
    /// re-fetch of unchanged evidence, which re-stamps the fetch time, still compares equal.
    /// </summary>
    public static string ComputeContentHash(string rawEvidence, string normalizedCatalog)
    {
        var identity = rawEvidence + '\n' + WithoutRetrievalStamp(normalizedCatalog);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private static string WithoutRetrievalStamp(string normalizedCatalog)
    {
        try
        {
            var catalog = JsonNode.Parse(normalizedCatalog);
            catalog?.AsObject().Remove("retrievedAt");
            return catalog?.ToJsonString() ?? normalizedCatalog;
        }
        catch (JsonException)
        {
            // Validation reports malformed catalogs with a proper error; identity just falls
            // back to the unmodified string so that error path stays intact.
            return normalizedCatalog;
        }
    }
}

public static class PricingSourceIds
{
    public const string OpenAi = "openai-pricing";
    public const string Claude = "claude-pricing";
    public const string Kimi = "kimi-pricing";
    public const string GoogleCloudCatalog = "google-cloud-catalog";
    public const string GeminiDeveloperApi = "gemini-developer-api-pricing";
}

public enum PricingActivationResult
{
    Activated,
    Unchanged,
}
