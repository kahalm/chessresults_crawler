using System.Text.RegularExpressions;
using ChessResultsCrawler.Services;

namespace ChessResultsCrawler.Tests.Services;

/// <summary>
/// Das gemeinsame <see cref="SourceText.Collapse"/> ersetzt zwoelf private Kopien in zwei
/// Schreibweisen (zehnmal <c>Regex \s+</c>, zweimal <c>Split</c> bei ICU und Chess Scotland). Die
/// Tests sichern, dass es fuer JEDES Zeichen dasselbe liefert wie beide alten Fassungen.
/// </summary>
public class SourceTextTests
{
    private static string OldRegex(string? text) => Regex.Replace(text ?? "", @"\s+", " ").Trim();

    private static string OldSplit(string? text) =>
        text is null ? "" : string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("  Open\r\n\t Graz  ", "Open Graz")]
    [InlineData("a\u00a0\u00a0b", "a b")]
    [InlineData("a\u2028b\u3000c", "a b c")]
    public void Collapse_JoinsWhitespaceRunsAndTrims(string? input, string expected)
    {
        Assert.Equal(expected, SourceText.Collapse(input));
    }

    [Fact]
    public void Collapse_MatchesBothOldVariants_ForEveryBmpCharacter()
    {
        for (var code = 0; code <= char.MaxValue; code++)
        {
            var c = (char)code;
            foreach (var input in new[] { $"{c}", $"a{c}{c}b", $"{c}x{c}", $" {c} y {c} " })
            {
                string actual = SourceText.Collapse(input), regex = OldRegex(input), split = OldSplit(input);
                if (actual != regex || actual != split)
                    Assert.Fail($"U+{code:X4}: \"{actual}\" vs Regex \"{regex}\" / Split \"{split}\"");
            }
        }
    }

    [Fact]
    public void ParserServices_UseTheSharedCollapse_InsteadOfOwnCopies()
    {
        var dir = RepoRoot.File("src", "ChessResultsCrawler", "Services");
        foreach (var file in Directory.GetFiles(dir, "*.cs"))
        {
            if (Path.GetFileName(file) == "SourceText.cs") continue;
            Assert.False(Regex.IsMatch(File.ReadAllText(file), @"\bstring\s+Collapse\s*\("),
                $"{Path.GetFileName(file)} definiert ein eigenes Collapse — SourceText.Collapse nutzen");
        }
    }
}
