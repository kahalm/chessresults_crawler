namespace ChessResultsCrawler.Models;

public enum CrawlJobType
{
    Full,
    PlayersOnly,
    PairingsOnly,
    CheckNewRounds
}

public enum CrawlJobStatus
{
    Queued,
    Running,
    Completed,
    Failed
}

public class CrawlJob
{
    public int Id { get; set; }
    public int? TournamentId { get; set; }
    public required string ChessResultsId { get; set; }
    public CrawlJobType JobType { get; set; }
    public CrawlJobStatus Status { get; set; } = CrawlJobStatus.Queued;
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public Tournament? Tournament { get; set; }
}
