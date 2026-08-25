using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;
using AiObservatory.Data.Pricing.Catalogs;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;

namespace AiObservatory.Ingest.Sources;

public interface IPricingSource
{
    string SourceId { get; }
    Task<PricingSnapshotCandidate?> FetchAsync(CancellationToken cancellationToken);
}

public sealed record PricingSourceDefinition(string SourceId, bool IsConfigured, Duration ExpectedRefreshInterval);

public static class PricingCatalogJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static string Serialize<T>(T catalog) => JsonSerializer.Serialize(catalog, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new InvalidDataException("The normalized pricing catalog is null.");

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        return options.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
    }
}

internal static class PricingCandidate
{
    public static PricingSnapshotCandidate Create<T>(
        Provider provider,
        string sourceId,
        Instant retrievedAt,
        string sourceUrl,
        string rawEvidence,
        T catalog
    )
    {
        Validate(catalog);
        return new PricingSnapshotCandidate(
            provider,
            sourceId,
            retrievedAt,
            sourceUrl,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawEvidence))),
            rawEvidence,
            PricingCatalogJson.Serialize(catalog)
        );
    }

    private static void Validate<T>(T catalog)
    {
        switch (catalog)
        {
            case OpenAiPriceCatalog openAi:
                openAi.Validate();
                break;
            case AnthropicPriceCatalog anthropic:
                anthropic.Validate();
                break;
            case KimiPriceCatalog kimi:
                kimi.Validate();
                break;
            case GooglePriceCatalog google:
                google.Validate();
                break;
            default:
                throw new ArgumentException("Unknown pricing catalog type.", nameof(catalog));
        }
    }
}
