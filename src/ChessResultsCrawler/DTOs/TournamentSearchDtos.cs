using ChessResultsCrawler.Services;

namespace ChessResultsCrawler.DTOs;

/// <summary>
/// Eine Zeile der chess-results-Turniersuche. Bewusst flach und ohne Persistenz: der Crawler
/// speichert das Verzeichnis nicht, er reicht es an RookHub durch (siehe TournamentSearchController).
/// </summary>
public class DirectoryTournamentResponse
{
    public string ChessResultsId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Federation { get; set; }
    public string? State { get; set; }
    /// <summary>ISO-Datum (yyyy-MM-dd) oder null, wenn die Zelle leer/unlesbar war.</summary>
    public string? StartDate { get; set; }
    public string? EndDate { get; set; }
    public string? Location { get; set; }
    public string? TimeControl { get; set; }
    public string? Director { get; set; }
    public string? Organizer { get; set; }
    public string? ChiefArbiter { get; set; }
    public int? Rounds { get; set; }
    public int? PlayerCount { get; set; }
    /// <summary>Rohtext der Spalte, z.B. "3 Hours 36 Min." - fuer Diagnose behalten.</summary>
    public string? LastUpdateText { get; set; }
    /// <summary>
    /// Aus dem relativen Alter gebildeter Zeitpunkt. Naeherung: chess-results rundet die Angabe
    /// grob (Minutenaufloesung erst unterhalb einer Stunde), also nie auf Gleichheit vergleichen.
    /// </summary>
    public DateTime? LastUpdatedApproxUtc { get; set; }

    public static DirectoryTournamentResponse FromParsed(ParsedDirectoryTournament p, DateTime nowUtc) => new()
    {
        ChessResultsId = p.ChessResultsId,
        Name = p.Name,
        Federation = p.Federation,
        State = p.State,
        StartDate = p.StartDate?.ToString("yyyy-MM-dd"),
        EndDate = p.EndDate?.ToString("yyyy-MM-dd"),
        Location = p.LocationText,
        TimeControl = p.TimeControlText,
        Director = p.Director,
        Organizer = p.Organizer,
        ChiefArbiter = p.ChiefArbiter,
        Rounds = p.Rounds,
        PlayerCount = p.PlayerCount,
        LastUpdateText = p.LastUpdateText,
        LastUpdatedApproxUtc = p.LastUpdateAge is { } age ? nowUtc - age : null,
    };
}
