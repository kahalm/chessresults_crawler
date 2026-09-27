using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using ChessResultsCrawler.Data;
using ChessResultsCrawler.Models;
using Microsoft.EntityFrameworkCore;

namespace ChessResultsCrawler.Services;

/// <summary>
/// Vereine für Turniere, deren Startliste keine Vereinsspalte hat — viele Opens führen nur Name,
/// FIDE-ID, Land, Elo, und die Spielerkarte nennt dann auch keinen.
///
/// <para>Zwei Quellen, beide über die FIDE-ID (ohne sie wäre jeder Namensvetter ein Kandidat):</para>
/// <list type="number">
/// <item><b>Automatisch, ohne Abruf</b> (<see cref="ResolveAsync"/>): derselbe Spieler in einem
/// anderen schon geholten EINZELturnier mit Verein, das jüngste zuerst. Mannschaftsturniere zählen
/// nicht — dort steht die Mannschaft („SK Dornbirn 2", bei der Olympiade das Land), nicht der Verein.</item>
/// <item><b>Auf Knopfdruck</b> (<see cref="FillAsync"/>): die chess-results-Spielersuche, die je
/// Turnier den Verein nennt, den der Spieler dort hatte — ein Suchabruf je Spieler, gemerkt in
/// <see cref="PlayerClub"/> für jedes weitere Turnier.</item>
/// </list>
/// <para>Die Ansicht zeigt beide als ÜBERNOMMEN („laut …"): der Verein kann in diesem Turnier ein
/// anderer gewesen sein.</para>
/// </summary>
public class ClubService
{
    /// <summary>So viele Spieler sucht EIN Knopfdruck höchstens (je zwei Seitenabrufe).</summary>
    public const int MaxLookupsPerRun = 150;

    /// <summary>So lange gilt ein Suchergebnis — auch ein leeres —, bevor derselbe Spieler neu gesucht wird.</summary>
    public static readonly TimeSpan LookupTtl = TimeSpan.FromDays(60);

    /// <summary>Welche Turniere gerade nachgetragen werden (Prozess-weit: ein Lauf je Turnier).</summary>
    private static readonly ConcurrentDictionary<string, byte> Running = new();

    private readonly AppDbContext _db;
    private readonly CrawlerService _crawler;
    private readonly ILogger<ClubService> _logger;

    public ClubService(AppDbContext db, CrawlerService crawler, ILogger<ClubService> logger)
    {
        _db = db;
        _crawler = crawler;
        _logger = logger;
    }

    public sealed record ResolvedClub(string Club, string? SourceTournamentName);

    /// <summary>
    /// Vereine für die Spieler ohne eigenen (nach Spieler-Id): zuerst der Treffer der Spielersuche,
    /// sonst derselbe Spieler in einem anderen geholten Einzelturnier, das jüngste zuerst.
    /// </summary>
    public async Task<Dictionary<int, ResolvedClub>> ResolveAsync(
        int tournamentId, IReadOnlyCollection<Player> players, CancellationToken ct = default)
    {
        var missing = players
            .Where(p => p.Team is null && p.TeamId is null && Fide(p.FideId) is not null)
            .ToList();
        if (missing.Count == 0) return [];

        var fideIds = missing.Select(p => Fide(p.FideId)!).Distinct().ToList();

        var searched = await _db.PlayerClubs.AsNoTracking()
            .Where(c => fideIds.Contains(c.FideId) && c.Club != null)
            .ToDictionaryAsync(c => c.FideId, ct);

        var elsewhere = await _db.Players.AsNoTracking()
            .Where(p => p.FideId != null && fideIds.Contains(p.FideId)
                        && p.TournamentId != tournamentId && p.TeamId != null && p.BoardNumber == null)
            .Select(p => new { p.FideId, Club = p.Team!.Name, p.Tournament.Name, p.Tournament.DateText, p.TournamentId })
            .ToListAsync(ct);
        var latest = elsewhere
            .Where(x => !string.IsNullOrWhiteSpace(x.Club))
            .GroupBy(x => x.FideId!)
            .ToDictionary(g => g.Key, g => g
                .OrderByDescending(x => LastDate(x.DateText) ?? DateOnly.MinValue)
                .ThenByDescending(x => x.TournamentId)
                .First());

        var result = new Dictionary<int, ResolvedClub>();
        foreach (var player in missing)
        {
            var fide = Fide(player.FideId)!;
            if (searched.TryGetValue(fide, out var hit))
                result[player.Id] = new ResolvedClub(hit.Club!, hit.SourceTournamentName);
            else if (latest.TryGetValue(fide, out var other))
                result[player.Id] = new ResolvedClub(other.Club, other.Name);
        }
        return result;
    }

    /// <summary>
    /// Wie viele Spieler dieses Turniers ein Knopfdruck noch suchen würde: ohne eigenen Verein, mit
    /// FIDE-ID und ohne frisches Suchergebnis. Die Ansicht fragt das nach, um den Fortschritt zu zeigen.
    /// </summary>
    public async Task<(int Pending, bool Running)> StatusAsync(Tournament tournament, CancellationToken ct = default)
    {
        var candidates = await CandidatesAsync(tournament.Id, ct);
        return (candidates.Count, Running.ContainsKey(tournament.ChessResultsId));
    }

    /// <summary>Beginnt einen Lauf, wenn für dieses Turnier keiner läuft. <c>false</c> = läuft schon.</summary>
    public static bool TryBegin(string chessResultsId) => Running.TryAdd(chessResultsId, 0);

    /// <summary>Gibt einen begonnenen Lauf frei, der gar nicht erst eingereiht werden konnte.</summary>
    public static void End(string chessResultsId) => Running.TryRemove(chessResultsId, out _);

    /// <summary>
    /// Sucht die Spieler ohne Verein über die chess-results-Spielersuche und merkt sich den Verein
    /// aus ihrem jüngsten Turnier, das einen nennt. Läuft im Hintergrund; <see cref="TryBegin"/> muss
    /// vorher gelungen sein, das Ende gibt den Lauf frei.
    /// </summary>
    public async Task<int> FillAsync(string chessResultsId, CancellationToken ct = default)
    {
        try
        {
            var tournament = await _db.Tournaments.FirstOrDefaultAsync(t => t.ChessResultsId == chessResultsId, ct);
            if (tournament is null) return 0;

            var candidates = (await CandidatesAsync(tournament.Id, ct)).Take(MaxLookupsPerRun).ToList();
            var found = 0;
            foreach (var player in candidates)
            {
                ct.ThrowIfCancellationRequested();
                var (last, first) = SplitName(player.Name);
                if (last is null) continue;

                List<ParsedPlayerTournament> rows;
                try
                {
                    rows = await _crawler.SearchPlayerTournamentsAsync(last, first, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Nichts merken: ein Netzfehler ist keine Auskunft über den Spieler.
                    _logger.LogWarning(ex, "Vereinssuche {Name} ({Fide}) fehlgeschlagen", player.Name, player.FideId);
                    continue;
                }

                var hit = PickClub(rows, Fide(player.FideId)!, chessResultsId);
                await RememberAsync(Fide(player.FideId)!, hit, ct);
                if (hit is not null) found++;
            }

            _logger.LogInformation("Vereine für {Id}: {Found} von {Searched} Spielern gefunden", chessResultsId, found, candidates.Count);
            return found;
        }
        finally
        {
            Running.TryRemove(chessResultsId, out _);
        }
    }

    /// <summary>
    /// Der Verein aus dem JÜNGSTEN anderen Turnier desselben Spielers (gleiche FIDE-ID), das einen
    /// nennt. Zeilen eines Namensvetters (andere FIDE-ID) zählen nie.
    /// </summary>
    internal static ParsedPlayerTournament? PickClub(IEnumerable<ParsedPlayerTournament> rows, string fideId, string ownTournamentId) =>
        rows.Where(r => Fide(r.FideId) == fideId && !string.IsNullOrWhiteSpace(r.Club) && r.TournamentId != ownTournamentId)
            // Nach dem DATUM, nicht dem Text: je nach Sprache kommt es als yyyy/MM/dd oder dd.MM.yyyy.
            .OrderByDescending(r => LastDate(r.EndDate) ?? DateOnly.MinValue)
            .FirstOrDefault();

    /// <summary>
    /// „Martinovic, Sasa" → (Martinovic, Sasa); ohne Komma („Kozul Zdenko", so führen manche
    /// Startlisten die Namen) das erste Wort als Nachname — chess-results schreibt den Nachnamen vorn.
    /// </summary>
    internal static (string? Last, string? First) SplitName(string name)
    {
        var text = Regex.Replace(name ?? "", @"\s+", " ").Trim();
        var comma = text.IndexOf(',');
        string last, first;
        if (comma >= 0)
        {
            last = text[..comma].Trim();
            first = text[(comma + 1)..].Trim();
        }
        else
        {
            var space = text.IndexOf(' ');
            last = space < 0 ? text : text[..space];
            first = space < 0 ? "" : text[(space + 1)..].Trim();
        }
        return last.Length < 2 ? (null, null) : (last, first.Length == 0 ? null : first);
    }

    private async Task<List<Player>> CandidatesAsync(int tournamentId, CancellationToken ct)
    {
        var players = await _db.Players.AsNoTracking()
            .Where(p => p.TournamentId == tournamentId && p.TeamId == null && p.FideId != null && p.FideId != "" && p.FideId != "0")
            .OrderBy(p => p.Snr)
            .ToListAsync(ct);
        if (players.Count == 0) return players;

        var fideIds = players.Select(p => Fide(p.FideId)!).Distinct().ToList();
        var fresh = DateTime.UtcNow - LookupTtl;
        var known = (await _db.PlayerClubs.AsNoTracking()
                .Where(c => fideIds.Contains(c.FideId) && c.FetchedAt >= fresh)
                .Select(c => c.FideId)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        return players.Where(p => !known.Contains(Fide(p.FideId)!)).ToList();
    }

    private async Task RememberAsync(string fideId, ParsedPlayerTournament? hit, CancellationToken ct)
    {
        var row = await _db.PlayerClubs.FirstOrDefaultAsync(c => c.FideId == fideId, ct);
        if (row is null)
        {
            row = new PlayerClub { FideId = fideId };
            _db.PlayerClubs.Add(row);
        }
        row.Club = Truncate(hit?.Club?.Trim(), 300);
        row.SourceTournamentId = hit?.TournamentId;
        row.SourceTournamentName = Truncate(hit?.TournamentName, 500);
        row.SourceEndDate = Truncate(hit?.EndDate, 20);
        row.FetchedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>„0" und leer sind bei chess-results „keine Nummer".</summary>
    internal static string? Fide(string? value)
    {
        var text = (value ?? "").Trim();
        return text.Length == 0 || text == "0" ? null : text;
    }

    /// <summary>Das LETZTE Datum im Datumsfeld eines Turniers („01.09.2026 - 05.09.2026", „2026/09/27").</summary>
    internal static DateOnly? LastDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        DateOnly? last = null;
        foreach (Match m in Regex.Matches(text, @"\d{4}/\d{2}/\d{2}|\d{2}\.\d{2}\.\d{4}"))
        {
            if (DateOnly.TryParseExact(m.Value, ["yyyy/MM/dd", "dd.MM.yyyy"], CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var d))
                last = d;
        }
        return last;
    }

    private static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];
}
