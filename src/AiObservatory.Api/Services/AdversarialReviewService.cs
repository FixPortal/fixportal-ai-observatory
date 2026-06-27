using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using NodaTime;

namespace AiObservatory.Api.Services;

public class AdversarialReviewService(IAdversarialReviewRepository repo, IClock clock)
{
    private static readonly Dictionary<string, string> ModelAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sonnet"] = "claude-sonnet-4-6",
        ["claude-sonnet"] = "claude-sonnet-4-6",
        ["opus"] = "claude-opus-4-8",
        ["claude-opus"] = "claude-opus-4-8",
        ["haiku"] = "claude-haiku-4-5",
        ["claude-haiku"] = "claude-haiku-4-5",
    };

    // Aliases are Anthropic short-names; only resolve them for the anthropic
    // reviewer so a non-Anthropic run posting "opus"/"sonnet"/"haiku" is not
    // silently rewritten to a claude-* model id.
    private static string NormalizeModel(string model, string reviewer)
    {
        var trimmed = model.Trim();
        if (reviewer != "anthropic")
        {
            return trimmed;
        }
        return ModelAliases.TryGetValue(trimmed, out var canonical) ? canonical : trimmed;
    }

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

        if (req.Role is not ("reviewer" or "judge"))
        {
            return Results.BadRequest("Role must be 'reviewer' or 'judge'");
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

        if (req.Role == "judge" && (req.IssuesRaised != 0 || req.IssuesAccepted != 0))
        {
            return Results.BadRequest("Judge runs must have zero issue counts");
        }

        decimal? costPerAcceptedFinding = req.IssuesAccepted > 0
            ? req.CostUsd / req.IssuesAccepted
            : null;

        var reviewer = req.Reviewer.Trim().ToLowerInvariant();
        var run = new AdversarialReviewRun
        {
            Reviewer = reviewer,
            Model = NormalizeModel(req.Model, reviewer),
            InputTokens = req.InputTokens,
            OutputTokens = req.OutputTokens,
            CostUsd = req.CostUsd,
            ReviewDurationMs = req.ReviewDurationMs,
            IssuesRaised = req.IssuesRaised,
            IssuesAccepted = req.IssuesAccepted,
            CostPerAcceptedFinding = costPerAcceptedFinding,
            RunId = req.RunId.Trim(),
            Role = req.Role,
            Repo = req.Repo?.Trim(),
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
    string RunId,
    string Role,
    string? Repo
);
