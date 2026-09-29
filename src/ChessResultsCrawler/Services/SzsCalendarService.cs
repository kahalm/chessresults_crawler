using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp;
using static ChessResultsCrawler.Services.SourceText;

namespace ChessResultsCrawler.Services;

/// <summary>Ein Turnier aus dem Kalender des slowenischen Verbands (ŠZS).</summary>
public class ParsedSzsEvent
{
    /// <summary>Die TID der Quelle — ihre Identitaet, und in der Trefferliste immer vorhanden.</summary>
    public string EventId { get; set; } = "";

    public string Name { get; set; } = "";
    public DateOnly Date { get; set; }

    /// <summary>
    /// Vierstellige Postleitzahl. Der eigentliche Wert dieser Quelle: sie steht schon in der
    /// TREFFERLISTE, nicht erst auf der Detailseite — die Verortung braucht also keinen zweiten
    /// Abruf. An der echten Seite gemessen tragen 29 von 30 Zeilen einer Seite eine gueltige.
    /// </summary>
    public string? PostalCode { get; set; }

    public string? Place { get; set; }

    /// <summary>
    /// Ob der Eintrag im NAMEN als abgesagt oder verlegt gekennzeichnet ist. Die Quelle hat kein
    /// Statusfeld dafuer — „ODPADE; Gurman 2026 - 36" ist der ganze Hinweis.
    /// </summary>
    public bool Cancelled { get; set; }
}

/// <summary>
/// Der Turnierkalender des slowenischen Verbands (Šahovska zveza Slovenije).
///
/// <para><b>Warum diese Quelle.</b> Das krasseste Verhaeltnis aller geprueften: 78 kuenftige
/// Turniere gegen <b>7</b> auf chess-results im selben Zeitraum. Und es ist nicht bloss Vorlauf —
/// der Rueckblick auf einen abgeschlossenen Monat zeigt, dass 36 von 88 Eintraegen (41 %) dort
/// NIE erscheinen; ganze woechentliche Serien fehlen strukturell. Kein einziger der 78 Eintraege
/// verlinkt nach chess-results.</para>
///
/// <para><b>Die Trefferliste traegt die Postleitzahl.</b> Das ist ungewoehnlich und wertvoll: der
/// genaueste Weg der Verortung braucht damit keinen Abruf je Turnier. Bedenkzeit, Rundenzahl und
/// Turniersystem stehen dagegen nur auf der Detailseite (ein Abruf je Turnier) — bewusst nicht
/// geholt, die Liste allein traegt Name, Termin, Ort und Postleitzahl.</para>
///
/// <para><b>Zwei Fallen, die diese Seite stellt.</b> (1) Sortiert wird nach Datum ABSTEIGEND,
/// kuenftige Turniere stehen also auf den ersten Seiten — wer von hinten liest, findet nur
/// Vergangenes. (2) Das Jahres-Auswahlfeld listet nur bis zum laufenden Jahr, aber
/// <c>leto=&lt;Folgejahr&gt;</c> funktioniert trotzdem; wer sich auf das Feld verlaesst, haelt das
/// Folgejahr fuer leer. Diese Route umgeht beides, indem sie ohne Jahresfilter liest und
/// seitenweise abbricht, sobald eine Seite vor dem Stichtag endet.</para>
///
/// <para>Rechtslage (2026-09-07 geprueft): keine robots.txt (404 seit mindestens 2025-07), keine
/// Nutzungsbedingungen, kein Copyright-Hinweis, kein TDM-Vorbehalt. Der Server hat allerdings
/// einen groben UA-Filter, der jede Zeichenfolge „bot" abweist — auch Googlebot und bingbot, es
/// ist also ein kopierter Schnipsel und keine ueberlegte Absage. Unser eigener User-Agent kommt
/// durch; eine Hoeflichkeitsmail an den Verband bleibt empfohlen.</para>
/// </summary>
public class SzsCalendarService
{
    internal const string AllowedHost = "www.sah-zveza.si";

    private const string SearchUrl = "https://www.sah-zveza.si/prireditve/iskalnik/";

    /// <summary>
    /// Deckel gegen eine Endlosschleife, wenn die Seite ihre Sortierung aendert. Bei 30 Zeilen je
    /// Seite deckt das rund ein Jahr Vorlauf ab — der Kalender fuehrt heute acht Monate.
    /// </summary>
    internal const int MaxPages = 12;

    private readonly HttpClient _http;
    private readonly ILogger<SzsCalendarService> _log;

    public SzsCalendarService(HttpClient http, ILogger<SzsCalendarService> log)
    {
        _http = http;
        _log = log;
    }

    /// <summary>
    /// Turniere ab <paramref name="from"/>. Gelesen wird seitenweise von vorn (dort steht das am
    /// weitesten in der Zukunft liegende) und abgebrochen, sobald eine Seite VOLLSTAENDIG vor dem
    /// Stichtag liegt.
    /// </summary>
    public async Task<List<ParsedSzsEvent>> FetchAsync(DateOnly from, CancellationToken ct = default)
    {
        var all = new List<ParsedSzsEvent>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var page = 1; page <= MaxPages; page++)
        {
            var target = new Uri($"{SearchUrl}?action=filter&search=" +
                                 "&klubska=on&drzavna=on&mednarodna=on&vecdnevna=on&ciklusi=on&festivali=on" +
                                 $"&kraj=0&leto=0&mesec=0&page={page}");
            EnsureAllowedTarget(target);

            using var response = await _http.GetAsync(target, ct);
            var html = await response.Content.ReadAsStringAsync(ct);
            response.EnsureSuccessStatusCode();

            var rows = await ParseAsync(html);
            if (rows.Count == 0) break;

            var kept = rows.Where(r => r.Date >= from && seen.Add(r.EventId)).ToList();
            all.AddRange(kept);

            // Absteigend sortiert: liegt die LETZTE Zeile dieser Seite vor dem Stichtag, kommt auf
            // den folgenden Seiten nur noch Aelteres.
            if (rows[^1].Date < from) break;
        }

        _log.LogInformation("SZS-Kalender ab {From}: {Count} Turniere", from, all.Count);
        return all;
    }

    /// <summary>
    /// Eine Trefferseite auseinandernehmen. Tabelle <c>#ITable</c>, Spalten
    /// <c>TID | Naziv | Datum | Pošta | Kraj | Povezava | Naložil | Stanje | Priloge</c>.
    /// </summary>
    internal static async Task<List<ParsedSzsEvent>> ParseAsync(string html)
    {
        var results = new List<ParsedSzsEvent>();
        var context = BrowsingContext.New(Configuration.Default);
        var document = await context.OpenAsync(req => req.Content(html));

        var table = document.QuerySelector("#ITable") ?? document.QuerySelector("table");
        if (table is null) return results;

        foreach (var row in table.QuerySelectorAll("tr"))
        {
            var cells = row.QuerySelectorAll("td");
            if (cells.Length < 5) continue;   // Kopfzeile hat nur th

            var id = Collapse(cells[0].TextContent);
            if (!IdPattern.IsMatch(id)) continue;

            var date = ParseSlovenianDate(cells[2].TextContent);
            if (date is null) continue;

            var name = Collapse(cells[1].TextContent);
            results.Add(new ParsedSzsEvent
            {
                EventId = id,
                Name = name,
                Date = date.Value,
                // Nur vierstellige Werte gelten: in der echten Liste stehen auch Bruchstuecke
                // („4" statt „1410", „34" statt Maribor) — eine einstellige Zahl waere keine
                // Postleitzahl, sondern ein Tippfehler des Einstellers.
                PostalCode = Collapse(cells[3].TextContent) is { Length: 4 } zip
                             && zip.All(char.IsAsciiDigit) ? zip : null,
                Place = Collapse(cells[4].TextContent) is { Length: > 0 } place ? place : null,
                Cancelled = CancelledPattern.IsMatch(name),
            });
        }
        return results;
    }

    private static readonly Regex IdPattern = new(@"^\d{1,9}$", RegexOptions.Compiled);

    /// <summary>
    /// Absagen und Verlegungen stehen im NAMEN, nicht in einem Statusfeld: „ODPADE; Gurman
    /// 2026 - 36", „ODPOVEDANO Litija šahira", „Prestavljeno iz 7.9.; …".
    /// </summary>
    private static readonly Regex CancelledPattern =
        new(@"\b(ODPADE|ODPOVEDAN\w*|PRESTAVLJEN\w*)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// „23. februar 2027" — der Monat steht als slowenisches WORT da, nicht als Zahl.
    /// </summary>
    internal static DateOnly? ParseSlovenianDate(string? text)
    {
        var m = DatePattern.Match(Collapse(text));
        if (!m.Success) return null;

        var month = Array.IndexOf(Months, m.Groups[2].Value.ToLowerInvariant()) + 1;
        if (month == 0) return null;

        return int.TryParse(m.Groups[1].Value, out var day)
               && int.TryParse(m.Groups[3].Value, out var year)
               && day >= 1 && day <= DateTime.DaysInMonth(year, month)
            ? new DateOnly(year, month, day)
            : null;
    }

    private static readonly Regex DatePattern =
        new(@"(\d{1,2})\.\s*([A-Za-zČčŠšŽž]+)\s+(\d{4})", RegexOptions.Compiled);

    private static readonly string[] Months =
    [
        "januar", "februar", "marec", "april", "maj", "junij",
        "julij", "avgust", "september", "oktober", "november", "december",
    ];

    internal static void EnsureAllowedTarget(Uri url) => SourceHostGuard.Ensure(url, AllowedHost);
}
