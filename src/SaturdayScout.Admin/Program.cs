using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using SaturdayScout.Admin;
using SaturdayScout.Admin.Components;
using SaturdayScout.Application;
using SaturdayScout.Infrastructure;
using SaturdayScout.Infrastructure.Identity;
using SaturdayScout.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Threading.RateLimiting;

var bootstrapRequested = args.Length > 0 && args[0] == "bootstrap-admin";
var developmentPlayerRequested = args.Length > 0 && args[0] == "provision-dev-player";
var builder = WebApplication.CreateBuilder(bootstrapRequested || developmentPlayerRequested ? [] : args);
builder.Services.AddScoutIdentity(builder.Configuration, builder.Environment);
builder.Services.AddScoped<IAdminScoutService, AdminScoutStore>();
builder.Services.AddScoped<IScoutStore, ScoutStore>();
builder.Services.AddScoped<IScoutService, ScoutService>();

if (bootstrapRequested)
{
    if (args.Length != 4 || args[0] != "bootstrap-admin" || args[1] != "--email" ||
        args[3] != "--confirm-owned-email" ||
        builder.Configuration["SATURDAY_SCOUT_BOOTSTRAP_APPROVED"] != "yes")
        throw new InvalidOperationException("Bootstrap requires bootstrap-admin --email ADDRESS --confirm-owned-email and SATURDAY_SCOUT_BOOTSTRAP_APPROVED=yes.");
    if (Console.IsInputRedirected)
        throw new InvalidOperationException("Bootstrap requires an interactive terminal for the password prompt.");
    await using var bootstrapApp = builder.Build();
    await using var scope = bootstrapApp.Services.CreateAsyncScope();
    Console.Write("Administrator display name: ");
    var name = Console.ReadLine() ?? "";
    var password = ReadPassword("New administrator password: ");
    var confirmation = ReadPassword("Confirm password: ");
    if (password != confirmation) throw new InvalidOperationException("Passwords do not match.");
    var id = await scope.ServiceProvider.GetRequiredService<AccountProvisioningService>()
        .BootstrapFirstAdministratorAsync(args[2], name, password, true, CancellationToken.None);
    Console.WriteLine($"First administrator account created: {id}. MFA enrollment is required before dashboard access.");
    return;
}

if (developmentPlayerRequested)
{
    if (!builder.Environment.IsDevelopment() || args.Length != 9 || args[1] != "--email" ||
        args[3] != "--display-name" || args[5] != "--league-id" ||
        args[7] != "--confirm-owned-email" || args[8] != "--confirm-operator-approval" ||
        builder.Configuration["SATURDAY_SCOUT_DEV_PLAYER_APPROVED"] != "yes")
        throw new InvalidOperationException("Development Player provisioning requires Development, a league ID, explicit operator approval, and email ownership confirmation.");
    if (string.IsNullOrWhiteSpace(args[2]) || string.IsNullOrWhiteSpace(args[4]) ||
        !Guid.TryParse(args[6], out var developmentLeagueId) || developmentLeagueId == Guid.Empty)
        throw new InvalidOperationException("A valid Player email, display name, and league ID are required.");

    var target = new NpgsqlConnectionStringBuilder(builder.Configuration["SATURDAY_SCOUT_DB_CONNECTION"]);
    if (target.Host != "127.0.0.1" || target.Port != 55432 || target.Database != "saturday_scout")
        throw new InvalidOperationException("Development Player provisioning is restricted to the local saturday_scout database on port 55432.");
    await using var playerApp = builder.Build();
    await using var scope = playerApp.Services.CreateAsyncScope();
    var domain = scope.ServiceProvider.GetRequiredService<SaturdayScoutDbContext>();
    var league = await domain.Leagues.AsNoTracking()
        .Where(x => x.Id == developmentLeagueId)
        .Select(x => new { x.Id, x.Name, x.CommissionerUserId }).SingleOrDefaultAsync();
    if (league is null)
        throw new InvalidOperationException("The requested development league does not exist in the local database.");
    Console.WriteLine($"Target league: {league.Name} ({league.Id:D})");
    var provisioning = scope.ServiceProvider.GetRequiredService<AccountProvisioningService>();
    var existingId = await provisioning.FindExistingDevelopmentPlayerAsync(args[2], args[4], league.Id,
        league.CommissionerUserId, operatorApproved: true, emailOwnershipConfirmed: true, CancellationToken.None);
    if (existingId is { } alreadyProvisioned)
    {
        Console.WriteLine($"Development Player is already provisioned and linked: {alreadyProvisioned:D}. No changes made.");
        return;
    }
    Console.Write("New Player password: ");
    var password = ReadPassword();
    Console.Write("Confirm password: ");
    var confirmation = ReadPassword();
    if (password.Length == 0 || password != confirmation)
        throw new InvalidOperationException("Passwords are empty or do not match.");

    var playerId = await provisioning.ProvisionDevelopmentPlayerAsync(args[2], args[4], password, league.Id, league.CommissionerUserId,
            operatorApproved: true, emailOwnershipConfirmed: true, CancellationToken.None);
    Console.WriteLine($"Development Player account and league membership are ready: {playerId:D}. The password was not logged.");
    return;
}

if (!builder.Environment.IsDevelopment())
{
    var publicOrigin = builder.Configuration["SATURDAY_SCOUT_PUBLIC_ORIGIN"];
    if (!Uri.TryCreate(publicOrigin, UriKind.Absolute, out var origin) || origin.Scheme != Uri.UriSchemeHttps ||
        origin.AbsolutePath != "/" || !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment))
        throw new InvalidOperationException("SATURDAY_SCOUT_PUBLIC_ORIGIN must be an HTTPS origin outside Development.");
}

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, AdminRevalidatingAuthenticationStateProvider>();
// This facade is only used by Interactive Server pages. CLI commands build the
// shared Identity host without a Blazor circuit or AuthenticationStateProvider.
builder.Services.AddScoped<AdminPortalService>();
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("SystemAdministrator", policy => policy.RequireAuthenticatedUser()
        .AddRequirements(new SystemAdministratorMfaRequirement()));
    options.AddPolicy("MfaEnrollment", policy => policy.RequireAuthenticatedUser()
        .AddRequirements(new MfaEnrollmentRequirement()));
});
builder.Services.AddAntiforgery();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("mfa", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

var app = builder.Build();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseHttpsRedirection();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/auth") ||
        context.Request.Path.StartsWithSegments("/enroll") ||
        context.Request.Path.StartsWithSegments("/mfa") ||
        context.Request.Path.StartsWithSegments("/recovery") ||
        context.Request.Path.StartsWithSegments("/login"))
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
    }
    await next(context);
});
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapAdminAuthEndpoints();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

static string ReadPassword(string prompt = "")
{
    if (!string.IsNullOrEmpty(prompt)) Console.Write(prompt);
    if (Console.IsInputRedirected)
        return Console.ReadLine() ?? "";
    var chars = new List<char>();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) break;
        if (key.Key == ConsoleKey.Backspace)
        {
            if (chars.Count > 0) chars.RemoveAt(chars.Count - 1);
        }
        else if (!char.IsControl(key.KeyChar) && chars.Count < 1024) chars.Add(key.KeyChar);
    }
    Console.WriteLine();
    return new string(chars.ToArray());
}

public partial class Program;
