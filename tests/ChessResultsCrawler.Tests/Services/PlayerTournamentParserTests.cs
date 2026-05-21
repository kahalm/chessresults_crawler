using ChessResultsCrawler.Services;

namespace ChessResultsCrawler.Tests.Services;

public class PlayerTournamentParserTests
{
    private readonly HtmlParserService _parser = new();

    [Fact]
    public async Task ParsePlayerTournamentsAsync_ValidHtml_ReturnsTournaments()
    {
        var html = @"<html><body><table class=""CRs2"">
            <tr class=""CRg1b""><th>Name</th><th>Ident</th><th>FideID</th><th>Verein/Ort</th><th>Land</th><th>Turnierbezeichnung</th><th>Ende-Datum</th><th>Rg.</th><th>Rd.</th><th>n</th></tr>
            <tr class=""CRg2""><td><a href=""tnr1202326.aspx?lan=0&amp;art=9&amp;snr=71"">Oberschmid, Patrik</a></td>
                <td>144749</td><td>1693034</td><td>Schwaz</td><td>AUT</td>
                <td><a href=""tnr1202326.aspx?lan=0"">18. Salzkammergut Schachopen 2</a></td>
                <td>25.05.2026</td><td></td><td></td><td></td></tr>
            <tr class=""CRg1""><td><a href=""tnr1199999.aspx?lan=0&amp;art=9&amp;snr=12"">Oberschmid, Patrik</a></td>
                <td>144749</td><td>1693034</td><td>Schwaz</td><td>AUT</td>
                <td><a href=""tnr1199999.aspx?lan=0"">Tiroler Meisterschaft 2026</a></td>
                <td>30.06.2026</td><td></td><td></td><td></td></tr>
            </table></body></html>";

        var results = await _parser.ParsePlayerTournamentsAsync(html);

        Assert.Equal(2, results.Count);
        Assert.Equal("1202326", results[0].TournamentId);
        Assert.Equal("18. Salzkammergut Schachopen 2", results[0].TournamentName);
        Assert.Equal("25.05.2026", results[0].EndDate);
        Assert.Equal("1199999", results[1].TournamentId);
        Assert.Equal("Tiroler Meisterschaft 2026", results[1].TournamentName);
        Assert.Equal("30.06.2026", results[1].EndDate);
    }

    [Fact]
    public async Task ParsePlayerTournamentsAsync_EmptyTable_ReturnsEmptyList()
    {
        var html = @"<html><body><table class=""CRs2"">
            <tr class=""CRg1b""><th>Name</th><th>Ident</th><th>FideID</th><th>Verein/Ort</th><th>Land</th><th>Turnierbezeichnung</th><th>Ende-Datum</th></tr>
            </table></body></html>";

        var results = await _parser.ParsePlayerTournamentsAsync(html);

        Assert.Empty(results);
    }

    [Fact]
    public async Task ParsePlayerTournamentsAsync_NoTnrLink_SkipsRow()
    {
        var html = @"<html><body><table class=""CRs2"">
            <tr class=""CRg1b""><th>Name</th><th>Ident</th><th>FideID</th><th>Verein/Ort</th><th>Land</th><th>Turnierbezeichnung</th><th>Ende-Datum</th></tr>
            <tr class=""CRg2""><td>Oberschmid, Patrik</td>
                <td>144749</td><td>1693034</td><td>Schwaz</td><td>AUT</td>
                <td>Turnier ohne Link</td>
                <td>25.05.2026</td></tr>
            <tr class=""CRg1""><td><a href=""tnr1202326.aspx?lan=0&amp;art=9&amp;snr=71"">Oberschmid, Patrik</a></td>
                <td>144749</td><td>1693034</td><td>Schwaz</td><td>AUT</td>
                <td><a href=""tnr1202326.aspx?lan=0"">Valid Tournament</a></td>
                <td>25.05.2026</td></tr>
            </table></body></html>";

        var results = await _parser.ParsePlayerTournamentsAsync(html);

        Assert.Single(results);
        Assert.Equal("1202326", results[0].TournamentId);
        Assert.Equal("Valid Tournament", results[0].TournamentName);
    }

    [Fact]
    public async Task ParsePlayerTournamentsAsync_DuplicateTournaments_Deduplicated()
    {
        var html = @"<html><body><table class=""CRs2"">
            <tr class=""CRg1b""><th>Name</th><th>Ident</th><th>FideID</th><th>Verein/Ort</th><th>Land</th><th>Turnierbezeichnung</th><th>Ende-Datum</th></tr>
            <tr class=""CRg2""><td>Player A</td><td>111</td><td>222</td><td>Wien</td><td>AUT</td>
                <td><a href=""tnr1202326.aspx?lan=0"">Same Tournament</a></td>
                <td>25.05.2026</td></tr>
            <tr class=""CRg1""><td>Player B</td><td>333</td><td>444</td><td>Wien</td><td>AUT</td>
                <td><a href=""tnr1202326.aspx?lan=0"">Same Tournament</a></td>
                <td>25.05.2026</td></tr>
            </table></body></html>";

        var results = await _parser.ParsePlayerTournamentsAsync(html);

        Assert.Single(results);
        Assert.Equal("1202326", results[0].TournamentId);
    }

    [Fact]
    public async Task ParsePlayerTournamentsAsync_NoTable_ReturnsEmptyList()
    {
        var html = "<html><body><p>No results found</p></body></html>";

        var results = await _parser.ParsePlayerTournamentsAsync(html);

        Assert.Empty(results);
    }

    [Fact]
    public async Task ParsePlayerTournamentsAsync_EnglishHeaders_ParsesCorrectly()
    {
        var html = @"<html><body><table class=""CRs1"">
            <tr><th>Name</th><th>Ident</th><th>FideID</th><th>Club/City</th><th>FED</th><th>Tournament</th><th>End-Date</th></tr>
            <tr><td>Smith, John</td><td>99999</td><td>1234567</td><td>London</td><td>ENG</td>
                <td><a href=""tnr5555555.aspx?lan=1"">London Chess Classic</a></td>
                <td>15.12.2026</td></tr>
            </table></body></html>";

        var results = await _parser.ParsePlayerTournamentsAsync(html);

        Assert.Single(results);
        Assert.Equal("5555555", results[0].TournamentId);
        Assert.Equal("London Chess Classic", results[0].TournamentName);
        Assert.Equal("15.12.2026", results[0].EndDate);
    }
}
