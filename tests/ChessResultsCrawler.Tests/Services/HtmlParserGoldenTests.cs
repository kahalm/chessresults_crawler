using System.Text.Encodings.Web;
using System.Text.Json;
using ChessResultsCrawler.Services;

namespace ChessResultsCrawler.Tests.Services;

/// <summary>
/// Golden-Tests des <see cref="HtmlParserService"/>: JEDE Parse-Methode laeuft ueber JEDE
/// HTML-Probe (die echten chess-results-Fixtures plus die Proben unter <c>Fixtures/golden/</c>),
/// das Ergebnis wird als JSON mit der gespeicherten Fassung <c>Fixtures/golden/&lt;probe&gt;.json</c>
/// verglichen — byte-gleich. Gedacht als Netz fuer reine Umbauten (Zerlegung Z44): auch das
/// Verhalten einer Methode auf einer FREMDEN Seite (leere Liste, null, Ausnahme) ist festgehalten.
///
/// <para>Neu schreiben (nur wenn sich das Verhalten absichtlich aendert):
/// <c>UPDATE_GOLDEN=1 dotnet test --filter HtmlParserGoldenTests</c>.</para>
/// </summary>
public class HtmlParserGoldenTests
{
    private static readonly HtmlParserService Parser = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly (string Name, Func<string, Task<object?>> Run)[] Methods =
    [
        ("ParsePlayerListAsync", async h => await Parser.ParsePlayerListAsync(h)),
        ("ParseTeamPairingsAsync", async h => await Parser.ParseTeamPairingsAsync(h)),
        ("ParseIndividualPairingsAsync", async h => await Parser.ParseIndividualPairingsAsync(h)),
        ("IsTeamPairingsPageAsync", async h => await Parser.IsTeamPairingsPageAsync(h)),
        ("HasPairingsTableAsync", async h => await Parser.HasPairingsTableAsync(h)),
        ("ParseTotalRoundsAsync", async h => await Parser.ParseTotalRoundsAsync(h)),
        ("ParseAvailableRoundsAsync", async h => await Parser.ParseAvailableRoundsAsync(h)),
        ("ParseAvailableRoundsAsync(max=5)", async h => await Parser.ParseAvailableRoundsAsync(h, 5)),
        ("ParseTournamentNameAsync", async h => await Parser.ParseTournamentNameAsync(h)),
        ("ParseTournamentDetailsAsync", async h => await Parser.ParseTournamentDetailsAsync(h)),
        ("ParsePlayerSearchAsync", async h => await Parser.ParsePlayerSearchAsync(h)),
        ("ParsePlayerTournamentsAsync", async h => await Parser.ParsePlayerTournamentsAsync(h)),
        ("ParsePlayerDetailPageAsync", async h => await Parser.ParsePlayerDetailPageAsync(h)),
        ("ParseRoundPlanAsync", async h => await Parser.ParseRoundPlanAsync(h)),
        ("ParsePlayerCardAsync", async h => await Parser.ParsePlayerCardAsync(h)),
        ("ParseTeamNamesAsync", async h => await Parser.ParseTeamNamesAsync(h)),
        ("ParseCalendarAsync", async h => await Parser.ParseCalendarAsync(h)),
        ("ParseTournamentSearchAsync", async h => await Parser.ParseTournamentSearchAsync(h)),
    ];

    private static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private static string GoldenSourceDir =>
        RepoRoot.File("tests", "ChessResultsCrawler.Tests", "Fixtures", "golden");

    public static TheoryData<string> Probes()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(FixtureDir, "*.html")
                     .Concat(Directory.GetFiles(Path.Combine(FixtureDir, "golden"), "*.html"))
                     .Select(f => Path.GetRelativePath(FixtureDir, f).Replace('\\', '/'))
                     .Order(StringComparer.Ordinal))
            data.Add(file);
        return data;
    }

    [Theory]
    [MemberData(nameof(Probes))]
    public async Task EveryParseMethod_MatchesGolden(string probe)
    {
        var html = await File.ReadAllTextAsync(Path.Combine(FixtureDir, probe));
        var results = new Dictionary<string, object?>();
        foreach (var (name, run) in Methods)
        {
            try { results[name] = await run(html); }
            catch (Exception ex) { results[name] = $"EXCEPTION {ex.GetType().FullName}: {ex.Message}"; }
        }
        AssertGolden(Path.GetFileNameWithoutExtension(probe) + ".json", results);
    }

    [Fact]
    public void StaticHelpers_MatchGolden()
    {
        string?[] dates = ["2026/12/18", "18.12.2026", "2026-12-18", "18.12.26", "", null, " 2026/01/02 "];
        string?[] ages = ["16 Days", "1 Days 2 Hours", "8 Tage 23 Std.", "3 Hours 36 Min.", "7 Minutes",
            "12 Sekunden", "5 Wochen", "", null, "kein Alter"];
        string[] urls = ["https://chess-results.com/s2/tnr1.aspx", "https://s3.chess-results.com/tnr1.aspx",
            "http://chess-results.com/s12/x", "https://example.org/s1/"];
        var results = new Dictionary<string, object?>
        {
            ["ParseSearchDate"] = dates.Select(d => new { In = d, Out = HtmlParserService.ParseSearchDate(d) }),
            ["ParseRelativeAge"] = ages.Select(a => new { In = a, Out = HtmlParserService.ParseRelativeAge(a) }),
            ["ExtractSNode"] = urls.Select(u => new { In = u, Out = HtmlParserService.ExtractSNode(u) }),
        };
        AssertGolden("_static-helpers.json", results);
    }

    private static void AssertGolden(string goldenName, object results)
    {
        var actual = JsonSerializer.Serialize(results, Json).ReplaceLineEndings("\n") + "\n";
        var path = Path.Combine(GoldenSourceDir, goldenName);
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            File.WriteAllText(path, actual);
            return;
        }
        Assert.True(File.Exists(path), $"Golden fehlt: {path} (UPDATE_GOLDEN=1 setzen)");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }
}
