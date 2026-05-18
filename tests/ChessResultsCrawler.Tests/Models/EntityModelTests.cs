using ChessResultsCrawler.Data;
using ChessResultsCrawler.Models;
using Microsoft.EntityFrameworkCore;

namespace ChessResultsCrawler.Tests.Models;

public class EntityModelTests : IDisposable
{
    private readonly AppDbContext _db;

    public EntityModelTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public async Task Tournament_CanBeSavedAndRetrieved()
    {
        var tournament = new Tournament
        {
            ChessResultsId = "1394015",
            Name = "Test Tournament",
            TotalRounds = 7,
            BaseUrl = "https://chess-results.com/tnr1394015.aspx",
            SNode = "s1"
        };

        _db.Tournaments.Add(tournament);
        await _db.SaveChangesAsync();

        var retrieved = await _db.Tournaments.FindAsync(tournament.Id);
        Assert.NotNull(retrieved);
        Assert.Equal("1394015", retrieved.ChessResultsId);
        Assert.Equal("Test Tournament", retrieved.Name);
        Assert.Equal(7, retrieved.TotalRounds);
    }

    [Fact]
    public async Task Team_BelongsToTournament()
    {
        var tournament = new Tournament { ChessResultsId = "1", Name = "T" };
        _db.Tournaments.Add(tournament);
        await _db.SaveChangesAsync();

        var team = new Team { TournamentId = tournament.Id, Snr = 1, Name = "Team Alpha" };
        _db.Teams.Add(team);
        await _db.SaveChangesAsync();

        var loaded = await _db.Teams.Include(t => t.Tournament).FirstAsync();
        Assert.Equal("T", loaded.Tournament.Name);
    }

    [Fact]
    public async Task Player_CanBeAssociatedWithTeam()
    {
        var tournament = new Tournament { ChessResultsId = "1", Name = "T" };
        _db.Tournaments.Add(tournament);
        await _db.SaveChangesAsync();

        var team = new Team { TournamentId = tournament.Id, Snr = 1, Name = "Team A" };
        _db.Teams.Add(team);
        await _db.SaveChangesAsync();

        var player = new Player
        {
            TournamentId = tournament.Id,
            TeamId = team.Id,
            Snr = 1,
            Name = "Player One",
            Title = "GM",
            Elo = 2700,
            Country = "GER"
        };
        _db.Players.Add(player);
        await _db.SaveChangesAsync();

        var loaded = await _db.Players.Include(p => p.Team).FirstAsync();
        Assert.Equal("Team A", loaded.Team!.Name);
    }

    [Fact]
    public async Task CrawlJob_DefaultsAreCorrect()
    {
        var job = new CrawlJob
        {
            ChessResultsId = "123",
            JobType = CrawlJobType.Full
        };

        Assert.Equal(CrawlJobStatus.Queued, job.Status);
        Assert.Null(job.StartedAt);
        Assert.Null(job.CompletedAt);
        Assert.Null(job.ErrorMessage);

        _db.CrawlJobs.Add(job);
        await _db.SaveChangesAsync();

        var loaded = await _db.CrawlJobs.FindAsync(job.Id);
        Assert.NotNull(loaded);
        Assert.Equal(CrawlJobStatus.Queued, loaded.Status);
    }

    [Fact]
    public async Task Round_WithPairings_CascadeLoads()
    {
        var tournament = new Tournament { ChessResultsId = "1", Name = "T" };
        _db.Tournaments.Add(tournament);
        await _db.SaveChangesAsync();

        var teamA = new Team { TournamentId = tournament.Id, Snr = 1, Name = "A" };
        var teamB = new Team { TournamentId = tournament.Id, Snr = 2, Name = "B" };
        _db.Teams.AddRange(teamA, teamB);
        await _db.SaveChangesAsync();

        var round = new Round { TournamentId = tournament.Id, RoundNumber = 1, PairingsPublished = true };
        _db.Rounds.Add(round);
        await _db.SaveChangesAsync();

        _db.TeamPairings.Add(new TeamPairing
        {
            RoundId = round.Id,
            MatchNumber = 1,
            HomeTeamId = teamA.Id,
            AwayTeamId = teamB.Id,
            HomeScore = 3.5m,
            AwayScore = 0.5m
        });
        await _db.SaveChangesAsync();

        var loaded = await _db.Rounds
            .Include(r => r.TeamPairings)
            .ThenInclude(tp => tp.HomeTeam)
            .FirstAsync();

        Assert.Single(loaded.TeamPairings);
        Assert.Equal(3.5m, loaded.TeamPairings.First().HomeScore);
        Assert.Equal("A", loaded.TeamPairings.First().HomeTeam.Name);
    }
}
