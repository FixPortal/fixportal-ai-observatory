using System.Text.Json;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;
using AiObservatory.Data.Repositories;
using Microsoft.Extensions.Options;
using NodaTime;

namespace AiObservatory.Api.Endpoints;

// Request records are instantiated by ASP.NET Core model binding.
// ReSharper disable ClassNeverInstantiated.Global

public static class EventsEndpoints
{
    public static void MapEventsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/events/{id:guid}", GetEventByIdAsync).WithName("GetEventById");
        app.MapPost("/events", RecordEventAsync);
        app.MapGet("/events", GetEventsAsync);
        app.MapPatch("/events/{eventKey}/cost", PatchEventCostAsync);
    }

    private static async Task<IResult> GetEventByIdAsync(Guid id, AiObservatoryDbContext db)
    {
        var evt = await db.UsageEvents.FindAsync(id);
        return evt is not null ? Results.Ok(evt) : Results.NotFound();
    }

    private static async Task<IResult> RecordEventAsync(
        UsageEventRequest req,
        IUsageRepository repo,
        IClock clock,
        IOptions<AnthropicPricingOptions> anthropicPricing,
        ILoggerFactory loggerFactory,
        HttpContext ctx
    )
    {
        if (
            !Enum.TryParse<Provider>(req.Provider, ignoreCase: true, out var provider)
            || !Enum.IsDefined(provider)
        )
        {
            return Results.BadRequest($"Unknown provider: {req.Provider}");
        }

        if (
            req.InputTokens < 0
            || req.OutputTokens < 0
            || req.CacheReadTokens < 0
            || req.CacheWriteTokens < 0
            || req.CacheWrite1hTokens < 0
            || req.CostUsd < 0
        )
        {
            return Results.BadRequest("Token counts and cost must be non-negative");
        }

        // CacheWrite1hTokens is a SUBSET of CacheWriteTokens, not a sibling total. Rejecting
        // an over-large one here is what lets the cost calculation derive the five-minute
        // remainder by subtraction; the same rule is enforced again as a check constraint.
        if (req.CacheWrite1hTokens > req.CacheWriteTokens)
        {
            return Results.BadRequest("CacheWrite1hTokens must not exceed CacheWriteTokens");
        }

        var rawPayload = req.RawPayload ?? "{}";
        try
        {
            JsonDocument.Parse(rawPayload).Dispose();
        }
        catch (JsonException)
        {
            return Results.BadRequest("RawPayload must be valid JSON");
        }

        var now = clock.GetCurrentInstant();
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

        // Anthropic events are priced HERE, from the shared rate table, and the caller's
        // CostUsd is ignored. Clients used to price their own events, which put a second
        // rate table (and a second copy of the resolution rules) in every producer — the
        // drift that made months of recorded spend wrong. Every other provider still
        // supplies its own cost: Copilot and Moonshot are flat-rate subscriptions with no
        // per-token price to apply, and Google/OpenAI report billed figures directly.
        var costUsd = req.CostUsd;
        if (provider == Provider.Anthropic)
        {
            var usageDate = occurredAt.InUtc().Date;
            var options = anthropicPricing.Value;
            // Model is optional on the wire; an absent one matches no prefix and prices at
            // the fallback, which the warning below makes visible rather than silent.
            var model = req.Model ?? string.Empty;

            // Resolved once and reused for both the warning and the rates — ResolveRates
            // would repeat the same prefix/date scan.
            var match = options.Match(model, usageDate);
            if (match is null)
            {
                // Fallback rates are a guess. Say so, with the model, rather than letting a
                // renamed model quietly accrue cost at Sonnet prices.
                loggerFactory
                    .CreateLogger("AiObservatory.Api.Pricing")
                    .LogWarning(
                        "No Anthropic pricing entry for model '{Model}' on {UsageDate}; using fallback rates. Add an entry to pricing.anthropic.json.",
                        model,
                        usageDate
                    );
            }

            costUsd = AnthropicPricingResolver.ComputeCost(
                match?.ToRates() ?? options.FallbackPricing,
                req.InputTokens,
                req.OutputTokens,
                req.CacheReadTokens,
                req.CacheWriteTokens,
                req.CacheWrite1hTokens
            );
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
            CacheWrite1hTokens = req.CacheWrite1hTokens,
            CostUsd = costUsd,
            RawPayload = rawPayload,
            EventKey = eventKey,
        };

        var result = await repo.RecordEventAsync(evt, ctx.RequestAborted);

        return result.IsDuplicate
            ? Results.Ok(new { Id = result.EventId, Duplicate = true })
            : Results.CreatedAtRoute(
                "GetEventById",
                new { id = result.EventId },
                new { Id = result.EventId }
            );
    }

    private static async Task<IResult> GetEventsAsync(
        string provider,
        IUsageRepository repo,
        CancellationToken ct,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int limit = 10_000
    )
    {
        if (
            !Enum.TryParse<Provider>(provider, ignoreCase: true, out var parsed)
            || !Enum.IsDefined(parsed)
        )
        {
            return Results.BadRequest($"Unknown provider: {provider}");
        }

        var fromInstant = from is { } f ? Instant.FromDateTimeOffset(f) : (Instant?)null;
        var toInstant = to is { } t ? Instant.FromDateTimeOffset(t) : (Instant?)null;
        var cappedLimit = Math.Clamp(limit, 1, 10_000);

        var events = await repo.GetEventsByProviderAsync(
            parsed,
            fromInstant,
            toInstant,
            cappedLimit,
            ct
        );
        return Results.Ok(events);
    }

    private static async Task<IResult> PatchEventCostAsync(
        string eventKey,
        string provider,
        UpdateEventCostRequest req,
        IUsageRepository repo,
        CancellationToken ct
    )
    {
        if (
            !Enum.TryParse<Provider>(provider, ignoreCase: true, out var parsed)
            || !Enum.IsDefined(parsed)
        )
        {
            return Results.BadRequest($"Unknown provider: {provider}");
        }

        if (req.CostUsd < 0)
        {
            return Results.BadRequest("CostUsd must be non-negative");
        }

        // Trim to match the stored key: POST persists req.EventKey.Trim(), so a padded
        // route value would otherwise miss the row and drop the cost correction as a 404.
        var result = await repo.PatchEventCostAsync(parsed, eventKey.Trim(), req.CostUsd, ct);

        return result is null
            ? Results.NotFound()
            : Results.Ok(
                new
                {
                    result.EventId,
                    result.OldCostUsd,
                    result.NewCostUsd,
                }
            );
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
    DateTimeOffset? OccurredAtUtc = null,
    // Optional and defaulted so producers that predate the TTL split keep working: omitting
    // it prices the whole cache write at the five-minute rate, exactly as before.
    // ReSharper disable once InconsistentNaming
    long CacheWrite1hTokens = 0
);

public sealed record UpdateEventCostRequest(decimal CostUsd);
