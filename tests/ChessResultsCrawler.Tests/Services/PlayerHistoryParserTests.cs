using ChessResultsCrawler.Services;

namespace ChessResultsCrawler.Tests.Services;

/// <summary>
/// Die Turnierhistorie eines Spielers und seine Spielerkarte — gegen echtes, getrimmtes
/// chess-results-Markup.
///
/// <para>Beide Seiten sind die einzige Quelle fuer Daten, die es sonst nirgends gibt: die
/// Spielersuche liefert ALLE Teilnahmen eines Spielers (vergangene und kuenftige) samt der
/// STARTNUMMER, die nur im Link steht; die Spielerkarte liefert Punkte, Platz,
/// Performance-Rating und Elo-Aenderung. Faellt hier etwas um, hat chess-results das Markup
/// umgebaut — sonst wuerde es nur als still leere Historie auffallen.</para>
/// </summary>
public class PlayerHistoryParserTests
{
    private readonly HtmlParserService _parser = new();

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public async Task ParsePlayerTournamentsAsync_RealSearchPage_ReadsEveryColumn()
    {
        var rows = await _parser.ParsePlayerTournamentsAsync(Fixture("player-search.html"));

        Assert.Equal(4, rows.Count);

        var open = rows.Single(r => r.TournamentName.StartsWith("Schach Tirol Open"));
        Assert.Equal("1107064", open.TournamentId);
        Assert.Equal("2025/08/30", open.EndDate);
        Assert.Equal(56, open.Rank);
        Assert.Equal(9, open.Rounds);
        Assert.Equal(56, open.PlayerCount);
        Assert.Equal("144749", open.IdentNumber);
        Assert.Equal("1693034", open.FideId);
        Assert.Equal("Schwaz", open.Club);
        Assert.Equal("AUT", open.Federation);
    }

    /// <summary>
    /// Die Startnummer steht in KEINER Spalte — nur im Link auf den Spielernamen
    /// (<c>tnr…?lan=1&amp;art=9&amp;snr=44</c>). Ohne sie ist die Spielerkarte dieses Turniers
    /// nicht adressierbar, und damit gaebe es weder Punkte noch Performance.
    /// </summary>
    [Fact]
    public async Task ParsePlayerTournamentsAsync_StartingNumber_ComesFromTheNameLink()
    {
        var rows = await _parser.ParsePlayerTournamentsAsync(Fixture("player-search.html"));

        Assert.Equal(44, rows.Single(r => r.TournamentId == "1107064").Snr);
        Assert.All(rows, r => Assert.NotNull(r.Snr));
    }

    /// <summary>
    /// „-" in der Platz-Spalte heisst „noch nicht gespielt". Das ist die Unterscheidung, an der
    /// haengt, ob ein Kartenabruf ueberhaupt lohnt — und sie darf nicht als Platz 0 ankommen.
    /// </summary>
    [Fact]
    public async Task ParsePlayerTournamentsAsync_FutureTournament_HasNoRank()
    {
        var rows = await _parser.ParsePlayerTournamentsAsync(Fixture("player-search.html"));

        var league = rows.Single(r => r.TournamentName.StartsWith("TMM 1.Klasse"));
        Assert.Null(league.Rank);
        Assert.Equal(11, league.Rounds);
        Assert.NotNull(league.Snr);
    }

    /// <summary>
    /// Bei einem AUSLANDS-Turnier ist die Ident-Nummer „0" — dort traegt allein die Fide-ID die
    /// Identitaet. Wer die Zeilen nur ueber die Ident-Nummer dem Konto zuordnet, verliert genau
    /// diese Turniere.
    /// </summary>
    [Fact]
    public async Task ParsePlayerTournamentsAsync_ForeignTournament_KeepsTheFideId()
    {
        var rows = await _parser.ParsePlayerTournamentsAsync(Fixture("player-search.html"));

        var abroad = rows.Single(r => r.TournamentName.Contains("Tegernsee"));
        Assert.Equal("0", abroad.IdentNumber);
        Assert.Equal("1693034", abroad.FideId);
    }

    [Fact]
    public async Task ParsePlayerCardAsync_RealCard_ReadsPointsRankAndPerformance()
    {
        var card = await _parser.ParsePlayerCardAsync(Fixture("player-card.html"));

        Assert.NotNull(card);
        Assert.True(card!.HasResult);
        Assert.Equal(1.5m, card.Points);
        Assert.Equal(56, card.Rank);
        Assert.Equal(1740, card.PerformanceRating);
        Assert.Equal(44, card.StartingRank);
        Assert.Equal(1854, card.RatingNational);
        Assert.Equal(1923, card.RatingInternational);
        Assert.Equal("144749", card.IdentNumber);
        Assert.Equal("1693034", card.FideId);
        Assert.Equal(1983, card.YearOfBirth);
    }

    /// <summary>
    /// Gezaehlt werden die tatsaechlich gespielten Partien, nicht die Rundenzahl des Turniers: in
    /// einer Liga steht ein Spieler an Brett 17 und wird an einem TEIL der Termine aufgestellt.
    /// Eine Zeile ohne Gegnernamen ist ein Freilos und keine Partie.
    /// </summary>
    [Fact]
    public async Task ParsePlayerCardAsync_CountsTheGamesActuallyPlayed()
    {
        var card = await _parser.ParsePlayerCardAsync(Fixture("player-card.html"));

        Assert.Equal(1, card!.GamesPlayed);
    }

    /// <summary>Eine Zeile ohne Gegner (Freilos, nicht angetreten) zaehlt nicht mit.</summary>
    [Fact]
    public async Task ParsePlayerCardAsync_ByeRowsDoNotCount()
    {
        var html = """
            <html><body>
              <table><tr><td>Name</td><td>Oberschmid Patrik</td></tr>
                     <tr><td>Points</td><td>2</td></tr></table>
              <table>
                <tr><td>Rd.</td><td>Bo.</td><td>SNo</td><td>Name</td><td>Res.</td></tr>
                <tr><td>1</td><td>16</td><td>16</td><td>Bodrov Timofey</td><td>1</td></tr>
                <tr><td>2</td><td></td><td></td><td></td><td>-</td></tr>
                <tr><td>3</td><td>8</td><td>9</td><td>Musterfrau Anna</td><td>0</td></tr>
              </table>
            </body></html>
            """;

        var card = await _parser.ParsePlayerCardAsync(html);

        Assert.Equal(2, card!.GamesPlayed);
    }

    /// <summary>
    /// chess-results schreibt Kommazahlen mit KOMMA, auch auf der englischen Seite: „1,5" und
    /// „-51,6". Mit invarianter Kultur allein gelesen wuerde aus 1,5 Punkten eine 15 und aus
    /// -51,6 Elo eine -516.
    /// </summary>
    [Fact]
    public async Task ParsePlayerCardAsync_DecimalComma_IsReadCorrectly()
    {
        var card = await _parser.ParsePlayerCardAsync(Fixture("player-card.html"));

        Assert.Equal(-51.6m, card!.RatingChange);
    }

    /// <summary>
    /// Deutsche Fassung (lan=0) der Spielerkarte: dort heissen die Wertungszeilen
    /// „Elo national" / „Elo international" (wie „Elorechnung: Elo national" auf den
    /// Turnierseiten). Der Rueckfall nannte frueher zweimal den englischen Namen.
    /// </summary>
    [Fact]
    public async Task ParsePlayerCardAsync_GermanLabels_ReadsRatings()
    {
        var html = """
            <html><body>
              <table><tr><td>Name</td><td>Oberschmid Patrik</td></tr>
                     <tr><td>Elo national</td><td>1854</td></tr>
                     <tr><td>Elo international</td><td>1923</td></tr>
                     <tr><td>Punkte</td><td>1,5</td></tr></table>
            </body></html>
            """;

        var card = await _parser.ParsePlayerCardAsync(html);

        Assert.Equal(1854, card!.RatingNational);
        Assert.Equal(1923, card.RatingInternational);
    }

    [Fact]
    public async Task ParsePlayerCardAsync_PageWithoutTheBlock_ReturnsNull()
    {
        Assert.Null(await _parser.ParsePlayerCardAsync("<html><body><p>nichts</p></body></html>"));
    }

    /// <summary>
    /// Der Fund, der diese Datei ausgeloest hat: auf der art=9-Seite tragen ZWEI Tabellen die
    /// Klasse CRs1, und die erste ist der zweispaltige Player-info-Block. Die frueher zuerst
    /// versuchte Klassen-Auswahl griff also den falschen Block — dessen Zeilen haben zwei
    /// Zellen, fielen durch die Mindestzellen-Pruefung, und die Rundenliste kam LEER zurueck.
    /// Gegen die alte Fassung faellt dieser Test um.
    /// </summary>
    [Fact]
    public async Task ParsePlayerDetailPageAsync_PageWithPlayerInfoBlock_StillFindsTheRounds()
    {
        var rounds = await _parser.ParsePlayerDetailPageAsync(Fixture("player-card.html"));

        var round = Assert.Single(rounds);
        Assert.Equal(1, round.RoundNumber);
        Assert.Equal("Bodrov Timofey", round.OpponentName);
        Assert.Equal(2129, round.OpponentElo);
    }

    // ----- Rundenplan -------------------------------------------------------

    /// <summary>
    /// Start und Ende einer LIGA sagen nicht, wann gespielt wird: elf Runden von September bis
    /// April liegen zwei bis fuenf Wochen auseinander. Im Kalender stand die Liga damit an rund
    /// 200 Tagen, an denen nichts stattfindet.
    /// </summary>
    [Fact]
    public async Task ParseRoundPlanAsync_RealLeaguePlan_ReadsEveryRound()
    {
        var rounds = await _parser.ParseRoundPlanAsync(Fixture("round-plan.html"));

        Assert.Equal(11, rounds.Count);
        Assert.Equal(1, rounds[0].Number);
        Assert.Equal(new DateOnly(2026, 9, 26), rounds[0].Date);
        Assert.Equal(new DateOnly(2027, 4, 17), rounds[^1].Date);
        Assert.Equal(11, rounds[^1].Number);
        Assert.Contains("14:00", rounds[0].TimeText);

        // Und der Beweis, dass Start bis Ende die falsche Auskunft ist: zwischen der ersten und
        // der zweiten Runde liegen zwei Wochen ohne Schach.
        Assert.Equal(14, rounds[1].Date.DayNumber - rounds[0].Date.DayNumber);
    }

    /// <summary>
    /// Kein hinterlegter Plan ist der Normalfall bei den meisten Turnieren — eine leere Liste,
    /// kein Fehler. Der Kalender faellt dann auf Start bis Ende zurueck, was bei einem
    /// Wochenend-Open auch richtig ist.
    /// </summary>
    /// <summary>
    /// DIE GANZE Seite, nicht der ausgeschnittene Rundenplan.
    ///
    /// <para>Der Fehler, den nur das findet: auf der echten Seite steckt die Rundenplan-Tabelle
    /// DREI Tabellen tief. <c>FindTableByHeaders</c> nahm die erste Tabelle, deren erste Zeile
    /// die Kopfnamen „enthaelt" — und <c>TextContent</c> ist REKURSIV, also enthaelt schon die
    /// aeusserste Wrapper-Tabelle „Round" und „Date". Zurueck kam die Wrapper-Tabelle, deren
    /// eigene Zeilen keine Runden sind: das Ergebnis war eine LEERE Liste.</para>
    ///
    /// <para>Auf dem Dev-Stand nachgemessen, nachdem es ausgerollt war: 337 Turniere geprueft,
    /// <b>0 Spieltermine</b> gespeichert. Die getrimmte Fixture konnte das nicht zeigen — dort
    /// haengt die Tabelle direkt unter einem <c>div</c>. Deshalb steht sie hier vollstaendig.</para>
    /// </summary>
    [Fact]
    public async Task ParseRoundPlanAsync_VollstaendigeSeite_LiestDieRunden()
    {
        var rounds = await _parser.ParseRoundPlanAsync(Fixture("roundplan-tnr1488755.html"));

        Assert.Equal(10, rounds.Count);
        Assert.Equal(1, rounds[0].Number);
        Assert.Equal(new DateOnly(2026, 9, 26), rounds[0].Date);
        Assert.Equal("15:00 Uhr", rounds[0].TimeText);
        Assert.Equal(new DateOnly(2027, 4, 10), rounds[^1].Date);
        // Und die Abstaende sind der Grund, warum es das Feature gibt: Wochen, nicht Tage.
        Assert.True((rounds[1].Date.DayNumber - rounds[0].Date.DayNumber) > 14);
    }

    [Fact]
    public async Task ParseRoundPlanAsync_PageWithoutAPlan_ReturnsEmpty()
    {
        Assert.Empty(await _parser.ParseRoundPlanAsync("<html><body><p>nichts</p></body></html>"));
    }

    /// <summary>
    /// Beide Datumsformate: „yyyy/MM/dd" liefert die englische Seite (lan=1), „dd.MM.yyyy" die
    /// deutsche. Ein Sprachwechsel darf den Rundenplan nicht still leer laufen lassen.
    /// </summary>
    [Theory]
    [InlineData("2026/09/26")]
    [InlineData("26.09.2026")]
    public async Task ParseRoundPlanAsync_BothDateFormats_AreRead(string date)
    {
        var html = $"""
            <table class="CRs1"><tr><th>Round</th><th>Date</th><th>Time</th></tr>
            <tr><td>1</td><td>{date}</td><td>14:00 Uhr</td></tr></table>
            """;

        var round = Assert.Single(await _parser.ParseRoundPlanAsync(html));
        Assert.Equal(new DateOnly(2026, 9, 26), round.Date);
    }
}
