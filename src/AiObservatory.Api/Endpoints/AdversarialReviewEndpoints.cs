using AiObservatory.Api.Services;
using AiObservatory.Data.Repositories;

namespace AiObservatory.Api.Endpoints;

public static class AdversarialReviewEndpoints
{
    public static IEndpointRouteBuilder MapAdversarialReviewEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/adversarial-review/runs", async (
            AdversarialReviewRunRequest req,
            AdversarialReviewService svc,
            HttpContext ctx) =>
        {
            return await svc.RecordRunAsync(req, ctx.RequestAborted);
        }).WithName("RecordAdversarialReviewRun");

        app.MapGet("/adversarial-review/runs", async (
            IAdversarialReviewRepository repo,
            HttpContext ctx) =>
        {
            var runs = await repo.GetRunsAsync(ctx.RequestAborted);
            return Results.Ok(runs.Select(r => new
            {
                r.Id,
                r.Reviewer,
                r.Model,
                r.InputTokens,
                r.OutputTokens,
                r.CostUsd,
                r.ReviewDurationMs,
                r.IssuesRaised,
                r.IssuesAccepted,
                r.CostPerAcceptedFinding,
                r.RunId,
                recordedAt = r.RecordedAt.ToString()
            }));
        });

        app.MapGet("/adversarial-review/stats", async (
            IAdversarialReviewRepository repo,
            HttpContext ctx) =>
        {
            var stats = await repo.GetStatsAsync(ctx.RequestAborted);
            return Results.Ok(stats);
        });

        return app;
    }
}
