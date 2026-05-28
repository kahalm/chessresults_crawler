using ChessResultsCrawler.DTOs;
using ChessResultsCrawler.Models;
using ChessResultsCrawler.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChessResultsCrawler.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TournamentsController : ControllerBase
{
    private readonly TournamentService _service;
    private readonly RoundDetectionService _roundDetection;

    public TournamentsController(TournamentService service, RoundDetectionService roundDetection)
    {
        _service = service;
        _roundDetection = roundDetection;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 1;
        if (pageSize > 200) pageSize = 200;

        var (items, totalCount) = await _service.GetAllTournamentsPagedAsync(page, pageSize);
        return Ok(new
        {
            items = items.Select(TournamentResponse.FromEntity),
            totalCount,
            page,
            pageSize
        });
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(string id)
    {
        var tournament = await ResolveTournamentAsync(id);
        if (tournament is null) return NotFound();
        return Ok(TournamentResponse.FromEntity(tournament));
    }

    [HttpGet("{id}/players")]
    public async Task<IActionResult> GetPlayers(string id, [FromQuery] string? team, [FromQuery] string? sortBy)
    {
        var tournament = await ResolveTournamentAsync(id);
        if (tournament is null) return NotFound();

        var players = await _service.GetPlayersAsync(tournament.Id, team, sortBy);
        return Ok(players.Select(PlayerResponse.FromEntity));
    }

    [HttpGet("{id}/teams")]
    public async Task<IActionResult> GetTeams(string id)
    {
        var tournament = await ResolveTournamentAsync(id);
        if (tournament is null) return NotFound();

        var teams = await _service.GetTeamsAsync(tournament.Id);
        return Ok(teams.Select(t => TeamResponse.FromEntity(t)));
    }

    [HttpGet("{id}/teams/{snr:int}")]
    public async Task<IActionResult> GetTeam(string id, int snr)
    {
        var tournament = await ResolveTournamentAsync(id);
        if (tournament is null) return NotFound();

        var team = await _service.GetTeamAsync(tournament.Id, snr);
        if (team is null) return NotFound();
        return Ok(TeamResponse.FromEntity(team, includePlayers: true));
    }

    [HttpGet("{id}/pairings")]
    public async Task<IActionResult> GetPairings(string id, [FromQuery] int? round)
    {
        var tournament = await ResolveTournamentAsync(id);
        if (tournament is null) return NotFound();

        // Return team pairings if available, otherwise individual pairings
        var hasTeam = await _service.HasTeamPairingsAsync(tournament.Id);
        if (hasTeam)
        {
            var pairings = await _service.GetPairingsAsync(tournament.Id, round);
            return Ok(pairings.Select(TeamPairingResponse.FromEntity));
        }
        else
        {
            var pairings = await _service.GetIndividualPairingsAsync(tournament.Id, round);
            return Ok(pairings.Select(PairingResponse.FromEntity));
        }
    }

    [HttpGet("{id}/pairings/latest")]
    public async Task<IActionResult> GetLatestPairings(string id)
    {
        var tournament = await ResolveTournamentAsync(id);
        if (tournament is null) return NotFound();

        var hasTeam = await _service.HasTeamPairingsAsync(tournament.Id);
        if (hasTeam)
        {
            var pairings = await _service.GetLatestPairingsAsync(tournament.Id);
            return Ok(pairings.Select(TeamPairingResponse.FromEntity));
        }
        else
        {
            var pairings = await _service.GetLatestIndividualPairingsAsync(tournament.Id);
            return Ok(pairings.Select(PairingResponse.FromEntity));
        }
    }

    [HttpGet("{id}/rounds")]
    public async Task<IActionResult> GetRounds(string id)
    {
        var tournament = await ResolveTournamentAsync(id);
        if (tournament is null) return NotFound();

        var rounds = await _service.GetRoundsAsync(tournament.Id);
        return Ok(rounds.Select(RoundResponse.FromEntity));
    }

    [HttpGet("{id}/players/{snr:int}/results")]
    public async Task<IActionResult> GetPlayerResults(string id, int snr)
    {
        var tournament = await ResolveTournamentAsync(id);
        if (tournament is null) return NotFound();

        var results = await _service.GetPlayerResultsAsync(tournament.Id, snr);
        return Ok(results.Select(PlayerResultResponse.FromEntity));
    }

    [HttpGet("{id}/rounds/check")]
    public async Task<IActionResult> CheckNewRounds(string id)
    {
        var tournament = await ResolveTournamentAsync(id);
        if (tournament is null) return NotFound();

        var result = await _roundDetection.CheckForNewRoundsAsync(tournament);
        return Ok(result);
    }

    private async Task<Tournament?> ResolveTournamentAsync(string id)
    {
        if (int.TryParse(id, out var intId))
        {
            return await _service.GetTournamentAsync(intId)
                ?? await _service.GetTournamentByChessResultsIdAsync(id);
        }
        return await _service.GetTournamentByChessResultsIdAsync(id);
    }
}
