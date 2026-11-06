using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SaturdayScout.Infrastructure.Persistence;

namespace SaturdayScout.Infrastructure.Identity;

public static class ScoutIdentityServices
{
    public const string DataProtectionApplicationName = "SaturdayScout.SharedIdentity.v1";
    public const string CookieName = "__Host-SaturdayScout";
    public const string DisabledClaim = "saturday_scout:disabled";
    public const string AuthenticationTimeClaim = "auth_time";
    public const string MfaClaim = "amr";

    public static IServiceCollection AddScoutIdentity(this IServiceCollection services,
        IConfiguration configuration, IHostEnvironment environment)
    {
        var connection = configuration["SATURDAY_SCOUT_DB_CONNECTION"];
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("SATURDAY_SCOUT_DB_CONNECTION is required.");
        var keyPath = configuration["SATURDAY_SCOUT_DP_KEYS_PATH"];
        if (string.IsNullOrWhiteSpace(keyPath))
        {
            if (!environment.IsDevelopment())
                throw new InvalidOperationException("SATURDAY_SCOUT_DP_KEYS_PATH is required outside Development.");
            keyPath = Path.Combine(Path.GetTempPath(), "saturday-scout-dev-keys");
        }
        if (!Path.IsPathFullyQualified(keyPath))
            throw new InvalidOperationException("SATURDAY_SCOUT_DP_KEYS_PATH must be an absolute path.");
        if (environment.IsDevelopment()) Directory.CreateDirectory(keyPath);
        else if (!Directory.Exists(keyPath))
            throw new InvalidOperationException("SATURDAY_SCOUT_DP_KEYS_PATH must name an existing directory.");

        services.AddDataProtection().SetApplicationName(DataProtectionApplicationName)
            .PersistKeysToFileSystem(new DirectoryInfo(keyPath));
        services.AddDbContext<SaturdayScoutDbContext>(options => options.UseNpgsql(connection));
        services.AddDbContext<ScoutIdentityDbContext>(options => options.UseNpgsql(connection,
            npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "auth")));
        services.AddIdentity<ApplicationUser, IdentityRole<Guid>>(options =>
        {
            options.User.RequireUniqueEmail = true;
            options.SignIn.RequireConfirmedEmail = true;
            options.Password.RequiredLength = 12;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.AllowedForNewUsers = true;
        }).AddEntityFrameworkStores<ScoutIdentityDbContext>().AddDefaultTokenProviders();
        services.Configure<SecurityStampValidatorOptions>(options =>
            options.ValidationInterval = TimeSpan.FromMinutes(1));
        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = CookieName;
            options.Cookie.Path = "/";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = true;
            options.Events.OnValidatePrincipal = ValidateCookieAsync;
            options.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        });
        services.AddScoped<AccountProvisioningService>();
        services.AddScoped<Microsoft.AspNetCore.Authorization.IAuthorizationHandler,
            SystemAdministratorMfaHandler>();
        services.AddScoped<Microsoft.AspNetCore.Authorization.IAuthorizationHandler,
            MfaEnrollmentHandler>();
        return services;
    }

    public static Claim PasswordClaim() => new(MfaClaim, "pwd");
    public static Claim MfaVerifiedClaim() => new(MfaClaim, "mfa");
    public static Claim AuthenticationTime() => new(AuthenticationTimeClaim,
        DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));

    public static bool RecentlyAuthenticated(ClaimsPrincipal principal, TimeSpan maximumAge)
    {
        if (!long.TryParse(principal.FindFirstValue(AuthenticationTimeClaim),
                NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)) return false;
        try
        {
            var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(seconds);
            return age >= TimeSpan.Zero && age <= maximumAge;
        }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    public static async Task<bool> IsDisabledAsync(UserManager<ApplicationUser> users, ApplicationUser user) =>
        (await users.GetClaimsAsync(user)).Any(x => x.Type == DisabledClaim && x.Value == "true");

    private static async Task ValidateCookieAsync(
        Microsoft.AspNetCore.Authentication.Cookies.CookieValidatePrincipalContext context)
    {
        if (context.Principal?.Identity?.IsAuthenticated != true) return;
        // Validate the stored stamp without replacing the principal. Rebuilding the
        // principal would discard the server-issued MFA and authentication-time claims.
        var signIn = context.HttpContext.RequestServices.GetRequiredService<SignInManager<ApplicationUser>>();
        var user = await signIn.ValidateSecurityStampAsync(context.Principal);
        var users = context.HttpContext.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
        var domain = context.HttpContext.RequestServices.GetRequiredService<SaturdayScoutDbContext>();
        if (user is not null && user.EmailConfirmed &&
            !await users.IsLockedOutAsync(user) && !await IsDisabledAsync(users, user) &&
            await domain.Users.AsNoTracking().AnyAsync(x => x.Id == user.Id) &&
            RecentlyAuthenticated(context.Principal, TimeSpan.FromHours(12))) return;

        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
    }
}
