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
        [FromQuery] string? lastName, [FromQuery] string? firstName, [FromQuery] string? identNumber)
    {
        var hasName = !string.IsNullOrWhiteSpace(lastName) && lastName.Trim().Length >= 2;
        var hasIdent = !string.IsNullOrWhiteSpace(identNumber) && identNumber.Trim().Length >= 1;

        if (!hasName && !hasIdent)
            return BadRequest(new { message = "lastName (min 2 chars) or identNumber required." });

        if (lastName?.Length > 100) lastName = lastName[..100];
        if (firstName?.Length > 100) firstName = firstName[..100];
        if (identNumber?.Length > 20) identNumber = identNumber[..20];

        var results = await _crawlerService.SearchPlayersAsync(lastName?.Trim(), firstName?.Trim(), identNumber?.Trim());
        return Ok(results.Select(PlayerSearchResponse.FromParsed).ToList());
    }
}
