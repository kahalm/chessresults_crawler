using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using static ChessResultsCrawler.Services.SourceText;

namespace ChessResultsCrawler.Services;

/// <summary>Ein Turnier aus dem Kalender der Irish Chess Union.</summary>
public class ParsedIcuEvent
{
    /// <summary>Die Nummer hinter <c>/events/</c> — eine echte, stabile Kennung.</summary>
    public string EventId { get; set; } = "";

    public string Name { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public string Url { get; set; } = "";

    /// <summary>
    /// Der Spielort, wie die Trefferliste ihn fuehrt — bei dieser Quelle oft die VOLLE Anschrift
    /// samt Postleitzahl („Clayton Hotel Sligo, Clarion Road, Ballytivnan, Ballinode, Co. Sligo,
    /// F91 N8EF"). <c>null</c>, wenn dort nur „TBA" steht.
    /// </summary>
    public string? Place { get; set; }

    /// <summary>
    /// Koordinaten der Spielstaette — aus dem Kartenblock DERSELBEN Seite, also ohne eigenen
    /// Abruf. Fuer 30 der 81 Turniere gesetzt; die uebrigen Spielstaetten hat die Quelle selbst
    /// nicht verortet.
    /// </summary>
    public double? Lat { get; set; }
    public double? Lon { get; set; }

    /// <summary>
    /// Die Schlagworte der Zeile: Bedenkzeit-Klasse („Classical", „Rapid", „Blitz"), dazu
    /// „FIDE-rated", „Women only", „Foreign", „Junior International".
    /// </summary>
    public List<string> Categories { get; set; } = [];

    /// <summary>
    /// Der Eintrag ist nach seinem NAMEN gar kein Turnier, sondern Unterricht, ein Lehrgang oder
    /// eine Sitzung. Die Quelle fuehrt beides in derselben Liste und hat kein Feld, das sie
    /// trennt — am 2026-09-09 waren es 5 von 81 („Chess Lessons Dublin Chess Club (DCC) 2026",
    /// „Greystones Chess Academy - Autumn 2026 Lessons", „National Arbiters Course and Running
    /// Live Boards", „High Level training with GM Ivan Sokolov", „AGM 2026"). Die beiden
    /// Unterrichtsreihen laufen ueber 78 bzw. 84 Tage und verdeckten im Kalender ein Vierteljahr.
    /// </summary>
    public bool NonTournament { get; set; }
}

/// <summary>
/// Was die DETAILseite eines Turniers ueber die Trefferliste hinaus hergibt — gemessen an 28
/// echten Seiten, und bewusst wenig: alles andere steht schon in der Liste.
/// </summary>
public class ParsedIcuDetail
{
    /// <summary>
    /// Die chess-results-Nummer, wenn die Seite unter „Links" dorthin verweist. Der einzige
    /// EXAKTE Zuordnungsschluessel dieser Quelle — aber selten: 2 von 28 (7 %).
    /// </summary>
    public string? ChessResultsId { get; set; }

    /// <summary>
    /// Die Zahl der bereits gemeldeten Spieler („28 entries"). 6 von 28 Seiten nennen sie —
    /// neben chessarbiter die einzige Quelle, die vor dem Turnier ueberhaupt eine Zahl hat.
    /// </summary>
    public int? PlayerCount { get; set; }

    /// <summary>Die Turnierseite des Veranstalters, wenn die Seite einen „Website"-Knopf hat.</summary>
    public string? Website { get; set; }
}

/// <summary>
/// Der Turnierkalender der Irish Chess Union (icu.ie).
///
/// <para><b>Warum diese Quelle.</b> Am 2026-09-09 gemessen: <b>81 kuenftige Turniere</b> bis
/// Oktober 2030, gegen 19 fuer IRL auf chess-results — rund 94 % fehlen dort. Irland faehrt sein
/// Turnierwesen ueber den eigenen Kalender; chess-results bekommt hoechstens die Paarungen, und
/// auch das nur bei den groesseren.</para>
///
/// <para><b>Die Liste traegt fast alles, und das ist der Grund, warum sie billig ist.</b> Fuenf
/// Abrufe (20 Zeilen je Seite, „1-20 of 81") bringen Name, Termin, Bedenkzeit-Klasse,
/// FIDE-Wertung, die Publikums-Schlagworte UND den Ortstext — der hier oft die volle Anschrift
/// mit Eircode ist (15 von 81) statt eines blossen Ortsnamens. Ein JSON-Feed gibt es nicht
/// (<c>/events.json</c> und <c>/events.ics</c> antworten 406), aber die Seite ist serverseitig
/// gerendert und braucht kein JavaScript.</para>
///
/// <para><b>Die KOORDINATEN stehen in derselben Seite.</b> Die Kartenansicht bekommt ihre Marken
/// als eingebettetes JSON (<c>&lt;script id="map-data"&gt;</c>), und jede Marke nennt in ihrem
/// vorgerenderten Popup-Text den Verweis auf ihr Turnier — damit sind sie zuordenbar. 30 der 81
/// Turniere sind so ohne jeden Zusatzabruf verortet. Die Karte zeigt je Seite hoechstens 20
/// Marken, aber ab dem ERSTEN Turnier der jeweiligen Seite; ueber alle Seiten gelesen ist die
/// Vereinigung vollstaendig (nachgemessen: die 17 Detailseiten mit Koordinaten waren alle schon
/// dabei).</para>
///
/// <para><b>Warum die Detailseite trotzdem existiert — und warum sie wenig bringt.</b> An 28
/// echten Seiten gemessen: chess-results-Nummer 2-mal (7 %), Teilnehmerzahl 6-mal (21 %),
/// Koordinaten <b>0-mal zusaetzlich</b>. Rundenzahl und Bedenkzeit stehen nur im Fliesstext der
/// Ausschreibung („7 Rounds, Time Control 20m+2s") und werden bewusst nicht ausgelesen — das ist
/// Werbetext, keine Spalte. Ein Abruf je Turnier lohnt deshalb nur EINMAL und nur fuer neue
/// Turniere; der Aufrufer entscheidet das, wie bei chessarbiter.</para>
///
/// <para><b>Rechtslage (2026-09-09 geprueft).</b> Die <c>robots.txt</c> enthaelt ausschliesslich
/// Kommentarzeilen — die Beispiel-Sperre ist ausdruecklich AUSkommentiert („To ban all spiders
/// from the entire site uncomment the next two lines"). Es gibt also keine Regel, die etwas
/// verbietet, und keine genannte Wartezeit; gewartet wird hier trotzdem
/// (<see cref="CrawlDelay"/>).</para>
/// </summary>
public class IcuCalendarService
{
    internal const string AllowedHost = "www.icu.ie";

    private const string ListUrl = "https://www.icu.ie/events";
    private const string EventUrl = "https://www.icu.ie/events/";

    /// <summary>Zeilen je Seite — was die Quelle liefert, nicht was wir wuenschen.</summary>
    internal const int PageSize = 20;

    /// <summary>
    /// Deckel fuer die Blaetterei. Heute sind es 5 Seiten; der Deckel faengt den Fall ab, dass
    /// die „next"-Verweise im Kreis zeigen.
    /// </summary>
    internal const int MaxPages = 20;

    /// <summary>
    /// Wartezeit zwischen zwei Abrufen. Die Quelle nennt keine — dies ist eine
    /// Selbstbeschraenkung fuer eine kleine Verbandsseite.
    /// </summary>
    internal static readonly TimeSpan CrawlDelay = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;
    private readonly ILogger<IcuCalendarService> _log;

    public IcuCalendarService(HttpClient http, ILogger<IcuCalendarService> log)
    {
        _http = http;
        _log = log;
    }

    /// <summary>
    /// Die Trefferliste ab <paramref name="from"/>, ueber alle Seiten.
    ///
    /// <para>Der Stichtag geht als <c>year</c>/<c>month</c> mit, weil die Quelle genau so
    /// filtert — und weil ihre Datumsspalte das Jahr WEGLAESST, solange es das laufende ist
    /// („12 Sep", aber „17 Jan 2027"). Das abgefragte Jahr ist damit die Vorgabe fuer jede
    /// jahreslose Zeile; die Kartenmarken bestaetigen das (ihr Popup nennt „12 Sep 2026").</para>
    /// </summary>
    public async Task<List<ParsedIcuEvent>> FetchAsync(DateOnly from, CancellationToken ct = default)
    {
        var events = new List<ParsedIcuEvent>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var coordinates = new Dictionary<string, (double Lat, double Lon)>(StringComparer.Ordinal);

        for (var page = 1; page <= MaxPages; page++)
        {
            if (page > 1) await Task.Delay(CrawlDelay, ct);

            var html = await GetAsync(
                $"{ListUrl}?year={from.Year}&month={from.Month:00}&page={page}", ct);
            if (html is null) break;

            var (rows, hasNext) = await ParseListAsync(html, from.Year);
            foreach (var row in rows)
            {
                if (row.EndDate < from || !seen.Add(row.EventId)) continue;
                events.Add(row);
            }

            // Die Karte derselben Seite traegt die Koordinaten — sie liegen NICHT parallel zu den
            // Zeilen (hoechstens 20 Marken, aber ab dem ersten Turnier dieser Seite), deshalb wird
            // ueber die Turnier-Nummer zusammengefuehrt und nicht ueber die Reihenfolge.
            foreach (var (id, point) in ParseMapData(html)) coordinates[id] = point;

            if (!hasNext) break;
        }

        foreach (var row in events)
        {
            if (!coordinates.TryGetValue(row.EventId, out var point)) continue;
            row.Lat = point.Lat;
            row.Lon = point.Lon;
        }

        _log.LogInformation("ICU-Kalender ab {From}: {Count} Turniere, {WithGeo} mit Koordinaten",
            from, events.Count, events.Count(e => e.Lat is not null));
        return [.. events.OrderBy(e => e.StartDate)];
    }

    /// <summary>
    /// Die Detailseite EINES Turniers. <c>null</c>, wenn sie nicht lesbar ist — ein Fehlschlag
    /// hier ist kein Grund, den Durchgang abzubrechen.
    /// </summary>
    public async Task<ParsedIcuDetail?> FetchDetailAsync(string eventId, CancellationToken ct = default)
    {
        if (!IdPattern.IsMatch(eventId)) return null;

        var html = await GetAsync($"{EventUrl}{eventId}", ct);
        return html is null ? null : await ParseDetailAsync(html);
    }

    private async Task<string?> GetAsync(string url, CancellationToken ct)
    {
        var target = new Uri(url);
        EnsureAllowedTarget(target);

        using var response = await _http.GetAsync(target, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode) return body;

        _log.LogWarning("ICU: {Url} antwortete {Status}", url, (int)response.StatusCode);
        return null;
    }

    // ----- Trefferliste ------------------------------------------------------

    /// <summary>
    /// Eine Trefferseite auseinandernehmen. Tabelle <c>#results</c>, Spalten
    /// <c>Dates | Event name | Location</c>; die Fusszeile („1-20 of 81 ∙ next ∙ last") sagt, ob
    /// es weitergeht.
    ///
    /// <para><paramref name="defaultYear"/> gilt fuer jede Zeile OHNE Jahresangabe — siehe
    /// <see cref="ParseDateRange"/>.</para>
    /// </summary>
    internal static async Task<(List<ParsedIcuEvent> Events, bool HasNext)> ParseListAsync(
        string html, int defaultYear)
    {
        var results = new List<ParsedIcuEvent>();
        var context = BrowsingContext.New(Configuration.Default);
        var document = await context.OpenAsync(req => req.Content(html));

        var table = document.QuerySelector("#results") ?? document.QuerySelector("table");
        if (table is null) return (results, false);

        foreach (var row in table.QuerySelectorAll("tbody tr"))
        {
            var link = row.QuerySelector("td a[href^='/events/']");
            var id = link is null ? null : IdOf(link.GetAttribute("href"));
            if (id is null) continue;

            var cells = row.QuerySelectorAll("td");
            if (cells.Length < 3) continue;

            var name = Collapse(link.TextContent);
            if (name.Length == 0) continue;

            if (ParseDateRange(cells[0].TextContent, defaultYear) is not var (start, end)) continue;

            results.Add(new ParsedIcuEvent
            {
                EventId = id,
                Name = name,
                StartDate = start,
                EndDate = end,
                Url = $"{EventUrl}{id}",
                Place = PlaceOf(cells[^1].TextContent),
                Categories = CategoriesOf(row),
                NonTournament = NonTournamentPattern.IsMatch(name),
            });
        }

        // „next" gibt es nur, solange eine Seite folgt — auf der letzten fehlt die ganze Fusszeile.
        var hasNext = table.QuerySelectorAll("a[href*='page=']")
            .Any(a => Collapse(a.TextContent).Equals("next", StringComparison.OrdinalIgnoreCase));

        return (results, hasNext);
    }

    /// <summary>
    /// Die Koordinaten aus dem Kartenblock derselben Seite, je Turnier-Nummer.
    ///
    /// <para>Die Marke nennt ihr Turnier nicht als Feld — nur ihr vorgerenderter Popup-Text
    /// enthaelt den Verweis <c>/events/{id}</c>. Daraus wird zugeordnet; eine Marke ohne diesen
    /// Verweis ist nicht verwertbar und faellt weg.</para>
    /// </summary>
    internal static Dictionary<string, (double Lat, double Lon)> ParseMapData(string html)
    {
        var result = new Dictionary<string, (double, double)>(StringComparer.Ordinal);

        var block = MapDataPattern.Match(html);
        if (!block.Success) return result;

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(block.Groups[1].Value);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return result;
        }

        if (root.ValueKind != JsonValueKind.Object) return result;
        if (!root.TryGetProperty("markers", out var markers)
            || markers.ValueKind != JsonValueKind.Array) return result;

        foreach (var marker in markers.EnumerateArray())
        {
            if (marker.ValueKind != JsonValueKind.Object) continue;

            var popup = marker.TryGetProperty("popupHtml", out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString() : null;
            if (popup is null || EventLinkPattern.Match(popup) is not { Success: true } link) continue;

            if (Coordinate(marker, "lat", -90, 90) is not { } lat) continue;
            if (Coordinate(marker, "lng", -180, 180) is not { } lon) continue;

            result[link.Groups[1].Value] = (lat, lon);
        }
        return result;
    }

    /// <summary>
    /// Die Datumsspalte. Sie ist kurz und laesst weg, was sich aus dem Rest ergibt:
    /// <c>12 Sep</c> · <c>12–13 Sep</c> (ein Monat, einmal genannt) ·
    /// <c>16 Sep – 2 Dec</c> · <c>2–5 Jan 2027</c> · <c>29 Jan – 7 Feb 2027</c>.
    ///
    /// <para><b>Das Jahr fehlt, solange es das laufende ist</b> — steht eines da, gehoert es zum
    /// ENDdatum (es steht ganz hinten). Daraus die zwei Regeln: mit Jahresangabe rechnet der
    /// Anfang zurueck (Dez–Jan heisst, der Anfang lag im Vorjahr), ohne Jahresangabe rechnet das
    /// Ende vor. Beides ist noetig, sonst kippt ein Jahreswechsel innerhalb eines Zeitraums um
    /// zwoelf Monate.</para>
    /// </summary>
    internal static (DateOnly Start, DateOnly End)? ParseDateRange(string? text, int defaultYear)
    {
        var match = DateRangePattern.Match(Collapse(text));
        if (!match.Success) return null;

        var startDay = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var endDay = match.Groups[3].Success
            ? int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) : startDay;

        // „12–13 Sep" nennt den Monat nur einmal, und zwar hinten.
        var endMonth = MonthOf(match.Groups[4].Success ? match.Groups[4].Value : match.Groups[2].Value);
        var startMonth = match.Groups[2].Success ? MonthOf(match.Groups[2].Value) : endMonth;
        if (startMonth is null || endMonth is null) return null;

        int startYear, endYear;
        if (match.Groups[5].Success)
        {
            endYear = int.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture);
            startYear = startMonth > endMonth ? endYear - 1 : endYear;
        }
        else
        {
            startYear = defaultYear;
            endYear = endMonth < startMonth ? startYear + 1 : startYear;
        }

        var start = TryDate(startYear, startMonth.Value, startDay);
        var end = TryDate(endYear, endMonth.Value, endDay);
        if (start is null || end is null || end < start) return null;

        return (start.Value, end.Value);
    }

    // ----- Detailseite -------------------------------------------------------

    /// <summary>
    /// Die Detailseite. Gelesen werden genau die drei Angaben, die die Trefferliste nicht hat —
    /// alles Weitere dort (Preisgeld, Ausschreibungstext, Meldeliste) gehoert in keinen
    /// Turnierkalender.
    /// </summary>
    internal static async Task<ParsedIcuDetail> ParseDetailAsync(string html)
    {
        var context = BrowsingContext.New(Configuration.Default);
        var document = await context.OpenAsync(req => req.Content(html));

        return new ParsedIcuDetail
        {
            // Die Nummer steht im Verweis unter „Links" — mal auf chess-results.com, mal auf
            // einen ihrer Spiegel („s2.chess-results.com"), mal mit grossem „Tnr".
            ChessResultsId = document.QuerySelectorAll("a[href]")
                .Select(a => ChessResultsPattern.Match(a.GetAttribute("href") ?? ""))
                .FirstOrDefault(m => m.Success)?.Groups[1].Value,
            PlayerCount = EntriesOf(document),
            Website = document.QuerySelectorAll("a[href^='http']")
                .FirstOrDefault(a => Collapse(a.TextContent)
                    .Equals("Website", StringComparison.OrdinalIgnoreCase))
                ?.GetAttribute("href"),
        };
    }

    /// <summary>„28 entries" im Kopf der Meldeliste — die einzige Zahl, die es vorher gibt.</summary>
    private static int? EntriesOf(IDocument document)
    {
        foreach (var span in document.QuerySelectorAll("span.text-muted"))
        {
            var m = EntriesPattern.Match(Collapse(span.TextContent));
            if (m.Success && int.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out var n))
                return n;
        }
        return null;
    }

    // ----- Hilfen ------------------------------------------------------------

    /// <summary>
    /// Die Schlagworte einer Zeile. Sie stehen als <c>&lt;span&gt;</c> in
    /// <c>.event-metadata</c> — MEHRERE Bedenkzeit-Klassen aber in EINEM, durch Komma getrennt
    /// („Rapid, Blitz"), deshalb wird zusaetzlich am Komma zerlegt.
    /// </summary>
    private static List<string> CategoriesOf(IElement row)
    {
        var result = new List<string>();
        foreach (var span in row.QuerySelectorAll(".event-metadata span"))
        {
            foreach (var part in span.TextContent.Split(','))
            {
                var value = Collapse(part);
                if (value.Length > 0 && !result.Contains(value, StringComparer.OrdinalIgnoreCase))
                    result.Add(value);
            }
        }
        return result;
    }

    /// <summary>
    /// Der Ortstext. „TBA" ist kein Ort, sondern die Ansage, dass es noch keinen gibt — 20 der 81
    /// Zeilen stehen so da (Platzhalter fuer Meisterschaften bis 2030). Steht daneben doch etwas
    /// („TBA - Dublin"), bleibt dieser Rest stehen: Dublin ist eine Auskunft.
    /// </summary>
    internal static string? PlaceOf(string? text)
    {
        var value = Collapse(text);
        if (value.Length == 0) return null;

        var stripped = Collapse(TbaPattern.Replace(value, " ")).Trim(' ', ',', '-', '–', ':');
        return stripped.Length == 0 ? null : Collapse(stripped);
    }

    private static string? IdOf(string? href)
    {
        var m = EventLinkPattern.Match(href ?? "");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static double? Coordinate(JsonElement row, string name, double min, double max)
    {
        if (!row.TryGetProperty(name, out var value)) return null;

        // Die Quelle schickt die Koordinaten als ZEICHENKETTE („53.5242079"), nicht als Zahl.
        double? number = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var d) => d,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };

        // 0/0 liegt im Golf von Guinea — dort landet ein leeres Feld, nicht ein Turnier.
        return number is { } n && n >= min && n <= max && Math.Abs(n) > 0.0001 ? n : null;
    }

    private static int? MonthOf(string? name)
    {
        var value = Collapse(name).ToLowerInvariant();
        if (value.Length < 3) return null;

        var index = Array.IndexOf(Months, value[..3]);
        return index < 0 ? null : index + 1;
    }

    private static readonly string[] Months =
        ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    private static DateOnly? TryDate(int year, int month, int day) =>
        year is >= 1900 and <= 2200 && day >= 1 && day <= DateTime.DaysInMonth(year, month)
            ? new DateOnly(year, month, day) : null;

    private static readonly Regex IdPattern = new(@"^\d{1,9}$", RegexOptions.Compiled);

    private static readonly Regex EventLinkPattern =
        new(@"/events/(\d{1,9})\b", RegexOptions.Compiled);

    private static readonly Regex MapDataPattern = new(
        """<script id="map-data" type="application/json">(.*?)</script>""",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>Der Bindestrich ist ein Halbgeviertstrich, mit oder ohne Leerzeichen darum.</summary>
    private static readonly Regex DateRangePattern = new(
        @"^(\d{1,2})\s*(?:([A-Za-z]{3,9})\s*)?(?:[–—-]\s*(\d{1,2})\s+([A-Za-z]{3,9})\s*)?(\d{4})?$",
        RegexOptions.Compiled);

    private static readonly Regex ChessResultsPattern =
        new(@"chess-results\.com/tnr(\d{1,12})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex EntriesPattern =
        new(@"^(\d{1,6})\s+entr(?:y|ies)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TbaPattern =
        new(@"\b(?:TBA|TBC|TBD)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Was in dieser Liste steht, aber kein Turnier ist: Unterricht („Lessons"), Lehrgaenge
    /// („National Arbiters Course"), Training („High Level training with GM Ivan Sokolov") und
    /// die Jahreshauptversammlung („AGM 2026"). Gesucht wird nach GANZEN Woertern — „Coursework"
    /// oder ein Turnier namens „Trainingsopen" soll nicht mit herausfallen.
    /// </summary>
    private static readonly Regex NonTournamentPattern = new(
        @"\b(lessons?|course|training|seminar|workshop|webinar|agm|annual general meeting)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static void EnsureAllowedTarget(Uri url) => SourceHostGuard.Ensure(url, AllowedHost);
}
