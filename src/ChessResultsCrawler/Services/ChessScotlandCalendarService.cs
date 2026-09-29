using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using static ChessResultsCrawler.Services.SourceText;

namespace ChessResultsCrawler.Services;

/// <summary>Ein Turnier aus dem Kalender von Chess Scotland.</summary>
public class ParsedChessScotlandEvent
{
    /// <summary>
    /// Der URL-Bestandteil hinter <c>/calendar/</c> — eine lesbare, aber teils lange Kennung
    /// (bis 54 Zeichen gemessen). Stabil, solange der Veranstalter den Titel nicht aendert.
    /// </summary>
    public string Slug { get; set; } = "";

    public string Name { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public string Url { get; set; } = "";

    /// <summary>Publikums-Schlagworte der Zeile: „Adult", „Junior", „International", „Online".</summary>
    public List<string> Categories { get; set; } = [];

    /// <summary>
    /// Die Bedenkzeit-KLASSE — der Sonderwert dieser Quelle, siehe Klassendokumentation. Werte:
    /// „Standard", „Allegro" (FIDE-Sprache fuer Schnellschach/Rapid), „Blitz", dazu „Fide" als
    /// reiner Wertungs-Vermerk (keine eigene Klasse). Ein Kongress kann mehrere zugleich tragen.
    /// </summary>
    public List<string> TimeControls { get; set; } = [];
}

/// <summary>Was die Detailseite EINES Turniers ueber die Liste hinaus hergibt — siehe unten: wenig, und mit Vorsicht zu geniessen.</summary>
public class ParsedChessScotlandDetail
{
    /// <summary>
    /// Die Zeile der Ausschreibung, in der eine britische Postleitzahl steht — der einzige Weg,
    /// ueberhaupt einen Spielort zu gewinnen. <c>null</c>, wenn keine Zeile eine traegt (der
    /// haeufige Fall: 35 von 43 gemessen).
    /// </summary>
    public string? Venue { get; set; }
}

/// <summary>
/// Der Turnierkalender von Chess Scotland (chessscotland.com).
///
/// <para><b>Warum diese Quelle.</b> Am 2026-09-09 gemessen: 43-44 kuenftige Turniere bis 2028
/// (44 bei der ersten Messung, 43 einen Tag spaeter — ein Termin war zwischenzeitlich vergangen),
/// gegen 8 auf chess-results fuer SCO im selben Zeitraum. Rund 36 zusaetzliche Termine, ueberwiegend
/// kleinere Vereins- und Jugendturniere (Allegro-/Blitz-Abende, Schulligen).</para>
///
/// <para><b>Eine einzige Seite, nicht geblaettert.</b> <c>/calendar/upcoming</c> zeigt ALLE
/// kuenftigen Termine auf einmal, serverseitig gerendertes HTML ohne JavaScript. Die Filter
/// <c>/calendar/upcoming/junior</c> und <c>/calendar/upcoming/international</c> sind nur
/// client-seitige Teilmengen derselben Liste (Vergleich der Optionswerte im Monatsfilter bestaetigt
/// das) und werden hier nicht gesondert abgefragt.</para>
///
/// <para><b>Die Bedenkzeit-KLASSE steht strukturiert dabei — der Sonderwert dieser Quelle.</b> Bei
/// jeder anderen Quelle des Projekts muss sie aus Freitext erschlossen werden
/// (<c>TournamentSpeedClassifier</c> auf RookHub-Seite); hier traegt die Zeile sie als Schlagwort
/// mit: „Standard", „Blitz", „Allegro" (FIDE-Sprache fuer Schnellschach) und „Fide" als reiner
/// Wertungs-Vermerk. Ein Kongress kann mehrere gleichzeitig anbieten (Hauptturnier + Blitz-Abend),
/// die Zeile nennt dann alle.</para>
///
/// <para><b>Was die Liste NICHT liefert: einen Spielort.</b> Es gibt kein Ortsfeld. Die
/// Detailseite (ein Abruf je Turnier) traegt die Ausschreibung als freien Rich-Text-Block
/// (Quill-Delta-JSON in einem versteckten Eingabefeld <c>input.content</c>) — Struktur nur, wenn
/// der Veranstalter selbst eine Anschrift hineinschreibt. Gemessen an allen 43 damals kuenftigen
/// Detailseiten: <b>8 (19 %)</b> enthalten eine erkennbare britische Postleitzahl irgendwo im
/// Text, 35 nicht („More details to follow" ist der haeufigste Grund). <see cref="VenueOf"/> holt
/// NUR die eine Zeile mit der Postleitzahl — mehr laesst sich aus Fliesstext nicht verlaesslich
/// herausloesen, und ein Beispiel enthielt ein per Copy-Paste eingefuegtes Bild als
/// Base64-Daten-URI MITTEN im selben JSON: ein naiver Volltext-Regex-Treffer darauf fand zwei
/// falsche „Postleitzahlen" aus zufaelligen Base64-Zeichen. <see cref="PlainTextOf"/> liest
/// deshalb NUR die <c>string</c>-„insert"-Werte des Quill-Deltas, keine eingebetteten Bilder.</para>
///
/// <para><b>Ohne Postleitzahl-Bestand fuer Grossbritannien im Ortslexikon (siehe RookHub) traegt
/// die gefundene Zeile trotzdem meist einen ECHTEN Ortsnamen</b> („Dalblair Road, Ayr. KA7 1UG",
/// „Kelvin West Church, Glasgow, G12 8LE") — die normale Ortsaufloesung ueber den Staedtenamen
/// greift also, auch ohne dass die Postleitzahl selbst etwas beitraegt. Nur eine gemessene
/// Anschrift (Dundee) verteilt sich auf mehrere kurze Zeilen OHNE Komma, dort traegt die
/// Postleitzahl-Zeile allein keinen Ortsnamen — ein bekannter, hingenommener Verlust.</para>
///
/// <para><b>Rechtslage (2026-09-09 geprueft).</b> <c>/robots.txt</c> antwortet 404 (RFC 9309: keine
/// Einschraenkung fuer irgendeinen Client), keine Nutzungsbedingungen gefunden. Keine genannte
/// Wartezeit; es wird trotzdem mit Zurueckhaltung gewartet (<see cref="DetailDelay"/>), wie bei den
/// uebrigen kleinen Verbandsseiten dieses Projekts.</para>
/// </summary>
public class ChessScotlandCalendarService
{
    internal const string AllowedHost = "www.chessscotland.com";

    private const string BaseUrl = "https://www.chessscotland.com/calendar/";
    private const string ListUrl = BaseUrl + "upcoming";

    /// <summary>Wartezeit vor einem Detailabruf. Die Quelle nennt keine — Selbstbeschraenkung.</summary>
    internal static readonly TimeSpan DetailDelay = TimeSpan.FromMilliseconds(300);

    private readonly HttpClient _http;
    private readonly ILogger<ChessScotlandCalendarService> _log;

    public ChessScotlandCalendarService(HttpClient http, ILogger<ChessScotlandCalendarService> log)
    {
        _http = http;
        _log = log;
    }

    /// <summary>Die vollstaendige Liste, gefiltert auf Turniere ab <paramref name="from"/>.</summary>
    public async Task<List<ParsedChessScotlandEvent>> FetchAsync(DateOnly from, CancellationToken ct = default)
    {
        var html = await GetAsync(ListUrl, ct);
        if (html is null) return [];

        var events = await ParseListAsync(html);
        var result = events.Where(e => e.EndDate >= from).OrderBy(e => e.StartDate).ToList();

        _log.LogInformation("Chess-Scotland-Kalender ab {From}: {Count} Turniere", from, result.Count);
        return result;
    }

    /// <summary>
    /// Die Detailseite EINES Turniers — heute nur fuer <see cref="ParsedChessScotlandDetail.Venue"/>
    /// gebraucht. <c>null</c>, wenn die Seite nicht lesbar ist; ein Ausfall kostet den Spielort,
    /// nicht den Termin (den hat die Liste schon).
    /// </summary>
    public async Task<ParsedChessScotlandDetail?> FetchDetailAsync(string slug, CancellationToken ct = default)
    {
        if (!SlugPattern.IsMatch(slug)) return null;

        var html = await GetAsync(BaseUrl + slug, ct);
        return html is null ? null : ParseDetail(html);
    }

    private async Task<string?> GetAsync(string url, CancellationToken ct)
    {
        var target = new Uri(url);
        EnsureAllowedTarget(target);

        using var response = await _http.GetAsync(target, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode) return body;

        _log.LogWarning("Chess Scotland: {Url} antwortete {Status}", url, (int)response.StatusCode);
        return null;
    }

    // ----- Die Liste -----------------------------------------------------------

    /// <summary>
    /// Eine Zeile ist <c>div.published</c>: ein Verweis (Titel + Slug), ein direktes
    /// <c>&lt;p&gt;</c> mit dem Termin, dann ein <c>&lt;div&gt;</c> mit bis zu zwei weiteren
    /// <c>&lt;p&gt;</c> („Categories:"/„Time Controls:") — beide koennen fehlen (leere Zeilen wie
    /// „Chess Scotland SGM").
    /// </summary>
    internal static async Task<List<ParsedChessScotlandEvent>> ParseListAsync(string html)
    {
        var results = new List<ParsedChessScotlandEvent>();
        var context = BrowsingContext.New(Configuration.Default);
        var document = await context.OpenAsync(req => req.Content(html));

        foreach (var row in document.QuerySelectorAll("div.published"))
        {
            var link = row.QuerySelector("a[href^='/calendar/']");
            var href = link?.GetAttribute("href");
            if (link is null || href is not { Length: > 0 }) continue;

            var slug = SlugOf(href);
            if (slug is not { Length: > 0 } || !SlugPattern.IsMatch(slug)) continue;

            var name = Collapse(link.TextContent);
            if (name.Length == 0) continue;

            var dateParagraph = row.Children.OfType<IElement>()
                .FirstOrDefault(e => e.TagName.Equals("P", StringComparison.OrdinalIgnoreCase));
            if (ParseDateRange(dateParagraph?.TextContent) is not var (start, end)) continue;

            var meta = row.Children.OfType<IElement>()
                .FirstOrDefault(e => e.TagName.Equals("DIV", StringComparison.OrdinalIgnoreCase));

            results.Add(new ParsedChessScotlandEvent
            {
                Slug = slug,
                Name = name,
                StartDate = start,
                EndDate = end,
                Url = BaseUrl + slug,
                Categories = LabelledListOf(meta, "Categories:"),
                TimeControls = LabelledListOf(meta, "Time Controls:"),
            });
        }
        return results;
    }

    private static string? SlugOf(string href)
    {
        const string prefix = "/calendar/";
        if (!href.StartsWith(prefix, StringComparison.Ordinal)) return null;
        return href[prefix.Length..].Trim('/');
    }

    /// <summary>
    /// Die kommaseparierte Liste hinter einem Label („Categories:", „Time Controls:") — oder leer,
    /// wenn kein <c>&lt;p&gt;</c> mit diesem Label vorkommt.
    /// </summary>
    private static List<string> LabelledListOf(IElement? meta, string label)
    {
        if (meta is null) return [];

        foreach (var p in meta.QuerySelectorAll("p"))
        {
            var text = Collapse(p.TextContent);
            if (!text.StartsWith(label, StringComparison.OrdinalIgnoreCase)) continue;

            return text[label.Length..]
                .Split(',')
                .Select(part => part.Trim())
                .Where(part => part.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        return [];
    }

    /// <summary>
    /// „Fri 11th September 2026 - Sun 13th September 2026" bzw. „Tue 8th September 2026" — anders
    /// als beim ICU-Kalender steht hier IMMER Wochentag, Tag, Monat UND Jahr auf beiden Seiten,
    /// nichts wird aus dem Kontext ergaenzt.
    /// </summary>
    internal static (DateOnly Start, DateOnly End)? ParseDateRange(string? text)
    {
        var match = DateRangePattern.Match(Collapse(text));
        if (!match.Success) return null;

        if (Date(match, 1) is not { } start) return null;

        if (!match.Groups[4].Success) return (start, start);

        if (Date(match, 4) is not { } end || end < start) return null;
        return (start, end);
    }

    private static DateOnly? Date(Match match, int firstGroup)
    {
        var day = int.Parse(match.Groups[firstGroup].Value, CultureInfo.InvariantCulture);
        var month = MonthOf(match.Groups[firstGroup + 1].Value);
        var year = int.Parse(match.Groups[firstGroup + 2].Value, CultureInfo.InvariantCulture);
        return month is { } m && year is >= 1900 and <= 2200 && day >= 1
               && day <= DateTime.DaysInMonth(year, m)
            ? new DateOnly(year, m, day) : null;
    }

    private static int? MonthOf(string name)
    {
        var value = Collapse(name).ToLowerInvariant();
        if (value.Length < 3) return null;

        var index = Array.IndexOf(Months, value[..3]);
        return index < 0 ? null : index + 1;
    }

    private static readonly string[] Months =
        ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    // ----- Die Detailseite -------------------------------------------------------

    /// <summary>
    /// Liest den Spielort aus der Ausschreibung — bewusst die einzige Angabe, die von dort geholt
    /// wird. Rundenzahl, System und Bedenkzeit stehen dort ebenfalls, aber als voellig uneinheitlicher
    /// Werbetext („A 5-round Swiss", „6 Rounds Swiss Pairings", „Format: 5-round Swiss") — kein Fall
    /// fuer eine Spalte.
    /// </summary>
    internal static ParsedChessScotlandDetail? ParseDetail(string html)
    {
        var match = ContentInputPattern.Match(html);
        if (!match.Success) return null;

        var quillJson = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);

        string plainText;
        try
        {
            plainText = PlainTextOf(quillJson);
        }
        catch (JsonException)
        {
            return null;
        }

        return new ParsedChessScotlandDetail { Venue = VenueOf(plainText) };
    }

    /// <summary>
    /// Nur die TEXT-„insert"-Werte eines Quill-Deltas, in Reihenfolge aneinandergehaengt.
    /// Eingebettete Bilder/Formeln haben ein OBJEKT als „insert" (<c>{"image": "data:..."}</c>)
    /// statt einer Zeichenkette und werden uebersprungen — sonst faende ein Postleitzahl-Regex
    /// zufaellige Treffer in einem eingefuegten Base64-Bild (gemessen: „glasgow-congress-2027"
    /// hatte genau das, zwei falsche Treffer aus reinen Zufallszeichen).
    /// </summary>
    internal static string PlainTextOf(string quillJson)
    {
        using var document = JsonDocument.Parse(quillJson);
        if (!document.RootElement.TryGetProperty("ops", out var ops) || ops.ValueKind != JsonValueKind.Array)
            return "";

        var text = new StringBuilder();
        foreach (var op in ops.EnumerateArray())
        {
            if (op.ValueKind != JsonValueKind.Object) continue;
            if (op.TryGetProperty("insert", out var insert) && insert.ValueKind == JsonValueKind.String)
                text.Append(insert.GetString());
        }
        return text.ToString();
    }

    /// <summary>
    /// Die EINE Zeile mit einer britischen Postleitzahl — mehr laesst sich aus Fliesstext nicht
    /// verlaesslich herausloesen. Ein Label davor („Venue:", „Location:") ist Fuellwort und wird
    /// abgeschnitten, ebenso ein Klammerzusatz danach („(see map on the Venue tab above)").
    ///
    /// <para>Der haeufigste Fall (Murrayfield, Ayr, die Polytechnic-Reihe, Top-Scot-Quali-Turniere)
    /// ist EINE kommaseparierte Zeile mit dem Ortsnamen VOR der Postleitzahl — genau die Form, die
    /// <c>GeocodingService</c> ueber den Staedtenamen aufloest. Eine gemessene Ausnahme (Dundee)
    /// verteilt die Anschrift auf mehrere kurze Zeilen ohne Komma; dort traegt die
    /// Postleitzahl-Zeile allein keinen Ortsnamen mehr — ein hingenommener Verlust, kein Fehler.</para>
    /// </summary>
    internal static string? VenueOf(string plainText)
    {
        foreach (var rawLine in plainText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || !PostcodePattern.IsMatch(line)) continue;

            line = LabelPattern.Replace(line, "");
            line = ParentheticalPattern.Replace(line, "").Trim();
            line = line.TrimEnd('.', ',', ' ');
            return line.Length == 0 ? null : line;
        }
        return null;
    }

    /// <summary>
    /// Diese Quelle hat keine Nummer — der Slug ist eine ECHTE, wenn auch teils lange Kennung.
    /// <see cref="Models.TournamentDirectoryEntry.PublicId"/> (RookHub-Seite) fasst nur 24 Zeichen,
    /// darum dort ein Hash-Kurzwert; der lesbare Slug bleibt vollstaendig im Herkunftsvermerk.
    /// </summary>
    internal static string PublicIdOf(string slug)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(slug.Trim().ToLowerInvariant()));
        return "sc" + Convert.ToHexStringLower(digest)[..12];
    }

    // ----- Hilfen ------------------------------------------------------------

    /// <summary>Nur Kleinbuchstaben/Ziffern/Bindestrich — deckt jeden gemessenen Slug, schuetzt vor Pfad-Ausbruch.</summary>
    private static readonly Regex SlugPattern = new(@"^[a-z0-9][a-z0-9-]{0,150}$", RegexOptions.Compiled);

    private static readonly Regex DateRangePattern = new(
        @"^[A-Za-z]{3}\s+(\d{1,2})(?:st|nd|rd|th)\s+([A-Za-z]+)\s+(\d{4})"
        + @"(?:\s*-\s*[A-Za-z]{3}\s+(\d{1,2})(?:st|nd|rd|th)\s+([A-Za-z]+)\s+(\d{4}))?$",
        RegexOptions.Compiled);

    private static readonly Regex ContentInputPattern = new(
        """<input\s+type="hidden"\s+class="content"\s+value="(.*?)">""",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// Eine britische Postleitzahl („KA7 1UG", „G12 8LE", „DD1 4HN"). Bewusst ohne Verankerung an
    /// Wortgrenzen ueber Satzzeichen hinweg (ein Punkt direkt danach — „Ayr. KA7 1UG" — ist im
    /// Fliesstext haeufig).
    /// </summary>
    private static readonly Regex PostcodePattern = new(
        @"\b[A-Za-z]{1,2}\d[A-Za-z0-9]?\s?\d[A-Za-z]{2}\b", RegexOptions.Compiled);

    private static readonly Regex LabelPattern = new(
        @"^(?:venue|location|address)\s*:\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ParentheticalPattern = new(
        @"\(.*?\)\s*$", RegexOptions.Compiled);

    internal static void EnsureAllowedTarget(Uri url) => SourceHostGuard.Ensure(url, AllowedHost);
}
