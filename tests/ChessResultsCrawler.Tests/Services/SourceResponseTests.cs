using System.Net;
using System.Text;
using System.Text.Json;
using ChessResultsCrawler.Controllers;
using ChessResultsCrawler.Middleware;
using ChessResultsCrawler.Services;
using Microsoft.AspNetCore.Http;
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

    // ----- alle JSON-Quellen, nicht nur KNSB --------------------------------

    private static readonly DateOnly From = new(2026, 9, 1);

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    public static TheoryData<string> JsonSources() => new() { "chess.hu", "chess.sk", "ECF", "FRSah", "KNSB" };

    /// <summary>Der Abruf der Quelle (Dienst direkt) gegen einen Stub, der immer dasselbe antwortet.</summary>
    private static Func<Task<int>> Fetch(string source, string body, string mediaType, DateOnly? from = null)
    {
        var http = new HttpClient(new FixedHandler(body, mediaType));
        var f = from ?? From;
        return source switch
        {
            "chess.hu" => async () => (await new ChessHuCalendarService(http, NullLogger<ChessHuCalendarService>.Instance).FetchAsync(f)).Count,
            "chess.sk" => async () => (await new ChessSkCalendarService(http, NullLogger<ChessSkCalendarService>.Instance).FetchAsync(f, details: false)).Count,
            "ECF" => async () => (await new EcfCalendarService(http, NullLogger<EcfCalendarService>.Instance).FetchAsync(f)).Count,
            "FRSah" => async () => (await new FrsahCalendarService(http, NullLogger<FrsahCalendarService>.Instance).FetchAsync(f)).Count,
            "KNSB" => async () => (await new KnsbCalendarService(http, NullLogger<KnsbCalendarService>.Instance).FetchAsync(f)).Count,
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
        };
    }

    /// <summary>Der Endpunkt der Quelle (Controller), so wie ihn die Pipeline aufruft.</summary>
    private static RequestDelegate Endpoint(string source, string body, string mediaType)
    {
        var http = new HttpClient(new FixedHandler(body, mediaType));
        return source switch
        {
            "chess.hu" => async _ => await new ChessHuCalendarController(new ChessHuCalendarService(http, NullLogger<ChessHuCalendarService>.Instance)).Calendar(From),
            "chess.sk" => async _ => await new ChessSkCalendarController(new ChessSkCalendarService(http, NullLogger<ChessSkCalendarService>.Instance)).Calendar(From, details: false),
            "ECF" => async _ => await new EcfCalendarController(new EcfCalendarService(http, NullLogger<EcfCalendarService>.Instance)).Calendar(From),
            "FRSah" => async _ => await new FrsahCalendarController(new FrsahCalendarService(http, NullLogger<FrsahCalendarService>.Instance)).Calendar(From),
            "KNSB" => async _ => await new KnsbCalendarController(new KnsbCalendarService(http, NullLogger<KnsbCalendarService>.Instance)).Calendar(From),
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
        };
    }

    /// <summary>
    /// Jede JSON-Quelle meldet eine Sperrseite mit HTTP 200 als Quellenfehler mit Auszug — vorher
    /// tat das nur KNSB, chess.hu/chess.sk/ECF/FRSah starben im JSON-Parser.
    /// </summary>
    [Theory]
    [MemberData(nameof(JsonSources))]
    public async Task FetchAsync_ChallengePageInsteadOfJson_FailsAsASourceErrorForEveryJsonSource(string source)
    {
        var ex = await Assert.ThrowsAsync<SourceResponseException>(Fetch(source, ChallengePage, "text/html"));

        Assert.Equal(source, ex.Source);
        Assert.Equal(200, ex.StatusCode);
        Assert.Equal("text/html", ex.ContentType);
        Assert.Contains("One moment, please", ex.Excerpt);
    }

    /// <summary>Die Vorpruefung laesst eine echte JSON-Antwort unveraendert durch.</summary>
    [Theory]
    [InlineData("chess.hu", "chess-hu-calendar.json", "2000-01-01", true)]
    [InlineData("chess.sk", "chess-sk-tournaments.json", "2000-01-01", true)]
    [InlineData("FRSah", "frsah-events.json", "2000-01-01", true)]
    // ECF holt bei Treffern die Spielstaetten mit 10 s Pause nach — hier ohne Treffer.
    [InlineData("ECF", "ecf-events.json", "2100-01-01", false)]
    public async Task FetchAsync_RealJson_PassesThePreCheck(string source, string fixture, string from, bool expectEvents)
    {
        var count = await Fetch(source, Fixture(fixture), "application/json", DateOnly.Parse(from))();

        Assert.Equal(expectEvents, count > 0);
    }

    /// <summary>
    /// Am Endpunkt: die <see cref="UpstreamErrorMiddleware"/> macht aus der Sperrseite JEDER
    /// JSON-Quelle eine 502 mit Auszug (vorher: unbehandelte 500, nur KNSB hatte einen eigenen catch).
    /// </summary>
    [Theory]
    [MemberData(nameof(JsonSources))]
    public async Task Endpoint_ChallengePageInsteadOfJson_Answers502WithExcerpt(string source)
    {
        var middleware = new UpstreamErrorMiddleware(Endpoint(source, ChallengePage, "text/html"),
            NullLogger<UpstreamErrorMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var json = JsonDocument.Parse(await new StreamReader(context.Response.Body).ReadToEndAsync());
        var root = json.RootElement;
        Assert.Equal(source, root.GetProperty("source").GetString());
        Assert.Equal(200, root.GetProperty("upstreamStatus").GetInt32());
        Assert.Equal("text/html", root.GetProperty("contentType").GetString());
        Assert.Contains("One moment, please", root.GetProperty("excerpt").GetString());
        Assert.Contains(source, root.GetProperty("message").GetString());
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
