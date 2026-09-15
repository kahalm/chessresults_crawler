using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;

namespace ChessResultsCrawler.Services;

/// <summary>Ein Turnier aus dem Kalender des niederlaendischen Verbands (KNSB).</summary>
public class ParsedKnsbEvent
{
    /// <summary>
    /// Der WordPress-Slug — traegt Titel UND Startdatum als Suffix
    /// (<c>"zomeravondcompetitie-2027-07-19"</c>) und ist die deterministische Kennung dieser
    /// Quelle. <c>date</c>/<c>modified</c> ALLER 177 gemessenen Eintraege standen auf demselben
    /// Tag — ein Hinweis auf einen taeglichen Voll-Reimport, bei dem unklar ist, ob die
    /// numerische Post-Id erhalten bleibt. Der Slug ist aus Titel und Termin gebildet und damit
    /// stabiler.
    /// </summary>
    public string Slug { get; set; } = "";

    public string Name { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public string Url { get; set; } = "";

    /// <summary>
    /// Bedenkzeit-Klasse als Klartext der "speed"-Taxonomie: "Normaalschaak", "Rapidschaak" oder
    /// "Snelschaak". Jeder der 177 gemessenen Eintraege trug GENAU einen Wert — <c>null</c> nur,
    /// falls die Liste (theoretisch) einmal keinen liefert.
    /// </summary>
    public string? Speed { get; set; }

    /// <summary>
    /// Als "Internetschaak" markiert (event-category 61) — kein Spielort, dasselbe Konzept wie
    /// ECFs "Online"-Schlagwort.
    /// </summary>
    public bool Online { get; set; }
}

/// <summary>
/// Der Terminkalender des niederlaendischen Verbands (KNSB, schaakbond.nl).
///
/// <para><b>Ein grosser Zusatz, gemessen am 2026-09-09.</b> <b>177 kuenftige Eintraege</b> gegen
/// 12 auf chess-results im selben Zeitraum — der Zusatz sind ueberwiegend Klubturniere, die
/// chess-results nie sieht. Die Spanne reicht bis 2027-09-04, ein ganzes Jahr Vorlauf.</para>
///
/// <para><b>Es ist WordPress-KERN-REST, nicht das Plugin "The Events Calendar" (ECF/Rumaenien).</b>
/// Die Antwort ist eine reine JSON-LISTE ohne umschliessendes Objekt, und die Gesamtseitenzahl
/// steht NUR im Antwort-HEADER <c>X-WP-TotalPages</c> — anders als bei ECF, wo "total_pages" im
/// Rumpf selbst steht. <c>per_page=100</c> ist die groesste Seite, die WordPress-Kern akzeptiert
/// (Standard-Deckel); gemessen: <b>2 Seiten</b> fuer alle 177 Eintraege (100 + 77).</para>
///
/// <para><b>Was fehlt, und zwar STRUKTURELL — nicht nur mit Mehraufwand erreichbar:</b> die Liste
/// hat kein Enddatum-, Orts-, Anschrift-, Postleitzahl-, Koordinaten-, Rundenzahl- oder
/// Teilnehmerzahl-Feld. Alle diese Angaben (ausser Rundenzahl/Teilnehmer, die es NIRGENDS gibt)
/// stehen einzig auf der Detailseite, in einem einzigen <c>&lt;p class=eventtime&gt;</c>-Textblock
/// (eintaegig <c>"04 September 2027  10:30 - 17.30&lt;br&gt;Boorstraat 107 3513 SE&lt;br&gt;
/// Utrecht"</c>, mehrtaegig <c>"06 May 2027 - 08 May 2027 &lt;br&gt;Zoetermeer&lt;br&gt;Zoetermeer"</c>)
/// — auch ueber REST kommt dort nichts mit (<c>"acf": []</c>, die ACF-Custom-Fields sind bewusst
/// nicht freigegeben). Ein Abruf je Turnier waere noetig, und bei 15 s Wartezeit (siehe unten)
/// kostet das rund 45 Minuten fuer alle 177 — ein eigener, gedeckelter Nachtlauf analog zum
/// Rundenplan-Dienst, hier bewusst NICHT gebaut. Diese Quelle liefert deshalb heute nur Name,
/// Startdatum, Link und Bedenkzeit-Klasse; Enddatum bleibt <c>StartDate</c> gleichgesetzt (bei
/// den 48 von 177 als "Meerdaags" markierten Eintraegen also nachweislich ungenau), und es gibt
/// KEINEN Ortstext.</para>
///
/// <para><b>Die Bedenkzeit-Klasse ist der eine Lichtblick: sie steht STRUKTURIERT in der Liste.</b>
/// Die "speed"-Taxonomie traegt bei JEDEM der 177 Eintraege GENAU einen Wert (Term-Ids gemessen
/// gegen <c>/wp-json/wp/v2/speed</c>: 36 "Normaalschaak" [57], 37 "Rapidschaak" [98], 39
/// "Snelschaak" [22]) — anders als bei den meisten Quellen des Projekts muss sie hier nicht aus
/// Freitext erschlossen werden. WordPress-Kern liefert Taxonomien in der Liste nur als
/// Term-ID-Array; die Namen kommen erst mit <c>_embed=1</c> mit — das aber versechsfacht die
/// Antwortgroesse (gemessen: 3 Eintraege mit <c>_embed</c> = 184 KB, ohne = 9,7 KB je Eintrag;
/// hochgerechnet auf alle 177 waeren das rund 11 MB statt der tatsaechlich gemessenen 1,7 MB).
/// Die drei benoetigten Term-Ids sind deshalb als Konstanten hinterlegt: aendert die Quelle sie,
/// bleibt <see cref="ParsedKnsbEvent.Speed"/> einfach <c>null</c> statt zu brechen.</para>
///
/// <para><b>Die Kategorie "Jeugd"/"Senior" ist KEIN verlaessliches Alters-Schlagwort</b> (anders
/// als ECFs "Juniors Only"): 43 der 118 mit "Jeugd" markierten Eintraege tragen GLEICHZEITIG
/// "Senior" (z. B. die offene "Zomeravondcompetitie") — die Tags sagen "fuer diese Mitgliedschaft
/// zugelassen", nicht "dieses Turnier ist ein Jugendturnier". Sie werden deshalb bewusst NICHT
/// ausgewertet; Alter/Geschlecht kommen wie sonst ueberall aus dem Namen
/// (<c>TournamentClassifier</c>, RookHub-seitig).</para>
///
/// <para><b>Kein "Meeting"-Aequivalent noetig.</b> <c>event-category=33</c> ("Schaakkalender")
/// filtert die Liste bereits auf reine Turnier-Ankuendigungen — Kurse/Workshops/Webinare
/// existieren als eigene Kategorien, aber KEINER der 177 gemessenen Eintraege traegt sie
/// gleichzeitig. Ein Eintrag traegt "Simultaan" (Simultanveranstaltung) — bewusst NICHT
/// herausgefiltert, da kein zweites Beispiel eine Regel rechtfertigt.</para>
///
/// <para><b>Rechtslage (2026-09-09 geprueft).</b> robots.txt ist Yoast-Standard (kein KI-Bot-Block,
/// gesperrt nur <c>/testpagina/</c> und <c>/wp-admin/</c>), aber <b>"Crawl-delay: 15"</b> gilt fuer
/// <c>*</c> und damit auch fuer uns. Kein <c>Content-Signal</c>, kein Scraping-Verbot in den
/// Nutzungsbedingungen — nur eine urheberrechtliche Klausel gegen Vervielfaeltigung von Texten,
/// die die Fliesstext-Ausschreibung betrifft, nicht die hier uebernommenen Fakten (Name, Termin,
/// Link, Bedenkzeit).</para>
///
/// <para><b>Kosten eines Durchgangs:</b> 2 Seiten mit 15 s Wartezeit vor der zweiten — rund 15
/// Sekunden insgesamt (kein Vergleich zu den 45 Minuten, die eine Detailseiten-Ergaenzung kosten
/// wuerde).</para>
/// </summary>
public class KnsbCalendarService
{
    internal const string AllowedHost = "schaakbond.nl";

    private const string EventsUrl = "https://schaakbond.nl/wp-json/wp/v2/event";

    /// <summary>Kategorie 33 = "Schaakkalender" — filtert die Turnierliste auf echte Ankuendigungen.</summary>
    private const int CalendarCategoryId = 33;

    /// <summary>Die groesste Seitengroesse, die WordPress-Kern-REST akzeptiert (Standard-Deckel).</summary>
    internal const int PageSize = 100;

    /// <summary>Deckel je Durchgang. Gemessen: 2 Seiten fuer alle 177 Eintraege — reichlich Luft.</summary>
    internal const int MaxPages = 10;

    /// <summary>Die robots.txt der Quelle nennt "Crawl-delay: 15" fuer "*".</summary>
    internal static readonly TimeSpan CrawlDelay = TimeSpan.FromSeconds(15);

    // Term-Ids der "speed"-Taxonomie, gemessen am 2026-09-09 gegen /wp-json/wp/v2/speed. Ohne
    // _embed abgefragt (siehe Klassen-Kommentar) — aendern sie sich, bleibt Speed einfach null.
    private const int SpeedNormal = 36;
    private const int SpeedRapid = 37;
    private const int SpeedBlitz = 39;

    /// <summary>"Internetschaak" — die Online-Kategorie, ebenfalls am 2026-09-09 gemessen (11 von 177).</summary>
    private const int OnlineCategoryId = 61;

    private static readonly Regex SlugDateSuffix = new(@"-(\d{4})-(\d{2})-(\d{2})$", RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly ILogger<KnsbCalendarService> _log;

    public KnsbCalendarService(HttpClient http, ILogger<KnsbCalendarService> log)
    {
        _http = http;
        _log = log;
    }

    public async Task<List<ParsedKnsbEvent>> FetchAsync(DateOnly from, CancellationToken ct = default)
    {
        var events = new List<ParsedKnsbEvent>();

        // Zwei Eintraege im gemessenen Bestand teilten sich Id UND Slug — vermutlich ein
        // Seiten-Grenzfall der Standard-Sortierung zwischen zwei Abrufen. Ohne Entdopplung stuende
        // ein Turnier zweimal im Verzeichnis.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var pages = 1;
        for (var page = 1; page <= Math.Min(pages, MaxPages); page++)
        {
            if (page > 1) await Task.Delay(CrawlDelay, ct);

            var (body, totalPages) = await GetPageAsync(
                $"{EventsUrl}?event-category={CalendarCategoryId}&per_page={PageSize}&page={page}", ct);
            if (body is null) break;

            pages = Math.Max(pages, totalPages);
            foreach (var e in ParseEvents(body))
            {
                if (e.StartDate < from || !seen.Add(e.Slug)) continue;
                events.Add(e);
            }
        }

        _log.LogInformation(
            "KNSB-Kalender ab {From}: {Count} Turniere, {Online} online, Bedenkzeit {Normal}/{Rapid}/{Blitz} (Normaal/Rapid/Snelschaak)",
            from, events.Count, events.Count(e => e.Online),
            events.Count(e => e.Speed == "Normaalschaak"),
            events.Count(e => e.Speed == "Rapidschaak"),
            events.Count(e => e.Speed == "Snelschaak"));
        return [.. events.OrderBy(e => e.StartDate)];
    }

    private async Task<(string? Body, int TotalPages)> GetPageAsync(string url, CancellationToken ct)
    {
        var target = new Uri(url);
        EnsureAllowedTarget(target);

        using var response = await _http.GetAsync(target, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            _log.LogWarning("KNSB: {Url} antwortete {Status}", url, (int)response.StatusCode);
            return (null, 1);
        }

        // Seit 2026-09-14 liefert schaakbond.nl unserem VPN-Ausgang (Rechenzentrums-IP) statt
        // der JSON-Liste eine JavaScript-Warteseite: HTTP 200, text/html, rund 12 kB,
        // „<title>One moment, please...</title>", Server openresty. Von einer privaten IP kommt
        // dieselbe Adresse unveraendert als JSON (gemessen 2026-09-15: 186 Eintraege). Die Seite
        // ist eine Sperre gegen die IP, keine geaenderte Schnittstelle — und sie wird bewusst NICHT
        // umgangen (kein Loesen der Challenge, kein anderer User-Agent). Gemeldet wird sie als
        // Quellenfehler mit Auszug, nicht als Parser-Absturz.
        try
        {
            SourceResponse.EnsureJson("KNSB", (int)response.StatusCode,
                response.Content.Headers.ContentType?.MediaType, body);
        }
        catch (SourceResponseException ex)
        {
            _log.LogWarning("{Message}", ex.Message);
            throw;
        }

        // WordPress-Kern-REST traegt die Gesamtseitenzahl NUR im Header — anders als bei ECF
        // ("The Events Calendar"), wo "total_pages" im JSON-Rumpf selbst steht.
        var totalPages = response.Headers.TryGetValues("X-WP-TotalPages", out var values)
            && int.TryParse(values.FirstOrDefault(), out var n) ? Math.Max(1, n) : 1;
        return (body, totalPages);
    }

    /// <summary>Eine Seite Termine — eine reine JSON-Liste, kein umschliessendes Objekt.</summary>
    internal static List<ParsedKnsbEvent> ParseEvents(string json)
    {
        var results = new List<ParsedKnsbEvent>();
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return results;

        foreach (var row in document.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;

            var slug = Text(row, "slug");
            var title = Decode(RenderedText(row, "title"));
            if (slug is not { Length: > 0 } || title is not { Length: > 0 }) continue;

            var start = StartDateFromSlug(slug);
            if (start is null) continue;

            var categories = IntArray(row, "event-category");
            var speedTerms = IntArray(row, "speed");

            results.Add(new ParsedKnsbEvent
            {
                Slug = slug,
                Name = title,
                StartDate = start.Value,
                Url = Text(row, "link") ?? "",
                Speed = SpeedNameOf(speedTerms),
                Online = categories.Contains(OnlineCategoryId),
            });
        }
        return results;
    }

    /// <summary>
    /// Das Startdatum steckt als Suffix im Slug — die Liste hat sonst kein Datumsfeld (<c>date</c>/
    /// <c>modified</c> sind Bearbeitungszeitstempel, nicht der Turniertermin). Gemessen: bei ALLEN
    /// 177 kuenftigen Eintraegen vorhanden. Ein Slug ohne (oder mit unplausiblem) Datumssuffix
    /// wird uebersprungen statt geraten.
    /// </summary>
    internal static DateOnly? StartDateFromSlug(string slug)
    {
        var m = SlugDateSuffix.Match(slug);
        if (!m.Success) return null;
        if (!int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var y)
            || !int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var mo)
            || !int.TryParse(m.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var d))
        {
            return null;
        }

        try { return new DateOnly(y, mo, d); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static string? SpeedNameOf(List<int> speedTerms)
    {
        if (speedTerms.Contains(SpeedRapid)) return "Rapidschaak";
        if (speedTerms.Contains(SpeedBlitz)) return "Snelschaak";
        if (speedTerms.Contains(SpeedNormal)) return "Normaalschaak";
        return null;
    }

    // ----- Hilfen ------------------------------------------------------------

    private static string? Text(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() : null;

    /// <summary>"title"/"content"/"excerpt" liegen als <c>{"rendered": "..."}</c> vor.</summary>
    private static string? RenderedText(JsonElement row, string name) =>
        row.TryGetProperty(name, out var wrapper) && wrapper.ValueKind == JsonValueKind.Object
        && wrapper.TryGetProperty("rendered", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() : null;

    private static List<int> IntArray(JsonElement row, string name)
    {
        var result = new List<int>();
        if (!row.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array) return result;

        foreach (var item in list.EnumerateArray())
            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var n)) result.Add(n);
        return result;
    }

    /// <summary>Titel kommen mit HTML-Entitaeten ("Maasstad &amp;#8217;87"). Ohne Aufloesung stuenden sie so im Kalender.</summary>
    private static string? Decode(string? text) =>
        text is { Length: > 0 } ? Empty(HttpUtility.HtmlDecode(text)) : null;

    private static string? Empty(string? text) =>
        text is { Length: > 0 } && text.Trim() is { Length: > 0 } value ? value : null;

    internal static void EnsureAllowedTarget(Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"Refusing non-https target: {url}");

        if (!url.Host.Equals(AllowedHost, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing unexpected host: {url.Host}");
    }
}
