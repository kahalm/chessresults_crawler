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
    /// </summary>
    public static async Task<List<ParsedPairing>> ParseIndividualPairingsAsync(string html)
    {
        var pairings = new List<ParsedPairing>();
        var document = await OpenAsync(html);

        var table = document.QuerySelector("table.CRs1")
            ?? document.QuerySelector("table.CRs2");
        if (table is null) return pairings;

        var allRows = table.QuerySelectorAll(":scope > tr, :scope > tbody > tr");

        foreach (var row in allRows)
        {
            var cells = row.QuerySelectorAll(":scope > td").ToList();
            if (cells.Count < 10) continue;
            if (row.QuerySelectorAll(":scope > th").Length > 0) continue;

            var boardText = cells[0].TextContent.Trim();
            if (!int.TryParse(boardText, out var board)) continue;

            // cells: Br(0) | Nr(1) | Title(2) | Name(3) | Elo(4) | Pts(5) | Result(6) | Pts(7) | Title(8) | Name(9) | Elo(10) | Nr(11)
            var whiteName = cells[3].TextContent.Trim();
            var blackName = cells[9].TextContent.Trim();
            var result = cells[6].TextContent.Trim().Replace(" ", "");

            int.TryParse(cells[1].TextContent.Trim(), out var whiteSnr);
            int.TryParse(cells.Count > 11 ? cells[11].TextContent.Trim() : "", out var blackSnr);

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
