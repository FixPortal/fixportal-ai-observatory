using System.Globalization;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Text;

namespace AiObservatory.Api.Endpoints;

public static class AggregatesEndpoints
{
    public static IEndpointRouteBuilder MapAggregatesEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/aggregates", async (
            AiObservatoryDbContext db,
            IClock clock,
            string? from, string? to) =>
        {
            var today = clock.GetCurrentInstant().InUtc().Date;
            LocalDate start, end;

            if (from is not null)
            {
                if (!DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fromDate))
                {
                    return Results.BadRequest("from must be yyyy-MM-dd");
                }

                start = LocalDate.FromDateOnly(fromDate);
            }
            else
            {
                start = today.PlusDays(-30);
            }

            if (to is not null)
            {
                if (!DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var toDate))
                {
                    return Results.BadRequest("to must be yyyy-MM-dd");
                }

                end = LocalDate.FromDateOnly(toDate);
            }
            else
            {
                end = today;
            }

            var data = await db.DailyAggregates
                .AsNoTracking()
                .Where(a => a.Date >= start && a.Date <= end)
                .OrderBy(a => a.Date).ThenBy(a => a.Provider).ThenBy(a => a.Model)
                .Select(a => new
                {
                    // Explicit ISO yyyy-MM-dd. LocalDate.ToString() with no pattern uses the
                    // server culture's long-date format ("29 May 2026"), which broke the
                    // frontend's slice/sort (it assumes ISO) and scrambled the chart axis.
                    date = LocalDatePattern.Iso.Format(a.Date),
                    provider = a.Provider.ToString().ToLower(),
                    a.Model,
                    a.InputTokens,
                    a.OutputTokens,
                    a.CacheReadTokens,
                    a.CacheWriteTokens,
                    a.CostUsd,
                    a.RequestCount
                })
                .ToListAsync();

            return Results.Ok(data);
        });

        // Provider-scoped reset. Deletes both raw events and the additive daily
        // aggregates for one provider so a clean backfill can follow. Admin-key
        // gated by ApiKeyEndpointFilter (it's a DELETE). Irreversible.
        app.MapDelete("/aggregates", async (
            string? provider,
            IUsageRepository repo,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(provider)
                || !Enum.TryParse<Provider>(provider, ignoreCase: true, out var parsed)
                || !Enum.IsDefined(parsed))
            {
                return Results.BadRequest("provider query parameter is required (anthropic|google|openai|copilot)");
            }

            var result = await repo.PurgeProviderAsync(parsed, ct);
            return Results.Ok(new
            {
                provider = parsed.ToString(),
                deletedEvents = result.DeletedEvents,
                deletedAggregates = result.DeletedAggregates
            });
        });

        return app;
    }
}
