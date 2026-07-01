using ChessResultsCrawler.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChessResultsCrawler.Tests.Controllers;

/// <summary>
/// Tests des build-info-Endpoints: er spiegelt die vom CI gesetzten ENV-Variablen
/// (BUILD_GIT_SHA/BUILD_GIT_REF), damit RookHubs Admin-CI-Seite den laufenden Crawler-Build markiert.
/// </summary>
public class HealthControllerTests
{
    private static HealthController NewController() =>
        new(new SimpleHttpClientFactory(), NullLogger<HealthController>.Instance);

    [Fact]
    public void BuildInfo_ReflectsEnvironmentVariables()
    {
        Environment.SetEnvironmentVariable("BUILD_GIT_SHA", "abc123");
        Environment.SetEnvironmentVariable("BUILD_GIT_REF", "master");
        try
        {
            var result = NewController().BuildInfo() as OkObjectResult;
            Assert.NotNull(result);
            var sha = result!.Value!.GetType().GetProperty("sha")!.GetValue(result.Value);
            var reff = result.Value!.GetType().GetProperty("ref")!.GetValue(result.Value);
            Assert.Equal("abc123", sha);
            Assert.Equal("master", reff);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BUILD_GIT_SHA", null);
            Environment.SetEnvironmentVariable("BUILD_GIT_REF", null);
        }
    }

    [Fact]
    public void BuildInfo_MissingEnv_ReturnsEmptyStrings()
    {
        Environment.SetEnvironmentVariable("BUILD_GIT_SHA", null);
        Environment.SetEnvironmentVariable("BUILD_GIT_REF", null);
        var result = NewController().BuildInfo() as OkObjectResult;
        Assert.NotNull(result);
        var sha = result!.Value!.GetType().GetProperty("sha")!.GetValue(result.Value);
        Assert.Equal("", sha);
    }

    private sealed class SimpleHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
