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

            if (cells.Count >= 6)
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

            if (label.Equals("Date", StringComparison.OrdinalIgnoreCase) ||
                label.Equals("Datum", StringComparison.OrdinalIgnoreCase))
            {
                details.DateText = value;
            }
            else if (label.Equals("Location", StringComparison.OrdinalIgnoreCase) ||
                     label.Equals("Ort", StringComparison.OrdinalIgnoreCase))
            {
                details.Location = value;
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

            results.Add(new ParsedPlayerTournament
            {
                TournamentId = tournamentId,
                TournamentName = tournamentName,
                EndDate = endDate
            });
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
    /// </summary>
    public async Task<List<ParsedPlayerResult>> ParsePlayerDetailPageAsync(string html)
    {
        var results = new List<ParsedPlayerResult>();
        var context = BrowsingContext.New(Configuration.Default);
        var document = await context.OpenAsync(req => req.Content(html));

        var table = document.QuerySelector("table.CRs1")
            ?? document.QuerySelector("table.CRs2")
            ?? FindTableByHeaders(document, ["Rd.", "Name"]);
        if (table is null)
        {
            // Try German header variant
            table = FindTableByHeaders(document, ["Rd.", "Erg."]);
            if (table is null) return results;
        }

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

            var eloText = GetCellValue(cells, headers, "Rtg") ?? GetCellValue(cells, headers, "Elo");
            if (int.TryParse(eloText, out var elo)) result.OpponentElo = elo;

            result.Points = GetCellValue(cells, headers, "Pkt.") ?? GetCellValue(cells, headers, "Pts.");
            result.Result = GetCellValue(cells, headers, "Erg.") ?? GetCellValue(cells, headers, "Res.");

            results.Add(result);
        }

        return results;
    }

    /// <summary>
    /// Extracts the SNode (s1/s2/s3) from a redirect URL or page content.
    /// </summary>
    public static string? ExtractSNode(string url)
    {
        var match = Regex.Match(url, @"chess-results\.com/(s\d+)/");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static IElement? FindTableByHeaders(IDocument document, string[] requiredHeaders)
    {
        var tables = document.QuerySelectorAll("table");
        foreach (var table in tables)
        {
            var firstRow = table.QuerySelector("tr");
            if (firstRow is null) continue;

            var headerTexts = firstRow.QuerySelectorAll("th, td")
                .Select(c => c.TextContent.Trim())
                .ToList();

            if (requiredHeaders.All(h =>
                headerTexts.Any(ht => ht.Contains(h, StringComparison.OrdinalIgnoreCase))))
            {
                return table;
            }
        }
        return null;
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
}

public class ParsedPlayerTournament
{
    public string TournamentId { get; set; } = "";
    public string TournamentName { get; set; } = "";
    public string? EndDate { get; set; }
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
