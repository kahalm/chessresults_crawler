using Microsoft.AspNetCore.Mvc;

namespace ChessResultsCrawler.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HealthController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;

    public HealthController(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
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
            return Ok(new { ip = "unknown", error = ex.Message, timestamp = DateTime.UtcNow });
        }
    }
}
