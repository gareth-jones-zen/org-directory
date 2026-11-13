using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using WinBackground;

namespace OrgDirectory.Tests;

/// <summary>The synthetic scenarios in Golden/scenarios.json, made by running the Python version (tests/parity/
/// scenarios.py): every report, summary, log line, hook input and file (by SHA-256) must be the same. Set
/// ORG_DIRECTORY_GOLDEN to the path of a bigger local run to check that too.</summary>
public sealed class ScenarioTests(ITestOutputHelper output)
{
    private const string Placeholder = "{DIR}";

    public static string GoldenFile(string name) => Path.Combine(AppContext.BaseDirectory, "Golden", name);

    [Fact]
    public void every_committed_scenario_matches_python() => Check(GoldenFile("scenarios.json"));

    [Fact]
    public void a_local_golden_file_matches_python()
    {
        var path = Environment.GetEnvironmentVariable("ORG_DIRECTORY_GOLDEN");
        Assert.SkipWhen(string.IsNullOrEmpty(path), "ORG_DIRECTORY_GOLDEN isn't set");
        Check(path!);
    }

    private void Check(string path)
    {
        var golden = JsonNode.Parse(File.ReadAllText(path))!;
        var cases = golden["cases"]!.AsArray();
        var failures = new List<string>();
        int steps = 0, pythonCrashes = 0, whos = 0;
        foreach (var item in cases)
        {
            var (s, crashes, w, fails) = RunCase(item!.AsObject());
            steps += s;
            pythonCrashes += crashes;
            whos += w;
            failures.AddRange(fails);
        }
        output.WriteLine($"{cases.Count} scenarios, {steps} syncs compared, {whos} `who` calls; {pythonCrashes} where Python "
                         + "crashed (KeyError or RecursionError) and .NET carries on (deliberate), not compared after that point");
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
    }

    private static string Hash(string file) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));

    private static void Apply(JsonArray ops, string folder)
    {
        foreach (var op in ops)
        {
            var path = Path.Combine(folder, (string)op!["path"]!);
            if (op["delete"] is not null && (bool)op["delete"]!)
            {
                if (File.Exists(path))
                    File.Delete(path);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Convert.FromBase64String((string)op["bytes"]!));
        }
    }

    private static (int Steps, int Crashes, int Whos, List<string> Failures) RunCase(JsonObject c)
    {
        var name = (string)c["name"]!;
        var failures = new List<string>();
        using var dir = new TempFolder();
        string Mask(string text) =>
            text.Replace(dir.Path.Replace("\\", "\\\\", StringComparison.Ordinal), Placeholder, StringComparison.Ordinal)
                .Replace(dir.Path, Placeholder, StringComparison.Ordinal);
        var configPath = dir.File("config.toml");
        File.WriteAllText(configPath, ((string)c["settings"]!).Replace("\n", "\r\n", StringComparison.Ordinal), new UTF8Encoding(false));
        var steps = c["steps"]!.AsArray();
        var results = c["results"]!.AsArray();
        var compared = 0;
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i]!.AsObject();
            var expected = results[i]!.AsObject();
            Apply(step["ops"]!.AsArray(), dir.Path);
            var now = DateTime.ParseExact((string)step["now"]!, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
            var today = DateOnly.ParseExact((string)step["today"]!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var logs = new List<string>();
            var hooks = new List<string>();
            var pf = step["people_first"]?.AsObject();
            JsonObject Read(PeopleFirstSettings settings, Action<string> log)
            {
                if (pf is null)
                    throw new InvalidOperationException("People First read when the scenario says it isn't");
                if (pf["error"] is { } error)
                    throw new InvalidOperationException((string)error!);
                log("people first: (fake read)");
                return pf["numbers"]!.DeepClone().AsObject();
            }
            Completed Runner(IReadOnlyList<string> args, string input, string cwd, TimeSpan timeout)
            {
                hooks.Add(input);
                return new Completed((int)step["hook_exit"]!, (string)step["hook_stdout"]!, (string)step["hook_stderr"]!);
            }
            string? error = null;
            Report? report = null;
            try
            {
                var config = Config.Load(configPath);
                report = Sync.Run(config, new FakeGraph(step["users"]!.AsArray().Select(u => u?.DeepClone())), (bool)step["dry_run"]!,
                    hook: true, logs.Add, Read, Runner, today, () => now);
            }
            catch (Exception e)
            {
                error = e.GetType().Name;
            }
            var where = $"{name} step {i}";
            if (expected["error"] is { } pyError)
            {
                // Python's crashes on a stale member list (KeyError, see Charts.Changes) and on a manager loop through a
                // chart's top person (RecursionError, see Charts.Render), which .NET doesn't share (deliberate).
                if ((string)pyError! is "KeyError" or "RecursionError" && error is null)
                    return (compared, 1, 0, failures);
                if (error is null)
                    failures.Add($"{where}: Python raised {pyError}, .NET didn't");
                return (compared, 0, 0, failures);
            }
            if (error is not null)
            {
                failures.Add($"{where}: .NET raised {error}");
                return (compared, 0, 0, failures);
            }
            compared++;
            Same(failures, where, "report", (string)expected["report"]!, Mask(report!.ToJson()));
            Same(failures, where, "summary", (string)expected["summary"]!, Mask(report.Summary()));
            Same(failures, where, "log", string.Join("\n", expected["log"]!.AsArray().Select(l => (string)l!)), string.Join("\n", logs.Select(Mask)));
            Same(failures, where, "hook", string.Join("\n", expected["hook"]!.AsArray().Select(l => (string)l!)), string.Join("\n", hooks.Select(Mask)));
            var files = Directory.EnumerateFiles(dir.Path, "*", SearchOption.AllDirectories)
                .Where(f => Path.GetFileName(f) != "config.toml")
                .ToDictionary(f => Path.GetRelativePath(dir.Path, f).Replace('\\', '/'), Hash);
            var expectedFiles = expected["files"]!.AsObject().ToDictionary(p => p.Key, p => (string)p.Value!);
            foreach (var (file, hash) in expectedFiles)
                if (!files.TryGetValue(file, out var got))
                    failures.Add($"{where}: {file} missing");
                else if (got != hash)
                    failures.Add($"{where}: {file} differs");
            foreach (var file in files.Keys.Except(expectedFiles.Keys))
                failures.Add($"{where}: {file} written only by .NET");
        }
        var whos = 0;
        foreach (var who in c["who_results"]!.AsArray())
        {
            var outWriter = new StringWriter();
            var errWriter = new StringWriter();
            var code = Cli.Run(["--config", configPath, "who", (string)who!["query"]!], new CliHost { Out = outWriter, Err = errWriter });
            var where = $"{name} who {(string)who["query"]!}";
            if (code != (int)who["code"]!)
                failures.Add($"{where}: exit {code}, Python {(int)who["code"]!}");
            Same(failures, where, "stdout", (string)who["out"]!, Mask(outWriter.ToString().Replace("\r\n", "\n", StringComparison.Ordinal)));
            Same(failures, where, "stderr", (string)who["err"]!, Mask(errWriter.ToString().Replace("\r\n", "\n", StringComparison.Ordinal)));
            whos++;
        }
        return (compared, 0, whos, failures);
    }

    private static void Same(List<string> failures, string where, string what, string expected, string actual)
    {
        if (expected == actual)
            return;
        var at = 0;
        while (at < expected.Length && at < actual.Length && expected[at] == actual[at])
            at++;
        string Around(string s) => s.Substring(Math.Max(0, at - 40), Math.Min(s.Length - Math.Max(0, at - 40), 100));
        failures.Add($"{where}: {what} differs at {at}\n  python: {Around(expected)}\n  dotnet: {Around(actual)}");
    }
}
