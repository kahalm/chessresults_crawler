namespace ChessResultsCrawler.Tests.Integration;

/// <summary>
/// Wie <see cref="FactAttribute"/>, überspringt den Test aber, wenn keine MariaDB konfiguriert ist —
/// <c>dotnet test</c> bleibt ohne Docker grün, nur die Integrationstests melden sich als übersprungen.
///
/// Verbindungszeichenfolge über die Umgebungsvariable CRAWLER_TEST_MYSQL, z. B.
///   server=127.0.0.1;port=33991;user=root;password=test
/// OHNE database= — jeder Test legt sich sein eigenes Schema an und räumt es wieder weg.
/// </summary>
public sealed class MySqlFactAttribute : FactAttribute
{
    public const string EnvVar = "CRAWLER_TEST_MYSQL";

    public static string? ConnectionBase => Environment.GetEnvironmentVariable(EnvVar);

    public MySqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(ConnectionBase))
            Skip = $"{EnvVar} nicht gesetzt - keine MariaDB für den Integrationstest.";
    }
}
