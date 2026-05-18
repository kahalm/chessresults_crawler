namespace ChessResultsCrawler.Models;

public class TeamPairing
{
    public int Id { get; set; }
    public int RoundId { get; set; }
    public int MatchNumber { get; set; }
    public int HomeTeamId { get; set; }
    public int AwayTeamId { get; set; }
    public decimal? HomeScore { get; set; }
    public decimal? AwayScore { get; set; }

    public Round Round { get; set; } = null!;
    public Team HomeTeam { get; set; } = null!;
    public Team AwayTeam { get; set; } = null!;
}
