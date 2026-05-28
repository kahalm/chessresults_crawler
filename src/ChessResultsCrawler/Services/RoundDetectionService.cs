using ChessResultsCrawler.Data;
using ChessResultsCrawler.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace ChessResultsCrawler.Services;

public class RoundDetectionService
{
    private readonly CrawlerService _crawler;
    private readonly HtmlParserService _parser;
    private readonly AppDbContext _db;
    private readonly IMemoryCache _cache;

    public RoundDetectionService(CrawlerService crawler, HtmlParserService parser, AppDbContext db, IMemoryCache cache)
    {
        _crawler = crawler;
        _parser = parser;
        _db = db;
        _cache = cache;
    }

    public async Task<RoundCheckResult> CheckForNewRoundsAsync(Tournament tournament)
    {
        var cacheKey = $"RoundCheck_{tournament.Id}";
        if (_cache.TryGetValue<RoundCheckResult>(cacheKey, out var cached))
            return cached!;

        var baseUrl = tournament.BaseUrl
            ?? $"https://chess-results.com/tnr{tournament.ChessResultsId}.aspx?lan=0";

        var html = await _crawler.FetchPageAsync(baseUrl, "art=2");
        var availableRounds = await _parser.ParseAvailableRoundsAsync(html);

        var knownRounds = await _db.Rounds
            .Where(r => r.TournamentId == tournament.Id)
            .Select(r => r.RoundNumber)
            .ToListAsync();

        var newRounds = availableRounds.Except(knownRounds).OrderBy(r => r).ToList();

        var result = new RoundCheckResult
        {
            KnownRounds = knownRounds.Count,
            AvailableRounds = availableRounds.Count,
            HasNewRound = newRounds.Count > 0,
            NewRoundNumbers = newRounds
        };

        _cache.Set(cacheKey, result, TimeSpan.FromSeconds(60));
        return result;
    }
}

public class RoundCheckResult
{
    public int KnownRounds { get; set; }
    public int AvailableRounds { get; set; }
    public bool HasNewRound { get; set; }
    public List<int> NewRoundNumbers { get; set; } = [];
}
