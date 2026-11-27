using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using SaturdayScout.Infrastructure.Hosting;
using Xunit;

namespace SaturdayScout.Infrastructure.Tests;

public sealed class ProductionConfigurationTests
{
    [Fact]
    public void ProductionRequiresVerifyFullAndCertificateValidation()
    {
        const string valid = "Host=db.example.test;Database=scout;Username=app;Password=placeholder;Ssl Mode=VerifyFull";
        Assert.Equal(valid, ScoutDatabaseConfiguration.Validate(valid, new TestEnvironment(Environments.Production)));
    }

    [Fact]
    public void HostedNonDevelopmentEnvironmentAlsoRequiresVerifyFull()
    {
        const string weakConnection = "Host=db.example.test;Database=scout;Username=app;Password=placeholder;Ssl Mode=Require";
        Assert.Throws<InvalidOperationException>(() => ScoutDatabaseConfiguration.Validate(
            weakConnection, new TestEnvironment("Staging")));
    }

    [Theory]
    [InlineData("Disable")]
    [InlineData("Allow")]
    [InlineData("Prefer")]
    [InlineData("Require")]
    [InlineData("VerifyCA")]
    public void ProductionRejectsSslModesWeakerThanVerifyFull(string sslMode)
    {
        var connection = $"Host=db.example.test;Database=scout;Username=app;Password=secret-marker;Ssl Mode={sslMode}";
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ScoutDatabaseConfiguration.Validate(connection, new TestEnvironment(Environments.Production)));
        Assert.DoesNotContain("secret-marker", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionRejectsVerifyFullWhenCertificateTrustIsBypassed()
    {
        const string connection = "Host=db.example.test;Database=scout;Username=app;Password=secret-marker;Ssl Mode=VerifyFull;Trust Server Certificate=true";
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ScoutDatabaseConfiguration.Validate(connection, new TestEnvironment(Environments.Production)));
        Assert.DoesNotContain("secret-marker", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionAllowsExplicitlyDisabledTrustBypassWithVerifyFull()
    {
        const string connection = "Host=db.example.test;Database=scout;Username=app;Password=placeholder;Ssl Mode=VerifyFull;Trust Server Certificate=false";
        Assert.Equal(connection, ScoutDatabaseConfiguration.Validate(connection,
            new TestEnvironment(Environments.Production)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-connection-string=;Ssl Mode=VerifyFull")]
    public void ProductionRejectsMissingOrMalformedConnectionStrings(string? connection)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ScoutDatabaseConfiguration.Validate(connection, new TestEnvironment(Environments.Production)));
        Assert.DoesNotContain("secret", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void NonProductionEnvironmentsRetainExistingConnectionStringCompatibility(string environmentName)
    {
        const string legacy = "Host=localhost;Database=scout;Username=app;Password=dev;Ssl Mode=Prefer";
        Assert.Equal(legacy, ScoutDatabaseConfiguration.Validate(legacy, new TestEnvironment(environmentName)));
    }

    [Theory]
    [InlineData("WEBSITE_SITE_NAME")]
    [InlineData("WEBSITE_INSTANCE_ID")]
    [InlineData("WEBSITE_HOSTNAME")]
    public void AzureAppServiceMarkersRejectDevelopmentAndTesting(string marker)
    {
        foreach (var environment in new[] { Environments.Development, "Testing" })
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { [marker] = "saturday-scout" }).Build();
            Assert.Throws<InvalidOperationException>(() => ScoutHostingEnvironmentGuard.Validate(
                new TestEnvironment(environment), config));
        }
    }

    [Fact]
    public void ProductionAndUnhostedLocalDevelopmentAreAllowed()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>()).Build();
        ScoutHostingEnvironmentGuard.Validate(new TestEnvironment(Environments.Development), config);
        ScoutHostingEnvironmentGuard.Validate(new TestEnvironment(Environments.Production), config);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("example.test;*.example.test")]
    [InlineData("https://example.test")]
    public void ProductionRequiresExplicitHostnameEntries(string? allowedHosts)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["AllowedHosts"] = allowedHosts }).Build();
        Assert.Throws<InvalidOperationException>(() => ScoutHostConfiguration.ValidateAllowedHosts(
            config, new TestEnvironment(Environments.Production)));
    }

    [Fact]
    public void ProductionAcceptsExplicitDnsHostnamesAndLocalEnvironmentsKeepExistingDefaults()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["AllowedHosts"] = "saturday-scout-player.azurewebsites.net;player.example.test" }).Build();
        ScoutHostConfiguration.ValidateAllowedHosts(config, new TestEnvironment(Environments.Production));
        ScoutHostConfiguration.ValidateAllowedHosts(new ConfigurationBuilder().Build(),
            new TestEnvironment(Environments.Development));
    }

    private sealed class TestEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "SaturdayScout.Infrastructure.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
