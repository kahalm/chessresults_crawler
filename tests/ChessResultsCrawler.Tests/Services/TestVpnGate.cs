using ChessResultsCrawler.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ChessResultsCrawler.Tests.Services;

/// <summary>
/// Ein <see cref="VpnReadinessGate"/> fuer Tests, die mit dem VPN nichts zu tun haben. Seit die
/// Rotation dort liegt, braucht der <c>CrawlerService</c> das Gate im Konstruktor — die Tests
/// sollen deshalb nicht jeder ein eigenes bauen muessen.
/// </summary>
internal static class TestVpnGate
{
    /// <summary>Ein Gate, das nie benutzt wird (Rotation aus: `RotateAfterRequests` steht in
    /// diesen Tests hoch). Sein gluetun-Client wirft, damit ein versehentlicher Aufruf auffaellt
    /// statt still ins Netz zu gehen.</summary>
    public static VpnReadinessGate Unused()
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(new ThrowingHandler()));
        return From(factory.Object, new ConfigurationBuilder().Build());
    }

    /// <summary>Ein Gate auf der uebergebenen Client-Fabrik — fuer die Tests, die den
    /// Tunnel-Neustart selbst beobachten.</summary>
    public static VpnReadinessGate From(IHttpClientFactory factory, IConfiguration configuration) =>
        new(factory, configuration, Options.Create(GluetunOptions.From(configuration)),
            NullLogger<VpnReadinessGate>.Instance);

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            throw new InvalidOperationException(
                "Dieser Test wollte keinen VPN-Zugriff — siehe TestVpnGate.Unused");
    }
}
