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
            .ToListAsync(cancellationToken);
        foreach (var usage in events)
        {
            var quote = await resolver.ResolveAsync(usage, cancellationToken);
            if (usage.CostUsd != quote?.CostUsd || usage.CacheSavingsUsd != quote?.CacheSavingsUsd)
            {
                await repository.UpdateEventPricingAsync(usage.Id, quote, cancellationToken);
            }
        }
    }
}
