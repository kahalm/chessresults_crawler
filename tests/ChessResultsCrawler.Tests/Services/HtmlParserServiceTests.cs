using ChessResultsCrawler.Services;

namespace ChessResultsCrawler.Tests.Services;

public class HtmlParserServiceTests
{
    private readonly HtmlParserService _parser = new();

    #region ParsePlayerListAsync

    [Fact]
    public async Task ParsePlayerListAsync_StandardTable_ParsesAllPlayers()
    {
        var html = BuildPlayerListHtml([
            ("1", "GM", "Carlsen, Magnus", "1503014", "2830", "NOR", "Team A", "1"),
            ("2", "GM", "Caruana, Fabiano", "2020009", "2786", "USA", "Team B", "1"),
            ("3", "IM", "Doe, John", "1234567", "2450", "GER", "Team A", "2"),
        ]);

        var players = await _parser.ParsePlayerListAsync(html);

        Assert.Equal(3, players.Count);
        Assert.Equal("Carlsen, Magnus", players[0].Name);
        Assert.Equal("GM", players[0].Title);
        Assert.Equal("1503014", players[0].FideId);
        Assert.Equal(2830, players[0].Elo);
        Assert.Equal("NOR", players[0].Country);
        Assert.Equal("Team A", players[0].TeamName);
        Assert.Equal(1, players[0].BoardNumber);
        Assert.Equal(1, players[0].Snr);
    }

    [Fact]
    public async Task ParsePlayerListAsync_EmptyTable_ReturnsEmptyList()
    {
        var html = "<html><body><table><tr><th>Nr.</th><th>Name</th></tr></table></body></html>";

        var players = await _parser.ParsePlayerListAsync(html);

        Assert.Empty(players);
    }

    [Fact]
    public async Task ParsePlayerListAsync_NoMatchingTable_ReturnsEmptyList()
    {
        var html = "<html><body><table><tr><td>Foo</td><td>Bar</td></tr></table></body></html>";

        var players = await _parser.ParsePlayerListAsync(html);

        Assert.Empty(players);
    }

    [Fact]
    public async Task ParsePlayerListAsync_MissingOptionalFields_SetsNull()
    {
        var html = BuildPlayerListHtml([
            ("1", "", "Smith, Jane", "", "", "USA", "", ""),
        ]);

        var players = await _parser.ParsePlayerListAsync(html);

        Assert.Single(players);
        Assert.Equal("Smith, Jane", players[0].Name);
        Assert.Null(players[0].Title);
        Assert.Null(players[0].FideId);
        Assert.Null(players[0].Elo);
        Assert.Null(players[0].TeamName);
        Assert.Null(players[0].BoardNumber);
    }

    [Fact]
    public async Task ParsePlayerListAsync_SkipsRowsWithInvalidSnr()
    {
        var html = @"<html><body><table>
            <tr><th>Nr.</th><th>Name</th><th>Rtg</th></tr>
            <tr><td>abc</td><td>Invalid</td><td>2000</td></tr>
            <tr><td>1</td><td>Valid Player</td><td>2000</td></tr>
            </table></body></html>";

        var players = await _parser.ParsePlayerListAsync(html);

        Assert.Single(players);
        Assert.Equal("Valid Player", players[0].Name);
    }

    #endregion

    #region ParseTeamPairingsAsync

    [Fact]
    public async Task ParseTeamPairingsAsync_CompactFormat_ParsesPairings()
    {
        var html = @"<html><body><table class='CRs1'>
            <tr><th>Nr.</th><th>Home</th><th>Away</th><th>Result</th></tr>
            <tr><td>1</td><td>Team Alpha</td><td>Team Beta</td><td>3.5:0.5</td></tr>
            <tr><td>2</td><td>Team Gamma</td><td>Team Delta</td><td>2:2</td></tr>
            </table></body></html>";

        var pairings = await _parser.ParseTeamPairingsAsync(html);

        Assert.Equal(2, pairings.Count);
        Assert.Equal("Team Alpha", pairings[0].HomeTeamName);
        Assert.Equal("Team Beta", pairings[0].AwayTeamName);
        Assert.Equal(3.5m, pairings[0].HomeScore);
        Assert.Equal(0.5m, pairings[0].AwayScore);
    }

    [Fact]
    public async Task ParseTeamPairingsAsync_HalfSymbol_ParsesCorrectly()
    {
        var html = @"<html><body><table class='CRs1'>
            <tr><th>Nr.</th><th>Home</th><th>Away</th><th>Result</th></tr>
            <tr><td>1</td><td>Team A</td><td>Team B</td><td>3½:½</td></tr>
            </table></body></html>";

        var pairings = await _parser.ParseTeamPairingsAsync(html);

        Assert.Single(pairings);
        Assert.Equal(3.5m, pairings[0].HomeScore);
        Assert.Equal(0.5m, pairings[0].AwayScore);
    }

    [Fact]
    public async Task ParseTeamPairingsAsync_NoResult_ScoresAreNull()
    {
        var html = @"<html><body><table class='CRs1'>
            <tr><th>Nr.</th><th>Home</th><th>Away</th><th>Result</th></tr>
            <tr><td>1</td><td>Team A</td><td>Team B</td><td></td></tr>
            </table></body></html>";

        var pairings = await _parser.ParseTeamPairingsAsync(html);

        Assert.Single(pairings);
        Assert.Null(pairings[0].HomeScore);
        Assert.Null(pairings[0].AwayScore);
    }

    [Fact]
    public async Task ParseTeamPairingsAsync_EmptyHtml_ReturnsEmpty()
    {
        var html = "<html><body></body></html>";

        var pairings = await _parser.ParseTeamPairingsAsync(html);

        Assert.Empty(pairings);
    }

    #endregion

    #region ParseTotalRoundsAsync

    [Fact]
    public async Task ParseTotalRoundsAsync_GermanFormat_ExtractsRounds()
    {
        var html = "<html><body>Rangliste nach 7 Runden</body></html>";

        var rounds = await _parser.ParseTotalRoundsAsync(html);

        Assert.Equal(7, rounds);
    }

    [Fact]
    public async Task ParseTotalRoundsAsync_EnglishFormat_ExtractsRounds()
    {
        var html = "<html><body>Standing after 9 Rounds</body></html>";

        var rounds = await _parser.ParseTotalRoundsAsync(html);

        Assert.Equal(9, rounds);
    }

    [Fact]
    public async Task ParseTotalRoundsAsync_NoMatch_ReturnsNull()
    {
        var html = "<html><body>Some other content</body></html>";

        var rounds = await _parser.ParseTotalRoundsAsync(html);

        Assert.Null(rounds);
    }

    #endregion

    #region ParseAvailableRoundsAsync

    [Fact]
    public async Task ParseAvailableRoundsAsync_LinksWithRdParam_ExtractsRounds()
    {
        var html = @"<html><body>
            <a href='tnr123.aspx?rd=1'>Rd.1</a>
            <a href='tnr123.aspx?rd=2'>Rd.2</a>
            <a href='tnr123.aspx?rd=3'>Rd.3</a>
            </body></html>";

        var rounds = await _parser.ParseAvailableRoundsAsync(html);

        Assert.Equal([1, 2, 3], rounds);
    }

    [Fact]
    public async Task ParseAvailableRoundsAsync_NoDuplicates()
    {
        var html = @"<html><body>
            <a href='?rd=1'>Rd.1</a>
            <a href='?rd=1'>Rd. 1</a>
            <a href='?rd=2'>Rd.2</a>
            </body></html>";

        var rounds = await _parser.ParseAvailableRoundsAsync(html);

        Assert.Equal([1, 2], rounds);
    }

    [Fact]
    public async Task ParseAvailableRoundsAsync_NoLinks_ReturnsEmpty()
    {
        var html = "<html><body>No round links here</body></html>";

        var rounds = await _parser.ParseAvailableRoundsAsync(html);

        Assert.Empty(rounds);
    }

    #endregion

    #region ParseTournamentNameAsync

    [Fact]
    public async Task ParseTournamentNameAsync_H2Tag_ExtractsName()
    {
        var html = "<html><body><h2>Bundesliga 2024/25</h2></body></html>";

        var name = await _parser.ParseTournamentNameAsync(html);

        Assert.Equal("Bundesliga 2024/25", name);
    }

    [Fact]
    public async Task ParseTournamentNameAsync_NoHeader_ReturnsNull()
    {
        var html = "<html><body><p>No header</p></body></html>";

        var name = await _parser.ParseTournamentNameAsync(html);

        Assert.Null(name);
    }

    #endregion

    #region ParseTournamentDetailsAsync

    [Fact]
    public async Task ParseTournamentDetailsAsync_EnglishLabels_ExtractsDateAndLocation()
    {
        var html = @"<html><body><table>
            <tr><td>Date</td><td>17.05.2026</td></tr>
            <tr><td>Location</td><td>Berlin, Germany</td></tr>
            </table></body></html>";

        var details = await _parser.ParseTournamentDetailsAsync(html);

        Assert.Equal("17.05.2026", details.DateText);
        Assert.Equal("Berlin, Germany", details.Location);
    }

    [Fact]
    public async Task ParseTournamentDetailsAsync_GermanLabels_ExtractsDateAndLocation()
    {
        var html = @"<html><body><table>
            <tr><td>Datum</td><td>2026/05/17</td></tr>
            <tr><td>Ort</td><td>München</td></tr>
            </table></body></html>";

        var details = await _parser.ParseTournamentDetailsAsync(html);

        Assert.Equal("2026/05/17", details.DateText);
        Assert.Equal("München", details.Location);
    }

    [Fact]
    public async Task ParseTournamentDetailsAsync_DateRange_PreservesFullText()
    {
        var html = @"<html><body><table>
            <tr><td>Date</td><td>17.05.2026 - 19.05.2026</td></tr>
            </table></body></html>";

        var details = await _parser.ParseTournamentDetailsAsync(html);

        Assert.Equal("17.05.2026 - 19.05.2026", details.DateText);
    }

    [Fact]
    public async Task ParseTournamentDetailsAsync_NoDetails_ReturnsNulls()
    {
        var html = "<html><body><table><tr><td>Foo</td><td>Bar</td></tr></table></body></html>";

        var details = await _parser.ParseTournamentDetailsAsync(html);

        Assert.Null(details.DateText);
        Assert.Null(details.Location);
    }

    [Fact]
    public async Task ParseTournamentDetailsAsync_EmptyValues_ReturnsNulls()
    {
        var html = @"<html><body><table>
            <tr><td>Date</td><td>  </td></tr>
            <tr><td>Location</td><td></td></tr>
            </table></body></html>";

        var details = await _parser.ParseTournamentDetailsAsync(html);

        Assert.Null(details.DateText);
        Assert.Null(details.Location);
    }

    #endregion

    #region ExtractSNode

    [Fact]
    public void ExtractSNode_ValidUrl_ExtractsSNode()
    {
        Assert.Equal("s1", HtmlParserService.ExtractSNode("https://chess-results.com/s1/tnr123.aspx"));
        Assert.Equal("s2", HtmlParserService.ExtractSNode("https://chess-results.com/s2/tnr456.aspx"));
        Assert.Equal("s3", HtmlParserService.ExtractSNode("https://chess-results.com/s3/tnr789.aspx"));
    }

    [Fact]
    public void ExtractSNode_NoSNode_ReturnsNull()
    {
        Assert.Null(HtmlParserService.ExtractSNode("https://chess-results.com/tnr123.aspx"));
    }

    #endregion

    private static string BuildPlayerListHtml(
        (string nr, string title, string name, string fideId, string elo, string country, string team, string board)[] rows)
    {
        var rowsHtml = string.Join("\n", rows.Select(r =>
            $"<tr><td>{r.nr}</td><td>{r.title}</td><td>{r.name}</td><td>{r.fideId}</td><td>{r.elo}</td><td>{r.country}</td><td>{r.team}</td><td>{r.board}</td></tr>"));

        return $@"<html><body><table>
            <tr><th>Nr.</th><th>Title</th><th>Name</th><th>FideID</th><th>Rtg</th><th>FED</th><th>Team</th><th>Br.</th></tr>
            {rowsHtml}
            </table></body></html>";
    }
}
