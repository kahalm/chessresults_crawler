using ChessResultsCrawler.Middleware;
using Microsoft.AspNetCore.Http;
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
}
