using System.Globalization;
using AngleSharp.Dom;
using System.Text.RegularExpressions;
using static ChessResultsCrawler.Services.HtmlTable;

namespace ChessResultsCrawler.Services;

/// <summary>
/// Turnierseiten von chess-results: Startrangliste (art=0/16), Turnierdetails samt Turnierauswahl,
/// Runden (Anzahl, verfuegbare, Rundenplan art=14), Turniername und Mannschaftsnamen.
/// Aufgerufen ueber <see cref="HtmlParserService"/>.
/// </summary>
internal static class TournamentPagesParser
{
    /// <summary>
    /// Parses the player list: art=16 (team tournaments, "Startrangliste" sorted by Elo) or
    /// art=0 (individual tournaments, starting rank list).
    /// Returns list of parsed players with Snr, Name, Title, FideId, Elo, Country, Team name, BoardNumber.
    /// Snr is the real starting number from the name link (art=9&amp;snr=N): on art=16 the "Nr."
    /// column is only the row number of the Elo sort, not the starting number. "Nr." is used only
    /// for rows without such a link.
    /// </summary>
    public static async Task<List<ParsedPlayer>> ParsePlayerListAsync(string html)
    {
        var players = new List<(ParsedPlayer Player, bool SnrFromLink)>();
        var document = await OpenAsync(html);

        // Prefer CRs1/CRs2 tables (chess-results data tables), fall back to header search
        var table = document.QuerySelector("table.CRs1")
            ?? document.QuerySelector("table.CRs2")
            ?? FindTableByHeaders(document, ["Nr.", "Name"]);
        if (table is null) return [];

        var headers = HeaderMap(table);

        var allRows = table.QuerySelectorAll(":scope > tr, :scope > tbody > tr");
        var rows = allRows.Skip(1);
        foreach (var row in rows)
        {
            var cells = row.QuerySelectorAll(":scope > td").ToList();
            if (cells.Count < 3) continue;

            // Die Startnummer steht verlaesslich nur im Namenslink (tnr<id>.aspx?...&art=9&...&snr=<n>).
            // Auf art=16 ("Spieler nach Elo sortiert") ist "Nr." nur die laufende Zeile der
            // Elo-Sortierung - wer sie als Snr nimmt, holt spaeter die Spielerkarte einer anderen Person.
            var snr = GetLinkedSnr(cells, headers);
            var snrFromLink = snr is not null;
            if (snr is null)
            {
                var snrText = GetCellValue(cells, headers, "Nr.");
                if (int.TryParse(snrText, out var nr)) snr = nr;
            }
            if (snr is null) continue;

            var player = new ParsedPlayer
            {
                Snr = snr.Value,
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
                players.Add((player, snrFromLink));
        }

        // Snr ist je Turnier eindeutig (Upsert-Schluessel). Eine "Nr."-Rueckfallzeile darf keine
        // Startnummer belegen, die ein Namenslink schon vergeben hat; sonst gewinnt die erste Zeile.
        return players
            .GroupBy(p => p.Player.Snr)
            .Select(g => g.OrderByDescending(p => p.SnrFromLink).First().Player)
            .ToList();
    }

    /// <summary>
    /// Startnummer aus dem Link auf den Spielernamen (art=9-Spielerkarte), sonst null.
    /// </summary>
    private static int? GetLinkedSnr(List<IElement> cells, Dictionary<string, int> headers)
    {
        if (!headers.TryGetValue("Name", out var idx) || idx >= cells.Count) return null;
        foreach (var a in cells[idx].QuerySelectorAll("a[href]"))
        {
            var href = a.GetAttribute("href") ?? "";
            if (!Regex.IsMatch(href, @"[?&]art=9(?:&|$)")) continue;
            var m = Regex.Match(href, @"[?&]snr=(\d+)(?:&|$)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var snr)) return snr;
        }
        return null;
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

    /// <summary>
    /// Parses art=0 page to extract total number of rounds.
    /// Looks for patterns like "nach X Runden" or "after X rounds".
    /// </summary>
    public static async Task<int?> ParseTotalRoundsAsync(string html)
    {
        var document = await OpenAsync(html);

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
    public static async Task<List<int>> ParseAvailableRoundsAsync(string html, int? maxRound = null)
    {
        var roundNumbers = new List<int>();
        var document = await OpenAsync(html);

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
    public static async Task<string?> ParseTournamentNameAsync(string html)
    {
        var document = await OpenAsync(html);

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
    public static async Task<ParsedTournamentDetails> ParseTournamentDetailsAsync(string html)
    {
        var details = new ParsedTournamentDetails();
        var document = await OpenAsync(html);

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
    public static async Task<List<ParsedRoundDate>> ParseRoundPlanAsync(string html)
    {
        var results = new List<ParsedRoundDate>();
        var document = await OpenAsync(html);

        var table = FindTableByHeaders(document, ["Round", "Date"])
                    ?? FindTableByHeaders(document, ["Runde", "Datum"]);
        if (table is null) return results;

        var headers = HeaderMap(table);

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
    public static async Task<List<string>> ParseTeamNamesAsync(string html)
    {
        var names = new List<string>();
        var document = await OpenAsync(html);

        var table = FindTableByHeaders(document, ["Team"])
            ?? FindTableByHeaders(document, ["Mannschaft"]);
        if (table is null) return names;

        var headers = HeaderMap(table);

        foreach (var row in table.QuerySelectorAll(":scope > tr, :scope > tbody > tr").Skip(1))
        {
            var cells = row.QuerySelectorAll("td").ToList();
            if (cells.Count == 0) continue;

            var name = GetCellValue(cells, headers, "Team") ?? GetCellValue(cells, headers, "Mannschaft");
            if (string.IsNullOrWhiteSpace(name)) continue;

            var cleaned = PairingsParser.CleanTeamName(name);
            if (cleaned.Length > 0 && !names.Contains(cleaned, StringComparer.OrdinalIgnoreCase))
                names.Add(cleaned);
        }
        return names;
    }
}
