namespace ChessResultsCrawler.Models;

public class Pairing
{
    public int Id { get; set; }
    public int RoundId { get; set; }
    public int BoardNumber { get; set; }
    public int? WhitePlayerId { get; set; }
    public int? BlackPlayerId { get; set; }
    public string? Result { get; set; } // "1-0", "0-1", "½-½", "1-0F", etc.

    public Round Round { get; set; } = null!;
    public Player? WhitePlayer { get; set; }
    public Player? BlackPlayer { get; set; }
}
