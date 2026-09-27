namespace ChessResultsCrawler.Models;

/// <summary>
/// Der Verein eines Spielers, wie ihn die chess-results-SPIELERSUCHE nennt — nachgetragen für
/// Turniere, deren Startliste keine Vereinsspalte hat (viele Opens führen nur Name, FIDE-ID, Land,
/// Elo; auch die Spielerkarte nennt dann keinen).
///
/// <para>Am Spieler (FIDE-ID), nicht am Turnier: derselbe Treffer gilt in jedem Turnier, in dem er
/// ohne Verein steht, und kostet so nur EINEN Suchabruf. <see cref="Club"/> = <c>null</c> heißt
/// „gesucht, keiner gefunden" — damit derselbe Spieler nicht bei jedem Knopfdruck neu gesucht wird.</para>
/// </summary>
public class PlayerClub
{
    public int Id { get; set; }
    public required string FideId { get; set; }
    public string? Club { get; set; }
    /// <summary>Aus welchem Turnier der Verein stammt — die Ansicht nennt es („laut …").</summary>
    public string? SourceTournamentId { get; set; }
    public string? SourceTournamentName { get; set; }
    /// <summary>Enddatum jenes Turniers, wie die Suche es liefert (<c>yyyy/MM/dd</c>).</summary>
    public string? SourceEndDate { get; set; }
    public DateTime FetchedAt { get; set; } = DateTime.UtcNow;
}
