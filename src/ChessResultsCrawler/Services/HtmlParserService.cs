using System.Globalization;
using AngleSharp;
using AngleSharp.Dom;
using ChessResultsCrawler.Models;
using System.Text.RegularExpressions;

namespace ChessResultsCrawler.Services;

public class HtmlParserService
{
    /// <summary>
    /// Parses art=15 page (player list).
    /// Returns list of parsed players with Snr, Name, Title, FideId, Elo, Country, Team name, BoardNumber.
    /// </summary>
    public async Task<List<ParsedPlayer>> ParsePlayerListAsync(string html)
    {
        var players = new List<ParsedPlayer>();
        var context = BrowsingContext.New(Configuration.Default);
        var document = await context.OpenAsync(req => req.Content(html));

        // Prefer CRs1/CRs2 tables (chess-results data tables), fall back to header search
        var table = document.QuerySelector("table.CRs1")
            ?? document.QuerySelector("table.CRs2")
            ?? FindTableByHeaders(document, ["Nr.", "Name"]);
        if (table is null) return players;

        var headerCells = table.QuerySelectorAll(":scope > tr, :scope > thead > tr, :scope > tbody > tr").FirstOrDefault()
            ?.QuerySelectorAll("th, td")
            .Select((cell, idx) => (Name: cell.TextContent.Trim(), Index: idx))
            .ToList() ?? [];
        var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in headerCells)
        {
            headers.TryAdd(h.Name, h.Index);
        }

        var allRows = table.QuerySelectorAll(":scope > tr, :scope > tbody > tr");
        var rows = allRows.Skip(1);
        foreach (var row in rows)
        {
            var cells = row.QuerySelectorAll(":scope > td").ToList();
            if (cells.Count < 3) continue;

            var snrText = GetCellValue(cells, headers, "Nr.");
            if (!int.TryParse(snrText, out var snr)) continue;

            var player = new ParsedPlayer
            {
                Snr = snr,
                Name = GetCellValue(cells, headers, "Name") ?? "",
                Title = GetCellValue(cells, headers, "Title") ?? GetCellValue(cells, headers, "Ti.") ?? GetCellValue(cells, headers, "Typ"),
                FideId = GetCellValue(cells, headers, "FideID") ?? GetCellValue(cells, headers, "FIDE-ID"),
                Country = GetCellValue(cells, headers, "FED") ?? GetCellValue(cells, headers, "Fed") ?? GetCellValue(cells, headers, "Land"),
                TeamName = GetCellValue(cells, headers, "Team") ?? GetCellValue(cells, headers, "Club/City") ?? GetCellValue(cells, headers, "Verein/Ort"),
            };

            var eloText = GetCellValue(cells, headers, "Rtg") ?? GetCellValue(cells, headers, "Elo");
            if (int.TryParse(eloText, out var elo)) player.Elo = elo;

            var boardText = GetCellValue(cells, headers, "Br.") ?? GetCellValue(cells, headers, "Bo.");
            if (int.TryParse(boardText, out var board)) player.BoardNumber = board;

            if (!string.IsNullOrWhiteSpace(player.Name))
                players.Add(player);
        }

        return players;
    }

    /// <summary>
    /// Parses art=2 page (team pairings / Auslosungen) for a specific round.
    /// Format: Nr | HomeTeam | AwayTeam | HomeScore | : | AwayScore
    /// First row may be a date header (colspan), second row is the column header.
    /// </summary>
    public async Task<List<ParsedTeamPairing>> ParseTeamPairingsAsync(string html)
    {
        var pairings = new List<ParsedTeamPairing>();
        var context = BrowsingContext.New(Configuration.Default);
        var document = await context.OpenAsync(req => req.Content(html));

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
        var home = teamCols[0];
        var away = teamCols[1];
        return (home, away, colon, Math.Max(away, colon + 1));
    }

    /// <summary>
    /// Parses art=2 page for individual (non-team) pairings.
    /// Format: Br | Nr | Title | Name | Elo | Pts | Result | Pts | Title | Name | Elo | Nr | (PGN)
    /// </summary>
    public async Task<List<ParsedPairing>> ParseIndividualPairingsAsync(string html)
    {
        var pairings = new List<ParsedPairing>();
        var context = BrowsingContext.New(Configuration.Default);
        var document = await context.OpenAsync(req => req.Content(html));

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
    public async Task<bool> IsTeamPairingsPageAsync(string html)
    {
        var context = BrowsingContext.New(Configuration.Default);
        var document = await context.OpenAsync(req => req.Content(html));
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
    public async Task<bool> HasPairingsTableAsync(string html)
    {
        var context = BrowsingContext.New(Configuration.Default);
        var document = await context.OpenAsync(req => req.Content(html));
        return document.QuerySelector("table.CRs1") is not null
            || document.QuerySelector("table.CRs2") is not null;
    }

    /// <summary>
    /// Die Zeile „Turnierauswahl" („Tournament selection"): die GRUPPEN derselben Veranstaltung —
    /// „Gruppe A, Gruppe B, Mädchen, <i><b>Schnellschach</b></i>" bei einer Rallye, „Open, Women"
    /// bei der Olympiade. Die anderen stehen als Link auf ihre tnr-Seite, die eigene kursiv-fett ohne
    /// Link (sie bekommt <c>ChessResultsId = null</c>, der Aufrufer kennt sie). Reihenfolge wie auf
    /// der Seite.
    /// </summary>
    internal static List<ParsedTournamentGroup> ParseTournamentSelection(AngleSharp.Dom.IElement cell)
    {
        var groups = new List<ParsedTournamentGroup>();
        foreach (var node in cell.Children)
        {
            var link = node.LocalName == "a" ? node : null;
            if (link is not null)
            {
                var id = Regex.Match(link.GetAttribute("href") ?? "", @"tnr(\d+)\.aspx", RegexOptions.IgnoreCase);
                var text = link.TextContent.Trim();
                if (id.Success && text.Length > 0)
                    groups.Add(new ParsedTournamentGroup { ChessResultsId = id.Groups[1].Value, Label = text });
            }
            else if (node.LocalName is "i" or "b")
            {
                var text = node.TextContent.Trim();
                if (text.Length > 0)
                    groups.Add(new ParsedTournamentGroup { ChessResultsId = null, Label = text, IsCurrent = true });
            }
        }
        // Eine „Auswahl" aus nur dem eigenen Turnier ist keine.
        return groups.Count > 1 && groups.Any(g => g.IsCurrent) ? groups : [];
    }

    private static string NormalizeResult(string result)
    {
        return result.Replace("&frac12;", "½");
    }

    /// <summary>
    /// Parses art=0 page to extract total number of rounds.
    /// Looks for patterns like "nach X Runden" or "after X rounds".
    /// </summary>
    public async Task<int?> ParseTotalRoundsAsync(string html)
    {
        var context = BrowsingContext.New(Configuration.Default);
        var document = await context.OpenAsync(req => req.Content(html));

        // Zuerst die Turnierdetails: „Rundenanzahl | 11" (lan=1: „Number of rounds") ist die
        // GEPLANTE Rundenzahl und steht schon vor der ersten Runde da. Die Texte weiter unten nennen
        // nur gespielte Runden, und ein laufendes Mannschaftsturnier schreibt „Stand nach der
        // 10 Runde" — das trifft keines der Muster, die Olympiade 2026 blieb so bei TotalRounds 0
        // (und RookHub zeigte keine Rundenauswahl).
        foreach (var row in document.QuerySelectorAll("tr"))
        {
            var cells = row.QuerySelectorAll(":scope > td").ToList();
            if (cells.Count < 2) continue;
            var label = cells[0].TextContent.Trim().TrimEnd(':');
            if (!label.Equals("Rundenanzahl", StringComparison.OrdinalIgnoreCase) &&
                !label.Equals("Number of rounds", StringComparison.OrdinalIgnoreCase)) continue;
            if (int.TryParse(cells[1].TextContent.Trim(), out var planned) && planned > 0)
                return planned;
        }

        var text = document.Body?.TextContent ?? "";

        // German: "nach 7 Runden" or "nach 9 Runden"
        var match = Regex.Match(text, @"nach\s+(\d+)\s+Runde", RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var rounds))
            return rounds;

        // English: "after 7 Rounds"
        match = Regex.Match(text, @"after\s+(\d+)\s+[Rr]ound", RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups[1].Value, out rounds))
            return rounds;

        return null;
    }

    /// <summary>
    /// Parses art=2 page to extract available round numbers from navigation links.
    /// Looks for "Rd.1", "Rd.2", etc. links.
    /// <para><paramref name="maxRound"/> (optional, i. d. R. <c>Tournament.TotalRounds</c>) klemmt das
    /// Ergebnis: Runden &lt; 1 oder &gt; maxRound werden verworfen — sonst erzeugen beliebige
    /// <c>rd=</c>-Links (z. B. aus fremden Navigations-/Werbe-Hrefs) Phantom-Runden.</para>
    /// </summary>
    public async Task<List<int>> ParseAvailableRoundsAsync(string html, int? maxRound = null)
    {
        var roundNumbers = new List<int>();
        var context = BrowsingContext.New(Configuration.Default);
        var document = await context.OpenAsync(req => req.Content(html));

        // Look for links or text like "Rd.1", "Rd.2", "Rd. 1" etc.
        var links = document.QuerySelectorAll("a");
        foreach (var link in links)
        {
            var text = link.TextContent.Trim();
            var rdMatch = Regex.Match(text, @"Rd\.?\s*(\d+)", RegexOptions.IgnoreCase);
            if (rdMatch.Success && int.TryParse(rdMatch.Groups[1].Value, out var rd))
            {
                if (!roundNumbers.Contains(rd))
                    roundNumbers.Add(rd);
            }
        }

        // Also check for rd= in hrefs
        var allLinks = document.QuerySelectorAll("a[href]");
        foreach (var link in allLinks)
        {
            var href = link.GetAttribute("href") ?? "";
            var rdMatch = Regex.Match(href, @"rd=(\d+)", RegexOptions.IgnoreCase);
            if (rdMatch.Success && int.TryParse(rdMatch.Groups[1].Value, out var rd))
            {
                if (!roundNumbers.Contains(rd))
                    roundNumbers.Add(rd);
            }
        }

        // Phantom-Runden aus beliebigen rd=-Links abwehren: gültig sind nur 1..maxRound
        // (maxRound==null/≤0 ⇒ keine Obergrenze, aber weiterhin rd≥1).
        var clamped = roundNumbers.Where(r => r >= 1 && (maxRound is not > 0 || r <= maxRound)).ToList();
        clamped.Sort();
        return clamped;
    }

    /// <summary>
    /// Parses the tournament name from any chess-results page.
    /// </summary>
    public async Task<string?> ParseTournamentNameAsync(string html)
    {
        var context = BrowsingContext.New(Configuration.Default);
        var document = await context.OpenAsync(req => req.Content(html));

        // Tournament name is typically in a large header div
        var header = document.QuerySelector("div.defaultDialog h2")
            ?? document.QuerySelector("h2")
            ?? document.QuerySelector(".ContentTable h2");

        return header?.TextContent.Trim();
    }

    /// <summary>
    /// Parses turdet=YES page to extract tournament date and location.
    /// Looks for table rows where the first cell is "Date"/"Datum" or "Location"/"Ort".
    /// </summary>
    public async Task<ParsedTournamentDetails> ParseTournamentDetailsAsync(string html)
    {
        var details = new ParsedTournamentDetails();
        var context = BrowsingContext.New(Configuration.Default);
        var document = await context.OpenAsync(req => req.Content(html));

        var rows = document.QuerySelectorAll("table tr");
        foreach (var row in rows)
        {
            var cells = row.QuerySelectorAll("td").ToList();
            if (cells.Count < 2) continue;

            var label = cells[0].TextContent.Trim().TrimEnd(':');
            var value = cells[1].TextContent.Trim();

            if (string.IsNullOrWhiteSpace(value)) continue;

            if (label.Equals("Turnierauswahl", StringComparison.OrdinalIgnoreCase) ||
                label.Equals("Tournament selection", StringComparison.OrdinalIgnoreCase))
            {
                details.Groups = ParseTournamentSelection(cells[1]);
            }
            else if (label.Equals("Date", StringComparison.OrdinalIgnoreCase) ||
                label.Equals("Datum", StringComparison.OrdinalIgnoreCase))
            {
                details.DateText = value;
            }
            else if (label.Equals("Location", StringComparison.OrdinalIgnoreCase) ||
                     label.Equals("Ort", StringComparison.OrdinalIgnoreCase))
            {
                details.Location = value;
            }
            // Die Bedenkzeit steht als „Time control (Standard)" bzw. „Bedenkzeit (Blitz)" da —
            // die KLASSE nennt chess-results also selbst, in Klammern hinter der Beschriftung.
            // Der Wert daneben ist Freitext („90 Min. / 40 Zuege + 30 Min. + 30 Sekunden ab Zug 1").
            // Deshalb `StartsWith` statt `Equals`: mit dem exakten Vergleich fand der Parser das
            // Feld nie.
            else if (label.StartsWith("Bedenkzeit", StringComparison.OrdinalIgnoreCase) ||
                     label.StartsWith("Zeitkontrolle", StringComparison.OrdinalIgnoreCase) ||
                     label.StartsWith("Time control", StringComparison.OrdinalIgnoreCase) ||
                     label.StartsWith("Rate of play", StringComparison.OrdinalIgnoreCase))
            {
                details.TimeControl = value;
                var kind = Regex.Match(label, @"\(([^)]+)\)");
                if (kind.Success) details.TimeControlKind = kind.Groups[1].Value.Trim();
            }
        }

        return details;
    }

    /// <summary>
    /// Parses the SpielerSuche.aspx player search results page.
    /// Returns list of players with Name, FideId, ChessResultsId (Ident-Number), Elo, Country, Title.
    /// </summary>
    public async Task<List<ParsedPlayerSearchResult>> ParsePlayerSearchAsync(string html)
    {
        var results = new List<ParsedPlayerSearchResult>();
        var context = BrowsingContext.New(Configuration.Default);
        var document = await context.OpenAsync(req => req.Content(html));

        var table = document.QuerySelector("table.CRs1")
            ?? document.QuerySelector("table.CRs2")
            ?? FindTableByHeaders(document, ["Name"]);
        if (table is null) return results;

        var headerCells = table.QuerySelectorAll(":scope > tr, :scope > thead > tr, :scope > tbody > tr").FirstOrDefault()
            ?.QuerySelectorAll("th, td")
            .Select((cell, idx) => (Name: cell.TextContent.Trim(), Index: idx))
            .ToList() ?? [];
        var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in headerCells)
        {
            headers.TryAdd(h.Name, h.Index);
        }

        var allRows = table.QuerySelectorAll(":scope > tr, :scope > tbody > tr");
        var rows = allRows.Skip(1);
        foreach (var row in rows)
        {
            var cells = row.QuerySelectorAll(":scope > td").ToList();
            if (cells.Count < 2) continue;

            var name = GetCellValue(cells, headers, "Name");
            if (string.IsNullOrWhiteSpace(name)) continue;

            var result = new ParsedPlayerSearchResult
            {
                Name = name,
                Title = GetCellValue(cells, headers, "Title") ?? GetCellValue(cells, headers, "Ti.") ?? GetCellValue(cells, headers, "Typ"),
                FideId = GetCellValue(cells, headers, "FideID") ?? GetCellValue(cells, headers, "FIDE-ID") ?? GetCellValue(cells, headers, "Fide-ID"),
                Country = GetCellValue(cells, headers, "FED") ?? GetCellValue(cells, headers, "Fed") ?? GetCellValue(cells, headers, "Land"),
                ChessResultsId = GetCellValue(cells, headers, "Ident-Number") ?? GetCellValue(cells, headers, "Ident-Nummer") ?? GetCellValue(cells, headers, "Ident")
            };

            var eloText = GetCellValue(cells, headers, "Rtg") ?? GetCellValue(cells, headers, "Elo");
            if (int.TryParse(eloText, out var elo)) result.Elo = elo;

            results.Add(result);
        }

        return results;
    }

    /// <summary>
    /// Parses the SpielerSuche.aspx player search results to extract tournament participations.
    /// Returns list of tournaments with TournamentId (from tnrXXX links), TournamentName, and EndDate.
    /// </summary>
    public async Task<List<ParsedPlayerTournament>> ParsePlayerTournamentsAsync(string html)
    {
        var results = new List<ParsedPlayerTournament>();
        var context = BrowsingContext.New(Configuration.Default);
        var document = await context.OpenAsync(req => req.Content(html));

        var table = document.QuerySelector("table.CRs1")
            ?? document.QuerySelector("table.CRs2")
            ?? FindTableByHeaders(document, ["Name"]);
        if (table is null) return results;

        var headerCells = table.QuerySelectorAll(":scope > tr, :scope > thead > tr, :scope > tbody > tr").FirstOrDefault()
            ?.QuerySelectorAll("th, td")
            .Select((cell, idx) => (Name: cell.TextContent.Trim(), Index: idx))
            .ToList() ?? [];
        var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in headerCells)
        {
            headers.TryAdd(h.Name, h.Index);
        }

        // Find the tournament name column index
        int tournamentColIdx = -1;
        if (headers.TryGetValue("Turnierbezeichnung", out var tbIdx)) tournamentColIdx = tbIdx;
        else if (headers.TryGetValue("Tournament", out var tIdx)) tournamentColIdx = tIdx;

        // Find the end date column index
        int endDateColIdx = -1;
        if (headers.TryGetValue("Ende-Datum", out var edIdx)) endDateColIdx = edIdx;
        else if (headers.TryGetValue("End-Date", out var edIdx2)) endDateColIdx = edIdx2;

        if (tournamentColIdx < 0) return results;

        var allRows = table.QuerySelectorAll(":scope > tr, :scope > tbody > tr");
        var rows = allRows.Skip(1);
        foreach (var row in rows)
        {
            var cells = row.QuerySelectorAll(":scope > td").ToList();
            if (cells.Count <= tournamentColIdx) continue;

            var tournamentCell = cells[tournamentColIdx];
            var link = tournamentCell.QuerySelector("a[href]");
            if (link is null) continue;

            var href = link.GetAttribute("href") ?? "";
            var tnrMatch = Regex.Match(href, @"tnr(\d+)");
            if (!tnrMatch.Success) continue;

            var tournamentId = tnrMatch.Groups[1].Value;
            var tournamentName = link.TextContent.Trim();
            if (string.IsNullOrWhiteSpace(tournamentName)) continue;

            string? endDate = null;
            if (endDateColIdx >= 0 && endDateColIdx < cells.Count)
            {
                var dateText = cells[endDateColIdx].TextContent.Trim();
                if (!string.IsNullOrWhiteSpace(dateText))
                    endDate = dateText;
            }

            var entry = new ParsedPlayerTournament
            {
                TournamentId = tournamentId,
                TournamentName = tournamentName,
                EndDate = endDate,
            };

            // Die Startnummer steht NUR im Link auf den Spielernamen
            // (tnr<id>.aspx?lan=1&art=9&snr=<n>) - es gibt keine Spalte dafuer. Ohne sie ist die
            // Spielerkarte dieses Turniers nicht erreichbar, und mit ihr genau EIN Abruf.
            var cardLink = row.QuerySelectorAll("a[href]")
                .Select(a => Regex.Match(a.GetAttribute("href") ?? "", @"tnr(\d+)\.aspx[^""]*[?&]snr=(\d+)"))
                .FirstOrDefault(m => m.Success);
            if (cardLink is { Success: true } && int.TryParse(cardLink.Groups[2].Value, out var snr))
                entry.Snr = snr;

            entry.IdentNumber = GetCellValue(cells, headers, "ID") ?? GetCellValue(cells, headers, "Ident-Number");
            entry.FideId = GetCellValue(cells, headers, "FideID") ?? GetCellValue(cells, headers, "Fide-ID");
            entry.Club = GetCellValue(cells, headers, "Club/City") ?? GetCellValue(cells, headers, "Verein/Ort");
            entry.Federation = GetCellValue(cells, headers, "FED") ?? GetCellValue(cells, headers, "Land");
            entry.PlayerName = GetCellValue(cells, headers, "Name");

            // "-" heisst: noch nicht gespielt. Das ist die Unterscheidung zwischen einem kuenftigen
            // und einem abgeschlossenen Turnier - ein Kartenabruf lohnt nur beim zweiten.
            if (int.TryParse(GetCellValue(cells, headers, "Rk.") ?? GetCellValue(cells, headers, "Rg."), out var rank))
                entry.Rank = rank;
            if (int.TryParse(GetCellValue(cells, headers, "Rd."), out var rounds)) entry.Rounds = rounds;
            if (int.TryParse(GetCellValue(cells, headers, "n"), out var count)) entry.PlayerCount = count;

            results.Add(entry);
        }

        // Deduplicate by TournamentId
        return results
            .GroupBy(r => r.TournamentId)
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>
    /// Parses art=9 page (player detail / Einzelergebnisse).
    /// Columns: Rd. | Br. | Snr | Name | Elo | Land | Verein/Ort | Pkt. | Erg.
    /// Returns list of parsed results per round.
    ///
    /// <para>Die Tabelle wird ueber ihre KOPFZEILE gesucht, nicht ueber die Klasse. Auf der
    /// art=9-Seite gibt es ZWEI Tabellen mit der Klasse CRs1, und die erste ist der
    /// zweispaltige „Player info"-Block. Die frueher zuerst versuchte Klassen-Auswahl griff
    /// also den falschen Block; dessen Zeilen haben zwei Zellen, fielen durch die
    /// Mindestzellen-Pruefung und die Rundenliste kam LEER zurueck.</para>
    /// </summary>
    public async Task<List<ParsedPlayerResult>> ParsePlayerDetailPageAsync(string html)
    {
        var results = new List<ParsedPlayerResult>();
        var context = BrowsingContext.New(Configuration.Default);
        var document = await context.OpenAsync(req => req.Content(html));

        var table = FindTableByHeaders(document, ["Rd.", "Name"])
            ?? FindTableByHeaders(document, ["Rd.", "Erg."])
            ?? document.QuerySelector("table.CRs1")
            ?? document.QuerySelector("table.CRs2");
        if (table is null) return results;

        var headerCells = table.QuerySelectorAll(":scope > tr, :scope > thead > tr, :scope > tbody > tr").FirstOrDefault()
            ?.QuerySelectorAll("th, td")
            .Select((cell, idx) => (Name: cell.TextContent.Trim(), Index: idx))
            .ToList() ?? [];
        var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in headerCells)
        {
            headers.TryAdd(h.Name, h.Index);
        }

        var allRows = table.QuerySelectorAll(":scope > tr, :scope > tbody > tr");
        var rows = allRows.Skip(1);
        foreach (var row in rows)
        {
            var cells = row.QuerySelectorAll(":scope > td").ToList();
            if (cells.Count < 3) continue;

            var rdText = GetCellValue(cells, headers, "Rd.");
            if (!int.TryParse(rdText, out var roundNumber)) continue;

            var result = new ParsedPlayerResult { RoundNumber = roundNumber };

            var boardText = GetCellValue(cells, headers, "Br.") ?? GetCellValue(cells, headers, "Bo.");
            if (int.TryParse(boardText, out var board)) result.BoardNumber = board;

            var snrText = GetCellValue(cells, headers, "SNr") ?? GetCellValue(cells, headers, "SNo");
            if (int.TryParse(snrText, out var snr)) result.OpponentSnr = snr;

            result.OpponentName = GetCellValue(cells, headers, "Name");

            // „RtgI"/„RtgN" sind die heutigen Spaltennamen (international/national); „Rtg"/„Elo"
            // bleiben als aeltere Varianten stehen. Die INTERNATIONALE Wertung zuerst — sie ist
            // die, mit der gerechnet wird, und bei Spielern ohne nationale Wertung die einzige.
            var eloText = GetCellValue(cells, headers, "RtgI")
                          ?? GetCellValue(cells, headers, "Rtg")
                          ?? GetCellValue(cells, headers, "Elo")
                          ?? GetCellValue(cells, headers, "RtgN");
            if (int.TryParse(eloText, out var elo)) result.OpponentElo = elo;

            result.Points = GetCellValue(cells, headers, "Pkt.") ?? GetCellValue(cells, headers, "Pts.");
            result.Result = GetCellValue(cells, headers, "Erg.") ?? GetCellValue(cells, headers, "Res.");

            results.Add(result);
        }

        return results;
    }

    /// <summary>
    /// Der RUNDENPLAN eines Turniers (art=14): je Runde Nummer, Datum und Uhrzeit.
    ///
    /// <para>Warum das gebraucht wird: Start- und Enddatum sagen bei einer Liga NICHT, wann
    /// gespielt wird. „2026-09-26 bis 2027-04-17" sind elf Runden mit zwei bis fuenf Wochen
    /// Abstand — im Kalender stand die Liga damit an rund 200 Tagen, an denen nichts
    /// stattfindet, und verdeckte die Turniere, die es wirklich gibt.</para>
    ///
    /// <para>Die Tabelle wird ueber ihre Kopfzeile gesucht („Round"/„Runde" + „Date"/„Datum"),
    /// nicht ueber die Klasse — dieselbe Lehre wie bei der Spielerkarte. Eine leere Liste heisst
    /// „kein Rundenplan hinterlegt"; das ist bei vielen Turnieren der Normalfall.</para>
    /// </summary>
    public async Task<List<ParsedRoundDate>> ParseRoundPlanAsync(string html)
    {
        var results = new List<ParsedRoundDate>();
        var context = BrowsingContext.New(Configuration.Default);
        var document = await context.OpenAsync(req => req.Content(html));

        var table = FindTableByHeaders(document, ["Round", "Date"])
                    ?? FindTableByHeaders(document, ["Runde", "Datum"]);
        if (table is null) return results;

        var headerCells = table.QuerySelectorAll(":scope > tr, :scope > thead > tr, :scope > tbody > tr")
            .FirstOrDefault()
            ?.QuerySelectorAll("th, td")
            .Select((cell, idx) => (Name: cell.TextContent.Trim(), Index: idx))
            .ToList() ?? [];
        var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in headerCells) headers.TryAdd(h.Name, h.Index);

        foreach (var row in table.QuerySelectorAll(":scope > tr, :scope > tbody > tr").Skip(1))
        {
            var cells = row.QuerySelectorAll(":scope > td").ToList();
            if (cells.Count < 2) continue;

            if (!int.TryParse(GetCellValue(cells, headers, "Round") ?? GetCellValue(cells, headers, "Runde"),
                    out var number)) continue;

            var date = ParseDate(GetCellValue(cells, headers, "Date") ?? GetCellValue(cells, headers, "Datum"));
            if (date is null) continue;

            results.Add(new ParsedRoundDate
            {
                Number = number,
                Date = date.Value,
                // „14:00 Uhr" — die Einheit steht mit drin und bleibt Rohtext: fuer den Kalender
                // zaehlt der TAG, und eine halb geparste Uhrzeit waere nur eine Fehlerquelle.
                TimeText = GetCellValue(cells, headers, "Time") ?? GetCellValue(cells, headers, "Zeit"),
            });
        }

        return results;
    }

    /// <summary>
    /// Ein Datum der Seite. Bei lan=1 kommt „yyyy/MM/dd", bei lan=0 „dd.MM.yyyy" — beide Formate
    /// werden gelesen, damit ein Sprachwechsel den Rundenplan nicht still leer laeuft.
    /// </summary>
    private static DateOnly? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string[] formats = ["yyyy/MM/dd", "yyyy-MM-dd", "dd.MM.yyyy", "d.M.yyyy"];
        return DateOnly.TryParseExact(text.Trim(), formats, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date) ? date : null;
    }

    /// <summary>
    /// Der „Player info"-Block der Spielerkarte (art=9): Punkte, Platz, Performance-Rating und
    /// Elo-Aenderung eines Spielers in EINEM Turnier.
    ///
    /// <para>Das ist die einzige Quelle fuer diese vier Werte — die Spielersuche nennt nur den
    /// Platz, und die Turnierseite selbst hat sie je Spieler nur in der Tabelle, nicht als
    /// abfragbares Feld. Ein Abruf je Turnier und Spieler.</para>
    ///
    /// <para>Ein KUENFTIGES Turnier hat den Block, aber ohne Werte. Deshalb
    /// <see cref="ParsedPlayerCard.HasResult"/>: fehlen Punkte UND Platz, gab es noch kein
    /// Ergebnis — das ist der Normalfall vor dem Turnier und kein Fehler.</para>
    ///
    /// <para>Die Zahlen tragen ein Dezimal-KOMMA („1,5", „-51,6"), auch auf der englischen
    /// Seite (lan=1). Deshalb wird mit der invarianten UND der deutschen Kultur geparst.</para>
    /// </summary>
    public async Task<ParsedPlayerCard?> ParsePlayerCardAsync(string html)
    {
        var context = BrowsingContext.New(Configuration.Default);
        var document = await context.OpenAsync(req => req.Content(html));

        // Der Block wird ueber seine ZEILEN erkannt (Beschriftung + Wert), nicht ueber die
        // Klasse: „Player info" ist eine Ueberschrift daneben, und die Klasse CRs1 tragen auf
        // dieser Seite mehrere Tabellen.
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in document.QuerySelectorAll("table"))
        {
            foreach (var row in table.QuerySelectorAll(":scope > tr, :scope > tbody > tr"))
            {
                var cells = row.QuerySelectorAll(":scope > td").ToList();
                if (cells.Count != 2) continue;
                var label = cells[0].TextContent.Trim().TrimEnd(':').Trim();
                if (label.Length == 0) continue;
                fields.TryAdd(label, cells[1].TextContent.Trim());
            }
        }

        if (fields.Count == 0) return null;

        var card = new ParsedPlayerCard
        {
            Name = Field(fields, "Name", "Namen"),
            Federation = Field(fields, "Federation", "Foederation", "Land"),
            Club = Field(fields, "Club/City", "Verein/Ort"),
            IdentNumber = Field(fields, "Ident-Number", "Ident-Nummer"),
            FideId = Field(fields, "Fide-ID", "FideID"),
            StartingRank = Int(fields, "Starting rank", "Startrang"),
            RatingNational = Int(fields, "Rating national", "Rating national"),
            RatingInternational = Int(fields, "Rating international", "Rating international"),
            PerformanceRating = Int(fields, "Performance rating", "Rating-Performance", "Performance"),
            Rank = Int(fields, "Rank", "Rang", "Platz"),
            YearOfBirth = Int(fields, "Year of birth", "Geburtsjahr"),
            Points = Decimal(fields, "Points", "Punkte"),
            RatingChange = Decimal(fields, "FIDE rtg +/-", "Rtg +/-", "Elo +/-"),
        };

        // Ohne Punkte und ohne Platz ist es ein Turnier, das noch nicht gespielt wurde.
        card.HasResult = card.Points is not null || card.Rank is not null;
        card.GamesPlayed = CountGames(document);
        return card;
    }

    private static string? Field(Dictionary<string, string> fields, params string[] labels)
    {
        foreach (var label in labels)
        {
            if (fields.TryGetValue(label, out var value) && value.Length > 0) return value;
        }
        return null;
    }

    private static int? Int(Dictionary<string, string> fields, params string[] labels) =>
        int.TryParse(Field(fields, labels), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value : null;

    /// <summary>
    /// Eine Kommazahl der Seite. chess-results schreibt „1,5" und „-51,6" — auch auf der
    /// englischen Fassung (lan=1).
    ///
    /// <para><b>Die Reihenfolge und der Stil sind beides wesentlich.</b> Mit
    /// <c>NumberStyles.Number</c> ist das Tausendertrennzeichen erlaubt, und in der invarianten
    /// Kultur IST das Komma genau das: „1,5" wird dort erfolgreich als <b>15</b> gelesen, „-51,6"
    /// als <b>-516</b>. Ein erfolgreicher Fehlwert also, den kein Fallback mehr korrigiert.
    /// Deshalb ohne <c>AllowThousands</c> und mit dem Dezimal-KOMMA zuerst: „1,5" scheitert dann
    /// invariant und gelingt deutsch, „1.5" umgekehrt.</para>
    /// </summary>
    private static decimal? Decimal(Dictionary<string, string> fields, params string[] labels)
    {
        var text = Field(fields, labels);
        if (string.IsNullOrWhiteSpace(text)) return null;

        const NumberStyles style = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint
                                                                 | NumberStyles.AllowLeadingWhite
                                                                 | NumberStyles.AllowTrailingWhite;

        return decimal.TryParse(text, style, GermanCulture, out var german)
            ? german
            : decimal.TryParse(text, style, CultureInfo.InvariantCulture, out var invariant) ? invariant : null;
    }

    private static readonly CultureInfo GermanCulture = new("de-DE");

    /// <summary>
    /// Extracts the SNode (s1/s2/s3) from a redirect URL or page content.
    /// </summary>
    public static string? ExtractSNode(string url)
    {
        var match = Regex.Match(url, @"chess-results\.com/(s\d+)/");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Die Tabelle, deren erste Zeile die verlangten Kopfnamen traegt.
    ///
    /// <para><b>Bevorzugt wird eine Tabelle OHNE verschachtelte Tabelle</b> — und das ist der
    /// ganze Witz dieser Methode. chess-results baut sein Layout aus Tabellen: die Datentabelle
    /// steckt mehrere Ebenen tief (beim Rundenplan drei). <c>TextContent</c> ist REKURSIV, also
    /// „enthaelt" schon die aeusserste Wrapper-Tabelle jeden Kopfnamen, der irgendwo darin
    /// vorkommt. Ohne diese Bevorzugung kam die WRAPPER-Tabelle zurueck, deren eigene Zeilen
    /// keine Datenzeilen sind — das Ergebnis war eine leere Liste, ohne Fehler und ohne Hinweis.
    /// Auf dem Dev-Stand gemessen: 337 geprueften Turnieren standen 0 Spieltermine gegenueber.
    /// Eine Datentabelle ist immer ein BLATT.</para>
    ///
    /// <para>Findet sich kein Blatt, gilt der erste Treffer wie bisher — besser die Wrapper-
    /// Tabelle als gar nichts, falls eine Seite ihre Daten doch verschachtelt fuehrt.</para>
    /// </summary>
    /// <summary>
    /// Wie viele Partien der Spieler in diesem Turnier WIRKLICH gespielt hat.
    ///
    /// <para>Die Rundenzahl des Turniers taugt dafuer nicht: in einer Liga steht ein Spieler an
    /// Brett 17 und wird an drei von elf Terminen aufgestellt. Gezaehlt werden deshalb die Zeilen
    /// der Partien-Tabelle unter dem Player-info-Block — und zwar nur die mit einem GEGNER: eine
    /// Zeile ohne Namen ist ein Freilos oder eine nicht gespielte Runde und keine Partie.</para>
    ///
    /// <para>Bewusst NICHT ueber Spaltenindizes: die Datenzeilen haben eine Zelle weniger als die
    /// Kopfzeile (eine leere Spalte fehlt dort), jeder feste Index zeigt also auf die falsche
    /// Spalte. Eine Zeile zaehlt, wenn sie mit einer Rundennummer beginnt und irgendwo einen
    /// Namen traegt.</para>
    /// </summary>
    private static int? CountGames(IDocument document)
    {
        var table = FindTableByHeaders(document, ["Rd.", "Name"])
            ?? FindTableByHeaders(document, ["Rd", "Name"])
            ?? FindTableByHeaders(document, ["Ru.", "Name"]);
        if (table is null) return null;

        var games = 0;
        foreach (var row in table.QuerySelectorAll("tr").Skip(1))
        {
            var cells = row.QuerySelectorAll("td, th").Select(c => c.TextContent.Trim()).ToList();
            if (cells.Count < 3) continue;
            if (!int.TryParse(cells[0], out var round) || round < 1) continue;

            // Ein Name hat Buchstaben; Zahlen, Striche und Wertungen zaehlen nicht.
            if (cells.Skip(1).Any(c => c.Length >= 3 && c.Any(char.IsLetter))) games++;
        }
        return games;
    }

    private static IElement? FindTableByHeaders(IDocument document, string[] requiredHeaders)
    {
        IElement? fallback = null;

        foreach (var table in document.QuerySelectorAll("table"))
        {
            var firstRow = table.QuerySelector("tr");
            if (firstRow is null) continue;

            var headerTexts = firstRow.QuerySelectorAll("th, td")
                .Select(c => c.TextContent.Trim())
                .ToList();

            var matches = requiredHeaders.All(h =>
                headerTexts.Any(ht => ht.Contains(h, StringComparison.OrdinalIgnoreCase)));
            if (!matches) continue;

            if (table.QuerySelector("table") is null) return table;    // Blatt = Datentabelle
            fallback ??= table;
        }

        return fallback;
    }

    private static string? GetCellValue(List<IElement> cells, Dictionary<string, int> headers, string headerName)
    {
        if (headers.TryGetValue(headerName, out var idx) && idx < cells.Count)
        {
            var val = cells[idx].TextContent.Trim();
            return string.IsNullOrWhiteSpace(val) ? null : val;
        }
        return null;
    }

    private static string CleanTeamName(string text)
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

    /// <summary>
    /// Parst die Trefferliste der Turniersuche (TurnierSuche.aspx).
    /// Zieltabelle ist die innere table.CRs2 in #datenxx; deren Kopfzeile mischt th und td.
    /// Spalten werden - wie ueberall in dieser Datei - ueber den Kopfzeilen-NAMEN aufgeloest, mit
    /// deutschen Synonymen: eine Server-Node, die den lan-Parameter ignoriert, wuerde sonst still
    /// eine leere Liste liefern statt aufzufallen.
    /// </summary>
    /// <summary>
    /// Die Vereins-/Mannschaftsnamen der Startrangliste einer Mannschaftsveranstaltung.
    ///
    /// <para>Zweck ist NICHT die Turnierauswertung, sondern die Verortung: chess-results nennt den
    /// Spielort als Freitext und oft abgekuerzt („Mayrhofen, St.Veit"), und derselbe Ortsname
    /// existiert mehrfach — „St. Veit" liegt in Tirol UND (als „St. Veit an der Glan") in
    /// Kaernten. Die Vereinsnamen tragen die Unterscheidung dagegen mit: „SV - Das Wien -
    /// St.Veit/Glan". Sie sind damit ein Hinweis, den es sonst nirgends gibt, und kosten keinen
    /// eigenen Seitenabruf: die Startrangliste steht auf der Turnierseite selbst.</para>
    ///
    /// <para>Leere Liste heisst „keine Mannschaftsveranstaltung oder keine Tabelle gefunden" —
    /// das ist kein Fehler, die meisten Turniere sind Einzelturniere.</para>
    /// </summary>
    public async Task<List<string>> ParseTeamNamesAsync(string html)
    {
        var names = new List<string>();
        var context = BrowsingContext.New(Configuration.Default);
        var document = await context.OpenAsync(req => req.Content(html));

        var table = FindTableByHeaders(document, ["Team"])
            ?? FindTableByHeaders(document, ["Mannschaft"]);
        if (table is null) return names;

        var headerCells = table.QuerySelectorAll(":scope > tr, :scope > thead > tr, :scope > tbody > tr").FirstOrDefault()
            ?.QuerySelectorAll("th, td")
            .Select((cell, idx) => (Name: cell.TextContent.Trim(), Index: idx))
            .ToList() ?? [];
        var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in headerCells)
        {
            headers.TryAdd(h.Name, h.Index);
        }

        foreach (var row in table.QuerySelectorAll(":scope > tr, :scope > tbody > tr").Skip(1))
        {
            var cells = row.QuerySelectorAll("td").ToList();
            if (cells.Count == 0) continue;

            var name = GetCellValue(cells, headers, "Team") ?? GetCellValue(cells, headers, "Mannschaft");
            if (string.IsNullOrWhiteSpace(name)) continue;

            var cleaned = CleanTeamName(name);
            if (cleaned.Length > 0 && !names.Contains(cleaned, StringComparer.OrdinalIgnoreCase))
                names.Add(cleaned);
        }
        return names;
    }

    /// <summary>
    /// Den Ankuendigungskalender auseinandernehmen.
    ///
    /// <para>Die Tabelle <c>#datenxx</c> mischt zwei Zeilenarten: MONATSUEBERSCHRIFTEN
    /// („Start-Date | End-Date | Feb. 2026 | FED | URL | DOC", sechs Zellen) und Datenzeilen mit
    /// ACHT Zellen — Wochentag, Beginn, Wochentag, Ende, Name, Foederation, Verweis,
    /// Ausschreibung. Unterschieden wird nicht ueber die Zellenzahl allein, sondern darueber, ob
    /// in der zweiten Zelle ein Datum im Format <c>yyyy/MM/dd</c> steht: die Kopfzeilen wiederholen
    /// sich je Monat, und eine kuenftige Layout-Aenderung soll nicht stillschweigend Kopfzeilen
    /// als Turniere einlesen.</para>
    /// </summary>
    public async Task<List<ParsedCalendarEntry>> ParseCalendarAsync(string html)
    {
        var results = new List<ParsedCalendarEntry>();
        var context = BrowsingContext.New(Configuration.Default);
        var document = await context.OpenAsync(req => req.Content(html));

        var table = document.QuerySelector("#datenxx") ?? document.QuerySelector("table.CRs2");
        if (table is null) return results;

        foreach (var row in table.QuerySelectorAll("tr"))
        {
            var cells = row.QuerySelectorAll("td, th");
            if (cells.Length != 8) continue;

            var start = ParseCalendarDate(cells[1].TextContent);
            if (start is null) continue;   // Monatsueberschrift oder Fussnote

            var links = row.QuerySelectorAll("a")
                .Select(a => a.GetAttribute("href") ?? "")
                .Where(h => h.Length > 0)
                .ToList();

            results.Add(new ParsedCalendarEntry
            {
                StartDate = start,
                EndDate = ParseCalendarDate(cells[3].TextContent) ?? start,
                Name = Collapse(cells[4].TextContent),
                Federation = Collapse(cells[5].TextContent) is { Length: 3 } fed ? fed : null,
                CalendarId = links.Select(CalendarIdFrom).FirstOrDefault(id => id is not null),
                Url = links.FirstOrDefault(h => h.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                                                && !h.Contains("chess-results", StringComparison.OrdinalIgnoreCase)),
                ChessResultsId = links.Select(TournamentIdFrom).FirstOrDefault(id => id is not null),
            });
        }
        return results;
    }

    /// <summary>Der Kalender schreibt <c>yyyy/MM/dd</c> — anders als die Trefferliste der Suche.</summary>
    private static DateOnly? ParseCalendarDate(string? text) =>
        DateOnly.TryParseExact((text ?? "").Trim(), "yyyy/MM/dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static readonly Regex CalendarIdPattern =
        new(@"key6=DL&(?:amp;)?id=(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string? CalendarIdFrom(string href)
    {
        var m = CalendarIdPattern.Match(href);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static readonly Regex CalendarTnrPattern =
        new(@"chess-results\.com/[Tt]nr(\d+)\.aspx", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string? TournamentIdFrom(string href)
    {
        var m = CalendarTnrPattern.Match(href);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Mehrfache Leerzeichen und Umbrueche aus dem Markup zusammenziehen.</summary>
    private static string Collapse(string? text) =>
        Regex.Replace(text ?? "", @"\s+", " ").Trim();

    public async Task<List<ParsedDirectoryTournament>> ParseTournamentSearchAsync(string html)
    {
        var results = new List<ParsedDirectoryTournament>();
        var context = BrowsingContext.New(Configuration.Default);
        var document = await context.OpenAsync(req => req.Content(html));

        var table = document.QuerySelector("#datenxx table.CRs2")
            ?? document.QuerySelector("table.CRs2")
            ?? document.QuerySelector("table.CRs1")
            ?? FindTableByHeaders(document, ["dbkey"]);
        if (table is null) return results;

        var headerCells = table.QuerySelectorAll(":scope > tr, :scope > thead > tr, :scope > tbody > tr").FirstOrDefault()
            ?.QuerySelectorAll("th, td")
            .Select((cell, idx) => (Name: cell.TextContent.Trim(), Index: idx))
            .ToList() ?? [];
        var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in headerCells)
        {
            headers.TryAdd(h.Name, h.Index);
        }
        // Ohne dbkey-Spalte ist es nicht die Trefferliste (z.B. Fehlerseite, Cloudflare-Interstitial).
        // Lieber leer zurueckgeben als aus einer fremden Tabelle Muell zu ziehen.
        if (!headers.ContainsKey("dbkey")) return results;

        var allRows = table.QuerySelectorAll(":scope > tr, :scope > tbody > tr");
        foreach (var row in allRows.Skip(1))
        {
            var cells = row.QuerySelectorAll(":scope > td").ToList();
            if (cells.Count < 3) continue;

            var dbKey = GetCellValue(cells, headers, "dbkey");
            if (dbKey is null || !Regex.IsMatch(dbKey, @"^\d{1,10}$")) continue;

            var name = GetCellValue(cells, headers, "Tournament") ?? GetCellValue(cells, headers, "Turnierbezeichnung");
            if (name is null) continue;

            var entry = new ParsedDirectoryTournament
            {
                ChessResultsId = dbKey,
                Name = name,
                Federation = GetCellValue(cells, headers, "FED") ?? GetCellValue(cells, headers, "Land"),
                State = GetCellValue(cells, headers, "State") ?? GetCellValue(cells, headers, "Bundesland"),
                LocationText = GetCellValue(cells, headers, "Location") ?? GetCellValue(cells, headers, "Ort"),
                TimeControlText = GetCellValue(cells, headers, "Time control") ?? GetCellValue(cells, headers, "Bedenkzeit"),
                Director = GetCellValue(cells, headers, "Tournament director") ?? GetCellValue(cells, headers, "Turnierdirektor"),
                Organizer = GetCellValue(cells, headers, "Organizer(s)") ?? GetCellValue(cells, headers, "Veranstalter"),
                ChiefArbiter = GetCellValue(cells, headers, "Chief Arbiter") ?? GetCellValue(cells, headers, "Hauptschiedsrichter"),
                StartDate = ParseSearchDate(GetCellValue(cells, headers, "from") ?? GetCellValue(cells, headers, "Von")),
                EndDate = ParseSearchDate(GetCellValue(cells, headers, "to") ?? GetCellValue(cells, headers, "Bis")),
                LastUpdateText = GetCellValue(cells, headers, "Last update") ?? GetCellValue(cells, headers, "Letztes Update"),
            };
            entry.LastUpdateAge = ParseRelativeAge(entry.LastUpdateText);

            if (int.TryParse(GetCellValue(cells, headers, "Rd."), out var rounds)) entry.Rounds = rounds;
            if (int.TryParse(GetCellValue(cells, headers, "n"), out var playerCount)) entry.PlayerCount = playerCount;

            results.Add(entry);
        }

        // Ein dbkey kann in einer Trefferliste mehrfach auftauchen; erster Treffer gewinnt, damit der
        // Aufrufer nicht zweimal denselben Eintrag upsertet.
        return results
            .GroupBy(r => r.ChessResultsId)
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>
    /// Datumsformat der Trefferliste haengt am lan-Parameter: lan=1 liefert "2026/12/18",
    /// lan=0 liefert "18.12.2026". Beide werden akzeptiert.
    /// </summary>
    internal static DateOnly? ParseSearchDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string[] formats = ["yyyy/MM/dd", "dd.MM.yyyy", "yyyy-MM-dd"];
        return DateOnly.TryParseExact(text.Trim(), formats,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    private static readonly (string[] Tokens, Func<double, TimeSpan> Factory)[] AgeUnits =
    [
        (["day", "days", "tag", "tage", "tagen"], v => TimeSpan.FromDays(v)),
        (["hour", "hours", "std", "stunde", "stunden"], v => TimeSpan.FromHours(v)),
        (["minute", "minutes", "min", "minuten"], v => TimeSpan.FromMinutes(v)),
        (["second", "seconds", "sec", "sek", "sekunde", "sekunden"], v => TimeSpan.FromSeconds(v)),
    ];

    /// <summary>
    /// "Last update" steht in der Trefferliste als relatives ALTER: "16 Days", "1 Days 2 Hours",
    /// "8 Tage 23 Std.", "3 Hours 36 Min.", "7 Minutes". Rueckgabe ist bewusst die Zeitspanne und
    /// nicht der Zeitpunkt - so bleibt die Funktion ohne Uhr testbar; den Zeitstempel bildet der
    /// Aufrufer. Unbekannte Einheiten werden ignoriert statt die ganze Angabe zu verwerfen.
    /// </summary>
    internal static TimeSpan? ParseRelativeAge(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        TimeSpan? total = null;
        foreach (Match match in Regex.Matches(text, @"(\d+)\s*([A-Za-zÄÖÜäöü]+)"))
        {
            if (!int.TryParse(match.Groups[1].Value, out var value)) continue;
            var unit = match.Groups[2].Value.ToLowerInvariant();
            var entry = AgeUnits.FirstOrDefault(u => u.Tokens.Contains(unit));
            if (entry.Factory is null) continue;
            total = (total ?? TimeSpan.Zero) + entry.Factory(value);
        }
        return total;
    }
}

public class ParsedPlayer
{
    public int Snr { get; set; }
    public string Name { get; set; } = "";
    public string? Title { get; set; }
    public string? FideId { get; set; }
    public int? Elo { get; set; }
    public string? Country { get; set; }
    public string? TeamName { get; set; }
    public int? BoardNumber { get; set; }
}

public class ParsedTeamPairing
{
    public int MatchNumber { get; set; }
    public string HomeTeamName { get; set; } = "";
    public string AwayTeamName { get; set; } = "";
    public decimal? HomeScore { get; set; }
    public decimal? AwayScore { get; set; }
}

public class ParsedPairing
{
    public int BoardNumber { get; set; }
    public string WhiteName { get; set; } = "";
    public string BlackName { get; set; } = "";
    public int WhiteSnr { get; set; }
    public int BlackSnr { get; set; }
    public string? Result { get; set; }
}

public class ParsedTournamentDetails
{
    public string? DateText { get; set; }
    public string? Location { get; set; }

    /// <summary>
    /// Die Bedenkzeit als ROHTEXT, wie chess-results sie fuehrt („90 Min. / 40 Zuege + 30 Min. +
    /// 30 Sekunden ab Zug 1"). Bewusst nicht hier schon in eine Kategorie uebersetzt: der Crawler
    /// gibt weiter, was auf der Seite steht.
    /// </summary>
    public string? TimeControl { get; set; }

    /// <summary>
    /// Die Klasse, die chess-results SELBST nennt — sie steht in Klammern hinter der Beschriftung
    /// („Time control (Standard)", „(Rapid)", „(Blitz)"). Das ist die verlaesslichere Quelle als
    /// jede Ableitung aus dem Freitext; fehlt sie, bleibt nur der Text.
    /// </summary>
    public string? TimeControlKind { get; set; }

    /// <summary>Die Gruppen derselben Veranstaltung (Zeile „Turnierauswahl"); leer ohne eine solche.</summary>
    public List<ParsedTournamentGroup> Groups { get; set; } = [];
}

/// <summary>Eine Gruppe aus der „Turnierauswahl" — Nummer und Bezeichnung, wie chess-results sie zeigt.</summary>
public class ParsedTournamentGroup
{
    /// <summary>chess-results-Nummer; <c>null</c> fuer die eigene (dort ohne Link).</summary>
    public string? ChessResultsId { get; set; }
    public string Label { get; set; } = "";
    public bool IsCurrent { get; set; }
}

/// <summary>
/// Eine Zeile der Spielersuche: die Teilnahme EINES Spielers an EINEM Turnier. Ein Abruf liefert
/// die ganze Historie — vergangene und kuenftige Turniere.
/// </summary>
public class ParsedPlayerTournament
{
    public string TournamentId { get; set; } = "";
    public string TournamentName { get; set; } = "";
    public string? EndDate { get; set; }

    /// <summary>
    /// Startnummer des Spielers in diesem Turnier — steht NUR im Link auf den Namen und ist der
    /// Schluessel zur Spielerkarte (Punkte, Platz, Performance).
    /// </summary>
    public int? Snr { get; set; }

    public string? PlayerName { get; set; }
    /// <summary>chess-results-Ident-Nummer; bei Auslandsturnieren steht dort „0".</summary>
    public string? IdentNumber { get; set; }
    public string? FideId { get; set; }
    public string? Club { get; set; }
    public string? Federation { get; set; }

    /// <summary>Platz; <c>null</c> heisst „noch nicht gespielt" (Spalte enthaelt „-").</summary>
    public int? Rank { get; set; }
    public int? Rounds { get; set; }
    /// <summary>Teilnehmerzahl des Turniers (Spalte „n").</summary>
    public int? PlayerCount { get; set; }
}

/// <summary>Eine Runde mit ihrem Termin, aus dem Rundenplan (art=14).</summary>
/// <summary>
/// Kopfdaten eines Turniers, ohne Import — Termin, Ort, Rundenzahl und Bedenkzeit (Rohtext).
/// </summary>
public class ParsedTournamentInfo
{
    public string ChessResultsId { get; set; } = "";
    public string? Name { get; set; }
    public string? DateText { get; set; }
    public string? Location { get; set; }
    public string? TimeControl { get; set; }
    /// <summary>Die von chess-results genannte Klasse („Standard", „Rapid", „Blitz").</summary>
    public string? TimeControlKind { get; set; }
    public int? TotalRounds { get; set; }
}

public class ParsedRoundDate
{
    public int Number { get; set; }
    public DateOnly Date { get; set; }
    /// <summary>Rohtext der Uhrzeit („14:00 Uhr") — fuer den Kalender zaehlt der Tag.</summary>
    public string? TimeText { get; set; }
}

/// <summary>
/// Punkte, Platz, Performance-Rating und Elo-Aenderung eines Spielers in EINEM Turnier — der
/// „Player info"-Block der Spielerkarte (art=9).
/// </summary>
public class ParsedPlayerCard
{
    /// <summary>
    /// Tatsaechlich gespielte Partien (Zeilen der Partien-Tabelle mit Gegner). <c>null</c>, wenn
    /// die Seite keine solche Tabelle hat. NICHT die Rundenzahl des Turniers: in einer Liga wird
    /// ein Spieler an einem Teil der Termine aufgestellt.
    /// </summary>
    public int? GamesPlayed { get; set; }

    public string? Name { get; set; }
    public string? Federation { get; set; }
    public string? Club { get; set; }
    public string? IdentNumber { get; set; }
    public string? FideId { get; set; }
    public int? StartingRank { get; set; }
    public int? RatingNational { get; set; }
    public int? RatingInternational { get; set; }
    /// <summary>Turnier-Leistung („Performance rating") — die Zahl, um die es hier eigentlich geht.</summary>
    public int? PerformanceRating { get; set; }
    public int? Rank { get; set; }
    public int? YearOfBirth { get; set; }
    /// <summary>Erreichte Punkte; Bruchteile kommen als Kommazahl („1,5").</summary>
    public decimal? Points { get; set; }
    /// <summary>Elo-Aenderung aus diesem Turnier („FIDE rtg +/-").</summary>
    public decimal? RatingChange { get; set; }

    /// <summary>
    /// Gab es schon ein Ergebnis? Ein kuenftiges Turnier liefert den Block ohne Werte — das ist
    /// der Normalfall und kein Fehler, aber es darf nicht als „null Punkte" gespeichert werden.
    /// </summary>
    public bool HasResult { get; set; }
}

public class ParsedPlayerSearchResult
{
    public string Name { get; set; } = "";
    public string? FideId { get; set; }
    public string? ChessResultsId { get; set; }
    public int? Elo { get; set; }
    public string? Country { get; set; }
    public string? Title { get; set; }
}

public class ParsedPlayerResult
{
    public int RoundNumber { get; set; }
    public int BoardNumber { get; set; }
    public int? OpponentSnr { get; set; }
    public string? OpponentName { get; set; }
    public int? OpponentElo { get; set; }
    public string? Points { get; set; }
    public string? Result { get; set; }
}


/// <summary>
/// Ein Eintrag des chess-results-ANKUENDIGUNGSkalenders (<c>Kalender.aspx</c>).
///
/// <para>Bewusst mager: der Kalender fuehrt Termin, Name, Foederation und Verweise — und sonst
/// NICHTS. Kein Ort, keine Bedenkzeit, keine Rundenzahl. Er ist eine Ankuendigung, keine
/// Turnierseite; sein Wert liegt allein im VORLAUF.</para>
/// </summary>
public class ParsedCalendarEntry
{
    public string Name { get; set; } = "";
    public string? Federation { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }

    /// <summary>
    /// Die Nummer des Kalendereintrags aus dem Ausschreibungs-Verweis
    /// (<c>Kalender.aspx?key6=DL&amp;id=10814</c>). Sie ist die einzige stabile Kennung, die der
    /// Kalender hergibt — und sie fehlt bei rund einem Fuenftel der Eintraege.
    /// </summary>
    public string? CalendarId { get; set; }

    /// <summary>Verweis des Veranstalters (eigene Seite). Selten ein chess-results-Link.</summary>
    public string? Url { get; set; }

    /// <summary>
    /// Die chess-results-Turniernummer, WENN der Verweis auf eine Turnierseite zeigt. Nur bei
    /// wenigen Eintraegen (an AUT gemessen: 6 von 146) — die Zuordnung zu bestehenden Eintraegen
    /// muss deshalb ueber Termin und Namen laufen, nicht ueber diese Nummer.
    /// </summary>
    public string? ChessResultsId { get; set; }
}

public class ParsedDirectoryTournament
{
    public string ChessResultsId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Federation { get; set; }
    public string? State { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public string? LocationText { get; set; }
    public string? TimeControlText { get; set; }
    public string? Director { get; set; }
    public string? Organizer { get; set; }
    public string? ChiefArbiter { get; set; }
    public int? Rounds { get; set; }
    public int? PlayerCount { get; set; }
    public string? LastUpdateText { get; set; }
    public TimeSpan? LastUpdateAge { get; set; }
}
