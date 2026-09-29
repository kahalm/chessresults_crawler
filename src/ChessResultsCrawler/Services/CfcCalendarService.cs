using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChessResultsCrawler.Services;

/// <summary>Ein Termin aus dem Ankuendigungskalender der Chess Federation of Canada.</summary>
public class ParsedCfcEvent
{
    /// <summary>
    /// Aus Termin, Ort und Name gebildet — die Quelle hat KEINE stabile Kennung (siehe
    /// <see cref="CfcCalendarService.EventKeyOf"/>).
    /// </summary>
    public string EventId { get; set; } = "";

    public string Name { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }

    /// <summary>Ort ohne Anschrift — die Quelle fuehrt keine Strasse und keine Postleitzahl.</summary>
    public string? Place { get; set; }

    /// <summary>Provinz-Kuerzel (ON, BC, QC …) — bei uns der Bundesland-Wert.</summary>
    public string? Province { get; set; }

    /// <summary>Meist eine FREMDE Seite (Verein, Formular, PDF) — kein eigenes Permalink.</summary>
    public string? Url { get; set; }
}

/// <summary>
/// Der Ankuendigungskalender der Chess Federation of Canada (chess.ca).
///
/// <para><b>Warum diese Quelle lohnt.</b> chess-results kennt fuer Kanada 146 Eintraege und davon
/// 68 kuenftige; dieser Kalender fuehrt 173 Eintraege, <b>171 davon kuenftig</b> (gemessen
/// 2026-09-10). Rund hundert Turniere mehr fuer EINEN Abruf — Kanada ist eines der Laender, in
/// denen chess-results praktisch nicht als Kalender benutzt wird.</para>
///
/// <para><b>Der Zugang ist zweistufig, und das ist Pflicht, nicht Bequemlichkeit.</b> Die Seite
/// <c>/en/events/</c> rendert nur ein leeres Svelte-Geruest, verweist aber auf eine STATISCHE
/// Datei <c>/ext/cfc-data.&lt;hash&gt;.js</c> mit dem kompletten Datensatz als
/// <c>window.ws_cfc_data = {…};</c>. Ein Abruf, kein JavaScript, keine Schnittstelle. <b>Der Hash
/// wechselt bei jedem Site-Build</b> — er darf nie hartkodiert werden, sonst faellt die Quelle beim
/// naechsten Deploy des Verbands still aus.</para>
///
/// <para><b>robots.txt (A)/(B)</b>: <c>www.chess.ca/robots.txt</c> ist woertlich nur
/// <c>User-agent: *</c> — keine Disallow-Zeile, kein <c>Crawl-delay</c>, kein
/// <c>Content-Signal</c>. Beide Fragen also „ja". (<c>forums.chess.ca</c> sperrt dagegen die
/// KI-Crawler mit Art.-4-Vorbehalt — dort liegen aber nur Freitext-Threads, keine Turnierdaten,
/// und dieser Dienst ruft den Host nicht auf.)</para>
///
/// <para><b>Die Kennung ist der schwierige Teil, und sie ist gemessen.</b> <c>oid</c> ist nur die
/// LISTENPOSITION (1..N, belegt an zwei Archivaufnahmen, beide beginnen wieder bei 1), <c>url</c>
/// zeigt bei 77 von 173 Eintraegen auf dieselbe Veranstalterseite. Gezaehlt wurde deshalb, wie
/// eindeutig die Alternativen sind:
/// <code>
/// start|end|city        160 von 173 eindeutig  (13 Kollisionen)
/// start|end|city|name   172 von 173 eindeutig  ( 1 Kollision)
/// </code>
/// Termin und Ort GENUEGEN NICHT — am 12.09. stehen zwei Turniere in Mississauga, am 20.09. zwei
/// in Markham, am 27.09. zwei in Thornhill. Anders als in Wales gibt es hier keine Anschrift, die
/// sie trennen koennte, also muss der NAME in den Schluessel. Der Preis ist bekannt: wird ein
/// Tippfehler im Namen spaeter korrigiert, entsteht ein neuer Eintrag und der alte laeuft in die
/// Verschwunden-Erkennung. Das ist hier das kleinere Uebel gegenueber zwei Turnieren, die zu einem
/// verschmelzen.</para>
///
/// <para><b>Die eine verbleibende Kollision ist eine Dublette der QUELLE</b>, kein Fehler des
/// Schluessels: „Vancouver Chess Festival #16" steht zweimal im Datensatz, mit identischem Termin,
/// Ort und Link — nur <c>oid</c> unterscheidet sich (109 und 110). Der Schluessel fuehrt die beiden
/// zusammen, und das ist richtig.</para>
///
/// <para><b>Gefiltert wird ueber die PROVINZ, nicht ueber den Typ.</b> Der Datensatz enthaelt
/// Auslandsreisen kanadischer Delegationen und Online-Termine; beide gehoeren nicht in ein
/// Verzeichnis, das sagt, wo man hinfahren kann. Der naheliegende Filter waere <c>type</c> — der
/// greift aber daneben: „FIDE: World CC for People with Disabilities" in Usbekistan traegt
/// <c>type=OTB</c> und nur <c>prov=FO</c>. Gezaehlt: <c>prov</c> ist bei 9 Eintraegen <c>FO</c> und
/// bei 7 <c>Online</c>, <c>type=Foreign</c> dagegen nur bei einem. Und der Filter ist eine
/// ERLAUBTE Liste der dreizehn Provinz-Kuerzel, keine Sperrliste: ein neues Pseudo-Kuerzel rutschte
/// sonst als kanadisches Turnier durch. Nebeneffekt: der Online-Filter entfernt auch Eintraege, die
/// gar keine Turniere sind („Application for CFC Subsidy …", ein Meldeschluss).</para>
///
/// <para><b>Was fehlt</b>: Anschrift und Postleitzahl ganz — es gibt nur Ort und Provinz, der
/// PLZ-zuerst-Weg des Geocoders greift hier also nie. Dazu Bedenkzeit, Rundenzahl und
/// Teilnehmerzahl. Kanada steht im Bestand bei 82 % Verortung, der Ortsname allein traegt dort
/// also.</para>
/// </summary>
public class CfcCalendarService
{
    internal const string AllowedHost = "www.chess.ca";

    private const string EventsPageUrl = "https://www.chess.ca/en/events/";

    /// <summary>
    /// Die dreizehn Provinzen und Territorien. ERLAUBTE Liste, damit ein neues Pseudo-Kuerzel
    /// (heute <c>FO</c> fuer Ausland und <c>Online</c>) nicht als kanadisches Turnier durchrutscht.
    /// </summary>
    private static readonly HashSet<string> CanadianProvinces =
        new(StringComparer.OrdinalIgnoreCase)
        { "AB", "BC", "MB", "NB", "NL", "NS", "NT", "NU", "ON", "PE", "QC", "SK", "YT" };

    /// <summary>
    /// Der Dateiname mit dem Datensatz. Der Hash wechselt bei jedem Site-Build des Verbands,
    /// deshalb wird er aus dem HTML gelesen und nie geraten.
    /// </summary>
    private static readonly Regex DataFilePattern =
        new(@"/ext/cfc-data\.[A-Fa-f0-9]{8,}\.js", RegexOptions.Compiled);

    private static readonly Regex PayloadPattern =
        new(@"window\.ws_cfc_data\s*=\s*(?<json>\{.*\})\s*;?\s*$",
            RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ILogger<CfcCalendarService> _log;

    public CfcCalendarService(HttpClient http, ILogger<CfcCalendarService> log)
    {
        _http = http;
        _log = log;
    }

    public async Task<List<ParsedCfcEvent>> FetchAsync(DateOnly from, CancellationToken ct = default)
    {
        var dataUrl = await ResolveDataUrlAsync(ct);

        using var response = await _http.GetAsync(dataUrl, ct);
        var script = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();

        var all = ParsePayload(script);
        var events = all.Where(e => e.EndDate >= from).ToList();

        _log.LogInformation("CFC-Kalender ab {From}: {Count} von {All} Eintraegen (Datei {File})",
            from, events.Count, all.Count, dataUrl.AbsolutePath);
        return events;
    }

    /// <summary>
    /// Schritt eins: den aktuellen Dateinamen aus der Ereignisseite lesen. Faellt das Muster aus,
    /// wird ABGEBROCHEN und nicht auf einen geratenen Namen ausgewichen — ein 404 waere hier ein
    /// leerer Kalender und damit ein Lauf, der jedes kanadische Turnier zurueckzieht.
    /// </summary>
    private async Task<Uri> ResolveDataUrlAsync(CancellationToken ct)
    {
        var page = new Uri(EventsPageUrl);
        EnsureAllowedTarget(page);

        using var response = await _http.GetAsync(page, ct);
        var html = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();

        var path = DataFilePattern.Match(html);
        if (!path.Success)
            throw new InvalidOperationException(
                "CFC: kein /ext/cfc-data.<hash>.js im HTML — die Seite wurde umgebaut.");

        var target = new Uri(page, path.Value);
        EnsureAllowedTarget(target);
        return target;
    }

    /// <summary>
    /// Schritt zwei: <c>window.ws_cfc_data</c> aus der Datei schaelen und die Ereignisliste lesen.
    /// </summary>
    internal static List<ParsedCfcEvent> ParsePayload(string script)
    {
        var match = PayloadPattern.Match(script);
        if (!match.Success)
            throw new InvalidOperationException(
                "CFC: window.ws_cfc_data nicht gefunden — das Dateiformat hat sich geaendert.");

        var payload = JsonSerializer.Deserialize<CfcPayload>(match.Groups["json"].Value, JsonOptions);
        var rows = payload?.Events ?? [];
        var events = new List<ParsedCfcEvent>();

        foreach (var row in rows)
        {
            if (row.Name is not { Length: > 0 }) continue;
            if (row.Prov is not { Length: > 0 } || !CanadianProvinces.Contains(row.Prov.Trim())) continue;
            if (!DateOnly.TryParse(row.Start, out var start)) continue;
            if (!DateOnly.TryParse(row.End, out var end)) end = start;
            // Ein Enddatum VOR dem Start ist ein Tippfehler der Quelle, kein einstuendiges Turnier.
            if (end < start) end = start;

            events.Add(new ParsedCfcEvent
            {
                EventId = EventKeyOf(start, end, row.City, row.Name),
                Name = row.Name.Trim(),
                StartDate = start,
                EndDate = end,
                Place = row.City?.Trim(),
                Province = row.Prov.Trim().ToUpperInvariant(),
                Url = row.Url?.Trim() is { Length: > 0 } u ? u : null,
            });
        }

        // Die Quelle listet einzelne Turniere doppelt (gleicher Termin, Ort, Name und Link, nur
        // eine andere Listenposition). Der Schluessel fuehrt sie zusammen, der erste gewinnt.
        return events
            .GroupBy(e => e.EventId, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>
    /// Kennung aus Termin, Ort UND Name. Der Name muss hinein, weil Termin und Ort allein 13
    /// Kollisionen haben (zwei Turniere am selben Tag in derselben Stadt) und diese Quelle keine
    /// Anschrift fuehrt, die sie trennen koennte.
    /// </summary>
    internal static string EventKeyOf(DateOnly start, DateOnly end, string? city, string name) =>
        $"{start:yyyy-MM-dd}|{end:yyyy-MM-dd}|{(city ?? "").Trim().ToLowerInvariant()}"
        + $"|{name.Trim().ToLowerInvariant()}";

    internal static void EnsureAllowedTarget(Uri url) => SourceHostGuard.Ensure(url, AllowedHost);

    private sealed class CfcPayload
    {
        public List<CfcRow>? Events { get; set; }
    }

    private sealed class CfcRow
    {
        public string? Name { get; set; }
        public string? Start { get; set; }
        public string? End { get; set; }
        public string? City { get; set; }
        public string? Prov { get; set; }
        public string? Type { get; set; }
        public string? Url { get; set; }
    }
}
