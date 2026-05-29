using ChessResultsCrawler.Data;
using ChessResultsCrawler.Middleware;
using ChessResultsCrawler.Services;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.Elasticsearch;

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
            configuration.WriteTo.Elasticsearch(new ElasticsearchSinkOptions(new Uri(esUrl))
            {
                IndexFormat = "crawler-logs-{0:yyyy.MM}",
                AutoRegisterTemplate = true,
                AutoRegisterTemplateVersion = AutoRegisterTemplateVersion.ESv7,
                BatchAction = ElasticOpType.Create,
                NumberOfReplicas = 0,
                NumberOfShards = 1
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
    });
    builder.Services.AddHttpClient("Gluetun", client =>
    {
        client.Timeout = TimeSpan.FromSeconds(5);
    });
    builder.Services.AddScoped<HtmlParserService>();
    builder.Services.AddScoped<TournamentService>();
    builder.Services.AddScoped<RoundDetectionService>();
    builder.Services.AddSingleton<IBackgroundTaskQueue, BackgroundTaskQueue>();
    builder.Services.AddHostedService<BackgroundTaskWorker>();
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
            if (ex != null || httpContext.Response.StatusCode >= 500)
                return LogEventLevel.Error;
            return LogEventLevel.Information;
        };
    });
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
