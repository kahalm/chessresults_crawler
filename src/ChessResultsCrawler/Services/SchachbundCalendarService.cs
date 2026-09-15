using System.Globalization;
using System.Text.RegularExpressions;
using System.Web;
using System.Xml.Linq;

namespace ChessResultsCrawler.Services;

/// <summary>Ein Termin aus der Turnierdatenbank des Deutschen Schachbunds.</summary>
public class ParsedSchachbundEvent
{
    /// <summary>
    /// Der Adressbestandteil der Detailseite — die einzige Kennung, die diese Quelle hat. Eine
    /// Nummer gibt es nirgends.
    /// </summary>
    public string EventId { get; set; } = "";

    public string Name { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }

    /// <summary>Der Regions-Schluessel, unter dem der Termin gemeldet wurde („bayern", „welt").</summary>
    public string Region { get; set; } = "";

    /// <summary>
    /// Die Anschrift aus dem Abschnitt „Ort" der Ausschreibung, zusammengezogen zu einer Zeile
    /// („Caissa Center München, Frankfurter Ring 193a, 80807 München").
    /// </summary>
    public string? Place { get; set; }

    /// <summary>Bedenkzeit im Rohtext („12 Minuten + 3 Sekunden/Zug").</summary>
    public string? TimeControl { get; set; }

    public int? Rounds { get; set; }

    /// <summary>„swiss" oder „roundRobin", aus dem Abschnitt „Modus".</summary>
    public string? System { get; set; }

    public string Url { get; set; } = "";
}

/// <summary>
/// Die Turnierdatenbank des Deutschen Schachbunds (schachbund.de).
///
/// <para><b>Was sie beitraegt, ist eine ANDERE Art Turnier.</b> Deutschland ist ueber
/// chess-results teilweise abgedeckt; die Frage war nie „gibt es Turniere", sondern „gibt es
/// welche, die dort fehlen". Diese Datenbank ist ein reines MELDE-System — der Veranstalter
/// traegt seinen Termin selbst ein, ohne Swiss-Manager und ohne Ergebnismeldung. Deshalb stehen
/// hier Vereins-Abendturniere, Jugend-Cups, <b>Fernschach</b>, <b>Problemschach</b>, Online und
/// Schach960: Kategorien, die chess-results praktisch nie fuehrt.</para>
///
/// <para><b>Der Ertrag ist klein</b> (rund 96 kuenftige Eintraege ueber 25 Regionen), aber
/// billig: zwei Abrufe je Region und keine Detailseite je Turnier.</para>
///
/// <para><b>Warum zwei Abrufe und nicht einer.</b> Die Uebersichtsseite und der RSS-Feed derselben
/// Region tragen VERSCHIEDENE Dinge, und beides wird gebraucht:</para>
/// <list type="bullet">
/// <item>Die <b>Seite</b> hat je Termin eine Zeile mit ANSCHRIFT („Caissa Center München,
/// Frankfurter Ring 193a, 80807 München") — an der echten Quelle gemessen bei nahezu jedem
/// Eintrag, oft mit Postleitzahl. Ohne sie gaebe es keinen Pin und damit keine Umkreissuche.</item>
/// <item>Der <b>Feed</b> traegt die AUSSCHREIBUNG. Sie ist Freitext des Veranstalters — nur vier
/// von 96 gliedern sie in „Termin/Ort/Modus" —, aber Rundenzahl (60 von 96) und Bedenkzeit
/// (29 von 96) lassen sich daraus lesen. Die Seite nennt beides gar nicht.</item>
/// </list>
///
/// <para><b>Zwei Fallen, und die zweite ist toedlich, wenn man sie nicht kennt.</b></para>
/// <list type="number">
/// <item><b>Der Feed-Schluessel ist nicht der Seiten-Schluessel.</b> Die Uebersichtsseite
/// verlinkt <c>turnierdatenbank-nordrhein-westfalen.html</c>, der Feed heisst aber
/// <c>feed-turnierdatenbank-nordrheinwestfalen.xml</c> — ohne Bindestriche. Mit Bindestrich
/// antwortet er 404. Betrifft fuenf der 25 Regionen.</item>
/// <item><b>Der erste Abruf liefert nur „Einen Moment …"</b> mit
/// <c>document.cookie="dwzc=1"; location.reload()</c>. Das ist ein JS-Cookie-Gate, kein
/// Bot-Schutz: mit <c>Cookie: dwzc=1</c> kommt sofort der volle Inhalt. Ohne das Wissen haelt man
/// die Quelle fuer eine JavaScript-Anwendung und gibt auf.</item>
/// </list>
///
/// <para><b>Die Regionen werden GELESEN, nicht geraten.</b> Die Uebersichtsseite nennt sie; eine
/// fest eingebaute Liste wuerde bei jeder neuen Kategorie stillschweigend eine Region
/// uebersehen — und genau in diesen Sonderkategorien liegt der Wert dieser Quelle.</para>
///
/// <para>Rechtslage (2026-09-08 geprueft): <c>robots.txt</c> sperrt die DWZ-Ratingdatenbank
/// (<c>/turnier.html</c>, <c>/spieler/</c>, <c>/verein/</c>, …) — <c>/turnierdatenbank*</c>,
/// <c>/turnierdetails/</c> und <c>/share/feed-*</c> sind frei. Kein KI-Bot-Block, kein
/// <c>Content-Signal</c>, keine Scraping-Klausel. <c>Crawl-delay: 5</c>, und die wird
/// eingehalten.</para>
/// </summary>
public class SchachbundCalendarService
{
    internal const string AllowedHost = "www.schachbund.de";

    private const string OverviewUrl = "https://www.schachbund.de/turnierdatenbank.html";

    /// <summary>Die Quelle nennt sie in ihrer robots.txt, und sie gilt hier fuer JEDEN Abruf.</summary>
    internal static readonly TimeSpan CrawlDelay = TimeSpan.FromSeconds(5);

    /// <summary>Deckel gegen eine Uebersichtsseite, die eines Tages hundert Regionen verlinkt.</summary>
    internal const int MaxRegions = 40;

    /// <summary>
    /// Regionen, die die Uebersichtsseite verlinkt, die aber KEINEN Feed haben. Ihre Termine
    /// stehen auf der Regionsseite und werden weiter gelesen — nur die Ausschreibung (Rundenzahl,
    /// Bedenkzeit) gibt es fuer sie nicht.
    ///
    /// <para><b>Gemessen am 2026-09-15</b>: fuer alle vier antwortet
    /// <c>/share/feed-turnierdatenbank-&lt;region&gt;.xml</c> ebenso mit 404 wie die Variante ohne
    /// „turnierdatenbank-" (Bayern zur Kontrolle: 200). Vorher stand jede Nacht viermal
    /// „schachbund: Feed … nicht lesbar" im Log — fuer Feeds, die es nie gab.</para>
    ///
    /// <para><b>Bewusst eine feste Liste und kein „404 heisst: kein Feed".</b> Ein 404 auf einer
    /// GEWOEHNLICHEN Region ist ein echter Fehler — genau so fiel die Bindestrich-Falle bei
    /// Nordrhein-Westfalen auf (siehe <see cref="FeedKey"/>). Alle anderen Regionen melden einen
    /// fehlenden Feed deshalb weiter als Warnung.</para>
    /// </summary>
    internal static readonly IReadOnlySet<string> RegionsWithoutFeed =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "blindenschachbund", "fernschachbund", "problemschach", "schach960",
        };

    /// <summary>Ob fuer diese Region ein Feed abgerufen wird.</summary>
    internal static bool HasFeed(string region) => !RegionsWithoutFeed.Contains(region);

    private readonly HttpClient _http;
    private readonly ILogger<SchachbundCalendarService> _log;

    public SchachbundCalendarService(HttpClient http, ILogger<SchachbundCalendarService> log)
    {
        _http = http;
        _log = log;
    }

    public async Task<List<ParsedSchachbundEvent>> FetchAsync(
        DateOnly from, CancellationToken ct = default)
    {
        var regions = await FetchRegionsAsync(ct);
        if (regions.Count == 0)
        {
            _log.LogWarning("schachbund: keine Regionen auf der Uebersichtsseite gefunden");
            return [];
        }

        var all = new List<ParsedSchachbundEvent>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var region in regions.Take(MaxRegions))
        {
            ct.ThrowIfCancellationRequested();

            List<ParsedSchachbundEvent> rows;
            try
            {
                rows = await FetchRegionAsync(region, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "schachbund: Region {Region} nicht lesbar", region);
                continue;
            }

            // Die Ausschreibungen aus dem Feed derselben Region. Ein Ausfall hier kostet nur
            // Rundenzahl und Bedenkzeit — die Termine stehen schon.
            if (!HasFeed(region))
            {
                _log.LogDebug("schachbund: Region {Region} hat keinen Feed — Termine ohne Ausschreibung", region);
            }
            else try
            {
                var announcements = await FetchAnnouncementsAsync(region, ct);
                foreach (var row in rows)
                {
                    if (!announcements.TryGetValue(row.EventId, out var lines)) continue;
                    row.Place ??= PlaceOf(lines);
                    row.TimeControl = TimeControlOf(lines);
                    row.Rounds = RoundsOf(lines);
                    row.System = SystemOf(lines);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "schachbund: Feed {Region} nicht lesbar", region);
            }

            foreach (var row in rows)
            {
                if (row.EndDate < from || !seen.Add(row.EventId)) continue;
                all.Add(row);
            }
        }

        _log.LogInformation("schachbund-Turnierdatenbank ab {From}: {Count} Termine aus {Regions} Regionen",
            from, all.Count, regions.Count);
        return [.. all.OrderBy(e => e.StartDate)];
    }

    // ----- Regionen ----------------------------------------------------------

    /// <summary>
    /// Die Regionen von der Uebersichtsseite lesen. Zurueck kommt der SEITEN-Schluessel (mit
    /// Bindestrichen); den Feed-Schluessel macht <see cref="FeedKey"/> daraus.
    /// </summary>
    internal async Task<List<string>> FetchRegionsAsync(CancellationToken ct)
    {
        var target = new Uri(OverviewUrl);
        EnsureAllowedTarget(target);

        using var response = await Send(target, ct);
        var html = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();

        return ParseRegions(html);
    }

    internal static List<string> ParseRegions(string html)
    {
        var result = new List<string>();
        foreach (Match m in RegionPattern.Matches(html))
        {
            var key = m.Groups[1].Value;
            if (key.Length > 0 && !result.Contains(key)) result.Add(key);
        }
        return result;
    }

    private static readonly Regex RegionPattern =
        new(@"turnierdatenbank-([a-z0-9-]+)\.html", RegexOptions.Compiled);

    // ----- Eine Region ------------------------------------------------------

    /// <summary>
    /// Die Uebersichtsseite EINER Region: je Termin eine Zeile mit Datum, Name, Anschrift und dem
    /// Adressbestandteil der Detailseite. Sie ist die Quelle der Termine — der Feed ergaenzt nur.
    /// </summary>
    private async Task<List<ParsedSchachbundEvent>> FetchRegionAsync(
        string region, CancellationToken ct)
    {
        EnsureRegionKey(region);
        await Task.Delay(CrawlDelay, ct);

        var target = new Uri($"https://{AllowedHost}/turnierdatenbank-{PageKey(region)}.html");
        EnsureAllowedTarget(target);

        using var response = await Send(target, ct);
        var html = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();

        return ParsePage(html, region);
    }

    /// <summary>
    /// Die Ausschreibungen derselben Region aus dem RSS-Feed, nach dem Adressbestandteil der
    /// Detailseite verschluesselt.
    ///
    /// <para><b>Der Feed-Schluessel ist NICHT der Seiten-Schluessel:</b> die Seite heisst
    /// <c>turnierdatenbank-nordrhein-westfalen.html</c>, der Feed
    /// <c>feed-turnierdatenbank-nordrheinwestfalen.xml</c>. Mit Bindestrich antwortet er 404 —
    /// betrifft fuenf der 25 Regionen.</para>
    /// </summary>
    private async Task<Dictionary<string, List<string>>> FetchAnnouncementsAsync(
        string region, CancellationToken ct)
    {
        EnsureRegionKey(region);
        await Task.Delay(CrawlDelay, ct);

        var target = new Uri($"https://{AllowedHost}/share/feed-turnierdatenbank-{FeedKey(region)}.xml");
        EnsureAllowedTarget(target);

        using var response = await Send(target, ct);
        var xml = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();

        return ParseFeed(xml);
    }

    /// <summary>Der Schluessel, wie er in der SEITEN-Adresse steht — mit Bindestrichen.</summary>
    internal static string PageKey(string region) => region;

    /// <summary>Der Schluessel, wie er in der FEED-Adresse steht — ohne Bindestriche.</summary>
    internal static string FeedKey(string region) => region.Replace("-", "");

    private static void EnsureRegionKey(string region)
    {
        if (!RegionKeyPattern.IsMatch(region))
            throw new InvalidOperationException($"Refusing malformed region key: {region}");
    }

    private static readonly Regex RegionKeyPattern = new(@"^[a-z0-9-]{2,40}$", RegexOptions.Compiled);

    /// <summary>
    /// Die Terminliste einer Regionsseite. Eine Zeile ist ein <c>layout_teaser</c> mit den drei
    /// Bloecken <c>event_datum</c>, <c>event_titel</c> und <c>event_ort</c>.
    ///
    /// <para><b>Der Termin kommt aus dem <c>title</c>-Attribut</b>, nicht aus dem Datumsblock:
    /// dort steht er vollstaendig ausgeschrieben („12.09.2026 10:00–13.09.2026 16:30"), waehrend
    /// der Block bei mehrtaegigen Turnieren abkuerzt („12. - 13.09.2026") und ueber den
    /// Monatswechsel raten liesse.</para>
    /// </summary>
    internal static List<ParsedSchachbundEvent> ParsePage(string html, string region)
    {
        var results = new List<ParsedSchachbundEvent>();

        foreach (Match row in RowPattern.Matches(html))
        {
            var body = row.Groups[1].Value;

            var title = TitleLinkPattern.Match(body);
            if (!title.Success) continue;

            var slug = SlugPattern.Match(title.Groups[1].Value);
            if (!slug.Success) continue;

            var name = Collapse(Strip(title.Groups[3].Value));
            if (name.Length == 0) continue;

            var dates = DatePattern.Matches(Collapse(Strip(title.Groups[2].Value)))
                .Select(m => ParseDate(m.Value))
                .Where(d => d is not null)
                .Select(d => d!.Value)
                .ToList();

            if (dates.Count == 0)
            {
                var fallback = DateBlockPattern.Match(body);
                if (!fallback.Success) continue;
                foreach (Match m in DatePattern.Matches(Collapse(Strip(fallback.Groups[1].Value))))
                    if (ParseDate(m.Value) is { } d) dates.Add(d);
            }
            if (dates.Count == 0) continue;

            var place = PlaceBlockPattern.Match(body);
            results.Add(new ParsedSchachbundEvent
            {
                EventId = slug.Groups[1].Value,
                Name = name,
                StartDate = dates[0],
                EndDate = dates[^1] >= dates[0] ? dates[^1] : dates[0],
                Region = region,
                Place = place.Success ? Empty(Strip(place.Groups[1].Value)) : null,
                Url = $"https://{AllowedHost}/turnierdetails/{slug.Groups[1].Value}.html",
            });
        }
        return results;
    }

    private static readonly Regex RowPattern =
        new(@"<div class=""layout_teaser[^""]*"">(.*?)<div class=""more""",
            RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex TitleLinkPattern =
        new(@"event_titel[^""]*""><a href=""([^""]+)""\s+title=""([^""]*)""[^>]*>(.*?)</a>",
            RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex DateBlockPattern =
        new(@"event_datum[^""]*"">(.*?)</div>", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex PlaceBlockPattern =
        new(@"event_ort[^""]*"">(.*?)</div>", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex DatePattern = new(@"\d{2}\.\d{2}\.\d{4}", RegexOptions.Compiled);

    private static readonly Regex SlugPattern =
        new(@"turnierdetails/([^/?#""]+?)(?:\.html)?$", RegexOptions.Compiled);

    /// <summary>
    /// Die Ausschreibungen eines Regions-Feeds, nach dem Adressbestandteil der Detailseite
    /// verschluesselt. Zurueck kommen die ABSAETZE — die Ausschreibung ist Freitext, und was
    /// darin steht, entscheiden die Leser weiter unten.
    /// </summary>
    internal static Dictionary<string, List<string>> ParseFeed(string xml)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        XDocument document;
        try { document = XDocument.Parse(xml); }
        catch (System.Xml.XmlException) { return result; }

        foreach (var item in document.Descendants("item"))
        {
            var link = Collapse(item.Element("link")?.Value ?? item.Element("guid")?.Value);
            var slug = SlugPattern.Match(link);
            if (!slug.Success) continue;

            var lines = Describe(item.Element("description")?.Value);
            if (lines.Count > 0) result[slug.Groups[1].Value] = lines;
        }
        return result;
    }

    // ----- Die Ausschreibung -------------------------------------------------

    /// <summary>
    /// Die Beschreibung ist HTML mit Absaetzen. Zurueck kommen die Absaetze als Zeilen — die
    /// Ausschreibung ist nach Ueberschriften gegliedert („Ort", „Modus", „Bedenkzeit"), und die
    /// Angabe steht in den Zeilen DANACH.
    /// </summary>
    internal static List<string> Describe(string? html)
    {
        if (html is not { Length: > 0 }) return [];

        var text = Regex.Replace(html, @"<\s*/?\s*(p|br|div|li|tr)[^>]*>", "\n",
            RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", " ");
        text = HttpUtility.HtmlDecode(text) ?? "";

        return [.. text.Split('\n')
            .Select(line => Collapse(line).Trim())
            .Where(line => line.Length > 0)];
    }

    /// <summary>
    /// Die Anschrift: alle Zeilen zwischen der Ueberschrift „Ort" und der naechsten Ueberschrift.
    /// Sie traegt oft die POSTLEITZAHL („80807 München") und ist damit der genaueste Weg der
    /// Verortung, den diese Quelle hergibt.
    /// </summary>
    internal static string? PlaceOf(List<string> lines)
    {
        var block = SectionOf(lines, "ort", "spielort", "veranstaltungsort");
        if (block.Count == 0) return null;

        var joined = Collapse(string.Join(", ", block.Take(4)));
        return joined.Length > 0 ? joined : null;
    }

    internal static string? TimeControlOf(List<string> lines)
    {
        foreach (var line in lines)
        {
            var m = TimeControlPattern.Match(line);
            if (m.Success && Collapse(m.Groups[1].Value) is { Length: > 0 } value) return value;
        }

        // Ohne Beschriftung: die Zeilen des Abschnitts „Modus" tragen sie manchmal mit.
        return SectionOf(lines, "modus", "spielmodus")
            .FirstOrDefault(l => MinutesPattern.IsMatch(l));
    }

    private static readonly Regex TimeControlPattern =
        new(@"\bBedenkzeit\s*:?\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MinutesPattern =
        new(@"\d\s*(min|sek|sec|stunde)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>„5 Runden, Schweizer System" — die Zahl vor dem Wort.</summary>
    internal static int? RoundsOf(List<string> lines)
    {
        foreach (var line in lines)
        {
            var m = RoundsPattern.Match(line);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var rounds)
                && rounds is >= 1 and <= 30) return rounds;
        }
        return null;
    }

    private static readonly Regex RoundsPattern =
        new(@"(\d{1,2})\s*(?:Runden|Runde\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static string? SystemOf(List<string> lines)
    {
        foreach (var line in lines)
        {
            if (SwissPattern.IsMatch(line)) return "swiss";
            if (RoundRobinPattern.IsMatch(line)) return "roundRobin";
        }
        return null;
    }

    private static readonly Regex SwissPattern =
        new(@"schweizer|swiss", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RoundRobinPattern =
        new(@"rundenturnier|vollrundig|jeder gegen jeden|round.?robin|berger",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Die Zeilen unter einer Ueberschrift. Eine Ueberschrift ist eine KURZE Zeile, die genau aus
    /// dem gesuchten Wort besteht (mit oder ohne Doppelpunkt) — „Ort" als Ueberschrift, nicht das
    /// „Ort" mitten in einem Satz.
    /// </summary>
    private static List<string> SectionOf(List<string> lines, params string[] headings)
    {
        var start = lines.FindIndex(l => IsHeading(l, headings));
        if (start < 0) return [];

        var block = new List<string>();
        for (var i = start + 1; i < lines.Count; i++)
        {
            if (IsAnyHeading(lines[i])) break;
            block.Add(lines[i]);
        }
        return block;
    }

    private static bool IsHeading(string line, string[] headings) =>
        headings.Contains(line.TrimEnd(':').Trim().ToLowerInvariant());

    /// <summary>
    /// Wo der naechste Abschnitt beginnt. Eine Ueberschrift ist hoechstens drei Woerter lang,
    /// endet nicht auf einem Satzzeichen und traegt keine Ziffer — das trennt „Startgeld" von
    /// „80807 München".
    /// </summary>
    private static bool IsAnyHeading(string line) =>
        KnownHeadings.Contains(line.TrimEnd(':').Trim());

    /// <summary>
    /// Die Ueberschriften, die in diesen Ausschreibungen wirklich vorkommen. Bewusst eine LISTE
    /// statt einer Formregel: „Alle Teilnehmer benötigen eine FIDE-ID" sieht sonst wie eine
    /// Ueberschrift aus, und der Ortsblock liefe bis zum Ende der Ausschreibung.
    /// </summary>
    private static readonly HashSet<string> KnownHeadings = new(StringComparer.OrdinalIgnoreCase)
    {
        "termin", "ort", "spielort", "veranstaltungsort", "modus", "spielmodus", "bedenkzeit",
        "auswertung", "startgeld", "preise", "preisgeld", "anmeldung", "meldung", "meldeschluss",
        "kontakt", "veranstalter", "ausrichter", "turnierleitung", "sonstiges", "hinweise",
        "teilnahmeberechtigt", "teilnehmer", "fide-id", "ausschreibung", "zeitplan", "runden",
    };

    // ----- Hilfen ------------------------------------------------------------

    /// <summary>
    /// Jeder Abruf mit dem Cookie, das die Seite sich sonst per JavaScript selbst setzt — sonst
    /// kommt nur „Einen Moment …" zurueck.
    /// </summary>
    private async Task<HttpResponseMessage> Send(Uri target, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        request.Headers.Add("Cookie", "dwzc=1");
        return await _http.SendAsync(request, ct);
    }

    private static DateOnly? ParseDate(string? text) =>
        DateOnly.TryParseExact(text?.Trim(), "dd.MM.yyyy", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date) ? date : null;

    private static string Collapse(string? text) =>
        Regex.Replace(text ?? "", @"\s+", " ").Trim();

    /// <summary>Markup raus, Entitaeten aufgeloest, Leerraum zusammengezogen.</summary>
    private static string Strip(string? html) =>
        Collapse(HttpUtility.HtmlDecode(Regex.Replace(html ?? "", "<[^>]+>", " ")) ?? "")
            .Trim('\u00a0', ' ', ',');

    private static string? Empty(string? text) =>
        text is { Length: > 0 } && Collapse(text) is { Length: > 0 } value ? value : null;

    internal static void EnsureAllowedTarget(Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"Refusing non-https target: {url}");

        if (!url.Host.Equals(AllowedHost, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing unexpected host: {url.Host}");
    }
}
