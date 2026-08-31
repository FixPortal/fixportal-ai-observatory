using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Spend;
using NodaTime;

namespace AiObservatory.Api.Services.GitHub;

/// <summary>Folds GitHub's own billed usage into retained observations and the spend ledger.</summary>
public class GitHubBillingSyncService(
    GitHubBillingClient client,
    BillingObservationWriter writer,
    IClock clock,
    ILogger<GitHubBillingSyncService> logger
)
{
    private const string Currency = "USD";

    private static readonly Dictionary<string, (string VendorKey, string CategoryKey)> ProductMap = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["actions"] = ("github-actions", "ci"),
        ["packages"] = ("github-actions", "ci"),
        ["code_quality"] = ("github", "code-review"),
        ["ghas"] = ("github", "subscription"),
    };

    private static readonly (string VendorKey, string CategoryKey) Fallback = ("github", "subscription");

    public async Task<int> SyncAsync(CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var currentYear = now.InUtc().Year;
        var items = new List<GitHubBillingUsageItem>();
        foreach (var year in new[] { currentYear - 1, currentYear })
        {
            items.AddRange(await client.GetUsageAsync(year, ct));
        }

        if (items.Count == 0)
        {
            logger.LogInformation("GitHub billing: no usage items returned");
            return 0;
        }

        var written = 0;
        List<Exception>? failures = null;
        foreach (var line in Aggregate(items))
        {
            var (vendorKey, categoryKey) = ProductMap.GetValueOrDefault(line.Product, Fallback);
            try
            {
                var disposition = await writer.RecordAsync(ToObservation(line, now), vendorKey, categoryKey, ct);
                if (
                    disposition != BillingWriteDisposition.Unchanged
                    && (line.NetAmount != 0m || disposition == BillingWriteDisposition.Corrected)
                )
                {
                    written++;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
                logger.LogError(
                    exception,
                    "GitHub billing: could not retain {Product}/{Sku} in {Month}",
                    line.Product,
                    line.Sku,
                    line.Month
                );
            }
        }

        logger.LogInformation("GitHub billing: {Written} entries written or updated", written);
        if (failures is not null)
        {
            throw new AggregateException("One or more GitHub billing lines could not be retained.", failures);
        }
        return written;
    }

    private static IEnumerable<BillingLine> Aggregate(IEnumerable<GitHubBillingUsageItem> items) =>
        items
            .GroupBy(item =>
                (Month: LocalDate.FromDateOnly(item.Date).With(DateAdjusters.StartOfMonth), item.Product, item.Sku)
            )
            .Select(group => new BillingLine(
                group.Key.Month,
                group.Key.Product,
                group.Key.Sku,
                group.Sum(item => item.GrossAmount),
                group.Sum(item => item.DiscountAmount),
                group.Sum(item => item.NetAmount)
            ))
            .OrderBy(line => line.Month)
            .ThenBy(line => line.Product, StringComparer.Ordinal)
            .ThenBy(line => line.Sku, StringComparer.Ordinal);

    private static BillingObservation ToObservation(BillingLine line, Instant observedAt) =>
        new()
        {
            ProviderKey = "github",
            SourceId = UsageSourceIds.GitHubBillingApi,
            SourceKind = SourceKind.ProviderApi,
            UsageScope = UsageScope.Mixed,
            CostBasis = CostBasis.Billed,
            ObservationKey = ObservationKeyFor(line),
            OccurredOn = line.Month,
            BillingPeriod = line.Month.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            Service = line.Product,
            Sku = line.Sku,
            Currency = Currency,
            GrossAmount = line.GrossAmount,
            // The ledger's invariant is Gross + Credit = Net (same as the Google arm), and
            // GitHub's discountAmount is positive, so it lands as a negative credit.
            CreditAmount = -line.DiscountAmount,
            NetAmount = line.NetAmount,
            RawPayload = JsonSerializer.Serialize(
                new
                {
                    billingMonth = line.Month.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                    product = line.Product,
                    sku = line.Sku,
                    grossAmount = line.GrossAmount,
                    discountAmount = line.DiscountAmount,
                    netAmount = line.NetAmount,
                }
            ),
            ObservedAt = observedAt,
        };

    private static string ObservationKeyFor(BillingLine line)
    {
        var month = line.Month.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var readable = $"github:{month}:{line.Product}:{line.Sku}";
        if (readable.Length <= 200)
        {
            return readable;
        }

        var material = $"{Part(month)}{Part(line.Product)}{Part(line.Sku)}";
        return $"github:{month}:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)))}";
    }

    private static string Part(string value) => $"{value.Length.ToString(CultureInfo.InvariantCulture)}:{value}";

    private sealed record BillingLine(
        LocalDate Month,
        string Product,
        string Sku,
        decimal GrossAmount,
        decimal DiscountAmount,
        decimal NetAmount
    );
}
