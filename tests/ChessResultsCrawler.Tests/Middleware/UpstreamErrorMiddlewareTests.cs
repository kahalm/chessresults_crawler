using ChessResultsCrawler.Middleware;
using ChessResultsCrawler.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChessResultsCrawler.Tests.Middleware;

public class UpstreamErrorMiddlewareTests
{
    private static (UpstreamErrorMiddleware middleware, DefaultHttpContext context) Create(
        RequestDelegate next, bool clientAborted = false)
    {
        var middleware = new UpstreamErrorMiddleware(next, NullLogger<UpstreamErrorMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/players/tournaments";
        context.Response.Body = new MemoryStream();
        if (clientAborted)
            context.RequestAborted = new CancellationToken(canceled: true);
        return (middleware, context);
    }

    [Fact]
    public async Task Invoke_NoException_PassesThroughUnchanged()
    {
        var (mw, ctx) = Create(c => { c.Response.StatusCode = 200; return Task.CompletedTask; });

        await mw.InvokeAsync(ctx);

        Assert.Equal(200, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Invoke_HttpClientTimeout_MapsTo504()
    {
        // HttpClient.Timeout wirft TaskCanceledException OHNE dass RequestAborted gesetzt ist.
        var (mw, ctx) = Create(_ => throw new TaskCanceledException("timeout"));

        await mw.InvokeAsync(ctx);

        Assert.Equal(StatusCodes.Status504GatewayTimeout, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Invoke_UpstreamUnreachable_MapsTo502()
    {
        var (mw, ctx) = Create(_ => throw new HttpRequestException("connection refused"));

        await mw.InvokeAsync(ctx);

        Assert.Equal(StatusCodes.Status502BadGateway, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Invoke_ClientAborted_MapsTo499()
    {
        var (mw, ctx) = Create(_ => throw new OperationCanceledException(), clientAborted: true);

        await mw.InvokeAsync(ctx);

        Assert.Equal(499, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Invoke_RateLimiterSaturated_MapsTo503()
    {
        // CrawlerService.RateLimitAsync wirft TimeoutException, wenn das Ticket nicht binnen 60s kommt.
        var (mw, ctx) = Create(_ => throw new TimeoutException("Rate limiter acquisition timed out after 60 seconds."));

        await mw.InvokeAsync(ctx);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Invoke_GenericException_PropagatesAsRealError()
    {
        // Vertrag: ECHTE interne Fehler (kein Upstream/Gateway) duerfen NICHT verschluckt/umgemappt
        // werden, sondern muessen als 500 hochblubbern (Kestrel). Sonst maskiert die Middleware Bugs.
        var (mw, ctx) = Create(_ => throw new InvalidOperationException("real bug"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => mw.InvokeAsync(ctx));
    }

    [Fact]
    public async Task Invoke_Timeout_WritesJsonMessageBody()
    {
        var (mw, ctx) = Create(_ => throw new TaskCanceledException("timeout"));

        await mw.InvokeAsync(ctx);

        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        Assert.Contains("Upstream request timed out", body);
        Assert.Equal("application/json", ctx.Response.ContentType?.Split(';')[0]);
    }

    /// <summary>
    /// Eine Quelle lieferte statt JSON eine Sperrseite → 502 mit Auszug. Die Antwort ist BYTE-GLEICH
    /// mit der, die bisher nur der KNSB-Controller selbst schrieb (<c>StatusCode(502, new { … })</c>
    /// durch MVC) — RookHubs Nachtlauf sieht fuer KNSB nichts Neues, die anderen Quellen bekommen
    /// dieselbe Form.
    /// </summary>
    [Fact]
    public async Task Invoke_SourceResponseException_Writes502ByteIdenticalToTheFormerKnsbControllerAnswer()
    {
        var ex = new SourceResponseException("KNSB", 200, "text/html",
            "<!DOCTYPE html> <html lang=\"en\"> <title>One moment, please...</title> <script>&amp; ä</script>");
        var (mw, ctx) = Create(_ => throw ex);

        await mw.InvokeAsync(ctx);

        Assert.Equal(StatusCodes.Status502BadGateway, ctx.Response.StatusCode);
        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();

        var (expectedBody, expectedContentType) = await FormerKnsbControllerAnswerAsync(ex);
        Assert.Equal(expectedBody, body);
        Assert.Equal(expectedContentType, ctx.Response.ContentType);
        Assert.Contains("One moment, please", body);
    }

    /// <summary>So antwortete <c>KnsbCalendarController</c> bis zur Zentralisierung.</summary>
    private static async Task<(string Body, string? ContentType)> FormerKnsbControllerAnswerAsync(SourceResponseException ex)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers();
        await using var provider = services.BuildServiceProvider();

        var http = new DefaultHttpContext { RequestServices = provider };
        http.Response.Body = new MemoryStream();
        var result = new ObjectResult(new
        {
            source = ex.Source,
            upstreamStatus = ex.StatusCode,
            contentType = ex.ContentType,
            excerpt = ex.Excerpt,
            message = ex.Message,
        })
        { StatusCode = 502 };
        await result.ExecuteResultAsync(new ActionContext(http, new RouteData(), new ActionDescriptor()));

        Assert.Equal(502, http.Response.StatusCode);
        http.Response.Body.Seek(0, SeekOrigin.Begin);
        return (await new StreamReader(http.Response.Body).ReadToEndAsync(), http.Response.ContentType);
    }
}
