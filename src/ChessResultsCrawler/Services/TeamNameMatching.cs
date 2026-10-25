using ChessResultsCrawler.Models;

namespace ChessResultsCrawler.Services;

/// <summary>Teamnamen der Paarungsseite dem Bestand zuordnen: tolerante Name→Team-Map, Fussnoten-Marker,
/// Spielfrei-Erkennung. Reine Funktionen, oeffentlich ueber <see cref="CrawlerService"/>.</summary>
internal static class TeamNameMatching
{
    /// <summary>
    /// Baut die Name→Team-Map für das Upsert tolerant gegen doppelte Teamnamen: bei einer
    /// Dublette gewinnt das Team mit der kleinsten <see cref="Team.Snr"/> (stabil, deterministisch).
    /// Verhindert die <c>ToDictionary</c>-Exception bei doppelten Namen, die sonst den
    /// gesamten Spieler-Crawl scheitern lässt.
    /// </summary>
    public static Dictionary<string, Team> BuildTeamNameMap(IEnumerable<Team> teams)
    {
        var map = new Dictionary<string, Team>();
        foreach (var team in teams.OrderBy(t => t.Snr))
            map.TryAdd(team.Name, team);
        return map;
    }

    /// <summary>
    /// Fußnoten-Marker am Ende eines Teamnamens: chess-results hängt auf der Paarungsseite z. B.
    /// „ASVOE VHS Poechlarn 2 *)" an, im Teamverzeichnis steht derselbe Name ohne Marker.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex FootnoteMarker =
        new(@"\s*\*+\)?\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Teamname ohne angehängten Fußnoten-Marker (<c>*</c>, <c>*)</c>, <c>**)</c>).</summary>
    public static string StripFootnoteMarker(string name) => FootnoteMarker.Replace(name, "").TrimEnd();

    /// <summary>
    /// Das Team zu einem Namen von der Paarungsseite: erst exakt, dann ohne Fußnoten-Marker.
    /// Exakt zuerst, damit ein Team, das WIRKLICH so heißt, nie auf ein anderes umgebogen wird.
    /// </summary>
    public static Team? FindTeam(IReadOnlyDictionary<string, Team> teams, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        if (teams.TryGetValue(name, out var team))
            return team;
        var stripped = StripFootnoteMarker(name);
        return stripped != name && teams.TryGetValue(stripped, out team) ? team : null;
    }

    // „nicht ausgelost": so zeigt chess-results ein Team ohne Gegner in dieser Runde — fachlich
    // dasselbe wie spielfrei. Ohne den Eintrag erzeugte ein ungerades Teamfeld bei JEDEM Crawl
    // eine „Team not found"-Warnung (25.09.: 231 von 693 in einer Woche).
    private static readonly string[] ByeMarkers = { "spielfrei", "bye", "freilos", "nicht ausgelost" };

    /// <summary>
    /// Erkennt einen "Spielfrei"/Bye/Freilos-Gegner (case-insensitive, getrimmt). Solche Einträge
    /// sind kein echtes Team und kein Fehler — sie dürfen keinen Warn-Alert auslösen.
    /// </summary>
    public static bool IsByeOpponent(string? teamName)
    {
        if (string.IsNullOrWhiteSpace(teamName))
            return false;
        var normalized = teamName.Trim();
        foreach (var marker in ByeMarkers)
            if (normalized.Equals(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
