using System.Net;
using System.Net.Sockets;
using ChessResultsCrawler.Services;

namespace ChessResultsCrawler.Tests.Services;

public class CrawlerServiceTransientErrorTests
{
    [Fact]
    public void IsTransientConnectionError_ResourceTemporarilyUnavailable_True()
    {
        // Genau der Fehler aus dem Prod-Incident nach Redeploy/VPN-Rotation:
        // HttpRequestException ohne StatusCode (kein HTTP-Status erhalten).
        var ex = new HttpRequestException("Resource temporarily unavailable (chess-results.com:443)");
        Assert.True(CrawlerService.IsTransientConnectionError(ex));
    }

    [Fact]
    public void IsTransientConnectionError_SocketException_True()
    {
        var ex = new HttpRequestException("connection error", new SocketException(11));
        Assert.True(CrawlerService.IsTransientConnectionError(ex));
    }

    [Fact]
    public void IsTransientConnectionError_HttpClientTimeout_True()
    {
        // HttpClient-Timeout wirft TaskCanceledException (nicht durch unseren ct ausgelöst).
        var ex = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout");
        Assert.True(CrawlerService.IsTransientConnectionError(ex));
    }

    [Fact]
    public void IsTransientConnectionError_HttpErrorWithStatusCode_False()
    {
        // 404/500 aus EnsureSuccessStatusCode: HTTP-Status erhalten → echter Serverfehler,
        // NICHT endlos wiederholen.
        var ex = new HttpRequestException("Not Found", null, HttpStatusCode.NotFound);
        Assert.False(CrawlerService.IsTransientConnectionError(ex));
    }

    [Fact]
    public void IsTransientConnectionError_SsrfRejection_False()
    {
        var ex = new InvalidOperationException("Redirect to unexpected domain: https://evil.example");
        Assert.False(CrawlerService.IsTransientConnectionError(ex));
    }

    [Fact]
    public void IsTransientConnectionError_GenericParseError_False()
    {
        var ex = new FormatException("could not parse round number");
        Assert.False(CrawlerService.IsTransientConnectionError(ex));
    }
}
