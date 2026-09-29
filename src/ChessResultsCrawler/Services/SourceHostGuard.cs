namespace ChessResultsCrawler.Services;

/// <summary>
/// Der SSRF-Schutz der Verbandsquellen (<c>*CalendarService</c>): https und ein EXAKTER
/// Hostvergleich (Gross-/Kleinschreibung egal), vor jedem Abruf. Jeder Dienst ruft ihn mit seiner
/// eigenen <c>AllowedHost</c>-Konstante auf. Redirects verfolgen die Quellen-Clients nicht
/// automatisch (siehe <see cref="SourceClientSetup"/>) — ein 3xx kaeme als Antwort zurueck und
/// scheiterte an <c>EnsureSuccessStatusCode</c>, statt blind irgendwohin zu laufen.
/// </summary>
internal static class SourceHostGuard
{
    /// <exception cref="InvalidOperationException">Kein https oder ein anderer Host als
    /// <paramref name="allowedHost"/>.</exception>
    internal static void Ensure(Uri url, string allowedHost)
    {
        if (url.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"Refusing non-https target: {url}");

        if (!url.Host.Equals(allowedHost, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing unexpected host: {url.Host}");
    }
}
