using System.Text.RegularExpressions;

namespace ChessResultsCrawler.Services;

/// <summary>
/// Fassade der chess-results-Parser. Die Arbeit machen die Bereichs-Parser:
/// <see cref="TournamentPagesParser"/> (Startrangliste, Turnierdetails, Runden, Rundenplan,
/// Mannschaftsnamen), <see cref="PairingsParser"/> (Paarungsseiten art=2),
/// <see cref="PlayerPagesParser"/> (Spielersuche, Einzelergebnisse, Spielerkarte) und
/// <see cref="DirectoryParser"/> (Ankuendigungskalender, Turniersuche); das gemeinsame
/// Tabellen-Lesen steckt in <see cref="HtmlTable"/>, die Ergebnistypen in <c>ParsedModels.cs</c>.
/// </summary>
public class HtmlParserService
{
    /// <inheritdoc cref="TournamentPagesParser.ParsePlayerListAsync"/>
    public Task<List<ParsedPlayer>> ParsePlayerListAsync(string html) =>
        TournamentPagesParser.ParsePlayerListAsync(html);

    /// <inheritdoc cref="PairingsParser.ParseTeamPairingsAsync"/>
    public Task<List<ParsedTeamPairing>> ParseTeamPairingsAsync(string html) =>
        PairingsParser.ParseTeamPairingsAsync(html);

    /// <inheritdoc cref="PairingsParser.ParseIndividualPairingsAsync"/>
    public Task<List<ParsedPairing>> ParseIndividualPairingsAsync(string html) =>
        PairingsParser.ParseIndividualPairingsAsync(html);

    /// <inheritdoc cref="PairingsParser.IsTeamPairingsPageAsync"/>
    public Task<bool> IsTeamPairingsPageAsync(string html) =>
        PairingsParser.IsTeamPairingsPageAsync(html);

    /// <inheritdoc cref="PairingsParser.HasPairingsTableAsync"/>
    public Task<bool> HasPairingsTableAsync(string html) =>
        PairingsParser.HasPairingsTableAsync(html);

    /// <inheritdoc cref="TournamentPagesParser.ParseTotalRoundsAsync"/>
    public Task<int?> ParseTotalRoundsAsync(string html) =>
        TournamentPagesParser.ParseTotalRoundsAsync(html);

    /// <inheritdoc cref="TournamentPagesParser.ParseAvailableRoundsAsync"/>
    public Task<List<int>> ParseAvailableRoundsAsync(string html, int? maxRound = null) =>
        TournamentPagesParser.ParseAvailableRoundsAsync(html, maxRound);

    /// <inheritdoc cref="TournamentPagesParser.ParseTournamentNameAsync"/>
    public Task<string?> ParseTournamentNameAsync(string html) =>
        TournamentPagesParser.ParseTournamentNameAsync(html);

    /// <inheritdoc cref="TournamentPagesParser.ParseTournamentDetailsAsync"/>
    public Task<ParsedTournamentDetails> ParseTournamentDetailsAsync(string html) =>
        TournamentPagesParser.ParseTournamentDetailsAsync(html);

    /// <inheritdoc cref="PlayerPagesParser.ParsePlayerSearchAsync"/>
    public Task<List<ParsedPlayerSearchResult>> ParsePlayerSearchAsync(string html) =>
        PlayerPagesParser.ParsePlayerSearchAsync(html);

    /// <inheritdoc cref="PlayerPagesParser.ParsePlayerTournamentsAsync"/>
    public Task<List<ParsedPlayerTournament>> ParsePlayerTournamentsAsync(string html) =>
        PlayerPagesParser.ParsePlayerTournamentsAsync(html);

    /// <inheritdoc cref="PlayerPagesParser.ParsePlayerDetailPageAsync"/>
    public Task<List<ParsedPlayerResult>> ParsePlayerDetailPageAsync(string html) =>
        PlayerPagesParser.ParsePlayerDetailPageAsync(html);

    /// <inheritdoc cref="TournamentPagesParser.ParseRoundPlanAsync"/>
    public Task<List<ParsedRoundDate>> ParseRoundPlanAsync(string html) =>
        TournamentPagesParser.ParseRoundPlanAsync(html);

    /// <inheritdoc cref="PlayerPagesParser.ParsePlayerCardAsync"/>
    public Task<ParsedPlayerCard?> ParsePlayerCardAsync(string html) =>
        PlayerPagesParser.ParsePlayerCardAsync(html);

    /// <inheritdoc cref="TournamentPagesParser.ParseTeamNamesAsync"/>
    public Task<List<string>> ParseTeamNamesAsync(string html) =>
        TournamentPagesParser.ParseTeamNamesAsync(html);

    /// <inheritdoc cref="DirectoryParser.ParseCalendarAsync"/>
    public Task<List<ParsedCalendarEntry>> ParseCalendarAsync(string html) =>
        DirectoryParser.ParseCalendarAsync(html);

    /// <inheritdoc cref="DirectoryParser.ParseTournamentSearchAsync"/>
    public Task<List<ParsedDirectoryTournament>> ParseTournamentSearchAsync(string html) =>
        DirectoryParser.ParseTournamentSearchAsync(html);

    /// <inheritdoc cref="DirectoryParser.ParseSearchDate"/>
    internal static DateOnly? ParseSearchDate(string? text) => DirectoryParser.ParseSearchDate(text);

    /// <inheritdoc cref="DirectoryParser.ParseRelativeAge"/>
    internal static TimeSpan? ParseRelativeAge(string? text) => DirectoryParser.ParseRelativeAge(text);

    /// <summary>
    /// Extracts the SNode (s1/s2/s3) from a redirect URL or page content.
    /// </summary>
    public static string? ExtractSNode(string url)
    {
        var match = Regex.Match(url, @"chess-results\.com/(s\d+)/");
        return match.Success ? match.Groups[1].Value : null;
    }
}
