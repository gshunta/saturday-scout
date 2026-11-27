using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SaturdayScout.Infrastructure.Hosting;
using Xunit;

namespace SaturdayScout.Infrastructure.Tests;

public sealed class TrustedReverseProxyConfigurationTests
{
    [Fact]
    public void EnabledConfigurationUsesOnlyExplicitValidatedPeersAndOneForwardingHop()
    {
        var services = new ServiceCollection();
        services.AddTrustedReverseProxyForwarding(Configuration(
            ("TrustedReverseProxy:Enabled", "true"),
            ("TrustedReverseProxy:KnownProxies:0", "192.0.2.10"),
            ("TrustedReverseProxy:KnownNetworks:0", "198.51.100.0/24"),
            ("TrustedReverseProxy:KnownNetworks:1", "2001:db8::/64")),
            new TestEnvironment(Environments.Production));
        services.AddLogging();

        using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<TrustedReverseProxySettings>();
        var forwarded = provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        Assert.True(settings.Enabled);
        Assert.Equal(1, settings.ForwardLimit);
        Assert.Equal([IPAddress.Parse("192.0.2.10")], settings.KnownProxies);
        Assert.Equal([
            System.Net.IPNetwork.Parse("198.51.100.0/24"),
            System.Net.IPNetwork.Parse("2001:db8::/64")
        ], settings.KnownNetworks);
        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            forwarded.ForwardedHeaders);
        Assert.Equal(1, forwarded.ForwardLimit);
        Assert.Contains(IPAddress.Parse("192.0.2.10"), forwarded.KnownProxies);
        Assert.Contains(System.Net.IPNetwork.Parse("198.51.100.0/24"), forwarded.KnownIPNetworks);
        Assert.Contains(System.Net.IPNetwork.Parse("2001:db8::/64"), forwarded.KnownIPNetworks);
        Assert.DoesNotContain(IPAddress.Loopback, forwarded.KnownProxies);
    }

    [Fact]
    public void Ipv4MappedProxyAddressesAndNetworksNormalizeToIpv4()
    {
        var services = new ServiceCollection();
        services.AddTrustedReverseProxyForwarding(Configuration(
            ("TrustedReverseProxy:Enabled", "true"),
            ("TrustedReverseProxy:KnownProxies:0", "::ffff:192.0.2.10"),
            ("TrustedReverseProxy:KnownNetworks:0", "::ffff:198.51.100.0/120")),
            new TestEnvironment(Environments.Production));

        using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<TrustedReverseProxySettings>();

        Assert.Equal([IPAddress.Parse("192.0.2.10")], settings.KnownProxies);
        Assert.Equal([System.Net.IPNetwork.Parse("198.51.100.0/24")], settings.KnownNetworks);
    }

    [Theory]
    [InlineData("TrustedReverseProxy:KnownProxies:0", "not-an-ip")]
    [InlineData("TrustedReverseProxy:KnownNetworks:0", "192.0.2.0/99")]
    [InlineData("TrustedReverseProxy:KnownNetworks:0", "192.0.2.1")]
    [InlineData("TrustedReverseProxy:KnownNetworks:0", "0.0.0.0/0")]
    [InlineData("TrustedReverseProxy:KnownNetworks:0", "::/0")]
    [InlineData("TrustedReverseProxy:KnownNetworks:0", "::ffff:192.0.2.0/95")]
    public void InvalidProxyAddressOrNetworkFailsConfiguration(
        string key, string invalidValue)
    {
        var services = new ServiceCollection();
        var config = Configuration(("TrustedReverseProxy:Enabled", "false"), (key, invalidValue));

        Assert.Throws<InvalidOperationException>(() => services.AddTrustedReverseProxyForwarding(
            config, new TestEnvironment(Environments.Development)));
    }

    [Fact]
    public void ProductionCannotEnableForwardingWithoutExplicitTrustedPeer()
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<InvalidOperationException>(() =>
            services.AddTrustedReverseProxyForwarding(Configuration(
                ("TrustedReverseProxy:Enabled", "true")),
                new TestEnvironment(Environments.Production)));

        Assert.Contains("no trusted proxies or networks", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DirectProductionHostingAndDevelopmentNeedNoProxyConfiguration()
    {
        foreach (var environmentName in new[] { Environments.Production, Environments.Development })
        {
            var services = new ServiceCollection();
            services.AddTrustedReverseProxyForwarding(Configuration(), new TestEnvironment(environmentName));
            using var provider = services.BuildServiceProvider();

            Assert.False(provider.GetRequiredService<TrustedReverseProxySettings>().Enabled);
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("6")]
    public void ForwardLimitMustRemainBounded(string limit)
    {
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() => services.AddTrustedReverseProxyForwarding(
            Configuration(("TrustedReverseProxy:ForwardLimit", limit)),
            new TestEnvironment(Environments.Development)));
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder().AddInMemoryCollection(entries.ToDictionary(x => x.Key, x => (string?)x.Value))
            .Build();

    private sealed class TestEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "SaturdayScout.Infrastructure.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
