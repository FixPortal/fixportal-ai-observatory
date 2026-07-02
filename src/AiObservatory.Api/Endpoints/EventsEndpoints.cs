using System.Text.Json;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using NodaTime;

namespace AiObservatory.Api.Endpoints;

public static class EventsEndpoints
{
    public static void MapEventsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/events/{id:guid}", async (Guid id, AiObservatoryDbContext db) =>
        {
            var evt = await db.UsageEvents.FindAsync(id);
            return evt is not null ? Results.Ok(evt) : Results.NotFound();
        }).WithName("GetEventById");

        app.MapPost("/events", async (
            UsageEventRequest req,
            IUsageRepository repo,
            IClock clock,
            HttpContext ctx) =>
        {
            if (!Enum.TryParse<Provider>(req.Provider, ignoreCase: true, out var provider)
                || !Enum.IsDefined(provider))
            {
                return Results.BadRequest($"Unknown provider: {req.Provider}");
            }

            if (req.InputTokens < 0 || req.OutputTokens < 0 || req.CacheReadTokens < 0 || req.CacheWriteTokens < 0 || req.CostUsd < 0)
            {
                return Results.BadRequest("Token counts and cost must be non-negative");
            }

            var rawPayload = req.RawPayload ?? "{}";
            try
            { JsonDocument.Parse(rawPayload).Dispose(); }
            catch (JsonException) { return Results.BadRequest("RawPayload must be valid JSON"); }

            var now = clock.GetCurrentInstant();
            var ct = ctx.RequestAborted;

            var eventKey = string.IsNullOrWhiteSpace(req.EventKey) ? null : req.EventKey.Trim();
            if (eventKey is { Length: > 200 })
            {
                return Results.BadRequest("EventKey must be 200 characters or fewer");
            }

            // Backfilled events (e.g. from the local usage sweeper) carry the time the
            // usage actually happened so they aggregate onto the right day; live hooks
            // omit it and get the ingestion instant, as before.
            var occurredAt = req.OccurredAtUtc is { } supplied
                ? Instant.FromDateTimeOffset(supplied)
                : now;
            if (occurredAt > now + Duration.FromMinutes(5))
            {
                return Results.BadRequest("OccurredAtUtc must not be in the future");
            }

            var evt = new UsageEvent
            {
                Provider = provider,
                OccurredAt = occurredAt,
                IngestedAt = now,
                Model = req.Model,
                InputTokens = req.InputTokens,
                OutputTokens = req.OutputTokens,
                CacheReadTokens = req.CacheReadTokens,
                CacheWriteTokens = req.CacheWriteTokens,
                CostUsd = req.CostUsd,
                RawPayload = rawPayload,
                EventKey = eventKey
            };

            var result = await repo.RecordEventAsync(evt, ct);

            return result.IsDuplicate
                ? Results.Ok(new { Id = result.EventId, Duplicate = true })
                : Results.CreatedAtRoute("GetEventById", new { id = result.EventId }, new { Id = result.EventId });
        });

        app.MapGet("/events", async (
            string provider,
            IUsageRepository repo,
            CancellationToken ct,
            DateTimeOffset? from = null,
            DateTimeOffset? to = null,
            int limit = 10_000) =>
        {
            if (!Enum.TryParse<Provider>(provider, ignoreCase: true, out var p) || !Enum.IsDefined(p))
            {
                return Results.BadRequest($"Unknown provider: {provider}");
            }

            var fromInstant = from is { } f ? Instant.FromDateTimeOffset(f) : (Instant?)null;
            var toInstant = to is { } t ? Instant.FromDateTimeOffset(t) : (Instant?)null;
            var cappedLimit = Math.Clamp(limit, 1, 10_000);

            var events = await repo.GetEventsByProviderAsync(p, fromInstant, toInstant, cappedLimit, ct);
            return Results.Ok(events);
        });

        app.MapPatch("/events/{eventKey}/cost", async (
            string eventKey,
            string provider,
            UpdateEventCostRequest req,
            IUsageRepository repo,
            CancellationToken ct) =>
        {
            if (!Enum.TryParse<Provider>(provider, ignoreCase: true, out var p) || !Enum.IsDefined(p))
            {
                return Results.BadRequest($"Unknown provider: {provider}");
            }

            if (req.CostUsd < 0)
            {
                return Results.BadRequest("CostUsd must be non-negative");
            }

            // Trim to match the stored key: POST persists req.EventKey.Trim(), so a padded
            // route value would otherwise miss the row and drop the cost correction as a 404.
            var result = await repo.PatchEventCostAsync(p, eventKey.Trim(), req.CostUsd, ct);

            return result is null
                ? Results.NotFound()
                : Results.Ok(new { result.EventId, result.OldCostUsd, result.NewCostUsd });
        });
    }
}

public record UsageEventRequest(
    string Provider,
    string? Model,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    decimal CostUsd,
    string? RawPayload,
    string? EventKey = null,
    DateTimeOffset? OccurredAtUtc = null
);

public sealed record UpdateEventCostRequest(decimal CostUsd);
