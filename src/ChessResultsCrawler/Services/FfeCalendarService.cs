using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using static ChessResultsCrawler.Services.SourceText;

namespace ChessResultsCrawler.Services;

/// <summary>Ein Turnier aus der MONATSLISTE des franzoesischen Verbands (FFE).</summary>
public class ParsedFfeEvent
{
    /// <summary>
    /// Die FFE-Nummer aus dem Link auf die Turnierseite (<c>FicheTournoi.aspx?Ref=72680</c>).
    ///
    /// <para>Sie ist die Identitaet des Eintrags — an vier gemessenen Monatslisten tragen sie
    /// <b>alle</b> Zeilen, und sie steht auch in der ersten Spalte der Tabelle. Ohne sie muesste
    /// man ueber Name und Termin zuordnen, und ein umbenanntes Turnier waere jede Nacht ein
    /// neues.</para>
    /// </summary>
    public string EventId { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>
    /// Der Starttag. Die Liste nennt nur Tag und Monat („31 oct.") — das JAHR steht in der
    /// Zwischenueberschrift des Monatsblocks („octobre 2026").
    /// </summary>
    public DateOnly StartDate { get; set; }

    /// <summary>Spielort in der Versalschrift der Quelle („SAINT BRISSON").</summary>
    public string City { get; set; } = "";

    /// <summary>
    /// Die zweistellige DEPARTEMENTS-Nummer („58", „09"). Sie ist gleichzeitig der Anfang jeder
    /// franzoesischen Postleitzahl des Departements — und der einzige Unterscheider, den die
    /// LISTE bei gleichnamigen Orten mitbringt.
    /// </summary>
    public string Department { get; set; } = "";

    /// <summary>
    /// Wer das Turnier homologiert: „FFE" oder ein Ligue-Kuerzel („EST", „NAQ", „NOR", „BRE",
    /// „IDF"). Das ist bewusst NICHT als Region uebernommen — die Ligue sagt, wer die Wertung
    /// fuehrt, nicht wo gespielt wird, und in vier von fuenf Faellen steht dort ohnehin „FFE".
    /// </summary>
    public string? HomologatedBy { get; set; }

    /// <summary>Adresse der Turnierseite — zugleich der Beleg, dass die Detailseite gelesen wurde.</summary>
    public string Url { get; set; } = "";
}

/// <summary>
/// Die Turnierseite (<c>FicheTournoi.aspx</c>) EINES Turniers. Alles, was die Monatsliste nicht
/// hat: Enddatum, Rundenzahl, Bedenkzeit, Turniersystem und die Anschrift mit Postleitzahl.
/// </summary>
public class ParsedFfeDetail
{
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }

    public string? City { get; set; }
    public string? Department { get; set; }

    /// <summary>
    /// Anschrift als Freitext — der Grund, die Detailseite ueberhaupt zu holen: sie traegt in
    /// 7 von 8 gemessenen Faellen die POSTLEITZAHL, und das ist der einzige Weg der Verortung,
    /// der auf wenige Kilometer genau ist.
    /// </summary>
    public string? Address { get; set; }

    /// <summary>Bedenkzeit im Klartext („60' + [30'']", „1h30 + [30'']", „15' + [10'']").</summary>
    public string? TimeControl { get; set; }

    public int? Rounds { get; set; }

    /// <summary>Paarungsverfahren im Wortlaut der Quelle („Suisse", „S.A.D.", „Haley").</summary>
    public string? PairingSystem { get; set; }
}

/// <summary>
/// Der Turnierkalender des franzoesischen Verbands (Federation Francaise des Echecs).
///
/// <para><b>Warum diese Quelle.</b> Sie ist der groesste Einzel-Zugewinn der ganzen
/// Quellenrunde: von 40 gegengeprueften FFE-Turnieren stehen <b>zwei</b> auf chess-results (5 %).
/// Die FFE fuehrt die nicht-FIDE-gewerteten Vereins- und Ligue-Turniere, und die kennt
/// chess-results praktisch gar nicht.</para>
///
/// <para><b>Die Falle, die diese Seite stellt:</b> die nackte <c>ListeTournois.aspx</c> zeigt nur
/// ein rollierendes Fenster der naechsten rund 99 Turniere. Erst
/// <c>?Action=RES&amp;Mois=&lt;1-12&gt;&amp;Annee=&lt;Jahr&gt;</c> liefert vollstaendige,
/// stabil verlinkbare Monatslisten. Und <c>Action=RES</c> heisst „Resultate", ist aber eine
/// ANKUENDIGUNG — der Name klingt rueckwaerts, die Daten sind vorwaerts.</para>
///
/// <para><b>Wie weit der Kalender reicht (gemessen 09.09.2026).</b> September 105, Oktober 43,
/// November 12, Dezember 10 — danach Januar 0, Februar 8, Maerz 1, April 3, Mai 0, Juni 2, ab
/// Juli 0. Der Ertrag steckt also in den naechsten vier Monaten; weiter voraus stehen nur
/// vereinzelte Fruehstarter. Ein Durchgang muss deshalb ROLLIEREND nachfassen, ein einmaliger
/// Blick weit voraus bringt nichts.</para>
///
/// <para><b>Kosten.</b> Ein GET je Monat, 18–48 kB. Eine Monatsseite fasst 40 Zeilen; die
/// wenigen Monate darueber brauchen ein ASP.NET-Postback je Folgeseite. Zwoelf Monate ab heute
/// kosten damit 12 GET + 3 POST, zusammen rund 370 kB. Die Turnierseiten kosten einen Abruf je
/// Turnier und werden deshalb einzeln angefragt — der Aufrufer entscheidet, fuer welche.</para>
///
/// <para><b>Das Blaettern ist einfacher als bei chess-results.</b> Es braucht
/// <c>__EVENTTARGET</c>, <c>__EVENTARGUMENT</c> und <c>__VIEWSTATE</c> — aber KEIN
/// <c>__EVENTVALIDATION</c> und kein Cookie. Nachgemessen: das <c>__VIEWSTATE</c> der ERSTEN
/// Seite bedient auch Seite 3, es muss also nicht von Seite zu Seite fortgeschrieben werden.</para>
///
/// <para><b>Die Liste ist absteigend nach Termin sortiert</b> — Seite 1 traegt das Monatsende.
/// Darum wird nur so weit geblaettert, bis eine Seite ganz vor dem Stichtag liegt: im laufenden
/// Monat spart das die Seiten, auf denen nur Vergangenes steht.</para>
///
/// <para><b>Rechtslage (2026-09-09 geprueft):</b> <c>robots.txt</c> auf beiden Hostnamen 404 —
/// nichts gesperrt, fuer niemanden. ABER: die „Mentions legales" der FFE berufen sich in Punkt 7
/// ausdruecklich auf das sui-generis-DATENBANKRECHT (Gesetz vom 1. Juli 1998, Umsetzung der
/// EU-Richtlinie 96/9/EG), Punkt 5 verbietet Vervielfaeltigung ohne schriftliche Genehmigung.
/// Der Text liest sich wie generisches franzoesisches Vereins-Boilerplate („Loi 1901") und nicht
/// wie eine gegen die Extraktion des Turnierkalenders gerichtete Klausel — ein Verband
/// veroeffentlicht seine Ankuendigungen ja aktiv, ohne Anmeldung und ohne technische Huerde.
/// Er ist trotzdem echt, und er ist der Grund, hier sparsam zu holen: nur die Liste rollierend,
/// die Turnierseite nur einmal je Turnier. Eine kurze Anfrage beim oeffentlich genannten
/// Webmaster bleibt vor dem Dauerbetrieb das saubere Vorgehen.</para>
/// </summary>
public class FfeCalendarService
{
    internal const string AllowedHost = "www.echecs.asso.fr";

    private const string BaseUrl = "https://www.echecs.asso.fr";

    /// <summary>
    /// Wie viele Folgeseiten eines Monats hoechstens geholt werden. Ein kuenftiger Monat hat
    /// gemessen hoechstens drei; ein VERGANGENER kann acht haben (Maerz 2025: 309 Turniere). Der
    /// Deckel ist eine Reissleine gegen einen kaputten Pager, keine fachliche Grenze.
    /// </summary>
    internal const int MaxPagesPerMonth = 10;

    /// <summary>
    /// Pause zwischen zwei Abrufen desselben Hosts. Zwoelf Monate sind zwoelf Anfragen — das
    /// waere ohne Pause ein Stoss, und die Datenbankschutz-Klausel ist ein Grund, hier hoeflich
    /// zu sein statt schnell.
    /// </summary>
    private static readonly TimeSpan RequestDelay = TimeSpan.FromMilliseconds(1200);

    private readonly HttpClient _http;
    private readonly ILogger<FfeCalendarService> _log;

    public FfeCalendarService(HttpClient http, ILogger<FfeCalendarService> log)
    {
        _http = http;
        _log = log;
    }

    /// <summary>
    /// Die Turniere ab <paramref name="from"/> ueber <paramref name="months"/> Monatslisten
    /// hinweg. Alles vor dem Stichtag faellt weg — der laufende Monat enthaelt auch Vergangenes.
    /// </summary>
    public async Task<List<ParsedFfeEvent>> FetchAsync(
        DateOnly from, int months, CancellationToken ct = default)
    {
        var results = new List<ParsedFfeEvent>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cursor = new DateOnly(from.Year, from.Month, 1);

        for (var i = 0; i < months; i++, cursor = cursor.AddMonths(1))
        {
            ct.ThrowIfCancellationRequested();
            if (i > 0) await Task.Delay(RequestDelay, ct);

            var html = await FetchMonthPageAsync(cursor.Year, cursor.Month, null, null, ct);
            var viewState = ParseViewState(html);
            var pages = ParsePageCount(html);

            for (var page = 1; page <= Math.Min(pages, MaxPagesPerMonth); page++)
            {
                if (page > 1)
                {
                    await Task.Delay(RequestDelay, ct);
                    html = await FetchMonthPageAsync(
                        cursor.Year, cursor.Month, page, viewState, ct);
                }

                var rows = ParseList(html, cursor.Year);
                foreach (var row in rows.Where(r => r.StartDate >= from))
                    if (seen.Add(row.EventId))
                        results.Add(row);

                // Absteigend sortiert: liegt schon die letzte Zeile dieser Seite vor dem
                // Stichtag, steht auf den folgenden Seiten nur noch aelteres.
                if (rows.Count > 0 && rows[^1].StartDate < from) break;
                if (rows.Count == 0) break;
            }
        }

        _log.LogInformation("FFE-Kalender ab {From} ueber {Months} Monate: {Count} Turniere",
            from, months, results.Count);
        return results;
    }

    /// <summary>
    /// Die Turnierseite EINES Turniers. <c>null</c>, wenn sie nicht lesbar ist — ein Fehlschlag
    /// hier ist kein Grund, den ganzen Durchgang abzubrechen.
    /// </summary>
    public async Task<ParsedFfeDetail?> FetchDetailAsync(string eventId, CancellationToken ct = default)
    {
        if (!EventIdPattern.IsMatch(eventId))
            throw new InvalidOperationException($"Refusing malformed tournament id: {eventId}");

        var target = new Uri(DetailUrl(eventId));
        EnsureAllowedTarget(target);

        using var response = await _http.GetAsync(target, ct);
        var html = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) return null;

        return await ParseDetailAsync(html);
    }

    internal static string DetailUrl(string eventId) => $"{BaseUrl}/FicheTournoi.aspx?Ref={eventId}";

    private static readonly Regex EventIdPattern = new(@"^\d{1,9}$", RegexOptions.Compiled);

    /// <summary>
    /// Eine Monatsseite holen. Seite 1 ist ein GET; jede Folgeseite ein klassisches
    /// ASP.NET-Postback — mit <c>__VIEWSTATE</c>, aber ohne <c>__EVENTVALIDATION</c> und ohne
    /// Cookie. Das <c>__VIEWSTATE</c> der ersten Seite gilt fuer alle weiteren (nachgemessen bis
    /// Seite 3), es wird also nicht fortgeschrieben.
    /// </summary>
    private async Task<string> FetchMonthPageAsync(
        int year, int month, int? page, string? viewState, CancellationToken ct)
    {
        var target = new Uri($"{BaseUrl}/ListeTournois.aspx?Action=RES&Mois={month}&Annee={year}");
        EnsureAllowedTarget(target);

        using var request = new HttpRequestMessage(
            page is > 1 ? HttpMethod.Post : HttpMethod.Get, target);

        if (page is > 1)
        {
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__EVENTTARGET"] = "ctl00$ContentPlaceHolderMain$PagerHeader",
                ["__EVENTARGUMENT"] = page.Value.ToString(CultureInfo.InvariantCulture),
                ["__VIEWSTATE"] = viewState ?? "",
            });
        }

        using var response = await _http.SendAsync(request, ct);
        var html = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();
        return html;
    }

    // ----- Die Monatsliste ---------------------------------------------------

    /// <summary>
    /// Die Monatsliste auseinandernehmen.
    ///
    /// <para>Die Tabelle ist erfreulich altmodisch: acht Spalten, eine Zeile je Turnier
    /// (<c>tr.liste_clair</c> / <c>tr.liste_fonce</c>) — Nummer, Ort, Departement, Name mit dem
    /// Link auf die Turnierseite, Starttag, Homologation, zwei unsichtbare Spalten.</para>
    ///
    /// <para><b>Das Jahr entsteht hier.</b> Die Datumsspalte nennt nur Tag und Monatskuerzel
    /// („31 oct."); das Jahr steht in der Zwischenueberschrift des Monatsblocks
    /// (<c>td.liste_titre</c>, „octobre 2026"). Gibt es keine, gilt
    /// <paramref name="fallbackYear"/> — die Jahreszahl der Anfrage. Weicht der Monat der Zeile
    /// von dem der Ueberschrift um mehr als ein halbes Jahr ab, ist der Jahreswechsel gemeint
    /// und das Jahr wird um eins verschoben.</para>
    /// </summary>
    internal static List<ParsedFfeEvent> ParseList(string html, int fallbackYear)
    {
        var results = new List<ParsedFfeEvent>();
        var headerMonth = 0;
        var headerYear = fallbackYear;

        foreach (Match row in RowPattern.Matches(html))
        {
            var body = row.Groups[1].Value;
            var cells = CellPattern.Matches(body)
                .Select(c => Collapse(StripTags(c.Groups[1].Value)))
                .ToList();

            if (body.Contains("liste_titre", StringComparison.Ordinal))
            {
                var (m, y) = ParseMonthHeading(cells.FirstOrDefault());
                if (m > 0) { headerMonth = m; headerYear = y ?? headerYear; }
                continue;
            }

            if (cells.Count < 6) continue;

            var link = RefPattern.Match(body);
            var eventId = link.Success ? link.Groups[1].Value : Collapse(cells[0]);
            if (!EventIdPattern.IsMatch(eventId)) continue;

            var start = ParseListDate(cells[4], headerMonth, headerYear);
            if (start is null) continue;

            results.Add(new ParsedFfeEvent
            {
                EventId = eventId,
                Name = cells[3],
                StartDate = start.Value,
                City = cells[1],
                Department = cells[2],
                HomologatedBy = cells[5].Length > 0 ? cells[5] : null,
                Url = DetailUrl(eventId),
            });
        }

        return results;
    }

    /// <summary>
    /// Die Zeilen der Trefferliste. Zwei Sorten: die Zwischenueberschrift des Monats
    /// (<c>td.liste_titre</c>) und die Turnierzeilen. Beide werden in DOKUMENTREIHENFOLGE
    /// gebraucht — die Ueberschrift traegt das Jahr der Zeilen darunter —, deshalb ein
    /// gemeinsames Muster statt zweier Abfragen.
    /// </summary>
    private static readonly Regex RowPattern = new(
        @"<tr\b[^>]*>(.*?)</tr>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CellPattern = new(
        @"<td\b[^>]*>(.*?)</td>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RefPattern = new(
        @"FicheTournoi\.aspx\?Ref=(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Die Zahl der Seiten aus dem Pager (<c>__doPostBack(…,'2')</c>).</summary>
    internal static int ParsePageCount(string html)
    {
        var max = 1;
        foreach (Match m in PagerPattern.Matches(html))
            if (int.TryParse(m.Groups[1].Value, out var n) && n > max) max = n;
        return max;
    }

    private static readonly Regex PagerPattern = new(
        @"PagerHeader['""]\s*,\s*['""](\d+)['""]", RegexOptions.Compiled);

    /// <summary>Das <c>__VIEWSTATE</c> der Seite — der Ausweis fuer jedes Postback.</summary>
    internal static string? ParseViewState(string html)
    {
        var m = ViewStatePattern.Match(html);
        return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value) : null;
    }

    private static readonly Regex ViewStatePattern = new(
        @"id=""__VIEWSTATE""\s+value=""([^""]*)""", RegexOptions.Compiled);

    /// <summary>„octobre 2026" — die Zwischenueberschrift eines Monatsblocks.</summary>
    internal static (int Month, int? Year) ParseMonthHeading(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (0, null);

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return (0, null);

        var month = MonthOf(parts[0]);
        int? year = parts.Length > 1 && int.TryParse(parts[1], out var y) ? y : null;
        return (month, year);
    }

    /// <summary>
    /// „31 oct." — Tag und Monatskuerzel; das Jahr kommt von aussen. Nur das Kuerzel wird
    /// gelesen: es ist die verlaesslichere Angabe als die Ueberschrift, weil es an der Zeile
    /// selbst haengt.
    /// </summary>
    internal static DateOnly? ParseListDate(string? text, int headerMonth, int headerYear)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var m = ListDatePattern.Match(text);
        if (!m.Success) return null;

        var day = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var month = MonthOf(m.Groups[2].Value);
        if (month == 0) month = headerMonth;
        if (month == 0) return null;

        // Jahreswechsel: steht die Zeile im Januar und die Ueberschrift im Dezember (oder
        // umgekehrt), gehoert sie in das andere Jahr.
        var year = headerYear;
        if (headerMonth > 0)
        {
            if (month - headerMonth > 6) year--;
            else if (headerMonth - month > 6) year++;
        }

        return day >= 1 && day <= DateTime.DaysInMonth(year, month)
            ? new DateOnly(year, month, day)
            : null;
    }

    private static readonly Regex ListDatePattern = new(
        @"(\d{1,2})\s*([\p{L}]+)\.?", RegexOptions.Compiled);

    // ----- Die Turnierseite --------------------------------------------------

    /// <summary>
    /// Die Turnierseite auseinandernehmen. Jedes Feld haengt an einer eigenen, sprechenden
    /// Element-Kennung (<c>ctl00_ContentPlaceHolderMain_LabelCadence</c>) — das ist die stabilste
    /// Form, die eine WebForms-Seite anbietet, stabiler jedenfalls als die Reihenfolge der
    /// Tabellenzeilen.
    ///
    /// <para>Ein Feld kann FEHLEN: von neun gemessenen Turnieren hatte eines keine Anschrift.
    /// Fehlend heisst <c>null</c>, nicht leer — das ist der Unterschied, an dem
    /// <c>FillIfEmpty</c> auf der RookHub-Seite haengt.</para>
    /// </summary>
    internal static async Task<ParsedFfeDetail?> ParseDetailAsync(string html)
    {
        var context = BrowsingContext.New(Configuration.Default);
        var document = await context.OpenAsync(req => req.Content(html));

        var name = Label(document, "LabelNom");
        if (name is null) return null;

        var (start, end) = ParseDetailDates(Label(document, "LabelDates"));
        var (department, city) = SplitLieu(Label(document, "LabelLieu"));

        return new ParsedFfeDetail
        {
            StartDate = start,
            EndDate = end,
            City = city,
            Department = department,
            Address = Label(document, "LabelAdresse"),
            TimeControl = Label(document, "LabelCadence"),
            Rounds = ParseRounds(Label(document, "LabelNbrRondes")),
            PairingSystem = Label(document, "LabelAppariements"),
        };
    }

    private static string? Label(IDocument document, string id)
    {
        var value = Collapse(document.QuerySelector($"#ctl00_ContentPlaceHolderMain_{id}")?.TextContent);
        return value.Length > 0 ? value : null;
    }

    /// <summary>„58 - SAINT BRISSON" — Departements-Nummer und Ort in einem Feld.</summary>
    internal static (string? Department, string? City) SplitLieu(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null);

        var m = LieuPattern.Match(text);
        return m.Success
            ? (m.Groups[1].Value, Collapse(m.Groups[2].Value))
            : (null, Collapse(text));
    }

    private static readonly Regex LieuPattern = new(
        @"^\s*([0-9AB]{2,3})\s*-\s*(.+)$", RegexOptions.Compiled);

    /// <summary>
    /// „samedi 31 octobre 2026 - dimanche 01 novembre 2026" — Beginn UND Ende, ausgeschriebene
    /// franzoesische Monatsnamen. Eintaegige Turniere nennen denselben Tag zweimal.
    ///
    /// <para>Das Enddatum ist der eigentliche Grund, diese Seite zu holen: die Monatsliste kennt
    /// nur den Starttag, und ohne Ende steht ein dreitaegiges Open im Kalender nur an seinem
    /// ersten Tag.</para>
    /// </summary>
    internal static (DateOnly? Start, DateOnly? End) ParseDetailDates(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null);

        var dates = new List<DateOnly>();
        foreach (Match m in DetailDatePattern.Matches(text))
        {
            var day = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var month = MonthOf(m.Groups[2].Value);
            var year = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            if (month == 0 || year is < 1900 or > 2200) continue;
            if (day < 1 || day > DateTime.DaysInMonth(year, month)) continue;
            dates.Add(new DateOnly(year, month, day));
        }

        return dates.Count switch
        {
            0 => (null, null),
            1 => (dates[0], dates[0]),
            _ => (dates[0], dates[^1]),
        };
    }

    private static readonly Regex DetailDatePattern = new(
        @"(\d{1,2})\s+([\p{L}]+)\.?\s+(\d{4})", RegexOptions.Compiled);

    private static int? ParseRounds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Regex.Match(text, @"\d+");
        return m.Success && int.TryParse(m.Value, out var n) && n is > 0 and < 100 ? n : null;
    }

    // ----- Monatsnamen -------------------------------------------------------

    /// <summary>
    /// Franzoesische Monatsnamen — lang (Turnierseite) und kurz (Monatsliste).
    ///
    /// <para><b>Warum die Tabelle hier steht statt <c>CultureInfo("fr-FR")</c> zu benutzen:</b>
    /// die abgekuerzten Monatsnamen kommen dort aus ICU, und deren Schreibweise hat sich zwischen
    /// ICU-Fassungen schon geaendert (mit und ohne Punkt). Ein Container mit einer anderen
    /// ICU-Version wuerde dann still keine Termine mehr lesen. Alle zwoelf Kurzformen sind an der
    /// echten Seite nachgemessen (je ein Monat Januar bis Dezember abgerufen).</para>
    /// </summary>
    private static readonly Dictionary<string, int> Months = new(StringComparer.Ordinal)
    {
        ["janv"] = 1, ["janvier"] = 1,
        ["fevr"] = 2, ["fevrier"] = 2,
        ["mars"] = 3,
        ["avr"] = 4, ["avril"] = 4,
        ["mai"] = 5,
        ["juin"] = 6,
        ["juil"] = 7, ["juillet"] = 7,
        ["aout"] = 8,
        ["sept"] = 9, ["septembre"] = 9,
        ["oct"] = 10, ["octobre"] = 10,
        ["nov"] = 11, ["novembre"] = 11,
        ["dec"] = 12, ["decembre"] = 12,
    };

    /// <summary>Monatsnummer aus dem Namen; 0, wenn es keiner ist.</summary>
    internal static int MonthOf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        return Months.TryGetValue(Deaccent(text.Trim().TrimEnd('.')), out var m) ? m : 0;
    }

    /// <summary>
    /// „fevrier" aus „Février". Die Seite mischt Kodierungen und Entities; ein Vergleich ohne
    /// Akzente ist der Weg, der bei beiden ankommt.
    /// </summary>
    internal static string Deaccent(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string StripTags(string html) =>
        System.Net.WebUtility.HtmlDecode(Regex.Replace(html, @"<[^>]+>", " "));

    /// <summary>Derselbe Schutz wie bei den anderen Hosts: https und ein exakter Hostvergleich.</summary>
    internal static void EnsureAllowedTarget(Uri url) => SourceHostGuard.Ensure(url, AllowedHost);
}
