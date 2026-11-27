using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SaturdayScout.Infrastructure.Hosting;
using SaturdayScout.Infrastructure.Persistence;
using Xunit;

namespace SaturdayScout.Infrastructure.Tests;

public sealed class PostgresHealthReadinessTests
{
    [Fact]
    public async Task PostgresProbeReturnsUnavailableWhenDatabaseCannotBeReached()
    {
        var connection = "Host=127.0.0.1;Port=1;Database=health_probe_unavailable;Username=unused;Password=unused;Timeout=1;Pooling=false";
        var services = new ServiceCollection();
        services.AddDbContext<SaturdayScoutDbContext>(options => options.UseNpgsql(connection));
        services.AddScoutHealthEndpoints(new ConfigurationBuilder().Build());
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var probe = scope.ServiceProvider.GetRequiredService<IDatabaseReadinessProbe>();

        Assert.False(await probe.IsReadyAsync(CancellationToken.None));
    }

    [PostgresFact]
    public async Task PostgresProbeSucceedsAgainstDisposableDatabaseWithoutChangingDomainRows()
    {
        var adminBuilder = new NpgsqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("SATURDAY_SCOUT_TEST_ADMIN_CONNECTION"))
        { Database = "postgres" };
        var name = $"saturday_scout_health_test_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin))
            await create.ExecuteNonQueryAsync();

        try
        {
            var testConnection = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString)
            { Database = name, Pooling = false }.ConnectionString;
            var dbOptions = new DbContextOptionsBuilder<SaturdayScoutDbContext>()
                .UseNpgsql(testConnection).Options;
            await using (var db = new SaturdayScoutDbContext(dbOptions))
                await db.Database.MigrateAsync();

            var services = new ServiceCollection();
            services.AddDbContext<SaturdayScoutDbContext>(options => options.UseNpgsql(testConnection));
            services.AddScoutHealthEndpoints(new ConfigurationBuilder().Build());
            await using var provider = services.BuildServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            var probe = scope.ServiceProvider.GetRequiredService<IDatabaseReadinessProbe>();
            var context = scope.ServiceProvider.GetRequiredService<SaturdayScoutDbContext>();
            var before = await GetDomainRowCounts(context);

            Assert.True(await probe.IsReadyAsync(CancellationToken.None));

            Assert.Equal(before, await GetDomainRowCounts(context));
            Assert.Equal((0, 0, 0, 0), before);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task<(long Users, long Teams, long Games, long Slates)> GetDomainRowCounts(
        SaturdayScoutDbContext db) =>
        (await db.Users.LongCountAsync(), await db.Teams.LongCountAsync(),
            await db.Games.LongCountAsync(), await db.WeeklySlates.LongCountAsync());
}
