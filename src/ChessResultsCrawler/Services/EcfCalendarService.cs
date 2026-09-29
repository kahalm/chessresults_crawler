using System.Globalization;
using System.Text.Json;
using System.Web;

namespace ChessResultsCrawler.Services;

/// <summary>Ein Turnier aus dem Kalender des englischen Verbands (ECF).</summary>
public class ParsedEcfEvent
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

    /// <summary>Land als NAME („United Kingdom", „France"), so wie die Quelle es fuehrt.</summary>
    public string? Country { get; set; }

    /// <summary>
    /// Koordinaten der Spielstaette — der Sonderfall dieser Quelle: sie liefert sie MIT. Bei zwei
    /// Dritteln der Turniere gesetzt; die uebrigen brauchen den normalen Weg ueber den Ortstext.
    /// </summary>
    public double? Lat { get; set; }
    public double? Lon { get; set; }

    /// <summary>
    /// Die Schlagworte der Quelle („FIDE Rated", „Juniors Only", „Online", „Meeting"). Sie sind
    /// gepflegt und beantworten Fragen, die sonst nur der Turniername andeutet.
    /// </summary>
    public List<string> Categories { get; set; } = [];

    public string? Website { get; set; }
}

/// <summary>
/// Der Turnierkalender des englischen Verbands (English Chess Federation).
///
/// <para><b>Warum diese Quelle.</b> Am 2026-09-08 gemessen: <b>256 kuenftige Turniere</b> bis Juli
/// 2027, und <b>222 davon (86 %) stehen nicht auf chess-results</b> — dort sind es im selben
/// Zeitraum 143. England ist damit eine der groessten Luecken im Bestand, und der Vergleich ist
/// hier besonders belastbar, weil beide Seiten gemessen wurden.</para>
///
/// <para><b>Sie ist die einzige Quelle mit KOORDINATEN.</b> Der Verband faehrt WordPress mit dem
/// Plugin „The Events Calendar", und dessen dokumentierte REST-Schnittstelle hat neben den
/// Terminen einen zweiten Endpunkt fuer die Spielstaetten — mit <c>address</c>, <c>city</c>,
/// <c>zip</c> und <c>geo_lat</c>/<c>geo_lng</c>. Fuer 168 der 256 Turniere entfaellt damit das
/// Geocoding vollstaendig.</para>
///
/// <para><b>Der Haken, und er kostet die Haelfte der Abrufe:</b> im Termin steht die Spielstaette
/// nur als STUMMEL (Nummer, Name, Adresse der Detailseite) — die Felder mit Anschrift und
/// Koordinaten gibt es ausschliesslich unter <c>/venues</c>. Also zwei Endpunkte, beide
/// geblaettert: 6 Seiten Termine und 12 Seiten Spielstaetten (596 insgesamt, davon 249 wirklich
/// benutzt). Einzeln nachzuschlagen waere teurer — das waeren 249 Abrufe statt 12.</para>
///
/// <para><b>Die Schlagworte sind gepflegt und wertvoll.</b> „Juniors Only" traegt 50 Turniere,
/// „Online" 28, „Meeting" 4 (das sind Sitzungen, keine Turniere). Diese Angaben stehen sonst
/// nirgends als Feld — bei allen anderen Quellen muessen sie aus dem Namen erraten werden.</para>
///
/// <para><b>Rechtslage (2026-09-08 geprueft) — und warum das hier eine eigene Ueberlegung war.</b>
/// Die <c>robots.txt</c> sagt zwei Dinge gleichzeitig: <c>User-agent: *</c> mit <c>Allow: /</c>
/// und <c>Content-Signal: search=yes,ai-train=no,use=reference</c>, zwei Zeilen darunter aber
/// <c>User-agent: ClaudeBot</c> mit <c>Disallow: /</c>. Fuer diesen Crawler ist das ein Ja —
/// er faellt unter <c>*</c>, <c>/wp-json/</c> ist nicht gesperrt, und ein Turnierkalender mit
/// Verweis auf die Quelle ist genau der Fall, den <c>use=reference</c> deckt. Trainiert wird
/// nichts. Der Betreiber dieses Stacks hat die Abrufe ausdruecklich freigegeben.</para>
/// </summary>
public class EcfCalendarService
{
    internal const string AllowedHost = "www.englishchess.org.uk";

    private const string EventsUrl = "https://www.englishchess.org.uk/wp-json/tribe/events/v1/events";
    private const string VenuesUrl = "https://www.englishchess.org.uk/wp-json/tribe/events/v1/venues";

    /// <summary>Die Seitengroesse, die die Schnittstelle wirklich liefert — groessere Werte ignoriert sie.</summary>
    internal const int PageSize = 50;

    /// <summary>Deckel je Endpunkt. Heute sind es 6 bzw. 12 Seiten.</summary>
    internal const int MaxPages = 25;

    /// <summary>Die Wartezeit, die die robots.txt der Quelle nennt.</summary>
    internal static readonly TimeSpan CrawlDelay = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http;
    private readonly ILogger<EcfCalendarService> _log;

    public EcfCalendarService(HttpClient http, ILogger<EcfCalendarService> log)
    {
        _http = http;
        _log = log;
    }

    public async Task<List<ParsedEcfEvent>> FetchAsync(DateOnly from, CancellationToken ct = default)
    {
        var events = new List<ParsedEcfEvent>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var venueIds = new Dictionary<string, int>(StringComparer.Ordinal);

        var pages = 1;
        for (var page = 1; page <= Math.Min(pages, MaxPages); page++)
        {
            if (page > 1) await Task.Delay(CrawlDelay, ct);
            var json = await GetAsync($"{EventsUrl}?per_page={PageSize}&page={page}", ct);
            if (json is null) break;

            var (parsed, totalPages) = ParseEvents(json);
            pages = Math.Max(pages, totalPages);

            foreach (var (e, venueId) in parsed)
            {
                if (e.EndDate < from || !seen.Add(e.EventId)) continue;
                events.Add(e);
                if (venueId is { } id) venueIds[e.EventId] = id;
            }
        }

        // Die Spielstaetten sind ein EIGENER Endpunkt — im Termin steht nur ihre Nummer.
        if (venueIds.Count > 0)
        {
            var venues = await FetchVenuesAsync(ct);
            foreach (var e in events)
            {
                if (!venueIds.TryGetValue(e.EventId, out var id)) continue;
                if (venues.TryGetValue(id, out var venue)) Apply(e, venue);
            }
        }

        _log.LogInformation("ECF-Kalender ab {From}: {Count} Turniere, {WithGeo} mit Koordinaten",
            from, events.Count, events.Count(e => e.Lat is not null));
        return [.. events.OrderBy(e => e.StartDate)];
    }

    private async Task<Dictionary<int, ParsedEcfVenue>> FetchVenuesAsync(CancellationToken ct)
    {
        var result = new Dictionary<int, ParsedEcfVenue>();
        var pages = 1;

        for (var page = 1; page <= Math.Min(pages, MaxPages); page++)
        {
            await Task.Delay(CrawlDelay, ct);
            var json = await GetAsync($"{VenuesUrl}?per_page={PageSize}&page={page}", ct);
            if (json is null) break;

            var (venues, totalPages) = ParseVenues(json);
            pages = Math.Max(pages, totalPages);
            foreach (var v in venues) result[v.Id] = v;
        }
        return result;
    }

    private async Task<string?> GetAsync(string url, CancellationToken ct)
    {
        var target = new Uri(url);
        EnsureAllowedTarget(target);

        using var response = await _http.GetAsync(target, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode) return body;

        _log.LogWarning("ECF: {Url} antwortete {Status}", url, (int)response.StatusCode);
        return null;
    }

    // ----- Termine -----------------------------------------------------------

    /// <summary>Eine Seite Termine. Die Spielstaetten-NUMMER kommt je Termin mit zurueck.</summary>
    internal static (List<(ParsedEcfEvent Event, int? VenueId)> Events, int TotalPages) ParseEvents(string json)
    {
        var results = new List<(ParsedEcfEvent, int?)>();
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
            var e = new ParsedEcfEvent
            {
                EventId = id.Value.ToString(CultureInfo.InvariantCulture),
                Name = title,
                StartDate = start.Value,
                EndDate = end is { } value && value >= start ? value : start.Value,
                Url = Text(row, "url") ?? "",
                Website = Empty(Text(row, "website")),
                Categories = Names(row, "categories"),
            };

            // Eine als „virtuell" markierte Veranstaltung hat keinen Spielort — dasselbe sagt
            // sonst das Schlagwort „Online".
            if (row.TryGetProperty("is_virtual", out var virt) && virt.ValueKind == JsonValueKind.True
                && !e.Categories.Contains("Online", StringComparer.OrdinalIgnoreCase))
            {
                e.Categories.Add("Online");
            }

            int? venueId = null;
            if (row.TryGetProperty("venue", out var venue) && venue.ValueKind == JsonValueKind.Object)
                venueId = Number(venue, "id");

            results.Add((e, venueId));
        }
        return (results, totalPages);
    }

    // ----- Spielstaetten -----------------------------------------------------

    internal sealed record ParsedEcfVenue(
        int Id, string? Name, string? Address, string? City, string? PostalCode, string? Country,
        double? Lat, double? Lon);

    internal static (List<ParsedEcfVenue> Venues, int TotalPages) ParseVenues(string json)
    {
        var results = new List<ParsedEcfVenue>();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return (results, 1);

        var totalPages = root.TryGetProperty("total_pages", out var tp) && tp.TryGetInt32(out var n)
            ? Math.Max(1, n) : 1;
        if (!root.TryGetProperty("venues", out var list) || list.ValueKind != JsonValueKind.Array)
            return (results, totalPages);

        foreach (var row in list.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (Number(row, "id") is not { } id) continue;

            results.Add(new ParsedEcfVenue(
                id, Decode(Text(row, "venue")), Decode(Text(row, "address")),
                Decode(Text(row, "city")), Empty(Text(row, "zip")), Empty(Text(row, "country")),
                Coordinate(row, "geo_lat", -90, 90), Coordinate(row, "geo_lng", -180, 180)));
        }
        return (results, totalPages);
    }

    /// <summary>
    /// Die Angaben der Spielstaette auf den Termin uebertragen.
    ///
    /// <para>Der Ortstext wird so zusammengesetzt, dass der ORTSNAME hinten steht und die
    /// Postleitzahl zuletzt: der Geocoder nimmt bei einem Text mit Ziffer den LETZTEN
    /// Ortstreffer, und das soll die Stadt sein, nicht der Strassenname.</para>
    /// </summary>
    internal static void Apply(ParsedEcfEvent e, ParsedEcfVenue venue)
    {
        e.City = venue.City;
        e.PostalCode = venue.PostalCode;
        e.Country = venue.Country;
        e.Lat = venue.Lat;
        e.Lon = venue.Lon;

        var structured = new[] { venue.Address, venue.City, venue.PostalCode }
            .Where(part => part is { Length: > 0 })
            .ToList();

        // Ohne strukturierte Felder traegt der NAME die ganze Anschrift („The Clissold Arms @
        // The Clissold Arms, 105 Fortis Green, London, N2 9HR") — dann ist er der Ortstext.
        e.Place = structured.Count > 0
            ? string.Join(", ", new[] { venue.Name }.Concat(structured).Where(p => p is { Length: > 0 }))
            : venue.Name;
    }

    // ----- Hilfen ------------------------------------------------------------

    private static List<string> Names(JsonElement row, string property)
    {
        var result = new List<string>();
        if (!row.TryGetProperty(property, out var list) || list.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (Decode(Text(item, "name")) is { Length: > 0 } name && !result.Contains(name))
                result.Add(name);
        }
        return result;
    }

    private static string? Text(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() : null;

    private static int? Number(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number) ? number : null;

    /// <summary>
    /// Eine Koordinate — die Quelle schickt sie als Zahl. Werte ausserhalb des Wertebereichs sind
    /// keine Koordinate, und 0/0 ist der Punkt im Golf von Guinea, an dem ein leeres Feld landet.
    /// </summary>
    private static double? Coordinate(JsonElement row, string name, double min, double max)
    {
        if (!row.TryGetProperty(name, out var value)) return null;

        double? number = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var d) => d,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
        return number is { } n && n >= min && n <= max && Math.Abs(n) > 0.0001 ? n : null;
    }

    /// <summary>
    /// Die Titel kommen mit HTML-Entitaeten („Women&amp;#038;s"). Ohne Aufloesung stuenden sie so
    /// im Kalender.
    /// </summary>
    private static string? Decode(string? text) =>
        text is { Length: > 0 } ? Empty(HttpUtility.HtmlDecode(text)) : null;

    private static string? Empty(string? text) =>
        text is { Length: > 0 } && text.Trim() is { Length: > 0 } value ? value : null;

    /// <summary>„2026-09-08 10:30:00" — die Uhrzeit interessiert den Kalender nicht.</summary>
    private static DateOnly? ParseDate(string? text) =>
        text is { Length: >= 10 }
        && DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date) ? date : null;

    internal static void EnsureAllowedTarget(Uri url) => SourceHostGuard.Ensure(url, AllowedHost);
}
