using System.Net;
using ChessResultsCrawler.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace ChessResultsCrawler.Tests.Services;

public class VpnReadinessGateTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static IHttpClientFactory Factory(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var client = new HttpClient(new StubHandler(handler));
        return Mock.Of<IHttpClientFactory>(f => f.CreateClient("Gluetun") == client);
    }

    [Fact]
    public async Task WaitUntilReadyAsync_Disabled_ReturnsImmediately()
    {
        // WaitForReady nicht gesetzt (= false): das Gate darf NICHT probieren/blockieren,
        // auch wenn der Handler werfen würde (kein VPN in Dev).
        var probed = false;
        var gate = new VpnReadinessGate(
            Factory(_ => { probed = true; throw new HttpRequestException("should not be called"); }),
            Config(new() { ["Gluetun:WaitForReady"] = "false" }),
            Mock.Of<ILogger<VpnReadinessGate>>());

        await gate.WaitUntilReadyAsync(CancellationToken.None);

        Assert.False(probed);
    }

    [Fact]
    public async Task WaitUntilReadyAsync_TunnelReady_Completes()
    {
        var gate = new VpnReadinessGate(
            Factory(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"public_ip":"141.98.102.179"}""")
            }),
            Config(new()
            {
                ["Gluetun:WaitForReady"] = "true",
                ["Gluetun:ReadyTimeoutSeconds"] = "5",
                ["Gluetun:ReadyPollSeconds"] = "0"
            }),
            Mock.Of<ILogger<VpnReadinessGate>>());

        // Sollte praktisch sofort zurückkehren (Public-IP beim ersten Versuch da).
        await gate.WaitUntilReadyAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WaitUntilReadyAsync_NeverReady_ProceedsAfterTimeout()
    {
        var gate = new VpnReadinessGate(
            Factory(_ => throw new HttpRequestException("Resource temporarily unavailable")),
            Config(new()
            {
                ["Gluetun:WaitForReady"] = "true",
                ["Gluetun:ReadyTimeoutSeconds"] = "1",
                ["Gluetun:ReadyPollSeconds"] = "0"
            }),
            Mock.Of<ILogger<VpnReadinessGate>>());

        // Darf nicht ewig hängen: nach dem (kurzen) Timeout fortfahren.
        var task = gate.WaitUntilReadyAsync(CancellationToken.None);
        var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(task, finished);
        await task;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(_handler(request));
    }
}
