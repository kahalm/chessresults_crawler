using ChessResultsCrawler.Services;

namespace ChessResultsCrawler.Tests.Services;

/// <summary>
/// Die „Turnierauswahl": Gruppen derselben Veranstaltung, damit die Turnierseite zwischen ihnen
/// umschalten kann (gewuenscht an der Schachrallye Pradl 2026 — vier Gruppen am selben Tag).
/// </summary>
public class TournamentGroupsTests
{
    private readonly HtmlParserService _parser = new();

    /// <summary>Die Zeile, wie sie auf tnr1503220 steht (gekuerzt auf ihre Tabelle).</summary>
    private const string Pradl = """
        <html><body><table border="0" cellpadding="1" cellspacing="1" width="821">
        <tr valign="top"><td class="CRnowrap b">Turnierauswahl</td><td class="CR"><a href="https://chess-results.com/tnr1503214.aspx?lan=0&amp;art=1&amp;turdet=YES">Gruppe A</a>, <a href="https://chess-results.com/tnr1503215.aspx?lan=0&amp;art=1&amp;turdet=YES">Gruppe B</a>, <a href="https://chess-results.com/tnr1503219.aspx?lan=0&amp;art=1&amp;turdet=YES">Mädchen</a>, <i><b>Schnellschach</b></i></td></tr>
        <tr valign="top"><td class="CRnowrap b">Links</td><td class="CR"><a class="CRdb CR_orange" href="http://tirol.chess.at" target="_blank">Offizielle Homepage des Veranstalters</a>, <a href="https://chess-results.com/CalendarLink.aspx?lan=0&amp;tnr=1503220">Mit Turnierkalender verkn&uuml;pfen</a></td></tr>
        </table></body></html>
        """;

    [Fact]
    public async Task ParseTournamentDetails_RallyGroups_InPageOrder_OwnWithoutNumber()
    {
        var details = await _parser.ParseTournamentDetailsAsync(Pradl);

        Assert.Equal(["Gruppe A", "Gruppe B", "Mädchen", "Schnellschach"], details.Groups.Select(g => g.Label));
        Assert.Equal(["1503214", "1503215", "1503219", null], details.Groups.Select(g => g.ChessResultsId));
        Assert.True(details.Groups[3].IsCurrent);
    }

    /// <summary>Englische Oberflaeche und die Olympiade (Open/Women), die eigene an ERSTER Stelle.</summary>
    [Fact]
    public async Task ParseTournamentDetails_EnglishLabel_OwnFirst()
    {
        const string html = """
            <html><body><table><tr valign="top"><td class="CRnowrap b">Tournament selection</td><td class="CR"><i><b>Open</b></i>, <a href="https://chess-results.com/tnr1469896.aspx?lan=1&amp;art=0&amp;turdet=YES&amp;flag=30">Women</a></td></tr></table></body></html>
            """;

        var details = await _parser.ParseTournamentDetailsAsync(html);

        Assert.Equal(["Open", "Women"], details.Groups.Select(g => g.Label));
        Assert.Equal("1469896", details.Groups[1].ChessResultsId);
    }

    [Fact]
    public async Task ParseTournamentDetails_WithoutSelection_HasNoGroups()
    {
        var details = await _parser.ParseTournamentDetailsAsync(
            "<html><body><table><tr><td class='CR'>Ort</td><td class='CR'>Innsbruck</td></tr></table></body></html>");

        Assert.Empty(details.Groups);
        Assert.Equal("Innsbruck", details.Location);
    }

    /// <summary>
    /// Gespeichert wird die eigene Gruppe MIT ihrer Nummer — so kann die Antwort jeder Gruppe sagen,
    /// welche sie selbst ist; gelesen aus Sicht einer ANDEREN Gruppe wechselt nur das Kennzeichen.
    /// </summary>
    [Fact]
    public async Task Groups_RoundTrip_MarksTheCurrentOne()
    {
        var details = await _parser.ParseTournamentDetailsAsync(Pradl);

        var json = TournamentGroups.Serialize(details.Groups, "1503220");

        var own = TournamentGroups.Deserialize(json, "1503220");
        Assert.Equal(["1503214", "1503215", "1503219", "1503220"], own.Select(g => g.ChessResultsId));
        Assert.Equal("Schnellschach", Assert.Single(own, g => g.Current).Label);

        var seenFromA = TournamentGroups.Deserialize(json, "1503214");
        Assert.Equal("Gruppe A", Assert.Single(seenFromA, g => g.Current).Label);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{kaputt")]
    public void Deserialize_MissingOrBrokenJson_HasNoGroups(string? json)
    {
        Assert.Empty(TournamentGroups.Deserialize(json, "1"));
    }

    [Fact]
    public void Serialize_SingleGroup_IsNull()
    {
        Assert.Null(TournamentGroups.Serialize(
            [new ParsedTournamentGroup { Label = "Open", IsCurrent = true }], "1"));
    }
}
