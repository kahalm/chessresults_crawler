using ChessResultsCrawler.Data;
using ChessResultsCrawler.Models;
using ChessResultsCrawler.Services;
using Microsoft.EntityFrameworkCore;

namespace ChessResultsCrawler.Tests.Services;

public class TournamentServiceTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly TournamentService _service;

    public TournamentServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _service = new TournamentService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public async Task GetAllTournamentsAsync_ReturnsAllTournaments()
    {
        _db.Tournaments.Add(new Tournament { ChessResultsId = "1", Name = "T1" });
        _db.Tournaments.Add(new Tournament { ChessResultsId = "2", Name = "T2" });
        await _db.SaveChangesAsync();

        var result = await _service.GetAllTournamentsAsync();

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task GetTournamentAsync_ExistingId_ReturnsTournament()
    {
        var t = new Tournament { ChessResultsId = "123", Name = "Test Tournament" };
        _db.Tournaments.Add(t);
        await _db.SaveChangesAsync();

        var result = await _service.GetTournamentAsync(t.Id);

        Assert.NotNull(result);
        Assert.Equal("Test Tournament", result.Name);
    }

    [Fact]
    public async Task GetTournamentAsync_NonExistingId_ReturnsNull()
    {
        var result = await _service.GetTournamentAsync(999);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetTournamentByChessResultsIdAsync_ReturnsCorrectTournament()
    {
        _db.Tournaments.Add(new Tournament { ChessResultsId = "123", Name = "Found" });
        _db.Tournaments.Add(new Tournament { ChessResultsId = "456", Name = "Other" });
        await _db.SaveChangesAsync();

        var result = await _service.GetTournamentByChessResultsIdAsync("123");

        Assert.NotNull(result);
        Assert.Equal("Found", result.Name);
    }

    [Fact]
    public async Task GetPlayersAsync_FilterByTeam_ReturnsFilteredPlayers()
    {
        var t = new Tournament { ChessResultsId = "1", Name = "T" };
        _db.Tournaments.Add(t);
        await _db.SaveChangesAsync();

        var teamA = new Team { TournamentId = t.Id, Snr = 1, Name = "Alpha" };
        var teamB = new Team { TournamentId = t.Id, Snr = 2, Name = "Beta" };
        _db.Teams.AddRange(teamA, teamB);
        await _db.SaveChangesAsync();

        _db.Players.Add(new Player { TournamentId = t.Id, TeamId = teamA.Id, Name = "Player A", Snr = 1 });
        _db.Players.Add(new Player { TournamentId = t.Id, TeamId = teamB.Id, Name = "Player B", Snr = 2 });
        await _db.SaveChangesAsync();

        var result = await _service.GetPlayersAsync(t.Id, team: "Alpha");

        Assert.Single(result);
        Assert.Equal("Player A", result[0].Name);
    }

    [Fact]
    public async Task GetPlayersAsync_SortByElo_ReturnsSortedDesc()
    {
        var t = new Tournament { ChessResultsId = "1", Name = "T" };
        _db.Tournaments.Add(t);
        await _db.SaveChangesAsync();

        _db.Players.Add(new Player { TournamentId = t.Id, Name = "Low", Snr = 1, Elo = 2000 });
        _db.Players.Add(new Player { TournamentId = t.Id, Name = "High", Snr = 2, Elo = 2800 });
        _db.Players.Add(new Player { TournamentId = t.Id, Name = "Mid", Snr = 3, Elo = 2400 });
        await _db.SaveChangesAsync();

        var result = await _service.GetPlayersAsync(t.Id, sortBy: "elo");

        Assert.Equal("High", result[0].Name);
        Assert.Equal("Mid", result[1].Name);
        Assert.Equal("Low", result[2].Name);
    }

    [Fact]
    public async Task GetTeamsAsync_ReturnsTeamsOrderedBySnr()
    {
        var t = new Tournament { ChessResultsId = "1", Name = "T" };
        _db.Tournaments.Add(t);
        await _db.SaveChangesAsync();

        _db.Teams.Add(new Team { TournamentId = t.Id, Snr = 3, Name = "C" });
        _db.Teams.Add(new Team { TournamentId = t.Id, Snr = 1, Name = "A" });
        _db.Teams.Add(new Team { TournamentId = t.Id, Snr = 2, Name = "B" });
        await _db.SaveChangesAsync();

        var result = await _service.GetTeamsAsync(t.Id);

        Assert.Equal(3, result.Count);
        Assert.Equal("A", result[0].Name);
        Assert.Equal("B", result[1].Name);
        Assert.Equal("C", result[2].Name);
    }

    [Fact]
    public async Task GetTeamAsync_ExistingTeam_ReturnsWithPlayers()
    {
        var t = new Tournament { ChessResultsId = "1", Name = "T" };
        _db.Tournaments.Add(t);
        await _db.SaveChangesAsync();

        var team = new Team { TournamentId = t.Id, Snr = 1, Name = "Alpha" };
        _db.Teams.Add(team);
        await _db.SaveChangesAsync();

        _db.Players.Add(new Player { TournamentId = t.Id, TeamId = team.Id, Name = "P1", Snr = 1, BoardNumber = 1 });
        _db.Players.Add(new Player { TournamentId = t.Id, TeamId = team.Id, Name = "P2", Snr = 2, BoardNumber = 2 });
        await _db.SaveChangesAsync();

        var result = await _service.GetTeamAsync(t.Id, 1);

        Assert.NotNull(result);
        Assert.Equal(2, result.Players.Count);
    }

    [Fact]
    public async Task GetRoundsAsync_ReturnsOrderedRounds()
    {
        var t = new Tournament { ChessResultsId = "1", Name = "T" };
        _db.Tournaments.Add(t);
        await _db.SaveChangesAsync();

        _db.Rounds.Add(new Round { TournamentId = t.Id, RoundNumber = 3 });
        _db.Rounds.Add(new Round { TournamentId = t.Id, RoundNumber = 1 });
        _db.Rounds.Add(new Round { TournamentId = t.Id, RoundNumber = 2 });
        await _db.SaveChangesAsync();

        var result = await _service.GetRoundsAsync(t.Id);

        Assert.Equal(3, result.Count);
        Assert.Equal(1, result[0].RoundNumber);
        Assert.Equal(2, result[1].RoundNumber);
        Assert.Equal(3, result[2].RoundNumber);
    }

    [Fact]
    public async Task GetLatestPairingsAsync_ReturnsLatestRoundPairings()
    {
        var t = new Tournament { ChessResultsId = "1", Name = "T" };
        _db.Tournaments.Add(t);
        await _db.SaveChangesAsync();

        var teamA = new Team { TournamentId = t.Id, Snr = 1, Name = "A" };
        var teamB = new Team { TournamentId = t.Id, Snr = 2, Name = "B" };
        _db.Teams.AddRange(teamA, teamB);
        await _db.SaveChangesAsync();

        var r1 = new Round { TournamentId = t.Id, RoundNumber = 1 };
        var r2 = new Round { TournamentId = t.Id, RoundNumber = 2 };
        _db.Rounds.AddRange(r1, r2);
        await _db.SaveChangesAsync();

        _db.TeamPairings.Add(new TeamPairing
        {
            RoundId = r1.Id, MatchNumber = 1,
            HomeTeamId = teamA.Id, AwayTeamId = teamB.Id
        });
        _db.TeamPairings.Add(new TeamPairing
        {
            RoundId = r2.Id, MatchNumber = 1,
            HomeTeamId = teamB.Id, AwayTeamId = teamA.Id
        });
        await _db.SaveChangesAsync();

        var result = await _service.GetLatestPairingsAsync(t.Id);

        Assert.Single(result);
        Assert.Equal(r2.Id, result[0].RoundId);
    }

    [Fact]
    public async Task GetPairingsAsync_FilterByRound_ReturnsFilteredPairings()
    {
        var t = new Tournament { ChessResultsId = "1", Name = "T" };
        _db.Tournaments.Add(t);
        await _db.SaveChangesAsync();

        var teamA = new Team { TournamentId = t.Id, Snr = 1, Name = "A" };
        var teamB = new Team { TournamentId = t.Id, Snr = 2, Name = "B" };
        _db.Teams.AddRange(teamA, teamB);
        await _db.SaveChangesAsync();

        var r1 = new Round { TournamentId = t.Id, RoundNumber = 1 };
        var r2 = new Round { TournamentId = t.Id, RoundNumber = 2 };
        _db.Rounds.AddRange(r1, r2);
        await _db.SaveChangesAsync();

        _db.TeamPairings.Add(new TeamPairing
        {
            RoundId = r1.Id, MatchNumber = 1,
            HomeTeamId = teamA.Id, AwayTeamId = teamB.Id
        });
        _db.TeamPairings.Add(new TeamPairing
        {
            RoundId = r2.Id, MatchNumber = 1,
            HomeTeamId = teamB.Id, AwayTeamId = teamA.Id
        });
        await _db.SaveChangesAsync();

        var result = await _service.GetPairingsAsync(t.Id, round: 1);

        Assert.Single(result);
        Assert.Equal(r1.Id, result[0].RoundId);
    }
}
