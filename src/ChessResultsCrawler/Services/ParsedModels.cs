namespace ChessResultsCrawler.Services;

public class ParsedPlayer
{
    public int Snr { get; set; }
    public string Name { get; set; } = "";
    public string? Title { get; set; }
    public string? FideId { get; set; }
    public int? Elo { get; set; }
    public string? Country { get; set; }
    public string? TeamName { get; set; }
    public int? BoardNumber { get; set; }
}

public class ParsedTeamPairing
{
    public int MatchNumber { get; set; }
    public string HomeTeamName { get; set; } = "";
    public string AwayTeamName { get; set; } = "";
    public decimal? HomeScore { get; set; }
    public decimal? AwayScore { get; set; }
}

public class ParsedPairing
{
    public int BoardNumber { get; set; }
    public string WhiteName { get; set; } = "";
    public string BlackName { get; set; } = "";
    public int WhiteSnr { get; set; }
    public int BlackSnr { get; set; }
    public string? Result { get; set; }
}

public class ParsedTournamentDetails
{
    public string? DateText { get; set; }
    public string? Location { get; set; }

    /// <summary>
    /// Die Bedenkzeit als ROHTEXT, wie chess-results sie fuehrt („90 Min. / 40 Zuege + 30 Min. +
    /// 30 Sekunden ab Zug 1"). Bewusst nicht hier schon in eine Kategorie uebersetzt: der Crawler
    /// gibt weiter, was auf der Seite steht.
    /// </summary>
    public string? TimeControl { get; set; }

    /// <summary>
    /// Die Klasse, die chess-results SELBST nennt — sie steht in Klammern hinter der Beschriftung
    /// („Time control (Standard)", „(Rapid)", „(Blitz)"). Das ist die verlaesslichere Quelle als
    /// jede Ableitung aus dem Freitext; fehlt sie, bleibt nur der Text.
    /// </summary>
    public string? TimeControlKind { get; set; }

    /// <summary>Die Gruppen derselben Veranstaltung (Zeile „Turnierauswahl"); leer ohne eine solche.</summary>
    public List<ParsedTournamentGroup> Groups { get; set; } = [];
}

/// <summary>Eine Gruppe aus der „Turnierauswahl" — Nummer und Bezeichnung, wie chess-results sie zeigt.</summary>
public class ParsedTournamentGroup
{
    /// <summary>chess-results-Nummer; <c>null</c> fuer die eigene (dort ohne Link).</summary>
    public string? ChessResultsId { get; set; }
    public string Label { get; set; } = "";
    public bool IsCurrent { get; set; }
}

/// <summary>
/// Eine Zeile der Spielersuche: die Teilnahme EINES Spielers an EINEM Turnier. Ein Abruf liefert
/// die ganze Historie — vergangene und kuenftige Turniere.
/// </summary>
public class ParsedPlayerTournament
{
    public string TournamentId { get; set; } = "";
    public string TournamentName { get; set; } = "";
    public string? EndDate { get; set; }

    /// <summary>
    /// Startnummer des Spielers in diesem Turnier — steht NUR im Link auf den Namen und ist der
    /// Schluessel zur Spielerkarte (Punkte, Platz, Performance).
    /// </summary>
    public int? Snr { get; set; }

    public string? PlayerName { get; set; }
    /// <summary>chess-results-Ident-Nummer; bei Auslandsturnieren steht dort „0".</summary>
    public string? IdentNumber { get; set; }
    public string? FideId { get; set; }
    public string? Club { get; set; }
    public string? Federation { get; set; }

    /// <summary>Platz; <c>null</c> heisst „noch nicht gespielt" (Spalte enthaelt „-").</summary>
    public int? Rank { get; set; }
    public int? Rounds { get; set; }
    /// <summary>Teilnehmerzahl des Turniers (Spalte „n").</summary>
    public int? PlayerCount { get; set; }
}

/// <summary>Eine Runde mit ihrem Termin, aus dem Rundenplan (art=14).</summary>
/// <summary>
/// Kopfdaten eines Turniers, ohne Import — Termin, Ort, Rundenzahl und Bedenkzeit (Rohtext).
/// </summary>
public class ParsedTournamentInfo
{
    public string ChessResultsId { get; set; } = "";
    public string? Name { get; set; }
    public string? DateText { get; set; }
    public string? Location { get; set; }
    public string? TimeControl { get; set; }
    /// <summary>Die von chess-results genannte Klasse („Standard", „Rapid", „Blitz").</summary>
    public string? TimeControlKind { get; set; }
    public int? TotalRounds { get; set; }
}

public class ParsedRoundDate
{
    public int Number { get; set; }
    public DateOnly Date { get; set; }
    /// <summary>Rohtext der Uhrzeit („14:00 Uhr") — fuer den Kalender zaehlt der Tag.</summary>
    public string? TimeText { get; set; }
}

/// <summary>
/// Punkte, Platz, Performance-Rating und Elo-Aenderung eines Spielers in EINEM Turnier — der
/// „Player info"-Block der Spielerkarte (art=9).
/// </summary>
public class ParsedPlayerCard
{
    /// <summary>
    /// Tatsaechlich gespielte Partien (Zeilen der Partien-Tabelle mit Gegner). <c>null</c>, wenn
    /// die Seite keine solche Tabelle hat. NICHT die Rundenzahl des Turniers: in einer Liga wird
    /// ein Spieler an einem Teil der Termine aufgestellt.
    /// </summary>
    public int? GamesPlayed { get; set; }

    public string? Name { get; set; }
    public string? Federation { get; set; }
    public string? Club { get; set; }
    public string? IdentNumber { get; set; }
    public string? FideId { get; set; }
    public int? StartingRank { get; set; }
    public int? RatingNational { get; set; }
    public int? RatingInternational { get; set; }
    /// <summary>Turnier-Leistung („Performance rating") — die Zahl, um die es hier eigentlich geht.</summary>
    public int? PerformanceRating { get; set; }
    public int? Rank { get; set; }
    public int? YearOfBirth { get; set; }
    /// <summary>Erreichte Punkte; Bruchteile kommen als Kommazahl („1,5").</summary>
    public decimal? Points { get; set; }
    /// <summary>Elo-Aenderung aus diesem Turnier („FIDE rtg +/-").</summary>
    public decimal? RatingChange { get; set; }

    /// <summary>
    /// Gab es schon ein Ergebnis? Ein kuenftiges Turnier liefert den Block ohne Werte — das ist
    /// der Normalfall und kein Fehler, aber es darf nicht als „null Punkte" gespeichert werden.
    /// </summary>
    public bool HasResult { get; set; }
}

public class ParsedPlayerSearchResult
{
    public string Name { get; set; } = "";
    public string? FideId { get; set; }
    public string? ChessResultsId { get; set; }
    public int? Elo { get; set; }
    public string? Country { get; set; }
    public string? Title { get; set; }
}

public class ParsedPlayerResult
{
    public int RoundNumber { get; set; }
    public int BoardNumber { get; set; }
    public int? OpponentSnr { get; set; }
    public string? OpponentName { get; set; }
    public int? OpponentElo { get; set; }
    public string? Points { get; set; }
    public string? Result { get; set; }
}


/// <summary>
/// Ein Eintrag des chess-results-ANKUENDIGUNGSkalenders (<c>Kalender.aspx</c>).
///
/// <para>Bewusst mager: der Kalender fuehrt Termin, Name, Foederation und Verweise — und sonst
/// NICHTS. Kein Ort, keine Bedenkzeit, keine Rundenzahl. Er ist eine Ankuendigung, keine
/// Turnierseite; sein Wert liegt allein im VORLAUF.</para>
/// </summary>
public class ParsedCalendarEntry
{
    public string Name { get; set; } = "";
    public string? Federation { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }

    /// <summary>
    /// Die Nummer des Kalendereintrags aus dem Ausschreibungs-Verweis
    /// (<c>Kalender.aspx?key6=DL&amp;id=10814</c>). Sie ist die einzige stabile Kennung, die der
    /// Kalender hergibt — und sie fehlt bei rund einem Fuenftel der Eintraege.
    /// </summary>
    public string? CalendarId { get; set; }

    /// <summary>Verweis des Veranstalters (eigene Seite). Selten ein chess-results-Link.</summary>
    public string? Url { get; set; }

    /// <summary>
    /// Die chess-results-Turniernummer, WENN der Verweis auf eine Turnierseite zeigt. Nur bei
    /// wenigen Eintraegen (an AUT gemessen: 6 von 146) — die Zuordnung zu bestehenden Eintraegen
    /// muss deshalb ueber Termin und Namen laufen, nicht ueber diese Nummer.
    /// </summary>
    public string? ChessResultsId { get; set; }
}

public class ParsedDirectoryTournament
{
    public string ChessResultsId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Federation { get; set; }
    public string? State { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public string? LocationText { get; set; }
    public string? TimeControlText { get; set; }
    public string? Director { get; set; }
    public string? Organizer { get; set; }
    public string? ChiefArbiter { get; set; }
    public int? Rounds { get; set; }
    public int? PlayerCount { get; set; }
    public string? LastUpdateText { get; set; }
    public TimeSpan? LastUpdateAge { get; set; }
}
