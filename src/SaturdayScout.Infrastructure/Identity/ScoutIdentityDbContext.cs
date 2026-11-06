using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SaturdayScout.Domain;

namespace SaturdayScout.Infrastructure.Identity;

public sealed class ScoutIdentityDbContext(DbContextOptions<ScoutIdentityDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<AccountInvitation> Invitations => Set<AccountInvitation>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema("auth");
        builder.Entity<ApplicationUser>(b =>
        {
            b.ToTable("AspNetUsers", "auth");
            b.HasOne<User>().WithOne().HasForeignKey<ApplicationUser>(x => x.Id)
                .OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<IdentityRole<Guid>>(b =>
        {
            b.ToTable("AspNetRoles", "auth");
            b.HasData(
                new IdentityRole<Guid>
                {
                    Id = Guid.Parse("a98acb10-a901-4d63-a5de-3cad31010a01"),
                    Name = ScoutRoles.SystemAdministrator,
                    NormalizedName = "SYSTEMADMINISTRATOR",
                    ConcurrencyStamp = "system-administrator-v1"
                },
                new IdentityRole<Guid>
                {
                    Id = Guid.Parse("a98acb10-a901-4d63-a5de-3cad31010a02"),
                    Name = ScoutRoles.LeagueCommissioner,
                    NormalizedName = "LEAGUECOMMISSIONER",
                    ConcurrencyStamp = "league-commissioner-v1"
                },
                new IdentityRole<Guid>
                {
                    Id = Guid.Parse("a98acb10-a901-4d63-a5de-3cad31010a03"),
                    Name = ScoutRoles.Player,
                    NormalizedName = "PLAYER",
                    ConcurrencyStamp = "player-v1"
                });
        });
        builder.Entity<IdentityUserRole<Guid>>().ToTable("AspNetUserRoles", "auth");
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("AspNetUserClaims", "auth");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("AspNetUserLogins", "auth");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("AspNetUserTokens", "auth");
        builder.Entity<IdentityRoleClaim<Guid>>().ToTable("AspNetRoleClaims", "auth");
        builder.Entity<User>(b =>
        {
            b.ToTable("Users", "public", t => t.ExcludeFromMigrations());
            b.HasKey(x => x.Id);
            b.Property(x => x.DisplayName).HasMaxLength(120);
            b.Property(x => x.ExternalSubjectId).HasMaxLength(255);
            b.Property(x => x.Email).HasMaxLength(320);
        });
        builder.Entity<AccountInvitation>(b =>
        {
            b.ToTable("AccountInvitations", "auth");
            b.HasKey(x => x.Id);
            b.Property(x => x.Email).HasMaxLength(320).IsRequired();
            b.Property(x => x.NormalizedEmail).HasMaxLength(320).IsRequired();
            b.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            b.Property(x => x.Role).HasMaxLength(64).IsRequired();
            b.HasIndex(x => x.TokenHash).IsUnique();
            b.HasIndex(x => x.NormalizedEmail);
            b.HasOne<User>().WithMany().HasForeignKey(x => x.IssuedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}

public sealed class ScoutIdentityDbContextFactory : IDesignTimeDbContextFactory<ScoutIdentityDbContext>
{
    public ScoutIdentityDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("SATURDAY_SCOUT_DB_CONNECTION")
            ?? throw new InvalidOperationException("SATURDAY_SCOUT_DB_CONNECTION is required.");
        var options = new DbContextOptionsBuilder<ScoutIdentityDbContext>()
            .UseNpgsql(connection, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "auth"))
            .Options;
        return new ScoutIdentityDbContext(options);
    }
}
