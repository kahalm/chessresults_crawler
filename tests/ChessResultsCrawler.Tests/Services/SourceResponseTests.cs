using System.Net;
using System.Text;
using ChessResultsCrawler.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChessResultsCrawler.Tests.Services;

/// <summary>
/// Eine Quelle, die statt JSON eine Sperr- oder Warteseite liefert, ist ein QUELLENFEHLER mit
/// lesbarem Auszug — kein Parser-Absturz. Am 2026-09-14 begann schaakbond.nl, unserem VPN-Ausgang
/// eine JavaScript-Warteseite mit HTTP 200 auszuliefern; im Nachtlauf stand danach nur
/// „'&lt;' is an invalid start of a value".
/// </summary>
public class SourceResponseTests
{
    private const string ChallengePage =
        "<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n  <meta charset=\"utf8\">\n"
        + "  <title>One moment, please...</title>\n  <script>\n      (function(){ /* ... */ })();\n";

    [Theory]
    [InlineData("[{\"id\":1}]")]
    [InlineData("  \n\t{\"code\":\"rest_no_route\"}")]
    public void EnsureJson_AcceptsAJsonBody(string body) =>
        SourceResponse.EnsureJson("X", 200, "application/json", body);

    [Fact]
    public void EnsureJson_RejectsTheChallengePageWithAReadableExcerpt()
    {
        var body = ChallengePage + string.Concat(Enumerable.Repeat("<div class=\"loader\"></div>\n", 40));

        var ex = Assert.Throws<SourceResponseException>(
            () => SourceResponse.EnsureJson("KNSB", 200, "text/html", body));

        Assert.Equal("KNSB", ex.Source);
        Assert.Equal(200, ex.StatusCode);
        Assert.Equal("text/html", ex.ContentType);
        Assert.Contains("One moment, please", ex.Excerpt);
        Assert.True(ex.Excerpt.Length <= SourceResponse.ExcerptLength, ex.Excerpt.Length.ToString());
        Assert.DoesNotContain("\n", ex.Excerpt);
        Assert.Contains("KNSB", ex.Message);
        Assert.Contains("HTTP 200", ex.Message);
    }

    [Fact]
    public void EnsureJson_RejectsAnEmptyBody() =>
        Assert.Throws<SourceResponseException>(() => SourceResponse.EnsureJson("X", 200, null, "   "));

    [Fact]
    public void EnsureJson_DecidesByTheBodyNotByTheHeader()
    {
        // Eine Sperrseite, die sich als JSON ausgibt, bleibt eine Sperrseite.
        Assert.Throws<SourceResponseException>(
            () => SourceResponse.EnsureJson("X", 200, "application/json", ChallengePage));
    }

    [Fact]
    public async Task KnsbFetchAsync_ChallengePageInsteadOfJson_FailsAsASourceErrorNotAsAParserCrash()
    {
        var service = new KnsbCalendarService(
            new HttpClient(new FixedHandler(ChallengePage, "text/html")),
            NullLogger<KnsbCalendarService>.Instance);

        var ex = await Assert.ThrowsAsync<SourceResponseException>(
            () => service.FetchAsync(new DateOnly(2026, 9, 1)));

        Assert.Contains("One moment, please", ex.Excerpt);
    }

    private sealed class FixedHandler(string body, string mediaType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, mediaType),
            });
    }
}
