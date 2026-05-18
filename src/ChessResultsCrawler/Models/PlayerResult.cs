namespace ChessResultsCrawler.Models;

public class PlayerResult
{
    public int Id { get; set; }
    public int RoundId { get; set; }
    public int PlayerId { get; set; }
    public int BoardNumber { get; set; }
    public string? Result { get; set; }

    public Round Round { get; set; } = null!;
    public Player Player { get; set; } = null!;
}
