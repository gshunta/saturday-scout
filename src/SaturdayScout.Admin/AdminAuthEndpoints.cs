using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SaturdayScout.Infrastructure.Identity;
using SaturdayScout.Infrastructure.Persistence;

namespace SaturdayScout.Admin;

public static class AdminAuthEndpoints
{
    public static WebApplication MapAdminAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/auth/login", Login).RequireRateLimiting("login");
        app.MapPost("/auth/mfa", VerifyMfa).RequireRateLimiting("mfa");
        app.MapPost("/auth/recovery", VerifyRecovery).RequireRateLimiting("mfa");
        app.MapPost("/auth/enroll", EnrollAuthenticator).RequireAuthorization("MfaEnrollment")
            .RequireRateLimiting("mfa");
        app.MapPost("/auth/logout", Logout).RequireAuthorization();
        app.MapGet("/auth/session", (ClaimsPrincipal user) => Results.Ok(new
        {
            userId = user.FindFirstValue(ClaimTypes.NameIdentifier), mfa = true
        })).RequireAuthorization("SystemAdministrator");
        return app;
    }

    private static async Task<IResult> Login(HttpContext context, IAntiforgery antiforgery,
        UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn,
        SaturdayScoutDbContext domain, ILogger<Program> logger, IWebHostEnvironment environment)
    {
        if (!await antiforgery.IsRequestValidAsync(context)) return Results.BadRequest();
        var form = await context.Request.ReadFormAsync();
        var email = form["email"].ToString().Trim();
        var password = form["password"].ToString();
        if (email.Length is < 3 or > 256 || password.Length is < 1 or > 1024)
            return LoginFailed(logger, environment, "invalid_input");
        var user = await users.FindByEmailAsync(email);
        if (user is null) return LoginFailed(logger, environment, "identity_user_missing");
        if (!user.EmailConfirmed) return LoginFailed(logger, environment, "email_unconfirmed");
        if (!await users.IsInRoleAsync(user, ScoutRoles.SystemAdministrator))
            return LoginFailed(logger, environment, "administrator_role_missing");
        if (await ScoutIdentityServices.IsDisabledAsync(users, user))
            return LoginFailed(logger, environment, "account_disabled");
        if (!await domain.Users.AsNoTracking().AnyAsync(x => x.Id == user.Id))
            return LoginFailed(logger, environment, "domain_user_missing");

        // An administrator must never skip TOTP through Identity's remembered-client cookie.
        await signIn.ForgetTwoFactorClientAsync();
        var result = await signIn.PasswordSignInAsync(user, password, isPersistent: false, lockoutOnFailure: true);
        if (result.RequiresTwoFactor) return Results.Redirect("/mfa");
        if (!result.Succeeded)
        {
            var category = result.IsLockedOut ? "identity_account_locked_out" :
                result.IsNotAllowed ? "identity_signin_not_allowed" : "identity_password_or_signin_rejected";
            return LoginFailed(logger, environment, category);
        }
        if (await users.GetTwoFactorEnabledAsync(user))
        {
            await signIn.SignOutAsync();
            return LoginFailed(logger, environment, "identity_two_factor_challenge_missing");
        }
        if (await users.GetAuthenticatorKeyAsync(user) is null)
        {
            var reset = await users.ResetAuthenticatorKeyAsync(user);
            if (!reset.Succeeded)
            {
                await signIn.SignOutAsync();
                return LoginFailed(logger, environment, "authenticator_enrollment_setup_failed");
            }
        }
        try
        {
            await signIn.SignInWithClaimsAsync(user, isPersistent: false,
                [ScoutIdentityServices.PasswordClaim(), ScoutIdentityServices.AuthenticationTime()]);
        }
        catch
        {
            LogLoginFailure(logger, environment, "admin_cookie_issuance_failed");
            throw;
        }
        return Results.Redirect("/enroll");
    }

    private static async Task<IResult> VerifyMfa(HttpContext context, IAntiforgery antiforgery,
        SignInManager<ApplicationUser> signIn, UserManager<ApplicationUser> users,
        SaturdayScoutDbContext domain)
    {
        if (!await antiforgery.IsRequestValidAsync(context)) return Results.BadRequest();
        var user = await signIn.GetTwoFactorAuthenticationUserAsync();
        if (!await EligibleForMfaAsync(user, users, domain)) return Results.Redirect("/login?error=1");
        var form = await context.Request.ReadFormAsync();
        var code = form["code"].ToString().Replace(" ", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal);
        if (code.Length != 6 || !code.All(char.IsDigit)) return Results.Redirect("/mfa?error=1");
        var result = await signIn.TwoFactorAuthenticatorSignInAsync(code, isPersistent: false, rememberClient: false);
        if (!result.Succeeded) return Results.Redirect("/mfa?error=1");
        await signIn.SignInWithClaimsAsync(user!, isPersistent: false,
            [ScoutIdentityServices.MfaVerifiedClaim(), ScoutIdentityServices.AuthenticationTime()]);
        return Results.Redirect("/dashboard");
    }

    private static async Task<IResult> VerifyRecovery(HttpContext context, IAntiforgery antiforgery,
        SignInManager<ApplicationUser> signIn, UserManager<ApplicationUser> users,
        SaturdayScoutDbContext domain)
    {
        if (!await antiforgery.IsRequestValidAsync(context)) return Results.BadRequest();
        var user = await signIn.GetTwoFactorAuthenticationUserAsync();
        if (!await EligibleForMfaAsync(user, users, domain)) return Results.Redirect("/login?error=1");
        var form = await context.Request.ReadFormAsync();
        var code = form["code"].ToString().Trim();
        if (code.Length is < 4 or > 100) return Results.Redirect("/recovery?error=1");
        var result = await signIn.TwoFactorRecoveryCodeSignInAsync(code);
        if (!result.Succeeded) return Results.Redirect("/recovery?error=1");
        await signIn.SignInWithClaimsAsync(user!, isPersistent: false,
            [ScoutIdentityServices.MfaVerifiedClaim(), ScoutIdentityServices.AuthenticationTime()]);
        return Results.Redirect("/dashboard");
    }

    private static async Task<IResult> EnrollAuthenticator(HttpContext context, IAntiforgery antiforgery,
        UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn)
    {
        if (!await antiforgery.IsRequestValidAsync(context)) return Results.BadRequest();
        var user = await users.GetUserAsync(context.User);
        if (user is null || await users.GetTwoFactorEnabledAsync(user)) return Results.Forbid();
        var form = await context.Request.ReadFormAsync();
        var code = form["code"].ToString().Replace(" ", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal);
        if (code.Length != 6 || !await users.VerifyTwoFactorTokenAsync(user,
                users.Options.Tokens.AuthenticatorTokenProvider, code))
            return Results.Redirect("/enroll?error=1");
        var enabled = await users.SetTwoFactorEnabledAsync(user, true);
        if (!enabled.Succeeded) return Results.Redirect("/enroll?error=1");
        var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 8);
        if (codes is null) return Results.Problem("Recovery codes could not be generated.");
        await signIn.SignInWithClaimsAsync(user, isPersistent: false,
            [ScoutIdentityServices.MfaVerifiedClaim(), ScoutIdentityServices.AuthenticationTime()]);
        context.Response.Headers.CacheControl = "no-store";
        var items = string.Join("", codes.Select(x => $"<li><code>{WebUtility.HtmlEncode(x)}</code></li>"));
        return Results.Content($"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Recovery codes</title></head><body><main><h1>Save your recovery codes</h1><p>Each code works once. Store them offline; they will not be shown again.</p><ul>{items}</ul><a href=\"/dashboard\">Continue to dashboard</a></main></body></html>", "text/html");
    }

    private static async Task<IResult> Logout(HttpContext context, IAntiforgery antiforgery,
        SignInManager<ApplicationUser> signIn, UserManager<ApplicationUser> users)
    {
        if (!await antiforgery.IsRequestValidAsync(context)) return Results.BadRequest();
        var user = await users.GetUserAsync(context.User);
        if (user is not null) await users.UpdateSecurityStampAsync(user);
        await signIn.SignOutAsync();
        context.Response.Headers.CacheControl = "no-store";
        return Results.Redirect("/login?signedout=1");
    }

    private static async Task<bool> EligibleForMfaAsync(ApplicationUser? user,
        UserManager<ApplicationUser> users, SaturdayScoutDbContext domain) =>
        user is not null && user.EmailConfirmed && await users.GetTwoFactorEnabledAsync(user) &&
        !await users.IsLockedOutAsync(user) && !await ScoutIdentityServices.IsDisabledAsync(users, user) &&
        await users.IsInRoleAsync(user, ScoutRoles.SystemAdministrator) &&
        await domain.Users.AsNoTracking().AnyAsync(x => x.Id == user.Id);

    private static IResult LoginFailed(ILogger logger, IWebHostEnvironment environment, string category)
    {
        LogLoginFailure(logger, environment, category);
        return Results.Redirect("/login?error=1");
    }

    private static void LogLoginFailure(ILogger logger, IWebHostEnvironment environment, string category)
    {
        if (environment.IsDevelopment())
            logger.LogWarning("Administrator login rejected. Failure category: {FailureCategory}", category);
    }
}
