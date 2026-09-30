using System.Xml.Linq;

namespace ChessResultsCrawler.Tests.Ci;

/// <summary>
/// Keine gleitenden NuGet-Versionen (Review W4s S3-014). Das Dockerfile macht bei jedem CI-Build ein frisches
/// <c>dotnet restore</c>; mit <c>8.*</c> loeste jeder main-Push/Tag das gerade neueste Release auf — zwei Builds
/// desselben Commits konnten verschiedene Abhaengigkeiten enthalten, und Watchtower rollt :latest nachts aus.
/// Eine feste Version macht ein Update zur bewussten Aenderung (so auch RookHub und piratechess: 8.19.0).
/// </summary>
public class PackageVersionTests
{
    [Fact]
    public void EveryPackageReference_HasAFixedVersion()
    {
        var projects = new[] { "src", "tests" }
            .SelectMany(dir => Directory.GetFiles(RepoRoot.File(dir), "*.csproj", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        Assert.Contains(projects, p => p.EndsWith("ChessResultsCrawler.csproj", StringComparison.Ordinal));

        var floating = projects
            .SelectMany(file => XDocument.Load(file).Descendants("PackageReference")
                .Select(p => (file: Path.GetFileName(file), id: (string?)p.Attribute("Include"),
                    version: (string?)p.Attribute("Version"))))
            .Where(p => p.version is null || p.version.Contains('*')
                        || p.version.StartsWith('[') || p.version.StartsWith('('))
            .Select(p => $"{p.file}: {p.id} {p.version}")
            .ToList();

        Assert.True(floating.Count == 0, "Gleitende PackageReference-Versionen: " + string.Join(", ", floating));
    }
}
