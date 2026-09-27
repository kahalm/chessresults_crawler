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
