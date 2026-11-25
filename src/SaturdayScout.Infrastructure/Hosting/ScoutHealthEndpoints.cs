using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SaturdayScout.Infrastructure.Persistence;

namespace SaturdayScout.Infrastructure.Hosting;

public sealed record ScoutHealthOptions(TimeSpan DatabaseTimeout)
{
    public const string TimeoutSetting = "Health:DatabaseTimeoutSeconds";
    public const int DefaultTimeoutSeconds = 3;
    public const int MaximumTimeoutSeconds = 10;
}

public interface IDatabaseReadinessProbe
{
    Task<bool> IsReadyAsync(CancellationToken cancellationToken);
}

internal sealed class PostgresDatabaseReadinessProbe(SaturdayScoutDbContext db)
    : IDatabaseReadinessProbe
{
    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            // EF/Npgsql uses the application's configured provider and connection pool.
            // CanConnect issues a lightweight connectivity check and does not mutate data.
            return await db.Database.CanConnectAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
}

public static class ScoutHealthEndpoints
{
    public static IServiceCollection AddScoutHealthEndpoints(
        this IServiceCollection services, IConfiguration configuration)
    {
        var raw = configuration[ScoutHealthOptions.TimeoutSetting];
        var seconds = ScoutHealthOptions.DefaultTimeoutSeconds;
        if (raw is not null && (!int.TryParse(raw, out seconds) || seconds is < 1 or > ScoutHealthOptions.MaximumTimeoutSeconds))
            throw new InvalidOperationException(
                $"{ScoutHealthOptions.TimeoutSetting} must be between 1 and {ScoutHealthOptions.MaximumTimeoutSeconds} seconds.");

        services.AddSingleton(new ScoutHealthOptions(TimeSpan.FromSeconds(seconds)));
        services.TryAddScoped<IDatabaseReadinessProbe, PostgresDatabaseReadinessProbe>();
        return services;
    }

    public static IEndpointRouteBuilder MapScoutHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMethods("/health/live", ["GET", "HEAD"], (HttpContext context) =>
            WriteHealthResponse(context, StatusCodes.Status200OK, "Healthy"))
            .AllowAnonymous();

        endpoints.MapMethods("/health/ready", ["GET", "HEAD"], ReadyAsync)
            .AllowAnonymous();

        return endpoints;
    }

    private static async Task<IResult> ReadyAsync(
        HttpContext context, IDatabaseReadinessProbe probe, ScoutHealthOptions options)
    {
        using var timeout = new CancellationTokenSource(options.DatabaseTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, timeout.Token);
        bool ready;
        try
        {
            ready = await probe.IsReadyAsync(linked.Token).WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            ready = false;
        }
        catch
        {
            ready = false;
        }

        return WriteHealthResponse(context,
            ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable,
            ready ? "Healthy" : "Unavailable");
    }

    private static IResult WriteHealthResponse(HttpContext context, int statusCode, string body)
    {
        context.Response.Headers.CacheControl = "no-store, no-cache";
        context.Response.Headers.Pragma = "no-cache";
        // Player's status-code re-execution middleware must not turn a 503 into a page.
        var statusCodePages = context.Features.Get<IStatusCodePagesFeature>();
        if (statusCodePages is not null) statusCodePages.Enabled = false;

        if (HttpMethods.IsHead(context.Request.Method))
            return Results.StatusCode(statusCode);

        return Results.Text(body, "text/plain", statusCode: statusCode);
    }
}
