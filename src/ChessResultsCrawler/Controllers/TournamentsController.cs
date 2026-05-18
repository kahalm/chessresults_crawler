using ChessResultsCrawler.DTOs;
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
    public async Task<IActionResult> GetAll()
    {
        var tournaments = await _service.GetAllTournamentsAsync();
        return Ok(tournaments.Select(TournamentResponse.FromEntity));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var tournament = await _service.GetTournamentAsync(id);
        if (tournament is null) return NotFound();
        return Ok(TournamentResponse.FromEntity(tournament));
    }

    [HttpGet("{id:int}/players")]
    public async Task<IActionResult> GetPlayers(int id, [FromQuery] string? team, [FromQuery] string? sortBy)
    {
        var tournament = await _service.GetTournamentAsync(id);
        if (tournament is null) return NotFound();

        var players = await _service.GetPlayersAsync(id, team, sortBy);
        return Ok(players.Select(PlayerResponse.FromEntity));
    }

    [HttpGet("{id:int}/teams")]
    public async Task<IActionResult> GetTeams(int id)
    {
        var tournament = await _service.GetTournamentAsync(id);
        if (tournament is null) return NotFound();

        var teams = await _service.GetTeamsAsync(id);
        return Ok(teams.Select(t => TeamResponse.FromEntity(t)));
    }

    [HttpGet("{id:int}/teams/{snr:int}")]
    public async Task<IActionResult> GetTeam(int id, int snr)
    {
        var team = await _service.GetTeamAsync(id, snr);
        if (team is null) return NotFound();
        return Ok(TeamResponse.FromEntity(team, includePlayers: true));
    }

    [HttpGet("{id:int}/pairings")]
    public async Task<IActionResult> GetPairings(int id, [FromQuery] int? round)
    {
        var tournament = await _service.GetTournamentAsync(id);
        if (tournament is null) return NotFound();

        var pairings = await _service.GetPairingsAsync(id, round);
        return Ok(pairings.Select(TeamPairingResponse.FromEntity));
    }

    [HttpGet("{id:int}/pairings/latest")]
    public async Task<IActionResult> GetLatestPairings(int id)
    {
        var tournament = await _service.GetTournamentAsync(id);
        if (tournament is null) return NotFound();

        var pairings = await _service.GetLatestPairingsAsync(id);
        return Ok(pairings.Select(TeamPairingResponse.FromEntity));
    }

    [HttpGet("{id:int}/rounds")]
    public async Task<IActionResult> GetRounds(int id)
    {
        var tournament = await _service.GetTournamentAsync(id);
        if (tournament is null) return NotFound();

        var rounds = await _service.GetRoundsAsync(id);
        return Ok(rounds.Select(RoundResponse.FromEntity));
    }

    [HttpGet("{id:int}/rounds/check")]
    public async Task<IActionResult> CheckNewRounds(int id)
    {
        var tournament = await _service.GetTournamentAsync(id);
        if (tournament is null) return NotFound();

        var result = await _roundDetection.CheckForNewRoundsAsync(tournament);
        return Ok(result);
    }
}
