using System.Globalization;
using System.Text.RegularExpressions;
using System.Web;

namespace ChessResultsCrawler.Services;

/// <summary>Ein Turnier aus dem Kalender des polnischen Verbands (chessarbiter.com).</summary>
public class ParsedChessArbiterEvent
{
    /// <summary>Das Jahr im Adresspfad (<c>/turnieje/2026/ti_291/</c>) — Teil der Kennung.</summary>
    public string Year { get; set; } = "";

    /// <summary>Die laufende Nummer im Adresspfad — zusammen mit dem Jahr die Kennung.</summary>
    public string EventId { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>
    /// Startdatum. Die Liste nennt nur Tag und Monat; das Jahr kommt aus der REIHENFOLGE
    /// (siehe <see cref="ChessArbiterCalendarService.ParseList"/>).
    /// </summary>
    public DateOnly StartDate { get; set; }

    /// <summary>Ort als Freitext („Sosnowica").</summary>
    public string? Place { get; set; }

    /// <summary>Die Woiwodschaft ausgeschrieben („Śląskie") — aus der Auswahlliste derselben Seite.</summary>
    public string? Region { get; set; }

    /// <summary>Die Bedenkzeit-Klasse der Quelle: klasyczne, klasyczne FIDE, szybkie, blitz, inne.</summary>
    public string? SpeedText { get; set; }

    public string Url { get; set; } = "";
}

/// <summary>Was die Detailseite EINES polnischen Turniers zusaetzlich hergibt.</summary>
/// <summary>
/// Wie eine Detail-Abfrage ausgegangen ist. Der Unterschied zwischen den letzten beiden ist der
/// ganze Zweck dieses Typs: <b>„Seite geholt, sie traegt keine Angaben"</b> ist eine endgueltige
/// Auskunft — der Aufrufer darf sie sich merken und nie wieder fragen. <b>„Nicht zu holen"</b> ist
/// keine Auskunft und muss wiederholt werden. Beides als <c>null</c> zurueckzugeben hiess: der
/// Aufrufer fragt die rund 480 polnischen Turniere OHNE server-gerenderte Seite jede Nacht erneut
/// und verbraucht damit sein ganzes Abruf-Budget an ihnen (gemessen 2026-09-09).
/// </summary>
public enum ChessArbiterDetailOutcome
{
    /// <summary>Seite geholt und Angaben gelesen.</summary>
    Parsed,

    /// <summary>Seite geholt, aber ohne eine einzige Angabe — bei dieser Quelle der HAEUFIGE Fall:
    /// nur ein Teil der Turniere hat eine server-gerenderte Seite, die uebrigen liefern eine
    /// JavaScript-Huelle (Stichprobe 2026-09-09: 3 von 14 mit Angaben).</summary>
    Empty,

    /// <summary>Nicht zu holen (Netzfehler, Umleitung, 4xx/5xx der Quelle).</summary>
    Unavailable,
}

/// <summary>Ergebnis einer Detail-Abfrage: Ausgang plus die Angaben, wenn es welche gab.</summary>
public readonly record struct ChessArbiterDetailResult(
    ChessArbiterDetailOutcome Outcome, ParsedChessArbiterDetail? Detail)
{
    public static readonly ChessArbiterDetailResult Empty = new(ChessArbiterDetailOutcome.Empty, null);
    public static readonly ChessArbiterDetailResult Unavailable = new(ChessArbiterDetailOutcome.Unavailable, null);
}

public class ParsedChessArbiterDetail
{
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }

    /// <summary>Ort MIT Spielstaette („Sosnowica, Ośrodek Wypoczynkowo-Szkoleniowy …").</summary>
    public string? Place { get; set; }

    /// <summary>Bedenkzeit im Rohtext („90' + 30'' na ruch").</summary>
    public string? TimeControl { get; set; }

    public int? Rounds { get; set; }

    /// <summary>„swiss" oder „roundRobin"; <c>null</c>, wenn die Seite etwas anderes nennt.</summary>
    public string? System { get; set; }

    /// <summary>
    /// Die gemeldete Teilnehmerzahl. Bemerkenswert: sie steht auch bei GEPLANTEN Turnieren da —
    /// chess-results und FIDE nennen sie fuer die Zukunft grundsaetzlich nicht.
    /// </summary>
    public int? PlayerCount { get; set; }

    public string? Organizer { get; set; }
}

/// <summary>
/// Der Turnierkalender des polnischen Verbands (Polski Związek Szachowy, betrieben unter
/// chessarbiter.com).
///
/// <para><b>Die ergiebigste Einzelquelle des ganzen Projekts.</b> Am 2026-09-08 gemessen:
/// <b>611 kuenftige Turniere in EINER Antwort</b> (320 kB, keine Blaetterung) — mehr als alle
/// bisher angebundenen Verbandskalender zusammen. Der Turnierbereich traegt „© Polski Związek
/// Szachowy"; es ist der offizielle Kalender, nicht eine Sammlung Dritter.</para>
///
/// <para><b>Zwei Dinge muss man ueber diese Quelle wissen.</b></para>
/// <list type="number">
/// <item><b>Die Liste nennt kein Jahr.</b> Dort steht „09-09", Tag und Monat. Das Jahr im
/// Adresspfad (<c>turn=2026/ti_291</c>) ist das Jahr, in dem der EINTRAG angelegt wurde — bei
/// neun der 611 ist das 2024 oder 2025, obwohl sie 2026 gespielt werden. Verlaesslich ist dagegen
/// die REIHENFOLGE: die Liste ist streng nach Termin sortiert (nachgemessen: 611 Zeilen ohne eine
/// einzige Ausnahme), also laeuft ein Jahreszaehler mit und springt beim Monatswechsel
/// rueckwaerts weiter.</item>
/// <item><b>Die Detailseite ist server-gerendert und englisch beschriftet.</b> Die Recherche
/// hatte hier eine JavaScript-Datei (<c>capro_tournament.js</c>) erwartet — die gibt es nicht
/// (404). Stattdessen liefert <c>/turnieje/{jahr}/ti_{id}/</c> — <b>mit Schraegstrich am Ende</b>,
/// sonst kommt eine 301 auf genau diese Adresse — 8,5 kB fertiges HTML, in dem jede
/// Angabe als <c>Tr("Start date:","")</c> beschriftet ist und ihr Wert in der naechsten Zelle
/// steht. Daraus kommen Enddatum, Bedenkzeit, Rundenzahl, System — und die TEILNEHMERZAHL, die
/// chess-results und FIDE fuer kuenftige Turniere grundsaetzlich nicht nennen.
/// <b>Aber nur ein Teil der Turniere hat so eine Seite</b>: in einer Stichprobe von 14 waren es
/// 3 — die uebrigen liefern 4,1 kB JavaScript-Huelle ohne eine einzige Angabe (das Turnier-Paket
/// der Veranstalter entscheidet das, nicht der Verband). Fuer die gibt es hier nichts zu holen,
/// und <c>ParseDetail</c> gibt dort <c>null</c> zurueck.</item>
/// </list>
///
/// <para>Rechtslage (2026-09-08 geprueft): keine <c>robots.txt</c> (404), keine
/// Nutzungsbedingungen, kein TDM-Vorbehalt. Die Infrastruktur ist alt (PHP 5.2) — deshalb wird
/// die Detailseite nur fuer NEUE Turniere geholt, gedeckelt und mit Pause. Eine kurze Anfrage an
/// <c>biuro@pzszach.pl</c> bleibt vor dem Dauerbetrieb das saubere Vorgehen.</para>
/// </summary>
public class ChessArbiterCalendarService
{
    internal const string AllowedHost = "www.chessarbiter.com";

    private const string ListUrl =
        "https://www.chessarbiter.com/turnieje.php?status=planowane&wojewodztwo=wszystkie" +
        "&typ=wszystkie&rodzaj=wszystkie&szukaj=Wyswietl+turnieje";

    private readonly HttpClient _http;
    private readonly ILogger<ChessArbiterCalendarService> _log;

    public ChessArbiterCalendarService(HttpClient http, ILogger<ChessArbiterCalendarService> log)
    {
        _http = http;
        _log = log;
    }

    public async Task<List<ParsedChessArbiterEvent>> FetchListAsync(
        DateOnly today, CancellationToken ct = default)
    {
        var target = new Uri(ListUrl);
        EnsureAllowedTarget(target);

        using var response = await _http.GetAsync(target, ct);
        var html = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();

        var events = ParseList(html, today);
        _log.LogInformation("chessarbiter-Kalender: {Count} geplante Turniere", events.Count);
        return events;
    }

    /// <summary>
    /// Die Detailseite EINES Turniers. Unterscheidet „geholt, keine Angaben" von „nicht zu holen"
    /// (siehe <see cref="ChessArbiterDetailOutcome"/>) — beides als <c>null</c> zu melden liess den
    /// Aufrufer die Turniere ohne Datenseite endlos erneut fragen.
    /// </summary>
    public async Task<ChessArbiterDetailResult> FetchDetailAsync(
        string year, string id, CancellationToken ct = default)
    {
        if (!YearPattern.IsMatch(year) || !IdPattern.IsMatch(id))
            throw new InvalidOperationException($"Refusing malformed tournament key: {year}/{id}");

        // Der SCHRAEGSTRICH am Ende ist Pflicht: ohne ihn antwortet chessarbiter mit 301 auf
        // genau dieselbe Adresse MIT Schraegstrich, und der Crawl-Handler folgt Umleitungen
        // bewusst nicht (`AllowAutoRedirect = false`) — die Antwort ist dann kein 2xx, und der
        // Aufruf gab `null` zurueck. Am 2026-09-09 auf Dev nachgemessen: alle 150 Detailabrufe
        // eines Durchgangs endeten so, und weil ein Turnier ohne gelesene Detailseite Kandidat
        // bleibt, holte jeder weitere Durchgang dieselben Seiten erneut. Mit Schraegstrich: 200.
        var target = new Uri($"https://{AllowedHost}/turnieje/{year}/ti_{id}/");
        EnsureAllowedTarget(target);

        using var response = await _http.GetAsync(target, ct);
        var html = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) return ChessArbiterDetailResult.Unavailable;

        var detail = ParseDetail(html);
        return detail is null
            ? ChessArbiterDetailResult.Empty
            : new ChessArbiterDetailResult(ChessArbiterDetailOutcome.Parsed, detail);
    }

    private static readonly Regex YearPattern = new(@"^\d{4}$", RegexOptions.Compiled);
    private static readonly Regex IdPattern = new(@"^\d{1,9}$", RegexOptions.Compiled);

    // ----- Die Liste ---------------------------------------------------------

    /// <summary>
    /// Die Trefferliste auseinandernehmen.
    ///
    /// <para><b>Das Jahr entsteht hier</b>: die Liste nennt nur Tag und Monat, ist aber streng
    /// nach Termin sortiert. Der Zaehler beginnt im laufenden Jahr (bzw. im naechsten, wenn der
    /// erste Termin schon deutlich vorbei waere) und springt bei jedem Monatsrueckschritt um eins
    /// weiter. Das Jahr aus dem Adresspfad taugt dafuer NICHT — es ist das Anlagejahr des
    /// Eintrags.</para>
    /// </summary>
    internal static List<ParsedChessArbiterEvent> ParseList(string html, DateOnly today)
    {
        var regions = ParseRegions(html);
        var results = new List<ParsedChessArbiterEvent>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var year = today.Year;
        var previous = (Month: 0, Day: 0);

        foreach (Match row in RowPattern.Matches(html))
        {
            var body = row.Groups[1].Value;

            var key = KeyPattern.Match(body);
            var link = LinkPattern.Match(body);
            var date = DatePattern.Match(body);
            if (!key.Success || !link.Success || !date.Success) continue;

            var name = Clean(link.Groups[1].Value);
            if (name.Length == 0) continue;

            var day = int.Parse(date.Groups[1].Value);
            var month = int.Parse(date.Groups[2].Value);
            if (month is < 1 or > 12 || day < 1) continue;

            // Erster Eintrag: liegt sein Termin schon deutlich hinter uns, meint die Liste das
            // naechste Jahr. Danach zaehlt nur noch der Monatsrueckschritt.
            if (previous.Month == 0)
            {
                if (new DateOnly(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)))
                    < today.AddDays(-60)) year++;
            }
            else if ((month, day).CompareTo(previous) < 0 && month < previous.Month)
            {
                year++;
            }
            previous = (month, day);

            if (day > DateTime.DaysInMonth(year, month)) continue;

            var id = key.Groups[2].Value;
            var pathYear = key.Groups[1].Value;
            if (!seen.Add($"{pathYear}/{id}")) continue;

            var szary = SzaryPattern.Matches(body).Select(m => Clean(m.Groups[1].Value)).ToList();
            var region = RegionPattern.Match(body);

            results.Add(new ParsedChessArbiterEvent
            {
                Year = pathYear,
                EventId = id,
                Name = name,
                StartDate = new DateOnly(year, month, day),
                // Der erste graue Block ist der Status, der zweite Ort samt Aktualisierungsdatum,
                // der letzte die Bedenkzeit-Klasse.
                Place = szary.Count >= 2 ? StripUpdateNote(szary[1]) : null,
                Region = region.Success && regions.TryGetValue(region.Groups[1].Value, out var name_)
                    ? name_ : region.Success ? region.Groups[1].Value : null,
                SpeedText = szary.Count >= 3 ? szary[^1] : null,
                Url = $"https://{AllowedHost}/turnieje/{pathYear}/ti_{id}",
            });
        }
        return results;
    }

    private static readonly Regex RowPattern =
        new(@"<tr class=""tbl[12]"">(.*?)</tr>", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex KeyPattern =
        new(@"turn=(\d{4})/ti_(\d+)", RegexOptions.Compiled);

    private static readonly Regex LinkPattern =
        new(@"<a [^>]*>(.*?)</a>", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex DatePattern = new(@">(\d{2})-(\d{2})<", RegexOptions.Compiled);

    private static readonly Regex SzaryPattern =
        new(@"<div class=""szary"">(.*?)</div>", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>„Poland,SL" — das Kuerzel der Woiwodschaft hinter dem Land.</summary>
    private static readonly Regex RegionPattern =
        new(@"Poland,\s*([A-Z]{2})", RegexOptions.Compiled);

    /// <summary>
    /// Die Woiwodschaften ausgeschrieben. Sie stehen in der Auswahlliste DERSELBEN Antwort
    /// („Śląskie (SL)") — es braucht also weder eine zweite Anfrage noch eine Tabelle im Quelltext,
    /// die veralten koennte.
    /// </summary>
    internal static Dictionary<string, string> ParseRegions(string html)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var select = SelectPattern.Match(html);
        if (!select.Success) return result;

        foreach (Match option in OptionPattern.Matches(select.Value))
        {
            var code = option.Groups[1].Value;
            var label = Clean(option.Groups[2].Value);
            var name = RegionSuffix.Replace(label, "").Trim();
            if (code.Length == 2 && name.Length > 0) result[code] = name;
        }
        return result;
    }

    private static readonly Regex SelectPattern =
        new(@"name=""wojewodztwo"".*?</select>", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex OptionPattern =
        new(@"<option value=""([A-Z]{2})""[^>]*>(.*?)</option>",
            RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex RegionSuffix = new(@"\s*\([A-Z]{2}\)\s*$", RegexOptions.Compiled);

    /// <summary>„Sosnowica [aktualizacja:04-09-2026]" — der Vermerk gehoert nicht zum Ort.</summary>
    internal static string? StripUpdateNote(string? text)
    {
        var value = Clean(UpdatePattern.Replace(text ?? "", "")).Trim(' ', ',', '-');
        return value.Length > 0 ? value : null;
    }

    private static readonly Regex UpdatePattern =
        new(@"\[\s*aktualizacja\s*:[^\]]*\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ----- Die Detailseite ---------------------------------------------------

    /// <summary>
    /// Die Detailseite auseinandernehmen. Jede Angabe traegt eine ENGLISCHE Beschriftung
    /// (<c>Tr("Start date:","")</c>) und steht in der Zelle dahinter — die Seite ist also
    /// unabhaengig von der eingestellten Oberflaechensprache lesbar.
    /// </summary>
    internal static ParsedChessArbiterDetail? ParseDetail(string html)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match pair in FieldPattern.Matches(html))
        {
            var label = pair.Groups[1].Value.TrimEnd(':', ' ');
            if (!fields.ContainsKey(label)) fields[label] = Clean(pair.Groups[2].Value);
        }
        if (fields.Count == 0) return null;

        return new ParsedChessArbiterDetail
        {
            StartDate = Date(fields, "Start date"),
            EndDate = Date(fields, "End date"),
            Place = Value(fields, "Place"),
            TimeControl = Value(fields, "Rate of play"),
            Rounds = Count(fields, "No. of rounds", 1, 30),
            // NICHT ueber Value(): der Wert IST ein Uebersetzungsaufruf, und genau den liest SystemOf.
            System = fields.TryGetValue("System", out var system) ? SystemOf(system) : null,
            PlayerCount = Count(fields, "No. of players", 1, 100_000),
            Organizer = Value(fields, "Organizer"),
        };
    }

    private static readonly Regex FieldPattern = new(
        @"Tr\(""([^""]+)"",""[^""]*""\);</script></td><td[^>]*>(.*?)</td>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// Der Wert des Feldes „System" ist selbst wieder ein Uebersetzungsaufruf
    /// (<c>Tr("Swiss","")</c>) — gelesen wird der Text darin.
    /// </summary>
    internal static string? SystemOf(string? text)
    {
        if (text is not { Length: > 0 }) return null;
        var inner = InnerTrPattern.Match(text);
        var value = (inner.Success ? inner.Groups[1].Value : text).Trim();

        if (value.Contains("swiss", StringComparison.OrdinalIgnoreCase)) return "swiss";
        if (value.Contains("robin", StringComparison.OrdinalIgnoreCase)
            || value.Contains("circular", StringComparison.OrdinalIgnoreCase)) return "roundRobin";
        return null;
    }

    private static readonly Regex InnerTrPattern =
        new(@"Tr\(""([^""]*)""", RegexOptions.Compiled);

    private static string? Value(Dictionary<string, string> fields, string label) =>
        fields.TryGetValue(label, out var value) && value.Length > 0 && !value.StartsWith("Tr(")
            ? value : null;

    private static DateOnly? Date(Dictionary<string, string> fields, string label) =>
        fields.TryGetValue(label, out var value)
        && DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date) ? date : null;

    private static int? Count(Dictionary<string, string> fields, string label, int min, int max) =>
        fields.TryGetValue(label, out var value) && int.TryParse(value.Trim(), out var number)
        && number >= min && number <= max ? number : null;

    // ----- Hilfen ------------------------------------------------------------

    private static string Clean(string? html) =>
        Regex.Replace(HttpUtility.HtmlDecode(Regex.Replace(html ?? "", "<[^>]+>", " ")) ?? "",
            @"\s+", " ").Trim();

    internal static void EnsureAllowedTarget(Uri url) => SourceHostGuard.Ensure(url, AllowedHost);
}
