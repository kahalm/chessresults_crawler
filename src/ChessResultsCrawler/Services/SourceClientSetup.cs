namespace ChessResultsCrawler.Services;

/// <summary>
/// Die HttpClients der Verbandsquellen (<c>*CalendarService</c>) — an EINER Stelle, damit eine
/// Haertung alle Quellen erreicht und eine neue Quelle nichts mehr kopiert.
///
/// <para>Jede Quelle bekommt einen eigenen Client, weil der Host ein anderer ist und der
/// SSRF-Schutz des <see cref="CrawlerService"/> auf chess-results.com prueft; den Host prueft
/// jeder Dienst selbst gegen seine <c>AllowedHost</c>-Konstante. Gemeinsam ist allen:</para>
/// <list type="bullet">
/// <item>derselbe <see cref="UserAgent"/>;</item>
/// <item>derselbe primaere Handler (<see cref="CrawlHttpHandler.Create"/>): die Clients laufen durch
/// denselben Tunnel (<c>network_mode: service:gluetun</c>) und treffen nach einer Rotation dieselben
/// toten Verbindungen. Redirects werden auch hier NICHT automatisch verfolgt — ein 3xx kommt als
/// Antwort zurueck und scheitert an <c>EnsureSuccessStatusCode</c>, statt blind irgendwohin zu
/// laufen;</item>
/// <item>die Wiederholung ueber einen anderen VPN-Ausgang (<see cref="RotateOnConnectFailureHandler"/>)
/// mit dem Zeitlimit JE VERSUCH. <c>HttpClient.Timeout</c> gilt dagegen fuer alle Versuche
/// ZUSAMMEN (mit dem Limit je Versuch liess es die Wiederholung nie zum Zug kommen) und steht
/// deshalb auf <see cref="ClientTimeout"/>: alle Versuche, alle Wechsel und ein Fenster fuer den
/// Rumpf;</item>
/// <item>derselbe Deckel fuer den gepufferten Rumpf (<see cref="MaxResponseBytes"/>).</item>
/// </list>
/// <para>Je Quelle unterscheidet sich nur das Zeitlimit je Versuch.</para>
/// </summary>
internal static class SourceClientSetup
{
    /// <summary>
    /// User-Agent aller Quellen-Abrufe.
    ///
    /// <para>ACHTUNG: der slowenische Server (<see cref="SzsCalendarService"/>) weist jede
    /// Zeichenfolge „bot" ab — auch Googlebot und bingbot, es ist also ein kopierter
    /// nginx-Schnipsel und keine ueberlegte Absage. Unser Name enthaelt „Crawler" und kommt
    /// durch; wer ihn aendert, sollte das vorher pruefen.</para>
    /// </summary>
    internal const string UserAgent = "ChessResultsCrawler/1.0 (+RookHub)";

    /// <summary>
    /// Deckel fuer den gepufferten Antwortrumpf (<c>HttpClient.MaxResponseContentBufferSize</c>)
    /// — wie der 32-MB-Deckel des chess-results-Pfads im <see cref="CrawlerService"/>. Ohne ihn
    /// puffert der Client bis 2 GB: eine fehlkonfigurierte oder uebernommene Verbandsseite mit
    /// riesigem oder endlosem Rumpf trieb den Container in den Speichertod und riss chess-results-
    /// Crawls, Rundenmonitor und LeagueHub mit. Die groesste gemessene Quelle (Italien) liefert rund
    /// 1,25 MB. Darueber wirft der Client eine <see cref="HttpRequestException"/> (→ 502).
    /// </summary>
    internal const long MaxResponseBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Obergrenze fuer EINEN Ausgangswechsel (<see cref="VpnReadinessGate.RotateAsync"/>): bis 60 s
    /// Warten auf den Crawl-Riegel, dann stop→Pause→start unter hoechstens 33 s (Vorgabe) und im
    /// Fehlerfall noch ein Recovery-start mit 30 s — zusammen 123 s, der Rest ist Reserve fuer eine
    /// laengere <c>Crawler:VpnRestartPauseMs</c>.
    /// </summary>
    internal static readonly TimeSpan RotationBudget = TimeSpan.FromSeconds(150);

    /// <summary>
    /// <c>HttpClient.Timeout</c> eines Quellen-Clients: alle <see cref="RotateOnConnectFailureHandler.MaxAttempts"/>
    /// Versuche mit ihrem Zeitlimit, die Wechsel dazwischen und EIN weiteres Versuchs-Fenster fuer das
    /// Lesen des Rumpfs. Das Limit je Versuch endet mit den Kopfzeilen; den Rumpf puffert der Client
    /// danach allein unter diesem Deckel — ohne ihn (vorher: unbegrenzt) hing ein troepfelnder Rumpf,
    /// bis RookHubs Aufrufer abbrach. Das Extra-Fenster sorgt dafuer, dass der Deckel auch nach vier
    /// ausgeschoepften Fehlversuchen die erfolgreiche Wiederholung nicht abschneidet.
    /// </summary>
    internal static TimeSpan ClientTimeout(int attemptSeconds) =>
        TimeSpan.FromSeconds(attemptSeconds) * (RotateOnConnectFailureHandler.MaxAttempts + 1)
        + RotationBudget * (RotateOnConnectFailureHandler.MaxAttempts - 1);

    /// <summary>Registriert die Clients aller Verbandsquellen.</summary>
    public static IServiceCollection AddSourceClients(this IServiceCollection services)
    {
        services.AddSourceClient<FideCalendarService>(30);

        // Italien: die Trefferliste traegt ALLE Felder inline und ist damit rund 1,25 MB fuer
        // 283 Turniere.
        services.AddSourceClient<FsiCalendarService>(90);

        // Slowenien — siehe die Warnung zum User-Agent.
        services.AddSourceClient<SzsCalendarService>(30);

        // Slowakei: ein Durchgang holt die Detailseite JE TURNIER nach (mit Pause dazwischen).
        services.AddSourceClient<ChessSkCalendarService>(30);

        // Ungarn: GROSSZUEGIGER Zeitrahmen, der Endpunkt braucht fuer seine 31 kB rund 75 Sekunden
        // (am 2026-09-08 gemessen) — mit dem ueblichen halben Minuten-Limit saehe die Quelle wie ein
        // Dauerausfall aus.
        services.AddSourceClient<ChessHuCalendarService>(180);

        // Tschechien: ein Abruf, aber ein grosser (rund 300 kB HTML fuer 89 Eintraege in drei
        // Laschen).
        services.AddSourceClient<ChessCzCalendarService>(60);

        // Polen: alte Infrastruktur (PHP 5.2), deshalb defensiv — ein Abruf fuer die Liste, und die
        // Detailseiten holt der Aufrufer einzeln und gedeckelt.
        services.AddSourceClient<ChessArbiterCalendarService>(60);

        // Deutscher Schachbund: ein Durchgang holt zwei Seiten je Region und haelt dazwischen die
        // Wartezeit ein, die die robots.txt nennt.
        services.AddSourceClient<SchachbundCalendarService>(45);

        // England: zwei geblaetterte Endpunkte je Durchgang, dazwischen die Wartezeit aus der
        // robots.txt der Quelle.
        services.AddSourceClient<EcfCalendarService>(60);

        // Die Quellen der dritten Runde. Frankreich holt in EINER eingehenden Anfrage zwoelf
        // Monatsseiten, Irland fuenf Listenseiten mit Pause dazwischen.
        services.AddSourceClient<IcuCalendarService>(60);
        services.AddSourceClient<FfeCalendarService>(60);
        services.AddSourceClient<SjakkCalendarService>(60);
        services.AddSourceClient<ChessScotlandCalendarService>(60);

        // Kanada: zwei Abrufe (Seite fuer den Dateinamen, dann die Datei). Keine Wartezeit in der
        // robots.txt — sie ist woertlich nur „User-agent: *" ohne eine einzige Regel.
        services.AddSourceClient<CfcCalendarService>(60);

        services.AddSourceClient<WcuCalendarService>(60);

        // Die Niederlande verlangen in ihrer robots.txt 15 Sekunden zwischen zwei Abrufen. Bei zwei
        // Seiten ist das ein Durchgang von rund 17 Sekunden — der Zeitrahmen muss das aushalten.
        services.AddSourceClient<KnsbCalendarService>(90);

        services.AddSourceClient<FrsahCalendarService>(60);

        return services;
    }

    /// <summary>
    /// Ein Quellen-Client: <see cref="UserAgent"/>, Client-Zeitlimit <see cref="ClientTimeout"/>,
    /// Rumpf-Deckel <see cref="MaxResponseBytes"/>, <see cref="CrawlHttpHandler"/> und die
    /// Wiederholung ueber einen anderen VPN-Ausgang mit <paramref name="attemptSeconds"/> Sekunden
    /// je Versuch.
    /// </summary>
    public static IHttpClientBuilder AddSourceClient<TClient>(this IServiceCollection services, int attemptSeconds)
        where TClient : class =>
        services.AddHttpClient<TClient>(client =>
            {
                client.DefaultRequestHeaders.Add("User-Agent", UserAgent);
                client.Timeout = ClientTimeout(attemptSeconds);
                client.MaxResponseContentBufferSize = MaxResponseBytes;
            })
            .ConfigurePrimaryHttpMessageHandler(CrawlHttpHandler.Create)
            .WithExitRotationRetry(attemptSeconds);

    /// <summary>
    /// Haengt die Wiederholung ueber einen anderen VPN-Ausgang an einen Quellen-Client und legt das
    /// Zeitlimit JE VERSUCH fest. Das Client-Zeitlimit gilt fuer alle Versuche zusammen und liegt
    /// deshalb weit darueber (<see cref="ClientTimeout"/>) — sonst haette es die Wiederholung
    /// ausgehebelt.
    /// </summary>
    internal static IHttpClientBuilder WithExitRotationRetry(this IHttpClientBuilder builder, int attemptSeconds) =>
        builder.AddHttpMessageHandler(sp => new RotateOnConnectFailureHandler(
            sp.GetRequiredService<VpnReadinessGate>(),
            sp.GetRequiredService<ILogger<RotateOnConnectFailureHandler>>(),
            TimeSpan.FromSeconds(attemptSeconds)));
}
