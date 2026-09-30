using System.Globalization;
using System.Text.Json;

namespace ChessResultsCrawler.Services;

/// <summary>Ein Turnier aus dem Kalender des ungarischen Verbands (MSSZ, chess.hu).</summary>
public class ParsedChessHuEvent
{
    /// <summary>Die <c>id</c> der Quelle — zugleich die Nummer der Detailseite (<c>post.php?p=</c>).</summary>
    public string EventId { get; set; } = "";

    public string Name { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }

    /// <summary>Ort als Freitext („Siklós", „PÉCS", „Online", „Helyszín később").</summary>
    public string? Place { get; set; }

    /// <summary>
    /// Ob die Quelle einen SPIELORT nennt. „Online" und „Helyszín később" („Ort spaeter") stehen
    /// dort, wo sonst ein Ort steht — sie sind keine.
    /// </summary>
    public bool HasVenue { get; set; }

    /// <summary>Das Feld <c>fide</c> („igen"/„nem"): ob das Turnier FIDE-gewertet wird.</summary>
    public bool FideRated { get; set; }
}

/// <summary>
/// Der Turnierkalender des ungarischen Verbands (Magyar Sakkszövetség).
///
/// <para><b>Warum diese Quelle.</b> Vor allem VORLAUF: ab November 2026 fuehrt chess.hu 41
/// Turniere, wo chess-results 5 kennt. Im Rueckblick auf einen abgeschlossenen Zeitraum landen
/// 80 % irgendwann doch dort — 20 % nie. Ein Abruf bringt den ganzen Kalender.</para>
///
/// <para><b>Drei Fallen, die diese Quelle stellt.</b></para>
/// <list type="number">
/// <item><b>Ein GET antwortet 404.</b> Nur <c>POST</c> (mit leerem Rumpf) liefert die Daten — wer
/// den Endpunkt mit dem Browser prueft, haelt ihn fuer tot.</item>
/// <item><b>Das Datum kommt ohne Jahr.</b> <c>from_date</c> ist <c>MM.DD</c>, das Jahr steht
/// daneben in <c>from_year</c>. Wer nur das Datum liest, baut sich einen Kalender im laufenden
/// Jahr — und der Kalender reicht bis Juni 2027.</item>
/// <item><b>Die Antwort ist langsam.</b> Am 2026-09-08 gemessen: 75 Sekunden fuer 31 kB. Der
/// Zeitrahmen des Clients muss das aushalten, sonst sieht es wie ein Ausfall aus.</item>
/// </list>
///
/// <para><b>Was hier bewusst NICHT gelesen wird: das Feld <c>ifjusagi</c>.</b> Es heisst
/// „Jugend" und steht bei <b>77 von 107</b> Turnieren auf „igen" — darunter „Terézváros Open",
/// „Félegyházi Libafesztivál" (ein Gaensefest) und die „Weltbegegnung schachspielender Ungarn".
/// Es beantwortet also nicht „ist ein Jugendturnier", sondern etwas wie „Jugendliche duerfen
/// mitspielen". Als Jugendmerkmal uebernommen waeren drei Viertel des ungarischen Bestands
/// faelschlich Jugendturniere, und der Filter „nur Erwachsene" liesse fuer Ungarn fast nichts
/// uebrig. Die Einordnung kommt deshalb wie ueberall aus dem NAMEN („Ifjúsági", „Gyermek").</para>
///
/// <para><b>Und was es nicht zu holen gibt.</b> Die Detailseite (<c>post.php?p=&lt;id&gt;</c>,
/// leitet auf einen Titel-Alias um) traegt ausser Ort, Komitat und Terminen nur eine
/// Anmelde-Adresse und meist einen PDF-Verweis — keine Bedenkzeit, keine Rundenzahl, keine
/// Anschrift. Das KOMITAT waere der einzige Zugewinn, und der ist gemessen keiner: von 52
/// verschiedenen Ortsnamen des Kalenders stehen 46 im Lexikon, und nur drei davon mehrdeutig (und
/// zwar innerhalb derselben Stadt). Ein Abruf je Turnier fuer nichts — deshalb bleibt es bei dem
/// einen.</para>
///
/// <para>Rechtslage (2026-09-08 geprueft): <c>robots.txt</c> sperrt fuer <c>*</c> die Pfade
/// <c>/wp-admin/</c>, <c>/wp-includes/</c>, <c>/hu/</c> und <c>/en/</c>. Der Endpunkt liegt unter
/// <c>/app/</c> und ist damit nicht betroffen; die Detailseiten liegen im Wurzelverzeichnis.
/// Kein TDM-Vorbehalt, keine Nutzungsbedingungen.</para>
/// </summary>
public class ChessHuCalendarService
{
    internal const string AllowedHost = "chess.hu";

    private const string CalendarUrl = "https://chess.hu/app/versenynaptar.json";

    /// <summary>
    /// Die Quelle liefert hoechstens so viele Zeilen — ohne Fehlermeldung, ohne Hinweis. Wird der
    /// Deckel erreicht, ist der Kalender abgeschnitten und das gehoert ins Protokoll.
    /// </summary>
    internal const int RowCap = 150;

    private readonly HttpClient _http;
    private readonly ILogger<ChessHuCalendarService> _log;

    public ChessHuCalendarService(HttpClient http, ILogger<ChessHuCalendarService> log)
    {
        _http = http;
        _log = log;
    }

    /// <summary>Turniere, die am <paramref name="from"/> noch laufen oder spaeter beginnen.</summary>
    public async Task<List<ParsedChessHuEvent>> FetchAsync(DateOnly from, CancellationToken ct = default)
    {
        var target = new Uri(CalendarUrl);
        EnsureAllowedTarget(target);

        // Ein GET antwortet 404 — die Daten gibt es nur auf einen POST, und der braucht keinen
        // Rumpf.
        using var request = new HttpRequestMessage(HttpMethod.Post, target);
        using var response = await _http.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();
        SourceResponse.EnsureJson("chess.hu", response, json);

        var all = Parse(json);
        if (all.Count >= RowCap)
        {
            _log.LogWarning(
                "chess.hu-Kalender: {Count} Zeilen — der Deckel von {Cap} ist erreicht, die Liste ist vermutlich abgeschnitten",
                all.Count, RowCap);
        }

        var events = all.Where(e => e.EndDate >= from).OrderBy(e => e.StartDate).ToList();
        _log.LogInformation("chess.hu-Kalender ab {From}: {Count} von {All} Turnieren",
            from, events.Count, all.Count);
        return events;
    }

    internal static List<ParsedChessHuEvent> Parse(string json)
    {
        var results = new List<ParsedChessHuEvent>();
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return results;

        foreach (var row in document.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;

            var id = Text(row, "id");
            var name = Text(row, "name");
            var start = ParseDate(Text(row, "from_year"), Text(row, "from_date"));
            if (id is not { Length: > 0 } || name is not { Length: > 0 } || start is null) continue;

            var place = Text(row, "place");
            results.Add(new ParsedChessHuEvent
            {
                EventId = id,
                Name = name,
                StartDate = start.Value,
                // Ein Ende VOR dem Anfang ist ein Tippfehler der Quelle, kein Zeitraum.
                EndDate = ParseDate(Text(row, "to_year"), Text(row, "to_date")) is { } end
                          && end >= start.Value ? end : start.Value,
                Place = place,
                HasVenue = IsVenue(place),
                FideRated = string.Equals(Text(row, "fide"), "igen", StringComparison.OrdinalIgnoreCase),
            });
        }
        return results;
    }

    /// <summary>
    /// „Online" und „Helyszín később" („Ort spaeter") stehen im ORTS-Feld, sind aber keine Orte.
    /// Am 2026-09-08 sechs bzw. elf Eintraege. Ohne diese Pruefung sucht die Verortung nach einem
    /// Ort namens „Online" — findet heute nichts, koennte aber jederzeit auf einen gleichnamigen
    /// Eintrag im Lexikon treffen und einen Pin behaupten, den es nicht gibt.
    /// </summary>
    internal static bool IsVenue(string? place)
    {
        if (place is not { Length: > 0 }) return false;

        var folded = ChessSkCalendarService.Fold(place).Trim();
        return folded.Length > 0
               && !folded.StartsWith("online", StringComparison.Ordinal)
               && !folded.StartsWith("helyszin kesobb", StringComparison.Ordinal)
               && !folded.StartsWith("kesobb", StringComparison.Ordinal);
    }

    private static string? Text(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString()?.Trim(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;

    /// <summary>
    /// Jahr und <c>MM.DD</c> zu einem Datum zusammensetzen. Beides muss da sein — ein Datum ohne
    /// Jahr zu raten hiesse, den halben Kalender ins falsche Jahr zu legen.
    /// </summary>
    internal static DateOnly? ParseDate(string? year, string? monthDay)
    {
        if (year is not { Length: 4 } || monthDay is not { Length: > 0 }) return null;

        return DateOnly.TryParseExact($"{year}.{monthDay}", "yyyy.MM.dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    }

    internal static void EnsureAllowedTarget(Uri url) => SourceHostGuard.Ensure(url, AllowedHost);
}
