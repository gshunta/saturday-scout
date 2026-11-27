using System.Security.Cryptography;

namespace SaturdayScout.Player;

/// <summary>Imports and validates Player's RSA assertion signing key without exposing PEM in errors.</summary>
public static class PlayerApiSigningKey
{
    private const int MinimumRsaKeySize = 3072;
    private const string UnresolvedKeyVaultReferencePrefix = "@Microsoft.KeyVault(";

    public static RSA LoadFromPem(string? pem)
    {
        if (string.IsNullOrWhiteSpace(pem))
            throw new InvalidOperationException("Player API assertion private-key configuration is missing.");
        if (pem.TrimStart().StartsWith(UnresolvedKeyVaultReferencePrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Player API assertion private-key secret reference did not resolve.");

        var key = RSA.Create();
        try
        {
            // ImportFromPem accepts multiline PEM directly; no newline normalization is required.
            key.ImportFromPem(pem);
            ValidateCanSign(key);
            return key;
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            key.Dispose();
            throw new InvalidOperationException(
                "Player API assertion private-key PEM is invalid or does not contain usable RSA private signing material.");
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    public static RSA LoadFromFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("Player API assertion private-key file path must be absolute.");

        string pem;
        try
        {
            pem = File.ReadAllText(path);
        }
        catch
        {
            throw new InvalidOperationException("Player API assertion private-key file could not be read.");
        }

        return LoadFromPem(pem);
    }

    private static void ValidateCanSign(RSA key)
    {
        if (key.KeySize < MinimumRsaKeySize)
            throw new InvalidOperationException("The Player API assertion RSA key must be at least 3072 bits.");

        var challenge = RandomNumberGenerator.GetBytes(32);
        byte[]? signature = null;
        try
        {
            signature = key.SignData(challenge, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("Player API assertion PEM must contain RSA private signing material.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(challenge);
            if (signature is not null) CryptographicOperations.ZeroMemory(signature);
        }
    }
}
