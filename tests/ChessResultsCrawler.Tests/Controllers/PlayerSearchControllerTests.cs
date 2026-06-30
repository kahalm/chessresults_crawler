using ChessResultsCrawler.Controllers;
using Microsoft.AspNetCore.Mvc;

namespace ChessResultsCrawler.Tests.Controllers;

/// <summary>
/// Prüft die Eingabe-Validierung der Spielersuche. Bei zu kurzem Nachnamen kehrt der
/// Controller VOR dem CrawlerService-Aufruf zurück → der Service wird nicht gebraucht (null!).
/// </summary>
public class PlayerSearchControllerTests
{
    private static PlayerSearchController Sut() => new(null!);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a")]      // 1 Zeichen
    [InlineData(" a ")]    // getrimmt 1 Zeichen
    public async Task Search_RejectsShortLastName(string lastName)
    {
        var result = await Sut().Search(lastName, null, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("x")]
    public async Task SearchTournaments_RejectsShortLastName(string lastName)
    {
        var result = await Sut().SearchTournaments(lastName, null, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }
}
