using System.Text.RegularExpressions;
using ChessResultsCrawler.DTOs;
using ChessResultsCrawler.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChessResultsCrawler.Controllers;

/// <summary>
/// LeagueHub (RookHub): zustandslos wie die Turniersuche — holt die vier Seiten einer Mannschaftsliga
/// (art=2/3/16/20, immer mit <c>zeilen=99999</c>) bzw. die PGN-Partien eines Spielers und gibt sie
/// geparst zurück. Gespeichert wird nichts; das Fachliche liegt in RookHub.
/// </summary>
[ApiController]
[Route("api/league")]
public class LeagueController : ControllerBase
{
    private static readonly Regex Digits = new(@"^\d{1,12}$", RegexOptions.Compiled);
    private readonly CrawlerService _crawler;
    public LeagueController(CrawlerService crawler) => _crawler = crawler;

    internal static string PageUrl(int tnr, int art) =>
        $"https://chess-results.com/tnr{tnr}.aspx?lan=0&art={art}&zeilen=99999&turdet=YES";

    [HttpGet("{tnr:int}")]
    public async Task<ActionResult<LeaguePagesResponse>> Pages(int tnr, CancellationToken ct)
    {
        if (tnr <= 0) return BadRequest(new { message = "tnr must be positive." });
        // nacheinander, nicht parallel: chess-results drosselt Salven (Turnierverzeichnis, 2026-09-06)
        var matches = await LeaguePagesParser.ParseMatchesAsync(await _crawler.FetchHtmlAsync(PageUrl(tnr, 2), ct));
        var (games, dates) = await LeaguePagesParser.ParseGamesAsync(await _crawler.FetchHtmlAsync(PageUrl(tnr, 3), ct));
        var roster = await LeaguePagesParser.ParseRosterAsync(await _crawler.FetchHtmlAsync(PageUrl(tnr, 16), ct));
        var stats = await LeaguePagesParser.ParseStatsAsync(await _crawler.FetchHtmlAsync(PageUrl(tnr, 20), ct));
        return Ok(new LeaguePagesResponse(tnr, matches, games, dates, roster, stats));
    }

    /// <summary>Alle Partien eines Spielers aus der chess-results-Partiedatenbank (PGN, leer = keine).</summary>
    [HttpGet("games/{fideId}")]
    public async Task<IActionResult> Games(string fideId, CancellationToken ct)
    {
        if (!Digits.IsMatch(fideId)) return BadRequest(new { message = "fideId must be numeric." });
        var pgn = await _crawler.SearchGamesPgnByFideAsync(fideId, ct);
        return Content(pgn, "application/x-chess-pgn");
    }
}
