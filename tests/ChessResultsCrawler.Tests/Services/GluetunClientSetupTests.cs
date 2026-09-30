using System.Net;
using ChessResultsCrawler.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ChessResultsCrawler.Tests.Services;

/// <summary>
/// Sichert die zentrale Konfiguration des "Gluetun"-HttpClients ab: optionaler X-API-Key
/// (Gluetun:ApiKey) fuer den per Role-Auth abgesicherten Control-Server; ohne Key exakt
/// bisheriges Verhalten (kein Header). Dazu die <see cref="GluetunOptions"/>, die ApiUrl, ApiKey
/// und Neustart-Pause an EINER Stelle lesen (W4s S3-016).
/// </summary>
public class GluetunClientSetupTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static HttpClient Configure(Dictionary<string, string?> values)
    {
        var client = new HttpClient();
        GluetunClientSetup.Configure(client, GluetunOptions.From(Config(values)));
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
    public void Options_FromEnvironmentVariablesWithDoubleUnderscore_AreRead()
    {
        // Die Env-Schreibweise Gluetun__ApiKey braucht keinen eigenen Rueckfall: der
        // Umgebungsvariablen-Provider macht daraus Gluetun:ApiKey. (Eigener Praefix, damit keine
        // andere Testklasse dieselbe Prozess-Variable sieht.)
        const string prefix = "CRAWLER_TEST_W4S_S3016_";
        Environment.SetEnvironmentVariable(prefix + "Gluetun__ApiKey", "env-key");
        Environment.SetEnvironmentVariable(prefix + "Gluetun__ApiUrl", "http://gluetun.env:8000");
        Environment.SetEnvironmentVariable(prefix + "Crawler__VpnRestartPauseMs", "1234");
        try
        {
            var config = new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build();
            var options = GluetunOptions.From(config);

            Assert.Equal("env-key", options.ApiKey);
            Assert.Equal("http://gluetun.env:8000", options.ApiUrl);
            Assert.Equal(1234, options.RestartPauseMs);
        }
        finally
        {
            Environment.SetEnvironmentVariable(prefix + "Gluetun__ApiKey", null);
            Environment.SetEnvironmentVariable(prefix + "Gluetun__ApiUrl", null);
            Environment.SetEnvironmentVariable(prefix + "Crawler__VpnRestartPauseMs", null);
        }
    }

    [Fact]
    public void Options_Defaults_MatchTheFormerValues()
    {
        var options = GluetunOptions.From(Config(new()));

        Assert.Equal("http://localhost:8000", options.ApiUrl);
        Assert.Null(options.ApiKey);
        Assert.Equal(3000, options.RestartPauseMs);
    }

    [Fact]
    public void Options_NegativeRestartPause_IsClampedToZero()
    {
        var options = GluetunOptions.From(Config(new() { ["Crawler:VpnRestartPauseMs"] = "-5" }));

        Assert.Equal(0, options.RestartPauseMs);
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

    [Fact]
    public async Task AddGluetunControl_GateAndClientShareOneSetOfOptions()
    {
        // So wie Program.cs registriert: die Optionen einmal, der Steuer-Client und das Gate
        // bekommen dieselben Werte — der Wechsel geht an die konfigurierte URL, mit dem Key.
        var requests = new List<(HttpMethod Method, Uri Uri, string? ApiKey)>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Config(new()
        {
            ["Gluetun:ApiUrl"] = "http://gluetun.test:8000",
            ["Gluetun:ApiKey"] = "geheim-123",
            ["Crawler:VpnRestartPauseMs"] = "0",
        }));
        services.AddGluetunControl();
        services.AddHttpClient("Gluetun").ConfigurePrimaryHttpMessageHandler(() => new StubHandler(req =>
        {
            lock (requests)
                requests.Add((req.Method, req.RequestUri!,
                    req.Headers.TryGetValues("X-API-Key", out var v) ? v.Single() : null));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        services.AddSingleton<VpnReadinessGate>();
        await using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<GluetunOptions>>();
        Assert.Same(options.Value, provider.GetRequiredService<IOptions<GluetunOptions>>().Value);
        Assert.Equal("http://gluetun.test:8000", options.Value.ApiUrl);
        Assert.Equal(0, options.Value.RestartPauseMs);

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("Gluetun");
        Assert.Equal(TimeSpan.FromSeconds(5), client.Timeout);

        Assert.True(await provider.GetRequiredService<VpnReadinessGate>().RotateAsync(CancellationToken.None));
        lock (requests)
        {
            var puts = requests.Where(r => r.Method == HttpMethod.Put).ToList();
            Assert.Equal(2, puts.Count);
            Assert.All(puts, r =>
            {
                Assert.Equal("http://gluetun.test:8000/v1/vpn/status", r.Uri.ToString());
                Assert.Equal("geheim-123", r.ApiKey);
            });
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
