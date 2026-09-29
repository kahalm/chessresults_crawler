using ChessResultsCrawler.Services;

namespace ChessResultsCrawler.Tests.Services;

/// <summary>
/// Der gemeinsame SSRF-Schutz der Verbandsquellen. Die Fehlertexte sind dieselben wie vorher in den
/// 17 Kopien von <c>EnsureAllowedTarget</c>; die Tests je Quelle (<c>*CalendarServiceTests</c>)
/// pruefen weiter ueber die Weiterleitung des jeweiligen Dienstes.
/// </summary>
public class SourceHostGuardTests
{
    [Theory]
    [InlineData("https://chess.hu/app/versenynaptar.json")]
    [InlineData("https://CHESS.HU/app/versenynaptar.json")]
    [InlineData("https://chess.hu:443/x")]
    public void Ensure_HttpsAndExactHost_Passes(string url)
    {
        SourceHostGuard.Ensure(new Uri(url), "chess.hu");
    }

    [Fact]
    public void Ensure_NonHttps_ThrowsWithTarget()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => SourceHostGuard.Ensure(new Uri("http://chess.hu/app/versenynaptar.json"), "chess.hu"));
        Assert.Equal("Refusing non-https target: http://chess.hu/app/versenynaptar.json", ex.Message);
    }

    [Theory]
    [InlineData("https://www.chess.hu/", "www.chess.hu")]
    [InlineData("https://evilchess.hu/", "evilchess.hu")]
    [InlineData("https://chess.hu.evil.example/", "chess.hu.evil.example")]
    [InlineData("https://chess.hu@evil.example/", "evil.example")]
    [InlineData("https://127.0.0.1/", "127.0.0.1")]
    public void Ensure_OtherHost_ThrowsWithHost(string url, string host)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => SourceHostGuard.Ensure(new Uri(url), "chess.hu"));
        Assert.Equal($"Refusing unexpected host: {host}", ex.Message);
    }

    [Fact]
    public void CalendarServices_ForwardToTheGuard_InsteadOfCarryingOwnCopies()
    {
        // Eine neue Quelle soll die Pruefung nicht wieder abschreiben: jede *CalendarService.cs
        // leitet an SourceHostGuard weiter und traegt die Fehlertexte nicht selbst.
        var files = Directory.GetFiles(RepoRoot.File("src", "ChessResultsCrawler", "Services"), "*CalendarService.cs");
        Assert.Equal(17, files.Length);
        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            Assert.True(source.Contains("SourceHostGuard.Ensure(url, AllowedHost)"),
                $"{Path.GetFileName(file)} leitet EnsureAllowedTarget nicht an SourceHostGuard weiter");
            Assert.False(source.Contains("Refusing unexpected host") || source.Contains("Refusing non-https"),
                $"{Path.GetFileName(file)} traegt eine eigene Kopie der Host-Pruefung");
        }
    }
}
