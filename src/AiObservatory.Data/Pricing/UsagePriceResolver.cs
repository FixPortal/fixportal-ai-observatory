using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiObservatory.Data.Entities;
using Microsoft.Extensions.Logging;

namespace AiObservatory.Data.Pricing;

public sealed class UsagePriceResolver
{
    private const int MaximumWarningKeys = 4096;
    private const int MaximumLoggedModelLength = 160;
    private static readonly object WarningGate = new();
    private static readonly HashSet<(Provider Provider, string ModelFingerprint, string Missing)> Warnings = [];
    private readonly PricingSnapshotStore _store;
    private readonly IReadOnlyDictionary<Provider, IProviderPriceCalculator> _calculators;
    private readonly ILogger<UsagePriceResolver> _logger;

    public UsagePriceResolver(
        PricingSnapshotStore store,
        IEnumerable<IProviderPriceCalculator> calculators,
        ILogger<UsagePriceResolver> logger
    )
    {
        _store = store;
        _calculators = calculators.ToDictionary(calculator => calculator.Provider);
        _logger = logger;
    }

    public async Task<UsagePriceQuote?> ResolveAsync(UsageEvent usage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(usage);
        if (IsExactZeroUsage(usage))
        {
            return new UsagePriceQuote(0m, 0m);
        }

        if (!_calculators.TryGetValue(usage.Provider, out var calculator))
        {
            WarnOnce(usage, "calculator");
            return null;
        }

        var snapshot = await _store.GetCatalogForDateAsync(
            usage.Provider,
            usage.OccurredAt.InUtc().Date,
            cancellationToken
        );
        if (snapshot is null)
        {
            WarnOnce(usage, "catalog");
            return null;
        }

        var quote = calculator.Calculate(usage, snapshot.NormalizedCatalog);
        if (quote is null)
        {
            WarnOnce(usage, MissingDimensions(usage));
        }

        return quote;
    }

    private void WarnOnce(UsageEvent usage, string missing)
    {
        var model = usage.Model ?? "<missing>";
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(model)));
        lock (WarningGate)
        {
            if (Warnings.Count >= MaximumWarningKeys || !Warnings.Add((usage.Provider, fingerprint, missing)))
            {
                return;
            }
        }

        var sanitizedModel = model.Replace('\r', ' ').Replace('\n', ' ');
        if (sanitizedModel.Length > MaximumLoggedModelLength)
        {
            sanitizedModel = sanitizedModel[..MaximumLoggedModelLength] + "...";
        }

        _logger.LogWarning(
            "No exact {Provider} price for model '{Model}'; missing or unmatched dimensions: {MissingDimensions}.",
            usage.Provider,
            sanitizedModel,
            missing
        );
    }

    private static bool IsExactZeroUsage(UsageEvent usage) =>
        usage.InputTokens == 0
        && usage.OutputTokens == 0
        && (usage.CacheReadTokens ?? 0) == 0
        && (usage.CacheWriteTokens ?? 0) == 0
        && (usage.CacheWrite1hTokens ?? 0) == 0
        && (usage.ThoughtTokens ?? 0) == 0;

    private static string MissingDimensions(UsageEvent usage)
    {
        var missing = new List<string>();
        try
        {
            using var evidence = ProviderPricingJson.Evidence(usage.RawPayload);
            var root = evidence.RootElement;
            switch (usage.Provider)
            {
                case Provider.OpenAI:
                    RequireString(root, "processing", missing);
                    RequireString(root, "context", missing);
                    RequireString(root, "region", missing);
                    break;
                case Provider.Anthropic:
                    RequireString(root, "service_tier", missing);
                    RequireString(root, "speed", missing);
                    RequireString(root, "inference_geo", missing);
                    if ((usage.CacheWriteTokens ?? 0) > 0)
                    {
                        RequireNestedInt(root, "cache_creation", "ephemeral_5m_input_tokens", missing);
                        RequireNestedInt(root, "cache_creation", "ephemeral_1h_input_tokens", missing);
                    }
                    break;
                case Provider.Moonshot:
                    RequireBoolean(root, "high_speed", missing);
                    RequireBoolean(root, "batch", missing);
                    break;
                case Provider.Google:
                    foreach (var dimension in new[] { "service", "sku_id", "region", "modality", "tier", "cache_lane" })
                    {
                        RequireString(root, dimension, missing);
                    }
                    if (!ProviderPricingJson.TryInt64(root, "context_threshold", out _))
                    {
                        missing.Add("context_threshold");
                    }
                    break;
            }
        }
        catch (JsonException)
        {
            missing.Add("raw_payload");
        }

        if (string.IsNullOrWhiteSpace(usage.Model) && usage.Provider != Provider.Google)
        {
            missing.Add("model");
        }

        return missing.Count == 0 ? "catalog-entry" : string.Join(',', missing.Order(StringComparer.Ordinal));
    }

    private static void RequireString(JsonElement root, string name, ICollection<string> missing)
    {
        if (!ProviderPricingJson.TryString(root, name, out _))
        {
            missing.Add(name);
        }
    }

    private static void RequireBoolean(JsonElement root, string name, ICollection<string> missing)
    {
        if (!ProviderPricingJson.TryBoolean(root, name, out _))
        {
            missing.Add(name);
        }
    }

    private static void RequireNestedInt(JsonElement root, string parent, string name, ICollection<string> missing)
    {
        if (!ProviderPricingJson.TryNestedInt64(root, parent, name, out _))
        {
            missing.Add($"{parent}.{name}");
        }
    }
}
