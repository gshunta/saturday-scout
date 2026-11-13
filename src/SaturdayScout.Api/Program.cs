using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Antiforgery;
using SaturdayScout.Api;
using SaturdayScout.Application;
using SaturdayScout.Infrastructure;
using SaturdayScout.Infrastructure.Identity;
using SaturdayScout.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi("v1", options => options.AddDocumentTransformer((document, _, _) =>
{
    document.Info.Title = "Saturday Scout API";
    document.Info.Version = "1.0.0";
    document.Info.Description = "Version 1 API contract. Player requests are authenticated by the server-side Player BFF.";
    return Task.CompletedTask;
}));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ScoutExceptionHandler>();
builder.Services.AddScoutIdentity(builder.Configuration, builder.Environment);
builder.Services.AddPlayerAssertionAuthentication(builder.Configuration, builder.Environment.IsDevelopment());
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddScoped<IAuthorizationHandler, LeagueCommissionerAuthorizationHandler>();
builder.Services.AddScoped<IScoutStore, ScoutStore>();
builder.Services.AddScoped<IAdminScoutService, AdminScoutStore>();
builder.Services.AddScoped<IScoutService, ScoutService>();
builder.Services.AddScoped<IResultImportService>(sp => sp.GetRequiredService<IScoutStore>());

if (builder.Environment.IsDevelopment())
{
    builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, DevAuthHandler>("Development", _ => { });
    builder.Services.AddAuthentication().AddPolicyScheme("ScoutDevOrIdentity", null, options =>
        options.ForwardDefaultSelector = context =>
        {
            if (context.Request.Headers.ContainsKey("X-Dev-Subject")) return "Development";
            if (context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return PlayerAssertionDefaults.Scheme;
            return IdentityConstants.ApplicationScheme;
        });
    builder.Services.PostConfigure<AuthenticationOptions>(options =>
    {
        options.DefaultAuthenticateScheme = "ScoutDevOrIdentity";
        options.DefaultChallengeScheme = "ScoutDevOrIdentity";
    });
}
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Player", policy => policy.RequireAuthenticatedUser().RequireRole(ScoutRoles.Player)
        .AddAuthenticationSchemes(PlayerAssertionDefaults.ApiUserScheme));
    options.AddPolicy("SystemAdministrator", policy => policy.RequireAuthenticatedUser()
        .AddRequirements(new SystemAdministratorMfaRequirement()));
    options.AddPolicy("LeagueCommissioner", policy => policy.RequireAuthenticatedUser()
        .RequireRole(ScoutRoles.LeagueCommissioner).AddRequirements(new LeagueCommissionerRequirement()));
});

var app = builder.Build();
app.UseExceptionHandler();
if (app.Environment.IsDevelopment()) app.MapOpenApi();
else app.UseHsts();
app.UseHttpsRedirection();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/v1") &&
        context.Request.Method is not ("GET" or "HEAD" or "OPTIONS" or "TRACE") &&
        context.User.Identity?.AuthenticationType == IdentityConstants.ApplicationScheme)
    {
        if (!await context.RequestServices.GetRequiredService<IAntiforgery>().IsRequestValidAsync(context))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
    }
    await next(context);
});
app.UseAntiforgery();
app.UseAuthorization();

var authApi = app.MapGroup("/api/v1/auth").RequireAuthorization();
authApi.MapGet("/antiforgery", (HttpContext context, IAntiforgery antiforgery) =>
    Results.Ok(new { token = antiforgery.GetAndStoreTokens(context).RequestToken }));
var api = app.MapGroup("/api/v1");
var adminApi = app.MapGroup("/api/v1/admin").RequireAuthorization("SystemAdministrator");
adminApi.MapGet("/dashboard", async (IAdminScoutService service, CancellationToken ct) =>
    Results.Ok(await service.GetDashboardAsync(ct)));
adminApi.MapGet("/games", async ([AsParameters] AdminGameQuery query, IAdminScoutService service, CancellationToken ct) =>
    Results.Ok(await service.BrowseGamesAsync(query, ct)));
adminApi.MapGet("/leagues", async (IAdminScoutService service, CancellationToken ct) =>
    Results.Ok(await service.GetLeaguesAsync(ct)));
adminApi.MapGet("/commissioners", async (IAdminScoutService service, CancellationToken ct) =>
    Results.Ok(await service.GetEligibleCommissionersAsync(ct)));
adminApi.MapPost("/leagues", async (CreateAdminLeagueRequest request, IAdminScoutService service, CancellationToken ct) =>
{
    var league = await service.CreateLeagueAsync(request, ct);
    return Results.Created($"/api/v1/admin/leagues/{league.Id}", league);
});
adminApi.MapGet("/slates", async (IAdminScoutService service, CancellationToken ct) =>
    Results.Ok(await service.GetSlatesAsync(ct)));
adminApi.MapPost("/slates", async (CreateAdminSlateRequest request, IAdminScoutService service, CancellationToken ct) =>
{
    var slate = await service.CreateSlateAsync(request, ct);
    return Results.Created($"/api/v1/admin/slates/{slate.Id}", slate);
});
adminApi.MapGet("/slates/{slateId:guid}", async (Guid slateId, IAdminScoutService service, CancellationToken ct) =>
    Results.Ok(await service.GetSlateAsync(slateId, ct)));
adminApi.MapPut("/slates/{slateId:guid}/games", async (Guid slateId, UpdateAdminSlateGamesRequest request,
    IAdminScoutService service, CancellationToken ct) =>
    Results.Ok(await service.UpdateSlateGamesAsync(slateId, request, ct)));
adminApi.MapPost("/slates/{slateId:guid}/randomize", async (Guid slateId, RandomizeAdminSlateRequest request,
    IAdminScoutService service, CancellationToken ct) =>
    Results.Ok(await service.RandomizeSlateGamesAsync(slateId, request, ct)));
adminApi.MapPost("/slates/{slateId:guid}/publish", async (Guid slateId, PublishAdminSlateRequest request,
    IAdminScoutService service, CancellationToken ct) =>
    Results.Ok(await service.PublishSlateAsync(slateId, request, ct)));
if (app.Environment.IsDevelopment()) api.RequireAuthorization();
else api.RequireAuthorization("Player");
api.MapGet("/leagues/me", async (HttpContext http, IScoutService service, CancellationToken ct) =>
    Results.Ok(await service.GetMyLeaguesAsync(Actor(http), ct)));
api.MapGet("/leagues/{leagueId:guid}/slates/active", async (Guid leagueId, HttpContext http,
    IScoutService service, CancellationToken ct) =>
{
    var slate = await service.GetActiveCollegeSlateAsync(Actor(http), leagueId, ct);
    return slate is null ? Results.NoContent() : Results.Ok(slate);
});
api.MapGet("/leagues/{leagueId:guid}/slates/weeks", async (Guid leagueId, int season, HttpContext http,
    IScoutService service, CancellationToken ct) =>
    Results.Ok(await service.GetCollegeSlateWeeksAsync(Actor(http), leagueId, season, ct)));
api.MapGet("/games", async (string sport, int season, int week, int? page, int? pageSize,
    HttpContext http, IScoutService service, CancellationToken ct) =>
    Results.Ok(await service.BrowseGamesAsync(Actor(http), sport, season, week, page ?? 1, pageSize ?? 50, ct)));
api.MapPost("/leagues", async (CreateLeagueRequest request, HttpContext http, IScoutService service, CancellationToken ct) =>
{
    var league = await service.CreateLeagueAsync(Actor(http), request, ct);
    return Results.Created($"/api/v1/leagues/{league.Id}", league);
});
api.MapGet("/leagues/{leagueId:guid}", async (Guid leagueId, HttpContext http, IScoutService service, CancellationToken ct) =>
    Results.Ok(await service.GetLeagueAsync(Actor(http), leagueId, ct)));
api.MapPost("/leagues/{leagueId:guid}/members", async (Guid leagueId, AddMemberRequest request,
    HttpContext http, IScoutService service, CancellationToken ct) =>
    Results.Ok(await service.AddMemberAsync(Actor(http), leagueId, request, ct)));
api.MapPost("/leagues/{leagueId:guid}/slates", async (Guid leagueId, CreateSlateRequest request,
    HttpContext http, IScoutService service, CancellationToken ct) =>
{
    var slate = await service.CreateSlateAsync(Actor(http), leagueId, request, ct);
    return Results.Created($"/api/v1/slates/{slate.Id}", slate);
});
api.MapGet("/leagues/{leagueId:guid}/leaderboard", async (Guid leagueId, string scope, int season, int? week,
    HttpContext http, IScoutService service, CancellationToken ct) =>
    Results.Ok(await service.GetLeaderboardAsync(Actor(http), leagueId, scope, season, week, ct)));
api.MapPut("/slates/{slateId:guid}/games", async (Guid slateId, SetSlateGamesRequest request,
    HttpContext http, IScoutService service, CancellationToken ct) =>
    Results.Ok(await service.SetSlateGamesAsync(Actor(http), slateId, request, ct)));
api.MapPost("/slates/{slateId:guid}/publish", async (Guid slateId, PublishSlateRequest request,
    HttpContext http, IScoutService service, CancellationToken ct) =>
    Results.Ok(await service.PublishSlateAsync(Actor(http), slateId, request, ct)));
api.MapGet("/slates/{slateId:guid}", async (Guid slateId, HttpContext http, IScoutService service, CancellationToken ct) =>
    Results.Ok(await service.GetSlateAsync(Actor(http), slateId, ct)));
api.MapPut("/slates/{slateId:guid}/picks", async (Guid slateId, SavePicksRequest request,
    HttpContext http, IScoutService service, CancellationToken ct) =>
    Results.Ok(await service.SavePicksAsync(Actor(http), slateId, request, ct)));
api.MapPost("/slates/{slateId:guid}/submit", async (Guid slateId, SubmitRequest request,
    HttpContext http, IScoutService service, CancellationToken ct) =>
    Results.Ok(await service.SubmitAsync(Actor(http), slateId, request.ExpectedVersion, ct)));
api.MapGet("/slates/{slateId:guid}/picks/me", async (Guid slateId, HttpContext http, IScoutService service, CancellationToken ct) =>
{
    var sheet = await service.GetMySheetAsync(Actor(http), slateId, ct);
    return sheet is null ? Results.NotFound() : Results.Ok(sheet);
});

app.Run();

static Guid Actor(HttpContext context) => Guid.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);

public sealed record SubmitRequest(long? ExpectedVersion);
public partial class Program;
