using System.Globalization;
using System.Linq.Expressions;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Text;
using Npgsql;

namespace AiObservatory.Api.Endpoints;

public static class ActivityEndpoints
{
    public static void MapActivityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/activity/sessions", async (
            ActivitySessionsRequest req,
            AiObservatoryDbContext db,
            IClock clock,
            CancellationToken ct) =>
        {
            if (req.Sessions is not { Count: > 0 })
            {
                return Results.Ok(new { Upserted = 0 });
            }

            if (req.Sessions.Count > 1000)
            {
                return Results.BadRequest("Cannot upsert more than 1000 sessions at once.");
            }

            var now = clock.GetCurrentInstant();
            var seenSessionIds = new HashSet<string>();
            foreach (var s in req.Sessions)
            {
                if (string.IsNullOrWhiteSpace(s.SessionId) || s.SessionId.Length > 200)
                {
                    return Results.BadRequest($"SessionId invalid: '{s.SessionId}'");
                }
                if (string.IsNullOrWhiteSpace(s.Project) || s.Project.Length > 200)
                {
                    return Results.BadRequest($"Project invalid: '{s.Project}'");
                }
                if (s.ActiveSeconds < 0)
                {
                    return Results.BadRequest("ActiveSeconds must be non-negative.");
                }
                if (s.LastSeenAtUtc < s.StartedAtUtc)
                {
                    return Results.BadRequest($"LastSeenAtUtc must not be before StartedAtUtc: '{s.SessionId}'");
                }
                // Hoisted from the mutation loop: a bad row must fail BEFORE any earlier row
                // is committed (the update path uses ExecuteUpdateAsync, which writes
                // immediately), so validation happens entirely up-front.
                var startedAt = Instant.FromDateTimeOffset(s.StartedAtUtc);
                var lastSeenAt = Instant.FromDateTimeOffset(s.LastSeenAtUtc);
                if (startedAt > now + Duration.FromMinutes(5) || lastSeenAt > now + Duration.FromMinutes(5))
                {
                    return Results.BadRequest($"Timestamps must not be in the future: '{s.SessionId}'");
                }
                if (s.ActiveSeconds > (lastSeenAt - startedAt).TotalSeconds)
                {
                    return Results.BadRequest($"ActiveSeconds exceeds elapsed time for session '{s.SessionId}'");
                }
                if (!seenSessionIds.Add(s.SessionId))
                {
                    return Results.BadRequest($"Duplicate SessionId in batch: '{s.SessionId}'");
                }
            }

            var sessionIds = req.Sessions.Select(s => s.SessionId).ToList();
            var existing = await db.ClaudeActivitySessions
                .Where(s => sessionIds.Contains(s.SessionId))
                .ToDictionaryAsync(s => s.SessionId, ct);

            // One transaction so the ExecuteUpdateAsync writes and the deferred inserts commit
            // atomically — a unique-violation race on a concurrently-inserted SessionId rolls
            // the whole batch back rather than leaving it half-applied.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var upserted = 0;
            foreach (var s in req.Sessions)
            {
                var startedAt = Instant.FromDateTimeOffset(s.StartedAtUtc);
                var lastSeenAt = Instant.FromDateTimeOffset(s.LastSeenAtUtc);

                if (existing.TryGetValue(s.SessionId, out var current))
                {
                    if (!ShouldReplaceExisting(current, s.ActiveSeconds, lastSeenAt))
                    {
                        continue;
                    }

                    var (mergedAs, mergedLs) = MergeActivity(current, s.ActiveSeconds, lastSeenAt);
                    await db.ClaudeActivitySessions
                        .Where(x => x.SessionId == s.SessionId)
                        .ExecuteUpdateAsync(upd => upd
                            .SetProperty(p => p.ActiveSeconds, mergedAs)
                            .SetProperty(p => p.LastSeenAt, mergedLs), ct);
                }
                else
                {
                    db.ClaudeActivitySessions.Add(new ClaudeActivitySession
                    {
                        SessionId = s.SessionId,
                        Project = s.Project,
                        StartedAt = startedAt,
                        LastSeenAt = lastSeenAt,
                        ActiveSeconds = s.ActiveSeconds,
                        IngestedAt = now,
                    });
                }
                upserted++;
            }

            try
            {
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch (DbUpdateException ex) when (
                ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // A concurrent request inserted one of these SessionIds between the existence
                // snapshot and SaveChanges. The merge is monotonic, so a retry converges.
                await tx.RollbackAsync(ct);
                return Results.Conflict("Concurrent write to one or more sessions; retry.");
            }
            return Results.Ok(new { Upserted = upserted });
        });

        app.MapGet("/activity/daily", async (
            AiObservatoryDbContext db,
            IClock clock,
            string? from, string? to,
            CancellationToken ct) =>
        {
            var today = clock.GetCurrentInstant().InUtc().Date;
            if (!TryParseDateRange(from, to, today, out var start, out var end, out var error))
            {
                return error!;
            }

            var startInstant = start.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();
            var endInstant = end.PlusDays(1).AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();

            var sessions = await db.ClaudeActivitySessions
                .AsNoTracking()
                .Where(s => s.LastSeenAt > startInstant && s.StartedAt < endInstant)
                .Where(IsAllowedProjectPredicate)
                .Select(s => new ActivitySessionSlice(s.Project, s.StartedAt, s.LastSeenAt, s.ActiveSeconds))
                .ToListAsync(ct);

            var byDate = BuildDailyActivityResponses(sessions, start, end);

            return Results.Ok(byDate);
        }).AddEndpointFilter<AdminOnlyApiKeyEndpointFilter>();

        app.MapGet("/activity/by-project", async (
            AiObservatoryDbContext db,
            IClock clock,
            string? from, string? to,
            CancellationToken ct) =>
        {
            var today = clock.GetCurrentInstant().InUtc().Date;
            if (!TryParseDateRange(from, to, today, out var start, out var end, out var error))
            {
                return error!;
            }

            var startInstant = start.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();
            var endInstant = end.PlusDays(1).AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();

            var sessions = await db.ClaudeActivitySessions
                .AsNoTracking()
                .Where(s => s.StartedAt >= startInstant && s.StartedAt < endInstant)
                .Where(IsAllowedProjectPredicate)
                .Select(s => new { s.SessionId, s.Project, s.ActiveSeconds })
                .ToListAsync(ct);

            var totalSeconds = sessions.Sum(s => s.ActiveSeconds);

            var byProject = sessions
                .GroupBy(s => s.Project)
                .Select(g => new ProjectActivityResponse(
                    g.Key,
                    g.Select(s => s.SessionId).Distinct().Count(),
                    g.Sum(s => s.ActiveSeconds),
                    totalSeconds > 0 ? Math.Round(g.Sum(s => s.ActiveSeconds) * 100.0 / totalSeconds, 1) : 0))
                .OrderByDescending(p => p.ActiveSeconds)
                .ToList();

            return Results.Ok(byProject);
        }).AddEndpointFilter<AdminOnlyApiKeyEndpointFilter>();

        // One-off cleanup for pre-allowlist ingestion noise (scratch dirs, other
        // orgs, non-git leaf-folder fallbacks like "claude-review"). Irreversible.
        // Admin-key gated, mirrors the /aggregates provider-scoped reset.
        app.MapDelete("/activity/sessions/disallowed-projects", async (
            AiObservatoryDbContext db,
            CancellationToken ct) =>
        {
            var deleted = await DeleteDisallowedProjectSessionsAsync(db, ct);

            return Results.Ok(new { deletedSessions = deleted });
        }).AddEndpointFilter<AdminOnlyApiKeyEndpointFilter>();
    }

    // Only these two GitHub accounts are "real" projects for the dashboard — everything
    // else (scratch folders, other orgs, non-git dirs falling back to a leaf folder name)
    // is ingestion noise and stays out of the Project breakdown/treemap.
    public static readonly string[] AllowedProjectOwners = ["fix-portal", "chris-fixportal"];

    public sealed record ActivitySessionSlice(string Project, Instant StartedAt, Instant LastSeenAt, long ActiveSeconds);

    // Single source for the SQL-translatable allowlist rule, reused by every EF query
    // below instead of each carrying its own copy of the Any(...)/StartsWith(...) text.
    // IsAllowedProject (below) intentionally stays a separate, ordinal-comparison
    // implementation for the in-memory path — see its own comment.
    public static readonly Expression<Func<ClaudeActivitySession, bool>> IsAllowedProjectPredicate =
        s => AllowedProjectOwners.Any(o => s.Project == o || s.Project.StartsWith(o + "/"));

    // Negation of IsAllowedProjectPredicate, built once from the same expression body so
    // the "disallowed" side of the rule can never drift from the "allowed" side.
    private static readonly Expression<Func<ClaudeActivitySession, bool>> IsDisallowedProjectPredicate =
        Expression.Lambda<Func<ClaudeActivitySession, bool>>(
            Expression.Not(IsAllowedProjectPredicate.Body), IsAllowedProjectPredicate.Parameters[0]);

    // In-memory counterpart of IsAllowedProjectPredicate. Kept as its own implementation
    // (not derived from the expression above) because it deliberately uses an ordinal
    // StartsWith — culture-sensitive comparison would be wrong here — whereas SQL
    // translation via Npgsql is byte/ordinal-equivalent regardless.
    public static bool IsAllowedProject(string project) =>
        AllowedProjectOwners.Any(o => project == o || project.StartsWith(o + "/", StringComparison.Ordinal));

    public static Task<int> DeleteDisallowedProjectSessionsAsync(AiObservatoryDbContext db, CancellationToken ct = default) =>
        db.ClaudeActivitySessions
            .Where(IsDisallowedProjectPredicate)
            .ExecuteDeleteAsync(ct);

    public static List<DailyActivityResponse> BuildDailyActivityResponses(
        IEnumerable<ActivitySessionSlice> sessions,
        LocalDate start,
        LocalDate end)
    {
        var startInstant = start.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();
        var endInstant = end.PlusDays(1).AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();
        var slices = new List<(LocalDate Date, Instant Start, Instant End, long ActiveSeconds)>();

        foreach (var session in sessions.Where(s => IsAllowedProject(s.Project) && s.LastSeenAt > s.StartedAt))
        {
            var clippedStart = Max(session.StartedAt, startInstant);
            var clippedEnd = Min(session.LastSeenAt, endInstant);
            if (clippedEnd <= clippedStart)
            {
                continue;
            }

            var totalSeconds = (session.LastSeenAt - session.StartedAt).TotalSeconds;
            var cursor = clippedStart;
            while (cursor < clippedEnd)
            {
                var date = cursor.InUtc().Date;
                var nextMidnight = date.PlusDays(1).AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();
                var fragmentEnd = Min(clippedEnd, nextMidnight);
                var fragmentSeconds = (fragmentEnd - cursor).TotalSeconds;
                var activeSeconds = totalSeconds > 0
                    ? (long)Math.Round(session.ActiveSeconds * fragmentSeconds / totalSeconds, MidpointRounding.AwayFromZero)
                    : 0;
                slices.Add((date, cursor, fragmentEnd, activeSeconds));
                cursor = fragmentEnd;
            }
        }

        return slices
            .GroupBy(s => s.Date)
            .Select(g => new DailyActivityResponse(
                LocalDatePattern.Iso.Format(g.Key),
                g.Sum(s => s.ActiveSeconds),
                MergeIntervalSeconds(g.Select(s => (s.Start, s.End)))))
            .OrderBy(d => d.Date)
            .ToList();
    }

    public static bool ShouldReplaceExisting(ClaudeActivitySession existing, long newActiveSeconds, Instant newLastSeenAt) =>
        newActiveSeconds > existing.ActiveSeconds || newLastSeenAt > existing.LastSeenAt;

    // Per-field monotonic merge: ShouldReplaceExisting fires when EITHER field is newer,
    // so an update must not blindly overwrite both — that can regress whichever field the
    // incoming row didn't actually improve.
    public static (long ActiveSeconds, Instant LastSeenAt) MergeActivity(
        ClaudeActivitySession existing, long newActiveSeconds, Instant newLastSeenAt) =>
        (Math.Max(existing.ActiveSeconds, newActiveSeconds),
         newLastSeenAt > existing.LastSeenAt ? newLastSeenAt : existing.LastSeenAt);

    // Wall-clock time actually spent: merges overlapping session spans instead of
    // summing them, so N parallel sessions covering the same hour count as one hour.
    public static long MergeIntervalSeconds(IEnumerable<(Instant Start, Instant End)> spans)
    {
        var ordered = spans.Where(s => s.End > s.Start).OrderBy(s => s.Start).ToList();
        if (ordered.Count == 0)
        {
            return 0;
        }

        var total = Duration.Zero;
        var (curStart, curEnd) = ordered[0];
        foreach (var (start, end) in ordered.Skip(1))
        {
            if (start > curEnd)
            {
                total += curEnd - curStart;
                (curStart, curEnd) = (start, end);
            }
            else if (end > curEnd)
            {
                curEnd = end;
            }
        }
        total += curEnd - curStart;
        return (long)total.TotalSeconds;
    }

    private static Instant Min(Instant a, Instant b) => a < b ? a : b;

    private static Instant Max(Instant a, Instant b) => a > b ? a : b;

    public static bool TryParseDateRange(
        string? from, string? to, LocalDate today,
        out LocalDate start, out LocalDate end, out IResult? error)
    {
        error = null;
        start = today.PlusDays(-30);
        end = today;

        if (from is not null)
        {
            if (!DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fromDate))
            {
                error = Results.BadRequest("from must be yyyy-MM-dd");
                return false;
            }
            start = LocalDate.FromDateOnly(fromDate);
        }

        if (to is not null)
        {
            if (!DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var toDate))
            {
                error = Results.BadRequest("to must be yyyy-MM-dd");
                return false;
            }
            end = LocalDate.FromDateOnly(toDate);
        }

        if (start > end)
        {
            error = Results.BadRequest("from must be on or before to");
            return false;
        }

        return true;
    }
}

public sealed record ActivitySessionRequest(
    string SessionId,
    string Project,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset LastSeenAtUtc,
    long ActiveSeconds
);

public sealed record ActivitySessionsRequest(List<ActivitySessionRequest> Sessions);

public sealed record DailyActivityResponse(string Date, long ActiveSeconds, long WallClockSeconds);

public sealed record ProjectActivityResponse(string Project, int SessionCount, long ActiveSeconds, double SharePercent);
