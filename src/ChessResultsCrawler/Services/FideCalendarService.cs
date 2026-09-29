using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using static ChessResultsCrawler.Services.SourceText;

namespace ChessResultsCrawler.Services;

/// <summary>Ein Ereignis aus dem FIDE-Kalender.</summary>
public class ParsedFideEvent
{
    /// <summary>FIDE-Ereignis-Nummer (aus <c>calendar.php?id=</c>) — der Schluessel dort.</summary>
    public string EventId { get; set; } = "";
    public string Name { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    /// <summary>Stadt, wie FIDE sie schreibt. Leer bei Online-Ereignissen.</summary>
    public string? City { get; set; }
    /// <summary>Laenderkuerzel in FIDE-Schreibweise (drei Buchstaben); „ONL" = online.</summary>
    public string? Country { get; set; }
}

/// <summary>
/// Die DETAILangaben eines FIDE-Ereignisses — alles, was die Jahresansicht nicht hergibt.
///
/// <para>Jedes Feld ist optional, und das ist keine Vorsicht, sondern gemessen: die
/// 46. Schacholympiade (id=5072) hat weder Bedenkzeit-Beschreibung noch Runden- noch
/// Teilnehmerzahl, waehrend ein Norm-Turnier (id=17954) alle zwoelf Zeilen traegt.</para>
/// </summary>
public class ParsedFideEventDetail
{
    public string EventId { get; set; } = "";

    /// <summary>„Over-the-Board Tournament", „Online", „Hybrid", „Meeting".</summary>
    public string? EventType { get; set; }

    /// <summary>„Standard", „Rapid" oder „Blitz" — FIDEs eigene Klasse, kein Rohtext.</summary>
    public string? TimeControl { get; set; }

    /// <summary>Die ausgeschriebene Bedenkzeit („90 minutes with 30 second increment…").</summary>
    public string? TimeControlText { get; set; }

    /// <summary>
    /// „Round-Robin", „Swiss-System", „Other" — das TURNIERSYSTEM.
    ///
    /// <para>Es sagt NICHTS ueber Einzel gegen Mannschaft: die 46. Schacholympiade steht auf
    /// „Other", die Team-Blitz-WM auf „Round-Robin". Wer hier Mannschaften herauslesen will,
    /// liest etwas, das nicht drinsteht.</para>
    /// </summary>
    public string? System { get; set; }

    public int? Rounds { get; set; }
    public int? Players { get; set; }
    public string? Country { get; set; }
    public string? City { get; set; }

    /// <summary>
    /// Die Anschrift des Spielorts — der wertvollste Teil, weil sie eine POSTLEITZAHL traegt
    /// („Via Iberica, 69, 77, 50012 Zaragoza, Spain"). Die Verortung hat mit einer PLZ ihren
    /// genauesten Weg, und der greift bei FIDE-Eintraegen sonst nie.
    /// </summary>
    public string? VenueAddress { get; set; }

    public string? Website { get; set; }
}

/// <summary>
/// Der FIDE-Kalender als ZWEITE Turnierquelle.
///
/// <para><b>Warum ueberhaupt.</b> Das Verzeichnis lebt aus der chess-results-Turniersuche, und die
/// grossen internationalen Turniere stehen dort nicht — jedenfalls nicht Monate vorher. Am
/// 2026-09-07 gemessen: von 139 FIDE-Ereignissen des Jahres 2026 fanden sich <b>132 nicht</b> im
/// eigenen Bestand, und darunter Tata Steel, Rilton Cup, Prague Masters, Aeroflot Open, das
/// Frauen-Kandidatenturnier und die Freestyle-WM. Das ist der Zugewinn.</para>
///
/// <para><b>Der richtige Endpunkt ist nicht der naheliegende.</b> `calendar.php` mit
/// <c>show=table</c> bzw. <c>show=apilist</c> liefert saubere JSON-Zeilen — aber aus einer
/// Tabelle, die bei 2025 stehen geblieben ist (661 Ereignisse, keines in der Zukunft). Gepflegt
/// wird die JAHRESansicht: <c>show=showYear</c> mit <c>page=&lt;Jahr&gt;</c>, dieselbe
/// `calendar_server.php`. Sie liefert HTML statt JSON, dafuer aktuelle Daten: 2026 → 143
/// Ereignisse, 2027 → 15.</para>
///
/// <para><b>Ein eigener HttpClient und ein eigener Host-Schutz.</b> Der CrawlerService prueft
/// jeden Hop gegen <c>chess-results.com</c> — richtig fuer ihn, hier waere es falsch. Deshalb ein
/// getrennter Dienst mit derselben Regel fuer <c>calendar.fide.com</c>: kein automatisches
/// Redirect-Folgen, https erzwungen, exakter Hostvergleich.</para>
/// </summary>
public class FideCalendarService
{
    /// <summary>Der einzige zulaessige Host. Exakt verglichen — „calendar.fide.com.attacker.tld" faellt durch.</summary>
    internal const string AllowedHost = "calendar.fide.com";

    private const string ServerUrl = "https://calendar.fide.com/calendar_server.php";

    /// <summary>
    /// Die Jahresansicht traegt nur Tag und Monat („27 Dec - 05 Jan"); das Jahr kommt aus der
    /// Anfrage. Fuer eine Spanne ueber den Jahreswechsel siehe <see cref="ResolveYears"/>.
    /// </summary>
    private static readonly Regex EventPattern = new(
        """href="calendar\.php\?id=(?<id>\d+)"[^>]*>(?<name>[^<]+)</a>""",
        RegexOptions.Compiled);

    private static readonly Regex WhenPattern = new(
        @"^(?<d1>\d{1,2})\s+(?<m1>\w{3})\s*-\s*(?<d2>\d{1,2})\s+(?<m2>\w{3})\s*(?:/\s*(?<place>.*))?$",
        RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly ILogger<FideCalendarService> _log;

    public FideCalendarService(HttpClient http, ILogger<FideCalendarService> log)
    {
        _http = http;
        _log = log;
    }

    /// <summary>
    /// Die Ereignisse EINES Jahres. Ein Seitenabruf, nichts wird gespeichert — wie alles in
    /// diesem Dienst.
    /// </summary>
    public async Task<List<ParsedFideEvent>> FetchYearAsync(int year, CancellationToken ct = default)
    {
        var target = new Uri(ServerUrl);
        EnsureAllowedTarget(target);

        // Genau die Felder, die die Seite selbst schickt (js/tabs.js, loadYear). `cat_filter` und
        // `cat_cont` bleiben WEG: leer mitgeschickt antwortet der Server mit 500.
        var form = new Dictionary<string, string>
        {
            ["country"] = "",
            ["name_filter"] = "",
            ["event_type"] = "all",
            ["time_control"] = "all",
            ["page"] = year.ToString(CultureInfo.InvariantCulture),
            ["show"] = "showYear",
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, target)
        {
            Content = new FormUrlEncodedContent(form),
        };
        // Ohne diesen Kopf antwortet der Server die Rahmenseite statt des Ausschnitts.
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        request.Headers.Referrer = new Uri("https://calendar.fide.com/majorcalendar.php");

        using var response = await _http.SendAsync(request, ct);
        var html = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();

        var events = await ParseYearAsync(html, year);
        _log.LogInformation("FIDE-Kalender {Year}: {Count} Ereignisse", year, events.Count);
        return events;
    }

    /// <summary>
    /// Die Detailangaben EINES Ereignisses holen — ein Abruf je Ereignis.
    ///
    /// <para><b>Warum nicht die Ereignisseite.</b> <c>calendar.php?id=N</c> ist zu 100 % Geruest:
    /// zwei verschiedene Ereignis-Ids liefern byte-identische 84 956 Bytes, ohne Namen, ohne
    /// Tabellenzeile, ohne Datenquelle im Markup. Den Inhalt laedt <c>js/tabs.js</c> ueber genau
    /// diesen Aufruf nach.</para>
    ///
    /// <para><b>Warum nicht gesammelt.</b> Die Jahresansicht traegt die Felder nicht, und ihre
    /// Filter helfen nicht: <c>show=showYear</c> IGNORIERT <c>event_type</c> und
    /// <c>time_control</c> — nachgemessen liefern alle sechs Varianten dieselben 143 Ereignisse.
    /// Der billige Sammel-Trick, den chess-results ueber <c>art=</c> erlaubt, gibt es hier
    /// nicht.</para>
    /// </summary>
    public async Task<ParsedFideEventDetail?> FetchEventAsync(string eventId, CancellationToken ct = default)
    {
        if (!EventIdPattern.IsMatch(eventId)) return null;

        var target = new Uri($"{ServerUrl}?id={eventId}&preview=0");
        EnsureAllowedTarget(target);

        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        // Ohne diesen Kopf antwortet der Server die Rahmenseite statt des Ausschnitts — dieselbe
        // Bedingung wie bei der Jahresansicht.
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        request.Headers.Referrer = new Uri($"https://{AllowedHost}/calendar.php?id={eventId}");

        using var response = await _http.SendAsync(request, ct);
        var html = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();

        var detail = await ParseEventAsync(html, eventId);
        _log.LogInformation(
            "FIDE-Ereignis {EventId}: Bedenkzeit={TimeControl} System={System} Runden={Rounds} Anschrift={HasAddress}",
            eventId, detail.TimeControl ?? "-", detail.System ?? "-", detail.Rounds,
            detail.VenueAddress is not null);
        return detail;
    }

    /// <summary>Ereignis-Nummern sind Zahlen; alles andere kommt nicht in eine URL.</summary>
    private static readonly Regex EventIdPattern = new(@"^\d{1,10}$", RegexOptions.Compiled);

    /// <summary>
    /// Das Detail-Fragment auseinandernehmen. Aufbau je Angabe:
    /// <c>div.event-info-row</c> mit <c>div.event-info-row-left &gt; h5</c> als BESCHRIFTUNG und
    /// <c>div.event-info-row-right</c> als Wert.
    ///
    /// <para><b>Warum ueber die Paare und nicht ueber die Textreihenfolge.</b> „Beschriftung, dann
    /// naechste Textzeile" sieht einfacher aus und ist falsch, sobald ein Feld LEER ist — dann
    /// sammelt es die naechste Beschriftung als Wert ein. An der Team-Blitz-WM (id=14094)
    /// nachgestellt: dort kam auf diesem Weg <c>City = "Venue"</c> heraus. Und leere Felder sind
    /// hier der Normalfall.</para>
    /// </summary>
    internal static async Task<ParsedFideEventDetail> ParseEventAsync(string html, string eventId)
    {
        var context = BrowsingContext.New(Configuration.Default);
        var document = await context.OpenAsync(req => req.Content(html));

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in document.QuerySelectorAll("div.event-info-row"))
        {
            var label = row.QuerySelector(".event-info-row-left h5")?.TextContent.Trim();
            if (string.IsNullOrEmpty(label)) continue;

            var right = row.QuerySelector(".event-info-row-right");
            if (right is null) continue;

            // Der ERSTE nicht leere Absatz: „Address" traegt zwei <p>, das erste ist leer.
            var value = right.QuerySelectorAll("p")
                .Select(p => p.TextContent.Trim())
                .FirstOrDefault(t => t.Length > 0);

            // Website und E-Mail stehen als Verweis da, nicht als Absatz.
            value ??= right.QuerySelector("a")?.GetAttribute("href")?.Trim();

            if (!string.IsNullOrEmpty(value)) fields.TryAdd(label, Collapse(value));
        }

        return new ParsedFideEventDetail
        {
            EventId = eventId,
            EventType = Field(fields, "Type of event"),
            TimeControl = Field(fields, "Time control"),
            TimeControlText = Field(fields, "Time control description"),
            System = Field(fields, "Tournament system"),
            Rounds = Number(Field(fields, "Number of rounds")),
            Players = Number(Field(fields, "Number of players")),
            Country = Field(fields, "Country"),
            City = Field(fields, "City"),
            VenueAddress = Field(fields, "Address"),
            Website = Field(fields, "Website"),
        };
    }

    private static string? Field(Dictionary<string, string> fields, string label) =>
        fields.TryGetValue(label, out var value) ? value : null;

    private static int? Number(string? text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0
            ? n : null;

    /// <summary>
    /// Die Jahresansicht auseinandernehmen. Je Ereignis ein Link auf <c>calendar.php?id=</c> mit
    /// dem Namen und daneben ein <c>span.session-time</c> mit „01 May - 07 May / Malmo (SWE)".
    /// </summary>
    internal static async Task<List<ParsedFideEvent>> ParseYearAsync(string html, int year)
    {
        var results = new List<ParsedFideEvent>();
        var context = BrowsingContext.New(Configuration.Default);
        var document = await context.OpenAsync(req => req.Content(html));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var link in document.QuerySelectorAll("a[href*='calendar.php?id=']"))
        {
            var href = link.GetAttribute("href") ?? "";
            var idMatch = Regex.Match(href, @"id=(\d+)");
            if (!idMatch.Success) continue;

            var eventId = idMatch.Groups[1].Value;
            // Dasselbe Ereignis kann in der Jahresansicht mehrfach verlinkt sein (Bild + Titel).
            if (!seen.Add(eventId)) continue;

            var name = link.TextContent.Trim();
            if (name.Length == 0) continue;

            var when = FindSessionTime(link);
            if (when is null) continue;

            var parsed = ParseWhen(when, year);
            if (parsed is null) continue;

            results.Add(new ParsedFideEvent
            {
                EventId = eventId,
                Name = name,
                StartDate = parsed.Value.Start,
                EndDate = parsed.Value.End,
                City = parsed.Value.City,
                Country = parsed.Value.Country,
            });
        }
        return results;
    }

    /// <summary>
    /// Der Termin steht in einem Geschwister-Element des Titels, nicht in dessen Elternknoten —
    /// gesucht wird deshalb im umgebenden Block aufwaerts, bis einer ein
    /// <c>span.session-time</c> enthaelt.
    /// </summary>
    private static string? FindSessionTime(IElement link)
    {
        for (var node = link.ParentElement; node is not null; node = node.ParentElement)
        {
            var span = node.QuerySelector("span.session-time");
            if (span is not null) return Regex.Replace(span.TextContent, @"\s+", " ").Trim();
            // Nicht beliebig weit hoch: sonst findet ein Ereignis den Termin des naechsten.
            if (node.ClassList.Contains("session")) break;
        }
        return null;
    }

    internal static (DateOnly Start, DateOnly End, string? City, string? Country)? ParseWhen(
        string when, int year)
    {
        var match = WhenPattern.Match(when);
        if (!match.Success) return null;

        var m1 = Month(match.Groups["m1"].Value);
        var m2 = Month(match.Groups["m2"].Value);
        if (m1 is null || m2 is null) return null;

        var (startYear, endYear) = ResolveYears(m1.Value, m2.Value, year);

        if (!TryDate(startYear, m1.Value, match.Groups["d1"].Value, out var start)) return null;
        if (!TryDate(endYear, m2.Value, match.Groups["d2"].Value, out var end)) return null;

        var (city, country) = SplitPlace(match.Groups["place"].Value);
        return (start, end, city, country);
    }

    /// <summary>
    /// Welches Jahr gehoert zu Anfang und Ende?
    ///
    /// <para>Die Jahresansicht nennt nur Tag und Monat. Liegt der Endmonat VOR dem Startmonat,
    /// laeuft das Ereignis ueber den Jahreswechsel („27 Dec - 05 Jan", Rilton Cup) — und
    /// erscheint dann in ZWEI Jahresansichten mit derselben Zeichenkette. Aufgeloest wird es
    /// ueber den Startmonat: in der zweiten Jahreshaelfte beginnt es im abgefragten Jahr und
    /// endet im naechsten, sonst umgekehrt. Wer die Jahre AUFSTEIGEND abfragt und den ersten
    /// Treffer behaelt, bekommt damit die richtigen Daten. Von 139 Ereignissen des Jahres 2026
    /// war genau eines betroffen.</para>
    /// </summary>
    internal static (int Start, int End) ResolveYears(int startMonth, int endMonth, int year) =>
        endMonth >= startMonth ? (year, year)
            : startMonth >= 7 ? (year, year + 1)
            : (year - 1, year);

    private static bool TryDate(int year, int month, string day, out DateOnly date)
    {
        date = default;
        if (!int.TryParse(day, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d)) return false;
        if (d < 1 || d > DateTime.DaysInMonth(year, month)) return false;

        date = new DateOnly(year, month, d);
        return true;
    }

    private static int? Month(string abbreviation)
    {
        var index = Array.FindIndex(Months,
            m => m.Equals(abbreviation, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? null : index + 1;
    }

    private static readonly string[] Months =
        ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    /// <summary>
    /// „Malmo (SWE)" → Stadt + Land. Online-Ereignisse haben keine Stadt („ (ONL)"), und ein
    /// Stadtname darf Klammern enthalten — deshalb wird die LETZTE Klammer genommen.
    /// </summary>
    internal static (string? City, string? Country) SplitPlace(string? place)
    {
        var text = (place ?? "").Trim();
        if (text.Length == 0) return (null, null);

        var open = text.LastIndexOf('(');
        var close = text.LastIndexOf(')');
        if (open < 0 || close < open) return (text, null);

        var city = text[..open].Trim();
        var country = text[(open + 1)..close].Trim();
        return (city.Length == 0 ? null : city, country.Length == 0 ? null : country);
    }

    /// <summary>
    /// Derselbe Schutz wie im CrawlerService, nur fuer den anderen Host: https und ein exakter
    /// Hostvergleich. Redirects folgt dieser Client nicht automatisch (siehe SourceClientSetup) — ein
    /// 3xx kaeme also als Antwort zurueck und scheiterte an EnsureSuccessStatusCode, statt blind
    /// irgendwohin zu laufen.
    /// </summary>
    internal static void EnsureAllowedTarget(Uri url) => SourceHostGuard.Ensure(url, AllowedHost);
}
