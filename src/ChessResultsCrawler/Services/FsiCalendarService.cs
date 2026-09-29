using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using static ChessResultsCrawler.Services.SourceText;

namespace ChessResultsCrawler.Services;

/// <summary>Ein Turnier aus dem Kalender des italienischen Verbands (FSI).</summary>
public class ParsedFsiEvent
{
    /// <summary>
    /// Die fortlaufende FSI-Nummer aus dem Feld „Immissione" („04-06-2026 (21750)").
    ///
    /// <para>Sie ist die Identitaet des Eintrags — an der echten Seite gemessen tragen sie
    /// <b>283 von 283</b> Turnieren, und alle 283 sind verschieden. Ohne sie muesste man ueber
    /// Name und Termin zuordnen, und ein umbenanntes Turnier waere jede Nacht ein neues.</para>
    /// </summary>
    public string EventId { get; set; } = "";

    public string Name { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }

    /// <summary>Region in FSI-Schreibweise („LAZIO", „ALTO ADIGE", „TRENTINO").</summary>
    public string? Region { get; set; }

    /// <summary>Provinz — loest italienische Namensgleichheit auf („Marino" gibt es mehrfach).</summary>
    public string? Province { get; set; }

    /// <summary>Spielort als Freitext. Ohne Postleitzahl (0 von 283 tragen eine).</summary>
    public string? Place { get; set; }

    /// <summary>Turnierart der FSI („Torneo Elo Italia/FIDE", „FIDE Rapid", „FIDE Blitz", …).</summary>
    public string? EventType { get; set; }

    /// <summary>Bedenkzeit im Klartext („90 minuti + 30 secondi di incremento a mossa").</summary>
    public string? TimeControl { get; set; }

    public int? Rounds { get; set; }

    /// <summary>Freitext mit Spielstaette, Ratinggrenzen, Startgeld — 282 von 283 gefuellt.</summary>
    public string? Note { get; set; }
}

/// <summary>
/// Der Turnierkalender des italienischen Verbands (Federazione Scacchistica Italiana).
///
/// <para><b>Warum diese Quelle.</b> Italien faehrt sein Turnierwesen NICHT auf chess-results,
/// sondern auf Vega/vesus: von 285 Eintraegen des FSI-Kalenders verlinkt <b>kein einziger</b>
/// dorthin. Eine Namensstichprobe von 15 fand 3 auf chess-results — rund vier Fuenftel der
/// italienischen Turniere fehlen dort also, und das ist keine Momentaufnahme, sondern die
/// Oekosystem-Entscheidung eines ganzen Verbands.</para>
///
/// <para><b>Die Falle, die diese Seite stellt:</b> ohne <c>ric=1</c> zeigt sie nur das
/// Suchformular und KEINE Ergebnisse. Wer die nackte URL abruft, misst null und haelt den
/// Kalender fuer leer.</para>
///
/// <para><b>Ein Abruf genuegt.</b> Alle Felder stehen inline in der Trefferliste — Name, Termin,
/// Region, Provinz, Ort, Bedenkzeit, Rundenzahl. Kein Abruf je Turnier, keine Paginierung. Der
/// Preis ist die Groesse: 283 Eintraege sind rund 1,25 MB HTML.</para>
///
/// <para>Rechtslage (2026-09-07 geprueft): keine robots.txt auf allen Varianten (404), keine
/// Nutzungsbedingungen, keine UA-Diskriminierung (vier User-Agents geprueft, alle 200).</para>
/// </summary>
public class FsiCalendarService
{
    internal const string AllowedHost = "www.federscacchi.com";

    private const string CalendarUrl =
        "https://www.federscacchi.com/fsi/index.php/calendario/calendario";

    private readonly HttpClient _http;
    private readonly ILogger<FsiCalendarService> _log;

    public FsiCalendarService(HttpClient http, ILogger<FsiCalendarService> log)
    {
        _http = http;
        _log = log;
    }

    public async Task<List<ParsedFsiEvent>> FetchAsync(
        DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        // `ric=1` ist Pflicht — sonst kommt nur das Suchformular zurueck. `ord=1` sortiert nach
        // Startdatum, `senso=Asc` aufsteigend.
        var target = new Uri($"{CalendarUrl}?dtiniric={from:yyyy-MM-dd}&dtfinric={to:yyyy-MM-dd}" +
                             "&ord=1&senso=Asc&ric=1");
        EnsureAllowedTarget(target);

        using var response = await _http.GetAsync(target, ct);
        var html = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();

        var events = await ParseAsync(html);
        _log.LogInformation("FSI-Kalender {From}..{To}: {Count} Turniere", from, to, events.Count);
        return events;
    }

    /// <summary>
    /// Die Trefferliste auseinandernehmen.
    ///
    /// <para><b>Warum ueber die Zeichenkette getrennt wird und nicht ueber den Baum.</b> Die
    /// Seite ist keine Tabelle: ein Turnier ist eine FOLGE von Bootstrap-Bloecken ohne
    /// gemeinsamen Behaelter, und die Bloecke aller 283 Turniere liegen als Geschwister
    /// nebeneinander. Es gibt also kein Element, das „ein Turnier" ist — ein Baumlauf muesste
    /// raten, wo einer endet. Die <c>h1</c>-Ueberschrift ist dagegen ein verlaesslicher Anfang:
    /// getrennt wird am Markup, und jeder Abschnitt danach fuer sich geparst.</para>
    ///
    /// <para>Innerhalb eines Abschnitts stehen die Angaben als BESCHRIFTUNG in einem
    /// <c>&lt;b&gt;</c> („Provincia:", „Turni:") mit dem Wert dahinter im SELBEN Elternknoten —
    /// gelesen wird deshalb der Elterntext ohne die Beschriftung, nicht das naechste
    /// Geschwisterelement.</para>
    /// </summary>
    internal static async Task<List<ParsedFsiEvent>> ParseAsync(string html)
    {
        var results = new List<ParsedFsiEvent>();
        var context = BrowsingContext.New(Configuration.Default);

        foreach (var chunk in SplitAtHeadings(html))
        {
            var document = await context.OpenAsync(req => req.Content(chunk));

            var name = Collapse(document.QuerySelector("h1")?.TextContent);
            if (name.Length == 0) continue;

            var (start, end) = ParseDates(document.QuerySelector("h2")?.TextContent);
            if (start is null) continue;

            var subHeadings = document.QuerySelectorAll("h4")
                .Select(h => Collapse(h.TextContent))
                .Where(t => t.Length > 0)
                .ToList();

            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var label in document.QuerySelectorAll("b, strong"))
            {
                var labelText = Collapse(label.TextContent);
                var key = labelText.TrimEnd(':');
                if (key.Length == 0 || key.Length > 40 || !labelText.EndsWith(':')) continue;

                var whole = Collapse(label.ParentElement?.TextContent);
                var value = whole.StartsWith(labelText, StringComparison.Ordinal)
                    ? whole[labelText.Length..].Trim()
                    : whole;
                if (value.Length > 0) fields.TryAdd(key, value);
            }

            // Ohne die FSI-Nummer gibt es keine Identitaet — ein solcher Eintrag wird
            // uebersprungen, statt beim naechsten Durchgang als neues Turnier zu erscheinen.
            var immissione = Field(fields, "Immissione");
            var m = ImmissioneIdPattern.Match(immissione ?? "");
            if (!m.Success) continue;

            results.Add(new ParsedFsiEvent
            {
                EventId = m.Groups[1].Value,
                Name = name,
                StartDate = start.Value,
                EndDate = end ?? start.Value,
                // Die erste Unterueberschrift ist die Region, die zweite die Turnierart.
                Region = subHeadings.ElementAtOrDefault(0),
                EventType = subHeadings.ElementAtOrDefault(1),
                Province = Field(fields, "Provincia"),
                Place = Field(fields, "Luogo"),
                TimeControl = Field(fields, "Tempo riflessione"),
                Rounds = ParseRounds(Field(fields, "Turni")),
                Note = Field(fields, "Note"),
            });
        }
        return results;
    }

    /// <summary>
    /// Zerlegt die Seite an den <c>h1</c>-Ueberschriften. Was VOR der ersten steht (Kopf,
    /// Navigation, Suchformular) faellt weg — dort steht kein Turnier.
    /// </summary>
    internal static IEnumerable<string> SplitAtHeadings(string html)
    {
        var starts = HeadingPattern.Matches(html).Select(m => m.Index).ToList();
        for (var i = 0; i < starts.Count; i++)
        {
            var from = starts[i];
            var to = i + 1 < starts.Count ? starts[i + 1] : html.Length;
            yield return html[from..to];
        }
    }

    private static readonly Regex HeadingPattern =
        new(@"<h1(?:\s[^>]*)?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string? Field(Dictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var value) && value.Length > 0 ? value : null;

    private static readonly Regex ImmissioneIdPattern = new(@"\((\d+)\)", RegexOptions.Compiled);

    /// <summary>
    /// „11-09-2026 - 13-09-2026" oder „15-11-2026" (eintaegig). Das Trennzeichen ist von
    /// <c>&amp;nbsp;</c> umgeben, deshalb wird nach dem Zusammenziehen gesucht und nicht
    /// gesplittet.
    /// </summary>
    internal static (DateOnly? Start, DateOnly? End) ParseDates(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null);

        var dates = DatePattern.Matches(text)
            .Select(m => DateOnly.TryParseExact(m.Value, "dd-MM-yyyy",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : (DateOnly?)null)
            .Where(d => d is not null)
            .Select(d => d!.Value)
            .ToList();

        return dates.Count switch
        {
            0 => (null, null),
            1 => (dates[0], dates[0]),
            _ => (dates[0], dates[^1]),
        };
    }

    private static readonly Regex DatePattern = new(@"\d{2}-\d{2}-\d{4}", RegexOptions.Compiled);

    /// <summary>
    /// „9 (8)" heisst neun Runden im Haupt- und acht im Nebenturnier — gezaehlt wird die ERSTE
    /// Zahl. „variabile" und Ähnliches ergibt keine.
    /// </summary>
    internal static int? ParseRounds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Regex.Match(text, @"\d+");
        return m.Success && int.TryParse(m.Value, out var n) && n is > 0 and < 100 ? n : null;
    }

    /// <summary>Derselbe Schutz wie bei den anderen Hosts: https und ein exakter Hostvergleich.</summary>
    internal static void EnsureAllowedTarget(Uri url) => SourceHostGuard.Ensure(url, AllowedHost);
}
