using System.Net.Http.Headers;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace SaturdayScout.Player;

public static class PlayerBffEndpoints
{
    private const int MaximumJsonRequestBytes = 64 * 1024;

    public static WebApplication MapPlayerBffEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/bff/api/v1").RequireAuthorization("Player");

        api.MapGet("/leagues/me", (HttpContext context, IPlayerApiClient client, CancellationToken ct) =>
            ForwardAsync(context, client, HttpMethod.Get, "/api/v1/leagues/me", null, ct));
        api.MapGet("/leagues/{leagueId:guid}/slates/active", (Guid leagueId, HttpContext context,
            IPlayerApiClient client, CancellationToken ct) =>
            ForwardAsync(context, client, HttpMethod.Get, $"/api/v1/leagues/{leagueId:D}/slates/active", null, ct));
        api.MapGet("/leagues/{leagueId:guid}/slates/weeks", (Guid leagueId, HttpContext context,
            IPlayerApiClient client, CancellationToken ct) =>
            ForwardQueryAsync(context, client, ["season"], ct,
                $"/api/v1/leagues/{leagueId:D}/slates/weeks"));
        api.MapGet("/games", (HttpContext context, IPlayerApiClient client, CancellationToken ct) =>
            ForwardQueryAsync(context, client, ["sport", "season", "week", "page", "pageSize"], ct));
        api.MapGet("/leagues/{leagueId:guid}", (Guid leagueId, HttpContext context,
            IPlayerApiClient client, CancellationToken ct) =>
            ForwardAsync(context, client, HttpMethod.Get, $"/api/v1/leagues/{leagueId:D}", null, ct));
        api.MapGet("/leagues/{leagueId:guid}/leaderboard", (Guid leagueId, HttpContext context,
            IPlayerApiClient client, CancellationToken ct) =>
            ForwardQueryAsync(context, client, ["scope", "season", "week"], ct,
                $"/api/v1/leagues/{leagueId:D}/leaderboard"));
        api.MapGet("/slates/{slateId:guid}", (Guid slateId, HttpContext context,
            IPlayerApiClient client, CancellationToken ct) =>
            ForwardAsync(context, client, HttpMethod.Get, $"/api/v1/slates/{slateId:D}", null, ct));
        api.MapGet("/slates/{slateId:guid}/picks/me", (Guid slateId, HttpContext context,
            IPlayerApiClient client, CancellationToken ct) =>
            ForwardAsync(context, client, HttpMethod.Get, $"/api/v1/slates/{slateId:D}/picks/me", null, ct));
        api.MapPut("/slates/{slateId:guid}/picks", (Guid slateId, HttpContext context,
            IPlayerApiClient client, IAntiforgery antiforgery, CancellationToken ct) =>
            ForwardMutationAsync(context, client, antiforgery, HttpMethod.Put,
                $"/api/v1/slates/{slateId:D}/picks", ct));
        api.MapPost("/slates/{slateId:guid}/submit", (Guid slateId, HttpContext context,
            IPlayerApiClient client, IAntiforgery antiforgery, CancellationToken ct) =>
            ForwardMutationAsync(context, client, antiforgery, HttpMethod.Post,
                $"/api/v1/slates/{slateId:D}/submit", ct));

        app.MapFallback("/bff/api/v1/{**path}", () =>
                Problem(StatusCodes.Status404NotFound, "The BFF route was not found."))
            .RequireAuthorization("Player");

        return app;
    }

    private static Task<IResult> ForwardQueryAsync(HttpContext context, IPlayerApiClient client,
        string[] allowedQueryNames, CancellationToken ct, string? apiPath = null)
    {
        apiPath ??= "/api/v1/games";
        if (!TryBuildQuery(context.Request, allowedQueryNames, out var query))
            return Task.FromResult(Problem(StatusCodes.Status400BadRequest, "The query parameters are invalid."));
        return ForwardAsync(context, client, HttpMethod.Get, apiPath + query, null, ct);
    }

    private static bool TryBuildQuery(HttpRequest request, IReadOnlyCollection<string> allowedNames, out string query)
    {
        var parameters = new List<string>();
        foreach (var pair in request.Query)
        {
            if (!allowedNames.Contains(pair.Key, StringComparer.Ordinal) || pair.Value.Count != 1)
            {
                query = string.Empty;
                return false;
            }

            parameters.Add(Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value.ToString()));
        }

        query = parameters.Count == 0 ? string.Empty : "?" + string.Join("&", parameters);
        return true;
    }

    private static async Task<IResult> ForwardMutationAsync(HttpContext context, IPlayerApiClient client,
        IAntiforgery antiforgery, HttpMethod method, string apiPath, CancellationToken ct)
    {
        if (!HasSameOrigin(context.Request))
            return Problem(StatusCodes.Status403Forbidden, "The request origin is not allowed.");
        if (!await antiforgery.IsRequestValidAsync(context))
            return Problem(StatusCodes.Status400BadRequest, "The antiforgery token is missing or invalid.");
        if (!IsJson(context.Request.ContentType))
            return Problem(StatusCodes.Status415UnsupportedMediaType, "A JSON request body is required.");
        if (context.Request.ContentLength > MaximumJsonRequestBytes)
            return Problem(StatusCodes.Status413PayloadTooLarge, "The request body is too large.");

        await using var body = new MemoryStream();
        var buffer = new byte[8 * 1024];
        while (true)
        {
            var count = await context.Request.Body.ReadAsync(buffer, ct);
            if (count == 0) break;
            if (body.Length + count > MaximumJsonRequestBytes)
                return Problem(StatusCodes.Status413PayloadTooLarge, "The request body is too large.");
            await body.WriteAsync(buffer.AsMemory(0, count), ct);
        }

        if (body.Length == 0)
            return Problem(StatusCodes.Status400BadRequest, "A JSON request body is required.");

        var content = new ByteArrayContent(body.ToArray());
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(context.Request.ContentType!);
        return await ForwardAsync(context, client, method, apiPath, content, ct);
    }

    private static async Task<IResult> ForwardAsync(HttpContext context, IPlayerApiClient client,
        HttpMethod method, string apiPath, HttpContent? content, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        using var request = new HttpRequestMessage(method, apiPath) { Content = content };
        try
        {
            var response = await client.ForwardAsync(request, context.User, ct);
            context.Response.StatusCode = response.StatusCode;
            if (!string.IsNullOrWhiteSpace(response.RetryAfter))
                context.Response.Headers.RetryAfter = response.RetryAfter;
            if (response.StatusCode == StatusCodes.Status204NoContent)
                return Results.Empty;
            context.Response.ContentType = response.ContentType ?? "application/problem+json";
            context.Response.ContentLength = response.Body.Length;
            if (response.Body.Length > 0)
                await context.Response.Body.WriteAsync(response.Body, ct);
            return Results.Empty;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("SaturdayScout.Player.Bff")
                .LogError("BFF API forwarding failed. Failure type: {FailureType}.", exception.GetType().Name);
            return Problem(StatusCodes.Status500InternalServerError, "The request could not be completed.");
        }
    }

    private static bool HasSameOrigin(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Origin", out var origins)) return true;
        if (origins.Count != 1 || !Uri.TryCreate(origins[0], UriKind.Absolute, out var origin) ||
            !string.IsNullOrEmpty(origin.UserInfo) || origin.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment))
            return false;

        var expected = $"{request.Scheme}://{request.Host}";
        return string.Equals(origin.GetLeftPart(UriPartial.Authority), expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsJson(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out var parsed) &&
        string.Equals(parsed.MediaType, "application/json", StringComparison.OrdinalIgnoreCase);

    private static IResult Problem(int status, string title) => Results.Problem(
        statusCode: status,
        title: title,
        type: "about:blank");
}
