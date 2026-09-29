namespace ChessResultsCrawler.Tests;

/// <summary>
/// Die Wurzel des Repos (dort, wo <c>ChessResultsCrawler.sln</c> liegt) — fuer Waechter-Tests, die
/// Quelltext oder Workflow-Dateien lesen. Gesucht wird vom Testausgabe-Ordner aufwaerts; die Tests
/// laufen lokal und im CI im ausgecheckten Repo.
/// </summary>
internal static class RepoRoot
{
    public static string Path { get; } = Find();

    public static string File(params string[] parts) =>
        System.IO.Path.Combine([Path, .. parts]);

    private static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "ChessResultsCrawler.sln")))
                return dir.FullName;
        }
        throw new InvalidOperationException(
            $"ChessResultsCrawler.sln oberhalb von {AppContext.BaseDirectory} nicht gefunden");
    }
}
