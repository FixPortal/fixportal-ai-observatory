using AiObservatory.Data;
using AiObservatory.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AiObservatory.Api.Endpoints;

public static class BudgetRulesEndpoints
{
    public static IEndpointRouteBuilder MapBudgetRulesEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/budget-rules", async (AiObservatoryDbContext db) =>
            Results.Ok(await db.BudgetRules.AsNoTracking().ToListAsync()));

        app.MapGet("/budget-rules/{id:guid}", async (Guid id, AiObservatoryDbContext db) =>
        {
            var rule = await db.BudgetRules.FindAsync(id);
            return rule is not null ? Results.Ok(rule) : Results.NotFound();
        }).WithName("GetBudgetRuleById");

        app.MapPost("/budget-rules", async (CreateBudgetRuleRequest req, AiObservatoryDbContext db) =>
        {
            var rule = new BudgetRule
            {
                Provider = req.Provider,
                Period = req.Period,
                ThresholdUsd = req.ThresholdUsd,
            };
            db.BudgetRules.Add(rule);
            await db.SaveChangesAsync();
            return Results.CreatedAtRoute("GetBudgetRuleById", new { id = rule.Id }, rule);
        });

        app.MapDelete("/budget-rules/{id:guid}", async (Guid id, AiObservatoryDbContext db) =>
        {
            await db.BudgetRules.Where(r => r.Id == id).ExecuteDeleteAsync();
            return Results.NoContent();
        });

        return app;
    }
}

public sealed record CreateBudgetRuleRequest(Provider? Provider, BillingPeriod Period, decimal ThresholdUsd);
