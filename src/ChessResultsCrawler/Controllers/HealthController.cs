using Microsoft.AspNetCore.Mvc;

namespace ChessResultsCrawler.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HealthController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HealthController> _logger;

    public HealthController(IHttpClientFactory httpClientFactory, ILogger<HealthController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult Get() => Ok(new { status = "healthy", timestamp = DateTime.UtcNow });

    /// <summary>
    /// Commit-SHA + Ref des laufenden Images (vom CI als Build-Arg gesetzt, siehe Dockerfile
    /// <c>ARG GIT_SHA</c>/<c>GIT_REF</c> → ENV <c>BUILD_GIT_SHA</c>/<c>BUILD_GIT_REF</c>).
    /// RookHubs Admin-CI-Seite ruft das ab, um den GitHub-Actions-Run des laufenden Crawler-Images
    /// zu markieren (Branch bei :dev, Tag bei :prod). Leere Strings, wenn nicht gesetzt.
    /// </summary>
    [HttpGet("build-info")]
    public IActionResult BuildInfo() => Ok(new
    {
        sha = Environment.GetEnvironmentVariable("BUILD_GIT_SHA") ?? "",
        @ref = Environment.GetEnvironmentVariable("BUILD_GIT_REF") ?? "",
    });

    [HttpGet("ip")]
    public async Task<IActionResult> GetIp()
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            var ip = await client.GetStringAsync("https://api.ipify.org");
            return Ok(new { ip, timestamp = DateTime.UtcNow });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retrieve external IP");
            return StatusCode(503, new { error = "Failed to retrieve IP", timestamp = DateTime.UtcNow });
        }
    }
}
