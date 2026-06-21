using AiObservatory.Data.Entities;

namespace AiObservatory.Data.Repositories;

public record AdversarialReviewStats(
    string Reviewer,
    string Model,
    int RunCount,
    decimal AvgCostPerRun,
    double AvgIssuesRaised,
    double AvgIssuesAccepted,
    decimal? AvgCostPerAcceptedFinding
);

public interface IAdversarialReviewRepository
{
    Task<(Guid Id, bool IsDuplicate)> RecordRunAsync(AdversarialReviewRun run, CancellationToken ct = default);
    Task<IReadOnlyList<AdversarialReviewRun>> GetRunsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AdversarialReviewStats>> GetStatsAsync(CancellationToken ct = default);
}
