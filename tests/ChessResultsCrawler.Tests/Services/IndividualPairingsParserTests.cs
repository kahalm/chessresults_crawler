using ChessResultsCrawler.Services;

namespace ChessResultsCrawler.Tests.Services;

/// <summary>
/// Einzelpaarungen (art=2 eines Einzelturniers) ueber die KOPFZEILE statt fester Spaltenindizes (S3-015).
/// Die Fixtures <c>individual-pairings-lan0/1.html</c> sind im Markup der echten chess-results-Seiten
/// nachgebaut (offline lag kein echtes Einzelturnier-art=2 vor): lan=0 mit Land-Spalte je Seite,
/// lan=1 ohne Titel-Spalten. Mit den festen Indizes 3/6/9/11 landeten dort Elo/Punkte als Name bzw. Ergebnis.
/// </summary>
public class IndividualPairingsParserTests
{
    private readonly HtmlParserService _parser = new();

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Theory]
    [InlineData("individual-pairings-lan0.html", "spielfrei")]
    [InlineData("individual-pairings-lan1.html", "bye")]
    public async Task ParseIndividualPairingsAsync_ColumnsFromHeader(string fixture, string bye)
    {
        var pairings = await _parser.ParseIndividualPairingsAsync(Fixture(fixture));

        Assert.Equal([1, 2, 3, 4], pairings.Select(p => p.BoardNumber));
        Assert.Equal(("Huber, Hans", "Maier, Max"), (pairings[0].WhiteName, pairings[0].BlackName));
        Assert.Equal((4, 1), (pairings[0].WhiteSnr, pairings[0].BlackSnr));
        Assert.Equal("1-0", pairings[0].Result);
        Assert.Equal(("Gruber, Greta", "Schmid, Sepp"), (pairings[1].WhiteName, pairings[1].BlackName));
        Assert.Equal("½-½", pairings[1].Result);
        Assert.Equal((2, 6), (pairings[1].WhiteSnr, pairings[1].BlackSnr));
        Assert.Equal("0-1", pairings[2].Result);
        Assert.Equal((5, 3), (pairings[2].WhiteSnr, pairings[2].BlackSnr));
        Assert.Equal(("Lang, Lukas", bye), (pairings[3].WhiteName, pairings[3].BlackName));
        Assert.Equal(0, pairings[3].BlackSnr);
        Assert.Equal("+--", pairings[3].Result);
    }

    [Fact]
    public async Task ParseIndividualPairingsAsync_NoHeader_FallsBackToFixedColumns()
    {
        var html = @"<html><body><table class='CRs1'>
            <tr><td>1</td><td>5</td><td>GM</td><td>Carlsen, Magnus</td><td>2830</td><td>2</td><td>1-0</td><td>1</td><td>GM</td><td>Caruana, Fabiano</td><td>2786</td><td>3</td></tr>
            </table></body></html>";

        var p = Assert.Single(await _parser.ParseIndividualPairingsAsync(html));

        Assert.Equal(("Carlsen, Magnus", "Caruana, Fabiano", 5, 3, "1-0"),
            (p.WhiteName, p.BlackName, p.WhiteSnr, p.BlackSnr, p.Result));
    }

    [Fact]
    public async Task IsTeamPairingsPageAsync_IndividualFixtures_AreNotTeamPages()
    {
        Assert.False(await _parser.IsTeamPairingsPageAsync(Fixture("individual-pairings-lan0.html")));
        Assert.False(await _parser.IsTeamPairingsPageAsync(Fixture("individual-pairings-lan1.html")));
    }
}
