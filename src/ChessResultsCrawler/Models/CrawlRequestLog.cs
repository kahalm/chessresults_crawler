using System.ComponentModel.DataAnnotations;

namespace ChessResultsCrawler.Models;

public class CrawlRequestLog
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    [MaxLength(2000)]
    public string Url { get; set; } = string.Empty;
    public int? StatusCode { get; set; }
    public long DurationMs { get; set; }
    public long? ResponseSizeBytes { get; set; }
    public string? ResponseBody { get; set; }
    public bool Success { get; set; }
    [MaxLength(2000)]
    public string? ErrorMessage { get; set; }
    public bool IsRetry { get; set; }
}
