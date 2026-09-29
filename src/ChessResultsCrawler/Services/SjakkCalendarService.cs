using System.Text.RegularExpressions;
using System.Web;
using System.Xml.Linq;
using static ChessResultsCrawler.Services.SourceText;

namespace ChessResultsCrawler.Services;

/// <summary>Ein Termin aus dem Aktivitaeten-Feed des norwegischen Schachverbands.</summary>
public class ParsedSjakkEvent
{
    /// <summary>
    /// Der Adressbestandteil der Detailseite („horten-bgp-høst-2027") — die einzige Kennung, die
    /// diese Quelle hat. Eine Nummer gibt es nirgends, und er traegt norwegische Buchstaben.
    /// </summary>
    public string EventId { get; set; } = "";

    public string Name { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public string Url { get; set; } = "";
}

/// <summary>
/// Die Detailseite EINES Termins. Sie ist die einzige Stelle, an der diese Quelle ueberhaupt
/// etwas ueber den Spielort sagt — der Feed sagt dazu nichts.
/// </summary>
public class ParsedSjakkDetail
{
    /// <summary>Der Spielort aus der Zeile „Spillsted" („Scandic Valdres Hotell Fagernes").</summary>
    public string? Venue { get; set; }

    /// <summary>Der ausrichtende VEREIN aus der Zeile „Arrangør" („Kongsvinger Sjakklubb").</summary>
    public string? Organizer { get; set; }

    /// <summary>Bedenkzeit im Rohtext aus „Betenkningstid" („3 min / parti, 2 sekunder tillegg pr trekk").</summary>
    public string? TimeControl { get; set; }

    /// <summary>Rundenzahl aus „Spillesystem" („9 runder sveitser").</summary>
    public int? Rounds { get; set; }

    /// <summary>„swiss" oder „roundRobin", ebenfalls aus „Spillesystem".</summary>
    public string? System { get; set; }

    /// <summary>Die Turnier-Webseite aus „Nettside" — steht bei jedem Termin.</summary>
    public string? Website { get; set; }
}

/// <summary>
/// Der Aktivitaeten-Feed des norwegischen Schachverbands (sjakk.no).
///
/// <para><b>Der Zugewinn ist rechnerisch vollstaendig.</b> chess-results fuehrt fuer NOR
/// <b>null</b> kuenftige Turniere — jeder Eintrag hier ist einer, den das Verzeichnis sonst
/// ueberhaupt nicht kennt. Der Feed bringt in EINEM Abruf (542 kB) <b>1000</b> Eintraege, davon
/// <b>81 kuenftige</b> (gemessen 2026-09-09); der Rest ist Archiv bis zurueck ins Jahr 2022.</para>
///
/// <para><b>Der Feed allein genuegt nicht, und das ist der teure Teil.</b> Ein Item traegt
/// <c>title</c>, <c>link</c>, <c>guid</c>, <c>pubDate</c>, <c>author</c> und die beiden
/// Termin-Felder — <b>kein Ortsfeld</b>. Die <c>description</c> ist bei <b>985 von 1000</b> leer
/// (die 15 Ausnahmen sind Teams-Links zu Mitgliederversammlungen), und <c>author</c> ist der
/// PERSONENname dessen, der den Termin eingetragen hat, kein Verein. Wer nur den Feed liest,
/// bekommt Namen und Datum und sonst nichts.</para>
///
/// <para><b>Die Detailseite traegt die Angaben, und zwar als saubere Tabelle</b> („Spillsted",
/// „Arrangør", „Betenkningstid", „Spillesystem", „Nettside"). Sie kostet einen Abruf je Turnier,
/// deshalb ist sie — wie bei chessarbiter — eine EIGENE Route: der Aufrufer entscheidet, fuer
/// welche Termine sie sich lohnt (bei uns: fuer die neuen). An den 80 lesbaren kuenftigen
/// Terminen gemessen: <c>Spillsted</c> 17, <c>Arrangør</c> 52, <c>Betenkningstid</c> 38,
/// <c>Spillesystem</c> 39, <c>Nettside</c> 80.</para>
///
/// <para><b>Zwei Fallen, und beide bringen jeden strengen XML-Leser sofort zu Fall.</b></para>
/// <list type="number">
/// <item><b>Vor der XML-Deklaration steht ein Zeilenumbruch.</b> <c>&lt;?xml …?&gt;</c> muss das
/// allererste Zeichen des Dokuments sein; <c>XDocument.Parse</c> wirft sonst. Das ist kein
/// Schoenheitsfehler, sondern der erste von zwei harten Abbruechen.</item>
/// <item><b>Der Namensraum <c>ev:</c> ist nirgends deklariert.</b> Die Termine stehen in
/// <c>&lt;ev:startdate&gt;</c>/<c>&lt;ev:enddate&gt;</c>, das <c>&lt;rss&gt;</c>-Element nennt
/// aber nur <c>xmlns:atom</c>. Ein Leser meldet „unbound prefix" und liefert gar nichts — also
/// ausgerechnet die beiden Felder, wegen derer man den Feed liest, machen ihn unlesbar.</item>
/// </list>
/// <para>Beides repariert <see cref="RepairFeedXml"/> VOR dem Parsen. Bewusst repariert statt mit
/// Regex am XML vorbeigelesen: die Reparatur ist zwei Zeilen und laesst sich testen, ein
/// Regex-Leser haette bei jedem Feld dieselbe Frage neu zu beantworten.</para>
///
/// <para>Rechtslage (2026-09-09 geprueft): <c>robots.txt</c> sperrt nur <c>/cpresources/</c>,
/// <c>/vendor/</c>, <c>/.env</c> und <c>/cache/</c> — der Feed und <c>/aktiviteter/</c> sind frei.
/// Kein KI-Bot-Block, keine Scraping-Klausel, <b>kein Crawl-delay</b>. Die Wartezeit in
/// <see cref="CrawlDelay"/> ist deshalb keine Auflage, sondern eigene Zurueckhaltung: 81
/// Detailseiten hintereinander sind fuer eine Verbandsseite sonst eine Lastspitze.</para>
/// </summary>
public class SjakkCalendarService
{
    internal const string AllowedHost = "www.sjakk.no";

    private const string FeedUrl = "https://www.sjakk.no/aktiviteter-feed.rss";

    /// <summary>
    /// Pause vor jedem DETAILabruf. Die robots.txt der Quelle nennt keine — das hier ist eigene
    /// Zurueckhaltung, nicht ihre Auflage. Der Feed selbst ist EIN Abruf und wartet nicht.
    /// </summary>
    internal static readonly TimeSpan CrawlDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Deckel gegen einen Feed, der eines Tages das gesamte Archiv doppelt fuehrt. Heute stehen
    /// 1000 Items darin, und das ist sichtbar die Obergrenze der Quelle selbst.
    /// </summary>
    internal const int MaxItems = 5000;

    private readonly HttpClient _http;
    private readonly ILogger<SjakkCalendarService> _log;

    public SjakkCalendarService(HttpClient http, ILogger<SjakkCalendarService> log)
    {
        _http = http;
        _log = log;
    }

    // ----- Der Feed ----------------------------------------------------------

    /// <summary>
    /// Alle Termine, die am <paramref name="from"/> noch laufen oder spaeter beginnen. EIN Abruf
    /// fuer den gesamten Bestand — gefiltert wird hier, nicht bei der Quelle (sie kennt keinen
    /// Zeitraum-Parameter).
    /// </summary>
    public async Task<List<ParsedSjakkEvent>> FetchAsync(DateOnly from, CancellationToken ct = default)
    {
        var target = new Uri(FeedUrl);
        EnsureAllowedTarget(target);

        using var response = await _http.GetAsync(target, ct);
        var xml = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();

        var all = ParseFeed(xml);
        var upcoming = all.Where(e => e.EndDate >= from).OrderBy(e => e.StartDate).ToList();

        _log.LogInformation("sjakk.no-Aktivitaeten ab {From}: {Count} von {Total} Terminen",
            from, upcoming.Count, all.Count);
        return upcoming;
    }

    /// <summary>
    /// Die Items des Feeds. Ein Item ohne Termin wird uebergangen — der Feed fuehrt einzelne
    /// Eintraege ohne <c>ev:startdate</c> (gemessen: einen von 1000), und ein Turnier ohne Datum
    /// hat im Kalender keinen Platz.
    /// </summary>
    internal static List<ParsedSjakkEvent> ParseFeed(string xml)
    {
        XDocument document;
        try { document = XDocument.Parse(RepairFeedXml(xml)); }
        catch (System.Xml.XmlException) { return []; }

        var results = new List<ParsedSjakkEvent>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in document.Descendants("item").Take(MaxItems))
        {
            var link = Collapse(Value(item, "link") ?? Value(item, "guid"));
            var slug = SlugOf(link);
            if (slug is null || !seen.Add(slug)) continue;

            var name = Collapse(HttpUtility.HtmlDecode(Value(item, "title")));
            if (name.Length == 0) continue;

            var start = ParseDate(ValueLocal(item, "startdate"));
            if (start is null) continue;
            var end = ParseDate(ValueLocal(item, "enddate"));

            results.Add(new ParsedSjakkEvent
            {
                EventId = slug,
                Name = name,
                StartDate = start.Value,
                EndDate = end is { } e && e >= start.Value ? e : start.Value,
                Url = $"https://{AllowedHost}/aktiviteter/{slug}",
            });
        }
        return results;
    }

    /// <summary>
    /// Die beiden Formfehler des Feeds beheben, damit ein XML-Leser ihn ueberhaupt annimmt:
    /// der Zeilenumbruch VOR der XML-Deklaration und der nirgends deklarierte Namensraum
    /// <c>ev:</c>. Ohne das ist der Feed fuer <c>XDocument</c> kein XML.
    ///
    /// <para>Die Deklaration wird nur ergaenzt, wenn sie FEHLT — richtet die Quelle ihren Feed
    /// eines Tages, darf hier kein zweites <c>xmlns:ev</c> entstehen (das waere wieder ein
    /// Formfehler, nur ein anderer).</para>
    /// </summary>
    internal static string RepairFeedXml(string xml)
    {
        var text = (xml ?? "").TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (text.Contains("xmlns:ev=", StringComparison.OrdinalIgnoreCase)) return text;

        var root = RssRootPattern.Match(text);
        return root.Success
            ? text[..root.Index] + "<rss xmlns:ev=\"urn:x-sjakk:event\" " + text[(root.Index + 5)..]
            : text;
    }

    private static readonly Regex RssRootPattern = new(@"<rss\s", RegexOptions.Compiled);

    /// <summary>
    /// Der Adressbestandteil einer Termin-Adresse. Bewusst eng: alles mit Schraegstrich,
    /// Fragezeichen oder Doppelpunkt darin ist kein Adressbestandteil, sondern ein Weg, den
    /// Detailabruf woanders hin zu schicken.
    /// </summary>
    internal static string? SlugOf(string? link)
    {
        if (link is not { Length: > 0 }) return null;

        var match = SlugPattern.Match(link.Trim());
        if (!match.Success) return null;

        var slug = match.Groups[1].Value;
        return SlugShapePattern.IsMatch(slug) ? slug : null;
    }

    private static readonly Regex SlugPattern =
        new(@"/aktiviteter/([^/?#]+)/?$", RegexOptions.Compiled);

    /// <summary>Norwegische Buchstaben gehoeren dazu — „horten-bgp-høst-2027" ist der Normalfall.</summary>
    private static readonly Regex SlugShapePattern =
        new(@"^[\p{L}\p{N}._~-]{1,120}$", RegexOptions.Compiled);

    // ----- Die Detailseite ---------------------------------------------------

    /// <summary>
    /// Die Detailseite EINES Termins. <c>null</c>, wenn die Seite nicht lesbar ist — ein Ausfall
    /// hier kostet Ort, Verein und Bedenkzeit, aber nicht den Termin selbst (den hat der Feed
    /// schon).
    /// </summary>
    public async Task<ParsedSjakkDetail?> FetchDetailAsync(string slug, CancellationToken ct = default)
    {
        if (SlugShapePattern.IsMatch(slug ?? "") is false)
            throw new InvalidOperationException($"Refusing malformed slug: {slug}");

        await Task.Delay(CrawlDelay, ct);

        var target = new Uri($"https://{AllowedHost}/aktiviteter/{slug}");
        EnsureAllowedTarget(target);

        using var response = await _http.GetAsync(target, ct);
        var html = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            _log.LogWarning("sjakk.no: Detailseite {Slug} antwortete {Status}", slug, response.StatusCode);
            return null;
        }

        return ParseDetail(html);
    }

    /// <summary>
    /// Die Angaben-Tabelle der Detailseite: je Zeile eine Beschriftung und ein Wert
    /// (<c>&lt;tr&gt;&lt;td&gt;Spillsted&lt;/td&gt;&lt;td&gt;…&lt;/td&gt;&lt;/tr&gt;</c>).
    ///
    /// <para>Gelesen wird ueber die BESCHRIFTUNG, nicht ueber die Reihenfolge: die Zeilen fehlen
    /// einzeln (nur 17 von 80 Terminen haben ueberhaupt ein „Spillsted"), und die Tabelle waere
    /// bei jedem Termin anders lang.</para>
    /// </summary>
    internal static ParsedSjakkDetail? ParseDetail(string html)
    {
        var cells = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match row in RowPattern.Matches(html ?? ""))
        {
            var label = Strip(row.Groups[1].Value);
            var value = Strip(row.Groups[2].Value);
            if (label.Length == 0 || value.Length == 0) continue;
            cells.TryAdd(label.TrimEnd(':'), value);
        }
        if (cells.Count == 0) return null;

        var system = cells.GetValueOrDefault("Spillesystem");
        return new ParsedSjakkDetail
        {
            Venue = Empty(cells.GetValueOrDefault("Spillsted")),
            Organizer = Empty(cells.GetValueOrDefault("Arrangør")),
            TimeControl = Empty(cells.GetValueOrDefault("Betenkningstid")),
            Rounds = RoundsOf(system),
            System = SystemOf(system),
            Website = Empty(cells.GetValueOrDefault("Nettside")),
        };
    }

    private static readonly Regex RowPattern =
        new(@"<tr\b[^>]*>\s*<td\b[^>]*>(.*?)</td>\s*<td\b[^>]*>(.*?)</td>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// „9 runder sveitser", „6, FIDE Dutch Swiss", „5" — die Zahl vor dem Wort, sonst eine
    /// alleinstehende Zahl am Anfang. Der Rest der Zeile ist Freitext und darf keine Zahl
    /// beisteuern: „10 runder med 5 runder mandag und 5 runder tirsdag" sind zehn Runden.
    /// </summary>
    internal static int? RoundsOf(string? text)
    {
        if (text is not { Length: > 0 }) return null;

        var match = RoundsPattern.Match(text);
        if (!match.Success) match = LeadingNumberPattern.Match(text);

        return match.Success && int.TryParse(match.Groups[1].Value, out var rounds)
               && rounds is >= 1 and <= 30 ? rounds : null;
    }

    private static readonly Regex RoundsPattern =
        new(@"(\d{1,2})\s*(?:runder|runde|rounds|round)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LeadingNumberPattern = new(@"^\s*(\d{1,2})\b", RegexOptions.Compiled);

    /// <summary>
    /// „sveitser"/„swiss"/„monrad" ist Schweizer System — <b>monrad</b> ist der skandinavische
    /// Name dafuer und stuende sonst als „unbekannt" da.
    /// </summary>
    internal static string? SystemOf(string? text)
    {
        if (text is not { Length: > 0 }) return null;
        if (SwissPattern.IsMatch(text)) return "swiss";
        return RoundRobinPattern.IsMatch(text) ? "roundRobin" : null;
    }

    private static readonly Regex SwissPattern =
        new(@"sveitser|sveiser|swiss|monrad", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RoundRobinPattern =
        new(@"berger|round.?robin|alle mot alle", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ----- Hilfen ------------------------------------------------------------

    private static string? Value(XElement item, string name) => item.Element(name)?.Value;

    /// <summary>
    /// Ein Feld ueber seinen LOKALEN Namen. Der Namensraum, den
    /// <see cref="RepairFeedXml"/> ergaenzt, ist erfunden — sich auf ihn zu beziehen hiesse, die
    /// eigene Reparatur zur Quelle der Wahrheit zu machen.
    /// </summary>
    private static string? ValueLocal(XElement item, string localName) =>
        item.Elements().FirstOrDefault(
            e => e.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))?.Value;

    /// <summary>„2026-10-31T00:00:00+01:00" — gebraucht wird nur der Tag.</summary>
    private static DateOnly? ParseDate(string? text) =>
        DateTimeOffset.TryParse(Collapse(text), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var value)
            ? DateOnly.FromDateTime(value.DateTime)
            : null;

    private static string Strip(string? html) =>
        Collapse(HttpUtility.HtmlDecode(Regex.Replace(html ?? "", "<[^>]+>", " ")) ?? "")
            .Trim('\u00a0', ' ', ',');

    private static string? Empty(string? text) => text is { Length: > 0 } ? text : null;

    internal static void EnsureAllowedTarget(Uri url) => SourceHostGuard.Ensure(url, AllowedHost);
}
