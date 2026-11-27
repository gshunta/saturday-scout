using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace SaturdayScout.Infrastructure.Hosting;

/// <summary>Prevents local-only environments from being activated on Azure App Service.</summary>
public static class ScoutHostingEnvironmentGuard
{
    private static readonly string[] AppServiceMarkers =
    [
        "WEBSITE_SITE_NAME",
        "WEBSITE_INSTANCE_ID",
        "WEBSITE_HOSTNAME"
    ];

    public static void Validate(IHostEnvironment environment, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(configuration);

        var appServiceDetected = AppServiceMarkers.Any(marker =>
            !string.IsNullOrWhiteSpace(configuration[marker]) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(marker)));

        if (appServiceDetected && (environment.IsDevelopment() || environment.IsEnvironment("Testing")))
            throw new InvalidOperationException(
                "Azure App Service cannot run Saturday Scout with ASP.NET Core Development or Testing environment behavior enabled.");
    }
}
