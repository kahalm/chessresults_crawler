using ChessResultsCrawler.Services;
using Microsoft.Extensions.Configuration;

namespace ChessResultsCrawler.Tests.Services;

/// <summary>
/// Sichert die zentrale Konfiguration des "Gluetun"-HttpClients ab: optionaler X-API-Key
/// (Gluetun:ApiKey) fuer den per Role-Auth abgesicherten Control-Server; ohne Key exakt
/// bisheriges Verhalten (kein Header).
/// </summary>
public class GluetunClientSetupTests
{
    private static HttpClient Configure(Dictionary<string, string?> values)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var client = new HttpClient();
        GluetunClientSetup.Configure(client, config);
        return client;
    }

    [Fact]
    public void Configure_WithApiKey_AddsXApiKeyHeader()
    {
        using var client = Configure(new() { ["Gluetun:ApiKey"] = "geheim-123" });

        Assert.True(client.DefaultRequestHeaders.TryGetValues("X-API-Key", out var values));
        Assert.Equal("geheim-123", Assert.Single(values));
    }

    [Fact]
    public void Configure_WithEnvStyleKey_AddsXApiKeyHeader()
    {
        // Wie bei Gluetun__ApiUrl: auch die "__"-Schreibweise akzeptieren.
        using var client = Configure(new() { ["Gluetun__ApiKey"] = "env-key" });

        Assert.True(client.DefaultRequestHeaders.TryGetValues("X-API-Key", out var values));
        Assert.Equal("env-key", Assert.Single(values));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Configure_WithoutApiKey_SendsNoHeader(string? apiKey)
    {
        using var client = Configure(new() { ["Gluetun:ApiKey"] = apiKey });

        Assert.False(client.DefaultRequestHeaders.Contains("X-API-Key"));
    }

    [Fact]
    public void Configure_KeepsFiveSecondTimeout()
    {
        // Der bisherige Control-Server-Timeout (5 s) darf durch die Zentralisierung nicht kippen.
        using var client = Configure(new());

        Assert.Equal(TimeSpan.FromSeconds(5), client.Timeout);
    }
}
