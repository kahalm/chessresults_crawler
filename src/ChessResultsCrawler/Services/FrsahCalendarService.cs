using System.Globalization;
using System.Text.Json;
using System.Web;

namespace ChessResultsCrawler.Services;

/// <summary>Ein Turnier aus dem Kalender des rumaenischen Verbands (FRSah).</summary>
public class ParsedFrsahEvent
{
    /// <summary>Die numerische Beitrags-Nummer der Quelle — eine echte, stabile Kennung.</summary>
    public string EventId { get; set; } = "";

    public string Name { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public string Url { get; set; } = "";

    /// <summary>Spielstaette samt Anschrift, zu einer Zeile zusammengezogen.</summary>
    public string? Place { get; set; }

    public string? City { get; set; }
    public string? PostalCode { get; set; }

    /// <summary>Land als NAME („Romania"), so wie die Quelle es fuehrt.</summary>
    public string? Country { get; set; }
}

/// <summary>
/// Der Turnierkalender des rumaenischen Verbands (Federația Română de Șah, frsah.ro).
///
/// <para><b>Warum diese Quelle.</b> Am 2026-09-09 gemessen: <b>31 kuenftige Turniere</b>
/// (2026-09-12 bis 2026-11-30), davon rund <b>ein Drittel nicht auf chess-results</b> — dort
/// stehen im selben Zeitraum 73 Eintraege fuer ROU. Was fehlt, sind vor allem die KOMPLETTEN
/// nationalen Mannschaftsligen (Superliga, Divizia A, Finale) — chess-results kennt sie nicht,
/// weil sie nie als Swiss-Manager-Datei hochgeladen werden.</para>
///
/// <para><b>Derselbe Verbandsbaukasten wie England.</b> FRSah faehrt WordPress mit demselben
/// Plugin „The Events Calendar" wie die ECF — dieselbe REST-Route, dasselbe JSON-Schema. Der
/// Unterschied liegt in der TIEFE der Daten: waehrend England die Spielstaette nur als NUMMER im
/// Termin fuehrt und die Anschrift erst der zweite Endpunkt (<c>/venues</c>) liefert, steht sie
/// hier schon VOLLSTAENDIG eingebettet im Termin selbst — ein zweiter Abruf entfaellt komplett
/// (nachgemessen: derselbe Datensatz steht unter <c>/venues?...</c> identisch, nur eben schon da,
/// wo er gebraucht wird). EIN Abruf traegt damit den ganzen Kalender.</para>
///
/// <para><b>Was diese Quelle NICHT liefert, anders als England:</b> keine Koordinaten (kein
/// <c>geo_lat</c>/<c>geo_lng</c> in keinem der gemessenen Datensaetze), keine Postleitzahl als
/// eigenes Feld und keine gepflegten Schlagworte (<c>categories</c>/<c>tags</c> sind bei allen 31
/// Turnieren leer). Die Verortung geht deshalb den normalen Weg ueber den Ortstext und das
/// Ortslexikon — bei 26 von 31 Turnieren ist die Spielstaette immerhin als Freitext vorhanden
/// (84 %), bei fuenf (den Mannschaftsmeisterschaften ohne festen Austragungsort) fehlt sie
/// ganz.</para>
///
/// <para><b>Rechtslage (2026-09-09 geprueft) — und ein Befund, der vermerkt gehoert, nicht
/// angenommen.</b> <c>GET https://frsah.ro/robots.txt</c> antwortet mit <b>403 Forbidden</b>,
/// waehrend Inhalt und REST-API klaglos 200 liefern — die Datei ist also nicht LESBAR, nicht
/// „leer" und nicht „verbietend", sondern schlicht nicht erreichbar. RFC 9309 regelt genau diesen
/// Fall: ein 4xx-Status auf die robots.txt bedeutet „keine Einschraenkungen", der Crawler darf
/// also zugreifen. Danach wird hier gehandelt — aber der Befund steht bewusst im Code, damit
/// niemand spaeter annimmt, die Regel sei gelesen und erlaubend ausgefallen. Es gibt keine
/// genannte Wartezeit; <see cref="CrawlDelay"/> ist eine Selbstbeschraenkung wie bei anderen
/// kleinen Verbandsseiten ohne robots.txt-Vorgabe.</para>
/// </summary>
public class FrsahCalendarService
{
    internal const string AllowedHost = "frsah.ro";

    private const string EventsUrl = "https://frsah.ro/wp-json/tribe/events/v1/events";

    /// <summary>Die Seitengroesse, die die Schnittstelle wirklich liefert — groessere Werte ignoriert sie.</summary>
    internal const int PageSize = 50;

    /// <summary>Deckel je Endpunkt. Heute genuegt eine einzige Seite (31 Turniere).</summary>
    internal const int MaxPages = 25;

    /// <summary>
    /// Wartezeit zwischen zwei Seitenabrufen. Die robots.txt nennt keine (siehe Klassenkommentar
    /// zur Rechtslage) — dies ist eine Selbstbeschraenkung fuer eine kleine Verbandsseite.
    /// </summary>
    internal static readonly TimeSpan CrawlDelay = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;
    private readonly ILogger<FrsahCalendarService> _log;

    public FrsahCalendarService(HttpClient http, ILogger<FrsahCalendarService> log)
    {
        _http = http;
        _log = log;
    }

    public async Task<List<ParsedFrsahEvent>> FetchAsync(DateOnly from, CancellationToken ct = default)
    {
        var events = new List<ParsedFrsahEvent>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var pages = 1;
        for (var page = 1; page <= Math.Min(pages, MaxPages); page++)
        {
            if (page > 1) await Task.Delay(CrawlDelay, ct);
            var json = await GetAsync($"{EventsUrl}?per_page={PageSize}&page={page}", ct);
            if (json is null) break;

            var (parsed, totalPages) = ParseEvents(json);
            pages = Math.Max(pages, totalPages);

            foreach (var e in parsed)
            {
                if (e.EndDate < from || !seen.Add(e.EventId)) continue;
                events.Add(e);
            }
        }

        _log.LogInformation("FRSah-Kalender ab {From}: {Count} Turniere, {WithPlace} mit Spielort",
            from, events.Count, events.Count(e => e.Place is { Length: > 0 }));
        return [.. events.OrderBy(e => e.StartDate)];
    }

    private async Task<string?> GetAsync(string url, CancellationToken ct)
    {
        var target = new Uri(url);
        EnsureAllowedTarget(target);

        using var response = await _http.GetAsync(target, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode) return body;

        _log.LogWarning("FRSah: {Url} antwortete {Status}", url, (int)response.StatusCode);
        return null;
    }

    // ----- Termine -------------------------------------------------------------

    /// <summary>
    /// Eine Seite Termine. Anders als bei England ist die Spielstaette schon VOLLSTAENDIG
    /// eingebettet — ein zweiter Abruf entfaellt.
    /// </summary>
    internal static (List<ParsedFrsahEvent> Events, int TotalPages) ParseEvents(string json)
    {
        var results = new List<ParsedFrsahEvent>();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return (results, 1);

        var totalPages = root.TryGetProperty("total_pages", out var tp) && tp.TryGetInt32(out var n)
            ? Math.Max(1, n) : 1;
        if (!root.TryGetProperty("events", out var list) || list.ValueKind != JsonValueKind.Array)
            return (results, totalPages);

        foreach (var row in list.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;

            var id = Number(row, "id");
            var title = Decode(Text(row, "title"));
            var start = ParseDate(Text(row, "start_date"));
            if (id is null || title is not { Length: > 0 } || start is null) continue;

            var end = ParseDate(Text(row, "end_date"));
            var e = new ParsedFrsahEvent
            {
                EventId = id.Value.ToString(CultureInfo.InvariantCulture),
                Name = title,
                StartDate = start.Value,
                EndDate = end is { } value && value >= start ? value : start.Value,
                Url = Text(row, "url") ?? "",
            };

            if (row.TryGetProperty("venue", out var venue) && venue.ValueKind == JsonValueKind.Object)
                ApplyVenue(e, venue);

            results.Add(e);
        }
        return (results, totalPages);
    }

    /// <summary>
    /// Die eingebetteten Spielstaetten-Felder auf den Termin uebertragen.
    ///
    /// <para>Der Ortstext wird so zusammengesetzt, dass der ORTSNAME hinten steht: der Geocoder
    /// nimmt bei einem Text mit Ziffer (Adresse statt Ortsliste) den LETZTEN Ortstreffer, und das
    /// soll die Stadt sein, nicht der Strassenname.</para>
    ///
    /// <para>Bei 25 von 26 gemessenen Spielstaetten stehen <c>address</c>/<c>city</c>/
    /// <c>province</c> leer und der NAME traegt die ganze Anschrift („CATTIA … — Strada
    /// Institutului 35, 500484 Brașov") — dann ist er der Ortstext.</para>
    /// </summary>
    internal static void ApplyVenue(ParsedFrsahEvent e, JsonElement venue)
    {
        var name = Decode(Text(venue, "venue"));
        var address = Decode(Text(venue, "address"));
        var city = Decode(Text(venue, "city"));
        var province = Decode(Text(venue, "province"));
        var postalCode = Empty(Text(venue, "zip"));

        e.City = city;
        e.PostalCode = postalCode;
        e.Country = Decode(Text(venue, "country"));

        var structured = new[] { address, city, province, postalCode }
            .Where(part => part is { Length: > 0 })
            .ToList();

        e.Place = structured.Count > 0
            ? string.Join(", ", new[] { name }.Concat(structured).Where(p => p is { Length: > 0 }))
            : name;
    }

    // ----- Hilfen ------------------------------------------------------------

    private static string? Text(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() : null;

    private static int? Number(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number) ? number : null;

    /// <summary>
    /// Die Titel und Ortsnamen kommen mit HTML-Entitaeten („Ediția a IV-a &amp;#8211; Etapa").
    /// Ohne Aufloesung stuenden sie so im Kalender.
    /// </summary>
    private static string? Decode(string? text) =>
        text is { Length: > 0 } ? Empty(HttpUtility.HtmlDecode(text)) : null;

    private static string? Empty(string? text) =>
        text is { Length: > 0 } && text.Trim() is { Length: > 0 } value ? value : null;

    /// <summary>„2026-09-12 00:00:00" — die Uhrzeit interessiert den Kalender nicht.</summary>
    private static DateOnly? ParseDate(string? text) =>
        text is { Length: >= 10 }
        && DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date) ? date : null;

    internal static void EnsureAllowedTarget(Uri url) => SourceHostGuard.Ensure(url, AllowedHost);
}
