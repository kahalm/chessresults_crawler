using System.Net;
using ChessResultsCrawler.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ChessResultsCrawler.Tests.Services;

/// <summary>
/// Sichert die gemeinsame Registrierung der Verbandsquellen (<see cref="SourceClientSetup"/>) ab:
/// je Quelle derselbe User-Agent, ein Client-Zeitlimit, das alle Versuche samt Wechseln abdeckt (statt
/// unbegrenzt), ein Deckel fuer den gepufferten Rumpf, der <see cref="CrawlHttpHandler"/> als primaerer
/// Handler und die Wiederholung mit dem Zeitlimit JE VERSUCH — so, wie es vorher 17× einzeln in
/// Program.cs stand. Die Werte der Tabelle stammen aus den alten Bloecken.
/// </summary>
public class SourceClientSetupTests
{
    /// <summary>Quelle → Zeitlimit je Versuch in Sekunden (vorher: <c>WithExitRotationRetry(n)</c>).</summary>
    private static readonly Dictionary<Type, int> Expected = new()
    {
        [typeof(FideCalendarService)] = 30,
        [typeof(FsiCalendarService)] = 90,
        [typeof(SzsCalendarService)] = 30,
        [typeof(ChessSkCalendarService)] = 30,
        [typeof(ChessHuCalendarService)] = 180,
        [typeof(ChessCzCalendarService)] = 60,
        [typeof(ChessArbiterCalendarService)] = 60,
        [typeof(SchachbundCalendarService)] = 45,
        [typeof(EcfCalendarService)] = 60,
        [typeof(IcuCalendarService)] = 60,
        [typeof(FfeCalendarService)] = 60,
        [typeof(SjakkCalendarService)] = 60,
        [typeof(ChessScotlandCalendarService)] = 60,
        [typeof(CfcCalendarService)] = 60,
        [typeof(WcuCalendarService)] = 60,
        [typeof(KnsbCalendarService)] = 90,
        [typeof(FrsahCalendarService)] = 60,
    };

    public static TheoryData<Type, int> Sources()
    {
        var data = new TheoryData<Type, int>();
        foreach (var (type, seconds) in Expected) data.Add(type, seconds);
        return data;
    }

    private static ServiceProvider BuildProvider(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(TestVpnGate.Unused());
        services.AddSourceClients();
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static List<HttpMessageHandler> Chain(HttpMessageHandler handler)
    {
        var chain = new List<HttpMessageHandler> { handler };
        while (handler is DelegatingHandler { InnerHandler: { } inner })
        {
            chain.Add(inner);
            handler = inner;
        }
        return chain;
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public void AddSourceClients_RegistersSource_WithSharedSetupAndItsAttemptTimeout(Type source, int attemptSeconds)
    {
        using var provider = BuildProvider();

        // Der typisierte Dienst ist aufloesbar (AddHttpClient<T> registriert ihn).
        Assert.NotNull(provider.GetRequiredService(source));

        // Typisierte Clients heissen wie der Typ.
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(source.Name);
        Assert.Equal("ChessResultsCrawler/1.0 (+RookHub)", client.DefaultRequestHeaders.UserAgent.ToString());
        // Begrenzt statt unbegrenzt — aber so weit, dass alle Versuche, alle Wechsel dazwischen und
        // ein weiteres Versuchs-Fenster fuer den Rumpf hineinpassen.
        var max = RotateOnConnectFailureHandler.MaxAttempts;
        Assert.NotEqual(Timeout.InfiniteTimeSpan, client.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(attemptSeconds * (max + 1)) + (max - 1) * SourceClientSetup.RotationBudget,
            client.Timeout);
        Assert.True(client.Timeout > TimeSpan.FromSeconds(attemptSeconds * max) + (max - 1) * SourceClientSetup.RotationBudget);
        Assert.Equal(16L * 1024 * 1024, client.MaxResponseContentBufferSize);

        var chain = Chain(provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(source.Name));
        var retry = Assert.Single(chain.OfType<RotateOnConnectFailureHandler>());
        Assert.Equal(TimeSpan.FromSeconds(attemptSeconds), retry.AttemptTimeout);

        var primary = Assert.IsType<SocketsHttpHandler>(chain[^1]);
        Assert.False(primary.AllowAutoRedirect);
        Assert.Equal(CrawlHttpHandler.PoolTimeout, primary.PooledConnectionIdleTimeout);
        Assert.Equal(CrawlHttpHandler.PoolTimeout, primary.PooledConnectionLifetime);
        Assert.Equal(CrawlHttpHandler.ConnectTimeout, primary.ConnectTimeout);
    }

    /// <summary>
    /// Eine Quelle, die nach den Kopfzeilen einen riesigen Rumpf ohne Content-Length schickt: der
    /// Client bricht beim Deckel mit einer <see cref="HttpRequestException"/> ab (→ 502), statt bis
    /// 2 GB zu puffern. Der Rumpf hier ist gueltiges JSON — ohne Deckel kaeme eine leere Liste
    /// zurueck.
    /// </summary>
    [Fact]
    public async Task SourceClient_OversizedBody_AbortsAtTheCapInsteadOfBuffering()
    {
        const long bodyBytes = 20L * 1024 * 1024;
        var stub = new HugeJsonBodyHandler(bodyBytes);
        using var provider = BuildProvider(services => services
            .AddHttpClient(nameof(ChessHuCalendarService))
            .ConfigurePrimaryHttpMessageHandler(() => stub));

        var service = provider.GetRequiredService<ChessHuCalendarService>();

        await Assert.ThrowsAsync<HttpRequestException>(() => service.FetchAsync(new DateOnly(2026, 9, 1)));
        Assert.NotNull(stub.Body);
        Assert.True(stub.Body!.BytesRead < bodyBytes, $"{stub.Body.BytesRead} Bytes gelesen");
        Assert.True(stub.Body.BytesRead >= SourceClientSetup.MaxResponseBytes, $"{stub.Body.BytesRead} Bytes gelesen");
    }

    [Fact]
    public void AddSourceClients_CoversEveryCalendarServiceInTheCrawler()
    {
        // Eine neue Quelle gehoert in AddSourceClients (und in die Tabelle oben) — nicht als
        // eigener AddHttpClient-Block nach Program.cs kopiert.
        var calendarServices = typeof(SourceClientSetup).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.Name.EndsWith("CalendarService", StringComparison.Ordinal))
            .OrderBy(t => t.Name)
            .ToList();

        Assert.Equal(Expected.Keys.OrderBy(t => t.Name), calendarServices);

        using var provider = BuildProvider();
        var handlers = provider.GetRequiredService<IHttpMessageHandlerFactory>();
        foreach (var source in calendarServices)
            Assert.Single(Chain(handlers.CreateHandler(source.Name)).OfType<RotateOnConnectFailureHandler>());
    }

    /// <summary>Antwortet 200 mit <c>[ … ]</c> aus <paramref name="bytes"/> Bytes, OHNE Content-Length.</summary>
    private sealed class HugeJsonBodyHandler(long bytes) : HttpMessageHandler
    {
        public CountingJsonStream? Body { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Body = new CountingJsonStream(bytes);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(Body) });
        }
    }

    /// <summary>Ein nicht suchbarer Strom „[“ + Leerzeichen + „]“, der mitzaehlt, wie viel gelesen wurde.</summary>
    private sealed class CountingJsonStream(long length) : Stream
    {
        public long BytesRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, length - BytesRead);
            for (var i = 0; i < n; i++)
            {
                var pos = BytesRead + i;
                buffer[offset + i] = pos == 0 ? (byte)'[' : pos == length - 1 ? (byte)']' : (byte)' ';
            }
            BytesRead += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
