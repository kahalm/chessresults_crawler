using System.Net;
using System.Text.Json;
using ChessResultsCrawler.Controllers;
using ChessResultsCrawler.Data;
using ChessResultsCrawler.DTOs;
using ChessResultsCrawler.Services;
using ChessResultsCrawler.Tests.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace ChessResultsCrawler.Tests.Controllers;

/// <summary>
/// Die Turnierliste eines Spielers hat zwei Routen — <c>/api/tournament-search/player-history</c> und den Alias
/// <c>/api/players/tournaments</c> (Review W4s S3-008). Vorher kuerzte nur der Alias die Namen auf 100 Zeichen: ein
/// ueberlanger Name ging je Route verschieden in den chess-results-Postback. Hier laufen beide Routen gegen denselben
/// Stub, und verglichen wird, was im Formular ankommt und was zurueckkommt.
/// </summary>
public class PlayerTournamentRoutesTests : IDisposable
{
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    /// <summary>Die Formularwerte jedes POSTs an die Spielersuche, in Sendereihenfolge.</summary>
    private readonly List<Dictionary<string, string>> _posted = [];

    public void Dispose() => _db.Dispose();

    public static TheoryData<string> Routes => ["player-history", "players/tournaments"];

    private Task<ActionResult<List<PlayerTournamentResponse>>> Call(
        string route, string lastName, string? firstName)
    {
        var crawler = CreateCrawler();
        return route switch
        {
            "player-history" => new TournamentSearchController(crawler).PlayerHistory(lastName, firstName),
            "players/tournaments" => new PlayerSearchController(crawler).SearchTournaments(lastName, firstName, default),
            _ => throw new ArgumentOutOfRangeException(nameof(route)),
        };
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task LongNames_AreClippedTo100Characters_OnBothRoutes(string route)
    {
        var lastName = "Oberschmid" + new string('x', 140);
        var firstName = "Patrik" + new string('y', 110);

        var result = await Call(route, lastName, firstName);

        Assert.IsType<OkObjectResult>(result.Result);
        var form = Assert.Single(_posted);
        Assert.Equal(lastName[..PlayerNameQuery.MaxNameLength], form["ctl00$P1$txt_nachname"]);
        Assert.Equal(firstName[..PlayerNameQuery.MaxNameLength], form["ctl00$P1$txt_vorname"]);
    }

    [Theory]
    [InlineData("Oberschmid", "Patrik")]
    [InlineData("  Oberschmid ", " Patrik ")]
    [InlineData("Oberschmid", null)]
    [InlineData("Oberschmid", "")]
    public async Task BothRoutes_SendTheSameFormAndReturnTheSameBody(string lastName, string? firstName)
    {
        var history = await Call("player-history", lastName, firstName);
        var alias = await Call("players/tournaments", lastName, firstName);

        Assert.Equal(2, _posted.Count);
        Assert.Equal(_posted[0]["ctl00$P1$txt_nachname"], _posted[1]["ctl00$P1$txt_nachname"]);
        Assert.Equal(_posted[0]["ctl00$P1$txt_vorname"], _posted[1]["ctl00$P1$txt_vorname"]);
        Assert.Equal("Oberschmid", _posted[0]["ctl00$P1$txt_nachname"]);

        var historyBody = Assert.IsType<List<PlayerTournamentResponse>>(Assert.IsType<OkObjectResult>(history.Result).Value);
        var aliasBody = Assert.IsType<List<PlayerTournamentResponse>>(Assert.IsType<OkObjectResult>(alias.Result).Value);
        Assert.NotEmpty(historyBody);
        Assert.Equal(JsonSerializer.Serialize(historyBody), JsonSerializer.Serialize(aliasBody));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" O ")]
    public async Task TooShortLastName_SameBadRequestOnBothRoutes_WithoutFetching(string lastName)
    {
        var history = await Call("player-history", lastName, null);
        var alias = await Call("players/tournaments", lastName, null);

        var historyBody = JsonSerializer.Serialize(Assert.IsType<BadRequestObjectResult>(history.Result).Value);
        var aliasBody = JsonSerializer.Serialize(Assert.IsType<BadRequestObjectResult>(alias.Result).Value);
        Assert.Equal(aliasBody, historyBody);
        Assert.Contains(PlayerNameQuery.LastNameTooShortMessage, historyBody);
        Assert.Empty(_posted);
    }

    [Theory]
    [InlineData("Oberschmid", "Oberschmid")]
    [InlineData("  Oberschmid  ", "Oberschmid")]
    [InlineData("ab", "ab")]
    public void TryNormalize_TrimsAndAcceptsLastNamesFromTwoCharacters(string input, string expected)
    {
        Assert.True(PlayerNameQuery.TryNormalize(input, null, out var last, out var first));
        Assert.Equal(expected, last);
        Assert.Null(first);
    }

    [Fact]
    public void TryNormalize_ClipsAfterTrimming_AndDropsATrailingSpaceAtTheCut()
    {
        // 100 Leerzeichen vor dem Namen: vorher gekuerzt, dann getrimmt → leerer Nachname ging an chess-results.
        Assert.True(PlayerNameQuery.TryNormalize(new string(' ', 100) + "Oberschmid", "", out var last, out var first));
        Assert.Equal("Oberschmid", last);
        Assert.Equal("", first);

        var cutAtSpace = new string('a', 99) + " b";
        Assert.True(PlayerNameQuery.TryNormalize(cutAtSpace, null, out last, out _));
        Assert.Equal(new string('a', 99), last);

        Assert.True(PlayerNameQuery.TryNormalize(new string('a', 100), null, out last, out _));
        Assert.Equal(100, last.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" a ")]
    public void TryNormalize_RejectsLastNamesShorterThanTwoCharacters(string? input)
    {
        Assert.False(PlayerNameQuery.TryNormalize(input, "Patrik", out _, out _));
    }

    private CrawlerService CreateCrawler()
    {
        const string formPage =
            "<html><body><form>" +
            "<input type=\"hidden\" name=\"__VIEWSTATE\" value=\"VS\" />" +
            "<input type=\"hidden\" name=\"__VIEWSTATEGENERATOR\" value=\"VSG\" />" +
            "<input type=\"hidden\" name=\"__EVENTVALIDATION\" value=\"EV\" />" +
            "</form></body></html>";
        var resultPage = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "player-search.html"));

        var handler = new StubHandler(async req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                var body = await req.Content!.ReadAsStringAsync();
                _posted.Add(QueryHelpers.ParseQuery(body).ToDictionary(kv => kv.Key, kv => kv.Value.ToString()));
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(req.Method == HttpMethod.Post ? resultPage : formPage),
            };
        });

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Crawler:MinDelayMs"] = "0",
            ["Crawler:RetryDelayMs"] = "0",
            ["Crawler:CrawlMaxAttempts"] = "1",
            ["Crawler:CrawlRetryBackoffSeconds"] = "0",
        }).Build();

        var factory = Mock.Of<IHttpClientFactory>(f => f.CreateClient("Gluetun") == new HttpClient());
        return new CrawlerService(new HttpClient(handler), factory, new HtmlParserService(), _db,
            Mock.Of<ILogger<CrawlerService>>(), config, TestVpnGate.Unused());
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await handler(request);
            response.RequestMessage ??= request;
            return response;
        }
    }
}
