using System.Net;
using ChessResultsCrawler.Data;
using ChessResultsCrawler.Models;
using ChessResultsCrawler.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace ChessResultsCrawler.Tests.Services;

/// <summary>
/// Sichert ab, dass auch der Paarungs-Crawl doppelte Teamnamen verträgt: die Name→Team-Map wird
/// über BuildTeamNameMap gebaut. Mit ToDictionary flog hier eine (nicht transiente)
/// ArgumentException, die den ganzen Full-/PairingsOnly-Job als Failed beendete.
/// </summary>
public class CrawlerServiceTeamPairingsTests : IDisposable
{
    private readonly AppDbContext _db;

    public CrawlerServiceTeamPairingsTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            // InMemory kennt keine Transaktionen; der Paarungs-Upsert klammert delete+insert aber
            // bewusst in eine → Warnung ignorieren statt den Produktionscode zu verbiegen.
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _db = new AppDbContext(options);
    }

    public void Dispose() => _db.Dispose();

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _html;
        public StubHandler(string html) => _html = html;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_html),
                RequestMessage = request,
            });
    }

    [Fact]
    public async Task CrawlTeamPairingsAsync_DuplicateTeamNames_DoesNotThrow_AndUsesLowestSnr()
    {
        var tournament = new Tournament { ChessResultsId = "1", Name = "T" };
        _db.Tournaments.Add(tournament);
        await _db.SaveChangesAsync();

        // Zwei gleichnamige Teams (Altbestand/Dublette in den Quelldaten) + ein eindeutiges.
        _db.Teams.AddRange(
            new Team { TournamentId = tournament.Id, Snr = 3, Name = "Team A" },
            new Team { TournamentId = tournament.Id, Snr = 1, Name = "Team A" },
            new Team { TournamentId = tournament.Id, Snr = 2, Name = "Team B" });
        await _db.SaveChangesAsync();

        var html = @"<html><body><table class='CRs1'>
            <tr><th>Nr.</th><th>Home</th><th>Away</th><th>Erg.</th></tr>
            <tr><td>1</td><td>Team A</td><td>Team B</td><td>3:1</td></tr>
            </table></body></html>";

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gluetun__ApiUrl"] = "http://localhost:8000",
            ["Crawler:RetryDelayMs"] = "0",
            ["Crawler:MinDelayMs"] = "0",
        }).Build();
        var factory = Mock.Of<IHttpClientFactory>(f => f.CreateClient("Gluetun") == new HttpClient());
        var svc = new CrawlerService(new HttpClient(new StubHandler(html)), factory, new HtmlParserService(),
            _db, Mock.Of<ILogger<CrawlerService>>(), config);

        await svc.CrawlTeamPairingsAsync(tournament, "https://chess-results.com/tnr1.aspx?lan=0",
            new List<int> { 1 }, CancellationToken.None);

        var pairing = Assert.Single(_db.TeamPairings.ToList());
        var homeTeam = _db.Teams.First(t => t.Id == pairing.HomeTeamId);
        Assert.Equal("Team A", homeTeam.Name);
        Assert.Equal(1, homeTeam.Snr);   // deterministisch die kleinste Snr
        Assert.Equal(3m, pairing.HomeScore);
    }
}
