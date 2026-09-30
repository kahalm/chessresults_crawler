using System.Net;
using System.Text;
using ChessResultsCrawler.Data;
using ChessResultsCrawler.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace ChessResultsCrawler.Tests.Services;

/// <summary>
/// Charakterisierung der sechs ASP.NET-Postbacks (GET → versteckte Felder → POST auf die
/// aufgeloeste Node-URL). Aufgezeichnet wird je oeffentlicher Methode die komplette Anfragefolge
/// — Methode, URL und die Formularfelder in Sendereihenfolge. Die Felder sind je Seite
/// verschieden gewachsen (__EVENTTARGET fehlt bei den Spielersuchen, __LASTFOCUS gibt es nur bei
/// Turnier- und Partiesuche) und bleiben es: chess-results antwortet auf ein unerwartetes Feld
/// im Zweifel mit einem „Laufzeitfehler". Wer den Ablauf umbaut, muss diese Folge byte-gleich
/// halten.
/// </summary>
public class CrawlerServicePostBackTests : IDisposable
{
    private readonly AppDbContext _db;

    public CrawlerServicePostBackTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
    }

    public void Dispose() => _db.Dispose();

    private const string FormPage =
        "<html><body><h2>Formular-Name</h2><form>" +
        "<input type=\"hidden\" name=\"__VIEWSTATE\" value=\"VS/+=1&amp;x\" />" +
        "<input type=\"hidden\" name=\"__VIEWSTATEGENERATOR\" value=\"VSG-1\" />" +
        "<input type=\"hidden\" id=\"__EVENTVALIDATION\" value=\"EV-1\" />" +
        "</form></body></html>";

    private const string ResultPage = "<html><body><h2>Ergebnis-Name</h2></body></html>";

    public static TheoryData<string> PostBacks => new()
    {
        nameof(CrawlerService.FetchTournamentInfoAsync),
        nameof(CrawlerService.SearchTournamentsAsync),
        nameof(CrawlerService.FetchCalendarAsync),
        nameof(CrawlerService.SearchPlayersAsync),
        nameof(CrawlerService.SearchPlayerTournamentsAsync),
        nameof(CrawlerService.SearchGamesPgnByFideAsync),
    };

    [Theory]
    [MemberData(nameof(PostBacks))]
    public async Task PostBack_SendsRecordedRequestSequence(string method)
    {
        var rig = new Rig(_db, HttpStatusCode.OK, ResultPage, "text/html");

        await rig.InvokeAsync(method);

        Assert.Equal(Expected[method], rig.Log);
    }

    [Theory]
    [MemberData(nameof(PostBacks))]
    public async Task PostBack_ErrorStatusOnPost_ThrowsWithStatusAndDisposesResponse(string method)
    {
        var rig = new Rig(_db, HttpStatusCode.InternalServerError, "<html>kaputt</html>", "text/html");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => rig.InvokeAsync(method));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        // Erst Body lesen, dann EnsureSuccess — und die Antwort trotzdem freigeben.
        Assert.True(rig.PostContent!.Disposed);
    }

    [Fact]
    public async Task FetchTournamentInfoAsync_NameFromFormPage_DetailsFromPostBackPage()
    {
        const string detailPage = """
            <html><body><h2>Ergebnis-Name</h2><table>
            <tr><td>Datum</td><td>2026/05/17 bis 2026/05/19</td></tr>
            <tr><td>Time control (Rapid)</td><td>15 min + 10 sec</td></tr>
            </table>Standing after 7 Rounds</body></html>
            """;
        var rig = new Rig(_db, HttpStatusCode.OK, detailPage, "text/html");

        var info = await rig.Service.FetchTournamentInfoAsync("1");

        Assert.Equal("1", info.ChessResultsId);
        Assert.Equal("Formular-Name", info.Name);        // aus dem GET, nicht aus dem Postback
        Assert.Equal("15 min + 10 sec", info.TimeControl);
        Assert.Equal("Rapid", info.TimeControlKind);
        Assert.Equal(7, info.TotalRounds);
    }

    [Theory]
    [InlineData("application/x-chess-pgn", "[Event \"A\"]\n1. e4 *", "[Event \"A\"]\n1. e4 *")]
    [InlineData("text/html", "[Event \"A\"]\n1. e4 *", "")]
    [InlineData("application/octet-stream", "keine Partien", "")]
    public async Task SearchGamesPgnByFideAsync_ReturnsBodyOnlyForPgn(string mediaType, string body, string expected)
    {
        var rig = new Rig(_db, HttpStatusCode.OK, body, mediaType);

        Assert.Equal(expected, await rig.Service.SearchGamesPgnByFideAsync("1503014"));
    }

    // ---------------------------------------------------------------------

    private static string[] Lines(params string[] lines) => lines;

    private static readonly Dictionary<string, string[]> Expected = new()
    {
        [nameof(CrawlerService.FetchTournamentInfoAsync)] = Lines(
            "GET https://chess-results.com/tnr1.aspx?lan=1&art=0&turdet=YES",
            "GET https://s2.chess-results.com/tnr1.aspx?lan=1&art=0&turdet=YES",
            "POST https://s2.chess-results.com/tnr1.aspx?lan=1&art=0&turdet=YES",
            "  __EVENTTARGET=",
            "  __EVENTARGUMENT=",
            "  __VIEWSTATE=VS/+=1&x",
            "  __VIEWSTATEGENERATOR=VSG-1",
            "  __EVENTVALIDATION=EV-1",
            "  cb_alleDetails=Show tournament details"),
        [nameof(CrawlerService.SearchTournamentsAsync)] = Lines(
            "GET https://chess-results.com/TurnierSuche.aspx?lan=1",
            "GET https://s2.chess-results.com/TurnierSuche.aspx?lan=1",
            "POST https://s2.chess-results.com/TurnierSuche.aspx?lan=1",
            "  __EVENTTARGET=",
            "  __EVENTARGUMENT=",
            "  __LASTFOCUS=",
            "  __VIEWSTATE=VS/+=1&x",
            "  __VIEWSTATEGENERATOR=VSG-1",
            "  __EVENTVALIDATION=EV-1",
            "  ctl00$P1$combo_land=AUT",
            "  ctl00$P1$txt_von_tag=01.09.2026",
            "  ctl00$P1$txt_bis_tag=31.12.2026",
            "  ctl00$P1$combo_art=3",
            "  ctl00$P1$combo_bedenkzeit=0",
            "  ctl00$P1$combo_sort=3",
            "  ctl00$P1$combo_anzahl_zeilen=2",
            "  ctl00$P1$txt_bez=",
            "  ctl00$P1$txt_ort=",
            "  ctl00$P1$txt_veranstalter=",
            "  ctl00$P1$txt_leiter=",
            "  ctl00$P1$txt_Schiedsrichter=",
            "  ctl00$P1$txt_Hauptschiedsrichter=",
            "  ctl00$P1$txt_tnr=",
            "  ctl00$P1$txt_eventid=",
            "  ctl00$P1$cb_suchen=Search"),
        [nameof(CrawlerService.FetchCalendarAsync)] = Lines(
            "GET https://chess-results.com/Kalender.aspx?lan=1",
            "GET https://s2.chess-results.com/Kalender.aspx?lan=1",
            "POST https://s2.chess-results.com/Kalender.aspx?lan=1",
            "  __EVENTTARGET=ctl00$P1$combo_landsel$DropDownList1",
            "  __EVENTARGUMENT=",
            "  __VIEWSTATE=VS/+=1&x",
            "  __VIEWSTATEGENERATOR=VSG-1",
            "  __EVENTVALIDATION=EV-1",
            "  ctl00$P1$combo_landsel$DropDownList1=AUT",
            "  ctl00$P1$combo_kat$DropDownList1=0"),
        [nameof(CrawlerService.SearchPlayersAsync)] = Lines(
            "GET https://chess-results.com/SpielerSuche.aspx?lan=0",
            "GET https://s2.chess-results.com/SpielerSuche.aspx?lan=0",
            "POST https://s2.chess-results.com/SpielerSuche.aspx?lan=0",
            "  __VIEWSTATE=VS/+=1&x",
            "  __EVENTVALIDATION=EV-1",
            "  __VIEWSTATEGENERATOR=VSG-1",
            "  ctl00$P1$txt_nachname=Müller",
            "  ctl00$P1$txt_vorname=Max",
            "  ctl00$P1$cb_suchen=Suchen"),
        [nameof(CrawlerService.SearchPlayerTournamentsAsync)] = Lines(
            "GET https://chess-results.com/SpielerSuche.aspx?lan=1",
            "GET https://s2.chess-results.com/SpielerSuche.aspx?lan=1",
            "POST https://s2.chess-results.com/SpielerSuche.aspx?lan=1",
            "  __VIEWSTATE=VS/+=1&x",
            "  __EVENTVALIDATION=EV-1",
            "  __VIEWSTATEGENERATOR=VSG-1",
            "  ctl00$P1$txt_nachname=Müller",
            "  ctl00$P1$txt_vorname=",
            "  ctl00$P1$cb_suchen=Search"),
        [nameof(CrawlerService.SearchGamesPgnByFideAsync)] = Lines(
            "GET https://chess-results.com/PartieSuche.aspx?lan=0",
            "GET https://s2.chess-results.com/PartieSuche.aspx?lan=0",
            "POST https://s2.chess-results.com/PartieSuche.aspx?lan=0",
            "  __EVENTTARGET=",
            "  __EVENTARGUMENT=",
            "  __LASTFOCUS=",
            "  __VIEWSTATE=VS/+=1&x",
            "  __VIEWSTATEGENERATOR=VSG-1",
            "  __EVENTVALIDATION=EV-1",
            "  ctl00$P1$Txt_FideID=1503014",
            "  ctl00$P1$txt_nachname=",
            "  ctl00$P1$txt_vorname=",
            "  ctl00$P1$Txt_NatID=",
            "  ctl00$P1$txt_bez=",
            "  ctl00$P1$txt_dbkey=",
            "  ctl00$P1$txt_rdvon=",
            "  ctl00$P1$txt_rdbis=",
            "  ctl00$P1$txt_von_tag=",
            "  ctl00$P1$txt_bis_tag=",
            "  ctl00$P1$combo_spielerfarbe=-",
            "  ctl00$P1$combo_ergebnis=-",
            "  ctl00$P1$combo_anzahl_zeilen=5",
            "  ctl00$P1$cb_DownLoadPGN=Download als PGN-Datei"),
    };

    private sealed class Rig
    {
        private readonly List<string> _log = [];

        public CrawlerService Service { get; }
        public TrackingContent? PostContent { get; private set; }
        public List<string> Log { get { lock (_log) return [.. _log]; } }

        public Rig(AppDbContext db, HttpStatusCode postStatus, string postBody, string postMediaType)
        {
            var crawl = new HttpClient(new StubHandler(async req =>
            {
                var line = $"{req.Method} {req.RequestUri}";
                var lines = new List<string> { line };
                if (req.Method == HttpMethod.Post && req.Content is not null)
                {
                    var body = await req.Content.ReadAsStringAsync();
                    lines.AddRange(body.Split('&', StringSplitOptions.RemoveEmptyEntries)
                        .Select(pair => pair.Split('=', 2))
                        .Select(p => "  " + Decode(p[0]) + "=" + (p.Length > 1 ? Decode(p[1]) : "")));
                }
                lock (_log) _log.AddRange(lines);

                if (req.Method == HttpMethod.Get && req.RequestUri!.Host == "chess-results.com")
                {
                    var redirect = new HttpResponseMessage(HttpStatusCode.Found) { RequestMessage = req };
                    redirect.Headers.Location = new Uri("https://s2.chess-results.com" + req.RequestUri.PathAndQuery);
                    return redirect;
                }
                if (req.Method == HttpMethod.Get)
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(FormPage, Encoding.UTF8, "text/html"),
                        RequestMessage = req,
                    };

                PostContent = new TrackingContent(postBody, postMediaType);
                return new HttpResponseMessage(postStatus) { Content = PostContent, RequestMessage = req };
            }));

            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Crawler:MinDelayMs"] = "0",
                ["Crawler:RetryDelayMs"] = "0",
                ["Crawler:RotateAfterRequests"] = "1000000",
            }).Build();
            Service = new CrawlerService(crawl, new HtmlParserService(), db,
                Mock.Of<ILogger<CrawlerService>>(), config, TestVpnGate.Unused());
        }

        private static string Decode(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));

        public Task InvokeAsync(string method) => method switch
        {
            nameof(CrawlerService.FetchTournamentInfoAsync) => Service.FetchTournamentInfoAsync("1"),
            nameof(CrawlerService.SearchTournamentsAsync) => Service.SearchTournamentsAsync(
                "AUT", new DateOnly(2026, 9, 1), new DateOnly(2026, 12, 31), maxRows: 500, art: "3"),
            nameof(CrawlerService.FetchCalendarAsync) => Service.FetchCalendarAsync("AUT"),
            nameof(CrawlerService.SearchPlayersAsync) => Service.SearchPlayersAsync("Müller", "Max"),
            nameof(CrawlerService.SearchPlayerTournamentsAsync) =>
                Service.SearchPlayerTournamentsAsync("Müller", null),
            nameof(CrawlerService.SearchGamesPgnByFideAsync) => Service.SearchGamesPgnByFideAsync("1503014"),
            _ => throw new ArgumentOutOfRangeException(nameof(method), method, null),
        };
    }

    /// <summary>Antwort-Inhalt, der mitschreibt, ob er freigegeben wurde.</summary>
    private sealed class TrackingContent : HttpContent
    {
        private readonly byte[] _bytes;
        public bool Disposed { get; private set; }

        public TrackingContent(string body, string mediaType)
        {
            _bytes = Encoding.UTF8.GetBytes(body);
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(_bytes, 0, _bytes.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = _bytes.Length;
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => handler(request);
    }
}
