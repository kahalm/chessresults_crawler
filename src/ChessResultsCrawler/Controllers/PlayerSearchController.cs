using ChessResultsCrawler.DTOs;
using ChessResultsCrawler.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChessResultsCrawler.Controllers;

[ApiController]
[Route("api/players")]
public class PlayerSearchController : ControllerBase
{
    private readonly CrawlerService _crawlerService;

    public PlayerSearchController(CrawlerService crawlerService)
    {
        _crawlerService = crawlerService;
    }

    [HttpGet("search")]
    public async Task<ActionResult<List<PlayerSearchResponse>>> Search(
        [FromQuery] string lastName, [FromQuery] string? firstName, CancellationToken ct)
    {
        if (!PlayerNameQuery.TryNormalize(lastName, firstName, out var last, out var first))
            return BadRequest(new { message = PlayerNameQuery.LastNameTooShortMessage });

        var results = await _crawlerService.SearchPlayersAsync(last, first, ct);
        return Ok(results.Select(PlayerSearchResponse.FromParsed).ToList());
    }

    /// <summary>
    /// ALIAS von <c>GET /api/tournament-search/player-history</c>: dieselbe Pruefung (inkl. Kuerzung auf 100
    /// Zeichen), derselbe Abruf, dieselbe Antwort (<see cref="PlayerNameQuery.TournamentsAsync"/>). Bleibt, solange
    /// RookHubs AutoSubscriptionService ihn aufruft; neue Aufrufer nehmen player-history.
    /// </summary>
    [HttpGet("tournaments")]
    public Task<ActionResult<List<PlayerTournamentResponse>>> SearchTournaments(
        [FromQuery] string lastName, [FromQuery] string? firstName, CancellationToken ct)
        => PlayerNameQuery.TournamentsAsync(_crawlerService, lastName, firstName, ct);
}
