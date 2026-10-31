using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using SaturdayScout.Api;
using SaturdayScout.Application;
using SaturdayScout.Infrastructure;
using SaturdayScout.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ScoutExceptionHandler>();
var connection = builder.Configuration["SATURDAY_SCOUT_DB_CONNECTION"];
if (string.IsNullOrWhiteSpace(connection))
    throw new InvalidOperationException("SATURDAY_SCOUT_DB_CONNECTION is required.");
builder.Services.AddDbContext<SaturdayScoutDbContext>(options => options.UseNpgsql(connection));
builder.Services.AddScoped<IScoutStore, ScoutStore>();
builder.Services.AddScoped<IScoutService, ScoutService>();
builder.Services.AddScoped<IResultImportService>(sp => sp.GetRequiredService<IScoutStore>());

const string authScheme = "SaturdayScout";
builder.Services.AddAuthentication(authScheme).AddScheme<AuthenticationSchemeOptions, DenyAuthHandler>(authScheme, _ => { });
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, DevAuthHandler>("Development", _ => { });
    builder.Services.PostConfigure<AuthenticationOptions>(options =>
    {
        options.DefaultAuthenticateScheme = "Development";
        options.DefaultChallengeScheme = "Development";
    });
}
builder.Services.AddAuthorization();

var app = builder.Build();
app.UseExceptionHandler();
if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

var api = app.MapGroup("/api/v1").RequireAuthorization();
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
