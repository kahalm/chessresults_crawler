using System.Reflection;
using ChessResultsCrawler.Services;

namespace ChessResultsCrawler.Tests.Services;

/// <summary>
/// Sichert den SSRF-Host-Guard ab, der nach (auto-gefolgten) Redirects greift.
/// </summary>
public class CrawlerServiceSsrfTests
{
    private static void Invoke(string url)
    {
        var m = typeof(CrawlerService).GetMethod("EnsureChessResultsHost",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("EnsureChessResultsHost nicht gefunden");
        try
        {
            m.Invoke(null, new object?[] { url });
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }
    }

    [Theory]
    [InlineData("https://chess-results.com/Tnr.aspx")]
    [InlineData("https://s1.chess-results.com/Tnr.aspx")]
    [InlineData("https://www.chess-results.com/x")]
    public void EnsureChessResultsHost_AllowsChessResultsHosts(string url)
    {
        Invoke(url); // darf nicht werfen
    }

    [Theory]
    [InlineData("https://evilchess-results.com/x")]
    [InlineData("https://chess-results.com.attacker.tld/x")]
    [InlineData("https://attacker.com/x")]
    public void EnsureChessResultsHost_RejectsForeignHosts(string url)
    {
        Assert.Throws<InvalidOperationException>(() => Invoke(url));
    }
}
