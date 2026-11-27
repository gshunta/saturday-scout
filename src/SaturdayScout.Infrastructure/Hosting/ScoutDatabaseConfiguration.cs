using System.Data.Common;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace SaturdayScout.Infrastructure.Hosting;

/// <summary>Validates database connection security settings without opening a connection.</summary>
public static class ScoutDatabaseConfiguration
{
    public static string Validate(string? connectionString, IHostEnvironment environment)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("SATURDAY_SCOUT_DB_CONNECTION is required.");

        if (environment.IsDevelopment() || environment.IsEnvironment("Testing")) return connectionString;

        NpgsqlConnectionStringBuilder parsed;
        DbConnectionStringBuilder raw;
        try
        {
            parsed = new NpgsqlConnectionStringBuilder(connectionString);
            raw = new DbConnectionStringBuilder { ConnectionString = connectionString };
        }
        catch (Exception exception) when (exception is ArgumentException or KeyNotFoundException or FormatException)
        {
            throw new InvalidOperationException("SATURDAY_SCOUT_DB_CONNECTION is malformed.");
        }

        var trustServerCertificate = raw.Keys.Cast<string>()
            .FirstOrDefault(key => string.Concat(key.Where(char.IsLetterOrDigit))
                .Equals("trustservercertificate", StringComparison.OrdinalIgnoreCase));
        if (trustServerCertificate is not null &&
            (!bool.TryParse(Convert.ToString(raw[trustServerCertificate]), out var trustEnabled) || trustEnabled))
            throw new InvalidOperationException(
                "Production SATURDAY_SCOUT_DB_CONNECTION cannot specify Trust Server Certificate; use verified server TLS.");

        if (parsed.SslMode != SslMode.VerifyFull)
            throw new InvalidOperationException(
                "Production SATURDAY_SCOUT_DB_CONNECTION must use SSL Mode=VerifyFull so the server certificate and hostname are validated.");

        return connectionString;
    }
}
