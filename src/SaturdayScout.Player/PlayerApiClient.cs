using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using SaturdayScout.Infrastructure.Identity;

namespace SaturdayScout.Player;

public enum PlayerApiOutcome
{
    Success,
    AuthenticationRequired,
    Forbidden,
    NotFound,
    ValidationFailed,
    Conflict,
    TransientFailure,
    Failure
}

public sealed record PlayerApiResult<T>(
    PlayerApiOutcome Outcome,
    T? Value = default,
    string? Message = null,
    IReadOnlyDictionary<string, string[]>? ValidationErrors = null,
    bool RetryRecommended = false);

public sealed record PlayerApiForwardResponse(
    int StatusCode,
    string? ContentType,
    byte[] Body,
    string? RetryAfter = null);

public interface IPlayerApiClient
{
    Task<PlayerApiResult<T>> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken = default);
    Task<PlayerApiForwardResponse> ForwardAsync(HttpRequestMessage request, ClaimsPrincipal principal,
        CancellationToken cancellationToken = default);
}

public sealed class PlayerApiClient(
    HttpClient httpClient,
    AuthenticationStateProvider authenticationStateProvider,
    IPlayerApiAssertionIssuer assertions) : IPlayerApiClient
{
    private const int MaximumForwardResponseBytes = 4 * 1024 * 1024;

    public async Task<PlayerApiForwardResponse> ForwardAsync(HttpRequestMessage request, ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(principal);
        if (!IsAllowedApiTarget(request.RequestUri))
            return ProblemResponse(StatusCodes.Status400BadRequest, "Invalid API request target.");
        if (httpClient.BaseAddress is null)
            return ProblemResponse(StatusCodes.Status503ServiceUnavailable, "The API is temporarily unavailable.");

        if (principal?.Identity?.IsAuthenticated != true)
            return ProblemResponse(StatusCodes.Status401Unauthorized, "Authentication is required.");
        if (!principal.IsInRole(ScoutRoles.Player))
            return ProblemResponse(StatusCodes.Status403Forbidden, "Player access is required.");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", assertions.Issue(principal));
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return ProblemResponse(StatusCodes.Status503ServiceUnavailable, "The API is temporarily unavailable.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProblemResponse(StatusCodes.Status503ServiceUnavailable, "The API request timed out.");
        }

        using (response)
        {
            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength > MaximumForwardResponseBytes)
                return ProblemResponse(StatusCodes.Status502BadGateway, "The API response exceeded the allowed size.");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var content = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var count = await stream.ReadAsync(buffer, cancellationToken);
                if (count == 0) break;
                if (content.Length + count > MaximumForwardResponseBytes)
                    return ProblemResponse(StatusCodes.Status502BadGateway, "The API response exceeded the allowed size.");
                await content.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            }

            var body = content.ToArray();
            var statusCode = (int)response.StatusCode;
            if (statusCode != StatusCodes.Status204NoContent && body.Length == 0 && statusCode >= 400)
                return ProblemResponse(statusCode, "The API request could not be completed.");

            return new PlayerApiForwardResponse(statusCode, response.Content.Headers.ContentType?.ToString(), body,
                response.Headers.RetryAfter?.ToString());
        }
    }

    public async Task<PlayerApiResult<T>> SendAsync<T>(HttpRequestMessage request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsAllowedApiTarget(request.RequestUri) || httpClient.BaseAddress is null)
            return new(PlayerApiOutcome.Failure, Message: "The API request target is not configured correctly.");
        var principal = (await authenticationStateProvider.GetAuthenticationStateAsync()).User;
        if (principal?.Identity?.IsAuthenticated != true || !principal.IsInRole(ScoutRoles.Player))
            return new(PlayerApiOutcome.AuthenticationRequired, Message: "A signed-in player session is required.");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", assertions.Issue(principal));
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return new(PlayerApiOutcome.TransientFailure,
                Message: "The connection was interrupted. Check the server state before trying again.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(PlayerApiOutcome.TransientFailure,
                Message: "The request timed out. Check the server state before trying again.");
        }
        using (response)
        {
        if (response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.NoContent)
                return new(PlayerApiOutcome.Success);
            try
            {
                var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken);
                return new(PlayerApiOutcome.Success, value);
            }
            catch (System.Text.Json.JsonException)
            {
                return new(PlayerApiOutcome.Failure, Message: "The API returned an unreadable response.");
            }
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return new(PlayerApiOutcome.AuthenticationRequired, Message: "Your session expired. Sign in again.");
        if (response.StatusCode == HttpStatusCode.Forbidden)
            return new(PlayerApiOutcome.Forbidden, Message: "You do not have access to this league or action.");
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new(PlayerApiOutcome.NotFound);
        if (response.StatusCode == HttpStatusCode.Conflict)
            return new(PlayerApiOutcome.Conflict, Message: "The data changed. Refresh and try again.");
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
            return await ReadValidationFailureAsync<T>(response, cancellationToken);
        if ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500 ||
            response.StatusCode == HttpStatusCode.RequestTimeout)
            return new(PlayerApiOutcome.TransientFailure,
                Message: "The service is temporarily unavailable. Refresh current server state before trying again.",
                RetryRecommended: request.Method == HttpMethod.Get);

        return new(PlayerApiOutcome.Failure, Message: "The request could not be completed.");
        }
    }

    private static bool IsAllowedApiTarget(Uri? requestUri)
    {
        var requestPath = requestUri?.OriginalString;
        return requestUri is not null && !requestUri.IsAbsoluteUri &&
               !string.IsNullOrWhiteSpace(requestPath) &&
               !requestPath.StartsWith("//", StringComparison.Ordinal) &&
               (requestPath.StartsWith("/api/v1/", StringComparison.Ordinal) ||
                requestPath.StartsWith("api/v1/", StringComparison.Ordinal));
    }

    private static PlayerApiForwardResponse ProblemResponse(int statusCode, string title)
    {
        var body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "about:blank",
            title,
            status = statusCode
        });
        return new PlayerApiForwardResponse(statusCode, "application/problem+json", body);
    }

    private static async Task<PlayerApiResult<T>> ReadValidationFailureAsync<T>(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ValidationProblemResponse>(cancellationToken: cancellationToken);
            return new(PlayerApiOutcome.ValidationFailed,
                Message: string.IsNullOrWhiteSpace(problem?.Detail) ? "Review the submitted information." : problem.Detail,
                ValidationErrors: problem?.Errors);
        }
        catch (System.Text.Json.JsonException)
        {
            return new(PlayerApiOutcome.ValidationFailed, Message: "Review the submitted information.");
        }
    }

    private sealed record ValidationProblemResponse(
        string? Title,
        string? Detail,
        IReadOnlyDictionary<string, string[]>? Errors);
}
