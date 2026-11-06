using System.Net;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SaturdayScout.Infrastructure.Identity;
using SaturdayScout.Infrastructure.Persistence;
using Xunit;

namespace SaturdayScout.Admin.IntegrationTests;

public sealed class AdminAuthenticationTests
{
    private const string Password = "Disposable-test-Password-123!";

    [Fact]
    public void ProductionRequiresSharedKeyStorage()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("SATURDAY_SCOUT_DB_CONNECTION", "Host=127.0.0.1;Database=unused;Username=unused;Password=unused");
            builder.UseSetting("SATURDAY_SCOUT_DP_KEYS_PATH", "");
            builder.UseSetting("SATURDAY_SCOUT_PUBLIC_ORIGIN", "https://admin.example.test");
        });
        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }

    [Fact]
    public void ProductionRequiresHttpsPublicOrigin()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("SATURDAY_SCOUT_DB_CONNECTION", "Host=127.0.0.1;Database=unused;Username=unused;Password=unused");
            builder.UseSetting("SATURDAY_SCOUT_DP_KEYS_PATH", Path.GetTempPath());
            builder.UseSetting("SATURDAY_SCOUT_PUBLIC_ORIGIN", "http://admin.example.test");
        });
        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }

    [PostgresFact]
    public async Task AdministratorLoginEnrollmentMfaRecoveryAndLogout()
    {
        await using var database = await AdminTestDatabase.CreateAsync();
        await database.BootstrapAsync("admin@example.test", Password);
        using var factory = database.Factory();
        using var client = Client(factory);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/session")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsync("/auth/login", new FormUrlEncodedContent([
                new("email", "admin@example.test"), new("password", Password)
            ]))).StatusCode);
        var login = await PostFormAsync(client, "/login", "/auth/login",
            ("email", "admin@example.test"), ("password", Password));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/enroll", login.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/auth/session")).StatusCode);
        var passwordOnlyDashboard = await client.GetAsync("/dashboard");
        Assert.Equal(HttpStatusCode.Forbidden, passwordOnlyDashboard.StatusCode);
        Assert.DoesNotContain("Your administrator session has completed MFA",
            await passwordOnlyDashboard.Content.ReadAsStringAsync());

        var enrollmentPage = await client.GetAsync("/enroll");
        Assert.Equal(HttpStatusCode.OK, enrollmentPage.StatusCode);
        Assert.Contains("Manual key", await enrollmentPage.Content.ReadAsStringAsync());
        var code = await database.AuthenticatorCodeAsync("admin@example.test");
        var enrollment = await PostFormAsync(client, "/enroll", "/auth/enroll", ("code", code));
        Assert.True(enrollment.StatusCode == HttpStatusCode.OK,
            $"Enrollment returned {(int)enrollment.StatusCode} at {enrollment.Headers.Location}.");
        var enrollmentHtml = await enrollment.Content.ReadAsStringAsync();
        Assert.Contains("Save your recovery codes", enrollmentHtml);
        var recoveryCode = Regex.Match(enrollmentHtml, "<li><code>([^<]+)</code></li>").Groups[1].Value;
        Assert.NotEmpty(recoveryCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/auth/session")).StatusCode);

        var dashboard = await client.GetAsync("/dashboard");
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        var logout = await PostFormAsync(client, "/dashboard", "/auth/logout");
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/session")).StatusCode);

        login = await PostFormAsync(client, "/login", "/auth/login",
            ("email", "admin@example.test"), ("password", Password));
        Assert.Equal("/mfa", login.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/session")).StatusCode);
        var wrongCode = await PostFormAsync(client, "/mfa", "/auth/mfa", ("code", "000000"));
        Assert.Equal("/mfa?error=1", wrongCode.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/session")).StatusCode);
        code = await database.AuthenticatorCodeAsync("admin@example.test");
        var verified = await PostFormAsync(client, "/mfa", "/auth/mfa", ("code", code));
        Assert.Equal("/dashboard", verified.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/auth/session")).StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByEmailAsync("admin@example.test"))!;
            Assert.True((await users.UpdateSecurityStampAsync(user)).Succeeded);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/session")).StatusCode);

        login = await PostFormAsync(client, "/login", "/auth/login",
            ("email", "admin@example.test"), ("password", Password));
        Assert.Equal("/mfa", login.Headers.Location?.OriginalString);
        code = await database.AuthenticatorCodeAsync("admin@example.test");
        verified = await PostFormAsync(client, "/mfa", "/auth/mfa", ("code", code));
        Assert.Equal("/dashboard", verified.Headers.Location?.OriginalString);

        await PostFormAsync(client, "/dashboard", "/auth/logout");
        login = await PostFormAsync(client, "/login", "/auth/login",
            ("email", "admin@example.test"), ("password", Password));
        Assert.Equal("/mfa", login.Headers.Location?.OriginalString);
        var recovered = await PostFormAsync(client, "/recovery", "/auth/recovery", ("code", recoveryCode));
        Assert.Equal("/dashboard", recovered.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/auth/session")).StatusCode);

        await PostFormAsync(client, "/dashboard", "/auth/logout");
        using var replayFactory = database.Factory();
        using var replayClient = Client(replayFactory);
        login = await PostFormAsync(replayClient, "/login", "/auth/login",
            ("email", "admin@example.test"), ("password", Password));
        Assert.Equal("/mfa", login.Headers.Location?.OriginalString);
        var replay = await PostFormAsync(replayClient, "/recovery", "/auth/recovery", ("code", recoveryCode));
        Assert.Equal("/recovery?error=1", replay.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, (await replayClient.GetAsync("/auth/session")).StatusCode);
    }

    [PostgresFact]
    public async Task InvalidLoginLocksAccountAndDisabledAccountCannotSignIn()
    {
        await using var database = await AdminTestDatabase.CreateAsync();
        await database.BootstrapAsync("admin@example.test", Password);
        using var factory = database.Factory();
        using var client = Client(factory);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var response = await PostFormAsync(client, "/login", "/auth/login",
                ("email", "admin@example.test"), ("password", "Wrong-Password-123!"));
            Assert.Equal("/login?error=1", response.Headers.Location?.OriginalString);
        }
        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await PostFormAsync(client, "/login", "/auth/login",
                ("email", "admin@example.test"), ("password", "Wrong-Password-123!"))).StatusCode);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByEmailAsync("admin@example.test"))!;
            Assert.True(await users.IsLockedOutAsync(user));
            await users.SetLockoutEndDateAsync(user, null);
            await users.ResetAccessFailedCountAsync(user);
            await users.AddClaimAsync(user, new System.Security.Claims.Claim(ScoutIdentityServices.DisabledClaim, "true"));
        }
        using var secondFactory = database.Factory();
        using var freshClient = Client(secondFactory);
        var disabled = await PostFormAsync(freshClient, "/login", "/auth/login",
            ("email", "admin@example.test"), ("password", Password));
        Assert.Equal("/login?error=1", disabled.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, (await freshClient.GetAsync("/auth/session")).StatusCode);
    }

    [PostgresFact]
    public async Task NonAdministratorAndDuplicateBootstrapAreDenied()
    {
        await using var database = await AdminTestDatabase.CreateAsync();
        await database.BootstrapAsync("admin@example.test", Password);
        using var factory = database.Factory();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var provision = scope.ServiceProvider.GetRequiredService<AccountProvisioningService>();
            await Assert.ThrowsAsync<InvalidOperationException>(() => provision
                .BootstrapFirstAdministratorAsync("other@example.test", "Other", Password, false, default));
            await Assert.ThrowsAsync<InvalidOperationException>(() => provision
                .BootstrapFirstAdministratorAsync("other@example.test", "Other", Password, true, default));
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<SaturdayScoutDbContext>().Users.CountAsync());
        }
        await database.CreatePlayerAsync("player@example.test", Password);
        using var client = Client(factory);
        var login = await PostFormAsync(client, "/login", "/auth/login",
            ("email", "player@example.test"), ("password", Password));
        Assert.Equal("/login?error=1", login.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/session")).StatusCode);
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory) => factory.CreateClient(
        new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });

    private static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string formPage, string action,
        params (string Name, string Value)[] fields)
    {
        var html = await client.GetStringAsync(formPage);
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "Expected an antiforgery token in the form.");
        var values = fields.Select(x => new KeyValuePair<string, string>(x.Name, x.Value)).ToList();
        values.Add(new("__RequestVerificationToken", WebUtility.HtmlDecode(match.Groups[1].Value)));
        return await client.PostAsync(action, new FormUrlEncodedContent(values));
    }
}

public sealed class AdminTestDatabase : IAsyncDisposable
{
    private readonly NpgsqlConnection admin;
    private readonly string name;
    private readonly string connection;
    private readonly string keyPath;

    private AdminTestDatabase(NpgsqlConnection admin, string name, string connection, string keyPath)
    {
        this.admin = admin;
        this.name = name;
        this.connection = connection;
        this.keyPath = keyPath;
    }

    public static async Task<AdminTestDatabase> CreateAsync()
    {
        var builder = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SATURDAY_SCOUT_TEST_ADMIN_CONNECTION"))
        { Database = "postgres" };
        var admin = new NpgsqlConnection(builder.ConnectionString);
        await admin.OpenAsync();
        var name = $"saturday_scout_admin_test_{Guid.NewGuid():N}";
        await using (var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin))
            await command.ExecuteNonQueryAsync();
        var testConnection = new NpgsqlConnectionStringBuilder(builder.ConnectionString)
        { Database = name, Pooling = false }.ConnectionString;
        var keyPath = Path.Combine(Path.GetTempPath(), $"saturday-scout-admin-test-keys-{Guid.NewGuid():N}");
        Directory.CreateDirectory(keyPath);
        var database = new AdminTestDatabase(admin, name, testConnection, keyPath);
        await using var domain = new SaturdayScoutDbContext(new DbContextOptionsBuilder<SaturdayScoutDbContext>()
            .UseNpgsql(testConnection).Options);
        await domain.Database.MigrateAsync();
        await using var identity = new ScoutIdentityDbContext(new DbContextOptionsBuilder<ScoutIdentityDbContext>()
            .UseNpgsql(testConnection, x => x.MigrationsHistoryTable("__EFMigrationsHistory", "auth")).Options);
        await identity.Database.MigrateAsync();
        return database;
    }

    public WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("SATURDAY_SCOUT_DB_CONNECTION", connection);
            builder.UseSetting("SATURDAY_SCOUT_DP_KEYS_PATH", keyPath);
            builder.UseSetting("SATURDAY_SCOUT_PUBLIC_ORIGIN", "https://localhost");
        });

    public async Task BootstrapAsync(string email, string password)
    {
        using var factory = Factory();
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AccountProvisioningService>()
            .BootstrapFirstAdministratorAsync(email, "Test Administrator", password, true, default);
    }

    public async Task<string> AuthenticatorCodeAsync(string email)
    {
        using var factory = Factory();
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await users.FindByEmailAsync(email))!;
        var key = (await users.GetAuthenticatorKeyAsync(user))!;
        var code = Totp(key);
        Assert.True(await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, code));
        return code;
    }

    private static string Totp(string base32Key)
    {
        var bytes = new List<byte>();
        var bits = 0;
        var buffer = 0;
        foreach (var character in base32Key.ToUpperInvariant())
        {
            var value = character is >= 'A' and <= 'Z' ? character - 'A' : character - '2' + 26;
            if (value is < 0 or > 31) continue;
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits < 8) continue;
            bits -= 8;
            bytes.Add((byte)(buffer >> bits));
            buffer &= (1 << bits) - 1;
        }
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        var digest = HMACSHA1.HashData(bytes.ToArray(), counter);
        var offset = digest[^1] & 0x0f;
        var binary = ((digest[offset] & 0x7f) << 24) | (digest[offset + 1] << 16) |
            (digest[offset + 2] << 8) | digest[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    public async Task CreatePlayerAsync(string email, string password)
    {
        using var factory = Factory();
        await using var scope = factory.Services.CreateAsyncScope();
        var identity = scope.ServiceProvider.GetRequiredService<ScoutIdentityDbContext>();
        const string token = "disposable-player-invitation";
        identity.Invitations.Add(new AccountInvitation
        {
            Id = Guid.NewGuid(), Email = email, NormalizedEmail = email.ToUpperInvariant(),
            TokenHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(token))),
            Role = ScoutRoles.Player, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1)
        });
        await identity.SaveChangesAsync();
        var id = await scope.ServiceProvider.GetRequiredService<AccountProvisioningService>()
            .AcceptInvitationAsync(token, email, "Test Player", password, default);
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await users.FindByIdAsync(id.ToString()))!;
        var confirmation = await users.GenerateEmailConfirmationTokenAsync(user);
        Assert.True((await users.ConfirmEmailAsync(user, confirmation)).Succeeded);
    }

    public async ValueTask DisposeAsync()
    {
        await using var command = new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", admin);
        await command.ExecuteNonQueryAsync();
        await admin.DisposeAsync();
        Directory.Delete(keyPath, recursive: true);
    }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SATURDAY_SCOUT_TEST_ADMIN_CONNECTION")))
            Skip = "Set SATURDAY_SCOUT_TEST_ADMIN_CONNECTION to run disposable PostgreSQL integration tests.";
    }
}
