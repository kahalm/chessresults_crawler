using ChessResultsCrawler.Services;

namespace ChessResultsCrawler.Tests.Services;

/// <summary>
/// Vertrag der gemeinsamen Tabellen-Hilfen, die alle chess-results-Parser teilen.
/// </summary>
public class HtmlTableTests
{
    [Fact]
    public async Task HeaderMap_CaseInsensitive_FirstDuplicateWins()
    {
        var doc = await HtmlTable.OpenAsync(
            "<table><thead><tr><th> Nr. </th><td>Name</td><th>name</th></tr></thead><tbody><tr><td>1</td></tr></tbody></table>");

        var headers = HtmlTable.HeaderMap(doc.QuerySelector("table")!);

        Assert.Equal(0, headers["nr."]);
        Assert.Equal(1, headers["NAME"]);
        Assert.Equal(2, headers.Count);
    }

    [Fact]
    public async Task FindTableByHeaders_PrefersLeafTableOverWrapper()
    {
        // Die Wrapper-Tabelle "enthaelt" die Kopfnamen ueber TextContent rekursiv mit;
        // gemeint ist die innere Datentabelle (Blatt).
        var doc = await HtmlTable.OpenAsync(
            "<table id='outer'><tr><td><table id='inner'><tr><th>Round</th><th>Date</th></tr><tr><td>1</td><td>x</td></tr></table></td></tr></table>");

        var table = HtmlTable.FindTableByHeaders(doc, ["Round", "Date"]);

        Assert.Equal("inner", table?.Id);
        Assert.Null(HtmlTable.FindTableByHeaders(doc, ["Elo"]));
    }

    [Fact]
    public async Task GetCellValue_TrimsAndMapsBlankOrMissingToNull()
    {
        var doc = await HtmlTable.OpenAsync("<table><tr><td> Anna </td><td>  </td></tr></table>");
        var cells = doc.QuerySelectorAll("td").ToList();
        var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Name"] = 0, ["Elo"] = 1, ["Team"] = 5 };

        Assert.Equal("Anna", HtmlTable.GetCellValue(cells, headers, "name"));
        Assert.Null(HtmlTable.GetCellValue(cells, headers, "Elo"));
        Assert.Null(HtmlTable.GetCellValue(cells, headers, "Team"));
        Assert.Null(HtmlTable.GetCellValue(cells, headers, "Fed"));
    }
}
