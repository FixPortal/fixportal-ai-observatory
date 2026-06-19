using AiObservatory.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AiObservatory.Data.Repositories;

public class AdversarialReviewRepository(AiObservatoryDbContext ctx) : IAdversarialReviewRepository
{
    public async Task<(Guid Id, bool IsDuplicate)> RecordRunAsync(
        AdversarialReviewRun run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        var existingId = await ctx.AdversarialReviewRuns.AsNoTracking()
            .Where(r => r.RunId == run.RunId)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(ct);

        if (existingId is not null)
        {
            return (existingId.Value, IsDuplicate: true);
        }

        ctx.AdversarialReviewRuns.Add(run);
        try
        {
            await ctx.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            ctx.Entry(run).State = EntityState.Detached;
            var winnerId = await ctx.AdversarialReviewRuns.AsNoTracking()
                .Where(r => r.RunId == run.RunId)
                .Select(r => r.Id)
                .FirstAsync(ct);
            return (winnerId, IsDuplicate: true);
        }

        return (run.Id, IsDuplicate: false);
    }

    public async Task<IReadOnlyList<AdversarialReviewRun>> GetRunsAsync(CancellationToken ct = default)
    {
        return await ctx.AdversarialReviewRuns
            .AsNoTracking()
            .OrderByDescending(r => r.RecordedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<AdversarialReviewStats>> GetStatsAsync(CancellationToken ct = default)
    {
        // Materialise then group in memory. This is a small audit table (one row
        // per reviewer per review run), so a server-side GROUP BY buys nothing —
        // and the previous IQueryable GroupBy projecting a record with a cast
        // inside Average (`(double)r.IssuesRaised`) did not translate to Postgres
        // and 500'd the endpoint, leaving the dashboard's review panel blank.
        // LINQ-to-Objects Average(decimal?) ignores nulls and yields null when all
        // are null, matching the intended semantics.
        var runs = await ctx.AdversarialReviewRuns.AsNoTracking().ToListAsync(ct);

        return runs
            .GroupBy(r => new { r.Reviewer, r.Model })
            .Select(g => new AdversarialReviewStats(
                g.Key.Reviewer,
                g.Key.Model,
                g.Count(),
                g.Average(r => r.CostUsd),
                g.Average(r => (double)r.IssuesRaised),
                g.Average(r => (double)r.IssuesAccepted),
                g.Average(r => r.CostPerAcceptedFinding)   // null when every run had accepted=0
            ))
            .OrderBy(s => s.Reviewer).ThenBy(s => s.Model)
            .ToList();
    }
}
