using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using SaturdayScout.Application;
using SaturdayScout.Domain;
using SaturdayScout.Infrastructure;
using SaturdayScout.Infrastructure.Identity;
using SaturdayScout.Infrastructure.Persistence;
using Xunit;

namespace SaturdayScout.Api.IntegrationTests;

public sealed class AdminOperationsTests
{
    [PostgresFact]
    public async Task AdministratorLeagueCreationLinksExistingCommissionerAndUnblocksSlateCreation()
    {
        await using var database = await TestDatabase.Create();
        var commissioner = new User(Guid.NewGuid(), "Existing Administrator", "league-create-existing", "existing@example.test");
        await using (var seed = database.Context())
        {
            seed.Users.Add(commissioner);
            await seed.SaveChangesAsync();
        }

        await using var scope = database.Factory("Production").Services.CreateAsyncScope();
        var identityUsers = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var identityUser = new ApplicationUser
        {
            Id = commissioner.Id,
            UserName = commissioner.Email,
            Email = commissioner.Email,
            EmailConfirmed = true
        };
        Assert.True((await identityUsers.CreateAsync(identityUser, "Disposable-test-Password-123!")).Succeeded);
        Assert.True((await identityUsers.AddToRoleAsync(identityUser, ScoutRoles.SystemAdministrator)).Succeeded);

        var service = scope.ServiceProvider.GetRequiredService<IAdminScoutService>();
        var candidates = await service.GetEligibleCommissionersAsync(default);
        var candidate = Assert.Single(candidates);
        Assert.Equal(commissioner.Id, candidate.Id);
        Assert.Equal("Existing Administrator", candidate.DisplayName);
        Assert.Equal(commissioner.Email, candidate.Email);
        var created = await service.CreateLeagueAsync(
            new CreateAdminLeagueRequest("  Week Ten Private  ", commissioner.Id), default);
        Assert.Equal("Week Ten Private", created.Name);
        Assert.Equal(new[] { created }, await service.GetLeaguesAsync(default));

        await using (var verify = database.Context())
        {
            var league = await verify.Leagues.SingleAsync(x => x.Id == created.Id);
            Assert.Equal(commissioner.Id, league.CommissionerUserId);
            Assert.Equal(64, league.InviteCodeHash.Length);
            Assert.True(league.InviteExpiresUtc > DateTimeOffset.UtcNow.AddDays(300));
            var membership = await verify.LeagueMembers.SingleAsync(x => x.LeagueId == league.Id);
            Assert.Equal(commissioner.Id, membership.UserId);
            Assert.Equal(MembershipRole.Commissioner, membership.Role);
        }

        var slate = await service.CreateSlateAsync(new CreateAdminSlateRequest(created.Id, 2026, 10), default);
        Assert.Equal(created.Id, slate.LeagueId);
        await Assert.ThrowsAsync<ScoutException>(() => service.CreateLeagueAsync(
            new CreateAdminLeagueRequest("Week Ten Private", commissioner.Id), default));
        await Assert.ThrowsAsync<ScoutException>(() => service.CreateLeagueAsync(
            new CreateAdminLeagueRequest("Orphan", Guid.NewGuid()), default));
        await using var afterFailure = database.Context();
        Assert.Equal(1, await afterFailure.Leagues.CountAsync());
        Assert.Equal(1, await afterFailure.LeagueMembers.CountAsync());
    }

    [PostgresFact]
    public async Task AdminApiRequiresMfaIdentityCookieAndAntiforgery()
    {
        await using var database = await TestDatabase.Create();
        var userId = Guid.NewGuid();
        var domainUser = new User(userId, "API Administrator", "admin-api-test", "api-admin@example.test");
        var commissioner = new User(Guid.NewGuid(), "League Commissioner", "league-commissioner-test", "commissioner@example.test");
        var league = new League(Guid.NewGuid(), "API Admin League", userId, "hash", DateTimeOffset.UtcNow.AddDays(30));
        var home = new Team(Guid.NewGuid(), Sport.CollegeFootball, "API Home", "APH", "test", "api-home");
        var away = new Team(Guid.NewGuid(), Sport.CollegeFootball, "API Away", "APA", "test", "api-away");
        var games = Enumerable.Range(0, 10).Select(i => new Game(Guid.NewGuid(), Sport.CollegeFootball,
            2026, 8, home.Id, away.Id, DateTimeOffset.UtcNow.AddDays(3).AddHours(i), "test", $"api-game-{i}"))
            .ToArray();
        await using (var seed = database.Context())
        {
            seed.AddRange(domainUser, commissioner, league, home, away);
            seed.AddRange(games);
            await seed.SaveChangesAsync();
        }

        using var factory = database.Factory("Production");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
        });
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/v1/admin/dashboard")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/v1/admin/leagues",
                new CreateAdminLeagueRequest("No access", commissioner.Id))).StatusCode);

        string protectedCookie;
        string playerCookie;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser
            {
                Id = userId, UserName = domainUser.Email, Email = domainUser.Email, EmailConfirmed = true
            };
            Assert.True((await users.CreateAsync(user, "Disposable-test-Password-123!")).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, ScoutRoles.SystemAdministrator)).Succeeded);
            Assert.True((await users.SetTwoFactorEnabledAsync(user, true)).Succeeded);
            var principal = await scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>()
                .CreateAsync(user);
            ((System.Security.Claims.ClaimsIdentity)principal.Identity!).AddClaim(ScoutIdentityServices.MfaVerifiedClaim());
            ((System.Security.Claims.ClaimsIdentity)principal.Identity!).AddClaim(ScoutIdentityServices.AuthenticationTime());
            var cookie = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
                .Get(IdentityConstants.ApplicationScheme);
            var properties = new AuthenticationProperties
            {
                IssuedUtc = DateTimeOffset.UtcNow,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1)
            };
            protectedCookie = cookie.TicketDataFormat!.Protect(new AuthenticationTicket(
                principal, properties, IdentityConstants.ApplicationScheme));

            var player = new ApplicationUser
            {
                Id = commissioner.Id, UserName = commissioner.Email, Email = commissioner.Email, EmailConfirmed = true
            };
            Assert.True((await users.CreateAsync(player, "Disposable-test-Password-123!")).Succeeded);
            Assert.True((await users.AddToRoleAsync(player, ScoutRoles.Player)).Succeeded);
            Assert.True((await users.SetTwoFactorEnabledAsync(player, true)).Succeeded);
            var playerPrincipal = await scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>()
                .CreateAsync(player);
            ((System.Security.Claims.ClaimsIdentity)playerPrincipal.Identity!).AddClaim(ScoutIdentityServices.MfaVerifiedClaim());
            ((System.Security.Claims.ClaimsIdentity)playerPrincipal.Identity!).AddClaim(ScoutIdentityServices.AuthenticationTime());
            playerCookie = cookie.TicketDataFormat!.Protect(new AuthenticationTicket(
                playerPrincipal, properties, IdentityConstants.ApplicationScheme));
        }

        using (var playerClient = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
        }))
        {
            playerClient.DefaultRequestHeaders.Add("Cookie", ScoutIdentityServices.CookieName + "=" + playerCookie);
            Assert.Equal(System.Net.HttpStatusCode.Forbidden,
                (await playerClient.GetAsync("/api/v1/admin/commissioners")).StatusCode);
        }

        client.DefaultRequestHeaders.Add("Cookie", $"{ScoutIdentityServices.CookieName}={protectedCookie}");
        var dashboard = await client.GetAsync("/api/v1/admin/dashboard");
        Assert.Equal(System.Net.HttpStatusCode.OK, dashboard.StatusCode);

        using var withoutCsrf = await client.PostAsJsonAsync("/api/v1/admin/slates",
            new CreateAdminSlateRequest(league.Id, 2026, 8));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, withoutCsrf.StatusCode);

        using var tokenResponse = await client.GetAsync("/api/v1/auth/antiforgery");
        Assert.Equal(System.Net.HttpStatusCode.OK, tokenResponse.StatusCode);
        using var tokenJson = System.Text.Json.JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());
        var requestToken = tokenJson.RootElement.GetProperty("token").GetString()!;
        var antiforgeryCookies = tokenResponse.Headers.TryGetValues("Set-Cookie", out var setCookies)
            ? setCookies.Select(value => value.Split(';', 2)[0]).ToArray()
            : [];
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", string.Join("; ", new[]
            { $"{ScoutIdentityServices.CookieName}={protectedCookie}" }.Concat(antiforgeryCookies)));
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", requestToken);
        using var createdLeagueResponse = await client.PostAsJsonAsync("/api/v1/admin/leagues",
            new CreateAdminLeagueRequest("Initial Private League", commissioner.Id));
        Assert.Equal(System.Net.HttpStatusCode.Created, createdLeagueResponse.StatusCode);
        var createdLeague = (await createdLeagueResponse.Content.ReadFromJsonAsync<AdminLeagueDto>())!;
        await using (var check = database.Context())
        {
            var savedLeague = await check.Leagues.SingleAsync(x => x.Id == createdLeague.Id);
            Assert.Equal(commissioner.Id, savedLeague.CommissionerUserId);
            var membership = await check.LeagueMembers.SingleAsync(x => x.LeagueId == createdLeague.Id);
            Assert.Equal(commissioner.Id, membership.UserId);
            Assert.Equal(MembershipRole.Commissioner, membership.Role);
            Assert.DoesNotContain("InviteCodeHash", System.Text.Json.JsonSerializer.Serialize(createdLeague));
        }
        using var duplicateLeagueResponse = await client.PostAsJsonAsync("/api/v1/admin/leagues",
            new CreateAdminLeagueRequest("Initial Private League", commissioner.Id));
        Assert.Equal(System.Net.HttpStatusCode.Conflict, duplicateLeagueResponse.StatusCode);
        var adminLeagues = await client.GetFromJsonAsync<AdminLeagueDto[]>("/api/v1/admin/leagues");
        Assert.Contains(adminLeagues!, x => x.Id == createdLeague.Id);
        var createdLeagueSlate = await client.PostAsJsonAsync("/api/v1/admin/slates",
            new CreateAdminSlateRequest(createdLeague.Id, 2026, 10));
        Assert.Equal(System.Net.HttpStatusCode.Created, createdLeagueSlate.StatusCode);

        using var created = await client.PostAsJsonAsync("/api/v1/admin/slates",
            new CreateAdminSlateRequest(league.Id, 2026, 8));
        Assert.Equal(System.Net.HttpStatusCode.Created, created.StatusCode);
        var slate = (await created.Content.ReadFromJsonAsync<AdminSlateDto>())!;
        using var randomizedResponse = await client.PostAsJsonAsync($"/api/v1/admin/slates/{slate.Id}/randomize",
            new RandomizeAdminSlateRequest(slate.Version));
        Assert.Equal(System.Net.HttpStatusCode.OK, randomizedResponse.StatusCode);
        var randomized = (await randomizedResponse.Content.ReadFromJsonAsync<AdminSlateDto>())!;
        Assert.Equal(10, randomized.Games.Count);
        Assert.Equal(10, randomized.Games.Select(x => x.Id).Distinct().Count());
        var publish = new PublishAdminSlateRequest(games.Min(x => x.KickoffUtc).AddMinutes(-1), randomized.Version);
        var attempts = await Task.WhenAll(
            client.PostAsJsonAsync($"/api/v1/admin/slates/{slate.Id}/publish", publish),
            client.PostAsJsonAsync($"/api/v1/admin/slates/{slate.Id}/publish", publish));
        Assert.Single(attempts, response => response.StatusCode == System.Net.HttpStatusCode.OK);
        Assert.Single(attempts, response => response.StatusCode == System.Net.HttpStatusCode.Conflict);
        foreach (var response in attempts) response.Dispose();
    }

    [PostgresFact]
    public async Task DashboardGameQueriesAndDraftWorkflowUseExistingDomainRules()
    {
        await using var database = await TestDatabase.Create();
        var user = new User(Guid.NewGuid(), "Commissioner", "admin-operations-commissioner");
        var league = new League(Guid.NewGuid(), "Operations League", user.Id, "hash", DateTimeOffset.UtcNow.AddDays(30));
        var home = new Team(Guid.NewGuid(), Sport.CollegeFootball, "Falcons", "FAL", "test", "home");
        var away = new Team(Guid.NewGuid(), Sport.CollegeFootball, "Tigers", "TIG", "test", "away");
        var otherSport = new Team(Guid.NewGuid(), Sport.Nfl, "Pro Home", "PRH", "test", "nfl-home");
        var games = Enumerable.Range(0, 10).Select(i => new Game(Guid.NewGuid(), Sport.CollegeFootball,
            2026, 8, home.Id, away.Id, DateTimeOffset.UtcNow.AddDays(5).AddHours(i), "test", $"week8-{i}"))
            .ToArray();
        var importedUtc = DateTimeOffset.UtcNow.AddHours(-2);
        foreach (var game in games) game.RecordSource("scheduled", importedUtc);
        var final = new Game(Guid.NewGuid(), Sport.CollegeFootball, 2026, 8, home.Id, away.Id,
            DateTimeOffset.UtcNow.AddDays(-1), "test", "week8-final");
        final.SetFinalScore(17, 10);
        var nextWeek = new Game(Guid.NewGuid(), Sport.CollegeFootball, 2026, 9, home.Id, away.Id,
            DateTimeOffset.UtcNow.AddDays(12), "test", "week9");
        var nextSeason = new Game(Guid.NewGuid(), Sport.CollegeFootball, 2027, 1, home.Id, away.Id,
            DateTimeOffset.UtcNow.AddDays(40), "test", "next-season");
        await using (var seed = database.Context())
        {
            seed.AddRange(user, league, home, away, otherSport);
            seed.AddRange(games);
            seed.AddRange(final, nextWeek, nextSeason);
            await seed.SaveChangesAsync();
        }

        using var factory = database.Factory("Production");
        await using var scope = factory.Services.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<IAdminScoutService>();

        var dashboard = await admin.GetDashboardAsync(default);
        Assert.Equal(2, dashboard.TeamCount);
        Assert.Equal(13, dashboard.GameCount);
        Assert.Equal(1, dashboard.LeagueCount);
        Assert.Equal(importedUtc.ToUnixTimeSeconds(),
            dashboard.MostRecentImportUtc!.Value.ToUnixTimeSeconds());
        Assert.Equal("test", dashboard.MostRecentImportProvider);
        Assert.Equal("scheduled", dashboard.MostRecentImportStatus);
        Assert.Contains(dashboard.UpcomingGames, x => x.Id == games[0].Id);
        Assert.DoesNotContain(dashboard.UpcomingGames, x => x.Status == "Final");

        var page = await admin.BrowseGamesAsync(new AdminGameQuery("fal", null, "scheduled",
            2026, 8, true, 2, 3), default);
        Assert.Equal(10, page.TotalCount);
        Assert.Equal(3, page.Items.Count);
        Assert.False(page.ConferenceFilterAvailable);
        Assert.True(page.Items[0].KickoffUtc > page.Items[1].KickoffUtc);
        Assert.Equal(new[] { 2027, 2026 }, page.Seasons);
        Assert.Throws<ScoutException>(() => AdminGameQueryValidation.Validate(
            new AdminGameQuery(null, "Big Ten", null, 2026, 8, false, 1, 25)));

        var created = await admin.CreateSlateAsync(new CreateAdminSlateRequest(league.Id, 2026, 8), default);
        Assert.Empty(created.Games);
        Assert.False(created.ReadyForPublishing);
        await Assert.ThrowsAsync<ScoutException>(() => admin.CreateSlateAsync(
            new CreateAdminSlateRequest(league.Id, 2026, 8), default));
        await Assert.ThrowsAsync<ScoutException>(() => admin.UpdateSlateGamesAsync(created.Id,
            new UpdateAdminSlateGamesRequest([games[0].Id, games[0].Id], created.Version), default));
        await Assert.ThrowsAsync<ScoutException>(() => admin.UpdateSlateGamesAsync(created.Id,
            new UpdateAdminSlateGamesRequest([nextWeek.Id], created.Version), default));
        await Assert.ThrowsAsync<ScoutException>(() => admin.UpdateSlateGamesAsync(created.Id,
            new UpdateAdminSlateGamesRequest([final.Id], created.Version), default));

        var partial = await admin.UpdateSlateGamesAsync(created.Id,
            new UpdateAdminSlateGamesRequest([games[2].Id, games[0].Id, games[1].Id], created.Version), default);
        Assert.Equal(3, partial.Games.Count);
        Assert.False(partial.ReadyForPublishing);
        Assert.Equal(new[] { games[2].Id, games[0].Id, games[1].Id }, partial.Games.Select(x => x.Id));
        await Assert.ThrowsAsync<ScoutException>(() => admin.UpdateSlateGamesAsync(created.Id,
            new UpdateAdminSlateGamesRequest([games[0].Id], created.Version), default));

        var ready = await admin.UpdateSlateGamesAsync(created.Id,
            new UpdateAdminSlateGamesRequest(games.Select(x => x.Id).ToArray(), partial.Version), default);
        Assert.Equal(10, ready.Games.Count);
        Assert.True(ready.ReadyForPublishing);
        var randomized = await admin.RandomizeSlateGamesAsync(created.Id,
            new RandomizeAdminSlateRequest(ready.Version), default);
        Assert.Equal(10, randomized.Games.Count);
        Assert.Equal(10, randomized.Games.Select(x => x.Id).Distinct().Count());
        Assert.All(randomized.Games, game => Assert.Contains(games, candidate => candidate.Id == game.Id));
        await Assert.ThrowsAsync<ScoutException>(() => admin.UpdateSlateGamesAsync(created.Id,
            new UpdateAdminSlateGamesRequest(games.Select(x => x.Id).ToArray(), ready.Version), default));
        var lockUtc = games.Min(x => x.KickoffUtc).AddMinutes(-1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => admin.PublishSlateAsync(created.Id,
            new PublishAdminSlateRequest(games.Min(x => x.KickoffUtc).AddMinutes(1), randomized.Version), default));
        var published = await admin.PublishSlateAsync(created.Id,
            new PublishAdminSlateRequest(lockUtc, randomized.Version), default);
        Assert.Equal("Published", published.Status);
        Assert.Equal(lockUtc, published.LockUtc);
        await Assert.ThrowsAsync<ScoutException>(() => admin.PublishSlateAsync(created.Id,
            new PublishAdminSlateRequest(lockUtc, randomized.Version), default));

        var emptyWeek = await admin.CreateSlateAsync(new CreateAdminSlateRequest(league.Id, 2026, 12), default);
        await Assert.ThrowsAsync<ScoutException>(() => admin.RandomizeSlateGamesAsync(emptyWeek.Id,
            new RandomizeAdminSlateRequest(emptyWeek.Version), default));
    }

    [PostgresFact]
    public async Task PublishedSlateAndSubmittedPicksRemainImmutable()
    {
        await using var database = await TestDatabase.Create();
        var user = new User(Guid.NewGuid(), "Commissioner", "published-slate-commissioner");
        var player = new User(Guid.NewGuid(), "Player", "published-slate-player");
        var league = new League(Guid.NewGuid(), "Published League", user.Id, "hash", DateTimeOffset.UtcNow.AddDays(30));
        var home = new Team(Guid.NewGuid(), Sport.CollegeFootball, "Home", "HOM", "test", "home");
        var away = new Team(Guid.NewGuid(), Sport.CollegeFootball, "Away", "AWY", "test", "away");
        var now = DateTimeOffset.UtcNow;
        var games = Enumerable.Range(0, 10).Select(i => new Game(Guid.NewGuid(), Sport.CollegeFootball,
            2026, 8, home.Id, away.Id, now.AddDays(3).AddHours(i), "test", $"published-{i}")).ToArray();
        var slate = new WeeklySlate(Guid.NewGuid(), league.Id, Sport.CollegeFootball, 2026, 8);
        slate.ReplaceGames(games);
        slate.Publish(games, games.Min(x => x.KickoffUtc).AddMinutes(-1), now);
        var sheet = new PickSheet(Guid.NewGuid(), slate.Id, player.Id);
        sheet.ReplaceDraftPicks(slate, games, games.Select((game, index) =>
            (game.Id, home.Id, index + 1)), now.AddMinutes(1));
        sheet.Submit(slate, now.AddMinutes(2));
        await using (var seed = database.Context())
        {
            seed.AddRange(user, player, league, home, away, slate, sheet);
            seed.AddRange(games);
            await seed.SaveChangesAsync();
        }

        using var factory = database.Factory("Production");
        await using var scope = factory.Services.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<IAdminScoutService>();
        var current = await admin.GetSlateAsync(slate.Id, default);
        await Assert.ThrowsAsync<ScoutException>(() => admin.UpdateSlateGamesAsync(slate.Id,
            new UpdateAdminSlateGamesRequest(games.Reverse().Select(x => x.Id).ToArray(), current.Version), default));
        await using var verify = database.Context();
        Assert.Equal(SlateStatus.Published, (await verify.WeeklySlates.SingleAsync(x => x.Id == slate.Id)).Status);
        Assert.Equal(PickSheetStatus.Submitted, (await verify.PickSheets.SingleAsync(x => x.Id == sheet.Id)).Status);
        Assert.Equal(10, await verify.Picks.CountAsync(x => x.SheetId == sheet.Id));
        Assert.Equal(games.Select(x => x.Id).ToHashSet(),
            (await verify.SlateGames.Where(x => x.SlateId == slate.Id).Select(x => x.GameId).ToListAsync()).ToHashSet());
    }
}
