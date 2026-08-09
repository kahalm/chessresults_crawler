using ChessResultsCrawler.Data;
using ChessResultsCrawler.Middleware;
using ChessResultsCrawler.Services;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Events;
using Elastic.Serilog.Sinks;

// ReDoS-Schutz: globales Default-Timeout fuer Regex-Auswertungen, da der gecrawlte
// HTML-Body untrusted ist (verhindert haengende Regex bei pathologischer Eingabe).
AppContext.SetData("REGEX_DEFAULT_MATCH_TIMEOUT", TimeSpan.FromSeconds(5));

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, configuration) =>
    {
        configuration
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithMachineName()
            .Enrich.WithProperty("Application", "ChessResultsCrawler")
            .WriteTo.Console();

        var esUrl = context.Configuration["Elasticsearch:Url"];
        if (!string.IsNullOrEmpty(esUrl))
        {
            // ECS-Schema (Elastic.Serilog.Sinks) in einen Data-Stream. Felder werden zentral per
            // Ingest-Pipeline normalisiert (siehe log-watcher/schema/logging-schema.md).
            // Data-Stream-Basisname aus dem bisherigen Monats-IndexFormat ableiten (Teil vor "{"),
            // damit dev/prod unter ihren bestehenden "*-logs-*"-Patterns bleiben (Kibana, log-watcher).
            var indexFormat = context.Configuration["Elasticsearch:IndexFormat"] ?? "crawler-logs-{0:yyyy.MM}";
            var streamName = indexFormat.Split('{')[0].TrimEnd('-', '.', ' ');
            configuration.WriteTo.Elasticsearch([new Uri(esUrl)], opts =>
            {
                opts.DataStream = new Elastic.Ingest.Elasticsearch.DataStreams.DataStreamName(streamName);
                opts.BootstrapMethod = Elastic.Ingest.Elasticsearch.BootstrapMethod.Silent;
            });
        }
    });

    // Database
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not configured.");

    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseMySql(connectionString, new MariaDbServerVersion(new Version(11, 0, 0))));

    // Services
    builder.Services.AddMemoryCache();
    builder.Services.AddHttpClient<CrawlerService>(client =>
    {
        client.DefaultRequestHeaders.Add("User-Agent", "ChessResultsCrawler/1.0");
        client.Timeout = TimeSpan.FromSeconds(30);
    })
    // SSRF-Schutz: Redirects NICHT automatisch folgen. CrawlerService folgt ihnen manuell und prüft
    // jeden Hop (chess-results.com + https) VOR dem Absenden — sonst würde HttpClient eine
    // Redirect-Kette (bis 50 Hops) blind bis zu einem internen Host folgen und erst danach prüfen.
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
    // Timeout + optionaler X-API-Key (Gluetun:ApiKey) für alle Control-Server-Aufrufe —
    // zentral in GluetunClientSetup, damit CrawlerService und VpnReadinessGate identisch laufen.
    builder.Services.AddHttpClient("Gluetun",
        client => GluetunClientSetup.Configure(client, builder.Configuration));
    builder.Services.AddScoped<HtmlParserService>();
    builder.Services.AddScoped<TournamentService>();
    builder.Services.AddScoped<RoundDetectionService>();
    builder.Services.AddSingleton<IBackgroundTaskQueue, BackgroundTaskQueue>();
    // Gate, das den ersten Crawl nach dem Start bis zur VPN-Tunnel-Bereitschaft zurückhält.
    builder.Services.AddSingleton<VpnReadinessGate>();
    builder.Services.AddHostedService<BackgroundTaskWorker>();
    // Periodisches Lebenszeichen nach ES (Standard 60 s) → log-watcher erkennt toten Crawler.
    builder.Services.AddHostedService<HeartbeatService>();
    builder.Services.AddHttpClient(); // For HealthController IP check

    // API
    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new() { Title = "Chess Results Crawler API", Version = "v1" });
    });

    var app = builder.Build();

    // Auto-migrate
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.Migrate();

        // Verwaiste Jobs aus dem vorigen Prozess (Queued/Running ohne Worker) freigeben,
        // sonst blockiert ihr unique ActiveKey künftige Crawls desselben Turniers dauerhaft.
        var recovered = CrawlJobRecovery.RecoverStaleJobsAsync(db).GetAwaiter().GetResult();
        if (recovered > 0)
            scope.ServiceProvider.GetRequiredService<ILogger<Program>>()
                .LogWarning("Startup: {Count} verwaiste Crawl-Jobs auf Failed gesetzt.", recovered);
    }

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseMiddleware<ApiKeyMiddleware>();
    app.UseSerilogRequestLogging(options =>
    {
        options.GetLevel = (httpContext, elapsed, ex) =>
        {
            var path = httpContext.Request.Path.Value ?? "";
            if (path.StartsWith("/health") || path.StartsWith("/swagger"))
                return LogEventLevel.Debug;
            var status = httpContext.Response.StatusCode;
            // Gateway-/Drosselungs-Probleme (vom UpstreamErrorMiddleware gemappt: 502 Upstream weg,
            // 504 Upstream-Timeout, 503 eigener Rate-Limiter gesaettigt) sind KEIN Crash unseres
            // Service → Warning statt Error, damit der log-watcher sie nicht als HIGH-Fehler alarmiert.
            if (status is StatusCodes.Status502BadGateway
                or StatusCodes.Status503ServiceUnavailable
                or StatusCodes.Status504GatewayTimeout)
                return LogEventLevel.Warning;
            // 499 = Client hat die Verbindung abgebrochen → kein Fehler.
            if (status == 499)
                return LogEventLevel.Information;
            if (ex != null || status >= 500)
                return LogEventLevel.Error;
            return LogEventLevel.Information;
        };
    });
    // NACH dem Request-Logging, damit dieses den gemappten Gateway-Statuscode sieht
    // (sonst wuerde die rohe Exception als Error geloggt).
    app.UseMiddleware<UpstreamErrorMiddleware>();
    app.MapControllers();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}

public partial class Program { }
