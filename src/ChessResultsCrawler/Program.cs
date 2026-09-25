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
    // SSRF-Schutz (keine automatischen Redirects) UND kurze Pool-Lebensdauer, damit keine
    // Verbindung eine VPN-Rotation ueberlebt — Begruendung und Messung in CrawlHttpHandler.
    .ConfigurePrimaryHttpMessageHandler(CrawlHttpHandler.Create);
    // Timeout + optionaler X-API-Key (Gluetun:ApiKey) für alle Control-Server-Aufrufe —
    // zentral in GluetunClientSetup, damit CrawlerService und VpnReadinessGate identisch laufen.
    // Der FIDE-Kalender: eigener Client, weil der Host ein anderer ist und der SSRF-Schutz des
    // CrawlerService auf chess-results.com prueft. Redirects auch hier NICHT automatisch folgen —
    // ein 3xx kommt als Antwort zurueck und scheitert an EnsureSuccessStatusCode, statt blind
    // irgendwohin zu laufen.
    builder.Services.AddHttpClient<FideCalendarService>(client =>
    {
        client.DefaultRequestHeaders.Add("User-Agent", "ChessResultsCrawler/1.0 (+RookHub)");
        // Unbegrenzt, weil das Zeitlimit JE VERSUCH beim Wiederholungs-Handler liegt (30 s):
        // HttpClient.Timeout gilt fuer alle Versuche ZUSAMMEN und liess die Wiederholung nie zum Zug kommen.
        client.Timeout = Timeout.InfiniteTimeSpan;
    })
    // Derselbe Handler: dieser Client laeuft durch denselben Tunnel (network_mode: service:gluetun)
    // und trifft nach einer Rotation dieselben toten Verbindungen.
    .ConfigurePrimaryHttpMessageHandler(CrawlHttpHandler.Create)
    .WithExitRotationRetry(30);

    // Der italienische Verbandskalender: wieder ein eigener Host, also ein eigener Client mit
    // demselben Handler. Groesserer Zeitrahmen als bei FIDE — die Trefferliste traegt ALLE Felder
    // inline und ist damit rund 1,25 MB fuer 283 Turniere.
    builder.Services.AddHttpClient<FsiCalendarService>(client =>
    {
        client.DefaultRequestHeaders.Add("User-Agent", "ChessResultsCrawler/1.0 (+RookHub)");
        // Unbegrenzt, weil das Zeitlimit JE VERSUCH beim Wiederholungs-Handler liegt (90 s):
        // HttpClient.Timeout gilt fuer alle Versuche ZUSAMMEN und liess die Wiederholung nie zum Zug kommen.
        client.Timeout = Timeout.InfiniteTimeSpan;
    })
    .ConfigurePrimaryHttpMessageHandler(CrawlHttpHandler.Create)
    .WithExitRotationRetry(90);

    // Der slowenische Verbandskalender. Eigener Client wie die uebrigen fremden Hosts.
    //
    // ACHTUNG User-Agent: der Server weist jede Zeichenfolge „bot" ab — auch Googlebot und
    // bingbot, es ist also ein kopierter nginx-Schnipsel und keine ueberlegte Absage. Unser Name
    // enthaelt „Crawler" und kommt durch; wer ihn aendert, sollte das vorher pruefen.
    builder.Services.AddHttpClient<SzsCalendarService>(client =>
    {
        client.DefaultRequestHeaders.Add("User-Agent", "ChessResultsCrawler/1.0 (+RookHub)");
        // Unbegrenzt, weil das Zeitlimit JE VERSUCH beim Wiederholungs-Handler liegt (30 s):
        // HttpClient.Timeout gilt fuer alle Versuche ZUSAMMEN und liess die Wiederholung nie zum Zug kommen.
        client.Timeout = Timeout.InfiniteTimeSpan;
    })
    .ConfigurePrimaryHttpMessageHandler(CrawlHttpHandler.Create)
    .WithExitRotationRetry(30);

    // Der slowakische Verbandskalender. Eigener Client wie die uebrigen fremden Hosts; groesserer
    // Zeitrahmen, weil ein Durchgang die Detailseite JE TURNIER nachholt (mit Pause dazwischen).
    builder.Services.AddHttpClient<ChessSkCalendarService>(client =>
    {
        client.DefaultRequestHeaders.Add("User-Agent", "ChessResultsCrawler/1.0 (+RookHub)");
        // Unbegrenzt, weil das Zeitlimit JE VERSUCH beim Wiederholungs-Handler liegt (30 s):
        // HttpClient.Timeout gilt fuer alle Versuche ZUSAMMEN und liess die Wiederholung nie zum Zug kommen.
        client.Timeout = Timeout.InfiniteTimeSpan;
    })
    .ConfigurePrimaryHttpMessageHandler(CrawlHttpHandler.Create)
    .WithExitRotationRetry(30);

    // Der ungarische Verbandskalender. GROSSZUEGIGER Zeitrahmen: der Endpunkt braucht fuer seine
    // 31 kB rund 75 Sekunden (am 2026-09-08 gemessen) — mit dem ueblichen halben Minuten-Limit
    // saehe die Quelle wie ein Dauerausfall aus.
    builder.Services.AddHttpClient<ChessHuCalendarService>(client =>
    {
        client.DefaultRequestHeaders.Add("User-Agent", "ChessResultsCrawler/1.0 (+RookHub)");
        // Unbegrenzt, weil das Zeitlimit JE VERSUCH beim Wiederholungs-Handler liegt (180 s):
        // HttpClient.Timeout gilt fuer alle Versuche ZUSAMMEN und liess die Wiederholung nie zum Zug kommen.
        client.Timeout = Timeout.InfiniteTimeSpan;
    })
    .ConfigurePrimaryHttpMessageHandler(CrawlHttpHandler.Create)
    .WithExitRotationRetry(180);

    // Der tschechische Verbandskalender. Ein Abruf, aber ein grosser (rund 300 kB HTML fuer
    // 89 Eintraege in drei Laschen).
    builder.Services.AddHttpClient<ChessCzCalendarService>(client =>
    {
        client.DefaultRequestHeaders.Add("User-Agent", "ChessResultsCrawler/1.0 (+RookHub)");
        // Unbegrenzt, weil das Zeitlimit JE VERSUCH beim Wiederholungs-Handler liegt (60 s):
        // HttpClient.Timeout gilt fuer alle Versuche ZUSAMMEN und liess die Wiederholung nie zum Zug kommen.
        client.Timeout = Timeout.InfiniteTimeSpan;
    })
    .ConfigurePrimaryHttpMessageHandler(CrawlHttpHandler.Create)
    .WithExitRotationRetry(60);

    // Der polnische Verbandskalender. Alte Infrastruktur (PHP 5.2), deshalb defensiv: ein Abruf
    // fuer die Liste, und die Detailseiten holt der Aufrufer einzeln und gedeckelt.
    builder.Services.AddHttpClient<ChessArbiterCalendarService>(client =>
    {
        client.DefaultRequestHeaders.Add("User-Agent", "ChessResultsCrawler/1.0 (+RookHub)");
        // Unbegrenzt, weil das Zeitlimit JE VERSUCH beim Wiederholungs-Handler liegt (60 s):
        // HttpClient.Timeout gilt fuer alle Versuche ZUSAMMEN und liess die Wiederholung nie zum Zug kommen.
        client.Timeout = Timeout.InfiniteTimeSpan;
    })
    .ConfigurePrimaryHttpMessageHandler(CrawlHttpHandler.Create)
    .WithExitRotationRetry(60);

    // Die Turnierdatenbank des Deutschen Schachbunds. Grosszuegiger Zeitrahmen: ein Durchgang holt
    // zwei Seiten je Region und haelt dazwischen die Wartezeit ein, die die robots.txt nennt.
    builder.Services.AddHttpClient<SchachbundCalendarService>(client =>
    {
        client.DefaultRequestHeaders.Add("User-Agent", "ChessResultsCrawler/1.0 (+RookHub)");
        // Unbegrenzt, weil das Zeitlimit JE VERSUCH beim Wiederholungs-Handler liegt (45 s):
        // HttpClient.Timeout gilt fuer alle Versuche ZUSAMMEN und liess die Wiederholung nie zum Zug kommen.
        client.Timeout = Timeout.InfiniteTimeSpan;
    })
    .ConfigurePrimaryHttpMessageHandler(CrawlHttpHandler.Create)
    .WithExitRotationRetry(45);

    // Der englische Verbandskalender. Zwei geblaetterte Endpunkte je Durchgang, dazwischen die
    // Wartezeit aus der robots.txt der Quelle.
    builder.Services.AddHttpClient<EcfCalendarService>(client =>
    {
        client.DefaultRequestHeaders.Add("User-Agent", "ChessResultsCrawler/1.0 (+RookHub)");
        // Unbegrenzt, weil das Zeitlimit JE VERSUCH beim Wiederholungs-Handler liegt (60 s):
        // HttpClient.Timeout gilt fuer alle Versuche ZUSAMMEN und liess die Wiederholung nie zum Zug kommen.
        client.Timeout = Timeout.InfiniteTimeSpan;
    })
    .ConfigurePrimaryHttpMessageHandler(CrawlHttpHandler.Create)
    .WithExitRotationRetry(60);

    // Die sechs Quellen der dritten Runde. Alle nach demselben Muster wie die uebrigen fremden
    // Hosts: eigener Client, eigener Name, derselbe Handler.
    //
    // Zwei brauchen mehr Zeit als die anderen: Frankreich holt in EINER eingehenden Anfrage zwoelf
    // Monatsseiten, und Irland fuenf Listenseiten mit Pause dazwischen.
    builder.Services.AddHttpClient<IcuCalendarService>(client =>
    {
        client.DefaultRequestHeaders.Add("User-Agent", "ChessResultsCrawler/1.0 (+RookHub)");
        // Unbegrenzt, weil das Zeitlimit JE VERSUCH beim Wiederholungs-Handler liegt (60 s):
        // HttpClient.Timeout gilt fuer alle Versuche ZUSAMMEN und liess die Wiederholung nie zum Zug kommen.
        client.Timeout = Timeout.InfiniteTimeSpan;
    })
    .ConfigurePrimaryHttpMessageHandler(CrawlHttpHandler.Create)
    .WithExitRotationRetry(60);

    builder.Services.AddHttpClient<FfeCalendarService>(client =>
    {
        client.DefaultRequestHeaders.Add("User-Agent", "ChessResultsCrawler/1.0 (+RookHub)");
        // Unbegrenzt, weil das Zeitlimit JE VERSUCH beim Wiederholungs-Handler liegt (60 s):
        // HttpClient.Timeout gilt fuer alle Versuche ZUSAMMEN und liess die Wiederholung nie zum Zug kommen.
        client.Timeout = Timeout.InfiniteTimeSpan;
    })
    .ConfigurePrimaryHttpMessageHandler(CrawlHttpHandler.Create)
    .WithExitRotationRetry(60);

    builder.Services.AddHttpClient<SjakkCalendarService>(client =>
    {
        client.DefaultRequestHeaders.Add("User-Agent", "ChessResultsCrawler/1.0 (+RookHub)");
        // Unbegrenzt, weil das Zeitlimit JE VERSUCH beim Wiederholungs-Handler liegt (60 s):
        // HttpClient.Timeout gilt fuer alle Versuche ZUSAMMEN und liess die Wiederholung nie zum Zug kommen.
        client.Timeout = Timeout.InfiniteTimeSpan;
    })
    .ConfigurePrimaryHttpMessageHandler(CrawlHttpHandler.Create)
    .WithExitRotationRetry(60);

    builder.Services.AddHttpClient<ChessScotlandCalendarService>(client =>
    {
        client.DefaultRequestHeaders.Add("User-Agent", "ChessResultsCrawler/1.0 (+RookHub)");
        // Unbegrenzt, weil das Zeitlimit JE VERSUCH beim Wiederholungs-Handler liegt (60 s):
        // HttpClient.Timeout gilt fuer alle Versuche ZUSAMMEN und liess die Wiederholung nie zum Zug kommen.
        client.Timeout = Timeout.InfiniteTimeSpan;
    })
    .ConfigurePrimaryHttpMessageHandler(CrawlHttpHandler.Create)
    .WithExitRotationRetry(60);

    // Kanada: zwei Abrufe (Seite fuer den Dateinamen, dann die Datei). Keine Wartezeit in der
    // robots.txt — sie ist woertlich nur „User-agent: *" ohne eine einzige Regel.
    builder.Services.AddHttpClient<CfcCalendarService>(client =>
    {
        client.DefaultRequestHeaders.Add("User-Agent", "ChessResultsCrawler/1.0 (+RookHub)");
        // Unbegrenzt, weil das Zeitlimit JE VERSUCH beim Wiederholungs-Handler liegt (60 s):
        // HttpClient.Timeout gilt fuer alle Versuche ZUSAMMEN und liess die Wiederholung nie zum Zug kommen.
        client.Timeout = Timeout.InfiniteTimeSpan;
    })
    .ConfigurePrimaryHttpMessageHandler(CrawlHttpHandler.Create)
    .WithExitRotationRetry(60);

    builder.Services.AddHttpClient<WcuCalendarService>(client =>
    {
        client.DefaultRequestHeaders.Add("User-Agent", "ChessResultsCrawler/1.0 (+RookHub)");
        // Unbegrenzt, weil das Zeitlimit JE VERSUCH beim Wiederholungs-Handler liegt (60 s):
        // HttpClient.Timeout gilt fuer alle Versuche ZUSAMMEN und liess die Wiederholung nie zum Zug kommen.
        client.Timeout = Timeout.InfiniteTimeSpan;
    })
    .ConfigurePrimaryHttpMessageHandler(CrawlHttpHandler.Create)
    .WithExitRotationRetry(60);

    // Die Niederlande verlangen in ihrer robots.txt 15 Sekunden zwischen zwei Abrufen. Bei zwei
    // Seiten ist das ein Durchgang von rund 17 Sekunden — der Zeitrahmen muss das aushalten.
    builder.Services.AddHttpClient<KnsbCalendarService>(client =>
    {
        client.DefaultRequestHeaders.Add("User-Agent", "ChessResultsCrawler/1.0 (+RookHub)");
        // Unbegrenzt, weil das Zeitlimit JE VERSUCH beim Wiederholungs-Handler liegt (90 s):
        // HttpClient.Timeout gilt fuer alle Versuche ZUSAMMEN und liess die Wiederholung nie zum Zug kommen.
        client.Timeout = Timeout.InfiniteTimeSpan;
    })
    .ConfigurePrimaryHttpMessageHandler(CrawlHttpHandler.Create)
    .WithExitRotationRetry(90);

    builder.Services.AddHttpClient<FrsahCalendarService>(client =>
    {
        client.DefaultRequestHeaders.Add("User-Agent", "ChessResultsCrawler/1.0 (+RookHub)");
        // Unbegrenzt, weil das Zeitlimit JE VERSUCH beim Wiederholungs-Handler liegt (60 s):
        // HttpClient.Timeout gilt fuer alle Versuche ZUSAMMEN und liess die Wiederholung nie zum Zug kommen.
        client.Timeout = Timeout.InfiniteTimeSpan;
    })
    .ConfigurePrimaryHttpMessageHandler(CrawlHttpHandler.Create)
    .WithExitRotationRetry(60);

    builder.Services.AddHttpClient("Gluetun",
        client => GluetunClientSetup.Configure(client, builder.Configuration));
    builder.Services.AddScoped<HtmlParserService>();
    builder.Services.AddScoped<TournamentService>();
    builder.Services.AddScoped<RoundDetectionService>();
    // Kapazitaet einstellbar (Crawler:QueueCapacity); die Vorgabe fasst den Refresh-Schwall,
    // den RookHub nach jedem API-Start fuer alle abonnierten Turniere ausloest.
    builder.Services.AddSingleton<IBackgroundTaskQueue>(_ => new BackgroundTaskQueue(
        builder.Configuration.GetValue("Crawler:QueueCapacity", BackgroundTaskQueue.DefaultCapacity)));
    // Gate, das den ersten Crawl nach dem Start bis zur VPN-Tunnel-Bereitschaft zurückhält.
    builder.Services.AddSingleton<VpnReadinessGate>();
    // Wiederholt einen Quellen-Abruf ueber einen ANDEREN VPN-Ausgang, wenn keine Verbindung
    // zustande kommt (mehrere Verbaende sperren ganze Hosting-Netze, und zwar verschiedene).
    builder.Services.AddTransient<RotateOnConnectFailureHandler>();
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

/// <summary>
/// Haengt die Wiederholung ueber einen anderen VPN-Ausgang an einen Quellen-Client und legt das
/// Zeitlimit JE VERSUCH fest. Der Client selbst laeuft unbegrenzt — sein Zeitlimit haette fuer
/// alle Versuche zusammen gegolten und die Wiederholung damit ausgehebelt.
/// </summary>
internal static class ExitRotationRetryExtensions
{
    public static IHttpClientBuilder WithExitRotationRetry(this IHttpClientBuilder builder, int attemptSeconds) =>
        builder.AddHttpMessageHandler(sp => new RotateOnConnectFailureHandler(
            sp.GetRequiredService<VpnReadinessGate>(),
            sp.GetRequiredService<ILogger<RotateOnConnectFailureHandler>>(),
            TimeSpan.FromSeconds(attemptSeconds)));
}
