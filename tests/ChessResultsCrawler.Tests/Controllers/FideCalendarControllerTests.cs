using ChessResultsCrawler.Controllers;
using ChessResultsCrawler.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChessResultsCrawler.Tests.Controllers;

/// <summary>
/// Eingabepruefung des FIDE-Endpunkts. Der Wert geht unveraendert in eine fremde Anfrage — ein
/// absurdes Jahr laesst FIDE arbeiten, nicht uns.
/// </summary>
public class FideCalendarControllerTests
{
    private static FideCalendarController CreateController() =>
        new(new FideCalendarService(new HttpClient(new UnusedHandler()),
            NullLogger<FideCalendarService>.Instance));

    [Theory]
    [InlineData(0)]
    [InlineData(1999)]
    [InlineData(2101)]
    [InlineData(-2026)]
    public async Task Year_OutOfRange_ReturnsBadRequest(int year)
    {
        var result = await CreateController().Year(year);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    /// <summary>Keine Zahl = keine Ereignis-Nummer: 404, und es geht KEINE Anfrage hinaus.</summary>
    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Event_InvalidId_ReturnsNotFound(string? id)
    {
        var result = await CreateController().Event(id!);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    /// <summary>
    /// Der ganze Weg vom Fragment (echter Abruf id=17954) bis zur Antwort — die Felder, die
    /// RookHub liest, kommen auch durch die DTO-Abbildung an.
    /// </summary>
    [Fact]
    public async Task Event_RealFragment_MapsEveryFieldIntoTheResponse()
    {
        var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fide-event-17954.html"));
        var controller = new FideCalendarController(new FideCalendarService(
            new HttpClient(new FixedHandler(html)), NullLogger<FideCalendarService>.Instance));

        var result = await controller.Event(" 17954 ");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<ChessResultsCrawler.DTOs.FideEventDetailResponse>(ok.Value);
        Assert.Equal("17954", dto.EventId);
        Assert.Equal("Standard", dto.TimeControl);
        Assert.Equal(9, dto.Rounds);
        Assert.Equal(10, dto.Players);
        Assert.Contains("50012", dto.VenueAddress);
        Assert.Equal("Adla, Diego (ARG); Glavina, Pablo (ESP)", dto.Organizers);
        Assert.Equal("Cajo Esteban, Ruben (ESP)", dto.ChiefArbiter);
    }

    private sealed class FixedHandler(string html) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html"),
            });
    }

    /// <summary>Wird in diesen Tests nie benutzt — ein Aufruf ist ein Fehler, kein Zufall.</summary>
    private sealed class UnusedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("Diese Tests erwarten keinen HTTP-Aufruf.");
    }
}
