using System.Globalization;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Text;

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
                if (!seenSessionIds.Add(s.SessionId))
                {
                    return Results.BadRequest($"Duplicate SessionId in batch: '{s.SessionId}'");
                }
            }

            var now = clock.GetCurrentInstant();
            var sessionIds = req.Sessions.Select(s => s.SessionId).ToList();
            var existing = await db.ClaudeActivitySessions
                .Where(s => sessionIds.Contains(s.SessionId))
                .ToDictionaryAsync(s => s.SessionId, ct);

            var upserted = 0;
            foreach (var s in req.Sessions)
            {
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

            await db.SaveChangesAsync(ct);
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
                .Where(s => s.StartedAt >= startInstant && s.StartedAt < endInstant)
                .Select(s => new { s.StartedAt, s.ActiveSeconds })
                .ToListAsync(ct);

            // In-memory grouping: LocalDatePattern.Iso.Format(Instant) isn't SQL-
            // translatable, and this is personal-scale data (one user's local
            // sessions), so a client-side GroupBy is fine — revisit with a SQL
            // GROUP BY if row count ever grows past a single user's history.
            var byDate = sessions
                .GroupBy(s => LocalDatePattern.Iso.Format(s.StartedAt.InUtc().Date))
                .Select(g => new DailyActivityResponse(g.Key, g.Sum(s => s.ActiveSeconds)))
                .OrderBy(d => d.Date)
                .ToList();

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

public sealed record DailyActivityResponse(string Date, long ActiveSeconds);

public sealed record ProjectActivityResponse(string Project, int SessionCount, long ActiveSeconds, double SharePercent);
