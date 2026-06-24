using ChessResultsCrawler.Services;

namespace ChessResultsCrawler.Tests.Services;

/// <summary>
/// Sichert die Bye/Spielfrei-Erkennung ab: eine "spielfrei"-Paarung darf KEINEN Warn-Alert
/// auslösen (sonst treibt sie den warn_spike hoch), sondern wird nur informativ geloggt.
/// </summary>
public class CrawlerServiceByeTests
{
    [Theory]
    [InlineData("spielfrei", true)]
    [InlineData("Spielfrei", true)]
    [InlineData("SPIELFREI", true)]
    [InlineData("  spielfrei  ", true)]
    [InlineData("bye", true)]
    [InlineData("Bye", true)]
    [InlineData("freilos", true)]
    [InlineData("Freilos", true)]
    [InlineData("SC Musterstadt", false)]
    [InlineData("Bye United", false)]   // enthält "bye", ist aber ein echter Name
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void IsByeOpponent_DetectsByeMarkers(string? teamName, bool expected)
    {
        Assert.Equal(expected, CrawlerService.IsByeOpponent(teamName));
    }
}
