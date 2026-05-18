namespace ChessResultsCrawler.Models;

public class Round
{
    public int Id { get; set; }
    public int TournamentId { get; set; }
    public int RoundNumber { get; set; }
    public bool PairingsPublished { get; set; }
    public bool ResultsPublished { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Tournament Tournament { get; set; } = null!;
    public ICollection<TeamPairing> TeamPairings { get; set; } = [];
    public ICollection<PlayerResult> PlayerResults { get; set; } = [];
}
