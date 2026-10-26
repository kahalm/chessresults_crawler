using AngleSharp.Dom;
using static ChessResultsCrawler.Services.HtmlTable;

namespace ChessResultsCrawler.Services;

/// <summary>
/// Paarungsseiten (art=2) von chess-results: Mannschaftspaarungen, Einzelpaarungen und die
/// Unterscheidung beider, samt Ergebnis-/Punkte-Lesen. Aufgerufen ueber <see cref="HtmlParserService"/>.
/// </summary>
internal static class PairingsParser
{
    /// <summary>
    /// Parses art=2 page (team pairings / Auslosungen) for a specific round.
    /// Format: Nr | HomeTeam | AwayTeam | HomeScore | : | AwayScore
    /// First row may be a date header (colspan), second row is the column header.
    /// </summary>
    public static async Task<List<ParsedTeamPairing>> ParseTeamPairingsAsync(string html)
    {
        var pairings = new List<ParsedTeamPairing>();
        var document = await OpenAsync(html);

        var table = document.QuerySelector("table.CRs1")
            ?? document.QuerySelector("table.CRs2");
        if (table is null) return pairings;

        var allRows = table.QuerySelectorAll(":scope > tr, :scope > tbody > tr");
        var columns = FindTeamPairingColumns(allRows);

        foreach (var row in allRows)
        {
            var cells = row.QuerySelectorAll(":scope > td").ToList();

            // Skip rows with colspan (date headers like "1. Runde am ...") or too few cells
            if (cells.Count < 4) continue;
            if (cells.Any(c => c.HasAttribute("colspan"))) continue;

            // Skip header rows (th cells)
            if (row.QuerySelectorAll(":scope > th").Length > 0) continue;

            // First cell should be match number
            var nrText = cells[0].TextContent.Trim();
            // Echte Nr. aus der Tabelle verwenden statt eines eigenen Zaehlers
            // (sonst weichen MatchNumbers bei uebersprungenen/sortierten Zeilen ab).
            if (!int.TryParse(nrText, out var matchNo)) continue;
            var pairing = new ParsedTeamPairing { MatchNumber = matchNo };

            if (columns is { } col)
            {
                // Spalten aus der Kopfzeile (siehe FindTeamPairingColumns). Eine Zeile, der die
                // hinterste benoetigte Spalte fehlt, ist keine Paarung dieses Layouts.
                if (cells.Count <= col.Last) continue;
                pairing.HomeTeamName = CleanTeamName(cells[col.Home].TextContent);
                pairing.AwayTeamName = CleanTeamName(cells[col.Away].TextContent);
                ParseSplitScore(cells[col.Colon - 1].TextContent.Trim(), cells[col.Colon + 1].TextContent.Trim(), pairing);
            }
            else if (cells.Count >= 6)
            {
                // Standard format: Nr | HomeTeam | AwayTeam | HomeScore | : | AwayScore
                pairing.HomeTeamName = CleanTeamName(cells[1].TextContent);
                pairing.AwayTeamName = CleanTeamName(cells[2].TextContent);
                ParseSplitScore(cells[3].TextContent.Trim(), cells[5].TextContent.Trim(), pairing);
            }
            else
            {
                // Compact format: Nr | HomeTeam | AwayTeam | CombinedScore
                pairing.HomeTeamName = CleanTeamName(cells[1].TextContent);
                pairing.AwayTeamName = CleanTeamName(cells[2].TextContent);
                ParseScore(cells.Count > 3 ? cells[3].TextContent.Trim() : null, pairing);
            }

            if (!string.IsNullOrWhiteSpace(pairing.HomeTeamName) &&
                !string.IsNullOrWhiteSpace(pairing.AwayTeamName))
            {
                pairings.Add(pairing);
            }
        }

        return pairings;
    }

    /// <summary>
    /// Die Spalten der Mannschaftspaarung aus der KOPFZEILE (erste Zeile mit <c>th</c>): die beiden
    /// „Team"-Spalten und die „:"-Spalte, links und rechts davon stehen die Brettpunkte.
    ///
    /// <para>Noetig, weil das Layout nicht fest ist. Die Mannschafts-EM 2023 zeigt sechs Spalten
    /// (Nr. | Team | Team | Erg. | : | Erg.), die Olympiade 2026 (tnr1469895) dagegen sechzehn:
    /// Nr. | Snr | Flagge | FED | Team | Pkt. | MP | Erg. | : | Erg. | MP | Pkt. | Team | FED |
    /// Flagge | Snr. Mit den festen Indizes landete dort die Setznummer als Heimteam und die leere
    /// Flaggenzelle als Gastteam — jede Zeile fiel durch, jede Runde ergab 0 Paarungen, ohne Fehler
    /// und ohne Warnung (dasselbe Bild bei Olympiade 2024 und Mannschafts-EM 2025).</para>
    ///
    /// <para><c>null</c>, wenn die Kopfzeile fehlt oder nicht zwei Team-Spalten und eine „:"-Spalte
    /// nennt — dann greifen die bisherigen festen Positionen.</para>
    /// </summary>
    private static (int Home, int Away, int Colon, int Last)? FindTeamPairingColumns(IEnumerable<IElement> rows)
    {
        var header = rows.FirstOrDefault(r => r.QuerySelectorAll(":scope > th").Length > 0);
        if (header is null) return null;

        var names = header.QuerySelectorAll(":scope > th, :scope > td")
            .Select(c => c.TextContent.Trim())
            .ToList();
        var teamCols = names
            .Select((name, idx) => (name, idx))
            .Where(h => h.name.Equals("Team", StringComparison.OrdinalIgnoreCase)
                     || h.name.Equals("Mannschaft", StringComparison.OrdinalIgnoreCase))
            .Select(h => h.idx)
            .ToList();
        var colon = names.IndexOf(":");

        if (teamCols.Count < 2 || colon < 1 || colon + 1 >= names.Count) return null;

        // Welche der Team-Spalten ist das HEIM-, welche das GASTteam? Nicht „die ersten zwei": die
        // 22. Mannschafts-EM 2019 (tnr434677) nennt VIER Spalten „Team" — Kennung UND Name je Seite,
        // um das „:" gespiegelt (Nr. | Snr | Flagge | RUS | Russia | MP | Erg. | : | Erg. | MP |
        // Denmark | DEN | Flagge | Snr). Die ersten zwei waren damit Kennung und Name DERSELBEN
        // Mannschaft: „Team not found: RUS vs Russia", 20 Warnungen je Runde, 180 je Crawl, und
        // keine einzige gespeicherte Paarung (gemessen 01.10.2026 auf dev).
        //
        // Steht links UND rechts des „:" eine Team-Spalte, gilt die dem „:" NAECHSTE je Seite: diese
        // Tabellen sind um das Ergebnis gespiegelt aufgebaut, innen der NAME, aussen Kennung, Flagge
        // und Setznummer — und der Name ist es, den der Bestand kennt (die Teams entstehen im
        // Spieler-Crawl). Das deckt auch die Olympiade ab (dort heisst die Kennungs-Spalte „FED",
        // es gibt also nur je eine Team-Spalte). Nur wenn alle Team-Spalten LINKS des „:" stehen,
        // gelten die ersten zwei — das schlichte Layout „Nr. | Team | Team | Erg. | : | Erg.".
        var leftOfColon = teamCols.Where(i => i < colon).ToList();
        var rightOfColon = teamCols.Where(i => i > colon).ToList();
        int home, away;
        if (leftOfColon.Count > 0 && rightOfColon.Count > 0)
        {
            home = leftOfColon[^1];
            away = rightOfColon[0];
        }
        else if (leftOfColon.Count >= 2)
        {
            home = leftOfColon[0];
            away = leftOfColon[1];
        }
        else
        {
            return null;
        }
        return (home, away, colon, Math.Max(away, colon + 1));
    }

    /// <summary>
    /// Parses art=2 page for individual (non-team) pairings.
    /// Format: Br | Nr | Title | Name | Elo | Pts | Result | Pts | Title | Name | Elo | Nr | (PGN)
    /// Die Spalten kommen aus der Kopfzeile (<see cref="FindIndividualPairingColumns"/>); fehlt sie oder
    /// nennt sie die Spalten nicht, gelten die festen Positionen dieses Standardlayouts.
    /// </summary>
    public static async Task<List<ParsedPairing>> ParseIndividualPairingsAsync(string html)
    {
        var pairings = new List<ParsedPairing>();
        var document = await OpenAsync(html);

        var table = document.QuerySelector("table.CRs1")
            ?? document.QuerySelector("table.CRs2");
        if (table is null) return pairings;

        var allRows = table.QuerySelectorAll(":scope > tr, :scope > tbody > tr");
        var col = FindIndividualPairingColumns(allRows) ?? StandardIndividualColumns;

        foreach (var row in allRows)
        {
            var cells = row.QuerySelectorAll(":scope > td").ToList();
            if (cells.Count < col.MinCells) continue;
            if (row.QuerySelectorAll(":scope > th").Length > 0) continue;

            var boardText = cells[col.Board].TextContent.Trim();
            if (!int.TryParse(boardText, out var board)) continue;

            var whiteName = cells[col.White].TextContent.Trim();
            var blackName = cells[col.Black].TextContent.Trim();
            var result = cells[col.Result].TextContent.Trim().Replace(" ", "");

            int.TryParse(col.WhiteSnr >= 0 ? cells[col.WhiteSnr].TextContent.Trim() : "", out var whiteSnr);
            int.TryParse(col.BlackSnr >= 0 && cells.Count > col.BlackSnr ? cells[col.BlackSnr].TextContent.Trim() : "", out var blackSnr);

            pairings.Add(new ParsedPairing
            {
                BoardNumber = board,
                WhiteName = whiteName,
                BlackName = blackName,
                WhiteSnr = whiteSnr,
                BlackSnr = blackSnr,
                Result = NormalizeResult(result)
            });
        }

        return pairings;
    }

    /// <summary>Spalten einer Einzelpaarung; <c>-1</c> = Spalte fehlt. MinCells: Zeilen mit weniger Zellen sind keine Paarung.</summary>
    private readonly record struct IndividualColumns(int Board, int WhiteSnr, int White, int Result, int Black, int BlackSnr, int MinCells);

    /// <summary>Das Standardlayout Br(0) | Nr(1) | Ti(2) | Name(3) | Elo(4) | Pkt(5) | Erg(6) | Pkt(7) | Ti(8) | Name(9) | Elo(10) | Nr(11).</summary>
    private static readonly IndividualColumns StandardIndividualColumns = new(0, 1, 3, 6, 9, 11, 10);

    private static readonly string[] BoardHeaders = ["Br.", "Br", "Bo.", "Bo", "Brett", "Board"];
    private static readonly string[] SnrHeaders = ["Nr.", "Nr", "No.", "No", "SNr", "Snr", "SNo"];
    private static readonly string[] WhiteHeaders = ["Weiß", "Weiss", "White"];
    private static readonly string[] BlackHeaders = ["Schwarz", "Black"];
    private static readonly string[] ResultHeaders = ["Ergebnis", "Erg.", "Result", "Res."];

    /// <summary>
    /// Die Spalten der Einzelpaarung aus der KOPFZEILE (erste Zeile mit <c>th</c>), wie
    /// <see cref="FindTeamPairingColumns"/> fuer Mannschaften: Brett, Weiss/Schwarz (oder zweimal „Name"),
    /// Ergebnis, die Setznummer links vor Weiss und rechts nach Schwarz. Noetig, weil chess-results je nach
    /// Turnier Spalten ein- oder ausblendet (Titel, Land/FED, Verein) — mit festen Indizes landete dann
    /// Elo oder Punktestand als Name bzw. Ergebnis (S3-015). <c>null</c>, wenn die Kopfzeile fehlt oder
    /// Brett, beide Namen und Ergebnis nicht eindeutig nennt — dann greifen die festen Positionen.
    /// </summary>
    private static IndividualColumns? FindIndividualPairingColumns(IEnumerable<IElement> rows)
    {
        var header = rows.FirstOrDefault(r => r.QuerySelectorAll(":scope > th").Length > 0);
        if (header is null) return null;

        var names = header.QuerySelectorAll(":scope > th, :scope > td")
            .Select(c => c.TextContent.Trim())
            .ToList();
        int First(string[] wanted, int from = 0)
        {
            for (var i = from; i < names.Count; i++)
                if (wanted.Any(w => names[i].Equals(w, StringComparison.OrdinalIgnoreCase))) return i;
            return -1;
        }

        var board = First(BoardHeaders);
        var white = First(WhiteHeaders);
        var black = First(BlackHeaders);
        if (white < 0 || black < 0)
        {
            // Manche Seiten nennen beide Spieler-Spalten nur „Name".
            white = First(["Name"]);
            black = white < 0 ? -1 : First(["Name"], white + 1);
        }
        var result = white < 0 ? -1 : First(ResultHeaders, white + 1);
        if (board < 0 || white < 0 || black <= white || result <= white || result >= black) return null;

        var whiteSnr = -1;
        for (var i = white - 1; i > board; i--)
            if (SnrHeaders.Any(h => names[i].Equals(h, StringComparison.OrdinalIgnoreCase))) { whiteSnr = i; break; }
        var blackSnr = First(SnrHeaders, black + 1);

        // Die Setznummer von Schwarz darf fehlen (spielfrei), alles andere muss in der Zeile stehen.
        var minCells = new[] { board, whiteSnr, white, result, black }.Max() + 1;
        return new IndividualColumns(board, whiteSnr, white, result, black, blackSnr, minCells);
    }

    /// <summary>
    /// Detects whether an art=2 page contains team pairings or individual pairings.
    /// </summary>
    public static async Task<bool> IsTeamPairingsPageAsync(string html)
    {
        var document = await OpenAsync(html);
        var text = document.Body?.TextContent ?? "";
        // Team pages have "Teamauslosung" or "Team Composition" headers
        // Individual pages have "Paarungen" or "Pairings" headers
        // Also: team tables have "Erg." columns, individual have "Br." as first column
        if (text.Contains("Teamauslosung", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.Contains("Team Composition", StringComparison.OrdinalIgnoreCase)) return true;

        var table = document.QuerySelector("table.CRs1") ?? document.QuerySelector("table.CRs2");
        if (table is null) return false;
        var firstRow = table.QuerySelector(":scope > tr, :scope > tbody > tr");
        var headerText = firstRow?.TextContent ?? "";
        return headerText.Contains("Erg.", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Traegt eine art=2-Seite ueberhaupt eine Paarungstabelle (<c>table.CRs1</c>/<c>CRs2</c>)?
    /// Vor der ersten Auslosung steht dort nur die Ueberschrift „Paarungen/Ergebnisse" ohne
    /// Tabelle — die Rundenzahl aus den Turnierdetails ist dann schon bekannt, Runden gibt es
    /// aber noch keine.
    /// </summary>
    public static async Task<bool> HasPairingsTableAsync(string html)
    {
        var document = await OpenAsync(html);
        return document.QuerySelector("table.CRs1") is not null
            || document.QuerySelector("table.CRs2") is not null;
    }

    private static string NormalizeResult(string result)
    {
        return result.Replace("&frac12;", "½");
    }

    internal static string CleanTeamName(string text)
    {
        return text.Trim().Trim('-', ' ');
    }

    private static decimal? ParseSingleScore(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        // Strip forfeit marker, normalize fractions
        text = text.Replace("F", "").Replace("½", ".5").Replace(",", ".").Trim();
        if (string.IsNullOrWhiteSpace(text)) return null;
        return decimal.TryParse(text, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var val) ? val : null;
    }

    private static void ParseSplitScore(string homeText, string awayText, ParsedTeamPairing pairing)
    {
        pairing.HomeScore = ParseSingleScore(homeText);
        pairing.AwayScore = ParseSingleScore(awayText);
    }

    private static void ParseScore(string? scoreText, ParsedTeamPairing pairing)
    {
        if (string.IsNullOrWhiteSpace(scoreText)) return;

        // Score format: "3½:½" or "3.5:0.5" or "3:1" etc.
        scoreText = scoreText.Replace("½", ".5").Replace(",", ".").Replace("F", "");
        var parts = scoreText.Split(':');
        if (parts.Length == 2)
        {
            pairing.HomeScore = ParseSingleScore(parts[0]);
            pairing.AwayScore = ParseSingleScore(parts[1]);
        }
    }
}
