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
builder.Services.AddScoped<HtmlParserService>();
builder.Services.AddScoped<TournamentService>();
builder.Services.AddScoped<RoundDetectionService>();
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
    db.Database.EnsureCreated();

    // Add ResponseBody column to RequestLogs if it doesn't exist (for existing DBs)
    try
    {
        await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE RequestLogs ADD COLUMN IF NOT EXISTS ResponseBody LONGTEXT NULL
            """);
    }
    catch
    {
        // Column may already exist or table may not exist yet — safe to ignore
    }

    // Add Location and DateText columns to Tournaments (for existing DBs)
    try
    {
        await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE Tournaments ADD COLUMN IF NOT EXISTS Location VARCHAR(500) NULL
            """);
        await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE Tournaments ADD COLUMN IF NOT EXISTS DateText VARCHAR(100) NULL
            """);
    }
    catch
    {
        // Columns may already exist — safe to ignore
    }
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
