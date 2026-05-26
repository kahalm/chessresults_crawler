using System.ComponentModel.DataAnnotations;
using ChessResultsCrawler.Models;

namespace ChessResultsCrawler.DTOs;

public class CrawlRequest
{
    [Required, MaxLength(20)]
    public required string ChessResultsId { get; set; }

    [Required, MaxLength(20)]
    public string JobType { get; set; } = "Full";
}

public class PlayerDetailCrawlRequest
{
    [Required, MaxLength(20)]
    public required string ChessResultsId { get; set; }

    [Required]
    public required List<int> PlayerSnrs { get; set; }
}

public class CrawlJobResponse
{
    public int Id { get; set; }
    public string ChessResultsId { get; set; } = "";
    public string JobType { get; set; } = "";
    public string Status { get; set; } = "";
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public static CrawlJobResponse FromEntity(CrawlJob job) => new()
    {
        Id = job.Id,
        ChessResultsId = job.ChessResultsId,
        JobType = job.JobType.ToString(),
        Status = job.Status.ToString(),
        ErrorMessage = job.ErrorMessage,
        CreatedAt = job.CreatedAt,
        StartedAt = job.StartedAt,
        CompletedAt = job.CompletedAt
    };
}
