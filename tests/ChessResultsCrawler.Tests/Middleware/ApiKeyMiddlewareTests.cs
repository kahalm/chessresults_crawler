using ChessResultsCrawler.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;

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
        string? configApiKey, string path = "/api/tournaments", string environment = "Development",
        ILogger<ApiKeyMiddleware>? logger = null)
    {
        var called = new[] { false };
        RequestDelegate next = _ => { called[0] = true; return Task.CompletedTask; };
        var config = BuildConfig(configApiKey);
        var middleware = new ApiKeyMiddleware(next, config, new FakeEnv { EnvironmentName = environment }, logger);
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

    // ----- Platzhalter aus den .env-Vorlagen (Review W2 I2-001) --------------------------------

    private const string TemplatePlaceholder = "change_me_to_a_secure_key"; // rookhub/.env.vpn.example

    [Fact]
    public async Task PlaceholderKeyInProduction_FailsClosed_Returns503_EvenWithMatchingHeader()
    {
        // Der Platzhalter steht in einem oeffentlichen Repo — wer ihn mitschickt, darf nicht rein.
        var (middleware, context, called) = Create(TemplatePlaceholder, environment: Environments.Production);
        context.Request.Headers["X-Api-Key"] = TemplatePlaceholder;

        await middleware.InvokeAsync(context);

        Assert.False(called[0]);
        Assert.Equal(503, context.Response.StatusCode);
    }

    [Fact]
    public async Task PlaceholderKeyInProduction_HealthStillOpen()
    {
        var (middleware, context, called) = Create(TemplatePlaceholder, "/api/health", Environments.Production);

        await middleware.InvokeAsync(context);

        Assert.True(called[0]);
    }

    [Fact]
    public async Task PlaceholderKeyInDevelopment_TreatedAsMissing_PassesThrough()
    {
        // Wie ein fehlender Schluessel: Development bleibt der offene lokale Fallback.
        var (middleware, context, called) = Create(TemplatePlaceholder);

        await middleware.InvokeAsync(context);

        Assert.True(called[0]);
    }

    [Fact]
    public void PlaceholderKey_LogsOneError()
    {
        var logger = new Mock<ILogger<ApiKeyMiddleware>>();

        Create(TemplatePlaceholder, environment: Environments.Production, logger: logger.Object);

        logger.Verify(l => l.Log(LogLevel.Error, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    [Fact]
    public void RealKey_LogsNothing()
    {
        var logger = new Mock<ILogger<ApiKeyMiddleware>>();

        Create("secret-key", environment: Environments.Production, logger: logger.Object);

        logger.Verify(l => l.Log(It.IsAny<LogLevel>(), It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Never);
    }

    [Theory]
    [InlineData("change_me_to_a_secure_key", true)]
    [InlineData("change_me", true)]
    [InlineData("CHANGE_ME_please", true)]
    [InlineData("  change_me_to_a_secure_key ", true)]
    [InlineData("your_crawler_key", true)]
    [InlineData("secret-key", false)]
    [InlineData("my_change_me", false)]
    [InlineData("changeme", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsPlaceholder_RecognisesTemplatePrefixes(string? value, bool expected)
    {
        Assert.Equal(expected, ApiKeyMiddleware.IsPlaceholder(value));
    }
}
