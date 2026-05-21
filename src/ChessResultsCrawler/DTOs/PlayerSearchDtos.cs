using ChessResultsCrawler.Services;

namespace ChessResultsCrawler.DTOs;

public class PlayerTournamentResponse
{
    public string TournamentId { get; set; } = "";
    public string TournamentName { get; set; } = "";
    public string? EndDate { get; set; }

    public static PlayerTournamentResponse FromParsed(ParsedPlayerTournament p) => new()
    {
        TournamentId = p.TournamentId,
        TournamentName = p.TournamentName,
        EndDate = p.EndDate
    };
}

public class PlayerSearchResponse
{
    public string Name { get; set; } = "";
    public string? FideId { get; set; }
    public string? ChessResultsId { get; set; }
    public int? Elo { get; set; }
    public string? Country { get; set; }
    public string? Title { get; set; }

    public static PlayerSearchResponse FromParsed(ParsedPlayerSearchResult p) => new()
    {
        Name = p.Name,
        FideId = p.FideId,
        ChessResultsId = p.ChessResultsId,
        Elo = p.Elo,
        Country = p.Country,
        Title = p.Title
    };
}
