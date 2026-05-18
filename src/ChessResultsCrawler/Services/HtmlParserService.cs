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
    /// </summary>
    public async Task<List<ParsedTeamPairing>> ParseTeamPairingsAsync(string html)
    {
        var pairings = new List<ParsedTeamPairing>();
        var context = BrowsingContext.New(Configuration.Default);
        var document = await context.OpenAsync(req => req.Content(html));

        var table = FindTableByHeaders(document, ["Nr."]);
        if (table is null)
        {
            // Try alternate: look for tables with team pairing patterns
            table = FindPairingTable(document);
        }
        if (table is null) return pairings;

        var rows = table.QuerySelectorAll("tr").Skip(1);
        int matchNum = 0;

        foreach (var row in rows)
        {
            var cells = row.QuerySelectorAll("td").ToList();
            if (cells.Count < 4) continue;

            matchNum++;

            // Typical art=2 format: Nr | HomeTeam | AwayTeam | Result
            // or: Nr | HomeTeam | - | AwayTeam | Result
            var pairing = new ParsedTeamPairing { MatchNumber = matchNum };

            if (cells.Count >= 6)
            {
                // Wide format: Nr | Snr | HomeTeam | AwayTeam | Snr | Result
                pairing.HomeTeamName = CleanTeamName(cells[2].TextContent);
                pairing.AwayTeamName = CleanTeamName(cells[3].TextContent);
                var scoreText = cells.Count > 5 ? cells[5].TextContent.Trim() : null;
                ParseScore(scoreText, pairing);
            }
            else
            {
                // Compact format: Nr | HomeTeam | AwayTeam | Result
                pairing.HomeTeamName = CleanTeamName(cells[1].TextContent);
                pairing.AwayTeamName = CleanTeamName(cells[2].TextContent);
                var scoreText = cells.Count > 3 ? cells[3].TextContent.Trim() : null;
                ParseScore(scoreText, pairing);
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

    private static void ParseScore(string? scoreText, ParsedTeamPairing pairing)
    {
        if (string.IsNullOrWhiteSpace(scoreText)) return;

        // Score format: "3½:½" or "3.5:0.5" or "3:1" etc.
        scoreText = scoreText.Replace("½", ".5").Replace(",", ".");
        var parts = scoreText.Split(':');
        if (parts.Length == 2)
        {
            if (decimal.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var home))
                pairing.HomeScore = home;
            if (decimal.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var away))
                pairing.AwayScore = away;
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
