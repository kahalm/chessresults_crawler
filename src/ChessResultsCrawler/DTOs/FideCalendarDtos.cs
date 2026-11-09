using ChessResultsCrawler.Services;

namespace ChessResultsCrawler.DTOs;

/// <summary>
/// Ein Ereignis des FIDE-Kalenders. Flach und ohne Persistenz — der Crawler reicht es durch.
/// </summary>
public class FideEventResponse
{
    /// <summary>FIDE-Ereignis-Nummer; dort der Schluessel (<c>calendar.php?id=</c>).</summary>
    public string EventId { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>ISO-Datum (yyyy-MM-dd).</summary>
    public string StartDate { get; set; } = "";
    public string EndDate { get; set; } = "";
    /// <summary>Stadt, wie FIDE sie schreibt. <c>null</c> bei Online-Ereignissen.</summary>
    public string? City { get; set; }
    /// <summary>Dreibuchstabiges Laenderkuerzel; „ONL" = online.</summary>
    public string? Country { get; set; }

    public static FideEventResponse FromParsed(ParsedFideEvent e) => new()
    {
        EventId = e.EventId,
        Name = e.Name,
        StartDate = e.StartDate.ToString("yyyy-MM-dd"),
        EndDate = e.EndDate.ToString("yyyy-MM-dd"),
        City = e.City,
        Country = e.Country,
    };
}

/// <summary>
/// Die DETAILangaben eines FIDE-Ereignisses. Alles ausser der Nummer ist optional — die
/// Jahresansicht kennt diese Felder gar nicht, und selbst im Detail fehlen sie oft (die
/// 46. Schacholympiade nennt weder Runden- noch Teilnehmerzahl).
/// </summary>
public class FideEventDetailResponse
{
    public string EventId { get; set; } = "";

    /// <summary>„Over-the-Board Tournament", „Online", „Hybrid", „Meeting".</summary>
    public string? EventType { get; set; }

    /// <summary>FIDEs eigene Klasse: „Standard", „Rapid", „Blitz".</summary>
    public string? TimeControl { get; set; }

    /// <summary>Die ausgeschriebene Bedenkzeit.</summary>
    public string? TimeControlText { get; set; }

    /// <summary>
    /// Turniersystem („Round-Robin", „Swiss-System", „Other") — NICHT Einzel gegen Mannschaft.
    /// Die Schacholympiade steht hier auf „Other".
    /// </summary>
    public string? System { get; set; }

    public int? Rounds { get; set; }
    public int? Players { get; set; }
    public string? Country { get; set; }
    public string? City { get; set; }

    /// <summary>Anschrift des Spielorts — traegt oft eine Postleitzahl.</summary>
    public string? VenueAddress { get; set; }

    public string? Website { get; set; }

    /// <summary>Veranstalter („Name (FED)"), mit „; " verbunden.</summary>
    public string? Organizers { get; set; }

    /// <summary>Hauptschiedsrichter („Name (FED)") — nur, wenn FIDE die Rolle „Chief Arbiter" nennt.</summary>
    public string? ChiefArbiter { get; set; }

    public static FideEventDetailResponse FromParsed(ParsedFideEventDetail d) => new()
    {
        EventId = d.EventId,
        EventType = d.EventType,
        TimeControl = d.TimeControl,
        TimeControlText = d.TimeControlText,
        System = d.System,
        Rounds = d.Rounds,
        Players = d.Players,
        Country = d.Country,
        City = d.City,
        VenueAddress = d.VenueAddress,
        Website = d.Website,
        Organizers = d.Organizers,
        ChiefArbiter = d.ChiefArbiter,
    };
}
