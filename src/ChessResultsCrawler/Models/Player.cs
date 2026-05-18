namespace ChessResultsCrawler.Models;

public class Player
{
    public int Id { get; set; }
    public int TournamentId { get; set; }
    public int? TeamId { get; set; }
    public required string Name { get; set; }
    public string? Title { get; set; }
    public string? FideId { get; set; }
    public int? Elo { get; set; }
    public string? Country { get; set; }
    public int? BoardNumber { get; set; }
    public int Snr { get; set; }

    public Tournament Tournament { get; set; } = null!;
    public Team? Team { get; set; }
    public ICollection<PlayerResult> Results { get; set; } = [];
}
