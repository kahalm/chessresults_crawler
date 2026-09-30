using System.Net;
using ChessResultsCrawler.Data;
using ChessResultsCrawler.Models;
using ChessResultsCrawler.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ChessResultsCrawler.Tests.Services;

/// <summary>
/// Vereine nachtragen für Turniere, deren Startliste keine Vereinsspalte hat (Anlass: kroatische
/// Opens wie tnr1457876 — nur Name, FIDE-ID, Land, Elo; auch die Spielerkarte nennt keinen).
/// </summary>
public class ClubServiceTests : IDisposable
{
    private readonly AppDbContext _db;

    public ClubServiceTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);
    }

    public void Dispose() => _db.Dispose();

    private async Task<Tournament> TournamentAsync(string crId, string name, string? date)
    {
        var t = new Tournament { ChessResultsId = crId, Name = name, DateText = date };
        _db.Tournaments.Add(t);
        await _db.SaveChangesAsync();
        return t;
    }

    private async Task<Player> PlayerAsync(Tournament t, int snr, string name, string? fide, string? team = null, int? board = null)
    {
        Team? teamEntity = null;
        if (team is not null)
        {
            teamEntity = new Team { TournamentId = t.Id, Snr = snr, Name = team };
            _db.Teams.Add(teamEntity);
        }
        var p = new Player { TournamentId = t.Id, Snr = snr, Name = name, FideId = fide, Team = teamEntity, BoardNumber = board };
        _db.Players.Add(p);
        await _db.SaveChangesAsync();
        return p;
    }

    private ClubService Service(CrawlerService? crawler = null) =>
        new(_db, crawler!, NullLogger<ClubService>.Instance);

    /// <summary>
    /// Automatisch: derselbe Spieler (FIDE-ID) in einem anderen EINZELturnier mit Verein, das
    /// jüngste zuerst. Die Mannschaft aus einem Mannschaftsturnier (dort das Land) zählt nicht.
    /// </summary>
    [Fact]
    public async Task Resolve_TakesTheLatestIndividualTournament_NotATeamEvent()
    {
        var open = await TournamentAsync("1457876", "Maškovića Han", "20.07.2026");
        var martinovic = await PlayerAsync(open, 1, "Martinovic, Sasa", "14509792");
        await PlayerAsync(open, 2, "Mit Verein, Max", "1", team: "SK Eigen");

        var older = await TournamentAsync("100", "Split Open 2023", "01.08.2023 - 08.08.2023");
        await PlayerAsync(older, 5, "Martinovic, Sasa", "14509792", team: "ŠK Alt");
        var newer = await TournamentAsync("200", "Zagreb Open 2025", "2025/03/02");
        await PlayerAsync(newer, 3, "Martinovic, Sasa", "14509792", team: "ŠK Zagreb");
        var olympiad = await TournamentAsync("300", "Olympiad 2026", "06.10.2026");
        await PlayerAsync(olympiad, 9, "Martinovic, Sasa", "14509792", team: "Croatia", board: 3);

        var players = await _db.Players.Include(p => p.Team).Where(p => p.TournamentId == open.Id).ToListAsync();
        var clubs = await Service().ResolveAsync(open.Id, players);

        var club = Assert.Single(clubs).Value;
        Assert.Equal("ŠK Zagreb", club.Club);
        Assert.Equal("Zagreb Open 2025", club.SourceTournamentName);
        Assert.True(clubs.ContainsKey(martinovic.Id));
    }

    /// <summary>Das Ergebnis der Spielersuche geht vor — es kennt alle Turniere auf chess-results, nicht nur unsere.</summary>
    [Fact]
    public async Task Resolve_PrefersTheSearchResult()
    {
        var open = await TournamentAsync("1457876", "Maškovića Han", "20.07.2026");
        await PlayerAsync(open, 1, "Martinovic, Sasa", "14509792");
        var other = await TournamentAsync("200", "Zagreb Open 2025", "2025/03/02");
        await PlayerAsync(other, 3, "Martinovic, Sasa", "14509792", team: "ŠK Zagreb");
        _db.PlayerClubs.Add(new PlayerClub { FideId = "14509792", Club = "ŠK Solin", SourceTournamentName = "Solin Open 2026" });
        await _db.SaveChangesAsync();

        var players = await _db.Players.Include(p => p.Team).Where(p => p.TournamentId == open.Id).ToListAsync();
        var club = Assert.Single(await Service().ResolveAsync(open.Id, players)).Value;

        Assert.Equal("ŠK Solin", club.Club);
        Assert.Equal("Solin Open 2026", club.SourceTournamentName);
    }

    /// <summary>Ohne FIDE-ID wäre jeder Namensvetter ein Kandidat — dann lieber kein Verein.</summary>
    [Fact]
    public async Task Resolve_WithoutFideId_GuessesNothing()
    {
        var open = await TournamentAsync("1", "Open", null);
        await PlayerAsync(open, 1, "Huber, Karl", null);
        var other = await TournamentAsync("2", "Anderes", null);
        await PlayerAsync(other, 1, "Huber, Karl", null, team: "SK Irgendwo");

        var players = await _db.Players.Include(p => p.Team).Where(p => p.TournamentId == open.Id).ToListAsync();
        Assert.Empty(await Service().ResolveAsync(open.Id, players));
    }

    [Theory]
    [InlineData("Martinovic, Sasa", "Martinovic", "Sasa")]
    [InlineData("Kozul Zdenko", "Kozul", "Zdenko")]
    [InlineData("  van  der Berg,  Jan ", "van der Berg", "Jan")]
    [InlineData("Carlsen", "Carlsen", null)]
    public void SplitName_LastNameFirst(string name, string last, string? first)
    {
        Assert.Equal((last, first), ClubService.SplitName(name));
    }

    /// <summary>Nur Zeilen DESSELBEN Spielers (FIDE-ID), mit Verein, nicht das eigene Turnier; das jüngste gewinnt.</summary>
    [Fact]
    public void PickClub_LatestOwnRowWithAClub()
    {
        var rows = new List<ParsedPlayerTournament>
        {
            new() { TournamentId = "1", TournamentName = "Alt", EndDate = "2019/05/01", FideId = "14509792", Club = "ŠK Alt" },
            new() { TournamentId = "2", TournamentName = "Namensvetter", EndDate = "2026/09/01", FideId = "999", Club = "Falsch" },
            new() { TournamentId = "3", TournamentName = "Ohne", EndDate = "2026/08/01", FideId = "14509792", Club = "" },
            new() { TournamentId = "1457876", TournamentName = "Dieses", EndDate = "2026/07/20", FideId = "14509792", Club = "Eigen" },
            new() { TournamentId = "4", TournamentName = "Neu", EndDate = "02.03.2025", FideId = "14509792", Club = "ŠK Zagreb" },
        };

        var hit = ClubService.PickClub(rows, "14509792", "1457876");

        Assert.Equal("ŠK Zagreb", hit?.Club);
    }

    [Theory]
    [InlineData("27.09.2026", 2026, 9, 27)]
    [InlineData("01.08.2023 - 08.08.2023", 2023, 8, 8)]
    [InlineData("2025/03/02", 2025, 3, 2)]
    public void LastDate_ReadsTheEndOfTheRange(string text, int y, int m, int d)
    {
        Assert.Equal(new DateOnly(y, m, d), ClubService.LastDate(text));
    }

    // ----- Auf Knopfdruck: die Spielersuche ------------------------------------

    private sealed class SearchHandler(string resultHtml) : HttpMessageHandler
    {
        public int Posts { get; private set; }
        /// <summary>Nach jedem Such-POST, mit der laufenden Nummer.</summary>
        public Action<int>? OnPost { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post)
            {
                Posts++;
                OnPost?.Invoke(Posts);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.Method == HttpMethod.Get ? "<html><form></form></html>" : resultHtml),
                RequestMessage = request,
            });
        }
    }

    private CrawlerService Crawler(HttpMessageHandler handler)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gluetun:ApiUrl"] = "http://localhost:8000",
            ["Crawler:RetryDelayMs"] = "0",
            ["Crawler:MinDelayMs"] = "0",
            ["Crawler:CrawlMaxAttempts"] = "1",
            ["Crawler:RotateAfterRequests"] = "1000000",
        }).Build();
        return new CrawlerService(new HttpClient(handler), new HtmlParserService(),
            _db, Mock.Of<ILogger<CrawlerService>>(), config, TestVpnGate.Unused());
    }

    private const string SearchResult = """
        <html><body><table class="CRs2">
        <tr class="CRg1b"><th>Name</th><th>ID</th><th>FideID</th><th>Club/City</th><th>FED</th><th>Tournament</th><th>End date</th><th>Rk.</th><th>Rd.</th><th>n</th></tr>
        <tr class="CRg2"><td><a href="tnr900.aspx?lan=1&amp;art=9&amp;snr=4">Martinovic, Sasa</a></td><td>0</td><td>14509792</td><td>ŠK Solin</td><td>CRO</td><td><a href="tnr900.aspx?lan=1">Solin Open 2026</a></td><td>2026/05/10</td><td>1</td><td>9</td><td>80</td></tr>
        </table></body></html>
        """;

    /// <summary>
    /// Der Knopf sucht jeden Spieler ohne Verein EINMAL, merkt sich den Treffer (auch „keiner") und
    /// sucht ihn beim nächsten Druck nicht wieder.
    /// </summary>
    [Fact]
    public async Task Fill_SearchesEachPlayerOnce_AndRemembersTheResult()
    {
        var open = await TournamentAsync("1457876", "Maškovića Han", "20.07.2026");
        await PlayerAsync(open, 1, "Martinovic, Sasa", "14509792");
        await PlayerAsync(open, 2, "Unbekannt, Udo", "555");
        await PlayerAsync(open, 3, "Hat Verein, Hans", "777", team: "SK Eigen");
        var handler = new SearchHandler(SearchResult);

        Assert.True(ClubService.TryBegin("1457876"));
        var found = await Service(Crawler(handler)).FillAsync("1457876");

        Assert.Equal(1, found);
        Assert.Equal(2, handler.Posts);
        Assert.Equal("ŠK Solin", (await _db.PlayerClubs.SingleAsync(c => c.FideId == "14509792")).Club);
        Assert.Null((await _db.PlayerClubs.SingleAsync(c => c.FideId == "555")).Club);

        var (pending, running) = await Service().StatusAsync(open);
        Assert.Equal((0, false), (pending, running));
        Assert.True(ClubService.TryBegin("1457876"));
        await Service(Crawler(handler)).FillAsync("1457876");
        Assert.Equal(2, handler.Posts);
    }

    // ----- Häppchen: der Lauf teilt sich die Schlange mit den Crawls (S3-011) ---

    /// <summary>Ein Provider wie der Worker ihn je Auftrag baut: löst den ClubService für das nächste Häppchen auf.</summary>
    private ServiceProvider Provider(CrawlerService crawler, IBackgroundTaskQueue queue)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_db);
        services.AddSingleton(crawler);
        services.AddSingleton(queue);
        services.AddSingleton<ILogger<ClubService>>(NullLogger<ClubService>.Instance);
        services.AddTransient<ClubService>();
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Ein großer Lauf belegt den einzigen Worker nicht am Stück: ein Auftrag sucht ein Häppchen und
    /// stellt den Rest hinten an. Ein Crawl, der währenddessen kommt (etwa die neue Runde vom
    /// Rundenmonitor), ist vor dem Rest dran; der Lauf bleibt bis zum letzten Häppchen belegt.
    /// </summary>
    [Fact]
    public async Task Fill_WithQueue_SearchesOneChunkPerJob_SoACrawlQueuedMeanwhileGoesNext()
    {
        const string crId = "990231";
        var open = await TournamentAsync(crId, "Großes Open", "20.07.2026");
        for (var i = 1; i <= 25; i++)
            await PlayerAsync(open, i, $"Spieler{i:00}, Max", (990000 + i).ToString());
        var queue = new BackgroundTaskQueue(capacity: 10);
        var crawlRan = false;
        var handler = new SearchHandler(SearchResult)
        {
            OnPost = n =>
            {
                if (n == 1) queue.TryEnqueue((_, _) => { crawlRan = true; return Task.CompletedTask; });
            },
        };
        var crawler = Crawler(handler);
        using var provider = Provider(crawler, queue);

        Assert.True(ClubService.TryBegin(crId));
        await provider.GetRequiredService<ClubService>().FillAsync(crId);

        Assert.Equal(ClubService.LookupsPerChunk, handler.Posts);
        Assert.Equal((15, true), await Service().StatusAsync(open));

        var next = await queue.DequeueAsync(CancellationToken.None);
        await next(provider, CancellationToken.None);
        Assert.True(crawlRan, "Der Crawl muss vor dem Rest des Vereinslaufs drankommen.");
        Assert.Equal(ClubService.LookupsPerChunk, handler.Posts);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var jobs = 0;
        while ((await Service().StatusAsync(open)).Running)
        {
            var item = await queue.DequeueAsync(timeout.Token);
            await item(provider, CancellationToken.None);
            jobs++;
        }

        Assert.Equal(2, jobs);   // 10 + 5
        Assert.Equal(25, handler.Posts);
        Assert.Equal((0, false), await Service().StatusAsync(open));
        Assert.True(ClubService.TryBegin(crId));   // freigegeben
        ClubService.End(crId);
    }

    /// <summary>Ist die Schlange voll, geht der Lauf im selben Auftrag weiter und gibt sich am Ende frei.</summary>
    [Fact]
    public async Task Fill_WhenQueueIsFull_FinishesInTheSameJob()
    {
        const string crId = "990232";
        var open = await TournamentAsync(crId, "Open", "20.07.2026");
        for (var i = 1; i <= 15; i++)
            await PlayerAsync(open, i, $"Spieler{i:00}, Max", (990100 + i).ToString());
        var queue = new BackgroundTaskQueue(capacity: 1);
        Assert.True(queue.TryEnqueue((_, _) => Task.CompletedTask));
        var handler = new SearchHandler(SearchResult);
        var crawler = Crawler(handler);

        Assert.True(ClubService.TryBegin(crId));
        await new ClubService(_db, crawler, NullLogger<ClubService>.Instance, queue).FillAsync(crId);

        Assert.Equal(15, handler.Posts);
        Assert.Equal((0, false), await Service().StatusAsync(open));
    }
}
