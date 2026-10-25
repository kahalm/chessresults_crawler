using System.Globalization;
using System.Text.RegularExpressions;
using static ChessResultsCrawler.Services.HtmlTable;
using static ChessResultsCrawler.Services.SourceText;

namespace ChessResultsCrawler.Services;

/// <summary>
/// Verzeichnisseiten von chess-results: Ankuendigungskalender und Trefferliste der Turniersuche
/// (TurnierSuche.aspx). Aufgerufen ueber <see cref="HtmlParserService"/>.
/// </summary>
internal static class DirectoryParser
{
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
    public static async Task<List<ParsedCalendarEntry>> ParseCalendarAsync(string html)
    {
        var results = new List<ParsedCalendarEntry>();
        var document = await OpenAsync(html);

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

    /// <summary>
    /// Parst die Trefferliste der Turniersuche (TurnierSuche.aspx).
    /// Zieltabelle ist die innere table.CRs2 in #datenxx; deren Kopfzeile mischt th und td.
    /// Spalten werden - wie ueberall in dieser Datei - ueber den Kopfzeilen-NAMEN aufgeloest, mit
    /// deutschen Synonymen: eine Server-Node, die den lan-Parameter ignoriert, wuerde sonst still
    /// eine leere Liste liefern statt aufzufallen.
    /// </summary>
    public static async Task<List<ParsedDirectoryTournament>> ParseTournamentSearchAsync(string html)
    {
        var results = new List<ParsedDirectoryTournament>();
        var document = await OpenAsync(html);

        var table = document.QuerySelector("#datenxx table.CRs2")
            ?? document.QuerySelector("table.CRs2")
            ?? document.QuerySelector("table.CRs1")
            ?? FindTableByHeaders(document, ["dbkey"]);
        if (table is null) return results;

        var headers = HeaderMap(table);
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
