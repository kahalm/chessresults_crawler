namespace ChessResultsCrawler.Models;

public class Team
{
    public int Id { get; set; }
    public int TournamentId { get; set; }
    public int Snr { get; set; }
    public required string Name { get; set; }

    public Tournament Tournament { get; set; } = null!;
    public ICollection<Player> Players { get; set; } = [];
    public ICollection<TeamPairing> HomePairings { get; set; } = [];
    public ICollection<TeamPairing> AwayPairings { get; set; } = [];
}
