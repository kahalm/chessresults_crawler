using ChessResultsCrawler.DTOs;
using ChessResultsCrawler.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChessResultsCrawler.Controllers;

/// <summary>
/// Die EINE Eingabepruefung der Spielersuchen per Name und die EINE Abfrage der Turnierliste eines Spielers.
///
/// <para>Die Turnierliste hat zwei Routen: <c>GET /api/tournament-search/player-history</c> (die fachlich
/// dokumentierte, von RookHubs TournamentHistoryService genutzt) und <c>GET /api/players/tournaments</c> (Alias,
/// von RookHubs AutoSubscriptionService genutzt). Bis W4s S3-008 kuerzte nur der Alias die Namen auf 100 Zeichen —
/// derselbe Name ging je Route verschieden in den chess-results-Postback. Beide rufen jetzt
/// <see cref="TournamentsAsync"/>; der Alias bleibt, bis RookHub umgestellt ist.</para>
/// </summary>
internal static class PlayerNameQuery
{
    /// <summary>Laengster Vor-/Nachname, der an chess-results geht — laengere werden gekuerzt, nicht abgewiesen.</summary>
    internal const int MaxNameLength = 100;

    internal const string LastNameTooShortMessage = "lastName must be at least 2 characters.";

    /// <summary>
    /// Trimmt beide Namen und kuerzt sie auf <see cref="MaxNameLength"/> Zeichen; <c>false</c>, wenn vom Nachnamen
    /// weniger als 2 Zeichen bleiben. Ein fehlender Vorname bleibt <c>null</c>, ein leerer bleibt leer.
    /// </summary>
    internal static bool TryNormalize(string? lastName, string? firstName, out string last, out string? first)
    {
        last = Clip(lastName ?? "");
        first = firstName is null ? null : Clip(firstName);
        return last.Length >= 2;
    }

    /// <summary>Die Turnierliste eines Spielers — gemeinsamer Rumpf beider Routen (Pruefung, Abruf, Abbildung).</summary>
    internal static async Task<ActionResult<List<PlayerTournamentResponse>>> TournamentsAsync(
        CrawlerService crawler, string? lastName, string? firstName, CancellationToken ct)
    {
        if (!TryNormalize(lastName, firstName, out var last, out var first))
            return new BadRequestObjectResult(new { message = LastNameTooShortMessage });

        var results = await crawler.SearchPlayerTournamentsAsync(last, first, ct);
        return new OkObjectResult(results.Select(PlayerTournamentResponse.FromParsed).ToList());
    }

    private static string Clip(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length > MaxNameLength ? trimmed[..MaxNameLength].TrimEnd() : trimmed;
    }
}
