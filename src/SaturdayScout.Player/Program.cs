using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using SaturdayScout.Infrastructure;
using SaturdayScout.Infrastructure.Identity;
using SaturdayScout.Player;
using SaturdayScout.Player.Components;

var builder = WebApplication.CreateBuilder(args);

ScoutIdentityServices.AddScoutIdentity(builder.Services, builder.Configuration, builder.Environment);
var playerCookie = builder.Configuration.GetSection(PlayerCookieOptions.SectionName).Get<PlayerCookieOptions>() ?? new();
if (!playerCookie.Name.StartsWith("__Host-", StringComparison.Ordinal) ||
    playerCookie.IdleTimeoutMinutes is < 1 or > 1440 || playerCookie.AbsoluteTimeoutHours is < 1 or > 168)
    throw new InvalidOperationException("Player cookie settings are invalid.");

builder.Services.Configure<PlayerHostOptions>(builder.Configuration.GetSection(PlayerHostOptions.SectionName));
builder.Services.Configure<PlayerCookieOptions>(builder.Configuration.GetSection(PlayerCookieOptions.SectionName));
builder.Services.Configure<PlayerApiAssertionOptions>(builder.Configuration.GetSection(PlayerApiAssertionOptions.SectionName));
var assertionOptions = builder.Configuration.GetSection(PlayerApiAssertionOptions.SectionName).Get<PlayerApiAssertionOptions>() ?? new();
var apiOriginText = builder.Configuration[$"{PlayerHostOptions.SectionName}:ApiOrigin"];
if (!builder.Environment.IsDevelopment())
{
    if (!Uri.TryCreate(apiOriginText, UriKind.Absolute, out var apiOrigin) || apiOrigin.Scheme != Uri.UriSchemeHttps ||
        !string.IsNullOrEmpty(apiOrigin.UserInfo) ||
        apiOrigin.AbsolutePath != "/" || !string.IsNullOrEmpty(apiOrigin.Query) || !string.IsNullOrEmpty(apiOrigin.Fragment))
        throw new InvalidOperationException("Player:ApiOrigin must be an HTTPS origin outside Development.");
    if (string.IsNullOrWhiteSpace(assertionOptions.Issuer) || string.IsNullOrWhiteSpace(assertionOptions.Audience) ||
        string.IsNullOrWhiteSpace(assertionOptions.ClientId) || string.IsNullOrWhiteSpace(assertionOptions.KeyId) ||
        !Path.IsPathFullyQualified(assertionOptions.PrivateKeyPath) || !File.Exists(assertionOptions.PrivateKeyPath) ||
        assertionOptions.TokenLifetimeSeconds is < 30 or > 180)
        throw new InvalidOperationException("Player API assertion signing configuration is required outside Development.");
    PlayerApiAssertionIssuer.ValidatePrivateKeyFile(assertionOptions.PrivateKeyPath);
}
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IPlayerApiAssertionIssuer, PlayerApiAssertionIssuer>();
builder.Services.AddHttpClient<IPlayerApiClient, PlayerApiClient>((services, client) =>
{
    var configuredOrigin = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<PlayerHostOptions>>().Value.ApiOrigin;
    if (Uri.TryCreate(configuredOrigin, UriKind.Absolute, out var origin) && origin.Scheme is "http" or "https")
        client.BaseAddress = origin;
    client.Timeout = TimeSpan.FromSeconds(20);
});
builder.Services.AddScoped<PlayerPortalService>();
builder.Services.AddScoped<PlayerLeagueSelectionState>();
builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = PlayerAuthDefaults.Scheme;
        options.DefaultChallengeScheme = PlayerAuthDefaults.Scheme;
        options.DefaultSignInScheme = PlayerAuthDefaults.Scheme;
    })
    .AddCookie(PlayerAuthDefaults.Scheme, options =>
    {
        options.Cookie.Name = playerCookie.Name;
        options.Cookie.Path = "/";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(playerCookie.IdleTimeoutMinutes);
        options.SlidingExpiration = true;
        options.Events.OnValidatePrincipal = context => ScoutIdentityServices.ValidateCookieAsync(
            context, TimeSpan.FromHours(playerCookie.AbsoluteTimeoutHours), PlayerAuthDefaults.Scheme, ScoutRoles.Player);
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
builder.Services.AddAuthorization(options => options.AddPolicy("Player",
    policy => policy.RequireAuthenticatedUser().RequireRole(ScoutRoles.Player)));
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, PlayerRevalidatingAuthenticationStateProvider>();
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var app = builder.Build();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapPlayerAuthEndpoints();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

public partial class Program;
