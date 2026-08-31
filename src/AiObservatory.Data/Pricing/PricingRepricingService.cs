using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiObservatory.Data.Pricing;

public sealed class PricingRepricingService(
    AiObservatoryDbContext db,
    IUsageRepository repository,
    UsagePriceResolver resolver,
    ILogger<PricingRepricingService>? logger = null
)
{
    private readonly ILogger<PricingRepricingService> _logger = logger ?? NullLogger<PricingRepricingService>.Instance;

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
            if (quote is null && usage.CostUsd is not null)
            {
                // No snapshot covers this event (e.g. history older than the earliest bundled
                // effectiveFrom, or a model the current catalog retired). An unresolvable event
                // keeps its last known price and basis — blanking a figure we once knew converts
                // priced history into "Not reported" on both the row and its aggregate.
                _logger.LogWarning(
                    "Repricing skipped for {EventId} ({Provider}/{Model}, {OccurredAt:u}): no pricing snapshot covers the event; keeping the existing cost of {CostUsd} USD.",
                    usage.Id,
                    usage.Provider,
                    usage.Model,
                    usage.OccurredAt,
                    usage.CostUsd
                );
                continue;
            }

            if (usage.CostUsd != quote?.CostUsd || usage.CacheSavingsUsd != quote?.CacheSavingsUsd)
            {
                await repository.UpdateEventPricingAsync(usage, quote, cancellationToken);
            }
        }
    }
}
