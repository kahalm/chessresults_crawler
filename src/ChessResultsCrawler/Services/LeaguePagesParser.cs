using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using ChessResultsCrawler.DTOs;

namespace ChessResultsCrawler.Services;

/// <summary>
/// Parser für die vier Seiten einer Mannschaftsliga, die LeagueHub (RookHub) braucht — Portierung von
/// parse.py der Python-Fassung (~/claude/league-analyzer):
/// art=2 (Mannschaftspaarungen aller Runden), art=3 (Brettpaarungen aller Runden + Rundendaten),
/// art=16 (Startrangliste = Meldelisten mit Brett), art=20 (Teamaufstellung mit Einzelergebnissen).
///
/// <para><b>Wichtig</b>: die Seiten nur mit <c>zeilen=99999</c> abrufen — ohne das kappt chess-results
/// art=3 (alle Runden) und art=16 stillschweigend bei rund 150 Zeilen (in der Python-Fassung so gefunden:
/// 1. Klasse 2024/25 zeigte Runde 1–7 statt 1–11).</para>
///
/// <para>Die Zeilen bleiben roh (Namen wie auf der Seite, „Brett nicht besetzt" inklusive); den Abgleich
/// Paarung ↔ Meldeliste (akademische Titel, FIDE-ID, Brett) macht RookHub.</para>
/// </summary>
public static class LeaguePagesParser
{
    private static readonly Regex RoundRe = new(@"^(\d+)\. Runde(?: am (\d\d\.\d\d\.\d{4}))?", RegexOptions.Compiled);
    private static readonly Regex BoardRe = new(@"^(\d+)\.(\d+)", RegexOptions.Compiled);
    private static readonly Regex ResultRe = new(@"^[01½+\-]{1,2} - [01½+\-]{1,2}$", RegexOptions.Compiled);
    private static readonly Regex TeamHeadRe = new(@"^(\d+)\.\s+(.*?)\s*\((.*)\)\s*$", RegexOptions.Compiled);
    private static readonly Regex PgnIdRe = new(@"PartieSuche\.aspx\?art=36&(?:amp;)?id=(\d+)", RegexOptions.Compiled);

    private static readonly Dictionary<string, (double Home, double Away)> Results = new()
    {
        ["1 - 0"] = (1, 0), ["0 - 1"] = (0, 1), ["½ - ½"] = (.5, .5), ["0 - 0"] = (0, 0),
        ["+ - -"] = (1, 0), ["- - +"] = (0, 1), ["- - -"] = (0, 0), ["0 - ½"] = (0, .5), ["½ - 0"] = (.5, 0),
    };

    private static async Task<IDocument> Doc(string html) =>
        await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html));

    /// <summary>Text wie BeautifulSoup <c>get_text(" ")</c> + Whitespace zusammengezogen.</summary>
    internal static string Text(INode n)
    {
        var parts = n.Descendants<IText>().Select(t => t.Data);
        return Regex.Replace(string.Join(" ", parts).Replace(' ', ' '), @"\s+", " ").Trim();
    }

    internal static double? Num(string s)
    {
        var t = Regex.Replace(s, @"\s+", " ").Trim().Replace("½", ".5").Replace(",", ".");
        if (t.StartsWith('.')) t = "0" + t;
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static int? Int(string? s) => s is not null && Regex.IsMatch(s, @"^\d+$") ? int.Parse(s, CultureInfo.InvariantCulture) : null;

    private static IEnumerable<IHtmlTableElement> Tables(IDocument d) => d.QuerySelectorAll("table.CRs1").OfType<IHtmlTableElement>();

    private static bool IsClass(IElement e, string cls) => e.ClassList.Length == 1 && e.ClassList.Contains(cls);

    private static string? Col(IReadOnlyList<string> hdr, IReadOnlyList<string> txt, string col)
    {
        var i = hdr.ToList().IndexOf(col);
        return i >= 0 && i < txt.Count ? txt[i] : null;
    }

    /// <summary>art=2: Mannschaftskämpfe aller Runden (Freilos: Gast = „spielfrei").</summary>
    public static async Task<List<LeagueMatchRow>> ParseMatchesAsync(string html)
    {
        var d = await Doc(html);
        var out_ = new List<LeagueMatchRow>();
        foreach (var tbl in Tables(d))
        {
            int? rnd = null;
            List<string>? hdr = null;
            foreach (var tr in tbl.Rows)
            {
                var cs = tr.Cells.ToList();
                var txt = cs.Select(c => Text(c)).ToList();
                if (cs.Count == 1)
                {
                    var m = RoundRe.Match(txt[0]);
                    if (m.Success) rnd = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    continue;
                }
                if (tr.QuerySelector("th") is not null) { hdr = txt; continue; }
                if (hdr is null || rnd is null) continue;
                var ti = hdr.Select((h, i) => (h, i)).Where(x => x.h == "Team").Select(x => x.i).ToList();
                var ei = hdr.Select((h, i) => (h, i)).Where(x => x.h == "Erg.").Select(x => x.i).ToList();
                if (ti.Count < 2) continue;
                out_.Add(new LeagueMatchRow(rnd.Value, Int(txt[0]), txt[ti[0]], txt[ti[1]],
                    ei.Count > 0 ? Num(txt[ei[0]]) : null, ei.Count > 1 ? Num(txt[ei[1]]) : null,
                    Col(hdr, txt, "Datum"), Col(hdr, txt, "Zeit"), Col(hdr, txt, "Ort")));
            }
        }
        return out_;
    }

    /// <summary>art=3: Brettpartien aller Runden + Datum je Runde („1. Runde am 03.10.2026").</summary>
    public static async Task<(List<LeagueGameRow> Games, Dictionary<int, string?> RoundDates)> ParseGamesAsync(string html)
    {
        var d = await Doc(html);
        var games = new List<LeagueGameRow>();
        var dates = new Dictionary<int, string?>();
        foreach (var tbl in Tables(d))
        {
            int? rnd = null;
            (string Home, string Away)? match = null;
            foreach (var tr in tbl.Rows)
            {
                var cs = tr.Cells.ToList();
                var txt = cs.Select(c => Text(c)).ToList();
                if (cs.Count == 1)
                {
                    var m = RoundRe.Match(txt[0]);
                    if (m.Success)
                    {
                        rnd = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                        dates[rnd.Value] = m.Groups[2].Success ? m.Groups[2].Value : null;
                    }
                    continue;
                }
                if (tr.QuerySelector("th") is not null)
                {
                    // Br. | snr | Heim | - | snr | Gast | Ergebnis
                    var teams = cs.Select((c, i) => (c, i)).Where(x => IsClass(x.c, "CR")).Select(x => txt[x.i]).ToList();
                    if (teams.Count >= 2) match = (teams[0], teams[1]);
                    continue;
                }
                if (match is null || rnd is null) continue;
                var pcells = cs.Select((c, i) => (c, i)).Where(x => x.c.QuerySelector("table") is not null).Select(x => x.i).ToList();
                if (pcells.Count != 2)
                {
                    // „Brett nicht besetzt" ohne innere Tabelle: Namen stehen in CR-Zellen
                    pcells = cs.Select((c, i) => (c, i)).Where(x => IsClass(x.c, "CR")).Select(x => x.i).ToList();
                    if (pcells.Count != 2) continue;
                }
                var b = BoardRe.Match(txt[0]);
                if (!b.Success) continue;
                // Ergebnis = letzte Zelle im Muster „x - y" (danach kann noch eine PGN-Spalte folgen, LL 2022/23)
                var res = txt.AsEnumerable().Reverse().FirstOrDefault(x => ResultRe.IsMatch(x)) ?? "";
                var pgn = tr.QuerySelectorAll("a[href]").Select(a => PgnIdRe.Match(a.GetAttribute("href") ?? ""))
                    .FirstOrDefault(m => m.Success)?.Groups[1].Value;
                var hasScore = Results.TryGetValue(res, out var sc);
                var forfeit = res == "- - -" ? 2 : res.Contains('+') ? 1 : 0;
                string? Title(int i)
                {
                    if (i <= 0 || cs[i - 1].QuerySelector("table") is not null || !IsClass(cs[i - 1], "CRc")) return null;
                    var t = txt[i - 1];
                    return t is "-" or "" || char.IsDigit(t[0]) ? null : t;
                }
                string? Color(IElement c) =>
                    c.QuerySelector("div[class*='Farbew']") is not null ? "w"
                    : c.QuerySelector("div[class*='Farbes']") is not null ? "s" : null;
                games.Add(new LeagueGameRow(rnd.Value, int.Parse(b.Groups[1].Value, CultureInfo.InvariantCulture),
                    int.Parse(b.Groups[2].Value, CultureInfo.InvariantCulture), match.Value.Home, match.Value.Away,
                    txt[pcells[0]], txt[pcells[1]], Title(pcells[0]), Title(pcells[1]), Color(cs[pcells[0]]), res,
                    hasScore ? sc.Home : null, hasScore ? sc.Away : null, forfeit, pgn));
            }
        }
        return (games, dates);
    }

    /// <summary>art=16: alle gemeldeten Spieler mit Team und Meldebrett.</summary>
    public static async Task<List<LeagueRosterRow>> ParseRosterAsync(string html)
    {
        var d = await Doc(html);
        var out_ = new List<LeagueRosterRow>();
        foreach (var tbl in Tables(d))
        {
            List<string>? hdr = null;
            foreach (var tr in tbl.Rows)
            {
                var txt = tr.Cells.Select(c => Text(c)).ToList();
                if (tr.QuerySelector("th") is not null) { hdr = txt; continue; }
                if (hdr is null || !hdr.Contains("Name") || !hdr.Contains("Team")) continue;
                var ni = hdr.IndexOf("Name");
                out_.Add(new LeagueRosterRow(
                    Int(Col(hdr, txt, "Nr.")),
                    ni > 0 && hdr[ni - 1] == "" && ni - 1 < txt.Count ? txt[ni - 1] : null,
                    Col(hdr, txt, "Name") ?? "",
                    string.IsNullOrEmpty(Col(hdr, txt, "FideID")) ? null : Col(hdr, txt, "FideID"),
                    Int(Col(hdr, txt, "EloI")) ?? Int(Col(hdr, txt, "Elo")),
                    Int(Col(hdr, txt, "EloN")),
                    Col(hdr, txt, "Land"), Col(hdr, txt, "Team") ?? "", Int(Col(hdr, txt, "Br."))));
            }
        }
        return out_;
    }

    /// <summary>art=20: Einsatz-Statistik je Team (Punkte, Partien, Elo-Performance).</summary>
    public static async Task<List<LeagueStatsRow>> ParseStatsAsync(string html)
    {
        var d = await Doc(html);
        var out_ = new List<LeagueStatsRow>();
        foreach (var tbl in Tables(d))
        {
            string? team = null;
            List<string>? hdr = null;
            foreach (var tr in tbl.Rows)
            {
                var cs = tr.Cells.ToList();
                var txt = cs.Select(c => Text(c)).ToList();
                if (cs.Count == 1)
                {
                    var m = TeamHeadRe.Match(txt[0]);
                    if (m.Success) team = m.Groups[2].Value;
                    continue;
                }
                if (tr.QuerySelector("th") is not null) { hdr = txt; continue; }
                if (hdr is null || team is null || !hdr.Contains("Name")) continue;
                out_.Add(new LeagueStatsRow(team, Int(Col(hdr, txt, "Br.")), Col(hdr, txt, "Name") ?? "",
                    Num(Col(hdr, txt, "Pkt.") ?? ""), Int(Col(hdr, txt, "Anz")), Int(Col(hdr, txt, "EloDS"))));
            }
        }
        return out_;
    }
}
