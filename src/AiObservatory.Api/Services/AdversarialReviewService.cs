using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using NodaTime;

namespace AiObservatory.Api.Services;

public class AdversarialReviewService(IAdversarialReviewRepository repo, IClock clock)
{
    public async Task<IResult> RecordRunAsync(AdversarialReviewRunRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Reviewer))
        {
            return Results.BadRequest("Reviewer is required");
        }

        if (string.IsNullOrWhiteSpace(req.Model))
        {
            return Results.BadRequest("Model is required");
        }

        if (string.IsNullOrWhiteSpace(req.RunId))
        {
            return Results.BadRequest("RunId is required");
        }

        if (req.InputTokens < 0 || req.OutputTokens < 0)
        {
            return Results.BadRequest("Token counts must be non-negative");
        }

        if (req.CostUsd < 0)
        {
            return Results.BadRequest("CostUsd must be non-negative");
        }

        if (req.IssuesRaised < 0 || req.IssuesAccepted < 0)
        {
            return Results.BadRequest("Issue counts must be non-negative");
        }

        if (req.IssuesAccepted > req.IssuesRaised)
        {
            return Results.BadRequest("IssuesAccepted cannot exceed IssuesRaised");
        }

        decimal? costPerAcceptedFinding = req.IssuesAccepted > 0
            ? req.CostUsd / req.IssuesAccepted
            : null;

        var run = new AdversarialReviewRun
        {
            Reviewer = req.Reviewer.Trim().ToLowerInvariant(),
            Model = req.Model.Trim(),
            InputTokens = req.InputTokens,
            OutputTokens = req.OutputTokens,
            CostUsd = req.CostUsd,
            ReviewDurationMs = req.ReviewDurationMs,
            IssuesRaised = req.IssuesRaised,
            IssuesAccepted = req.IssuesAccepted,
            CostPerAcceptedFinding = costPerAcceptedFinding,
            RunId = req.RunId.Trim(),
            RecordedAt = clock.GetCurrentInstant()
        };

        var (id, isDuplicate) = await repo.RecordRunAsync(run, ct);

        return isDuplicate
            ? Results.Ok(new { Id = id, Duplicate = true })
            : Results.Created($"/api/adversarial-review/runs/{id}", new { Id = id });
    }
}

public record AdversarialReviewRunRequest(
    string EventType,
    string Reviewer,
    string Model,
    long InputTokens,
    long OutputTokens,
    decimal CostUsd,
    long ReviewDurationMs,
    int IssuesRaised,
    int IssuesAccepted,
    string RunId
);
