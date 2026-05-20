using ChessResultsCrawler.Services;

namespace ChessResultsCrawler.Tests.Services;

public class PlayerSearchParserTests
{
    private readonly HtmlParserService _parser = new();

    #region ParsePlayerSearchAsync

    [Fact]
    public async Task ParsePlayerSearchAsync_ValidHtml_ReturnsPlayers()
    {
        var html = BuildPlayerSearchHtml([
            ("GM", "Huber, Johann", "1234567", "2450", "AUT", "98765"),
            ("IM", "Huber, Maria", "7654321", "2280", "GER", "54321"),
        ]);

        var results = await _parser.ParsePlayerSearchAsync(html);

        Assert.Equal(2, results.Count);
        Assert.Equal("Huber, Johann", results[0].Name);
        Assert.Equal("GM", results[0].Title);
        Assert.Equal("1234567", results[0].FideId);
        Assert.Equal(2450, results[0].Elo);
        Assert.Equal("AUT", results[0].Country);
        Assert.Equal("98765", results[0].ChessResultsId);

        Assert.Equal("Huber, Maria", results[1].Name);
        Assert.Equal("IM", results[1].Title);
        Assert.Equal("7654321", results[1].FideId);
        Assert.Equal(2280, results[1].Elo);
        Assert.Equal("GER", results[1].Country);
        Assert.Equal("54321", results[1].ChessResultsId);
    }

    [Fact]
    public async Task ParsePlayerSearchAsync_EmptyTable_ReturnsEmptyList()
    {
        var html = @"<html><body><table class='CRs1'>
            <tr><th>Name</th><th>Title</th><th>FideID</th><th>Rtg</th><th>FED</th><th>Ident-Number</th></tr>
            </table></body></html>";

        var results = await _parser.ParsePlayerSearchAsync(html);

        Assert.Empty(results);
    }

    [Fact]
    public async Task ParsePlayerSearchAsync_MissingColumns_HandlesGracefully()
    {
        // Only Name and FED columns, no FideID or Elo
        var html = @"<html><body><table class='CRs1'>
            <tr><th>Name</th><th>FED</th></tr>
            <tr><td>Mueller, Hans</td><td>GER</td></tr>
            </table></body></html>";

        var results = await _parser.ParsePlayerSearchAsync(html);

        Assert.Single(results);
        Assert.Equal("Mueller, Hans", results[0].Name);
        Assert.Equal("GER", results[0].Country);
        Assert.Null(results[0].FideId);
        Assert.Null(results[0].Elo);
        Assert.Null(results[0].ChessResultsId);
    }

    [Fact]
    public async Task ParsePlayerSearchAsync_EmptyNameRows_AreSkipped()
    {
        var html = @"<html><body><table class='CRs1'>
            <tr><th>Name</th><th>FED</th></tr>
            <tr><td>  </td><td>GER</td></tr>
            <tr><td>Valid, Player</td><td>AUT</td></tr>
            </table></body></html>";

        var results = await _parser.ParsePlayerSearchAsync(html);

        Assert.Single(results);
        Assert.Equal("Valid, Player", results[0].Name);
    }

    [Fact]
    public async Task ParsePlayerSearchAsync_NoTable_ReturnsEmptyList()
    {
        var html = "<html><body><p>No results found</p></body></html>";

        var results = await _parser.ParsePlayerSearchAsync(html);

        Assert.Empty(results);
    }

    [Fact]
    public async Task ParsePlayerSearchAsync_GermanHeaders_ParsesCorrectly()
    {
        var html = @"<html><body><table class='CRs2'>
            <tr><th>Name</th><th>Typ</th><th>Fide-ID</th><th>Elo</th><th>Land</th><th>Ident-Nummer</th></tr>
            <tr><td>Schmidt, Fritz</td><td>FM</td><td>9999999</td><td>2350</td><td>GER</td><td>11111</td></tr>
            </table></body></html>";

        var results = await _parser.ParsePlayerSearchAsync(html);

        Assert.Single(results);
        Assert.Equal("Schmidt, Fritz", results[0].Name);
        Assert.Equal("FM", results[0].Title);
        Assert.Equal("9999999", results[0].FideId);
        Assert.Equal(2350, results[0].Elo);
        Assert.Equal("GER", results[0].Country);
        Assert.Equal("11111", results[0].ChessResultsId);
    }

    #endregion

    private static string BuildPlayerSearchHtml(
        (string title, string name, string fideId, string elo, string country, string identNumber)[] rows)
    {
        var rowsHtml = string.Join("\n", rows.Select(r =>
            $"<tr><td>{r.name}</td><td>{r.title}</td><td>{r.fideId}</td><td>{r.elo}</td><td>{r.country}</td><td>{r.identNumber}</td></tr>"));

        return $@"<html><body><table class='CRs1'>
            <tr><th>Name</th><th>Title</th><th>FideID</th><th>Rtg</th><th>FED</th><th>Ident-Number</th></tr>
            {rowsHtml}
            </table></body></html>";
    }
}
