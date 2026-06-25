using ChessResultsCrawler.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace ChessResultsCrawler.Tests.Middleware;

public class ApiKeyMiddlewareTests
{
    private sealed class FakeEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IConfiguration BuildConfig(string? apiKey)
    {
        var dict = new Dictionary<string, string?>();
        if (apiKey is not null)
            dict["API_KEY"] = apiKey;
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static (ApiKeyMiddleware middleware, DefaultHttpContext context, bool[] called) Create(
        string? configApiKey, string path = "/api/tournaments", string environment = "Development")
    {
        var called = new[] { false };
        RequestDelegate next = _ => { called[0] = true; return Task.CompletedTask; };
        var config = BuildConfig(configApiKey);
        var middleware = new ApiKeyMiddleware(next, config, new FakeEnv { EnvironmentName = environment });
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
    public async Task EmptyApiKeyInProduction_FailsClosed_Returns503()
    {
        // Fail-closed: in Production darf ein fehlender Key das Gate NICHT öffnen.
        var (middleware, context, called) = Create("", environment: Environments.Production);

        await middleware.InvokeAsync(context);

        Assert.False(called[0]);
        Assert.Equal(503, context.Response.StatusCode);
    }

    [Fact]
    public async Task EmptyApiKeyInProduction_HealthStillOpen()
    {
        // Liveness-Probe bleibt auch in Production ohne Key erreichbar.
        var (middleware, context, called) = Create("", "/api/health", Environments.Production);

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
    public async Task HealthIpEndpoint_RequiresAuth()
    {
        // /api/health/ip gibt die VPN-Exit-IP preis + triggert einen Outbound-Call → API-Key-pflichtig.
        var (middleware, context, called) = Create("secret-key", "/api/health/ip");

        await middleware.InvokeAsync(context);

        Assert.False(called[0]);
        Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public async Task HealthIpEndpoint_WithValidKey_PassesThrough()
    {
        var (middleware, context, called) = Create("secret-key", "/api/health/ip");
        context.Request.Headers["X-Api-Key"] = "secret-key";

        await middleware.InvokeAsync(context);

        Assert.True(called[0]);
    }

    [Fact]
    public async Task HealthLookalikePath_RequiresAuth()
    {
        // "/api/healthcheck" darf NICHT als offener Pfad gelten (vorher StartsWith-Bypass).
        var (middleware, context, called) = Create("secret-key", "/api/healthcheck");

        await middleware.InvokeAsync(context);

        Assert.False(called[0]);
        Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public async Task SwaggerLookalikePath_RequiresAuth()
    {
        var (middleware, context, called) = Create("secret-key", "/swaggerXYZ");

        await middleware.InvokeAsync(context);

        Assert.False(called[0]);
        Assert.Equal(401, context.Response.StatusCode);
    }
}
