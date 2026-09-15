using ChessResultsCrawler.Services;

namespace ChessResultsCrawler.Tests.Services;

/// <summary>
/// Vier Regionen der schachbund-Uebersichtsseite haben keinen Feed (gemessen 2026-09-15, beide
/// Adressvarianten 404). Sie werden nicht mehr abgefragt — jede andere Region meldet einen
/// fehlenden Feed aber weiter als Warnung, denn dort ist ein 404 ein echter Fehler.
/// </summary>
public class SchachbundFeedlessRegionTests
{
    [Theory]
    [InlineData("schach960")]
    [InlineData("problemschach")]
    [InlineData("blindenschachbund")]
    [InlineData("fernschachbund")]
    public void HasFeed_IsFalseForTheFourMeasuredFeedlessRegions(string region) =>
        Assert.False(SchachbundCalendarService.HasFeed(region));

    [Theory]
    [InlineData("bayern")]
    [InlineData("nordrhein-westfalen")]
    [InlineData("welt")]
    public void HasFeed_StaysTrueForEveryOtherRegion(string region) =>
        Assert.True(SchachbundCalendarService.HasFeed(region));
}
