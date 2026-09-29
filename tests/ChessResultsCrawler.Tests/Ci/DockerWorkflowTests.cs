using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ChessResultsCrawler.Tests.Ci;

/// <summary>
/// Wacht ueber den Release-Weg in <c>.github/workflows/docker.yml</c>: ein Tag setzt <c>:latest</c>,
/// und Watchtower rollt <c>:latest</c> nachts auf Prod aus. Deshalb darf nur ein reiner
/// Release-Tag vX.Y.Z auf einem Commit, der auf main liegt, zu <c>:latest</c> werden — nicht ein
/// Sicherungs-Tag wie „vorher-umbau" und nicht ein Semver-Tag auf einem ungemergten Branch
/// (Review W2 I1-004). Ein Fehler hier zeigt sich nicht als roter Lauf, sondern als falsches
/// Image auf Prod — deshalb ein Test und nicht nur ein Kommentar.
/// </summary>
public class DockerWorkflowTests
{
    private static string Workflow() => File.ReadAllText(RepoRoot.File(".github", "workflows", "docker.yml"));

    /// <summary>Die Zeilen des Jobs <c>build-crawler</c> (bis zum naechsten Job gleicher Einrueckung).</summary>
    private static string[] BuildJob()
    {
        var lines = Workflow().Split('\n');
        var start = Array.FindIndex(lines, l => l.TrimEnd() == "  build-crawler:");
        Assert.True(start >= 0, "Job build-crawler fehlt");
        var end = Array.FindIndex(lines, start + 1, l => Regex.IsMatch(l, @"^  [A-Za-z0-9_-]+:\s*$"));
        return lines[start..(end < 0 ? lines.Length : end)];
    }

    /// <summary>Index der Zeile, mit der der Schritt beginnt, der <paramref name="marker"/> enthaelt.</summary>
    private static int StepIndex(string[] job, string marker)
    {
        var line = Array.FindIndex(job, l => l.Contains(marker, StringComparison.Ordinal));
        Assert.True(line >= 0, $"Schritt mit '{marker}' fehlt in build-crawler");
        while (line >= 0 && !job[line].TrimStart().StartsWith("- ", StringComparison.Ordinal)) line--;
        return line;
    }

    /// <summary>Minimaler Nachbau der GitHub-Filtermuster (*, **, +, ?, [..]) fuer Tag-Namen.</summary>
    private static bool FilterMatches(string pattern, string refName)
    {
        var rx = new StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*') { rx.Append(".*"); i++; }
            else if (c == '*') rx.Append("[^/]*");
            else if (c is '+' or '?') rx.Append(c);
            else if (c == '[') { var j = pattern.IndexOf(']', i); rx.Append(pattern, i, j - i + 1); i = j; }
            else rx.Append(Regex.Escape(c.ToString()));
        }
        return Regex.IsMatch(refName, rx.Append('$').ToString());
    }

    private static List<string> TagPatterns()
    {
        var m = Regex.Match(Workflow(), @"(?m)^    tags:\s*\[(?<list>.*)\]\s*(?:#.*)?$");
        Assert.True(m.Success, "on.push.tags fehlt");
        return m.Groups["list"].Value.Split(',')
            .Select(p => p.Trim().Trim('\'', '"'))
            .Where(p => p.Length > 0)
            .ToList();
    }

    [Theory]
    [InlineData("v0.22.1")]
    [InlineData("v1.0.0")]
    [InlineData("v10.20.30")]
    public void ReleaseTags_TriggerTheWorkflow(string tag)
    {
        Assert.Contains(TagPatterns(), p => FilterMatches(p, tag));
    }

    [Theory]
    [InlineData("vorher-umbau")]
    [InlineData("v-test")]
    [InlineData("v1.2")]
    [InlineData("v1.2.3-rc1")]
    [InlineData("v2.20.1-1")]
    [InlineData("version")]
    public void OtherTags_DoNotTriggerTheWorkflow(string tag)
    {
        Assert.DoesNotContain(TagPatterns(), p => FilterMatches(p, tag));
    }

    [Fact]
    public void Latest_OnlyViaReleaseGuard()
    {
        var text = Workflow();
        Assert.DoesNotContain("startsWith(github.ref, 'refs/tags/v')", text);
        Assert.Contains("type=raw,value=latest,enable=${{ steps.release.outputs.release == 'true' }}", text);
        // Sonst haengt metadata-action bei type=semver (flavor latest=auto) :latest an der Pruefung vorbei an.
        Assert.Matches(@"(?m)^\s+flavor: \|\s*\n\s+latest=false\s*$", text);
    }

    [Fact]
    public void Guard_RunsBeforeLoginMetadataAndBuild_WithFullHistory()
    {
        var job = BuildJob();
        var checkout = StepIndex(job, "uses: actions/checkout@");
        var guard = StepIndex(job, "id: release");
        var login = StepIndex(job, "uses: docker/login-action@");
        var meta = StepIndex(job, "uses: docker/metadata-action@");
        var build = StepIndex(job, "uses: docker/build-push-action@");

        Assert.True(checkout < guard && guard < login && login < meta && meta < build,
            $"Reihenfolge checkout {checkout} < guard {guard} < login {login} < meta {meta} < build {build}");
        Assert.Contains(job[checkout..guard], l => l.Trim() == "fetch-depth: 0");
        Assert.Contains(job[guard..login], l => l.Trim() == "if: github.ref_type == 'tag'");
    }

    // ----- Das Guard-Skript echt ausgefuehrt, in einem Wegwerf-Repo --------------------------------

    /// <summary>Der <c>run: |</c>-Block des Schritts <c>id: release</c>, ausgerueckt.</summary>
    private static string GuardScript()
    {
        var job = BuildJob();
        var guard = StepIndex(job, "id: release");
        var run = Array.FindIndex(job, guard, l => l.Trim() == "run: |");
        Assert.True(run > guard, "Guard-Schritt ohne run-Block");
        var indent = job[run].Length - job[run].TrimStart().Length;
        var body = job.Skip(run + 1)
            .TakeWhile(l => l.Trim().Length == 0 || l.Length - l.TrimStart().Length > indent)
            .ToList();
        var bodyIndent = body.Where(l => l.Trim().Length > 0).Min(l => l.Length - l.TrimStart().Length);
        var script = string.Join('\n', body.Select(l => l.Length >= bodyIndent ? l[bodyIndent..] : l.Trim()));
        Assert.DoesNotContain("${{", script); // ohne Actions-Ausdruecke lauffaehig (nur Env-Variablen)
        return script;
    }

    private static (int Exit, string Output) Run(string file, string cwd, IDictionary<string, string> env,
        params string[] args)
    {
        var psi = new ProcessStartInfo(file)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, stdout.Result + stderr.Result);
    }

    private static string Git(string cwd, IDictionary<string, string> env, params string[] args)
    {
        var (exit, output) = Run("git", cwd, env, args);
        Assert.True(exit == 0, $"git {string.Join(' ', args)}: {output}");
        return output.Trim();
    }

    /// <summary>
    /// origin (bare) mit main und einem ungemergten Branch feature; Tags: v1.0.0 auf einem aelteren
    /// main-Stand, v1.1.0 auf der main-Spitze, v9.9.9 auf feature, vorher-umbau auf main.
    /// </summary>
    private sealed class ScratchRepo : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("crawler-release-guard-").FullName;
        public string Work => Path.Combine(Root, "work");

        public Dictionary<string, string> Env { get; } = new()
        {
            ["GIT_CONFIG_GLOBAL"] = "/dev/null",
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_AUTHOR_NAME"] = "t", ["GIT_AUTHOR_EMAIL"] = "t@example.invalid",
            ["GIT_COMMITTER_NAME"] = "t", ["GIT_COMMITTER_EMAIL"] = "t@example.invalid",
        };

        public ScratchRepo()
        {
            var origin = Path.Combine(Root, "origin.git");
            var seed = Path.Combine(Root, "seed");
            Git(Root, Env, "init", "-q", "--bare", "-b", "main", origin);
            Git(Root, Env, "init", "-q", "-b", "main", seed);
            Git(seed, Env, "commit", "-q", "--allow-empty", "-m", "a");
            var oldMain = Git(seed, Env, "rev-parse", "HEAD");
            Git(seed, Env, "commit", "-q", "--allow-empty", "-m", "b");
            Git(seed, Env, "checkout", "-q", "-b", "feature");
            Git(seed, Env, "commit", "-q", "--allow-empty", "-m", "feature");
            Git(seed, Env, "tag", "v9.9.9", "feature");
            Git(seed, Env, "tag", "v1.0.0", oldMain);
            Git(seed, Env, "tag", "v1.1.0", "main");
            Git(seed, Env, "tag", "vorher-umbau", "main");
            Git(seed, Env, "push", "-q", origin, "main", "feature", "--tags");
            Git(Root, Env, "clone", "-q", origin, Work);
        }

        public (int Exit, string Output) RunGuard(string tag)
        {
            Git(Work, Env, "checkout", "-q", "--detach", $"refs/tags/{tag}");
            var output = Path.Combine(Root, $"out-{tag}");
            File.WriteAllText(output, "");
            var (exit, log) = Run("bash", Work,
                new Dictionary<string, string>(Env) { ["GITHUB_REF_NAME"] = tag, ["GITHUB_OUTPUT"] = output },
                "-c", GuardScript());
            return (exit, File.ReadAllText(output) + log);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Theory]
    [InlineData("v1.1.0")]
    [InlineData("v1.0.0")]
    public void Guard_AcceptsReleaseTagOnMain(string tag)
    {
        using var repo = new ScratchRepo();
        var (exit, output) = repo.RunGuard(tag);
        Assert.True(exit == 0, output);
        Assert.Contains("release=true", output);
    }

    [Theory]
    [InlineData("v9.9.9")]        // Semver, aber ungemergt
    [InlineData("vorher-umbau")]  // Sicherungs-Tag (loest den Workflow ohnehin nicht mehr aus)
    public void Guard_RejectsOtherTags(string tag)
    {
        using var repo = new ScratchRepo();
        var (exit, output) = repo.RunGuard(tag);
        Assert.NotEqual(0, exit);
        Assert.DoesNotContain("release=true", output);
    }
}
