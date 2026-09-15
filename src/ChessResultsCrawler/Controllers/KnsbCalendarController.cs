using ChessResultsCrawler.DTOs;
using ChessResultsCrawler.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChessResultsCrawler.Controllers;

/// <summary>
/// Der Terminkalender des niederlaendischen Verbands (KNSB, schaakbond.nl). Zustandslos wie die
/// uebrigen Routen.
///
/// <para>177 kuenftige Eintraege gegen 12 auf chess-results (gemessen 2026-09-09) — aber Enddatum,
/// Ort, Anschrift, Postleitzahl, Koordinaten, Rundenzahl und Teilnehmerzahl fehlen strukturell in
/// dieser Liste; sie stehen (Enddatum/Ort/Anschrift) allenfalls auf der Detailseite, siehe
/// <see cref="KnsbCalendarService"/>.</para>
///
/// <para>Ein Durchgang holt zwei Seiten zu je 100 Eintraegen mit der Wartezeit aus der robots.txt
/// der Quelle dazwischen und dauert damit rund 15 Sekunden.</para>
/// </summary>
[ApiController]
[Route("api/knsb-calendar")]
public class KnsbCalendarController : ControllerBase
{
    private readonly KnsbCalendarService _knsb;

    public KnsbCalendarController(KnsbCalendarService knsb)
    {
        _knsb = knsb;
    }

    /// <summary>Turniere, die am <paramref name="from"/> (Vorgabe: heute) noch laufen oder spaeter beginnen.</summary>
    [HttpGet]
    public async Task<ActionResult<List<KnsbEventResponse>>> Calendar(
        [FromQuery] DateOnly? from = null, CancellationToken ct = default)
    {
        var start = from ?? DateOnly.FromDateTime(DateTime.UtcNow);
        try
        {
            var events = await _knsb.FetchAsync(start, ct);
            return Ok(events.Select(KnsbEventResponse.FromParsed).ToList());
        }
        catch (SourceResponseException ex)
        {
            // 502 statt einer unbehandelten 500: der Crawler funktioniert, die QUELLE hat etwas
            // anderes geliefert. Der Rumpf reist bis in RookHubs Nachtlauf-Log mit.
            return StatusCode(502, new
            {
                source = ex.Source,
                upstreamStatus = ex.StatusCode,
                contentType = ex.ContentType,
                excerpt = ex.Excerpt,
                message = ex.Message,
            });
        }
    }
}
