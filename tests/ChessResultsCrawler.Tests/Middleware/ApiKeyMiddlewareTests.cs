using System.Net;
using ChessResultsCrawler.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Serilog.Events;

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

    private static IConfiguration BuildConfig(string? apiKey, string? allowAnonymous = null)
    {
        var dict = new Dictionary<string, string?>();
        if (apiKey is not null)
            dict["API_KEY"] = apiKey;
        if (allowAnonymous is not null)
            dict["API_KEY_ALLOW_ANONYMOUS"] = allowAnonymous;
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static (ApiKeyMiddleware middleware, DefaultHttpContext context, bool[] called) Create(
        string? configApiKey, string path = "/api/tournaments", string environment = "Development",
        ILogger<ApiKeyMiddleware>? logger = null, string? allowAnonymous = null)
    {
        var called = new[] { false };
        RequestDelegate next = _ => { called[0] = true; return Task.CompletedTask; };
        var config = BuildConfig(configApiKey, allowAnonymous);
        var middleware = new ApiKeyMiddleware(next, config, new FakeEnv { EnvironmentName = environment }, logger);
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return (middleware, context, called);
    }

    // ----- Fail-closed unabhaengig von der Umgebung (Review W4s S3-010) ------------------------

    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task NoApiKeyConfigured_FailsClosed_Returns503_InEveryEnvironment(string environment)
    {
        // Vorher war ein Stack ohne Key in jeder Umgebung ausser Production still offen (POST /api/crawl inklusive).
        var (middleware, context, called) = Create(null, environment: environment);

        await middleware.InvokeAsync(context);

        Assert.False(called[0]);
        Assert.Equal(503, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyOrWhitespaceApiKeyInDevelopment_FailsClosed_Returns503(string apiKey)
    {
        var (middleware, context, called) = Create(apiKey);

        await middleware.InvokeAsync(context);

        Assert.False(called[0]);
        Assert.Equal(503, context.Response.StatusCode);
    }

    [Fact]
    public async Task NoApiKeyInDevelopment_HealthStillOpen()
    {
        var (middleware, context, called) = Create(null, "/api/health");

        await middleware.InvokeAsync(context);

        Assert.True(called[0]);
    }

    [Theory]
    [InlineData("Development", "true")]
    [InlineData("Development", "True")]
    [InlineData("Staging", "true")]
    public async Task NoApiKey_WithExplicitSwitchOutsideProduction_PassesThrough(string environment, string flag)
    {
        var (middleware, context, called) = Create(null, environment: environment, allowAnonymous: flag);

        await middleware.InvokeAsync(context);

        Assert.True(called[0]);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("")]
    public async Task NoApiKey_SwitchNotExactlyTrue_StaysClosed(string flag)
    {
        var (middleware, context, called) = Create(null, allowAnonymous: flag);

        await middleware.InvokeAsync(context);

        Assert.False(called[0]);
        Assert.Equal(503, context.Response.StatusCode);
    }

    [Fact]
    public async Task NoApiKey_SwitchIsIgnoredInProduction_Returns503()
    {
        var (middleware, context, called) = Create(null, environment: Environments.Production, allowAnonymous: "true");

        await middleware.InvokeAsync(context);

        Assert.False(called[0]);
        Assert.Equal(503, context.Response.StatusCode);
    }

    [Fact]
    public async Task ConfiguredKey_SwitchDoesNotOpenTheGate_Returns401()
    {
        // Der Schalter ersetzt nur einen FEHLENDEN Key; ein gesetzter Key wird immer verlangt.
        var (middleware, context, called) = Create("secret-key", allowAnonymous: "true");

        await middleware.InvokeAsync(context);

        Assert.False(called[0]);
        Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public async Task MultipleApiKeyHeaderValues_Returns401_EvenIfJoinedValueMatches()
    {
        // Wie piratechess: genau EIN Header-Wert. Vorher wurden die Werte zu "a,b" zusammengefuegt.
        var (middleware, context, called) = Create("a,b");
        context.Request.Headers["X-Api-Key"] = new Microsoft.Extensions.Primitives.StringValues(["a", "b"]);

        await middleware.InvokeAsync(context);

        Assert.False(called[0]);
        Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public async Task RepeatedValidApiKeyHeader_Returns401()
    {
        var (middleware, context, called) = Create("secret-key");
        context.Request.Headers["X-Api-Key"] = new Microsoft.Extensions.Primitives.StringValues(["secret-key", "secret-key"]);

        await middleware.InvokeAsync(context);

        Assert.False(called[0]);
        Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public async Task RejectedResponses_HaveMessageBody()
    {
        // Gleiche Form wie piratechess: JSON { message } bei 401 und 503.
        var (m401, c401, _) = Create("secret-key");
        await m401.InvokeAsync(c401);
        var (m503, c503, _) = Create(null);
        await m503.InvokeAsync(c503);

        Assert.Equal("{\"message\":\"Invalid or missing API key.\"}", ReadBody(c401));
        Assert.Equal("{\"message\":\"API key not configured.\"}", ReadBody(c503));
    }

    private static string ReadBody(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
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

    [Theory]
    [InlineData("/swagger")]
    [InlineData("/swagger/index.html")]
    [InlineData("/swagger/v1/swagger.json")]
    public async Task SwaggerPath_RequiresAuth(string path)
    {
        // Opt-out nur /api/health. Die Swagger-UI gibt es nur in Development und dort wird sie VOR
        // dieser Middleware ausgeliefert; in Production verriet der offene Pfad bisher nur 404 statt 401.
        var (middleware, context, called) = Create("secret-key", path);

        await middleware.InvokeAsync(context);

        Assert.False(called[0]);
        Assert.Equal(401, context.Response.StatusCode);
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
    public async Task PlaceholderKeyInDevelopment_TreatedAsMissing_Returns503()
    {
        // Wie ein fehlender Schluessel: auch in Development zu, offen nur mit dem expliziten Schalter.
        var (middleware, context, called) = Create(TemplatePlaceholder);
        context.Request.Headers["X-Api-Key"] = TemplatePlaceholder;

        await middleware.InvokeAsync(context);

        Assert.False(called[0]);
        Assert.Equal(503, context.Response.StatusCode);
    }

    [Fact]
    public async Task PlaceholderKeyInDevelopment_WithSwitch_PassesThrough()
    {
        var (middleware, context, called) = Create(TemplatePlaceholder, allowAnonymous: "true");

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
    [InlineData("Development")]
    [InlineData("Production")]
    public void MissingKey_WithoutSwitch_LogsOneError(string environment)
    {
        // Sonst steht nach einem Deploy ohne Key nirgends, warum der Crawler auf alles mit 503 antwortet.
        var logger = new Mock<ILogger<ApiKeyMiddleware>>();

        Create(null, environment: environment, logger: logger.Object);

        logger.Verify(l => l.Log(LogLevel.Error, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    [Fact]
    public void MissingKey_WithSwitchInDevelopment_LogsOneWarningAndNoError()
    {
        var logger = new Mock<ILogger<ApiKeyMiddleware>>();

        Create(null, logger: logger.Object, allowAnonymous: "true");

        logger.Verify(l => l.Log(LogLevel.Warning, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
        logger.Verify(l => l.Log(LogLevel.Error, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Never);
    }

    // ----- Abgewiesene Anfragen hinterlassen eine gedrosselte Security-Warning (Review W4s S3-012) ----

    private const string RejectTemplatePrefix = "API-Anfrage abgewiesen";

    private sealed class ListSink : Serilog.Core.ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent) { lock (Events) Events.Add(logEvent); }
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Echter Serilog-Logger mit LogContext, damit auch die gepushte LogTags-Property pruefbar ist.</summary>
    private static (ApiKeyMiddleware middleware, ListSink sink) CreateWithSink(string? configApiKey,
        TimeProvider? time = null, string environment = "Production")
    {
        var sink = new ListSink();
        var serilog = new Serilog.LoggerConfiguration().MinimumLevel.Verbose()
            .Enrich.FromLogContext().WriteTo.Sink(sink).CreateLogger();
        var logger = new Serilog.Extensions.Logging.SerilogLoggerFactory(serilog).CreateLogger<ApiKeyMiddleware>();
        var middleware = new ApiKeyMiddleware(_ => Task.CompletedTask, BuildConfig(configApiKey),
            new FakeEnv { EnvironmentName = environment }, logger, time);
        return (middleware, sink);
    }

    private static DefaultHttpContext NewRequest(string path, params string[] apiKeys)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = path;
        context.Request.QueryString = new QueryString("?token=query-secret");
        if (apiKeys.Length > 0)
            context.Request.Headers["X-Api-Key"] = new Microsoft.Extensions.Primitives.StringValues(apiKeys);
        context.Connection.RemoteIpAddress = IPAddress.Parse("172.18.0.9");
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static List<LogEvent> Rejections(ListSink sink) =>
        sink.Events.Where(e => e.MessageTemplate.Text.StartsWith(RejectTemplatePrefix)).ToList();

    private static object? Prop(LogEvent e, string name) =>
        e.Properties.TryGetValue(name, out var v) && v is ScalarValue s ? s.Value : null;

    [Fact]
    public async Task WrongKey_Returns401_AndLogsSecurityWarning_WithPathStatusAndIp_NeverTheKey()
    {
        // Vorher endete die Anfrage VOR dem Request-Logging ohne jede Logzeile — ein Schluessel-Durchprobieren
        // im Docker-Netz blieb fuer Elasticsearch/log-watcher unsichtbar.
        var (middleware, sink) = CreateWithSink("real-secret-key");
        var context = NewRequest("/api/crawl", "guessed-key-123");

        await middleware.InvokeAsync(context);

        Assert.Equal(401, context.Response.StatusCode);
        var e = Assert.Single(Rejections(sink));
        Assert.Equal(LogEventLevel.Warning, e.Level);
        Assert.Equal("security", Prop(e, "LogTags"));
        Assert.Equal("POST", Prop(e, "RequestMethod"));
        Assert.Equal("/api/crawl", Prop(e, "RequestPath"));
        Assert.Equal(401, Prop(e, "StatusCode"));
        Assert.Equal("172.18.0.9", Prop(e, "IpAddress"));
        Assert.Equal("X-Api-Key falsch", Prop(e, "Reason"));
        var everything = e.RenderMessage() + string.Join("|", e.Properties.Values.Select(v => v.ToString()));
        Assert.DoesNotContain("guessed-key-123", everything);
        Assert.DoesNotContain("real-secret-key", everything);
        Assert.DoesNotContain("query-secret", everything);
    }

    [Theory]
    [InlineData(new string[0], "X-Api-Key fehlt")]
    [InlineData(new[] { "real-secret-key", "real-secret-key" }, "mehrere X-Api-Key-Werte")]
    public async Task MissingOrRepeatedKey_Returns401_AndLogsReason(string[] apiKeys, string reason)
    {
        var (middleware, sink) = CreateWithSink("real-secret-key");
        var context = NewRequest("/api/tournaments", apiKeys);

        await middleware.InvokeAsync(context);

        Assert.Equal(401, context.Response.StatusCode);
        var e = Assert.Single(Rejections(sink));
        Assert.Equal(reason, Prop(e, "Reason"));
        Assert.DoesNotContain("real-secret-key", e.RenderMessage());
    }

    [Fact]
    public async Task NoKeyConfigured_Returns503_AndLogsSecurityWarning()
    {
        // Fehlt nach einem Deploy der Key, stand bisher pro Anfrage nichts im Crawler-Log (nur indirekt ueber RookHub).
        var (middleware, sink) = CreateWithSink(null);
        var context = NewRequest("/api/tournaments");

        await middleware.InvokeAsync(context);

        Assert.Equal(503, context.Response.StatusCode);
        var e = Assert.Single(Rejections(sink));
        Assert.Equal(LogEventLevel.Warning, e.Level);
        Assert.Equal("security", Prop(e, "LogTags"));
        Assert.Equal(503, Prop(e, "StatusCode"));
        Assert.Equal("/api/tournaments", Prop(e, "RequestPath"));
    }

    [Fact]
    public async Task AcceptedRequestAndHealthProbe_LogNoRejection()
    {
        var (middleware, sink) = CreateWithSink("real-secret-key");

        await middleware.InvokeAsync(NewRequest("/api/tournaments", "real-secret-key"));
        await middleware.InvokeAsync(NewRequest("/api/health"));

        Assert.Empty(Rejections(sink));
    }

    [Fact]
    public async Task RejectLog_IsThrottledPerWindow_AndNextWindowReportsSuppressedCount()
    {
        // Ein Scan darf das Log nicht fluten: hoechstens RejectLogBudget Warnings je Fenster, der Rest wird gezaehlt.
        var time = new ManualTime();
        var (middleware, sink) = CreateWithSink("real-secret-key", time);

        for (var i = 0; i < ApiKeyMiddleware.RejectLogBudget + 5; i++)
        {
            var context = NewRequest($"/api/probe{i}", "wrong");
            await middleware.InvokeAsync(context);
            Assert.Equal(401, context.Response.StatusCode); // gedrosselt wird nur das Log, nie die Abweisung
        }
        Assert.Equal(ApiKeyMiddleware.RejectLogBudget, Rejections(sink).Count);
        Assert.All(Rejections(sink), e => Assert.Equal(0, Prop(e, "SuppressedCount")));

        time.Now += ApiKeyMiddleware.RejectLogWindow;
        await middleware.InvokeAsync(NewRequest("/api/next", "wrong"));

        var rejections = Rejections(sink);
        Assert.Equal(ApiKeyMiddleware.RejectLogBudget + 1, rejections.Count);
        Assert.Equal(5, Prop(rejections[^1], "SuppressedCount"));
        Assert.Equal("/api/next", Prop(rejections[^1], "RequestPath"));
    }

    [Fact]
    public async Task UseMiddleware_ActivatesFromDi_WithoutRegisteredTimeProvider()
    {
        // Der optionale TimeProvider-Parameter darf die Aktivierung per UseMiddleware (Program.cs) nicht brechen.
        var services = new ServiceCollection()
            .AddSingleton(BuildConfig("real-secret-key"))
            .AddSingleton<IHostEnvironment>(new FakeEnv { EnvironmentName = Environments.Production })
            .AddLogging()
            .BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        app.UseMiddleware<ApiKeyMiddleware>();
        app.Run(_ => Task.CompletedTask);
        var context = NewRequest("/api/crawl", "wrong");

        await app.Build()(context);

        Assert.Equal(401, context.Response.StatusCode);
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
