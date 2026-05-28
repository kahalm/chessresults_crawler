using ChessResultsCrawler.Data;
using ChessResultsCrawler.Middleware;
using ChessResultsCrawler.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Database
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not configured.");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseMySql(connectionString, new MariaDbServerVersion(new Version(11, 0, 0))));

// Services
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
builder.Services.AddHostedService<LogRetentionService>();
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
app.UseMiddleware<RequestLoggingMiddleware>();
app.MapControllers();

app.Run();

public partial class Program { }
