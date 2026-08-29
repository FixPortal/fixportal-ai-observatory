using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using Microsoft.EntityFrameworkCore;

namespace AiObservatory.Data.Pricing;

public sealed class PricingRepricingService(
    AiObservatoryDbContext db,
    IUsageRepository repository,
    UsagePriceResolver resolver
)
{
    public async Task RepriceProviderAsync(Provider provider, CancellationToken cancellationToken = default)
    {
        // ponytail: pricing changes are rare and Observatory volume is modest; target by effective date/model if this scan is measured as slow.
        var events = await db
            .UsageEvents.AsNoTracking()
            .Where(usage =>
                usage.Provider == provider
                && (usage.CostBasis == CostBasis.ListPriceEstimate || usage.CostBasis == CostBasis.Notional)
            )
            .OrderBy(usage => usage.Id)
            .ToListAsync(cancellationToken);
        // Within one pass the snapshot rows for a source cannot change: an activation holds the advisory
        // lock across its whole repricing, and a standalone pass reprices what it read. Reading them once
        // per source rather than once per event is the only thing this local does; it dies with the pass,
        // so no later pass can see a stale catalog. The effective-date filter still runs per event.
        var snapshotsBySourceId = new Dictionary<string, List<PricingSnapshot>>(StringComparer.Ordinal);
        foreach (var usage in events)
        {
            var quote = await resolver.ResolveAsync(usage, snapshotsBySourceId, cancellationToken);
            if (usage.CostUsd != quote?.CostUsd || usage.CacheSavingsUsd != quote?.CacheSavingsUsd)
            {
                await repository.UpdateEventPricingAsync(usage.Id, quote, cancellationToken);
            }
        }
    }
}
