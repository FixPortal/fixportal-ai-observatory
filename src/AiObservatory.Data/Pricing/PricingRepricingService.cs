using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiObservatory.Data.Pricing;

public sealed class PricingRepricingService(
    AiObservatoryDbContext db,
    IUsageRepository repository,
    UsagePriceResolver resolver,
    PricingSnapshotStore store,
    ILogger<PricingRepricingService>? logger = null
)
{
    private readonly ILogger<PricingRepricingService> _logger = logger ?? NullLogger<PricingRepricingService>.Instance;

    public async Task RepriceProviderAsync(Provider provider, CancellationToken cancellationToken = default)
    {
        // Called two ways: from a catalog activation's beforeCommit — where the activation already
        // holds the exclusive advisory lock inside its transaction — and standalone at startup. The
        // standalone path opens its own transaction and takes the shared activation lock, so an
        // activation cannot commit a new catalog mid-pass and leave this pass writing prices
        // resolved from the catalog it read before the change.
        IDbContextTransaction? transaction = null;
        if (db.Database.CurrentTransaction is null)
        {
            transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await store.AcquireSharedActivationLocksAsync(provider, cancellationToken);
        }

        await using (transaction)
        {
            try
            {
                await RepriceLockedAsync(provider, cancellationToken);
                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }
            }
            catch
            {
                if (transaction is not null)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    db.ChangeTracker.Clear();
                }

                throw;
            }
        }
    }

    private async Task RepriceLockedAsync(Provider provider, CancellationToken cancellationToken)
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
        // Within one pass the snapshot rows for a source cannot change: the pass runs inside a
        // transaction holding the shared activation lock (or the activation's own exclusive lock),
        // which blocks any catalog activation until the pass commits. Reading them once per source
        // rather than once per event is the only thing this local does; it dies with the pass, so
        // no later pass can see a stale catalog. The effective-date filter still runs per event.
        var snapshotsBySourceId = new Dictionary<string, List<PricingSnapshot>>(StringComparer.Ordinal);
        foreach (var usage in events)
        {
            var quote = await resolver.ResolveAsync(usage, snapshotsBySourceId, cancellationToken);
            if (quote is null && usage.CostUsd is not null)
            {
                // No snapshot covers this event (e.g. a model every retained catalog has dropped).
                // An unresolvable event keeps its last known price and basis — blanking a figure
                // we once knew converts priced history into "Not reported" on both the row and
                // its aggregate.
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
