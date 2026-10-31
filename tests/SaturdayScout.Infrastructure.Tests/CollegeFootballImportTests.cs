using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Net;
using SaturdayScout.Application;
using SaturdayScout.Domain;
using SaturdayScout.Infrastructure.CollegeFootballData;
using SaturdayScout.Infrastructure.Persistence;
using Xunit;

namespace SaturdayScout.Infrastructure.Tests;

public sealed class CollegeFootballImportTests
{
    [PostgresFact]
    public async Task FixtureUpsertsPreserveIdsAndProtectFinalResults()
    {
        await WithDatabase(async db =>
        {
            var fixture = CollegeFootballDataParserTests.Read();
            var retrieved = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
            var batch = CollegeFootballDataParser.Parse(fixture.Teams, fixture.Schedule, fixture.Games, retrieved);
            var store = new FootballImportStore(db);
            var first = await store.ApplyWeekAsync(batch, default);
            Assert.Equal(3, first.TeamsCreated);
            Assert.Equal(2, first.GamesCreated);
            db.ChangeTracker.Clear();
            var ids = await db.Games.ToDictionaryAsync(x => x.ExternalId, x => x.Id);
            var teamIds = await db.Teams.ToDictionaryAsync(x => x.ExternalId, x => x.Id);
            var second = await store.ApplyWeekAsync(batch, default);
            Assert.Equal(0, second.GamesCreated);
            Assert.Equal(2, second.GamesUnchanged);
            db.ChangeTracker.Clear();

            var later = batch with { Games = batch.Games.Select(g => g.ExternalId == "101"
                ? g with { KickoffUtc = g.KickoffUtc!.Value.AddHours(1), RetrievedUtc = retrieved.AddHours(1) }
                : g with { RetrievedUtc = retrieved.AddHours(1) }).ToArray() };
            var updated = await store.ApplyWeekAsync(later, default);
            Assert.Equal(2, updated.GamesUpdated);
            db.ChangeTracker.Clear();
            Assert.Equal(ids, await db.Games.ToDictionaryAsync(x => x.ExternalId, x => x.Id));
            Assert.Equal(teamIds, await db.Teams.ToDictionaryAsync(x => x.ExternalId, x => x.Id));
            Assert.Equal(later.Games.Single(x => x.ExternalId == "101").KickoffUtc,
                (await db.Games.SingleAsync(x => x.ExternalId == "101")).KickoffUtc);

            db.ChangeTracker.Clear();
            var contradictory = later with { Games = later.Games.Select(g => g.ExternalId == "102"
                ? g with { HomeScore = 99, RetrievedUtc = retrieved.AddHours(2) }
                : g with { RetrievedUtc = retrieved.AddHours(2) }).ToArray() };
            var rejected = await store.ApplyWeekAsync(contradictory, default);
            Assert.Contains(rejected.Issues, x => x.ExternalId == "102" && x.Reason.Contains("final result"));
            db.ChangeTracker.Clear();
            var final = await db.Games.SingleAsync(x => x.ExternalId == "102");
            Assert.Equal(GameStatus.Final, final.Status);
            Assert.Equal(27, final.HomeScore);
            Assert.Equal(retrieved.AddHours(1), final.SourceRetrievedUtc);

            db.ChangeTracker.Clear();
            var downgraded = later with { Games = later.Games.Select(g => g.ExternalId == "102"
                ? g with { Status = GameStatus.Scheduled, HomeScore = null, AwayScore = null,
                    RetrievedUtc = retrieved.AddHours(3) }
                : g with { RetrievedUtc = retrieved.AddHours(3) }).ToArray() };
            var downgradeReport = await store.ApplyWeekAsync(downgraded, default);
            Assert.Contains(downgradeReport.Issues, x => x.ExternalId == "102" && x.Reason.Contains("final result"));
            db.ChangeTracker.Clear();
            Assert.Equal(GameStatus.Final, (await db.Games.SingleAsync(x => x.ExternalId == "102")).Status);
        });
    }

    [PostgresFact]
    public async Task RequestReservationsPersistAndRespectMonthlyAndSharedCaps()
    {
        await WithDatabase(async db =>
        {
            var options = new CollegeFootballDataOptions("fixture-only", 2, 1);
            var budget = new CfbdRequestBudget(db, options);
            var first = await budget.ReserveAsync("/teams", default);
            await budget.CompleteAsync(first, 200, 0, default);
            Assert.Equal(1, await db.ProviderRequestUsages.CountAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => budget.ReserveAsync("/games", default));
            db.ChangeTracker.Clear();
            var anotherRun = new CfbdRequestBudget(db, new CollegeFootballDataOptions("fixture-only", 2, 5));
            await Assert.ThrowsAsync<InvalidOperationException>(() => anotherRun.ReserveAsync("/games", default));
        });
    }

    [PostgresFact]
    public async Task HttpRetriesAreBoundedAndEachAttemptIsRecorded()
    {
        await WithDatabase(async db =>
        {
            var fixture = CollegeFootballDataParserTests.Read();
            var calls = 0;
            using var http = new HttpClient(new FixtureHandler(request =>
            {
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal("fixture-only", request.Headers.Authorization?.Parameter);
                calls++;
                return new HttpResponseMessage(calls == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
                {
                    Content = new StringContent(request.RequestUri!.AbsolutePath switch
                    {
                        "/teams" => fixture.Teams,
                        "/games/schedule" => fixture.Schedule,
                        "/games" => fixture.Games,
                        _ => throw new InvalidOperationException("Unexpected endpoint.")
                    })
                };
            })) { BaseAddress = new Uri("https://fixture.invalid") };
            var options = new CollegeFootballDataOptions("fixture-only", 4, 4);
            var client = new CollegeFootballDataClient(http, new CfbdRequestBudget(db, options), options);
            var week = await client.GetWeekAsync(Sport.CollegeFootball, 2026, 8, default);
            Assert.Equal(2, week.Games.Count);
            Assert.Equal(4, calls);
            Assert.Equal(4, await db.ProviderRequestUsages.CountAsync());
            Assert.Equal(1, await db.ProviderRequestUsages.CountAsync(x => x.Outcome == "Failed"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetWeekAsync(Sport.CollegeFootball, 2026, 8, default));
            Assert.Equal(4, calls);
        });
    }

    private static async Task WithDatabase(Func<SaturdayScoutDbContext, Task> test)
    {
        var adminBuilder = new NpgsqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("SATURDAY_SCOUT_TEST_ADMIN_CONNECTION")) { Database = "postgres" };
        var name = $"saturday_scout_import_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin))
            await create.ExecuteNonQueryAsync();
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString) { Database = name, Pooling = false };
            await using var db = new SaturdayScoutDbContext(new DbContextOptionsBuilder<SaturdayScoutDbContext>()
                .UseNpgsql(builder.ConnectionString).Options);
            await db.Database.MigrateAsync();
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            await test(db);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}

internal sealed class FixtureHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(respond(request));
}
