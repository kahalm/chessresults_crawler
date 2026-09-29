using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using static ChessResultsCrawler.Services.SourceText;

namespace ChessResultsCrawler.Services;

/// <summary>Ein Eintrag aus dem Kalender des tschechischen Verbands (SSCR, chess.cz).</summary>
public class ParsedChessCzEvent
{
    /// <summary>
    /// Der Adress-Bestandteil der Detailseite (<c>/akce/&lt;slug&gt;/</c>) — die einzige Kennung,
    /// die diese Quelle hat. Eine Nummer gibt es nirgends im Markup.
    /// </summary>
    public string EventId { get; set; } = "";

    public string Name { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }

    /// <summary>Ort als Freitext; bei Ligarunden LEER (sie werden dezentral gespielt).</summary>
    public string? Place { get; set; }

    /// <summary>Das Laenderfaehnchen der Zeile als ISO-2 („CZ", „SK").</summary>
    public string? Country { get; set; }

    /// <summary>Die chess-results-Nummer, wenn die Zeile dorthin verlinkt (39 von 89).</summary>
    public string? ChessResultsId { get; set; }

    /// <summary>
    /// Der Eintrag steht in der Lasche „Kalendář mládeže". Anders als das ungarische Jugend-Feld
    /// ist diese Angabe verlaesslich (8 von 89, alle richtig) — und sie traegt Faelle, die kein
    /// Namensmuster faengt: „Mistrovství Čech 8 – 10 let" nennt seine Altersklasse als Spanne
    /// ohne das Wort „mládež".
    /// </summary>
    public bool Youth { get; set; }

    /// <summary>
    /// Nach dem Namen kein Turnier: Schiedsrichter- und Trainerschulung, Trainingslager,
    /// Arbeitstreffen. 18 von 89 — jeder fuenfte Eintrag dieses Kalenders.
    /// </summary>
    public bool NonTournament { get; set; }

    /// <summary>
    /// Die Runde einer Mannschaftsmeisterschaft: „šachy.cz Extraliga – 3. kolo" ist Runde 3.
    /// <c>null</c> bei allem anderen.
    /// </summary>
    public int? RoundNumber { get; set; }

    /// <summary>
    /// Der Name der Meisterschaft ohne den Rundenzusatz („šachy.cz Extraliga"). Nur gesetzt, wenn
    /// <see cref="RoundNumber"/> es ist — er bindet die Runden zusammen.
    /// </summary>
    public string? SeriesName { get; set; }
}

/// <summary>
/// Der Terminkalender des tschechischen Verbands (Šachový svaz České republiky).
///
/// <para><b>Die billigste Quelle der Reihe — und die mit dem kleinsten Volumen.</b> EIN Abruf ohne
/// Formular, ohne Paginierung, ohne Detailseiten liefert 89 Eintraege. Davon sind aber 18 gar
/// keine Turniere (Schulungen, Trainingslager, Sitzungen) und 33 sind RUNDEN von drei
/// Mannschaftsmeisterschaften; es bleiben rund 38 echte Turniere, von denen 13 nicht auf
/// chess-results stehen. chess-results fuehrt fuer CZE im selben Zeitraum 146 — die Abdeckung ist
/// dort viermal dichter.</para>
///
/// <para><b>Warum sie sich trotzdem lohnt, sind die 33 Ligarunden.</b> Sie sind genau das, wofuer
/// sonst je Turnier eine eigene chess-results-Seite geholt wird (<c>art=14</c>, rund sechs
/// Sekunden hinter dem Rate-Limiter): die SPIELTERMINE einer Liga, die sich ueber sieben Monate
/// zieht. Hier stehen sie in derselben Antwort, und 22 von ihnen nennen die chess-results-Nummer
/// ihrer Meisterschaft gleich mit.</para>
///
/// <para><b>Die Seite hat drei Laschen, und die erste enthaelt alle.</b> „Nejbližší akce" fuehrt
/// alle 89; „Mistrovské soutěže" (41) und „Kalendář mládeže" (8) sind Teilmengen davon und
/// erscheinen im Markup ein zweites Mal. Ohne Entdopplung ueber den Slug bekaeme jeder zweite
/// Eintrag ein Duplikat — dafuer sagt die Jugend-Lasche verlaesslich, was ein Jugendturnier
/// ist.</para>
///
/// <para>Rechtslage (2026-09-08 geprueft): <c>robots.txt</c> sperrt fuer <c>*</c> die Pfade
/// <c>/wp-admin/</c>, <c>/souteze/vyber-kraje/</c>, <c>/soutez</c> und <c>/druzstvo</c> — die
/// Terminliste liegt unter <c>/vypis-vsech-udalosti/</c> und ist nicht betroffen. Kein
/// TDM-Vorbehalt.</para>
/// </summary>
public class ChessCzCalendarService
{
    internal const string AllowedHost = "www.chess.cz";

    private const string CalendarUrl = "https://www.chess.cz/vypis-vsech-udalosti/";

    private readonly HttpClient _http;
    private readonly ILogger<ChessCzCalendarService> _log;

    public ChessCzCalendarService(HttpClient http, ILogger<ChessCzCalendarService> log)
    {
        _http = http;
        _log = log;
    }

    public async Task<List<ParsedChessCzEvent>> FetchAsync(DateOnly from, CancellationToken ct = default)
    {
        var target = new Uri(CalendarUrl);
        EnsureAllowedTarget(target);

        using var response = await _http.GetAsync(target, ct);
        var html = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();

        var all = await ParseAsync(html);
        var events = all.Where(e => e.EndDate >= from).OrderBy(e => e.StartDate).ToList();

        _log.LogInformation("chess.cz-Kalender ab {From}: {Count} von {All} Eintraegen",
            from, events.Count, all.Count);
        return events;
    }

    /// <summary>
    /// Die Terminliste auseinandernehmen. Eine Zeile ist ein <c>div[role=event]</c>; entdoppelt
    /// wird ueber den Slug, weil dieselbe Zeile in mehreren Laschen steht.
    /// </summary>
    internal static async Task<List<ParsedChessCzEvent>> ParseAsync(string html)
    {
        var context = BrowsingContext.New(Configuration.Default);
        var document = await context.OpenAsync(req => req.Content(html));

        // Wer in der Jugend-Lasche steht, ist ein Jugendtermin. Zuerst gesammelt, weil dieselbe
        // Zeile weiter oben in „Nejbližší akce" ohne diesen Hinweis noch einmal vorkommt.
        var youth = document.QuerySelectorAll("#youth-calendar div[role=event]")
            .Select(SlugOf)
            .Where(s => s is { Length: > 0 })
            .ToHashSet(StringComparer.Ordinal)!;

        var results = new List<ParsedChessCzEvent>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in document.QuerySelectorAll("div[role=event]"))
        {
            var slug = SlugOf(row);
            if (slug is not { Length: > 0 } || !seen.Add(slug)) continue;

            var name = Collapse(row.QuerySelector(".title-big a")?.TextContent
                                ?? row.QuerySelector("strong")?.TextContent);
            if (name.Length == 0) continue;

            if (ParseSpan(CalendarTextOf(row)) is not { } span) continue;

            var round = RoundPattern.Match(name);
            results.Add(new ParsedChessCzEvent
            {
                EventId = slug,
                Name = name,
                StartDate = span.Start,
                EndDate = span.End,
                Place = Empty(row.QuerySelector(".place")?.TextContent),
                Country = CountryOf(row),
                ChessResultsId = ChessSkCalendarService.ChessResultsIdOf(
                    row.QuerySelectorAll("a[href]")
                        .Select(a => a.GetAttribute("href"))
                        .FirstOrDefault(h => h is not null && h.Contains("chess-results.com/tnr",
                            StringComparison.OrdinalIgnoreCase))),
                Youth = youth.Contains(slug),
                NonTournament = NonTournamentPattern.IsMatch(ChessSkCalendarService.Fold(name)),
                RoundNumber = round.Success && int.TryParse(round.Groups["n"].Value, out var n)
                              && n is >= 1 and <= 30 ? n : null,
                SeriesName = round.Success ? Collapse(round.Groups["series"].Value) : null,
            });
        }
        return results;
    }

    private static string? SlugOf(IElement row)
    {
        var href = row.QuerySelectorAll("a[href]")
            .Select(a => a.GetAttribute("href"))
            .FirstOrDefault(h => h is not null && h.Contains("/akce/", StringComparison.Ordinal));
        var m = href is null ? Match.Empty : SlugPattern.Match(href);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static readonly Regex SlugPattern = new(@"/akce/([^/?#""]+)/?", RegexOptions.Compiled);

    /// <summary>Der Text neben dem Kalender-Symbol — dort steht der Zeitraum.</summary>
    private static string CalendarTextOf(IElement row)
    {
        var icon = row.QuerySelector("i.fa-calendar");
        return Collapse(icon?.ParentElement?.TextContent);
    }

    private static string? CountryOf(IElement row)
    {
        foreach (var icon in row.QuerySelectorAll("i[class*='flag-icon-']"))
        {
            foreach (var name in icon.ClassList)
            {
                if (!name.StartsWith("flag-icon-", StringComparison.Ordinal)) continue;
                var code = name["flag-icon-".Length..];
                if (code.Length == 2 && code.All(char.IsAsciiLetter)) return code.ToUpperInvariant();
            }
        }
        return null;
    }

    /// <summary>
    /// Der Zeitraum in den drei Formen, die diese Quelle kennt: „12. 9. 26" (ein Tag),
    /// „5. - 11. 9. 26" (im selben Monat) und „28. 9. - 4. 10. 26" (ueber den Monatswechsel).
    ///
    /// <para>Das Jahr steht EINMAL am Ende und ist zweistellig. Ueber den JAHRESwechsel gilt es
    /// deshalb fuer das Ende — liegt das Ende danach vor dem Anfang, gehoert der Anfang ins Jahr
    /// davor.</para>
    /// </summary>
    internal static (DateOnly Start, DateOnly End)? ParseSpan(string? text)
    {
        var m = SpanPattern.Match(Collapse(text));
        if (!m.Success) return null;

        var firstMonth = Number(m, "m1");
        var secondMonth = Number(m, "m2");
        var startMonth = firstMonth ?? secondMonth;
        var endMonth = secondMonth ?? firstMonth;
        if (startMonth is null || endMonth is null) return null;

        var year = Number(m, "y")!.Value;
        if (year < 100) year += 2000;

        var startDay = Number(m, "d1")!.Value;
        var endDay = Number(m, "d2") ?? startDay;

        var start = Build(year, startMonth.Value, startDay);
        var end = Build(year, endMonth.Value, endDay);
        if (start is null || end is null) return null;

        // „28. 12. - 4. 1. 27": das Jahr gehoert zum Ende, der Anfang liegt davor.
        if (end < start) start = Build(year - 1, startMonth.Value, startDay);
        return start is null ? null : (start.Value, end.Value);
    }

    private static readonly Regex SpanPattern = new(
        @"^(?<d1>\d{1,2})\.\s*(?:(?<m1>\d{1,2})\.\s*)?(?:[-–]\s*(?<d2>\d{1,2})\.\s*)?" +
        @"(?:(?<m2>\d{1,2})\.\s*)?(?<y>\d{2}|\d{4})\.?$", RegexOptions.Compiled);

    private static int? Number(Match m, string group) =>
        m.Groups[group].Success && int.TryParse(m.Groups[group].Value, out var value) ? value : null;

    private static DateOnly? Build(int year, int month, int day) =>
        year is >= 2000 and <= 2100 && month is >= 1 and <= 12
        && day >= 1 && day <= DateTime.DaysInMonth(year, month)
            ? new DateOnly(year, month, day) : null;

    /// <summary>„šachy.cz Extraliga – 3. kolo" — der Zusatz macht aus dem Namen eine Runde.</summary>
    private static readonly Regex RoundPattern = new(
        @"^(?<series>.+?)\s*[-–—]\s*(?<n>\d{1,2})\.\s*kolo\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Was in dieser Liste steht, aber kein Turnier ist: Schulung (<c>skoleni</c>), Seminar,
    /// Trainingslager (<c>soustredeni</c>), Sitzung, Arbeitstreffen. Jeder fuenfte Eintrag.
    /// </summary>
    private static readonly Regex NonTournamentPattern = new(
        @"\b(skoleni|seminar\w*|soustredeni|schuze|pracovni setkani|konferenc\w*|valna hromada)",
        RegexOptions.Compiled);

    private static string? Empty(string? text) =>
        Collapse(text) is { Length: > 0 } value ? value : null;

    internal static void EnsureAllowedTarget(Uri url) => SourceHostGuard.Ensure(url, AllowedHost);
}
