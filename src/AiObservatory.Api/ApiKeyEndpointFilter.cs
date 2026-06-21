using System.Security.Cryptography;
using System.Text;
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
            var matchesReadonly = FixedTimeEquals(providedGetString, expectedReadonly);
            var matchesAdmin = !string.IsNullOrEmpty(expectedAdmin) && FixedTimeEquals(providedGetString, expectedAdmin);

            if (!matchesReadonly && !matchesAdmin)
            {
                return Results.Unauthorized();
            }

            return await next(context);
        }

        if (string.IsNullOrEmpty(expectedAdmin))
        {
            return Results.StatusCode(503);
        }

        if (!context.HttpContext.Request.Headers.TryGetValue("X-Observatory-Key", out var provided)
            || !FixedTimeEquals(provided.ToString(), expectedAdmin))
        {
            return Results.Unauthorized();
        }

        return await next(context);
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);
        if (aBytes.Length != bBytes.Length)
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
}
