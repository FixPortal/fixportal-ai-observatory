using AiObservatory.Data;
using AiObservatory.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace AiObservatory.Api.Endpoints;

public static class CavemanEndpoints
{
    public static void MapCavemanEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/caveman-sessions", async (
            CavemanSessionsRequest req,
            AiObservatoryDbContext db,
            IClock clock,
            CancellationToken ct) =>
        {
            if (req.Sessions is not { Count: > 0 })
                return Results.Ok(new { Upserted = 0 });

            if (req.Sessions.Count > 1000)
                return Results.BadRequest("Cannot upsert more than 1000 sessions at once.");

            var now = clock.GetCurrentInstant();

            foreach (var s in req.Sessions)
            {
                if (string.IsNullOrWhiteSpace(s.SessionId) || s.SessionId.Length > 200)
                    return Results.BadRequest($"SessionId invalid: '{s.SessionId}'");
                if (s.OutputTokens < 0 || s.EstSavedTokens < 0 || s.EstSavedUsd < 0)
                    return Results.BadRequest("Token counts and cost must be non-negative.");
            }

            var sessionIds = req.Sessions.Select(s => s.SessionId).ToList();
            var existingIds = await db.CavemanSessions
                .Where(s => sessionIds.Contains(s.SessionId))
                .Select(s => s.SessionId)
                .ToHashSetAsync(ct);

            var upserted = 0;
            foreach (var s in req.Sessions)
            {
                var occurredAt = Instant.FromDateTimeOffset(s.OccurredAtUtc);
                if (occurredAt > now + Duration.FromMinutes(5))
                    return Results.BadRequest($"OccurredAtUtc must not be in the future: {s.OccurredAtUtc}");

                if (existingIds.Contains(s.SessionId))
                {
                    await db.CavemanSessions
                        .Where(x => x.SessionId == s.SessionId)
                        .ExecuteUpdateAsync(upd => upd
                            .SetProperty(p => p.OccurredAt, occurredAt)
                            .SetProperty(p => p.Mode, s.Mode)
                            .SetProperty(p => p.Model, s.Model)
                            .SetProperty(p => p.OutputTokens, s.OutputTokens)
                            .SetProperty(p => p.EstSavedTokens, s.EstSavedTokens)
                            .SetProperty(p => p.EstSavedUsd, s.EstSavedUsd), ct);
                }
                else
                {
                    db.CavemanSessions.Add(new CavemanSession
                    {
                        SessionId = s.SessionId,
                        OccurredAt = occurredAt,
                        Mode = s.Mode,
                        Model = s.Model,
                        OutputTokens = s.OutputTokens,
                        EstSavedTokens = s.EstSavedTokens,
                        EstSavedUsd = s.EstSavedUsd,
                    });
                }
                upserted++;
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok(new { Upserted = upserted });
        });

        app.MapGet("/caveman-stats", async (AiObservatoryDbContext db, CancellationToken ct) =>
        {
            var stats = await db.CavemanSessions
                .GroupBy(s => true)
                .Select(g => new CavemanStatsResponse(
                    g.Count(),
                    g.Sum(s => s.OutputTokens),
                    g.Sum(s => s.EstSavedTokens),
                    g.Sum(s => s.EstSavedUsd)))
                .FirstOrDefaultAsync(ct);

            return Results.Ok(stats ?? new CavemanStatsResponse(0, 0, 0, 0m));
        });
    }
}

public sealed record CavemanSessionRequest(
    string SessionId,
    DateTimeOffset OccurredAtUtc,
    string? Mode,
    string? Model,
    long OutputTokens,
    long EstSavedTokens,
    decimal EstSavedUsd
);

public sealed record CavemanSessionsRequest(List<CavemanSessionRequest> Sessions);

public sealed record CavemanStatsResponse(
    int Sessions,
    long TotalOutputTokens,
    long TotalEstSavedTokens,
    decimal TotalEstSavedUsd
);
