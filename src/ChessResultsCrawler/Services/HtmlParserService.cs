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
                Title = GetCellValue(cells, headers, "Title") ?? GetCellValue(cells, headers, "Ti.") ?? GetCellValue(cells, headers, "Typ") ?? GetCellValue(cells, headers, ""),
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
        int matchNum = 0;

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
            if (!int.TryParse(nrText, out _)) continue;

            matchNum++;
            var pairing = new ParsedTeamPairing { MatchNumber = matchNum };

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
        // Normalize: "1-0", "0-1", "½-½", "+--", "--+" etc.
        return result
            .Replace("½", "½")  // already correct
            .Replace("&frac12;", "½");
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
    /// </summary>
    public async Task<List<int>> ParseAvailableRoundsAsync(string html)
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

        roundNumbers.Sort();
        return roundNumbers;
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

    private static IElement? FindPairingTable(IDocument document)
    {
        // Look for tables that look like pairing tables (contain team-like data)
        var tables = document.QuerySelectorAll("table.CRs1, table.CRs2, table");
        foreach (var table in tables)
        {
            var text = table.TextContent;
            if (text.Contains(":", StringComparison.Ordinal) &&
                table.QuerySelectorAll("tr").Length > 2)
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
