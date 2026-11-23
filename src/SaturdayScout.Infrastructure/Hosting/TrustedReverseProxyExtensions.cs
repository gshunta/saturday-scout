using System.Net;
using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace SaturdayScout.Infrastructure.Hosting;

public sealed record TrustedReverseProxySettings(
    bool Enabled,
    int ForwardLimit,
    IReadOnlyList<IPAddress> KnownProxies,
    IReadOnlyList<System.Net.IPNetwork> KnownNetworks)
{
    public const string SectionName = "TrustedReverseProxy";
}

public static class TrustedReverseProxyExtensions
{
    private const int MaximumForwardLimit = 5;

    public static IServiceCollection AddTrustedReverseProxyForwarding(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var section = configuration.GetSection(TrustedReverseProxySettings.SectionName);
        var configured = section.Get<TrustedReverseProxyConfiguration>() ?? new();
        var proxies = ParseProxies(configured.KnownProxies);
        var networks = ParseNetworks(configured.KnownNetworks);

        if (configured.ForwardLimit is < 1 or > MaximumForwardLimit)
            throw new InvalidOperationException(
                $"{TrustedReverseProxySettings.SectionName}:ForwardLimit must be between 1 and {MaximumForwardLimit}.");

        if (configured.Enabled && proxies.Count == 0 && networks.Count == 0)
        {
            var reason = environment.IsProduction()
                ? "Forwarded-header processing is enabled in Production but no trusted proxies or networks are configured."
                : "Forwarded-header processing requires at least one explicitly trusted proxy or network.";
            throw new InvalidOperationException(reason);
        }

        var settings = new TrustedReverseProxySettings(
            configured.Enabled,
            configured.ForwardLimit,
            proxies,
            networks);
        services.AddSingleton(settings);

        if (settings.Enabled)
        {
            services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                options.ForwardLimit = settings.ForwardLimit;

                // The framework defaults include loopback trust. Replace those defaults only
                // when processing is enabled, and only with the validated explicit entries.
                options.KnownProxies.Clear();
                options.KnownIPNetworks.Clear();
                foreach (var proxy in settings.KnownProxies)
                    options.KnownProxies.Add(proxy);
                foreach (var network in settings.KnownNetworks)
                    options.KnownIPNetworks.Add(network);
            });
        }

        return services;
    }

    public static IApplicationBuilder UseTrustedReverseProxyForwarding(this IApplicationBuilder app)
    {
        if (app.ApplicationServices.GetRequiredService<TrustedReverseProxySettings>().Enabled)
            app.UseForwardedHeaders();
        return app;
    }

    private static IReadOnlyList<IPAddress> ParseProxies(IReadOnlyCollection<string> values)
    {
        var proxies = new List<IPAddress>(values.Count);
        foreach (var value in values)
        {
            if (!IPAddress.TryParse(value, out var address))
                throw new InvalidOperationException(
                    $"{TrustedReverseProxySettings.SectionName}:KnownProxies contains an invalid IP address.");
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            if (!proxies.Contains(address)) proxies.Add(address);
        }

        return proxies;
    }

    private static IReadOnlyList<System.Net.IPNetwork> ParseNetworks(IReadOnlyCollection<string> values)
    {
        var networks = new List<System.Net.IPNetwork>(values.Count);
        foreach (var value in values)
        {
            var separator = value.LastIndexOf('/');
            if (separator > 0 && IPAddress.TryParse(value[..separator], out var configuredAddress) &&
                configuredAddress.IsIPv4MappedToIPv6 &&
                (!int.TryParse(value[(separator + 1)..], NumberStyles.None,
                    CultureInfo.InvariantCulture, out var configuredPrefix) || configuredPrefix < 96))
                throw new InvalidOperationException(
                    $"{TrustedReverseProxySettings.SectionName}:KnownNetworks has an ambiguous IPv4-mapped IPv6 prefix.");

            if (!System.Net.IPNetwork.TryParse(value, out var network))
                throw new InvalidOperationException(
                    $"{TrustedReverseProxySettings.SectionName}:KnownNetworks contains an invalid CIDR network.");
            if (network.BaseAddress.IsIPv4MappedToIPv6)
            {
                if (network.PrefixLength < 96)
                    throw new InvalidOperationException(
                        $"{TrustedReverseProxySettings.SectionName}:KnownNetworks has an ambiguous IPv4-mapped IPv6 prefix.");
                network = new System.Net.IPNetwork(
                    network.BaseAddress.MapToIPv4(), network.PrefixLength - 96);
            }
            if (network.PrefixLength == 0)
                throw new InvalidOperationException(
                    $"{TrustedReverseProxySettings.SectionName}:KnownNetworks cannot trust all IP addresses.");
            if (!networks.Contains(network)) networks.Add(network);
        }

        return networks;
    }

    private sealed class TrustedReverseProxyConfiguration
    {
        public bool Enabled { get; set; }
        public int ForwardLimit { get; set; } = 1;
        public string[] KnownProxies { get; set; } = [];
        public string[] KnownNetworks { get; set; } = [];
    }
}
