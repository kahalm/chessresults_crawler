using ChessResultsCrawler.Services;

namespace ChessResultsCrawler.Tests.Services;

public class HtmlParserServiceExtendedTests
{
    private readonly HtmlParserService _parser = new();

    #region ParseIndividualPairingsAsync

    [Fact]
    public async Task ParseIndividualPairingsAsync_ParsesPairings()
    {
        var html = @"<html><body><table class='CRs1'>
            <tr><th>Br.</th><th>Nr</th><th>Ti.</th><th>Name</th><th>Elo</th><th>Pts</th><th>Result</th><th>Pts</th><th>Ti.</th><th>Name</th><th>Elo</th><th>Nr</th></tr>
            <tr><td>1</td><td>5</td><td>GM</td><td>Carlsen, Magnus</td><td>2830</td><td>2</td><td>1-0</td><td>1</td><td>GM</td><td>Caruana, Fabiano</td><td>2786</td><td>3</td></tr>
            <tr><td>2</td><td>1</td><td>IM</td><td>Doe, John</td><td>2450</td><td>1.5</td><td>½-½</td><td>1.5</td><td>FM</td><td>Smith, Jane</td><td>2350</td><td>7</td></tr>
            </table></body></html>";

        var pairings = await _parser.ParseIndividualPairingsAsync(html);

        Assert.Equal(2, pairings.Count);
        Assert.Equal(1, pairings[0].BoardNumber);
        Assert.Equal("Carlsen, Magnus", pairings[0].WhiteName);
        Assert.Equal("Caruana, Fabiano", pairings[0].BlackName);
        Assert.Equal(5, pairings[0].WhiteSnr);
        Assert.Equal(3, pairings[0].BlackSnr);
        Assert.Equal("1-0", pairings[0].Result);
        Assert.Equal(2, pairings[1].BoardNumber);
        Assert.Equal("½-½", pairings[1].Result);
    }

    [Fact]
    public async Task ParseIndividualPairingsAsync_EmptyHtml_ReturnsEmpty()
    {
        var html = "<html><body></body></html>";

        var pairings = await _parser.ParseIndividualPairingsAsync(html);

        Assert.Empty(pairings);
    }

    #endregion

    #region IsTeamPairingsPageAsync

    [Fact]
    public async Task IsTeamPairingsPageAsync_TeamHeaders_ReturnsTrue()
    {
        var html = @"<html><body><h2>Teamauslosung</h2>
            <table class='CRs1'>
            <tr><th>Nr.</th><th>Home</th><th>Away</th><th>Erg.</th></tr>
            </table></body></html>";

        var result = await _parser.IsTeamPairingsPageAsync(html);

        Assert.True(result);
    }

    [Fact]
    public async Task IsTeamPairingsPageAsync_IndividualHeaders_ReturnsFalse()
    {
        var html = @"<html><body><h2>Paarungen</h2>
            <table class='CRs1'>
            <tr><th>Br.</th><th>Nr</th><th>Name</th><th>Result</th><th>Name</th><th>Nr</th></tr>
            </table></body></html>";

        var result = await _parser.IsTeamPairingsPageAsync(html);

        Assert.False(result);
    }

    [Fact]
    public async Task IsTeamPairingsPageAsync_EmptyHtml_ReturnsFalse()
    {
        var html = "<html><body></body></html>";

        var result = await _parser.IsTeamPairingsPageAsync(html);

        Assert.False(result);
    }

    #endregion
}
