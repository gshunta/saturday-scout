using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using SaturdayScout.Application;
using SaturdayScout.Domain;
using SaturdayScout.Infrastructure.Persistence;

namespace SaturdayScout.Api;

public sealed class DevAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, SaturdayScoutDbContext db, IWebHostEnvironment environment)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!environment.IsDevelopment()) return AuthenticateResult.NoResult();
        var subject = Request.Headers["X-Dev-Subject"].ToString().Trim();
        if (subject.Length is < 1 or > 255) return AuthenticateResult.NoResult();
        var user = await db.Users.SingleOrDefaultAsync(x => x.ExternalSubjectId == subject, Context.RequestAborted);
        if (user is null) return AuthenticateResult.NoResult();
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.DisplayName),
            new Claim("sub", subject)
        ], Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}

public sealed class ScoutExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var (status, title) = exception switch
        {
            ScoutException { Error: ScoutError.Invalid } => (400, "Invalid request"),
            ScoutException { Error: ScoutError.Forbidden } => (403, "Forbidden"),
            ScoutException { Error: ScoutError.NotFound } => (404, "Not found"),
            ScoutException { Error: ScoutError.Conflict } => (409, "Conflict"),
            DbUpdateConcurrencyException => (409, "Concurrent edit"),
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } } => (409, "Duplicate resource"),
            ArgumentException => (422, "Invalid domain value"),
            InvalidOperationException => (422, "Invalid state transition"),
            _ => (500, "Server error")
        };
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = status == 500 ? "An unexpected error occurred." : exception.Message
        }, cancellationToken: ct);
        return true;
    }
}
