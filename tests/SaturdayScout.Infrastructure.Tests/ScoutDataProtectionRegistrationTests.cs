using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SaturdayScout.Infrastructure.Identity;
using Xunit;

namespace SaturdayScout.Infrastructure.Tests;

public sealed class ScoutDataProtectionRegistrationTests
{
    [Fact]
    public void HostRingsUseSeparatePlayerNameAndSharedAdminApiName()
    {
        Assert.Equal("SaturdayScout.Player.v1",
            ScoutDataProtectionRegistration.GetApplicationName(ScoutDataProtectionRing.Player));
        Assert.Equal("SaturdayScout.SharedIdentity.v1",
            ScoutDataProtectionRegistration.GetApplicationName(ScoutDataProtectionRing.AdminApi));

        var player = ResolveApplicationDiscriminator(ScoutDataProtectionRing.Player, FileSystemConfiguration());
        var admin = ResolveApplicationDiscriminator(ScoutDataProtectionRing.AdminApi, FileSystemConfiguration());
        var api = ResolveApplicationDiscriminator(ScoutDataProtectionRing.AdminApi, FileSystemConfiguration());

        Assert.Equal("SaturdayScout.Player.v1", player);
        Assert.Equal(admin, api);
        Assert.NotEqual(player, admin);
    }

    [Fact]
    public void PersistedKeyCanBeUsedAfterProviderRecreationButNotByPlayerRing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"scout-dp-registration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        try
        {
            var config = FileSystemConfiguration(path);
            string protectedValue;
            using (var firstProvider = BuildProvider(config, ScoutDataProtectionRing.AdminApi))
            {
                protectedValue = firstProvider.GetRequiredService<IDataProtectionProvider>()
                    .CreateProtector("SaturdayScout.DataProtection.Tests").Protect("persisted-test-value");
            }

            using (var restartedProvider = BuildProvider(config, ScoutDataProtectionRing.AdminApi))
            {
                var recovered = restartedProvider.GetRequiredService<IDataProtectionProvider>()
                    .CreateProtector("SaturdayScout.DataProtection.Tests").Unprotect(protectedValue);
                Assert.Equal("persisted-test-value", recovered);
            }

            using var playerProvider = BuildProvider(config, ScoutDataProtectionRing.Player);
            Assert.Throws<CryptographicException>(() => playerProvider.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("SaturdayScout.DataProtection.Tests").Unprotect(protectedValue));
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public void AzureProviderRegistersBlobRepositoryAndDoesNotUseFilesystemFallback()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoutDataProtection(AzureConfiguration(
            ScoutIdentityServices.PlayerDataProtectionApplicationName),
            new TestEnvironment(Environments.Production), ScoutDataProtectionRing.Player);

        using var provider = services.BuildServiceProvider();
        var keyManagement = provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value;

        Assert.NotNull(keyManagement.XmlRepository);
        Assert.Contains("Blob", keyManagement.XmlRepository.GetType().Name, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FileSystem", keyManagement.XmlRepository.GetType().Name, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(keyManagement.XmlEncryptor);
        Assert.Contains("KeyVault", keyManagement.XmlEncryptor.GetType().Name, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ScoutIdentityServices.PlayerDataProtectionApplicationName,
            provider.GetRequiredService<IOptions<DataProtectionOptions>>().Value.ApplicationDiscriminator);
    }

    [Fact]
    public void DevelopmentUsesFilesystemWithoutAzureConfigurationOrCredentials()
    {
        var path = Path.Combine(Path.GetTempPath(), $"scout-dp-development-{Guid.NewGuid():N}");
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddScoutDataProtection(FileSystemConfiguration(path),
                new TestEnvironment(Environments.Development), ScoutDataProtectionRing.Player);
            using var provider = services.BuildServiceProvider();

            var xmlRepository = provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository;
            Assert.NotNull(xmlRepository);
            Assert.Contains("FileSystem", xmlRepository.GetType().Name, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(path));
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public void TestingEnvironmentUsesOnlyAnExplicitFilesystemKeyPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"scout-dp-testing-{Guid.NewGuid():N}");
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddScoutDataProtection(FileSystemConfiguration(path),
                new TestEnvironment("Testing"), ScoutDataProtectionRing.AdminApi);
            using var provider = services.BuildServiceProvider();

            var xmlRepository = provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository;
            Assert.NotNull(xmlRepository);
            Assert.Contains("FileSystem", xmlRepository.GetType().Name, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(path));

            var missingPathServices = new ServiceCollection();
            Assert.Throws<InvalidOperationException>(() => missingPathServices.AddScoutDataProtection(
                Configuration((ScoutDataProtectionRegistration.ProviderSetting, "FileSystem")),
                new TestEnvironment("Testing"), ScoutDataProtectionRing.AdminApi));

            var azureInTestServices = new ServiceCollection();
            Assert.Throws<InvalidOperationException>(() => azureInTestServices.AddScoutDataProtection(
                AzureConfiguration(ScoutIdentityServices.DataProtectionApplicationName),
                new TestEnvironment("Testing"), ScoutDataProtectionRing.AdminApi));
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public void ProductionRequiresExplicitAzureProviderAndAllAzureSettings()
    {
        var services = new ServiceCollection();

        var missingProvider = Assert.Throws<InvalidOperationException>(() => services.AddScoutDataProtection(
            new ConfigurationBuilder().Build(), new TestEnvironment(Environments.Production),
            ScoutDataProtectionRing.AdminApi));
        Assert.Contains(ScoutDataProtectionRegistration.ProviderSetting, missingProvider.Message);
        Assert.DoesNotContain("Password", missingProvider.Message, StringComparison.OrdinalIgnoreCase);

        var missingBlob = Assert.Throws<InvalidOperationException>(() => services.AddScoutDataProtection(
            Configuration((ScoutDataProtectionRegistration.ProviderSetting, "AzureBlob"),
                (ScoutDataProtectionRegistration.ApplicationNameSetting,
                    ScoutIdentityServices.DataProtectionApplicationName)),
            new TestEnvironment(Environments.Production), ScoutDataProtectionRing.AdminApi));
        Assert.Contains(ScoutDataProtectionRegistration.BlobUriSetting, missingBlob.Message);
    }

    [Theory]
    [InlineData("http://storage.blob.core.windows.net/container/ring.xml", "https://vault.vault.azure.net/keys/dp", "SaturdayScout.Player.v1")]
    [InlineData("https://example.test/container/ring.xml", "https://vault.vault.azure.net/keys/dp", "SaturdayScout.Player.v1")]
    [InlineData("https://user@storage.blob.core.windows.net/container/ring.xml", "https://vault.vault.azure.net/keys/dp", "SaturdayScout.Player.v1")]
    [InlineData("https://storage.blob.core.windows.net/container/ring.xml?sig=secret", "https://vault.vault.azure.net/keys/dp", "SaturdayScout.Player.v1")]
    [InlineData("https://storage.blob.core.windows.net/container", "https://vault.vault.azure.net/keys/dp", "SaturdayScout.Player.v1")]
    [InlineData("https://storage.blob.core.windows.net/container/ring.xml", "https://vault.vault.azure.net/keys/dp/version", "SaturdayScout.Player.v1")]
    [InlineData("https://storage.blob.core.windows.net/container/ring.xml", "http://vault.vault.azure.net/keys/dp", "SaturdayScout.Player.v1")]
    [InlineData("https://storage.blob.core.windows.net/container/ring.xml", "https://vault.vault.azure.net/keys/dp", "SaturdayScout.SharedIdentity.v1")]
    public void InvalidOrMismatchedProductionConfigurationFailsWithoutEchoingValues(
        string blobUri, string keyId, string applicationName)
    {
        var services = new ServiceCollection();
        var secretMarker = "sig=secret";
        var exception = Assert.Throws<InvalidOperationException>(() => services.AddScoutDataProtection(
            AzureConfiguration(applicationName, blobUri, keyId),
            new TestEnvironment(Environments.Production), ScoutDataProtectionRing.Player));

        Assert.DoesNotContain(secretMarker, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(blobUri, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(keyId, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionCannotSelectFilesystemOrCombineProviders()
    {
        var production = new TestEnvironment(Environments.Production);
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddScoutDataProtection(
            Configuration((ScoutDataProtectionRegistration.ProviderSetting, "FileSystem")), production,
            ScoutDataProtectionRing.Player));

        var conflicting = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => conflicting.AddScoutDataProtection(
            Configuration((ScoutDataProtectionRegistration.ProviderSetting, "AzureBlob"),
                (ScoutDataProtectionRegistration.ApplicationNameSetting,
                    ScoutIdentityServices.PlayerDataProtectionApplicationName),
                (ScoutDataProtectionRegistration.FileSystemPathSetting, "/tmp/dp")), production,
            ScoutDataProtectionRing.Player));
    }

    [Fact]
    public void AzureProviderRequiresKeyVaultConfiguration()
    {
        var config = Configuration(
            (ScoutDataProtectionRegistration.ProviderSetting, "AzureBlob"),
            (ScoutDataProtectionRegistration.BlobUriSetting,
                "https://scoutstorage.blob.core.windows.net/player-ring/keyring.xml"),
            (ScoutDataProtectionRegistration.ApplicationNameSetting,
                ScoutIdentityServices.PlayerDataProtectionApplicationName));

        var exception = Assert.Throws<InvalidOperationException>(() => new ServiceCollection()
            .AddScoutDataProtection(config, new TestEnvironment(Environments.Production), ScoutDataProtectionRing.Player));
        Assert.Contains(ScoutDataProtectionRegistration.KeyVaultKeyIdSetting, exception.Message);
    }

    private static string? ResolveApplicationDiscriminator(ScoutDataProtectionRing ring, IConfiguration configuration)
    {
        var provider = BuildProvider(configuration, ring);
        using (provider)
            return provider.GetRequiredService<IOptions<DataProtectionOptions>>().Value.ApplicationDiscriminator;
    }

    private static ServiceProvider BuildProvider(IConfiguration configuration, ScoutDataProtectionRing ring)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoutDataProtection(configuration, new TestEnvironment(Environments.Development), ring);
        return services.BuildServiceProvider();
    }

    private static IConfiguration FileSystemConfiguration(string? path = null) => Configuration(
        (ScoutDataProtectionRegistration.ProviderSetting, "FileSystem"),
        (ScoutDataProtectionRegistration.FileSystemPathSetting, path ?? Path.Combine(Path.GetTempPath(), $"scout-dp-{Guid.NewGuid():N}")));

    private static IConfiguration AzureConfiguration(string applicationName,
        string blobUri = "https://scoutstorage.blob.core.windows.net/player-ring/keyring.xml",
        string keyId = "https://scoutvault.vault.azure.net/keys/player-dp") => Configuration(
        (ScoutDataProtectionRegistration.ProviderSetting, "AzureBlob"),
        (ScoutDataProtectionRegistration.BlobUriSetting, blobUri),
        (ScoutDataProtectionRegistration.KeyVaultKeyIdSetting, keyId),
        (ScoutDataProtectionRegistration.ApplicationNameSetting, applicationName));

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
