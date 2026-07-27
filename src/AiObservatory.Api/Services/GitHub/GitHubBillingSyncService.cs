using System.Globalization;
using AiObservatory.Api.Services.Fx;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace AiObservatory.Api.Services.GitHub;

/// <summary>
/// Folds GitHub's own billed usage into the spend ledger, one entry per
/// (month, product, SKU).
/// <para>
/// Upserts rather than insert-once, deliberately. The current month's usage grows every day,
/// so an insert-only sync would either have to wait for month end — leaving the ledger up to
/// a month behind on the single largest unrecorded vendor — or write a figure it knows will
/// be wrong tomorrow. Re-running is therefore always safe and always converges on what
/// GitHub currently says it billed.
/// </para>
/// </summary>
public class GitHubBillingSyncService(
    GitHubBillingClient client,
    AiObservatoryDbContext db,
    FxRateProvider fx,
    IClock clock,
    ILogger<GitHubBillingSyncService> logger)
{
    /// <summary>Every GitHub charge is denominated in USD, whatever the payment method.</summary>
    private const string Currency = "USD";

    /// <summary>
    /// Which vendor and category a billing product books against.
    /// <para>
    /// Actions and Packages go to the pre-existing <c>github-actions</c> vendor under
    /// <c>ci</c>: that is CI compute and storage, exactly what the vendor was seeded for.
    /// Everything else goes to <c>github</c>, because booking Advanced Security or a Code
    /// Quality AI Credit against a vendor named "GitHub Actions" would misattribute it in
    /// every breakdown on the dashboard.
    /// </para>
    /// <para>
    /// <c>code_quality</c> earns <c>code-review</c> rather than a subscription line because
    /// Code Quality AI Credits are metered AI review spend — the same kind of charge as
    /// CodeRabbit, and the one line on the GitHub bill this project most needs visible.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, (string VendorKey, string CategoryKey)> ProductMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["actions"] = ("github-actions", "ci"),
            ["packages"] = ("github-actions", "ci"),
            ["code_quality"] = ("github", "code-review"),
            ["ghas"] = ("github", "subscription"),
        };

    /// <summary>
    /// Unrecognised products still land, under the GitHub vendor as a subscription. A new
    /// GitHub product line appearing on the bill must show up in the total even before
    /// anyone teaches this map about it — a silently dropped line is exactly the
    /// understatement this service exists to fix.
    /// </summary>
    private static readonly (string VendorKey, string CategoryKey) Fallback = ("github", "subscription");

    public async Task<int> SyncAsync(CancellationToken ct = default)
    {
        var currentYear = clock.GetCurrentInstant().InUtc().Year;

        // The previous year as well as this one, so a January run still backfills the
        // December that closed a few days earlier. Older years are not re-fetched: their
        // entries are already written and a closed month's usage cannot change.
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

        var vendors = await db.SpendVendors.AsNoTracking()
            .ToDictionaryAsync(v => v.Key, v => v.Id, StringComparer.OrdinalIgnoreCase, ct);
        var categories = await db.SpendCategories.AsNoTracking()
            .ToDictionaryAsync(c => c.Key, c => c.Id, StringComparer.OrdinalIgnoreCase, ct);

        var written = 0;
        foreach (var line in Aggregate(items))
        {
            if (await UpsertAsync(line, vendors, categories, ct))
            {
                written++;
            }
        }

        logger.LogInformation("GitHub billing: {Written} entries written or updated", written);
        return written;
    }

    /// <summary>
    /// Collapses the raw response to one line per (month, product, SKU).
    /// <para>
    /// GitHub reports <c>repositoryName</c> per item, so the same SKU can in principle arrive
    /// more than once in a month — one row per repository that incurred it. Summing here
    /// keeps <see cref="EntryKeyFor"/> unique whether or not that happens, rather than
    /// relying on today's single-row-per-SKU shape holding forever.
    /// </para>
    /// Zero-net lines are dropped: they are fully-discounted allowance usage that was never
    /// billed, and <c>CK_SpendEntry_Amount_NonZero</c> would reject them anyway.
    /// </summary>
    private static IEnumerable<BillingLine> Aggregate(IEnumerable<GitHubBillingUsageItem> items) =>
        items
            .GroupBy(i => (Month: LocalDate.FromDateTime(i.Date.UtcDateTime.Date).With(DateAdjusters.StartOfMonth),
                           i.Product,
                           i.Sku))
            .Select(g => new BillingLine(g.Key.Month, g.Key.Product, g.Key.Sku, g.Sum(i => i.NetAmount)))
            .Where(l => l.NetAmount != 0m)
            .OrderBy(l => l.Month).ThenBy(l => l.Product, StringComparer.Ordinal);

    /// <returns><c>true</c> when a row was inserted or its amount changed.</returns>
    private async Task<bool> UpsertAsync(
        BillingLine line,
        Dictionary<string, Guid> vendors,
        Dictionary<string, Guid> categories,
        CancellationToken ct)
    {
        var (vendorKey, categoryKey) = ProductMap.TryGetValue(line.Product, out var mapped) ? mapped : Fallback;

        if (!vendors.TryGetValue(vendorKey, out var vendorId) ||
            !categories.TryGetValue(categoryKey, out var categoryId))
        {
            // Seeded rows, so this only fires if someone hard-deleted one. Skip the line
            // rather than fail the sync: the remaining products still belong in the ledger.
            logger.LogWarning(
                "GitHub billing: no vendor '{VendorKey}' or category '{CategoryKey}' for product {Product}",
                vendorKey, categoryKey, line.Product);
            return false;
        }

        decimal rate;
        try
        {
            rate = await fx.GetGbpRateOnAsync(Currency, line.Month, ct);
        }
        catch (FxUnavailableException ex)
        {
            // USD has a static fallback inside FxRateProvider, so reaching here means a
            // genuinely unresolvable rate. Skip this line rather than freeze a wrong one.
            logger.LogWarning(ex, "GitHub billing: no FX rate for {Month}", line.Month);
            return false;
        }

        var entryKey = EntryKeyFor(line);
        var amountGbp = decimal.Round(line.NetAmount * rate, 4, MidpointRounding.ToEven);

        var existing = await db.SpendEntries
            .FirstOrDefaultAsync(e => e.Source == SpendSource.Api && e.EntryKey == entryKey, ct);

        if (existing is null)
        {
            db.SpendEntries.Add(new SpendEntry
            {
                OccurredOn = line.Month,
                VendorId = vendorId,
                CategoryId = categoryId,
                Amount = line.NetAmount,
                Currency = Currency,
                AmountGbp = amountGbp,
                FxRate = rate,
                Description = Truncate(line.Sku),
                Source = SpendSource.Api,
                EntryKey = entryKey,
                RecordedAt = clock.GetCurrentInstant(),
            });
        }
        else if (existing.Amount != line.NetAmount)
        {
            // Only the amount moves. Vendor and category are deliberately left alone so a
            // manual recategorisation on the dashboard survives the next sync.
            existing.Amount = line.NetAmount;
            existing.AmountGbp = amountGbp;
            existing.FxRate = rate;
            existing.RecordedAt = clock.GetCurrentInstant();
        }
        else
        {
            return false;
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Identity of the billing line, excluding the amount — that is the whole point, since
    /// the amount is what changes as an open month accrues.
    /// </summary>
    private static string EntryKeyFor(BillingLine line) =>
        Truncate($"github:{line.Month.ToString("yyyy-MM", CultureInfo.InvariantCulture)}:{line.Product}:{line.Sku}");

    /// <summary>Both Description and EntryKey are varchar(200); a long SKU must not fail the write.</summary>
    private static string Truncate(string value) => value.Length <= 200 ? value : value[..200];

    private sealed record BillingLine(LocalDate Month, string Product, string Sku, decimal NetAmount);
}
