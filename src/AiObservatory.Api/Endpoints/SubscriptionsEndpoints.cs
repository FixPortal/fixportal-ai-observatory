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

        app.MapPost("/subscriptions", async (SubscriptionRequest req, AiObservatoryDbContext db) =>
        {
            if (!Enum.TryParse<Provider>(req.Provider, ignoreCase: true, out var provider)
                || !Enum.IsDefined(provider))
            {
                return Results.BadRequest($"Unknown provider: {req.Provider}");
            }

            if (req.BillingDay < 1 || req.BillingDay > 31)
            {
                return Results.BadRequest("BillingDay must be between 1 and 31");
            }

            if (req.CostAmount < 0)
            {
                return Results.BadRequest("CostAmount must be non-negative");
            }

            var sub = new Subscription
            {
                Provider = provider,
                Name = req.Name,
                CostAmount = req.CostAmount,
                Currency = req.Currency,
                BillingDay = req.BillingDay,
                ActiveFrom = req.ActiveFrom,
                ActiveTo = req.ActiveTo
            };

            db.Subscriptions.Add(sub);
            await db.SaveChangesAsync();
            return Results.CreatedAtRoute("GetSubscriptionById", new { id = sub.Id }, sub);
        });

        app.MapPut("/subscriptions/{id:guid}", async (Guid id, SubscriptionRequest req, AiObservatoryDbContext db) =>
        {
            if (!Enum.TryParse<Provider>(req.Provider, ignoreCase: true, out var provider)
                || !Enum.IsDefined(provider))
            {
                return Results.BadRequest($"Unknown provider: {req.Provider}");
            }

            if (req.BillingDay < 1 || req.BillingDay > 31)
            {
                return Results.BadRequest("BillingDay must be between 1 and 31");
            }

            if (req.CostAmount < 0)
            {
                return Results.BadRequest("CostAmount must be non-negative");
            }

            var sub = await db.Subscriptions.FindAsync(id);
            if (sub is null)
            {
                return Results.NotFound();
            }

            sub.Provider = provider;
            sub.Name = req.Name;
            sub.CostAmount = req.CostAmount;
            sub.Currency = req.Currency;
            sub.BillingDay = req.BillingDay;
            sub.ActiveFrom = req.ActiveFrom;
            sub.ActiveTo = req.ActiveTo;
            await db.SaveChangesAsync();
            return Results.Ok(sub);
        });

        app.MapPatch("/subscriptions/{id:guid}/extra-usage",
            async (Guid id, ExtraUsageRequest req, AiObservatoryDbContext db) =>
        {
            var sub = await db.Subscriptions.FindAsync(id);
            if (sub is null)
            {
                return Results.NotFound();
            }

            sub.ExtraUsageCost = req.Amount;
            await db.SaveChangesAsync();
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
