using System.Text;
using System.Text.Json;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;
using AiObservatory.Data.Pricing.Catalogs;
using AiObservatory.Ingest.Sources;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Text;

namespace AiObservatory.Ingest.Pricing;

public sealed class GooglePricingSource : IPricingSource
{
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly IReadOnlyList<GoogleSkuMapping> VerifiedMappings = [];
    private readonly HttpClient _client;
    private readonly IClock _clock;
    private readonly ILogger<GooglePricingSource> _logger;
    private readonly IReadOnlyDictionary<string, GoogleSkuMapping> _mappings;
    private readonly string _sourceUrl;

    public GooglePricingSource(IClock clock, ILogger<GooglePricingSource> logger, IOptions<IngestOptions> options)
        : this(clock, logger, options, VerifiedMappings, null) { }

    internal GooglePricingSource(
        IClock clock,
        ILogger<GooglePricingSource> logger,
        IOptions<IngestOptions> options,
        IReadOnlyList<GoogleSkuMapping> mappings,
        HttpMessageHandler? handler
    )
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(mappings);
        var apiKey = RequiredSetting(options.Value.GoogleCloudCatalogApiKey, "Google Cloud Catalog API key");
        var serviceId = RequiredSetting(options.Value.GoogleCloudCatalogServiceId, "Google Cloud Catalog service ID");
        if (!serviceId.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))
        {
            throw new ArgumentException("The Google Cloud Catalog service ID is invalid.", nameof(options));
        }

        try
        {
            _mappings = mappings.ToDictionary(mapping => mapping.SkuId, StringComparer.Ordinal);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException(
                "Google SKU mappings must have unique exact SKU IDs.",
                nameof(mappings),
                exception
            );
        }

        _clock = clock;
        _logger = logger;
        _sourceUrl = $"https://cloudbilling.googleapis.com/v1/services/{serviceId}/skus";
        _client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }, handler is null)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _client.DefaultRequestHeaders.Add("X-Goog-Api-Key", apiKey);
    }

    public string SourceId => PricingSourceIds.GoogleCloudCatalog;

    public async Task<PricingSnapshotCandidate?> FetchAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        var pageToken = string.Empty;
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        var evidence = new List<string>();
        var entries = new List<GooglePriceEntry>();
        var unknownSkuCount = 0;

        do
        {
            var pageUri =
                pageToken.Length == 0
                    ? new Uri(_sourceUrl)
                    : new Uri($"{_sourceUrl}?pageToken={Uri.EscapeDataString(pageToken)}");
            using var request = new HttpRequestMessage(HttpMethod.Get, pageUri);
            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token
            );
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Google catalog request failed with HTTP {(int)response.StatusCode}.",
                    null,
                    response.StatusCode
                );
            }

            var raw = await ReadUtf8Async(response.Content, timeout.Token);
            using var page = ParsePage(raw);
            foreach (var sku in page.RootElement.GetProperty("skus").EnumerateArray())
            {
                var skuId = RequiredString(sku, "skuId");
                if (!_mappings.TryGetValue(skuId, out var mapping))
                {
                    unknownSkuCount++;
                    continue;
                }

                evidence.Add(sku.GetRawText());
                entries.AddRange(ParseMappedSku(sku, mapping));
            }

            pageToken = OptionalString(page.RootElement, "nextPageToken");
            if (pageToken.Length != 0 && !seenTokens.Add(pageToken))
            {
                throw new InvalidDataException("Google catalog pagination repeated a page token.");
            }
        } while (pageToken.Length != 0);

        var retrievedAt = _clock.GetCurrentInstant();
        var catalog = new GooglePriceCatalog("USD", _sourceUrl, retrievedAt, entries);
        var candidate = PricingCandidate.Create(
            Provider.Google,
            SourceId,
            retrievedAt,
            _sourceUrl,
            $"[{string.Join(',', evidence)}]",
            catalog
        );
        _logger.LogInformation(
            "Google catalog fetched: {MappedSkuCount} mapped SKU(s), {UnknownSkuCount} unknown SKU(s).",
            evidence.Count,
            unknownSkuCount
        );
        return candidate;
    }

    private static IEnumerable<GooglePriceEntry> ParseMappedSku(JsonElement sku, GoogleSkuMapping mapping)
    {
        var skuName = RequiredString(sku, "name");
        var description = RequiredString(sku, "description");
        var category = RequiredObject(sku, "category");
        if (RequiredString(category, "serviceDisplayName") != mapping.Service)
        {
            throw new InvalidDataException("A mapped Google SKU changed service.");
        }

        var serviceRegions = RequiredStrings(sku, "serviceRegions");
        var provider = RequiredString(sku, "serviceProviderName");
        var geoTaxonomy = RequiredObject(sku, "geoTaxonomy");
        var geoType = RequiredString(geoTaxonomy, "type");
        var geoRegions = RequiredStrings(geoTaxonomy, "regions");
        if (
            provider != "Google"
            || !serviceRegions.Contains(mapping.Region, StringComparer.Ordinal)
            || !geoRegions.Contains(mapping.Region, StringComparer.Ordinal)
        )
        {
            throw new InvalidDataException("A mapped Google SKU changed provider or region taxonomy.");
        }

        var pricingInfo = RequiredArray(sku, "pricingInfo").EnumerateArray().ToArray();
        if (pricingInfo.Length == 0)
        {
            throw new InvalidDataException("A mapped Google SKU has no pricing information.");
        }

        foreach (var price in pricingInfo)
        {
            var effectiveTime = ParseInstant(RequiredString(price, "effectiveTime"));
            var expression = RequiredObject(price, "pricingExpression");
            var usageUnit = RequiredString(expression, "usageUnit");
            var tiers = RequiredArray(expression, "tieredRates").EnumerateArray().ToArray();
            if (usageUnit != mapping.PricingUnit || tiers.Length != 1)
            {
                throw new InvalidDataException("A mapped Google SKU has an unrecognized pricing expression.");
            }

            var tier = tiers[0];
            var tierStart = RequiredDecimal(tier, "startUsageAmount");
            var money = RequiredObject(tier, "unitPrice");
            var currency = RequiredString(money, "currencyCode");
            var units = RequiredLong(money, "units");
            var nanos = RequiredInt(money, "nanos");
            if (
                tierStart != mapping.TierStartUsageAmount
                || currency != "USD"
                || nanos is < -999_999_999 or > 999_999_999
                || Math.Sign(units) * Math.Sign(nanos) < 0
            )
            {
                throw new InvalidDataException("A mapped Google SKU has a non-USD or ambiguous tier expression.");
            }

            var rate = (units + nanos / 1_000_000_000m) * 1_000_000m;
            var aggregation = RequiredObject(price, "aggregationInfo");
            var aggregationLevel = RequiredString(aggregation, "aggregationLevel");
            var aggregationInterval = RequiredString(aggregation, "aggregationInterval");
            var aggregationCount = RequiredInt(aggregation, "aggregationCount");
            var conversionRate = RequiredDecimal(price, "currencyConversionRate");
            if (
                rate <= 0
                || aggregationLevel == "AGGREGATION_LEVEL_UNSPECIFIED"
                || aggregationInterval == "AGGREGATION_INTERVAL_UNSPECIFIED"
                || aggregationCount <= 0
                || conversionRate != 1m
            )
            {
                throw new InvalidDataException("A mapped Google SKU has an unrecognized pricing expression.");
            }

            yield return new GooglePriceEntry(
                mapping.Service,
                mapping.SkuId,
                skuName,
                description,
                mapping.Aliases,
                effectiveTime.InUtc().Date,
                true,
                effectiveTime,
                mapping.Region,
                geoType,
                serviceRegions,
                geoRegions,
                mapping.Modality,
                mapping.Tier,
                mapping.CacheLane,
                mapping.ContextThreshold,
                usageUnit,
                RequiredString(expression, "usageUnitDescription"),
                RequiredString(expression, "baseUnit"),
                RequiredString(expression, "baseUnitDescription"),
                RequiredDecimal(expression, "baseUnitConversionFactor"),
                RequiredDecimal(expression, "displayQuantity"),
                tierStart,
                currency,
                units,
                nanos,
                aggregationLevel,
                aggregationInterval,
                aggregationCount,
                conversionRate,
                rate
            );
        }
    }

    private static JsonDocument ParsePage(string raw)
    {
        try
        {
            var page = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 32 });
            if (page.RootElement.ValueKind != JsonValueKind.Object)
            {
                page.Dispose();
                throw new InvalidDataException("Google catalog returned an invalid page.");
            }

            try
            {
                _ = RequiredArray(page.RootElement, "skus");
                return page;
            }
            catch
            {
                page.Dispose();
                throw;
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Google catalog returned invalid JSON.", exception);
        }
    }

    private static JsonElement RequiredObject(JsonElement parent, string name) =>
        Required(parent, name, JsonValueKind.Object);

    private static JsonElement RequiredArray(JsonElement parent, string name) =>
        Required(parent, name, JsonValueKind.Array);

    private static JsonElement Required(JsonElement parent, string name, JsonValueKind kind)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != kind)
        {
            throw new InvalidDataException("A mapped Google SKU is missing required catalog data.");
        }

        return value;
    }

    private static string RequiredString(JsonElement parent, string name)
    {
        if (
            !parent.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString())
        )
        {
            throw new InvalidDataException("A mapped Google SKU is missing required catalog data.");
        }

        return value.GetString()!;
    }

    private static string OptionalString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : throw new InvalidDataException("Google catalog returned an invalid page token.");
    }

    private static string[] RequiredStrings(JsonElement parent, string name)
    {
        var elements = RequiredArray(parent, name).EnumerateArray().ToArray();
        if (elements.Any(value => value.ValueKind != JsonValueKind.String))
        {
            throw new InvalidDataException("A mapped Google SKU is missing required catalog data.");
        }

        var values = elements.Select(value => value.GetString()).ToArray();
        if (values.Length == 0 || values.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException("A mapped Google SKU is missing required catalog data.");
        }

        return [.. values!];
    }

    private static decimal RequiredDecimal(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || !value.TryGetDecimal(out var result))
        {
            throw new InvalidDataException("A mapped Google SKU contains an invalid decimal.");
        }

        return result;
    }

    private static int RequiredInt(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || !value.TryGetInt32(out var result))
        {
            throw new InvalidDataException("A mapped Google SKU contains an invalid integer.");
        }

        return result;
    }

    private static long RequiredLong(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            throw new InvalidDataException("A mapped Google SKU contains an invalid integer.");
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }

        if (
            value.ValueKind == JsonValueKind.String
            && long.TryParse(
                value.GetString(),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var text
            )
        )
        {
            return text;
        }

        throw new InvalidDataException("A mapped Google SKU contains an invalid integer.");
    }

    private static Instant ParseInstant(string value)
    {
        var result = InstantPattern.ExtendedIso.Parse(value);
        return result.Success
            ? result.Value
            : throw new InvalidDataException("A mapped Google SKU contains an invalid effective time.");
    }

    private static string RequiredSetting(string? value, string setting)
    {
        if (
            string.IsNullOrWhiteSpace(value)
            || value.StartsWith("@Microsoft.KeyVault(", StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new InvalidOperationException($"{setting} is required.");
        }

        return value;
    }

    private static async Task<string> ReadUtf8Async(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes)
        {
            throw new InvalidDataException("Google catalog response exceeded the size limit.");
        }

        var charset = content.Headers.ContentType?.CharSet?.Trim('"');
        if (
            charset is not null
            && !string.Equals(charset, "utf-8", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(charset, "utf8", StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new InvalidDataException("Google catalog response is not UTF-8.");
        }

        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > MaximumResponseBytes)
            {
                throw new InvalidDataException("Google catalog response exceeded the size limit.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return StrictUtf8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }
}

internal sealed record GoogleSkuMapping(
    string SkuId,
    string Service,
    IReadOnlyList<string> Aliases,
    string Region,
    string Modality,
    string Tier,
    string CacheLane,
    long ContextThreshold,
    string PricingUnit,
    decimal TierStartUsageAmount
);
