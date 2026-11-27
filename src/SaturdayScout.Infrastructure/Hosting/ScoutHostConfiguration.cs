using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace SaturdayScout.Infrastructure.Hosting;

public static class ScoutHostConfiguration
{
    public static void ValidateAllowedHosts(IConfiguration configuration, IHostEnvironment environment)
    {
        if (environment.IsDevelopment() || environment.IsEnvironment("Testing")) return;

        var allowedHosts = configuration["AllowedHosts"];
        if (string.IsNullOrWhiteSpace(allowedHosts) || allowedHosts.Trim() == "*")
            throw new InvalidOperationException("Hosted applications require explicit AllowedHosts entries.");

        var hosts = allowedHosts.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (hosts.Length == 0 || hosts.Any(host => host.Contains('*') || Uri.CheckHostName(host) == UriHostNameType.Unknown))
            throw new InvalidOperationException("Hosted AllowedHosts must contain only explicit host names separated by semicolons.");
    }
}
