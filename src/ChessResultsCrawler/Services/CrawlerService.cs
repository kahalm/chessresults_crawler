using System.Net;
using System.Text.Json;
using ChessResultsCrawler.Data;
using ChessResultsCrawler.Models;
using Microsoft.Extensions.Logging;

namespace ChessResultsCrawler.Services;

/// <summary>
/// Fassade des chess-results-Crawlers (DI-Dienst, oeffentliche Flaeche unveraendert). Die Arbeit
/// machen <see cref="ChessResultsTransport"/> (HTTP, der EINE Crawl-Riegel, Postback),
/// <see cref="TournamentCrawlJob"/> (Crawl-Ablauf + Upserts) und <see cref="ChessResultsQueries"/>
/// (zustandslose Abfragen); dazu die reinen Helfer <see cref="TeamNameMatching"/> und
/// <see cref="AspNetForm"/>. Alle loggen unter der Kategorie <c>CrawlerService</c>.
/// </summary>
public class CrawlerService
{
    private readonly ChessResultsTransport _transport;
    private readonly TournamentCrawlJob _job;
    private readonly ChessResultsQueries _queries;

    /// <inheritdoc cref="ChessResultsTransport.CrawlGate"/>
    internal static SemaphoreSlim CrawlGate => ChessResultsTransport.CrawlGate;

    /// <inheritdoc cref="ChessResultsTransport.ResetThrottleClock"/>
    internal static void ResetThrottleClock() => ChessResultsTransport.ResetThrottleClock();

    /// <inheritdoc cref="ChessResultsTransport.DefaultMinDelayMs"/>
    internal const int DefaultMinDelayMs = ChessResultsTransport.DefaultMinDelayMs;

    public CrawlerService(HttpClient httpClient, HtmlParserService parser, AppDbContext db,
        ILogger<CrawlerService> logger, IConfiguration configuration, VpnReadinessGate vpnGate)
    {
        _transport = new ChessResultsTransport(httpClient, logger, configuration, vpnGate);
        _job = new TournamentCrawlJob(_transport, parser, db, logger, configuration);
        _queries = new ChessResultsQueries(_transport, parser);
    }

    // ---- Crawl-Ablauf ----

    public Task<CrawlJob> ExecuteCrawlAsync(CrawlJob job, CancellationToken ct = default) =>
        _job.ExecuteCrawlAsync(job, ct);

    public Task CrawlPlayerDetailsAsync(string chessResultsId, List<int> playerSnrs, CancellationToken ct = default) =>
        _job.CrawlPlayerDetailsAsync(chessResultsId, playerSnrs, ct);

    // internal (statt private) nur für den Regressionstest mit doppelten Teamnamen.
    internal Task CrawlTeamPairingsAsync(Tournament tournament, string baseUrl, List<int> availableRounds,
        CancellationToken ct, bool playersFresh = false) =>
        _job.Pairings.CrawlTeamPairingsAsync(tournament, baseUrl, availableRounds, ct, playersFresh);

    /// <inheritdoc cref="TeamNameMatching.BuildTeamNameMap"/>
    public static Dictionary<string, Team> BuildTeamNameMap(IEnumerable<Team> teams) =>
        TeamNameMatching.BuildTeamNameMap(teams);

    /// <inheritdoc cref="TeamNameMatching.StripFootnoteMarker"/>
    public static string StripFootnoteMarker(string name) => TeamNameMatching.StripFootnoteMarker(name);

    /// <inheritdoc cref="TeamNameMatching.FindTeam"/>
    public static Team? FindTeam(IReadOnlyDictionary<string, Team> teams, string? name) =>
        TeamNameMatching.FindTeam(teams, name);

    /// <inheritdoc cref="TeamNameMatching.IsByeOpponent"/>
    public static bool IsByeOpponent(string? teamName) => TeamNameMatching.IsByeOpponent(teamName);

    // ---- Transport ----

    public Task<string> FetchPageAsync(string baseUrl, string queryParams, CancellationToken ct = default) =>
        _transport.FetchPageAsync(baseUrl, queryParams, ct);

    public Task<(string Url, string Html)> FetchWithRedirectAsync(string url, CancellationToken ct = default) =>
        _transport.FetchWithRedirectAsync(url, ct);

    public Task<string> FetchHtmlAsync(string url, CancellationToken ct = default) =>
        _transport.FetchHtmlAsync(url, ct);

    /// <inheritdoc cref="ChessResultsTransport.IsRetryableStatus"/>
    internal static bool IsRetryableStatus(HttpStatusCode status) => ChessResultsTransport.IsRetryableStatus(status);

    /// <inheritdoc cref="ChessResultsTransport.GetRetryAfterDelay"/>
    internal static TimeSpan? GetRetryAfterDelay(HttpResponseMessage response, DateTimeOffset now) =>
        ChessResultsTransport.GetRetryAfterDelay(response, now);

    /// <inheritdoc cref="ChessResultsTransport.IsTransientConnectionError"/>
    public static bool IsTransientConnectionError(Exception ex) => ChessResultsTransport.IsTransientConnectionError(ex);

    /// <inheritdoc cref="AspNetForm.ExtractFormState"/>
    internal static AspNetFormState ExtractFormState(string html) => AspNetForm.ExtractFormState(html);

    /// <inheritdoc cref="AspNetForm.ExtractHiddenField"/>
    internal static string? ExtractHiddenField(string html, string fieldName) =>
        AspNetForm.ExtractHiddenField(html, fieldName);

    /// <summary>Extrahiert die <c>public_ip</c> aus der gluetun-Antwort von <c>/v1/publicip/ip</c>.</summary>
    public static string? ParsePublicIp(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("public_ip", out var ipEl)
                && ipEl.ValueKind == JsonValueKind.String)
            {
                var ip = ipEl.GetString();
                return string.IsNullOrWhiteSpace(ip) ? null : ip;
            }
        }
        catch (JsonException) { /* keine gültige JSON → null */ }
        return null;
    }

    // ---- Zustandslose Abfragen ----

    public Task<List<string>> FetchTeamNamesAsync(string chessResultsId, CancellationToken ct = default) =>
        _queries.FetchTeamNamesAsync(chessResultsId, ct);

    public Task<ParsedTournamentInfo> FetchTournamentInfoAsync(string chessResultsId, CancellationToken ct = default) =>
        _queries.FetchTournamentInfoAsync(chessResultsId, ct);

    public Task<List<ParsedDirectoryTournament>> SearchTournamentsAsync(
        string federation, DateOnly from, DateOnly to, int maxRows = 2000, string art = "5",
        CancellationToken ct = default) =>
        _queries.SearchTournamentsAsync(federation, from, to, maxRows, art, ct);

    /// <param name="federation">Dreibuchstabiger Code, oder <c>-</c> fuer alle.</param>
    /// <param name="ct">Abbruch.</param>
    public Task<List<ParsedCalendarEntry>> FetchCalendarAsync(string federation = "-", CancellationToken ct = default) =>
        _queries.FetchCalendarAsync(federation, ct);

    /// <inheritdoc cref="ChessResultsQueries.RowCountOption"/>
    internal static string RowCountOption(int maxRows) => ChessResultsQueries.RowCountOption(maxRows);

    public Task<List<ParsedPlayerSearchResult>> SearchPlayersAsync(string lastName, string? firstName, CancellationToken ct = default) =>
        _queries.SearchPlayersAsync(lastName, firstName, ct);

    public Task<List<ParsedRoundDate>> FetchRoundPlanAsync(string chessResultsId, CancellationToken ct = default) =>
        _queries.FetchRoundPlanAsync(chessResultsId, ct);

    public Task<ParsedPlayerCard?> FetchPlayerCardAsync(string chessResultsId, int snr, CancellationToken ct = default) =>
        _queries.FetchPlayerCardAsync(chessResultsId, snr, ct);

    public Task<List<ParsedPlayerTournament>> SearchPlayerTournamentsAsync(string lastName, string? firstName, CancellationToken ct = default) =>
        _queries.SearchPlayerTournamentsAsync(lastName, firstName, ct);

    public Task<string> SearchGamesPgnByFideAsync(string fideId, CancellationToken ct = default) =>
        _queries.SearchGamesPgnByFideAsync(fideId, ct);
}
