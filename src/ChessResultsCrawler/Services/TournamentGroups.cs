using System.Text.Json;
using ChessResultsCrawler.DTOs;

namespace ChessResultsCrawler.Services;

/// <summary>
/// Speicherform der „Turnierauswahl" (<see cref="Models.Tournament.GroupsJson"/>): eine Liste aus
/// Nummer und Bezeichnung, die eigene Gruppe mit IHRER Nummer statt ohne. So kann die Antwort jeder
/// Gruppe ansagen, welche davon sie selbst ist, ohne die Seite noch einmal zu lesen.
/// </summary>
public static class TournamentGroups
{
    private sealed record Stored(string Id, string Label);

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// <c>null</c>, wenn es keine Auswahl gibt. Laenger als die Spalte wird es nicht: mehr als eine
    /// Handvoll Gruppen hat keine Veranstaltung, und ein abgeschnittenes JSON waere unlesbar.
    /// </summary>
    public static string? Serialize(IReadOnlyList<ParsedTournamentGroup> groups, string ownChessResultsId)
    {
        if (groups.Count < 2) return null;
        var stored = groups
            .Select(g => new Stored(g.IsCurrent ? ownChessResultsId : g.ChessResultsId ?? "", g.Label))
            .Where(g => g.Id.Length > 0)
            .ToList();
        if (stored.Count < 2) return null;

        var json = JsonSerializer.Serialize(stored, Options);
        return json.Length <= 4000 ? json : null;
    }

    /// <summary>Kaputtes oder fehlendes JSON → keine Gruppen (die Turnierseite zeigt dann keine Leiste).</summary>
    public static List<TournamentGroupResponse> Deserialize(string? json, string ownChessResultsId)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return (JsonSerializer.Deserialize<List<Stored>>(json, Options) ?? [])
                .Select(g => new TournamentGroupResponse
                {
                    ChessResultsId = g.Id,
                    Label = g.Label,
                    Current = g.Id == ownChessResultsId,
                })
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
