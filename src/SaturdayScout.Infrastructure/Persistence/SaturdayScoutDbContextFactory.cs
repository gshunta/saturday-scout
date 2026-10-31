using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SaturdayScout.Infrastructure.Persistence;

public sealed class SaturdayScoutDbContextFactory : IDesignTimeDbContextFactory<SaturdayScoutDbContext>
{
    public SaturdayScoutDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("SATURDAY_SCOUT_DB_CONNECTION")
            ?? throw new InvalidOperationException("Set SATURDAY_SCOUT_DB_CONNECTION before running EF migrations.");
        var options = new DbContextOptionsBuilder<SaturdayScoutDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new SaturdayScoutDbContext(options);
    }
}
