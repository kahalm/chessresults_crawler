using ChessResultsCrawler.Services;

namespace ChessResultsCrawler.Tests.Services;

/// <summary>
/// Parser der TurnierSuche-Trefferliste. Die beiden Fixtures sind gekuerzte Ausschnitte ECHTER
/// Antworten (lan=1 und lan=0) - inklusive verschachtelter Tabellen und der Kopfzeile, die th und
/// td mischt. In-Code-HTML kann das nicht ersetzen: genau diese Eigenheiten haben den Parser zu
/// kopfzeilen- statt indexbasierter Aufloesung gezwungen.
/// </summary>
public class TournamentSearchParserTests
{
    private readonly HtmlParserService _parser = new();

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public async Task ParseTournamentSearchAsync_EnglishFixture_ParsesEveryRow()
    {
        var result = await _parser.ParseTournamentSearchAsync(Fixture("tournament-search-en.html"));

        Assert.Equal(6, result.Count);

        var first = result[0];
        Assert.Equal("1457129", first.ChessResultsId);
        Assert.Equal("Open Braunau 2026 A", first.Name);
        Assert.Equal("AUT", first.Federation);
        Assert.Equal("Salzburg", first.State);
        Assert.Equal(new DateOnly(2026, 12, 18), first.StartDate);
        Assert.Equal(new DateOnly(2026, 12, 20), first.EndDate);
        Assert.Equal("Ranshofen", first.LocationText);
        Assert.StartsWith("90 min/40 moves", first.TimeControlText);
        Assert.Equal("Martin Schneeweis", first.Director);
        Assert.Equal("WSV ATSV Ranshofen Schach", first.Organizer);
        Assert.Equal(0, first.Rounds);
        Assert.Equal(2, first.PlayerCount);
        Assert.Equal(TimeSpan.FromDays(16), first.LastUpdateAge);
    }

    [Fact]
    public async Task ParseTournamentSearchAsync_EnglishFixture_KeepsUmlautsAndCompoundAge()
    {
        var result = await _parser.ParseTournamentSearchAsync(Fixture("tournament-search-en.html"));

        var hallein = result.Single(r => r.ChessResultsId == "1313040");
        Assert.Contains("Hauptstraße", hallein.LocationText);
        // "270 Days 18 Hours" - zusammengesetzte Angaben muessen addiert werden, nicht nur der erste Teil.
        Assert.Equal(TimeSpan.FromDays(270) + TimeSpan.FromHours(18), hallein.LastUpdateAge);
    }

    [Fact]
    public async Task ParseTournamentSearchAsync_GermanFixture_ResolvesGermanHeadersAndDateFormat()
    {
        // lan=0 liefert andere Kopfzeilen (Turnierbezeichnung/Ort/Bundesland) UND ein anderes
        // Datumsformat (31.10.2026 statt 2026/10/31).
        var result = await _parser.ParseTournamentSearchAsync(Fixture("tournament-search-de.html"));

        Assert.Equal(4, result.Count);

        var first = result[0];
        Assert.Equal("1480313", first.ChessResultsId);
        Assert.StartsWith("U18 Halloween Champion", first.Name);
        Assert.Equal(new DateOnly(2026, 10, 31), first.StartDate);
        Assert.Equal(new DateOnly(2026, 10, 31), first.EndDate);
        Assert.Equal("Steiermark", first.State);
        Assert.Contains("Tillmitsch", first.LocationText);
        Assert.Equal(TimeSpan.FromDays(8) + TimeSpan.FromHours(23), first.LastUpdateAge);
    }

    [Fact]
    public async Task ParseTournamentSearchAsync_TableWithoutDbKeyColumn_ReturnsEmpty()
    {
        // Fehlerseite/Interstitial: lieber leer als aus einer fremden Tabelle Muell ziehen.
        var html = "<html><body><table class=\"CRs2\">" +
                   "<tr><td>No.</td><td>Tournament</td><td>Location</td></tr>" +
                   "<tr><td>1</td><td>Irgendwas</td><td>Wien</td></tr>" +
                   "</table></body></html>";

        Assert.Empty(await _parser.ParseTournamentSearchAsync(html));
    }

    [Fact]
    public async Task ParseTournamentSearchAsync_NonNumericDbKey_SkipsRow()
    {
        var html = BuildSearchHtml(
            ("1", "Gutes Turnier", "2026/10/01", "2026/10/02", "Wien", "1234567"),
            ("2", "Kaputte Zeile", "2026/10/01", "2026/10/02", "Graz", "-"));

        var result = await _parser.ParseTournamentSearchAsync(html);

        Assert.Single(result);
        Assert.Equal("1234567", result[0].ChessResultsId);
    }

    [Fact]
    public async Task ParseTournamentSearchAsync_DuplicateDbKey_KeepsFirstOccurrence()
    {
        var html = BuildSearchHtml(
            ("1", "Erster Treffer", "2026/10/01", "2026/10/02", "Wien", "999"),
            ("2", "Zweiter Treffer", "2026/10/01", "2026/10/02", "Graz", "999"));

        var result = await _parser.ParseTournamentSearchAsync(html);

        Assert.Single(result);
        Assert.Equal("Erster Treffer", result[0].Name);
    }

    [Fact]
    public async Task ParseTournamentSearchAsync_UnparsableDate_LeavesDateNullButKeepsRow()
    {
        var html = BuildSearchHtml(("1", "Ohne Datum", "", "kaputt", "Wien", "42"));

        var result = await _parser.ParseTournamentSearchAsync(html);

        var entry = Assert.Single(result);
        Assert.Null(entry.StartDate);
        Assert.Null(entry.EndDate);
        Assert.Equal("Ohne Datum", entry.Name);
    }

    [Theory]
    [InlineData("2026/12/18", 2026, 12, 18)]
    [InlineData("18.12.2026", 2026, 12, 18)]
    [InlineData("2026-12-18", 2026, 12, 18)]
    public void ParseSearchDate_AcceptsBothLanguageFormats(string text, int y, int m, int d)
        => Assert.Equal(new DateOnly(y, m, d), HtmlParserService.ParseSearchDate(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("demnaechst")]
    [InlineData("12/18/2026")]
    public void ParseSearchDate_RejectsUnknownInput(string? text)
        => Assert.Null(HtmlParserService.ParseSearchDate(text));

    [Theory]
    [InlineData("16 Days", 16, 0, 0)]
    [InlineData("1 Days 2 Hours", 1, 2, 0)]
    [InlineData("3 Hours 36 Min.", 0, 3, 36)]
    [InlineData("7 Minutes", 0, 0, 7)]
    [InlineData("8 Tage 23 Std.", 8, 23, 0)]
    [InlineData("19 Hours 51 Min.", 0, 19, 51)]
    public void ParseRelativeAge_ParsesEnglishAndGermanUnits(string text, int days, int hours, int minutes)
        => Assert.Equal(new TimeSpan(days, hours, minutes, 0), HtmlParserService.ParseRelativeAge(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nie")]
    [InlineData("42 Lichtjahre")]
    public void ParseRelativeAge_ReturnsNull_WhenNothingUsableIsPresent(string? text)
        => Assert.Null(HtmlParserService.ParseRelativeAge(text));

    /// <summary>
    /// Minimale Trefferliste mit derselben Spaltenordnung wie chess-results (inkl. der beiden
    /// FED-Spalten), aber ohne den Ballast der echten Seite - fuer die Randfaelle.
    /// </summary>
    private static string BuildSearchHtml(
        params (string No, string Name, string From, string To, string Location, string DbKey)[] rows)
    {
        var html = "<html><body><table id=\"datenxx\"><tr><td><table class=\"CRs2\">" +
                   "<tr><td>No.</td><td>Tournament</td><th>FED</th><th>flag</th><td>Last update</td>" +
                   "<th>from</th><th>to</th><td>Tournament director</td><td>Organizer(s)</td>" +
                   "<td>Chief Arbiter</td><td>Deputy Chief Arbiter</td><td>Arbiter</td><td>Location</td>" +
                   "<td>Time control</td><td>FED</td><td>State</td><th>Rd.</th><td>n</td><td>dbkey</td>" +
                   "<td>EventID</td></tr>";
        foreach (var r in rows)
        {
            html += $"<tr><td>{r.No}</td><td>{r.Name}</td><td>AUT</td><td></td><td>1 Days</td>" +
                    $"<td>{r.From}</td><td>{r.To}</td><td></td><td></td><td></td><td></td><td></td>" +
                    $"<td>{r.Location}</td><td>90 min</td><td>AUT</td><td>Wien</td><td>0</td><td>0</td>" +
                    $"<td>{r.DbKey}</td><td>0</td></tr>";
        }
        return html + "</table></td></tr></table></body></html>";
    }
}
