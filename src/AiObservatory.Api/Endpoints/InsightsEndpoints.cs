using AiObservatory.Api.Services.Intelligence;
using AiObservatory.Data;
using AiObservatory.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace AiObservatory.Api.Endpoints;

public static class InsightsEndpoints
{
    public static IEndpointRouteBuilder MapInsightsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/insights", async (AiObservatoryDbContext db) =>
        {
            var data = await db.Insights.AsNoTracking()
                .OrderByDescending(i => i.GeneratedAt)
                .Take(50)
                .Select(i => new
                {
                    i.Id,
                    generatedAt = i.GeneratedAt.ToString(),
                    insightType = i.InsightType.ToString().ToLower(),
                    i.Title,
                    i.Body,
                    i.Data,
                    acknowledged = i.AcknowledgedAt != null
                })
                .ToListAsync();
            return Results.Ok(data);
        });

        // Clear ALL insights (admin-key gated; it's a DELETE). Used for a fresh start
        // before regenerating. Irreversible.
        app.MapDelete("/insights", async (AiObservatoryDbContext db, CancellationToken ct) =>
        {
            var deleted = await db.Insights.ExecuteDeleteAsync(ct);
            return Results.Ok(new { deleted });
        });

        // Generate insights on demand for the current UTC date (same path the daily
        // worker runs). Admin-key gated (POST). Returns how many were written.
        app.MapPost("/insights/generate", async (
            IInsightGenerator generator, IClock clock, CancellationToken ct) =>
        {
            var date = clock.GetCurrentInstant().InUtc().Date;
            var generated = await generator.GenerateForDateAsync(date, ct);
            return Results.Ok(new { generated });
        });

        app.MapPost("/insights/{id:guid}/acknowledge", async (
            Guid id, IUsageRepository repo, IClock clock) =>
        {
            await repo.AcknowledgeInsightAsync(id, clock.GetCurrentInstant());
            return Results.NoContent();
        });

        app.MapPost("/insights/{id:guid}/explain", async (
            Guid id,
            AiObservatoryDbContext db,
            AnthropicIntelligenceClient client,
            CancellationToken ct) =>
        {
            var insight = await db.Insights
                .AsNoTracking()
                .Where(i => i.Id == id)
                .Select(i => new { i.Title, i.Body })
                .FirstOrDefaultAsync(ct);

            if (insight is null)
            {
                return Results.NotFound();
            }
            if (!client.IsConfigured)
            {
                return Results.Problem("ANTHROPIC_API_KEY is not configured.", statusCode: 503);
            }

            var explanation = await client.GenerateExplanationAsync(insight.Title, insight.Body, ct);
            return Results.Ok(new { explanation });
        });

        return app;
    }
}
