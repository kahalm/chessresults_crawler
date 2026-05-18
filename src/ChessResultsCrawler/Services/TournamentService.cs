using ChessResultsCrawler.Data;
using ChessResultsCrawler.Models;
using Microsoft.EntityFrameworkCore;

namespace ChessResultsCrawler.Services;

public class TournamentService
{
    private readonly AppDbContext _db;

    public TournamentService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<Tournament>> GetAllTournamentsAsync()
    {
        return await _db.Tournaments.OrderByDescending(t => t.CreatedAt).ToListAsync();
    }

    public async Task<Tournament?> GetTournamentAsync(int id)
    {
        return await _db.Tournaments
            .Include(t => t.Rounds)
            .FirstOrDefaultAsync(t => t.Id == id);
    }

    public async Task<Tournament?> GetTournamentByChessResultsIdAsync(string chessResultsId)
    {
        return await _db.Tournaments
            .Include(t => t.Rounds)
            .FirstOrDefaultAsync(t => t.ChessResultsId == chessResultsId);
    }

    public async Task<List<Player>> GetPlayersAsync(int tournamentId, string? team = null, string? sortBy = null)
    {
        var query = _db.Players
            .Include(p => p.Team)
            .Where(p => p.TournamentId == tournamentId);

        if (!string.IsNullOrWhiteSpace(team))
            query = query.Where(p => p.Team != null && p.Team.Name.Contains(team));

        query = sortBy?.ToLowerInvariant() switch
        {
            "elo" => query.OrderByDescending(p => p.Elo),
            "name" => query.OrderBy(p => p.Name),
            "board" => query.OrderBy(p => p.BoardNumber),
            _ => query.OrderBy(p => p.Snr)
        };

        return await query.ToListAsync();
    }

    public async Task<List<Team>> GetTeamsAsync(int tournamentId)
    {
        return await _db.Teams
            .Where(t => t.TournamentId == tournamentId)
            .OrderBy(t => t.Snr)
            .ToListAsync();
    }

    public async Task<Team?> GetTeamAsync(int tournamentId, int snr)
    {
        return await _db.Teams
            .Include(t => t.Players.OrderBy(p => p.BoardNumber))
            .FirstOrDefaultAsync(t => t.TournamentId == tournamentId && t.Snr == snr);
    }

    public async Task<List<TeamPairing>> GetPairingsAsync(int tournamentId, int? round = null)
    {
        var query = _db.TeamPairings
            .Include(tp => tp.HomeTeam)
            .Include(tp => tp.AwayTeam)
            .Include(tp => tp.Round)
            .Where(tp => tp.Round.TournamentId == tournamentId);

        if (round.HasValue)
            query = query.Where(tp => tp.Round.RoundNumber == round.Value);

        return await query.OrderBy(tp => tp.Round.RoundNumber)
            .ThenBy(tp => tp.MatchNumber)
            .ToListAsync();
    }

    public async Task<List<TeamPairing>> GetLatestPairingsAsync(int tournamentId)
    {
        var latestRound = await _db.Rounds
            .Where(r => r.TournamentId == tournamentId)
            .OrderByDescending(r => r.RoundNumber)
            .FirstOrDefaultAsync();

        if (latestRound is null) return [];

        return await _db.TeamPairings
            .Include(tp => tp.HomeTeam)
            .Include(tp => tp.AwayTeam)
            .Include(tp => tp.Round)
            .Where(tp => tp.RoundId == latestRound.Id)
            .OrderBy(tp => tp.MatchNumber)
            .ToListAsync();
    }

    public async Task<List<Pairing>> GetIndividualPairingsAsync(int tournamentId, int? round = null)
    {
        var query = _db.Pairings
            .Include(p => p.WhitePlayer)
            .Include(p => p.BlackPlayer)
            .Include(p => p.Round)
            .Where(p => p.Round.TournamentId == tournamentId);

        if (round.HasValue)
            query = query.Where(p => p.Round.RoundNumber == round.Value);

        return await query.OrderBy(p => p.Round.RoundNumber)
            .ThenBy(p => p.BoardNumber)
            .ToListAsync();
    }

    public async Task<bool> HasTeamPairingsAsync(int tournamentId)
    {
        return await _db.TeamPairings.AnyAsync(tp => tp.Round.TournamentId == tournamentId);
    }

    public async Task<List<Round>> GetRoundsAsync(int tournamentId)
    {
        return await _db.Rounds
            .Where(r => r.TournamentId == tournamentId)
            .OrderBy(r => r.RoundNumber)
            .ToListAsync();
    }
}
