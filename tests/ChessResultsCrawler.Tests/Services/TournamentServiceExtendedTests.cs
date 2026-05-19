using ChessResultsCrawler.Data;
using ChessResultsCrawler.Models;
using ChessResultsCrawler.Services;
using Microsoft.EntityFrameworkCore;

namespace ChessResultsCrawler.Tests.Services;

public class TournamentServiceExtendedTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly TournamentService _service;

    public TournamentServiceExtendedTests()
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

    #region GetIndividualPairingsAsync

    [Fact]
    public async Task GetIndividualPairingsAsync_ReturnsPairings()
    {
        var t = new Tournament { ChessResultsId = "1", Name = "T" };
        _db.Tournaments.Add(t);
        await _db.SaveChangesAsync();

        var p1 = new Player { TournamentId = t.Id, Name = "White", Snr = 1 };
        var p2 = new Player { TournamentId = t.Id, Name = "Black", Snr = 2 };
        _db.Players.AddRange(p1, p2);
        await _db.SaveChangesAsync();

        var r1 = new Round { TournamentId = t.Id, RoundNumber = 1 };
        _db.Rounds.Add(r1);
        await _db.SaveChangesAsync();

        _db.Pairings.Add(new Pairing
        {
            RoundId = r1.Id, BoardNumber = 1,
            WhitePlayerId = p1.Id, BlackPlayerId = p2.Id, Result = "1-0"
        });
        await _db.SaveChangesAsync();

        var result = await _service.GetIndividualPairingsAsync(t.Id);

        Assert.Single(result);
        Assert.Equal("1-0", result[0].Result);
        Assert.Equal(1, result[0].BoardNumber);
    }

    [Fact]
    public async Task GetIndividualPairingsAsync_FilterByRound()
    {
        var t = new Tournament { ChessResultsId = "1", Name = "T" };
        _db.Tournaments.Add(t);
        await _db.SaveChangesAsync();

        var p1 = new Player { TournamentId = t.Id, Name = "White", Snr = 1 };
        var p2 = new Player { TournamentId = t.Id, Name = "Black", Snr = 2 };
        _db.Players.AddRange(p1, p2);
        await _db.SaveChangesAsync();

        var r1 = new Round { TournamentId = t.Id, RoundNumber = 1 };
        var r2 = new Round { TournamentId = t.Id, RoundNumber = 2 };
        _db.Rounds.AddRange(r1, r2);
        await _db.SaveChangesAsync();

        _db.Pairings.Add(new Pairing { RoundId = r1.Id, BoardNumber = 1, WhitePlayerId = p1.Id, BlackPlayerId = p2.Id });
        _db.Pairings.Add(new Pairing { RoundId = r2.Id, BoardNumber = 1, WhitePlayerId = p2.Id, BlackPlayerId = p1.Id });
        await _db.SaveChangesAsync();

        var result = await _service.GetIndividualPairingsAsync(t.Id, round: 2);

        Assert.Single(result);
        Assert.Equal(r2.Id, result[0].RoundId);
    }

    [Fact]
    public async Task GetLatestIndividualPairingsAsync_ReturnsLatestRound()
    {
        var t = new Tournament { ChessResultsId = "1", Name = "T" };
        _db.Tournaments.Add(t);
        await _db.SaveChangesAsync();

        var p1 = new Player { TournamentId = t.Id, Name = "White", Snr = 1 };
        var p2 = new Player { TournamentId = t.Id, Name = "Black", Snr = 2 };
        _db.Players.AddRange(p1, p2);
        await _db.SaveChangesAsync();

        var r1 = new Round { TournamentId = t.Id, RoundNumber = 1 };
        var r2 = new Round { TournamentId = t.Id, RoundNumber = 2 };
        _db.Rounds.AddRange(r1, r2);
        await _db.SaveChangesAsync();

        _db.Pairings.Add(new Pairing { RoundId = r1.Id, BoardNumber = 1, WhitePlayerId = p1.Id, BlackPlayerId = p2.Id });
        _db.Pairings.Add(new Pairing { RoundId = r2.Id, BoardNumber = 1, WhitePlayerId = p2.Id, BlackPlayerId = p1.Id });
        await _db.SaveChangesAsync();

        var result = await _service.GetLatestIndividualPairingsAsync(t.Id);

        Assert.Single(result);
        Assert.Equal(r2.Id, result[0].RoundId);
    }

    #endregion

    #region HasTeamPairingsAsync

    [Fact]
    public async Task HasTeamPairingsAsync_True_WhenTeamPairingsExist()
    {
        var t = new Tournament { ChessResultsId = "1", Name = "T" };
        _db.Tournaments.Add(t);
        await _db.SaveChangesAsync();

        var teamA = new Team { TournamentId = t.Id, Snr = 1, Name = "A" };
        var teamB = new Team { TournamentId = t.Id, Snr = 2, Name = "B" };
        _db.Teams.AddRange(teamA, teamB);
        await _db.SaveChangesAsync();

        var r1 = new Round { TournamentId = t.Id, RoundNumber = 1 };
        _db.Rounds.Add(r1);
        await _db.SaveChangesAsync();

        _db.TeamPairings.Add(new TeamPairing
        {
            RoundId = r1.Id, MatchNumber = 1,
            HomeTeamId = teamA.Id, AwayTeamId = teamB.Id
        });
        await _db.SaveChangesAsync();

        var result = await _service.HasTeamPairingsAsync(t.Id);

        Assert.True(result);
    }

    [Fact]
    public async Task HasTeamPairingsAsync_False_WhenNoTeamPairings()
    {
        var t = new Tournament { ChessResultsId = "1", Name = "T" };
        _db.Tournaments.Add(t);
        await _db.SaveChangesAsync();

        var result = await _service.HasTeamPairingsAsync(t.Id);

        Assert.False(result);
    }

    #endregion
}
