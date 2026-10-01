using ChessResultsCrawler.Services;

namespace ChessResultsCrawler.Tests.Services;

/// <summary>
/// Mannschaftspaarungen im Layout der Mannschafts-Schweizersystem-Turniere. Das Fixture ist ein
/// gekuerzter Ausschnitt der ECHTEN Paarungsseite tnr1469895 (46. Schacholympiade Samarkand 2026
/// Open, Runde 1).
///
/// <para>Die Seite hat sechzehn Spalten statt sechs (Setznummer, Flagge und FED stehen VOR dem
/// Teamnamen). Der Parser las die Spalten ueber feste Indizes — Heimteam „105", Gastteam die leere
/// Flaggenzelle — und verwarf jede Zeile: Olympiade 2024 und 2026 sowie die Mannschafts-EM 2025
/// hatten in allen Runden 0 Paarungen, ohne Fehler und ohne Warnung.</para>
/// </summary>
public class TeamPairingsParserTests
{
    private readonly HtmlParserService _parser = new();

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public async Task ParseTeamPairingsAsync_FourTeamColumns_TakesTheNamesNotTheFederationCodes()
    {
        // 22. Mannschafts-EM 2019 (tnr434677): VIER Spalten heissen „Team" — Kennung UND Name je
        // Seite, um das „:" gespiegelt. Mit „die ersten zwei Team-Spalten" waren Heim und Gast
        // dieselbe Mannschaft („Team not found: RUS vs Russia", 180 Warnungen je Crawl auf dev am
        // 01.10.2026, null gespeicherte Paarungen).
        var pairings = await _parser.ParseTeamPairingsAsync(Fixture("team-pairings-teameuro-tnr434677.html"));

        Assert.Equal(4, pairings.Count);
        Assert.Equal([1, 4, 9, 17], pairings.Select(p => p.MatchNumber));

        Assert.Equal("Russia", pairings[0].HomeTeamName);           // NICHT „RUS"
        Assert.Equal("Denmark", pairings[0].AwayTeamName);           // NICHT „DEN"
        Assert.Equal(2m, pairings[0].HomeScore);
        Assert.Equal(2m, pairings[0].AwayScore);

        // Halbe Brettpunkte stehen links und rechts des „:", nicht in den MP-Spalten daneben.
        Assert.Equal(("Serbia", "Azerbaijan"), (pairings[1].HomeTeamName, pairings[1].AwayTeamName));
        Assert.Equal(0.5m, pairings[1].HomeScore);
        Assert.Equal(3.5m, pairings[1].AwayScore);

        // Ein langer Name und einer mit Ziffer: beide wuerden als Kennung unkenntlich.
        Assert.Equal("Republic of North Macedonia", pairings[2].AwayTeamName);
        Assert.Equal(("Turkey", "Georgia 2"), (pairings[3].HomeTeamName, pairings[3].AwayTeamName));
    }

    [Fact]
    public async Task ParseTeamPairingsAsync_OlympiadLayout_ReadsTeamsAndScores()
    {
        var pairings = await _parser.ParseTeamPairingsAsync(Fixture("team-pairings-olympiad-tnr1469895.html"));

        Assert.Equal(6, pairings.Count);
        Assert.Equal([1, 2, 3, 6, 102, 103], pairings.Select(p => p.MatchNumber));

        // Fussnoten-Marker bleibt am Namen — den entfernt erst CrawlerService.FindTeam.
        Assert.Equal("Jamaica", pairings[0].HomeTeamName);
        Assert.Equal("Uzbekistan *)", pairings[0].AwayTeamName);
        Assert.Equal(0m, pairings[0].HomeScore);
        Assert.Equal(4m, pairings[0].AwayScore);

        Assert.Equal("Iraq", pairings[1].HomeTeamName);
        Assert.Equal("United States of America", pairings[1].AwayTeamName);
        Assert.Equal(0.5m, pairings[1].HomeScore);
        Assert.Equal(3.5m, pairings[1].AwayScore);

        Assert.Equal("Hong Kong, China", pairings[3].AwayTeamName);
    }

    [Fact]
    public async Task ParseTeamPairingsAsync_OlympiadLayout_TeamWithoutOpponent_HasNoScore()
    {
        var pairings = await _parser.ParseTeamPairingsAsync(Fixture("team-pairings-olympiad-tnr1469895.html"));

        // „nicht ausgelost" ist der Gegner; der Paarungs-Crawl behandelt ihn als spielfrei.
        var angola = Assert.Single(pairings, p => p.MatchNumber == 102);
        Assert.Equal("Angola", angola.HomeTeamName);
        Assert.Equal("nicht ausgelost", angola.AwayTeamName);
        Assert.True(CrawlerService.IsByeOpponent(angola.AwayTeamName));
        Assert.Null(angola.HomeScore);
        Assert.Null(angola.AwayScore);

        Assert.Equal("Cote d’Ivoire", pairings.Single(p => p.MatchNumber == 103).HomeTeamName);
    }

    [Fact]
    public async Task ParseTeamPairingsAsync_SixColumnLayoutWithHeader_StillParses()
    {
        // Das bisherige Layout (Mannschafts-EM 2023, tnr832215) laeuft jetzt ueber die Kopfzeile —
        // es muss dasselbe herauskommen wie mit den festen Positionen.
        var html = @"<html><body><h2>Teamauslosung</h2><table class='CRs1'>
            <tr class='CRg1b'><td class='none' colspan='6'>1. Runde am 11.11.2023 um 15.00</td></tr>
            <tr class='CRng1b'><th>Nr.</th><th>Team</th><th>Team</th><th>Erg.</th><th>:</th><th>Erg.</th></tr>
            <tr><td>1</td><td><div class='tn_DEN'></div>&nbsp;&nbsp;Denmark</td><td><div class='tn_AZE'></div>&nbsp;&nbsp;Azerbaijan</td><td>2&frac12;</td><td>:</td><td>1&frac12;</td></tr>
            <tr><td>2</td><td>Norway *)</td><td>Slovakia</td><td>2</td><td>:</td><td>2</td></tr>
            </table></body></html>";

        var pairings = await _parser.ParseTeamPairingsAsync(html);

        Assert.Equal(2, pairings.Count);
        Assert.Equal("Denmark", pairings[0].HomeTeamName);
        Assert.Equal("Azerbaijan", pairings[0].AwayTeamName);
        Assert.Equal(2.5m, pairings[0].HomeScore);
        Assert.Equal(1.5m, pairings[0].AwayScore);
        Assert.Equal("Norway *)", pairings[1].HomeTeamName);
    }

    [Fact]
    public async Task ParseTeamPairingsAsync_LeagueLayoutWithDateColumns_ParsesPairings()
    {
        // Liga-Layout (1. Bundesliga AUT, tnr929746): Datum/Zeit/Ort HINTER den Ergebnissen.
        var html = @"<html><body><h2>Teamauslosung</h2><table class='CRs1'>
            <tr class='CRg1b'><td class='none' colspan='9'>3. Runde</td></tr>
            <tr class='CRg1b'><th>Nr.</th><th>Team</th><th>Team</th><th>Erg.</th><th>:</th><th>Erg.</th><th>Datum</th><th>Zeit</th><th>Ort</th></tr>
            <tr><td>1</td><td>Royal Salzburg</td><td>ASV Linz</td><td>4&frac12;</td><td>:</td><td>1&frac12;</td><td>11.10.2024</td><td>14.00 Uhr</td><td>Rathaus Linz</td></tr>
            </table></body></html>";

        var pairing = Assert.Single(await _parser.ParseTeamPairingsAsync(html));

        Assert.Equal("Royal Salzburg", pairing.HomeTeamName);
        Assert.Equal("ASV Linz", pairing.AwayTeamName);
        Assert.Equal(4.5m, pairing.HomeScore);
        Assert.Equal(1.5m, pairing.AwayScore);
    }

    [Fact]
    public async Task ParseTotalRoundsAsync_DetailsRundenanzahl_ReturnsPlannedRounds()
    {
        // Die Seite nennt sonst nur „nach der 10 Runde" (gespielt) — gebraucht wird die geplante Zahl.
        var rounds = await _parser.ParseTotalRoundsAsync(Fixture("team-pairings-olympiad-tnr1469895.html"));

        Assert.Equal(11, rounds);
    }

    [Fact]
    public async Task ParseTotalRoundsAsync_EnglishNumberOfRounds_ReturnsPlannedRounds()
    {
        var html = "<html><body><table>" +
                   "<tr><td class='CR'>Time control (Standard)</td><td class='CR'>90 min</td></tr>" +
                   "<tr><td class='CR'>Number of rounds</td><td class='CR'>11</td></tr>" +
                   "</table><h2>Rank after Round 10</h2></body></html>";

        Assert.Equal(11, await _parser.ParseTotalRoundsAsync(html));
    }

    [Fact]
    public async Task HasPairingsTableAsync_BeforeFirstDraw_ReturnsFalse()
    {
        // So sieht art=2 eines kommenden Turniers aus (tnr1483729): Ueberschrift, keine Tabelle.
        var html = "<html><body><h2>Paarungen/Ergebnisse</h2></body></html>";

        Assert.False(await _parser.HasPairingsTableAsync(html));
        Assert.True(await _parser.HasPairingsTableAsync(Fixture("team-pairings-olympiad-tnr1469895.html")));
    }
}
