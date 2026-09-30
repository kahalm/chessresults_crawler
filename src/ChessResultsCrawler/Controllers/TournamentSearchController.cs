using System.Globalization;
using System.Text.RegularExpressions;
using ChessResultsCrawler.DTOs;
using ChessResultsCrawler.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChessResultsCrawler.Controllers;

/// <summary>
/// Turnierverzeichnis-Abfrage: eine Foederation, ein Zeitfenster, eine Trefferliste. Zustandslos -
/// nichts wird hier gespeichert, das Verzeichnis lebt in RookHub. Laeuft wie alle anderen Routen
/// hinter der ApiKeyMiddleware.
/// </summary>
[ApiController]
[Route("api/tournament-search")]
public class TournamentSearchController : ControllerBase
{
    // Die Suche filtert auf das End-Datum. Ein Fenster ueber ~3 Jahre bringt keine zusaetzlichen
    // Treffer mehr (chess-results kappt bei 2000 Zeilen), kostet aber Antwortzeit.
    private const int MaxWindowDays = 3 * 366;
    private const int MaxRowsCap = 2000;

    private static readonly Regex FederationPattern = new(@"^[A-Za-z]{3}$", RegexOptions.Compiled);

    /// <summary>Turnierart „alle" — der Vorgabewert der Suchmaske.</summary>
    public const string AllTournamentTypes = "5";

    /// <summary>
    /// Die Turnierarten der Suchmaske: 0 Schweizer System, 1 Rundenturnier, 2 Rundenturnier fuer
    /// Mannschaften, 3 Schweizer System fuer Mannschaften, 5 alle.
    /// </summary>
    private static readonly string[] AllowedTypes = ["0", "1", "2", "3", AllTournamentTypes];

    /// <summary>Die chess-results-Turniernummer ist rein numerisch — nichts anderes wird geholt.</summary>
    private static readonly Regex TournamentIdPattern = new(@"^\d{1,10}$", RegexOptions.Compiled);

    private readonly CrawlerService _crawlerService;

    public TournamentSearchController(CrawlerService crawlerService)
    {
        _crawlerService = crawlerService;
    }

    [HttpGet]
    public async Task<ActionResult<List<DirectoryTournamentResponse>>> Search(
        [FromQuery] string fed,
        [FromQuery] string from,
        [FromQuery] string to,
        [FromQuery] int maxRows = MaxRowsCap,
        [FromQuery] string? art = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fed) || !FederationPattern.IsMatch(fed.Trim()))
            return BadRequest(new { message = "fed must be a 3-letter federation code (e.g. AUT)." });

        if (!TryParseIsoDate(from, out var fromDate))
            return BadRequest(new { message = "from must be an ISO date (yyyy-MM-dd)." });

        if (!TryParseIsoDate(to, out var toDate))
            return BadRequest(new { message = "to must be an ISO date (yyyy-MM-dd)." });

        if (toDate < fromDate)
            return BadRequest(new { message = "to must not be before from." });

        if (toDate.DayNumber - fromDate.DayNumber > MaxWindowDays)
            return BadRequest(new { message = $"Date window must not exceed {MaxWindowDays} days." });

        maxRows = Math.Clamp(maxRows, 1, MaxRowsCap);

        // Die Turnierart ist ein Dropdown-INDEX der Suchmaske. Nur die tatsaechlich vorhandenen
        // Werte durchlassen: ein erfundener Index laesst ASP.NET die Auswahl verwerfen, die Suche
        // liefert dann stillschweigend etwas anderes als bestellt.
        var artValue = (art ?? "").Trim();
        if (artValue.Length == 0) artValue = AllTournamentTypes;
        if (!AllowedTypes.Contains(artValue))
            return BadRequest(new { message = $"art must be one of {string.Join(", ", AllowedTypes)}." });

        var results = await _crawlerService.SearchTournamentsAsync(
            fed.Trim().ToUpperInvariant(), fromDate, toDate, maxRows, artValue, ct);

        var now = DateTime.UtcNow;
        return Ok(results.Select(r => DirectoryTournamentResponse.FromParsed(r, now)).ToList());
    }

    /// <summary>
    /// Die Vereins-/Mannschaftsnamen eines Turniers. Zustandslos wie die Suche — ein Seitenabruf,
    /// nichts wird gespeichert. RookHub benutzt sie, um einen mehrdeutigen Spielort aufzuloesen
    /// („St.Veit" ist Tirol ODER Kaernten; „SV ASKOE St. Veit/Glan" sagt, welches).
    /// </summary>
    [HttpGet("teams")]
    public async Task<ActionResult<List<string>>> Teams([FromQuery] string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id) || !TournamentIdPattern.IsMatch(id.Trim()))
            return BadRequest(new { message = "Invalid tournament ID." });

        return Ok(await _crawlerService.FetchTeamNamesAsync(id.Trim(), ct));
    }

    /// <summary>
    /// Die Turnierhistorie eines Spielers — EIN Seitenabruf, der alle Teilnahmen liefert
    /// (vergangene UND kuenftige), je Zeile mit Turnier-Id, Enddatum, Platz, Rundenzahl,
    /// Teilnehmerzahl und der STARTNUMMER (die nur im Link steht und die Spielerkarte adressiert).
    ///
    /// <para>Gesucht wird ueber den NAMEN, weil chess-results keine Suche ueber die Ident-Nummer
    /// anbietet. Die Zeilen tragen Ident-Nummer und Fide-ID mit, damit der Aufrufer bei
    /// Namensgleichheit die richtige Person auswaehlen kann — und weil bei Auslandsturnieren die
    /// Ident-Nummer „0" ist und nur die Fide-ID die Identitaet traegt.</para>
    ///
    /// <para><c>GET /api/players/tournaments</c> ist ein Alias mit derselben Pruefung (Namen getrimmt, auf 100
    /// Zeichen gekuerzt) — beide laufen durch <see cref="PlayerNameQuery.TournamentsAsync"/>.</para>
    /// </summary>
    [HttpGet("player-history")]
    public Task<ActionResult<List<PlayerTournamentResponse>>> PlayerHistory(
        [FromQuery] string lastName, [FromQuery] string? firstName = null, CancellationToken ct = default)
        => PlayerNameQuery.TournamentsAsync(_crawlerService, lastName, firstName, ct);

    /// <summary>
    /// Kopfdaten EINES Turniers, ohne es zu importieren: Termin, Ort, Rundenzahl — und die
    /// BEDENKZEIT als Rohtext.
    ///
    /// <para>Die Spielersuche liefert Termin, Platz und Rundenzahl, aber keine Bedenkzeit. Ohne die
    /// stehen im Turnierverlauf Blitz- und Turnierschach-Ergebnisse in derselben Spalte, als
    /// waeren sie vergleichbar. Welche KLASSE daraus wird, entscheidet der Aufrufer — hier steht
    /// nur, was auf der Seite steht.</para>
    /// </summary>
    [HttpGet("tournament-info")]
    public async Task<ActionResult<TournamentInfoResponse>> TournamentInfo(
        [FromQuery] string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id) || !TournamentIdPattern.IsMatch(id.Trim()))
            return BadRequest(new { message = "Invalid tournament ID." });

        var info = await _crawlerService.FetchTournamentInfoAsync(id.Trim(), ct);
        return Ok(TournamentInfoResponse.FromParsed(info));
    }

    /// <summary>
    /// Der ANKUENDIGUNGS-Kalender — der ZWEITE Datenbestand von chess-results, nicht die
    /// Turniersuche. Zustandslos, ein Seitenabruf.
    ///
    /// <para><b>Warum es beide gibt.</b> Die Turniersuche fuellt sich, wenn der Veranstalter seine
    /// Swiss-Manager-Datei hochlaedt — typisch Tage bis Wochen vorher. Der Kalender wird VORAB
    /// gepflegt. Am 2026-09-07 fuer AUT gemessen: Turniersuche 8 im November beginnende Turniere
    /// und 7 im Dezember, Kalender 23 und 16; von 143 kuenftigen Kalendereintraegen fehlten 93 in
    /// der Turniersuche.</para>
    ///
    /// <para><b>Ein Abruf genuegt fuer alles.</b> Ohne <paramref name="fed"/> (bzw. mit <c>-</c>)
    /// kommen alle Foederationen zusammen: 209 kuenftige Eintraege, davon AUT 143, GER 21, SUI 21,
    /// CZE 7. Der Kalender kennt nur 16 Foederationen — er ersetzt die Turniersuche also nicht
    /// (die deckt 261 ab), sondern ergaenzt sie um Vorlauf.</para>
    ///
    /// <para>Was er NICHT liefert: Ort, Bedenkzeit, Rundenzahl. Es ist eine Ankuendigung, keine
    /// Turnierseite.</para>
    /// </summary>
    [HttpGet("calendar")]
    public async Task<ActionResult<List<CalendarEntryResponse>>> Calendar(
        [FromQuery] string? fed = null, CancellationToken ct = default)
    {
        var federation = string.IsNullOrWhiteSpace(fed) ? "-" : fed.Trim().ToUpperInvariant();
        if (federation != "-" && !FederationPattern.IsMatch(federation))
            return BadRequest(new { message = "fed must be a three-letter code or '-' for all." });

        var entries = await _crawlerService.FetchCalendarAsync(federation, ct);
        return Ok(entries.Select(CalendarEntryResponse.FromParsed).ToList());
    }

    /// <summary>
    /// Der Rundenplan eines Turniers: je Runde Nummer, Datum und Uhrzeit. Zustandslos, ein
    /// Seitenabruf.
    ///
    /// <para>Gebraucht, weil Start- und Enddatum einer LIGA nicht sagen, wann gespielt wird:
    /// elf Runden von September bis April liegen Wochen auseinander. Eine leere Liste ist der
    /// Normalfall bei Turnieren ohne hinterlegten Plan, kein Fehler.</para>
    /// </summary>
    [HttpGet("rounds")]
    public async Task<ActionResult<List<RoundDateResponse>>> Rounds(
        [FromQuery] string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id) || !TournamentIdPattern.IsMatch(id.Trim()))
            return BadRequest(new { message = "Invalid tournament ID." });

        var rounds = await _crawlerService.FetchRoundPlanAsync(id.Trim(), ct);
        return Ok(rounds.Select(RoundDateResponse.FromParsed).ToList());
    }

    /// <summary>
    /// Die Spielerkarte: Punkte, Platz, Performance-Rating und Elo-Aenderung eines Spielers in
    /// EINEM Turnier. Zustandslos, ein Seitenabruf.
    ///
    /// <para>204, wenn die Seite keinen Player-info-Block hat (falsche Startnummer). Ein
    /// KUENFTIGES Turnier antwortet dagegen mit 200 und <c>hasResult: false</c> — der
    /// Unterschied ist wesentlich: das eine ist ein Fehler, das andere der Normalfall.</para>
    /// </summary>
    [HttpGet("player-card")]
    public async Task<ActionResult<PlayerCardResponse>> PlayerCard(
        [FromQuery] string id, [FromQuery] int snr, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id) || !TournamentIdPattern.IsMatch(id.Trim()))
            return BadRequest(new { message = "Invalid tournament ID." });

        if (snr is < 1 or > 10000)
            return BadRequest(new { message = "snr must be between 1 and 10000." });

        var card = await _crawlerService.FetchPlayerCardAsync(id.Trim(), snr, ct);
        return card is null ? NoContent() : Ok(PlayerCardResponse.FromParsed(card));
    }

    private static bool TryParseIsoDate(string? text, out DateOnly date)
    {
        date = default;
        return !string.IsNullOrWhiteSpace(text)
            && DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }
}
