using Microsoft.EntityFrameworkCore;
using SaturdayScout.Domain;

namespace SaturdayScout.Infrastructure.Persistence;

public sealed class SaturdayScoutDbContext(DbContextOptions<SaturdayScoutDbContext> options) : DbContext(options)
{
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<Game> Games => Set<Game>();
    public DbSet<User> Users => Set<User>();
    public DbSet<League> Leagues => Set<League>();
    public DbSet<LeagueMember> LeagueMembers => Set<LeagueMember>();
    public DbSet<WeeklySlate> WeeklySlates => Set<WeeklySlate>();
    public DbSet<SlateGame> SlateGames => Set<SlateGame>();
    public DbSet<PickSheet> PickSheets => Set<PickSheet>();
    public DbSet<Pick> Picks => Set<Pick>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Team>(b =>
        {
            b.ToTable("Teams");
            b.HasKey(x => x.Id);
            b.Property(x => x.Sport).HasConversion<string>().HasMaxLength(32);
            b.Property(x => x.Name).HasMaxLength(200);
            b.Property(x => x.Abbreviation).HasMaxLength(20);
            b.Property(x => x.Provider).HasMaxLength(100);
            b.Property(x => x.ExternalId).HasMaxLength(200);
            b.HasIndex(x => new { x.Sport, x.Provider, x.ExternalId }).IsUnique();
        });

        modelBuilder.Entity<Game>(b =>
        {
            b.ToTable("Games", t => t.HasCheckConstraint("CK_Games_Scores", "(\"Status\" = 'Final' AND \"HomeScore\" IS NOT NULL AND \"AwayScore\" IS NOT NULL AND \"HomeScore\" >= 0 AND \"AwayScore\" >= 0) OR (\"Status\" <> 'Final' AND \"HomeScore\" IS NULL AND \"AwayScore\" IS NULL)"));
            b.HasKey(x => x.Id);
            b.Property(x => x.Sport).HasConversion<string>().HasMaxLength(32);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            b.Property(x => x.Provider).HasMaxLength(100);
            b.Property(x => x.ExternalId).HasMaxLength(200);
            b.Property(x => x.KickoffUtc).HasColumnType("timestamp with time zone");
            b.HasIndex(x => new { x.Provider, x.ExternalId }).IsUnique();
            b.HasIndex(x => new { x.Sport, x.Season, x.Week });
            b.HasIndex(x => x.KickoffUtc);
            b.HasOne<Team>().WithMany().HasForeignKey(x => x.HomeTeamId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Team>().WithMany().HasForeignKey(x => x.AwayTeamId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<User>(b =>
        {
            b.ToTable("Users");
            b.HasKey(x => x.Id);
            b.Property(x => x.DisplayName).HasMaxLength(120);
            b.Property(x => x.ExternalSubjectId).HasMaxLength(255);
            b.Property(x => x.Email).HasMaxLength(320);
            b.HasIndex(x => x.ExternalSubjectId).IsUnique();
        });

        modelBuilder.Entity<League>(b =>
        {
            b.ToTable("Leagues");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(200);
            b.Property(x => x.InviteCodeHash).HasMaxLength(255);
            b.Property(x => x.InviteExpiresUtc).HasColumnType("timestamp with time zone");
            b.HasOne<User>().WithMany().HasForeignKey(x => x.CommissionerUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LeagueMember>(b =>
        {
            b.ToTable("LeagueMembers");
            b.HasKey(x => new { x.LeagueId, x.UserId });
            b.Property(x => x.Role).HasConversion<string>().HasMaxLength(32);
            b.Property(x => x.JoinedUtc).HasColumnType("timestamp with time zone");
            b.HasOne<League>().WithMany().HasForeignKey(x => x.LeagueId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WeeklySlate>(b =>
        {
            b.ToTable("WeeklySlates");
            b.HasKey(x => x.Id);
            b.Property(x => x.Sport).HasConversion<string>().HasMaxLength(32);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            b.Property(x => x.LockUtc).HasColumnType("timestamp with time zone");
            b.Property(x => x.PublishedUtc).HasColumnType("timestamp with time zone");
            b.HasIndex(x => new { x.LeagueId, x.Sport, x.Season, x.Week }).IsUnique();
            b.HasOne<League>().WithMany().HasForeignKey(x => x.LeagueId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(x => x.Games).WithOne().HasForeignKey(x => x.SlateId).OnDelete(DeleteBehavior.Restrict);
            b.Navigation(x => x.Games).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<SlateGame>(b =>
        {
            b.ToTable("SlateGames", t => t.HasCheckConstraint("CK_SlateGames_DisplayOrder", "\"DisplayOrder\" BETWEEN 1 AND 10"));
            b.HasKey(x => new { x.SlateId, x.GameId });
            b.HasIndex(x => new { x.SlateId, x.DisplayOrder }).IsUnique();
            b.HasOne<Game>().WithMany().HasForeignKey(x => x.GameId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PickSheet>(b =>
        {
            b.ToTable("PickSheets");
            b.HasKey(x => x.Id);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            b.Property(x => x.SubmittedUtc).HasColumnType("timestamp with time zone");
            b.Property(x => x.Version).IsConcurrencyToken();
            b.HasIndex(x => new { x.SlateId, x.UserId }).IsUnique();
            b.HasOne<WeeklySlate>().WithMany().HasForeignKey(x => x.SlateId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(x => x.Picks).WithOne().HasForeignKey(x => x.SheetId).OnDelete(DeleteBehavior.Restrict);
            b.Navigation(x => x.Picks).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<Pick>(b =>
        {
            b.ToTable("Picks", t => t.HasCheckConstraint("CK_Picks_ConfidenceRank", "\"ConfidenceRank\" BETWEEN 1 AND 10"));
            b.HasKey(x => new { x.SheetId, x.GameId });
            b.HasIndex(x => new { x.SheetId, x.ConfidenceRank }).IsUnique();
            b.HasOne<Game>().WithMany().HasForeignKey(x => x.GameId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Team>().WithMany().HasForeignKey(x => x.SelectedTeamId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
