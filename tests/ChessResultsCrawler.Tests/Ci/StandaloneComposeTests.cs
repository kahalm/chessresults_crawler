using System.Text.RegularExpressions;
using ChessResultsCrawler.Middleware;

namespace ChessResultsCrawler.Tests.Ci;

/// <summary>
/// Wacht ueber die Standalone-Vorlage <c>docker-compose.yml</c> (Review W4s S3-013). Sie wird in Prod/Dev nicht
/// benutzt, aber CLAUDE.md und README empfehlen sie fuer „nur Crawler + eigene DB". Wer ihr auf einem Server mit
/// oeffentlicher IP folgt, darf weder MariaDB noch das ungeschuetzte Elasticsearch/Kibana ins Netz stellen, und die
/// API muss mit Schluessel starten (ohne antwortet ApiKeyMiddleware mit 503). Gelesen wird zeilenweise wie in
/// <see cref="DockerWorkflowTests"/> — kein YAML-Paket noetig, die Datei ist flach.
/// </summary>
public class StandaloneComposeTests
{
    private static string[] ComposeLines() =>
        File.ReadAllText(RepoRoot.File("docker-compose.yml")).Replace("\r", "").Split('\n');

    /// <summary>Die Zeilen je Dienst unter <c>services:</c> (Einrueckung 2), bis zum naechsten Dienst oder Top-Level-Schluessel.</summary>
    private static Dictionary<string, string[]> Services()
    {
        var lines = ComposeLines();
        var start = Array.FindIndex(lines, l => l.TrimEnd() == "services:");
        Assert.True(start >= 0, "services: fehlt");
        var result = new Dictionary<string, string[]>();
        string? name = null;
        var body = new List<string>();
        foreach (var line in lines.Skip(start + 1))
        {
            if (Regex.IsMatch(line, @"^\S")) break; // naechster Top-Level-Schluessel (volumes:)
            var m = Regex.Match(line, @"^  ([A-Za-z0-9_-]+):\s*$");
            if (m.Success)
            {
                if (name is not null) result[name] = body.ToArray();
                name = m.Groups[1].Value;
                body = [];
                continue;
            }
            body.Add(line);
        }
        if (name is not null) result[name] = body.ToArray();
        return result;
    }

    /// <summary>Die Listeneintraege unter <c>ports:</c> eines Dienstes, ohne Anfuehrungszeichen.</summary>
    private static List<string> Ports(string[] service)
    {
        var start = Array.FindIndex(service, l => l.TrimEnd() == "    ports:");
        if (start < 0) return [];
        return service.Skip(start + 1)
            .TakeWhile(l => l.StartsWith("      - ", StringComparison.Ordinal))
            .Select(l => l.Trim()[2..].Trim().Trim('"', '\''))
            .ToList();
    }

    private static string? Value(string[] service, string key)
    {
        var line = service.FirstOrDefault(l => Regex.IsMatch(l, @"^\s+" + Regex.Escape(key) + @":\s"));
        return line?[(line.IndexOf(':') + 1)..].Trim();
    }

    [Fact]
    public void EveryPublishedPort_IsBoundToLoopback()
    {
        var services = Services();
        Assert.Contains("mariadb", services.Keys);
        Assert.Contains("gluetun", services.Keys);

        var published = services.SelectMany(s => Ports(s.Value).Select(p => (service: s.Key, port: p))).ToList();
        Assert.NotEmpty(published);
        var open = published.Where(p => !p.port.StartsWith("127.0.0.1:", StringComparison.Ordinal))
            .Select(p => $"{p.service}: {p.port}").ToList();
        Assert.True(open.Count == 0, "Ports ohne 127.0.0.1 (an alle Schnittstellen gebunden): " + string.Join(", ", open));
    }

    [Theory]
    [InlineData("elasticsearch")]
    [InlineData("kibana")]
    public void UnsecuredLogStack_StartsOnlyWithProfile(string service)
    {
        var lines = Services()[service];
        var profiles = Value(lines, "profiles");
        Assert.False(string.IsNullOrWhiteSpace(profiles), $"{service} ohne profiles: — startet bei jedem docker compose up");
    }

    [Fact]
    public void App_DoesNotHardwireTheProfiledElasticsearch()
    {
        // ES laeuft nur mit Profil; ein fest verdrahtetes http://elasticsearch:9200 wuerde ohne Profil ins Leere loggen.
        var url = Value(Services()["app"], "Elasticsearch__Url");
        Assert.NotNull(url);
        Assert.StartsWith("${", url);
    }

    [Fact]
    public void App_RequiresApiKey()
    {
        var app = Services()["app"];
        var apiKey = Value(app, "API_KEY");
        Assert.NotNull(apiKey);
        // ${VAR:?…}: Compose bricht ohne Wert ab, statt einen Crawler zu starten, der alles mit 503 beantwortet.
        Assert.Matches(@"^\$\{CRAWLER_API_KEY:\?[^}]*\}$", apiKey);
    }

    [Fact]
    public void EnvExample_DocumentsApiKey_AsRecognisedPlaceholder()
    {
        var line = File.ReadAllLines(RepoRoot.File(".env.example"))
            .FirstOrDefault(l => l.StartsWith("CRAWLER_API_KEY=", StringComparison.Ordinal));
        Assert.NotNull(line);
        var value = line["CRAWLER_API_KEY=".Length..];
        // Die Vorlage liegt im Repo: ihr Wert muss als Platzhalter erkannt werden (wie kein Schluessel → 503).
        Assert.True(ApiKeyMiddleware.IsPlaceholder(value), $"'{value}' wird von ApiKeyMiddleware nicht als Platzhalter erkannt");
    }

    [Fact]
    public void EveryImage_IsPinnedToATag()
    {
        var images = Services()
            .Select(s => (service: s.Key, image: Value(s.Value, "image")))
            .Where(s => s.image is not null)
            .ToList();
        Assert.Contains(images, i => i.service == "gluetun");
        foreach (var (service, image) in images)
        {
            var tag = image!.Contains(':') ? image[(image.LastIndexOf(':') + 1)..] : "";
            Assert.False(tag.Length == 0 || tag.Contains('/') || tag == "latest",
                $"{service}: Image '{image}' ohne feste Version (Watchtower zieht sonst auch Major-Spruenge)");
        }
    }
}
