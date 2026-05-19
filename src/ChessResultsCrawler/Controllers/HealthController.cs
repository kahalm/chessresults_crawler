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
