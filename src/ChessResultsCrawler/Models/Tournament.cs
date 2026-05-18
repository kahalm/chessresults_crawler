namespace ChessResultsCrawler.Models;

public class Tournament
{
    public int Id { get; set; }
    public required string ChessResultsId { get; set; }
    public required string Name { get; set; }
    public int TotalRounds { get; set; }
    public string? BaseUrl { get; set; }
    public string? SNode { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Team> Teams { get; set; } = [];
    public ICollection<Round> Rounds { get; set; } = [];
    public ICollection<Player> Players { get; set; } = [];
    public ICollection<CrawlJob> CrawlJobs { get; set; } = [];
}
