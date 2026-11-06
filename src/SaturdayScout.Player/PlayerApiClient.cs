using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components.Authorization;
using SaturdayScout.Infrastructure.Identity;

namespace SaturdayScout.Player;

public enum PlayerApiOutcome
{
    Success,
    AuthenticationRequired,
    Forbidden,
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

public interface IPlayerApiClient
{
    Task<PlayerApiResult<T>> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken = default);
}

public sealed class PlayerApiClient(
    HttpClient httpClient,
    AuthenticationStateProvider authenticationStateProvider,
    IPlayerApiAssertionIssuer assertions) : IPlayerApiClient
{
    public async Task<PlayerApiResult<T>> SendAsync<T>(HttpRequestMessage request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requestPath = request.RequestUri?.OriginalString;
        if (httpClient.BaseAddress is null || request.RequestUri is null || string.IsNullOrWhiteSpace(requestPath) || request.RequestUri.IsAbsoluteUri ||
            requestPath.StartsWith("//", StringComparison.Ordinal) ||
            !(requestPath.StartsWith("/api/v1/", StringComparison.Ordinal) || requestPath.StartsWith("api/v1/", StringComparison.Ordinal)))
            return new(PlayerApiOutcome.Failure, Message: "The API request target is not configured correctly.");
        var principal = (await authenticationStateProvider.GetAuthenticationStateAsync()).User;
        if (principal?.Identity?.IsAuthenticated != true || !principal.IsInRole(ScoutRoles.Player))
            return new(PlayerApiOutcome.AuthenticationRequired, Message: "A signed-in player session is required.");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", assertions.Issue(principal));
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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
