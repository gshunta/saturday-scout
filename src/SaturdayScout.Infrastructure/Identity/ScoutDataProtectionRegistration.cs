using Azure.Identity;
using Azure.Core;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace SaturdayScout.Infrastructure.Identity;

public enum ScoutDataProtectionRing
{
    Player,
    AdminApi
}

public enum ScoutDataProtectionProvider
{
    FileSystem,
    AzureBlob
}

/// <summary>Registers the named key ring used by one Saturday Scout authentication boundary.</summary>
public static class ScoutDataProtectionRegistration
{
    public const string ProviderSetting = "SATURDAY_SCOUT_DP_PROVIDER";
    public const string BlobUriSetting = "SATURDAY_SCOUT_DP_KEY_BLOB_URI";
    public const string KeyVaultKeyIdSetting = "SATURDAY_SCOUT_DP_KEY_VAULT_KEY_ID";
    public const string ApplicationNameSetting = "SATURDAY_SCOUT_DP_APPLICATION_NAME";
    public const string FileSystemPathSetting = "SATURDAY_SCOUT_DP_KEYS_PATH";

    public static IServiceCollection AddScoutDataProtection(this IServiceCollection services,
        IConfiguration configuration, IHostEnvironment environment, ScoutDataProtectionRing ring)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var expectedApplicationName = GetApplicationName(ring);
        var isTesting = environment.IsEnvironment("Testing");
        var providerText = configuration[ProviderSetting];
        ScoutDataProtectionProvider provider;
        if (string.IsNullOrWhiteSpace(providerText))
        {
            if (environment.IsDevelopment() ||
                isTesting && !string.IsNullOrWhiteSpace(configuration[FileSystemPathSetting]))
            {
                provider = ScoutDataProtectionProvider.FileSystem;
            }
            else
            {
                throw new InvalidOperationException($"{ProviderSetting} must be AzureBlob outside Development and Testing; Testing requires an explicit {FileSystemPathSetting}.");
            }
        }
        else if (!Enum.TryParse(providerText, ignoreCase: true, out provider) ||
                 !Enum.IsDefined(provider))
        {
            throw new InvalidOperationException($"{ProviderSetting} must be FileSystem or AzureBlob.");
        }

        if (!environment.IsDevelopment() && !isTesting && provider != ScoutDataProtectionProvider.AzureBlob)
            throw new InvalidOperationException($"{ProviderSetting} must be AzureBlob outside Development and Testing.");
        if (isTesting && provider != ScoutDataProtectionProvider.FileSystem)
            throw new InvalidOperationException($"{ProviderSetting} must be FileSystem in the Testing environment.");

        var configuredApplicationName = configuration[ApplicationNameSetting];
        if (provider == ScoutDataProtectionProvider.AzureBlob && string.IsNullOrWhiteSpace(configuredApplicationName))
            throw new InvalidOperationException($"{ApplicationNameSetting} is required when {ProviderSetting}=AzureBlob.");
        var applicationName = configuredApplicationName ?? expectedApplicationName;
        if (!string.Equals(applicationName, expectedApplicationName, StringComparison.Ordinal))
            throw new InvalidOperationException($"{ApplicationNameSetting} does not match the selected authentication ring.");

        var dataProtection = services.AddDataProtection().SetApplicationName(applicationName);
        if (provider == ScoutDataProtectionProvider.FileSystem)
        {
            if (!environment.IsDevelopment() && !isTesting)
                throw new InvalidOperationException("Filesystem Data Protection is supported only in Development or explicitly configured Testing.");
            if (!string.IsNullOrWhiteSpace(configuration[BlobUriSetting]) ||
                !string.IsNullOrWhiteSpace(configuration[KeyVaultKeyIdSetting]))
                throw new InvalidOperationException("Azure key settings cannot be combined with the FileSystem provider.");

            var path = configuration[FileSystemPathSetting];
            if (isTesting && string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException($"{FileSystemPathSetting} must be explicitly set in the Testing environment.");
            if (string.IsNullOrWhiteSpace(path))
            {
                path = Path.Combine(Path.GetTempPath(), ring == ScoutDataProtectionRing.Player
                    ? "saturday-scout-player-dev-keys"
                    : "saturday-scout-shared-identity-dev-keys");
            }
            if (!Path.IsPathFullyQualified(path))
                throw new InvalidOperationException($"{FileSystemPathSetting} must be an absolute path.");
            Directory.CreateDirectory(path);
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(path));
            return services;
        }

        if (!string.IsNullOrWhiteSpace(configuration[FileSystemPathSetting]))
            throw new InvalidOperationException($"{FileSystemPathSetting} cannot be set when {ProviderSetting}=AzureBlob.");

        var blobUri = ValidateBlobUri(configuration[BlobUriSetting]);
        var keyVaultKeyId = ValidateVersionlessKeyVaultKeyId(configuration[KeyVaultKeyIdSetting]);

        // Development may explicitly select AzureBlob and use the normal Azure CLI/IDE
        // credential chain. Hosted non-Development environments use system-assigned identity.
        TokenCredential credential = environment.IsDevelopment()
            ? new DefaultAzureCredential()
            : new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned);
        dataProtection
            .PersistKeysToAzureBlobStorage(blobUri, credential)
            .ProtectKeysWithAzureKeyVault(keyVaultKeyId, credential);
        return services;
    }

    public static string GetApplicationName(ScoutDataProtectionRing ring) => ring switch
    {
        ScoutDataProtectionRing.Player => ScoutIdentityServices.PlayerDataProtectionApplicationName,
        ScoutDataProtectionRing.AdminApi => ScoutIdentityServices.DataProtectionApplicationName,
        _ => throw new ArgumentOutOfRangeException(nameof(ring))
    };

    private static Uri ValidateBlobUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.EndsWith(".blob.core.windows.net", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException($"{BlobUriSetting} must be an HTTPS Azure Blob URI without credentials or query parameters.");

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2 || segments.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException($"{BlobUriSetting} must identify one container and one blob.");
        return uri;
    }

    private static Uri ValidateVersionlessKeyVaultKeyId(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.EndsWith(".vault.azure.net", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException($"{KeyVaultKeyIdSetting} must be a versionless HTTPS Azure Key Vault key identifier.");

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2 || !string.Equals(segments[0], "keys", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{KeyVaultKeyIdSetting} must omit a Key Vault key version.");
        return uri;
    }
}
