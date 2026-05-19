using ChessResultsCrawler.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace ChessResultsCrawler.Tests.Middleware;

public class ApiKeyMiddlewareTests
{
    private static IConfiguration BuildConfig(string? apiKey)
    {
        var dict = new Dictionary<string, string?>();
        if (apiKey is not null)
            dict["API_KEY"] = apiKey;
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static (ApiKeyMiddleware middleware, DefaultHttpContext context, bool[] called) Create(
        string? configApiKey, string path = "/api/tournaments")
    {
        var called = new[] { false };
        RequestDelegate next = _ => { called[0] = true; return Task.CompletedTask; };
        var config = BuildConfig(configApiKey);
        var middleware = new ApiKeyMiddleware(next, config);
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return (middleware, context, called);
    }

    [Fact]
    public async Task NoApiKeyConfigured_PassesThrough()
    {
        var (middleware, context, called) = Create(null);

        await middleware.InvokeAsync(context);

        Assert.True(called[0]);
    }

    [Fact]
    public async Task EmptyApiKeyConfigured_PassesThrough()
    {
        var (middleware, context, called) = Create("");

        await middleware.InvokeAsync(context);

        Assert.True(called[0]);
    }

    [Fact]
    public async Task ValidApiKey_PassesThrough()
    {
        var (middleware, context, called) = Create("secret-key");
        context.Request.Headers["X-Api-Key"] = "secret-key";

        await middleware.InvokeAsync(context);

        Assert.True(called[0]);
        Assert.Equal(200, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvalidApiKey_Returns401()
    {
        var (middleware, context, called) = Create("secret-key");
        context.Request.Headers["X-Api-Key"] = "wrong-key";

        await middleware.InvokeAsync(context);

        Assert.False(called[0]);
        Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public async Task MissingApiKey_Returns401()
    {
        var (middleware, context, called) = Create("secret-key");

        await middleware.InvokeAsync(context);

        Assert.False(called[0]);
        Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public async Task HealthEndpoint_SkipsAuth()
    {
        var (middleware, context, called) = Create("secret-key", "/api/health");

        await middleware.InvokeAsync(context);

        Assert.True(called[0]);
    }

    [Fact]
    public async Task SwaggerEndpoint_SkipsAuth()
    {
        var (middleware, context, called) = Create("secret-key", "/swagger/index.html");

        await middleware.InvokeAsync(context);

        Assert.True(called[0]);
    }

    [Fact]
    public async Task HealthIpEndpoint_SkipsAuth()
    {
        var (middleware, context, called) = Create("secret-key", "/api/health/ip");

        await middleware.InvokeAsync(context);

        Assert.True(called[0]);
    }
}
