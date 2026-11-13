using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SaturdayScout.Infrastructure.Identity;
using SaturdayScout.Infrastructure.Persistence;

namespace SaturdayScout.Player;

public static class PlayerAuthEndpoints
{
    public static WebApplication MapPlayerAuthEndpoints(this WebApplication app)
    {
        app.MapGet("/auth/antiforgery", (HttpContext context, IAntiforgery antiforgery) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { token = antiforgery.GetAndStoreTokens(context).RequestToken });
        });
        app.MapPost("/auth/login", Login);
        app.MapPost("/auth/logout", Logout).RequireAuthorization("Player");
        app.MapGet("/auth/session", (HttpContext context, ClaimsPrincipal user) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new
            {
                userId = user.FindFirstValue(ClaimTypes.NameIdentifier),
                displayName = user.FindFirstValue(ClaimTypes.GivenName),
                roles = new[] { ScoutRoles.Player }
            });
        }).RequireAuthorization("Player");
        return app;
    }

    private static async Task<IResult> Login(HttpContext context, IAntiforgery antiforgery,
        UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn,
        SaturdayScoutDbContext domain)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        if (!await antiforgery.IsRequestValidAsync(context)) return Results.BadRequest();

        var form = await context.Request.ReadFormAsync();
        var email = form["email"].ToString().Trim();
        var password = form["password"].ToString();
        if (email.Length is < 3 or > 256 || password.Length is < 1 or > 1024)
            return LoginFailed();

        var user = await users.FindByEmailAsync(email);
        if (user is null || !user.EmailConfirmed ||
            !await users.IsInRoleAsync(user, ScoutRoles.Player) ||
            await users.GetTwoFactorEnabledAsync(user) ||
            await ScoutIdentityServices.IsDisabledAsync(users, user) ||
            !await domain.Users.AsNoTracking().AnyAsync(x => x.Id == user.Id, context.RequestAborted))
            return LoginFailed();

        signIn.AuthenticationScheme = PlayerAuthDefaults.Scheme;
        var result = await signIn.PasswordSignInAsync(user, password, isPersistent: false, lockoutOnFailure: true);
        if (!result.Succeeded) return LoginFailed();

        await signIn.SignInWithClaimsAsync(user, isPersistent: false,
            [ScoutIdentityServices.AuthenticationTime()]);
        return Results.Redirect("/");
    }

    private static async Task<IResult> Logout(HttpContext context, IAntiforgery antiforgery,
        SignInManager<ApplicationUser> signIn)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!await antiforgery.IsRequestValidAsync(context)) return Results.BadRequest();
        signIn.AuthenticationScheme = PlayerAuthDefaults.Scheme;
        await signIn.SignOutAsync();
        return Results.Redirect("/login?signedout=1");
    }

    private static IResult LoginFailed() => Results.Redirect("/login?error=1");
}
