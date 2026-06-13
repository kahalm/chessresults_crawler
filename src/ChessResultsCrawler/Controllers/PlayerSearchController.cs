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
        if (string.IsNullOrWhiteSpace(lastName) || lastName.Trim().Length < 2)
            return BadRequest(new { message = "lastName must be at least 2 characters." });

        if (lastName.Length > 100) lastName = lastName[..100];
        if (firstName?.Length > 100) firstName = firstName[..100];

        var results = await _crawlerService.SearchPlayersAsync(lastName.Trim(), firstName?.Trim(), ct);
        return Ok(results.Select(PlayerSearchResponse.FromParsed).ToList());
    }

    [HttpGet("tournaments")]
    public async Task<ActionResult<List<PlayerTournamentResponse>>> SearchTournaments(
        [FromQuery] string lastName, [FromQuery] string? firstName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(lastName) || lastName.Trim().Length < 2)
            return BadRequest(new { message = "lastName must be at least 2 characters." });

        if (lastName.Length > 100) lastName = lastName[..100];
        if (firstName?.Length > 100) firstName = firstName[..100];

        var results = await _crawlerService.SearchPlayerTournamentsAsync(lastName.Trim(), firstName?.Trim(), ct);
        return Ok(results.Select(PlayerTournamentResponse.FromParsed).ToList());
    }
}
