using ChessResultsCrawler.Models;
using Microsoft.EntityFrameworkCore;

namespace ChessResultsCrawler.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Tournament> Tournaments => Set<Tournament>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<Player> Players => Set<Player>();
    public DbSet<Round> Rounds => Set<Round>();
    public DbSet<TeamPairing> TeamPairings => Set<TeamPairing>();
    public DbSet<PlayerResult> PlayerResults => Set<PlayerResult>();
    public DbSet<CrawlJob> CrawlJobs => Set<CrawlJob>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tournament>(e =>
        {
            e.HasIndex(t => t.ChessResultsId).IsUnique();
            e.Property(t => t.ChessResultsId).HasMaxLength(20);
            e.Property(t => t.Name).HasMaxLength(500);
            e.Property(t => t.BaseUrl).HasMaxLength(500);
            e.Property(t => t.SNode).HasMaxLength(10);
        });

        modelBuilder.Entity<Team>(e =>
        {
            e.HasIndex(t => new { t.TournamentId, t.Snr }).IsUnique();
            e.Property(t => t.Name).HasMaxLength(500);
            e.HasOne(t => t.Tournament)
                .WithMany(t => t.Teams)
                .HasForeignKey(t => t.TournamentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Player>(e =>
        {
            e.HasIndex(p => new { p.TournamentId, p.Snr }).IsUnique();
            e.Property(p => p.Name).HasMaxLength(500);
            e.Property(p => p.Title).HasMaxLength(10);
            e.Property(p => p.FideId).HasMaxLength(20);
            e.Property(p => p.Country).HasMaxLength(10);
            e.HasOne(p => p.Tournament)
                .WithMany(t => t.Players)
                .HasForeignKey(p => p.TournamentId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(p => p.Team)
                .WithMany(t => t.Players)
                .HasForeignKey(p => p.TeamId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Round>(e =>
        {
            e.HasIndex(r => new { r.TournamentId, r.RoundNumber }).IsUnique();
            e.HasOne(r => r.Tournament)
                .WithMany(t => t.Rounds)
                .HasForeignKey(r => r.TournamentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TeamPairing>(e =>
        {
            e.Property(tp => tp.HomeScore).HasColumnType("decimal(4,1)");
            e.Property(tp => tp.AwayScore).HasColumnType("decimal(4,1)");
            e.HasOne(tp => tp.Round)
                .WithMany(r => r.TeamPairings)
                .HasForeignKey(tp => tp.RoundId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(tp => tp.HomeTeam)
                .WithMany(t => t.HomePairings)
                .HasForeignKey(tp => tp.HomeTeamId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(tp => tp.AwayTeam)
                .WithMany(t => t.AwayPairings)
                .HasForeignKey(tp => tp.AwayTeamId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PlayerResult>(e =>
        {
            e.Property(pr => pr.Result).HasMaxLength(10);
            e.HasOne(pr => pr.Round)
                .WithMany(r => r.PlayerResults)
                .HasForeignKey(pr => pr.RoundId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(pr => pr.Player)
                .WithMany(p => p.Results)
                .HasForeignKey(pr => pr.PlayerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CrawlJob>(e =>
        {
            e.Property(cj => cj.ChessResultsId).HasMaxLength(20);
            e.Property(cj => cj.ErrorMessage).HasMaxLength(2000);
            e.HasOne(cj => cj.Tournament)
                .WithMany(t => t.CrawlJobs)
                .HasForeignKey(cj => cj.TournamentId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }
}
