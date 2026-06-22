using ChessResultsCrawler.Models;
using ChessResultsCrawler.Services;

namespace ChessResultsCrawler.Tests.Services;

public class CrawlerServiceTeamMapTests
{
    [Fact]
    public void BuildTeamNameMap_DistinctNames_MapsEach()
    {
        var teams = new[]
        {
            new Team { Snr = 1, Name = "Team A" },
            new Team { Snr = 2, Name = "Team B" },
        };

        var map = CrawlerService.BuildTeamNameMap(teams);

        Assert.Equal(2, map.Count);
        Assert.Equal(1, map["Team A"].Snr);
        Assert.Equal(2, map["Team B"].Snr);
    }

    [Fact]
    public void BuildTeamNameMap_DuplicateNames_KeepsLowestSnr_NoThrow()
    {
        // Doppelter Name (Tippfehler/echte Dublette) → früher ToDictionary-Exception.
        var teams = new[]
        {
            new Team { Snr = 5, Name = "Dup" },
            new Team { Snr = 2, Name = "Dup" },
            new Team { Snr = 9, Name = "Unique" },
        };

        var map = CrawlerService.BuildTeamNameMap(teams);

        Assert.Equal(2, map.Count);
        Assert.Equal(2, map["Dup"].Snr);   // kleinste Snr gewinnt (deterministisch)
        Assert.Equal(9, map["Unique"].Snr);
    }

    [Fact]
    public void BuildTeamNameMap_Empty_ReturnsEmpty()
    {
        Assert.Empty(CrawlerService.BuildTeamNameMap(Array.Empty<Team>()));
    }
}
