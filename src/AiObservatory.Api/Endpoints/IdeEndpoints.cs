using AiObservatory.Api.Routing;
using NodaTime;
using System.Text.Json;

namespace AiObservatory.Api.Endpoints;

public static class IdeEndpoints
{
    public static void MapIdeEndpoints(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/routing-snapshot", GetRoutingSnapshot);

    internal static IResult GetRoutingSnapshot(HttpContext context, RoutingCatalogService catalog, IClock clock)
    {
        var snapshot = catalog.GetSnapshot(clock.GetCurrentInstant());
        var etag = $"W/\"{snapshot.SnapshotId}\"";
        context.Response.Headers.ETag = etag;
        if (context.Request.Headers.IfNoneMatch.Any(value => string.Equals(value, etag, StringComparison.Ordinal)))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }
        return Results.Bytes(
            JsonSerializer.SerializeToUtf8Bytes(snapshot, RoutingCatalogService.SerializerOptions),
            "application/json"
        );
    }
}
