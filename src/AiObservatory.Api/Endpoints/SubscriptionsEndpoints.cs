using AiObservatory.Data;
using AiObservatory.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace AiObservatory.Api.Endpoints;

public static class SubscriptionsEndpoints
{
    public static IEndpointRouteBuilder MapSubscriptionsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/subscriptions", async (AiObservatoryDbContext db) =>
            Results.Ok(await db.Subscriptions.AsNoTracking().ToListAsync()));

        app.MapGet("/subscriptions/{id:guid}", async (Guid id, AiObservatoryDbContext db) =>
        {
            var sub = await db.Subscriptions.FindAsync(id);
            return sub is not null ? Results.Ok(sub) : Results.NotFound();
        }).WithName("GetSubscriptionById");

        app.MapPost("/subscriptions", async (SubscriptionRequest req, AiObservatoryDbContext db, CancellationToken ct) =>
        {
            if (!Enum.TryParse<Provider>(req.Provider, ignoreCase: true, out var provider)
                || !Enum.IsDefined(provider))
            {
                return Results.BadRequest($"Unknown provider: {req.Provider}");
            }

            if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 200)
            {
                return Results.BadRequest("Name is required and must be 200 characters or fewer");
            }

            if (req.BillingDay < 1 || req.BillingDay > 31)
            {
                return Results.BadRequest("BillingDay must be between 1 and 31");
            }

            if (req.CostAmount < 0)
            {
                return Results.BadRequest("CostAmount must be non-negative");
            }

            var currencyUpper = (req.Currency ?? "").ToUpperInvariant();
            if (currencyUpper != "GBP" && currencyUpper != "USD")
            {
                return Results.BadRequest("Currency must be GBP or USD");
            }

            var sub = new Subscription
            {
                Provider = provider,
                Name = req.Name,
                CostAmount = req.CostAmount,
                Currency = currencyUpper,
                BillingDay = req.BillingDay,
                ActiveFrom = req.ActiveFrom,
                ActiveTo = req.ActiveTo
            };

            db.Subscriptions.Add(sub);
            await db.SaveChangesAsync(ct);
            return Results.CreatedAtRoute("GetSubscriptionById", new { id = sub.Id }, sub);
        });

        app.MapPut("/subscriptions/{id:guid}", async (Guid id, SubscriptionRequest req, AiObservatoryDbContext db, CancellationToken ct) =>
        {
            if (!Enum.TryParse<Provider>(req.Provider, ignoreCase: true, out var provider)
                || !Enum.IsDefined(provider))
            {
                return Results.BadRequest($"Unknown provider: {req.Provider}");
            }

            if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 200)
            {
                return Results.BadRequest("Name is required and must be 200 characters or fewer");
            }

            if (req.BillingDay < 1 || req.BillingDay > 31)
            {
                return Results.BadRequest("BillingDay must be between 1 and 31");
            }

            if (req.CostAmount < 0)
            {
                return Results.BadRequest("CostAmount must be non-negative");
            }

            var currencyUpper = (req.Currency ?? "").ToUpperInvariant();
            if (currencyUpper != "GBP" && currencyUpper != "USD")
            {
                return Results.BadRequest("Currency must be GBP or USD");
            }

            var sub = await db.Subscriptions.FindAsync([id], ct);
            if (sub is null)
            {
                return Results.NotFound();
            }

            sub.Provider = provider;
            sub.Name = req.Name;
            sub.CostAmount = req.CostAmount;
            sub.Currency = currencyUpper;
            sub.BillingDay = req.BillingDay;
            sub.ActiveFrom = req.ActiveFrom;
            sub.ActiveTo = req.ActiveTo;
            await db.SaveChangesAsync(ct);
            return Results.Ok(sub);
        });

        app.MapPatch("/subscriptions/{id:guid}/extra-usage",
            async (Guid id, ExtraUsageRequest req, AiObservatoryDbContext db, CancellationToken ct) =>
        {
            // Reject a negative extra-usage amount: it would deflate the subscription's
            // total spend, its % of budget, and the over-budget flag (frontend guard alone
            // is bypassable — a typed "-5" passes the input's min="0").
            if (req.Amount is < 0)
            {
                return Results.BadRequest("Amount must be non-negative");
            }

            var sub = await db.Subscriptions.FindAsync([id], ct);
            if (sub is null)
            {
                return Results.NotFound();
            }

            sub.ExtraUsageCost = req.Amount;
            await db.SaveChangesAsync(ct);
            return Results.Ok(sub);
        });

        app.MapDelete("/subscriptions/{id:guid}", async (Guid id, AiObservatoryDbContext db) =>
        {
            await db.Subscriptions.Where(s => s.Id == id).ExecuteDeleteAsync();
            return Results.NoContent();
        });

        return app;
    }
}

public record SubscriptionRequest(
    string Provider,
    string Name,
    decimal CostAmount,
    string Currency,
    int BillingDay,
    LocalDate ActiveFrom,
    LocalDate? ActiveTo = null
);

public sealed record ExtraUsageRequest(decimal? Amount);
