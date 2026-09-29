using System.Globalization;
using System.Text.RegularExpressions;
using System.Web;
using static ChessResultsCrawler.Services.SourceText;

namespace ChessResultsCrawler.Services;

/// <summary>Ein Termin aus dem Saisonkalender der Welsh Chess Union.</summary>
public class ParsedWcuEvent
{
    /// <summary>
    /// Es gibt keine Nummer und keinen Slug — die Seite ist eine einzige Tabelle ohne
    /// Detailseiten. Der Wert wird deshalb aus Termin und Anschrift gebildet (siehe
    /// <see cref="WcuCalendarService.EventKeyOf"/>) und hier nur durchgereicht.
    /// </summary>
    public string EventId { get; set; } = "";

    public string Name { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }

    /// <summary>
    /// Die Anschrift aus der dritten Spalte, zusammengezogen zu einer Zeile — oft mit
    /// britischer Postleitzahl im Fliesstext ("Pembroke SA714LA").
    /// </summary>
    public string? Place { get; set; }

    /// <summary>Die Kalenderseite selbst — diese Quelle hat keine Detailseite je Turnier.</summary>
    public string Url { get; set; } = "";
}

/// <summary>
/// Der Saisonkalender der Welsh Chess Union (welshchessunion.uk).
///
/// <para><b>EIN Abruf, eine ganze Saison.</b> Die Seite <c>/calendar/</c> traegt eine einzige
/// TablePress-Tabelle mit drei Spalten (Datum, Turnier, Anschrift), Jahr und Monat stehen als
/// eigene fett gedruckte Zwischenzeilen darueber. Gemessen am 2026-09-09: 38 Turniere ueber zwei
/// Saisonjahre (2026/2027), 33 davon mit einer Anschrift, 30 mit einer erkennbaren britischen
/// Postleitzahl im Text — kein einziger Eintrag ist eine Sitzung, ein Lehrgang oder sonst kein
/// Turnier (anders als beim deutschen und tschechischen Verbandskalender, die beide einen Teil
/// ihrer Eintraege dafuer aussortieren muessen).</para>
///
/// <para><b>Die robots.txt sperrt gezielt den bequemen Weg.</b> Sie verbietet den
/// ai1ec-Export (<c>*controller=ai1ec_exporter_controller*</c>) und JEDE Kalender-„Aktion"
/// (<c>/calendar/action~*/</c> — Monats-, Wochen-, Tagesansicht, Agenda, Posterboard, Stream). Die
/// FLIESSTEXT-Seite <c>/calendar/</c> selbst steht in keiner dieser Zeilen und ist frei. Kein
/// <c>Crawl-delay</c> in der robots.txt; da diese Quelle ohnehin nur EINEN Abruf macht, braucht es
/// auch keinen.</para>
///
/// <para><b>KEINE stabile Kennung — und das ist die zentrale Schwierigkeit dieser Quelle.</b> Es
/// gibt weder eine Nummer noch einen URL-Slug je Turnier; die ganze Saison steht in einer Tabelle
/// ohne Verlinkung. <see cref="EventKeyOf"/> bildet die eigene Kennung deshalb aus TERMIN und
/// ANSCHRIFT, bewusst OHNE den Namen: der Verband tippt seine eigene Ausschreibung, und Tippfehler
/// im Namen kommen vor UND werden korrigiert, ohne dass sich am Turnier selbst etwas aendert.
/// Termin allein reicht nicht — am 1. November, 7. Februar und 14. Maerz stehen je ZWEI Turniere
/// auf denselben Tag (Start zweier paralleler Ligen, WCPL und WJCPL), aber IMMER mit
/// verschiedener Anschrift, und genau die trennt sie hier. Wuerde die Kennung sich bei einer
/// spaeteren Korrektur aendern, legte der naechtliche Durchgang das Turnier ein zweites Mal an —
/// deshalb bewusst NICHT der Name. Der Rest-Fehler bleibt eine Anschrift, die sich AENDERT statt
/// nur getippt zu werden (aus „(More details to follow)" wird spaeter eine echte Adresse); das ist
/// seltener als ein Tippfehler im Namen und wird hier in Kauf genommen. Dass Anschriften selbst
/// nicht fehlerfrei sind, zeigt der Bestand ebenfalls: „Best Western Heronston Hotel" steht auch
/// als „Bst Western" und als „Bet Western" im Text — verschiedene Wochen, selbes Haus.</para>
/// </summary>
public class WcuCalendarService
{
    internal const string AllowedHost = "www.welshchessunion.uk";

    private const string CalendarUrl = "https://www.welshchessunion.uk/calendar/";

    private readonly HttpClient _http;
    private readonly ILogger<WcuCalendarService> _log;

    public WcuCalendarService(HttpClient http, ILogger<WcuCalendarService> log)
    {
        _http = http;
        _log = log;
    }

    public async Task<List<ParsedWcuEvent>> FetchAsync(DateOnly from, CancellationToken ct = default)
    {
        var target = new Uri(CalendarUrl);
        EnsureAllowedTarget(target);

        using var response = await _http.GetAsync(target, ct);
        var html = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();

        var all = ParsePage(html);
        var events = all.Where(e => e.EndDate >= from).ToList();

        _log.LogInformation("WCU-Saisonkalender ab {From}: {Count} von {All} Eintraegen",
            from, events.Count, all.Count);
        return events;
    }

    // ----- Die Tabelle --------------------------------------------------------

    /// <summary>
    /// Die Kalendertabelle auseinandernehmen. Jahr und Monat stehen als eigene Zeilen (nur Spalte
    /// 1 gefuellt, Spalte 2+3 leer) und gelten fuer alle folgenden Zeilen, bis die naechste
    /// Jahres- bzw. Monatszeile kommt — ein Turnier selbst nennt sein Jahr nirgends.
    /// </summary>
    internal static List<ParsedWcuEvent> ParsePage(string html)
    {
        var table = TablePattern.Match(html);
        if (!table.Success) return [];

        var results = new List<ParsedWcuEvent>();
        var year = 0;
        var month = 0;

        foreach (Match row in RowPattern.Matches(table.Groups[1].Value))
        {
            var cells = CellPattern.Matches(row.Groups[1].Value);
            if (cells.Count != 3) continue; // z.B. die einspaltige "NOTE ..."-Hinweiszeile

            var col1Html = cells[0].Groups[1].Value;
            var col2Html = cells[1].Groups[1].Value;
            var col3Html = cells[2].Groups[1].Value;

            var col1Text = FlatText(col1Html);
            var col2Text = FlatText(col2Html);
            var col3Text = FlatText(col3Html);

            if (col1Text.Length == 0 && col2Text.Length == 0 && col3Text.Length == 0)
                continue; // Trennzeile ohne Inhalt

            if (col2Text.Length == 0 && col3Text.Length == 0)
            {
                if (TryParseYear(col1Text, out var newYear)) { year = newYear; continue; }
                if (TryParseMonth(col1Text, out var newMonth)) { month = newMonth; continue; }
            }

            if (year == 0 || month == 0) continue; // noch keine Jahres-/Monatszeile gesehen

            var day = DayRangePattern.Match(col1Text);
            if (!day.Success) continue;

            if (!TryBuildDate(year, month, day.Groups[1].Value, out var start)) continue;
            var endDayText = day.Groups[2].Success ? day.Groups[2].Value : day.Groups[1].Value;
            if (!TryBuildDate(year, month, endDayText, out var end)) end = start;
            if (end < start) end = start;

            var name = string.Join(" / ", LinesOf(col2Html));
            if (name.Length == 0) continue;

            var place = PlaceOf(col3Html);
            results.Add(new ParsedWcuEvent
            {
                EventId = EventKeyOf(start, end, place),
                Name = name,
                StartDate = start,
                EndDate = end,
                Place = place,
                Url = CalendarUrl,
            });
        }

        return [.. results.OrderBy(e => e.StartDate)];
    }

    private static readonly Regex TablePattern = new(
        @"<table[^>]*\bid=""tablepress-\d+""[^>]*>(.*?)</table>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex RowPattern =
        new(@"<tr\b[^>]*>(.*?)</tr>", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex CellPattern =
        new(@"<td\b[^>]*>(.*?)</td>", RegexOptions.Singleline | RegexOptions.Compiled);

    // ----- Jahr, Monat, Datum ---------------------------------------------------

    private static bool TryParseYear(string text, out int year) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out year)
        && year is >= 2000 and <= 2100;

    private static bool TryParseMonth(string text, out int month) =>
        MonthNames.TryGetValue(text.Trim(), out month);

    private static readonly Dictionary<string, int> MonthNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jan"] = 1, ["january"] = 1,
        ["feb"] = 2, ["february"] = 2,
        ["mar"] = 3, ["march"] = 3,
        ["apr"] = 4, ["april"] = 4,
        ["may"] = 5,
        ["jun"] = 6, ["june"] = 6,
        ["jul"] = 7, ["july"] = 7,
        ["aug"] = 8, ["august"] = 8,
        ["sep"] = 9, ["sept"] = 9, ["september"] = 9,
        ["oct"] = 10, ["october"] = 10,
        ["nov"] = 11, ["november"] = 11,
        ["dec"] = 12, ["december"] = 12,
    };

    /// <summary>
    /// „15th", „21st - 23rd", „12th-14th" (ohne Leerzeichen), „3rd - 9th " (mit
    /// Leerzeichen am Ende, wird von <see cref="FlatText"/> schon getrimmt).
    /// </summary>
    private static readonly Regex DayRangePattern = new(
        @"^(\d{1,2})(?:st|nd|rd|th)?\s*(?:-\s*(\d{1,2})(?:st|nd|rd|th)?)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool TryBuildDate(int year, int month, string dayText, out DateOnly date)
    {
        date = default;
        if (!int.TryParse(dayText, NumberStyles.None, CultureInfo.InvariantCulture, out var day))
            return false;
        if (day < 1 || day > DateTime.DaysInMonth(year, month)) return false;

        date = new DateOnly(year, month, day);
        return true;
    }

    // ----- Name und Anschrift ---------------------------------------------------

    /// <summary>
    /// Manche Zellen tragen ZWEI Turniere uebereinander (z.B. „Welsh Seniors Champ" und
    /// „Welsh Open" im selben Wochenend-Termin) — beide Zeilen werden mit „ / " zu einem
    /// gemeinsamen Eintrag verbunden statt zwei Eintraege anzulegen, denen beiden dieselbe Zeile
    /// gehoert.
    /// </summary>
    internal static string? PlaceOf(string columnHtml)
    {
        var withoutLinks = AnchorPattern.Replace(columnHtml, "");
        var lines = LinesOf(withoutLinks);
        return lines.Count > 0 ? string.Join(", ", lines) : null;
    }

    private static readonly Regex AnchorPattern =
        new(@"<a\b[^>]*>.*?</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Eine Zelle in ihre <c>&lt;br/&gt;</c>-getrennten Zeilen zerlegen, Markup raus, Entitaeten
    /// aufgeloest, ein angehaengtes Komma je Zeile entfernt, leere Zeilen verworfen.
    /// </summary>
    private static List<string> LinesOf(string html)
    {
        var withBreaks = BreakPattern.Replace(html ?? "", "\n");
        var text = HttpUtility.HtmlDecode(StripTags(withBreaks)) ?? "";

        return [.. text.Split('\n')
            .Select(line => Collapse(line).TrimEnd(','))
            .Where(line => line.Length > 0)];
    }

    private static readonly Regex BreakPattern =
        new(@"<\s*br\s*/?\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string FlatText(string html) =>
        Collapse(HttpUtility.HtmlDecode(StripTags(html)) ?? "");

    /// <summary>
    /// Markup entfernen — inklusive eines angebrochenen Tags OHNE schliessendes „&gt;" am Ende
    /// ("Welsh Championship&lt;b.&lt;" statt des ueblichen "&lt;b/&gt;"; ein echter Tippfehler
    /// der Quelle, einmal beobachtet). Ohne die zweite Regel bliebe der Rest des kaputten Tags im
    /// Text stehen, weil ihm das schliessende Zeichen fehlt.
    /// </summary>
    private static string StripTags(string? html) =>
        Regex.Replace(Regex.Replace(html ?? "", "<[^>]+>", " "), "<[^>]*$", " ");

    // ----- Die eigene Kennung ----------------------------------------------------

    /// <summary>Siehe die Klassen-Dokumentation: Termin + Anschrift, bewusst ohne den Namen.</summary>
    internal static string EventKeyOf(DateOnly start, DateOnly end, string? place) =>
        $"{start:yyyy-MM-dd}|{end:yyyy-MM-dd}|{(place ?? "").Trim().ToLowerInvariant()}";

    // ----- Ziel-Pruefung ---------------------------------------------------------

    internal static void EnsureAllowedTarget(Uri url) => SourceHostGuard.Ensure(url, AllowedHost);
}
