using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace AiObservatory.Api;

public class ApiKeyEndpointFilter(IConfiguration config, IHostEnvironment env) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        // A human signed in via Entra (JWT bearer) gets full access — read and write.
        // Machine callers (the observe-stop / sweeper / gemini-review hooks) carry no
        // token and fall through to the API-key checks below.
        if (context.HttpContext.User.Identity?.IsAuthenticated == true)
        {
            return await next(context);
        }

        var method = context.HttpContext.Request.Method;
        var isGet = HttpMethods.IsGet(method);

        var expectedAdmin = config["OBSERVATORY_API_KEY"];
        var expectedReadonly = config["OBSERVATORY_READONLY_API_KEY"];

        if (isGet)
        {
            if (string.IsNullOrEmpty(expectedReadonly))
            {
                // A configured admin key still works on GET even when the readonly key
                // hasn't been set — otherwise admin-only routes (e.g. the Activity
                // endpoints) become unreachable in a deploy that never set
                // OBSERVATORY_READONLY_API_KEY, despite the caller holding a valid
                // admin key. Once an admin key exists to check against, a non-matching
                // key is a plain auth failure (401), not a 503 — the 503 fallback below
                // is reserved for the case where no key at all is configured.
                if (!string.IsNullOrEmpty(expectedAdmin))
                {
                    if (context.HttpContext.Request.Headers.TryGetValue("X-Observatory-Key", out var providedAdminKey)
                        && ApiKeyComparer.FixedTimeEquals(providedAdminKey.ToString(), expectedAdmin))
                    {
                        return await next(context);
                    }

                    return Results.Unauthorized();
                }

                // Fail closed: a missing read-only key must NOT silently open the GET
                // surface (raw usage telemetry) to anonymous callers. Only local dev is
                // allowed to run keyless; any other environment treats the absent key as
                // a misconfiguration and refuses, mirroring the admin-path 503 below.
                return env.IsDevelopment()
                    ? await next(context)
                    : Results.StatusCode(503);
            }

            if (!context.HttpContext.Request.Headers.TryGetValue("X-Observatory-Key", out var providedGet))
            {
                return Results.Unauthorized();
            }

            var providedGetString = providedGet.ToString();
            var matchesReadonly = ApiKeyComparer.FixedTimeEquals(providedGetString, expectedReadonly);
            var matchesAdmin = !string.IsNullOrEmpty(expectedAdmin) && ApiKeyComparer.FixedTimeEquals(providedGetString, expectedAdmin);

            if (!matchesReadonly && !matchesAdmin)
            {
                return Results.Unauthorized();
            }

            return await next(context);
        }

        if (string.IsNullOrEmpty(expectedAdmin))
        {
            // No admin key configured. Mirror the GET branch (and AdminOnlyApiKeyEndpointFilter):
            // a keyless local dev box still serves writes so machine callers (observe-stop /
            // sweeper / gemini hooks) POSTing without a token work; any other environment treats
            // the absent key as a misconfiguration and refuses.
            return env.IsDevelopment()
                ? await next(context)
                : Results.StatusCode(503);
        }

        if (!context.HttpContext.Request.Headers.TryGetValue("X-Observatory-Key", out var provided)
            || !ApiKeyComparer.FixedTimeEquals(provided.ToString(), expectedAdmin))
        {
            return Results.Unauthorized();
        }

        return await next(context);
    }
}
