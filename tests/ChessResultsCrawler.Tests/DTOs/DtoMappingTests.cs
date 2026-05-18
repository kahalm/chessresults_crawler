using ChessResultsCrawler.DTOs;
using ChessResultsCrawler.Models;

namespace ChessResultsCrawler.Tests.DTOs;

public class DtoMappingTests
{
    [Fact]
    public void TournamentResponse_FromEntity_MapsCorrectly()
    {
        var tournament = new Tournament
        {
            Id = 1,
            ChessResultsId = "123",
            Name = "Test",
            TotalRounds = 7,
            Rounds = [new Round { RoundNumber = 1 }, new Round { RoundNumber = 2 }]
        };

        var dto = TournamentResponse.FromEntity(tournament);

        Assert.Equal(1, dto.Id);
        Assert.Equal("123", dto.ChessResultsId);
        Assert.Equal("Test", dto.Name);
        Assert.Equal(7, dto.TotalRounds);
        Assert.Equal(2, dto.KnownRounds);
    }

    [Fact]
    public void PlayerResponse_FromEntity_MapsCorrectly()
    {
        var player = new Player
        {
            Id = 1,
            Snr = 5,
            Name = "Carlsen, Magnus",
            Title = "GM",
            FideId = "1503014",
            Elo = 2830,
            Country = "NOR",
            BoardNumber = 1,
            Team = new Team { TournamentId = 1, Snr = 1, Name = "Team A" }
        };

        var dto = PlayerResponse.FromEntity(player);

        Assert.Equal("Carlsen, Magnus", dto.Name);
        Assert.Equal("GM", dto.Title);
        Assert.Equal(2830, dto.Elo);
        Assert.Equal("Team A", dto.TeamName);
        Assert.Equal(1, dto.BoardNumber);
    }

    [Fact]
    public void TeamResponse_FromEntity_IncludesPlayersWhenRequested()
    {
        var team = new Team
        {
            Id = 1,
            TournamentId = 1,
            Snr = 1,
            Name = "Alpha",
            Players = [new Player { TournamentId = 1, Name = "P1", Snr = 1 }]
        };

        var withPlayers = TeamResponse.FromEntity(team, includePlayers: true);
        var withoutPlayers = TeamResponse.FromEntity(team, includePlayers: false);

        Assert.Single(withPlayers.Players);
        Assert.Empty(withoutPlayers.Players);
    }

    [Fact]
    public void CrawlJobResponse_FromEntity_MapsCorrectly()
    {
        var job = new CrawlJob
        {
            Id = 1,
            ChessResultsId = "123",
            JobType = CrawlJobType.Full,
            Status = CrawlJobStatus.Running,
            StartedAt = DateTime.UtcNow
        };

        var dto = CrawlJobResponse.FromEntity(job);

        Assert.Equal("Full", dto.JobType);
        Assert.Equal("Running", dto.Status);
        Assert.NotNull(dto.StartedAt);
    }

    [Fact]
    public void TeamPairingResponse_FromEntity_MapsCorrectly()
    {
        var pairing = new TeamPairing
        {
            Id = 1,
            MatchNumber = 1,
            HomeScore = 3.5m,
            AwayScore = 0.5m,
            Round = new Round { TournamentId = 1, RoundNumber = 3 },
            HomeTeam = new Team { TournamentId = 1, Snr = 1, Name = "Home FC" },
            AwayTeam = new Team { TournamentId = 1, Snr = 2, Name = "Away FC" }
        };

        var dto = TeamPairingResponse.FromEntity(pairing);

        Assert.Equal(3, dto.RoundNumber);
        Assert.Equal("Home FC", dto.HomeTeam);
        Assert.Equal("Away FC", dto.AwayTeam);
        Assert.Equal(3.5m, dto.HomeScore);
    }

    [Fact]
    public void RoundResponse_FromEntity_MapsCorrectly()
    {
        var round = new Round
        {
            Id = 1,
            TournamentId = 1,
            RoundNumber = 5,
            PairingsPublished = true,
            ResultsPublished = false
        };

        var dto = RoundResponse.FromEntity(round);

        Assert.Equal(5, dto.RoundNumber);
        Assert.True(dto.PairingsPublished);
        Assert.False(dto.ResultsPublished);
    }
}
