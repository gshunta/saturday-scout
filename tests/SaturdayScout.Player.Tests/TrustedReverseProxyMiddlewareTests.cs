using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SaturdayScout.Infrastructure.Hosting;
using Xunit;

namespace SaturdayScout.Player.Tests;

public sealed class TrustedReverseProxyMiddlewareTests
{
    [Fact]
    public async Task TrustedProxyForwardsSchemeAndClientAddressBeforeHttpsRedirection()
    {
        await using var app = await CreateProbeApp(
            ("TrustedReverseProxy:Enabled", "true"),
            ("TrustedReverseProxy:KnownProxies:0", IPAddress.Loopback.ToString()));
        using var client = CreateClient(app, IPAddress.Loopback);
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.8");

        using var response = await client.GetAsync("/probe");
        var result = await response.Content.ReadFromJsonAsync<Probe>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("https", result?.Scheme);
        Assert.Equal(IPAddress.Parse("198.51.100.8"), IPAddress.Parse(result!.RemoteIp!));
        Assert.Null(response.Headers.Location);
    }

    [Theory]
    [InlineData("::1", "::1")]
    [InlineData("127.0.0.1", "::ffff:127.0.0.1")]
    [InlineData("::ffff:127.0.0.1", "127.0.0.1")]
    public async Task TrustedIpv6AndIpv4MappedPeersAreRecognized(string configuredProxy, string remotePeer)
    {
        await using var app = await CreateProbeApp(
            ("TrustedReverseProxy:Enabled", "true"),
            ("TrustedReverseProxy:KnownProxies:0", configuredProxy));
        using var client = CreateClient(app, IPAddress.Parse(remotePeer));
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.8");

        using var response = await client.GetAsync("/probe");
        var result = await response.Content.ReadFromJsonAsync<Probe>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("https", result?.Scheme);
        Assert.Equal("198.51.100.8", result?.RemoteIp);
    }

    [Fact]
    public async Task UntrustedPeerCannotSetForwardedSchemeOrClientAddress()
    {
        var untrustedPeer = IPAddress.Parse("203.0.113.9");
        await using var app = await CreateProbeApp(
            [
                ("TrustedReverseProxy:Enabled", "true"),
                ("TrustedReverseProxy:KnownProxies:0", "192.0.2.200")
            ], includeHttpsRedirection: false);
        using var client = CreateClient(app, untrustedPeer);
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.8");

        using var response = await client.GetAsync("/probe");
        var result = await response.Content.ReadFromJsonAsync<Probe>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("http", result?.Scheme);
        Assert.NotEqual("198.51.100.8", result?.RemoteIp);
    }

    [Fact]
    public async Task DirectHttpsRequestWorksWhenForwardingIsDisabled()
    {
        await using var app = await CreateProbeApp();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("https://localhost/probe");
        var result = await response.Content.ReadFromJsonAsync<Probe>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("https", result?.Scheme);
    }

    [Fact]
    public async Task MultiHopForwardingStopsWhenTheIntermediateHopIsNotTrusted()
    {
        await using var app = await CreateProbeApp(
            [
                ("TrustedReverseProxy:Enabled", "true"),
                ("TrustedReverseProxy:ForwardLimit", "2"),
                ("TrustedReverseProxy:KnownProxies:0", "192.0.2.10")
            ], includeHttpsRedirection: false);
        using var client = CreateClient(app, IPAddress.Parse("192.0.2.10"));
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.8, 192.0.2.11");
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "http, https");

        using var response = await client.GetAsync("/probe");
        var result = await response.Content.ReadFromJsonAsync<Probe>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("https", result?.Scheme);
        Assert.Equal("192.0.2.11", result?.RemoteIp);
    }

    [Fact]
    public async Task MultiHopForwardingProcessesEachExplicitlyTrustedProxy()
    {
        await using var app = await CreateProbeApp(
            [
                ("TrustedReverseProxy:Enabled", "true"),
                ("TrustedReverseProxy:ForwardLimit", "2"),
                ("TrustedReverseProxy:KnownProxies:0", "192.0.2.10"),
                ("TrustedReverseProxy:KnownProxies:1", "192.0.2.11")
            ], includeHttpsRedirection: false);
        using var client = CreateClient(app, IPAddress.Parse("192.0.2.10"));
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.8, 192.0.2.11");
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "http, https");

        using var response = await client.GetAsync("/probe");
        var result = await response.Content.ReadFromJsonAsync<Probe>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("http", result?.Scheme);
        Assert.Equal("198.51.100.8", result?.RemoteIp);
    }

    private static async Task<WebApplication> CreateProbeApp(
        (string Key, string Value)[] settings,
        bool includeHttpsRedirection = true)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production
        });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(settings.ToDictionary(x => x.Key, x => (string?)x.Value));
        builder.Services.AddTrustedReverseProxyForwarding(builder.Configuration, builder.Environment);
        builder.Services.Configure<HttpsRedirectionOptions>(options => options.HttpsPort = 443);

        var app = builder.Build();
        app.UseTrustedReverseProxyForwarding();
        if (includeHttpsRedirection) app.UseHttpsRedirection();
        app.MapGet("/probe", (HttpContext context) => Results.Ok(new Probe(
            context.Request.Scheme,
            context.Connection.RemoteIpAddress?.ToString())));
        await app.StartAsync();
        return app;
    }

    private static Task<WebApplication> CreateProbeApp() => CreateProbeApp([]);

    private static Task<WebApplication> CreateProbeApp(params (string Key, string Value)[] settings) =>
        CreateProbeApp(settings, includeHttpsRedirection: true);

    private static HttpClient CreateClient(WebApplication app, IPAddress remoteIp) =>
        new(app.GetTestServer().CreateHandler(context => context.Connection.RemoteIpAddress = remoteIp))
        {
            BaseAddress = new Uri("http://localhost")
        };

    private sealed record Probe(string Scheme, string? RemoteIp);
}
