using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using static ChessResultsCrawler.Services.SourceText;

namespace ChessResultsCrawler.Services;

/// <summary>Ein Turnier aus dem Kalender des slowakischen Verbands (SSZ, chess.sk).</summary>
public class ParsedChessSkEvent
{
    /// <summary>Die <c>tournamentId</c> der Quelle — ihre Identitaet, in jeder Zeile vorhanden.</summary>
    public string EventId { get; set; } = "";

    public string Name { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }

    /// <summary>Ort aus der Liste („Bratislava - mestska cast Raca").</summary>
    public string? City { get; set; }

    /// <summary>ISO-3-Staat der Quelle; fast immer SVK, vereinzelt CZE.</summary>
    public string? Country { get; set; }

    /// <summary>
    /// Die chess-results-Nummer, aus dem Feld <c>s_m</c> („Swiss manager URL") gezogen.
    ///
    /// <para>Der wertvollste Teil dieser Quelle: 27 von 79 Eintraegen nennen sie, und damit ist
    /// die Zuordnung zum bestehenden Verzeichnis ein EXAKTER Schluessel statt eines
    /// Namensvergleichs.</para>
    /// </summary>
    public string? ChessResultsId { get; set; }

    public string? Website { get; set; }

    /// <summary>Adresse der Seite selbst — Beleg fuer den Herkunftsvermerk.</summary>
    public string? Url { get; set; }

    // ----- nur von der Detailseite -----

    /// <summary>
    /// Anschrift des Spielorts („Slovnaft Business Center, Vlcie hrdlo 1/A, 824 12 Bratislava").
    /// Traegt oft eine Postleitzahl und ist damit der genaueste Weg der Verortung.
    /// </summary>
    public string? Address { get; set; }

    /// <summary>Das Freitextfeld „System" im Rohzustand — Turniersystem, Rundenzahl UND Bedenkzeit in einem.</summary>
    public string? SystemText { get; set; }

    /// <summary>Aus <see cref="SystemText"/> herausgeloest: nur der Bedenkzeit-Teil.</summary>
    public string? TimeControl { get; set; }

    /// <summary>Aus <see cref="SystemText"/> herausgeloest: „swiss", „roundRobin" oder <c>null</c>.</summary>
    public string? System { get; set; }

    /// <summary>Aus <see cref="SystemText"/> herausgeloest.</summary>
    public int? Rounds { get; set; }

    /// <summary>Das Feld „Typ turnaja": Standard / Rapid / Blitz / Online — eine ANGABE, keine Rechnung.</summary>
    public string? Type { get; set; }

    /// <summary>
    /// Der Eintrag ist nach seinem NAMEN gar kein Turnier, sondern eine Schulung, ein Seminar
    /// oder ein Trainingslager. Der Kalender fuehrt beides in derselben Liste und hat kein Feld,
    /// das sie trennt — am 2026-09-08 waren es 4 von 79 („Skolenie rozhodcov 2. a 3. triedy",
    /// „Trenersky seminar"). Sie gehoeren nicht in einen Turnierkalender.
    /// </summary>
    public bool NonTournament { get; set; }
}

/// <summary>
/// Der Turnierkalender des slowakischen Verbands (Slovensky sachovy zvaz) — die einzige der
/// geprueften Quellen mit einer AUSDRUECKLICH angebotenen REST-Schnittstelle.
///
/// <para><b>Warum diese Quelle.</b> 79 kuenftige Turniere, von denen 53 % keinen
/// chess-results-Verweis tragen. Und der Rest ist kein Verlust, sondern ein Gewinn: 27 Eintraege
/// nennen die chess-results-Nummer im Feld <c>s_m</c>, die Zuordnung zum bestehenden Bestand ist
/// dort also EXAKT statt geraten.</para>
///
/// <para><b>Die Liste ist eine echte API, die Details sind es nicht.</b>
/// <c>GET /api/turnaje.php/v1/tournaments</c> liefert JSON (im Fussteil der Seite als „Free api
/// specification" verlinkt, Spezifikation unter <c>/api/swagger.yaml</c>, dort ausdruecklich
/// „If you need more, just ask for it"). Sie fuehrt aber nur Name, Termin, Ort, Staat und ein
/// paar Verweise — <b>Bedenkzeit, Rundenzahl, Turniersystem und die Anschrift stehen nur auf der
/// Detailseite</b>, und die ist HTML. Ein Abruf je Turnier, deshalb mit Pause und Deckel.</para>
///
/// <para><b>Die Detailseite ist sprachneutral lesbar.</b> Ihre Felder tragen englische
/// CSS-Klassen (<c>datagridphp_detail_td_field_system</c>, <c>_miesto</c>, <c>_trn_type</c>) —
/// gelesen wird ueber die Klasse, nicht ueber die slowakische Beschriftung daneben. Eine
/// Uebersetzung der Oberflaeche wuerde diesen Parser also nicht brechen.</para>
///
/// <para>Rechtslage (2026-09-08 geprueft): <c>robots.txt</c> beginnt mit
/// <c>User-agent: * / Allow: /</c> und ist danach die bekannte „ultimate bad bot blocker"-Liste
/// (615 Namen, keiner davon unserer). Die API wird im Fussteil aktiv beworben. Keine
/// Nutzungsbedingungen, kein TDM-Vorbehalt.</para>
/// </summary>
public class ChessSkCalendarService
{
    internal const string AllowedHost = "www.chess.sk";

    private const string ListUrl = "https://www.chess.sk/api/turnaje.php/v1/tournaments";
    private const string DetailUrl = "https://www.chess.sk/index.php?str=kalendar&detail=19";

    /// <summary>
    /// Hoechstzahl der Detailabrufe eines Durchgangs. Der Kalender fuehrt heute 79 kuenftige
    /// Turniere; der Deckel ist die Bremse fuer den Tag, an dem jemand tausend eintraegt.
    /// </summary>
    internal const int MaxDetails = 200;

    /// <summary>
    /// Pause zwischen zwei Detailabrufen. Der Verband betreibt seinen Server selbst, und 79
    /// Anfragen am Stueck sind unhoeflich, auch wenn sie erlaubt sind.
    /// </summary>
    internal static readonly TimeSpan DetailDelay = TimeSpan.FromMilliseconds(700);

    private readonly HttpClient _http;
    private readonly ILogger<ChessSkCalendarService> _log;

    public ChessSkCalendarService(HttpClient http, ILogger<ChessSkCalendarService> log)
    {
        _http = http;
        _log = log;
    }

    /// <summary>
    /// Turniere ab <paramref name="from"/>. Mit <paramref name="details"/> wird je Turnier die
    /// Detailseite nachgeholt (Anschrift, Bedenkzeit, Rundenzahl, System, Typ); ohne sie bleibt
    /// es bei dem, was die Schnittstelle selbst hergibt.
    ///
    /// <para>Ein FEHLGESCHLAGENER Detailabruf verwirft das Turnier NICHT: Name, Termin und Ort
    /// stehen schon in der Liste, und ein halber Eintrag ist besser als keiner.</para>
    /// </summary>
    public async Task<List<ParsedChessSkEvent>> FetchAsync(
        DateOnly from, bool details = true, CancellationToken ct = default)
    {
        var target = new Uri(ListUrl);
        EnsureAllowedTarget(target);

        using var response = await _http.GetAsync(target, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();
        SourceResponse.EnsureJson("chess.sk", response, json);

        var events = ParseList(json)
            .Where(e => e.EndDate >= from)
            .OrderBy(e => e.StartDate)
            .ToList();

        if (details)
        {
            var fetched = 0;
            foreach (var e in events.Take(MaxDetails))
            {
                ct.ThrowIfCancellationRequested();
                if (fetched > 0) await Task.Delay(DetailDelay, ct);
                fetched++;

                try
                {
                    ApplyDetail(e, await FetchDetailAsync(e.EventId, ct));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "chess.sk: Detailseite {Id} nicht lesbar", e.EventId);
                }
            }
        }

        _log.LogInformation("chess.sk-Kalender ab {From}: {Count} Turniere ({Details} mit Detail)",
            from, events.Count, details ? Math.Min(events.Count, MaxDetails) : 0);
        return events;
    }

    private async Task<Dictionary<string, string>> FetchDetailAsync(string id, CancellationToken ct)
    {
        var target = new Uri($"{DetailUrl}&id_p={Uri.EscapeDataString(id)}&action_p=show");
        EnsureAllowedTarget(target);

        using var response = await _http.GetAsync(target, ct);
        var html = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();
        return await ParseDetailAsync(html);
    }

    // ----- Liste -------------------------------------------------------------

    /// <summary>
    /// Die JSON-Antwort der Schnittstelle. Zahlen kommen dort als ZEICHENKETTE
    /// (<c>"tournamentId":"5956"</c>), die Spezifikation nennt sie <c>integer</c> — gelesen wird
    /// deshalb beides.
    /// </summary>
    internal static List<ParsedChessSkEvent> ParseList(string json)
    {
        var results = new List<ParsedChessSkEvent>();
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return results;

        foreach (var row in document.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;

            var id = Text(row, "tournamentId");
            var name = Text(row, "nazov");
            var start = ParseDate(Text(row, "termin_od"));
            if (id is not { Length: > 0 } || name is not { Length: > 0 } || start is null) continue;

            results.Add(new ParsedChessSkEvent
            {
                EventId = id,
                Name = name,
                StartDate = start.Value,
                EndDate = ParseDate(Text(row, "termin_do")) ?? start.Value,
                City = Empty(Text(row, "mesto")),
                Country = Empty(Text(row, "stat")),
                ChessResultsId = ChessResultsIdOf(Text(row, "s_m")),
                Website = Empty(Text(row, "webstranka")),
                Url = Empty(Text(row, "url")),
                NonTournament = NonTournamentPattern.IsMatch(Fold(name)),
            });
        }
        return results;
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

    /// <summary>„https://s3.chess-results.com/tnr1482875.aspx?lan=4" → „1482875".</summary>
    internal static string? ChessResultsIdOf(string? url)
    {
        if (url is not { Length: > 0 }) return null;
        var m = ChessResultsPattern.Match(url);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static readonly Regex ChessResultsPattern =
        new(@"chess-results\.com/tnr(\d{1,12})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Was in dieser Liste steht, aber kein Turnier ist: Schulung (<c>skolenie</c>), Seminar,
    /// Trainingslager (<c>sustredenie</c>), Lehrgang (<c>kurz</c>), Sitzung. Gesucht wird im
    /// gefalteten Namen, weil die Quelle „Skolenie" und „Skolenie" (mit und ohne Haetschek)
    /// nebeneinander fuehrt — beide Schreibweisen stehen wirklich dort.
    /// </summary>
    private static readonly Regex NonTournamentPattern = new(
        @"\b(skolenie|seminar\w*|sustredenie|kurz\b|konferenci\w*|schodz\w*|valne zhromazdenie)",
        RegexOptions.Compiled);

    // ----- Detailseite -------------------------------------------------------

    /// <summary>
    /// Die Detailseite auseinandernehmen. Jedes Feld steht als
    /// <c>&lt;td class="datagridphp_detail_td_field_&lt;schluessel&gt; …"&gt;</c> — gelesen wird
    /// der SCHLUESSEL aus der Klasse, nicht die slowakische Beschriftung in der Zelle daneben.
    /// </summary>
    internal static async Task<Dictionary<string, string>> ParseDetailAsync(string html)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var context = BrowsingContext.New(Configuration.Default);
        var document = await context.OpenAsync(req => req.Content(html));

        foreach (var cell in document.QuerySelectorAll("td[class*='datagridphp_detail_td_field_']"))
        {
            var key = cell.ClassList
                .Select(c => c.StartsWith(FieldPrefix, StringComparison.Ordinal)
                    ? c[FieldPrefix.Length..] : null)
                .FirstOrDefault(k => k is { Length: > 0 });
            if (key is null || fields.ContainsKey(key)) continue;

            fields[key] = Collapse(cell.TextContent);
        }
        return fields;
    }

    private const string FieldPrefix = "datagridphp_detail_td_field_";

    /// <summary>Die Detailangaben auf den Eintrag uebertragen.</summary>
    internal static void ApplyDetail(ParsedChessSkEvent e, IReadOnlyDictionary<string, string> fields)
    {
        if (fields.TryGetValue("miesto", out var address)) e.Address = Empty(address);
        if (fields.TryGetValue("trn_type", out var type)) e.Type = Empty(type);
        if (fields.TryGetValue("mesto", out var city) && Empty(city) is { } c) e.City = c;

        if (fields.TryGetValue("system", out var system) && Empty(system) is { } s)
        {
            e.SystemText = s;
            e.TimeControl = TimeControlOf(s);
            e.System = SystemOf(s);
            e.Rounds = RoundsOf(s);
        }
    }

    /// <summary>
    /// Das Turniersystem aus dem Freitext.
    ///
    /// <para>Gesucht wird ueber einen NACHSICHTIGEN Ausdruck (<c>sv\w*ciar</c>), weil das Wort in
    /// der Quelle in mindestens vier Schreibweisen vorkommt: „Svajciarsky system", „svajciar",
    /// „Svajciarsky na 9 kol" und der Tippfehler „Sviaciarsky". Eine feste Zeichenkette findet
    /// nur einen Teil davon.</para>
    ///
    /// <para>Nennt ein Text BEIDE Systeme, gilt keines: „A turnaj kruhovy, B turnaj svajciarsky"
    /// beschreibt zwei Turniere in einem Eintrag, und sich fuer eines zu entscheiden waere
    /// geraten.</para>
    /// </summary>
    internal static string? SystemOf(string? text)
    {
        var t = Fold(text);
        if (t.Length == 0) return null;

        var swiss = SwissPattern.IsMatch(t);
        var roundRobin = RoundRobinPattern.IsMatch(t);
        if (swiss == roundRobin) return null;
        return swiss ? "swiss" : "roundRobin";
    }

    private static readonly Regex SwissPattern = new(@"sv\w*ciar|swiss", RegexOptions.Compiled);

    /// <summary>„kruhovy" = Rundenturnier, „uzavrety turnaj" = geschlossenes Turnier.</summary>
    private static readonly Regex RoundRobinPattern =
        new(@"kruhov|uzavret|round.?robin|berger", RegexOptions.Compiled);

    /// <summary>
    /// Die Rundenzahl aus dem Freitext: „na 7 kol", „9. kol", „7kol", „5 kol".
    ///
    /// <para>Genommen wird die ERSTE Nennung. In „svajciarsky 8 kol sachu (… a medzitym 7 kol
    /// bicyklovania)" — einem Schach-Rad-Biathlon — sind das die acht Schachrunden.</para>
    /// </summary>
    internal static int? RoundsOf(string? text)
    {
        var folded = Fold(text);
        var m = RoundsPattern.Match(folded);
        // Ein Teil der Eintraege ist auf ENGLISCH geschrieben — „90 min +30 sec/move; Number of
        // rounds: 7; Tournament type: Swiss". Dort steht die Zahl HINTER dem Wort.
        if (!m.Success) m = EnglishRoundsPattern.Match(folded);
        if (!m.Success) return null;

        return int.TryParse(m.Groups[1].Value, out var rounds) && rounds is >= 1 and <= 30
            ? rounds : null;
    }

    /// <summary>
    /// „na 7 kol", „9. kol", „7kol". Die Ziffer muss unmittelbar davorstehen: „8 dvojkol" sind
    /// acht DOPPELrunden, und die als acht Runden zu lesen waere falsch — dort bleibt es lieber
    /// unbekannt.
    /// </summary>
    private static readonly Regex RoundsPattern = new(@"(\d{1,2})\s*\.?\s*kol\w*", RegexOptions.Compiled);

    private static readonly Regex EnglishRoundsPattern =
        new(@"rounds?\s*[:=]?\s*(\d{1,2})", RegexOptions.Compiled);

    /// <summary>
    /// Den Bedenkzeit-Teil aus dem Freitext herausloesen.
    ///
    /// <para><b>Warum ueberhaupt herausgeloest wird.</b> Das Feld „System" traegt drei Angaben in
    /// einem Satz („Svajciarsky system na 7 kol ; 60min/partia+30s/ťah"). Als Bedenkzeit
    /// gespeichert waere der ganze Satz eine Zumutung fuer jeden, der nur wissen will, wie lange
    /// gespielt wird — und die Rundenzahl steht ohnehin schon in ihrer eigenen Spalte.</para>
    ///
    /// <para>Zerlegt wird an <c>; ( ) ,</c> und am SATZENDE („. " gefolgt von einem Grossbuchstaben
    /// — „min." und „9. kol" duerfen dabei nicht zerfallen). Behalten wird jeder Abschnitt, der
    /// nach Zeit aussieht: eine Zahl vor einer Minutenangabe, oder die Kurzform „90+30".</para>
    /// </summary>
    internal static string? TimeControlOf(string? text)
    {
        if (text is not { Length: > 0 }) return null;

        var parts = SegmentPattern.Split(text)
            .Select(p => Collapse(p).Trim(' ', '.', ',', ';', '-'))
            .Where(p => p.Length > 0 && TimePattern.IsMatch(Fold(p)))
            .ToList();
        if (parts.Count == 0) return null;

        return Tidy(string.Join(", ", parts));
    }

    /// <summary>
    /// Was vorne am Bedenkzeit-Text uebrig bleibt, wenn er nicht in einem eigenen Abschnitt
    /// stand: „svajciar 6 kol 7 min/partiu + 3 sek/ťah" hat kein Trennzeichen, also traegt der
    /// eine Abschnitt System und Rundenzahl mit. Beide stehen schon in ihrer eigenen Spalte.
    /// </summary>
    private static string? Tidy(string text)
    {
        var value = Collapse(LeadingSystemPattern.Replace(text, "", 1));

        // Wiederholt, weil sich die Fuellwoerter stapeln: „Tempo hry : 90 min." hat drei davon
        // hintereinander.
        bool changed;
        do
        {
            changed = false;
            value = value.TrimStart(' ', ':', '-', ',', '.');
            foreach (var noise in LeadingNoise)
            {
                if (!value.StartsWith(noise, StringComparison.OrdinalIgnoreCase)) continue;
                if (value.Length > noise.Length && char.IsLetterOrDigit(value[noise.Length])) continue;

                value = value[noise.Length..];
                changed = true;
            }
        } while (changed);

        value = Collapse(value);
        return value.Length > 0 ? value : null;
    }

    private static readonly Regex SegmentPattern =
        new(@"[;()]|,|\.\s+(?=[A-ZÁÄČĎÉÍĽĹŇÓÔŔŠŤÚÝŽ])", RegexOptions.Compiled);

    /// <summary>„2 x 15 min", „60min/partia", „10 min + 5sek" — oder die Kurzform „90+30".</summary>
    private static readonly Regex TimePattern =
        new(@"\d\s*(min|sek|sec)|\d+\s*\+\s*\d+", RegexOptions.Compiled);

    /// <summary>Fuehrende System- und Rundenangabe: „svajciarsky system na 7 kol …".</summary>
    private static readonly Regex LeadingSystemPattern = new(
        @"^\s*(?:[šs]v\w*[čc]iar\w*|kruhov\w*|uzavret\w*)" +
        @"(?:\s+(?:syst[eé]m\w*|turnaj\w*|na))*" +
        @"(?:\s*\d{1,2}\s*\.?\s*k[ôo]l\w*)?\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>„tempo hry" = Spieltempo, „cas" = Zeit — Ankuendigungen, keine Angabe.</summary>
    private static readonly string[] LeadingNoise =
        ["s hracím tempom", "hracím tempom", "tempo hry", "tempo", "hry", "hrania", "cas", "čas"];

    // ----- Hilfen ------------------------------------------------------------

    private static DateOnly? ParseDate(string? text) =>
        DateOnly.TryParseExact(text?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date) ? date : null;

    private static string? Empty(string? text) =>
        text is { Length: > 0 } && Collapse(text) is { Length: > 0 } value ? value : null;

    /// <summary>Kleinschreibung ohne Diakritika — die Quelle schreibt dasselbe Wort verschieden.</summary>
    internal static string Fold(string? text)
    {
        if (text is not { Length: > 0 }) return "";

        var normalized = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(c);
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    internal static void EnsureAllowedTarget(Uri url) => SourceHostGuard.Ensure(url, AllowedHost);
}
