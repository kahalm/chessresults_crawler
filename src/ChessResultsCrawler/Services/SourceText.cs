using System.Text.RegularExpressions;

namespace ChessResultsCrawler.Services;

/// <summary>
/// Textbausteine, die jeder Parser fremder Seiten braucht. Eingebunden per
/// <c>using static ChessResultsCrawler.Services.SourceText;</c>.
/// </summary>
internal static class SourceText
{
    /// <summary>
    /// Jeden Leerraum-Lauf (auch Zeilenumbruch, Tab, geschuetztes Leerzeichen) zu EINEM Leerzeichen
    /// zusammenziehen und aussen abschneiden; <c>null</c> wird zu <c>""</c>.
    /// </summary>
    internal static string Collapse(string? text) =>
        Regex.Replace(text ?? "", @"\s+", " ").Trim();
}
