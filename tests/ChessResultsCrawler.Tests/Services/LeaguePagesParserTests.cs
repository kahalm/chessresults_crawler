using ChessResultsCrawler.Services;

namespace ChessResultsCrawler.Tests.Services;

/// <summary>
/// LeagueHub-Seiten, geparst gegen echte chess-results-Seiten (Fixtures). Die Erwartungswerte hat
/// parse.py der Python-Fassung aus denselben Dateien gerechnet — beide Parser müssen gleich zählen.
/// </summary>
public class LeaguePagesParserTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public async Task Matches_AllRounds_WithScores()
    {
        var m = await LeaguePagesParser.ParseMatchesAsync(Fixture("league-1479341-art2.html"));
        Assert.Equal(15, m.Count);
        Assert.Equal(1, m[0].Round);
        Assert.Equal("Absam", m[0].Home);
        Assert.Equal("Sportverein Innsbruck", m[0].Away);
        Assert.Equal(1.5, m[0].HomePts);
        Assert.Equal(2.5, m[0].AwayPts);
        Assert.Null(m[0].Venue);   // Gebietsklasse: kein Spielort in der Tabelle
    }

    [Fact]
    public async Task Games_WithRoundDates_AndEmptyBoards()
    {
        var (g, dates) = await LeaguePagesParser.ParseGamesAsync(Fixture("league-1479341-art3.html"));
        Assert.Equal(60, g.Count);
        var first = g[0];
        Assert.Equal((1, 1, 1), (first.Round, first.MatchNo, first.Board));
        Assert.Equal("Kiechl, Simon", first.HomePlayer);
        Assert.Equal("Mitteregger, Johann", first.AwayPlayer);
        Assert.Equal("w", first.HomeColor);
        Assert.Equal("0 - 1", first.Result);
        Assert.Equal(0, first.HomeScore);
        Assert.Equal(1, first.AwayScore);
        Assert.Equal(48, g.Count(x => x.HomePlayer == "Brett nicht besetzt" || x.AwayPlayer == "Brett nicht besetzt"));
        Assert.Equal(5, dates.Count);
        Assert.Equal("26.09.2026", dates[1]);
        Assert.Equal("28.11.2026", dates[5]);
    }

    [Fact]
    public async Task Games_PgnColumn_DoesNotHideTheResult()
    {
        // Landesliga 2022/23: nach dem Ergebnis steht eine PGN-Spalte, Namen ohne Komma
        var (g, _) = await LeaguePagesParser.ParseGamesAsync(Fixture("league-668636-art3.html"));
        Assert.Equal(270, g.Count);
        var b2 = g[1];
        Assert.Equal("Kruckenhauser Arthur", b2.HomePlayer);
        Assert.Equal("FM", b2.HomeTitle);
        Assert.Equal("s", b2.HomeColor);
        Assert.Equal("1 - 0", b2.Result);
        Assert.Equal("4441445", b2.PgnId);
        Assert.Equal(258, g.Count(x => x.PgnId is not null));
        Assert.Equal(11, g.Count(x => x.Result == "+ - -"));
        Assert.All(g.Where(x => x.Result == "+ - -"), x => Assert.Equal(1, x.Forfeit));
    }

    [Fact]
    public async Task Games_BundesligaFedColumn_GuestIsTheTeamNotFed()
    {
        // 1. Bundesliga 2026/27 (tnr1404438, Runde 1–2 abgerufen 25.11.2026): je Seite eine Spalte „FED" hinter der Elo.
        // Bis zum Fix stand als Gast in allen 396 Zeilen „FED" (Prod), RookHub fand damit keine Meldeliste → AwayFide leer.
        var (g, dates) = await LeaguePagesParser.ParseGamesAsync(Fixture("league-1404438-art3.html"));
        Assert.Equal(72, g.Count);   // 2 Runden × 6 Begegnungen × 6 Bretter
        Assert.DoesNotContain(g, x => x.AwayTeam is "FED" or "" || x.HomeTeam is "FED" or "");
        var r1 = g.Where(x => x is { Round: 1, MatchNo: 1 }).OrderBy(x => x.Board).ToList();
        Assert.Equal(6, r1.Count);
        Assert.All(r1, x => Assert.Equal(("Pinggau-Friedberg", "Schachklub Schwaz"), (x.HomeTeam, x.AwayTeam)));
        Assert.Equal(new[] { "Aditya, Mittal", "Vaibhav, Suri", "Kadric, Denis", "Erdos, Viktor", "Raja, Rithvik R", "Ivanisevic, Ivan" },
            r1.Select(x => x.HomePlayer));
        Assert.Equal(new[] { "Hagner, Bennet", "Schmider, Niklas", "Lehner, Oliver" }, r1.Take(3).Select(x => x.AwayPlayer));
        Assert.Equal(("GM", "IM"), (r1[0].HomeTitle, r1[0].AwayTitle));
        Assert.Equal("w", r1[0].HomeColor);
        Assert.Equal("s", r1[1].HomeColor);
        Assert.All(r1, x => Assert.Equal("", x.Result));   // noch nicht gespielt
        Assert.Contains(g, x => x is { Round: 2, HomeTeam: "Schachklub Schwaz", AwayTeam: "SV Gamlitz" });
        Assert.Equal(12, g.Select(x => x.HomeTeam).Concat(g.Select(x => x.AwayTeam)).Distinct().Count());
        Assert.Equal(("25.11.2026", "26.11.2026"), (dates[1], dates[2]));
    }

    [Fact]
    public async Task Games_BundesligaUnlabelledFedColumn_WithResultsAndPgn()
    {
        // 2. Bundesliga 2025/26 (tnr1174791, Runde 1–2): Föderations-Spalte OHNE Beschriftung + PGN-Spalte hinter dem Ergebnis.
        // Bis zum Fix war der Gast hier leer.
        var (g, _) = await LeaguePagesParser.ParseGamesAsync(Fixture("league-1174791-art3.html"));
        Assert.Equal(72, g.Count);
        Assert.DoesNotContain(g, x => x.AwayTeam == "" || x.HomeTeam == "");
        var b1 = g[0];
        Assert.Equal(("SC Die Klagenfurter", "Styria TU Graz"), (b1.HomeTeam, b1.AwayTeam));
        Assert.Equal(("Genser, Harald", "Mihok, Oliver"), (b1.HomePlayer, b1.AwayPlayer));
        Assert.Equal(("FM", "GM"), (b1.HomeTitle, b1.AwayTitle));
        Assert.Equal("0 - 1", b1.Result);
        Assert.Equal((0.0, 1.0), (b1.HomeScore!.Value, b1.AwayScore!.Value));
        Assert.Equal("6144232", b1.PgnId);
        // Brettpunkte je Begegnung = Mannschaftsstand der Kopfzeile (1 : 5)
        Assert.Equal(1.0, g.Where(x => x is { Round: 1, MatchNo: 1 }).Sum(x => x.HomeScore ?? 0));
        Assert.Equal(5.0, g.Where(x => x is { Round: 1, MatchNo: 1 }).Sum(x => x.AwayScore ?? 0));
        Assert.Contains(g, x => x.AwayTeam == "SV \"Das Kärnten\" - St. Veit 2");
    }

    [Theory]
    [InlineData("Br.|1|Absam|Elo|-|5|Sportverein Innsbruck|Elo|1½:2½", 2, 6, 8)]
    [InlineData("Br.|1|Pinggau-Friedberg|Elo|FED|-|12|Schachklub Schwaz|Elo|FED|0 : 0", 2, 7, 10)]
    [InlineData("Br.|5|SC Die Klagenfurter|Elo||-|1|Styria TU Graz|Elo||1 : 5|PGN", 2, 7, 10)]
    [InlineData("Br.|1|SV - Das Wien - St. Veit|Elo|FED|-|8|SK Austria Wien|Elo|FED|0 : 0", 2, 7, 10)]
    public void GameColumns_FromHeader_FindsTeamsByLabel(string header, int home, int away, int result)
    {
        var c = LeaguePagesParser.GameColumns.FromHeader(header.Split('|'));
        Assert.NotNull(c);
        Assert.Equal((home, away, (int?)result), (c.Home, c.Away, c.Result));
    }

    [Fact]
    public async Task Roster_AllPlayers_WithTeamAndBoard()
    {
        var r = await LeaguePagesParser.ParseRosterAsync(Fixture("league-1479341-art16.html"));
        Assert.Equal(105, r.Count);
        Assert.Equal("Mitteregger, Johann", r[0].Name);
        Assert.Equal("1635930", r[0].FideId);
        Assert.Equal(1910, r[0].EloI);
        Assert.Equal("Sportverein Innsbruck", r[0].Team);
        Assert.Equal(1, r[0].RosterBoard);
    }

    [Fact]
    public async Task Stats_PerTeam()
    {
        var s = await LeaguePagesParser.ParseStatsAsync(Fixture("league-1479341-art20.html"));
        Assert.Equal(24, s.Count);
        Assert.Contains(s, x => x.Team == "Völs & Hak Ibk" && x.Name == "Willmann, Florian" && x.RosterBoard == 6
                                && x.Points == 1.0 && x.Games == 1 && x.EloPerf == 1625);
    }

    [Fact]
    public void Num_ReadsHalves() =>
        Assert.Equal(new double?[] { 1.5, 0.5, 3.5, null }, new[] { "1½", "½", "3,5", "x" }.Select(LeaguePagesParser.Num).ToArray());

    [Fact]
    public void PageUrl_AlwaysAsksForAllRows() =>
        Assert.Contains("zeilen=99999", ChessResultsCrawler.Controllers.LeagueController.PageUrl(1, 3));
}
