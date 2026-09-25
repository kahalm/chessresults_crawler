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

    // ── Fußnoten-Marker auf der Paarungsseite ────────────────────────────────────────────────
    // Echter Fall (Grand Prix PlusCity, 25.09.): die Paarungsseite zeigt „ASVOE VHS Poechlarn 2 *)",
    // das Teamverzeichnis „ASVOE VHS Poechlarn 2". Jede Paarung dieses Teams fiel als
    // „Team not found" durch — sieben Paarungen je Crawl, dreimal am Tag.

    private static Dictionary<string, Team> Teams(params string[] names) =>
        CrawlerService.BuildTeamNameMap(names.Select((n, i) => new Team { Snr = i + 1, Name = n }));

    [Theory]
    [InlineData("ASVOE VHS Poechlarn 2 *)")]
    [InlineData("ASVOE VHS Poechlarn 2*)")]
    [InlineData("ASVOE VHS Poechlarn 2 *")]
    [InlineData("ASVOE VHS Poechlarn 2 **)")]
    public void FindTeam_IgnoresFootnoteMarker(string pairingName)
    {
        var teams = Teams("ASVOE VHS Poechlarn 1", "ASVOE VHS Poechlarn 2", "ASVOE VHS Poechlarn 3");
        Assert.Equal(2, CrawlerService.FindTeam(teams, pairingName)!.Snr);
    }

    [Fact]
    public void FindTeam_ExactNameWinsOverStrippedOne()
    {
        // Heißt ein Team tatsächlich mit Sternchen, darf es nicht auf das ohne umgebogen werden.
        var teams = Teams("Stern", "Stern *)");
        Assert.Equal(2, CrawlerService.FindTeam(teams, "Stern *)")!.Snr);
    }

    [Theory]
    [InlineData("Haag am Hausruck")]
    [InlineData("SK Taufkirchen/ Pram")]
    [InlineData("TMM (Gruppe A)")]     // Klammer am Ende ist kein Marker
    public void StripFootnoteMarker_LeavesOrdinaryNamesAlone(string name)
    {
        Assert.Equal(name, CrawlerService.StripFootnoteMarker(name));
    }

    [Fact]
    public void FindTeam_UnknownOrEmpty_ReturnsNull()
    {
        var teams = Teams("Team Pasching");
        Assert.Null(CrawlerService.FindTeam(teams, "Lentia City"));
        Assert.Null(CrawlerService.FindTeam(teams, ""));
        Assert.Null(CrawlerService.FindTeam(teams, null));
    }
}
