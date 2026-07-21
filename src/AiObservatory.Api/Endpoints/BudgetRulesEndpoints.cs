using AiObservatory.Data;
using AiObservatory.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AiObservatory.Api.Endpoints;

// Request records are instantiated by ASP.NET Core model binding.
// ReSharper disable ClassNeverInstantiated.Global

public static class BudgetRulesEndpoints
{
    // Returning the builder is the standard fluent endpoint-mapping convention.
    // ReSharper disable once UnusedMethodReturnValue.Global
    public static IEndpointRouteBuilder MapBudgetRulesEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/budget-rules", async (AiObservatoryDbContext db) =>
            Results.Ok(await db.BudgetRules.AsNoTracking().ToListAsync()));

        app.MapGet("/budget-rules/email-status", (IConfiguration config) =>
            Results.Ok(new { configured = !string.IsNullOrEmpty(config["BUDGET_ALERT_EMAIL_TO"]) }));

        app.MapGet("/budget-rules/{id:guid}", async (Guid id, AiObservatoryDbContext db) =>
        {
            var rule = await db.BudgetRules.FindAsync(id);
            return rule is not null ? Results.Ok(rule) : Results.NotFound();
        }).WithName("GetBudgetRuleById");

        app.MapPost("/budget-rules", async (CreateBudgetRuleRequest req, AiObservatoryDbContext db, CancellationToken ct) =>
        {
            // A zero/negative threshold is exceeded by any spend, so the rule fires a
            // spurious alert (plus Insight row + email) every period until deleted.
            if (req.ThresholdUsd <= 0)
            {
                return Results.BadRequest("ThresholdUsd must be greater than zero");
            }

            var rule = new BudgetRule
            {
                Provider = req.Provider,
                Period = req.Period,
                ThresholdUsd = req.ThresholdUsd,
            };
            db.BudgetRules.Add(rule);
            await db.SaveChangesAsync(ct);
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
