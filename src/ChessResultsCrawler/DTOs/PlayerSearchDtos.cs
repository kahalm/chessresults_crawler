using ChessResultsCrawler.Services;

namespace ChessResultsCrawler.DTOs;

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
