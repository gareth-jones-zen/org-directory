using System.Text.Json;
using System.Text.Json.Nodes;
using WinBackground;
using static OrgDirectory.Tests.Users;

namespace OrgDirectory.Tests;

/// <summary>New tests: the command line, the hook, autostart, People First's numbers, and the deliberate differences
/// from the Python version.</summary>
public sealed class BehaviourTests : IDisposable
{
    private const string Settings = """
        state_file = "state.json"
        log_file = "log.txt"
        after_run = ["python", "hook.py"]
        changes_file = "org/changes.md"
        [[chart]]
        name = "tech"
        root = "cto@example.com"
        output = "org/tech.md"
        heading = "Technology"
        """;

    private readonly TempFolder dir = new();

    public BehaviourTests() => dir.Write("config.toml", Settings);

    public void Dispose() => dir.Dispose();

    private Config Load() => Config.Load(dir.File("config.toml"));

    private sealed record Call(IReadOnlyList<string> Args, string Input, string Cwd);

    [Fact]
    public void repr_matches_python()
    {
        var golden = JsonNode.Parse(File.ReadAllText(ScenarioTests.GoldenFile("misc.json")))!;
        foreach (var c in golden["repr"]!.AsArray())
            Assert.Equal((string)c!["out"]!, Py.Repr((string)c["in"]!));
    }

    [Fact]
    public void the_hook_gets_the_report_on_stdin_only_after_a_real_write()
    {
        var calls = new List<Call>();
        Completed Runner(IReadOnlyList<string> args, string input, string cwd, TimeSpan timeout)
        {
            calls.Add(new Call(args, input, cwd));
            return new Completed(3, "one\n\n  two  \n", "err\n");
        }
        var logs = new List<string>();
        Sync.Run(Load(), new FakeGraph(Org()), dryRun: true, hook: true, logs.Add, runner: Runner);
        Assert.Empty(calls);
        Sync.Run(Load(), new FakeGraph(Org()), hook: true, log: logs.Add, runner: Runner, today: new DateOnly(2026, 11, 8));
        var call = Assert.Single(calls);
        Assert.Equal(["python", "hook.py"], call.Args);
        Assert.Equal(dir.Path, call.Cwd);
        static string Quoted(string path) => "\"" + path.Replace("\\", "\\\\", StringComparison.Ordinal) + "\"";
        Assert.Equal("{\"config\": " + Quoted(dir.File("config.toml")) + ", \"written\": [" + Quoted(dir.File("org", "tech.md"))
                     + "], \"failed\": [], \"unmatched\": [], \"summary\": \"1 written, 0 change(s), 0 unchanged, 7 people in the directory\", "
                     + "\"changes\": []}", call.Input);
        Assert.Equal(["after-run hook: python hook.py", "  hook: one", "  hook: two", "  hook: err", "after-run hook exited 3"], logs.TakeLast(5));
        Sync.Run(Load(), new FakeGraph(Org()), hook: true, log: logs.Add, runner: Runner, today: new DateOnly(2026, 11, 9));
        Assert.Single(calls);   // nothing written, so no hook
    }

    [Fact]
    public void a_stale_member_list_does_not_crash_the_next_sync()
    {
        // Python raised KeyError here, on every sync from then on: the chart was skipped once (a hand-edited file)
        // while the snapshot moved on without someone in it.
        var config = Load();
        Sync.Once(config, new FakeGraph(Org()), today: new DateOnly(2026, 11, 8));
        var tech = dir.Read("org", "tech.md");
        dir.Write("org/tech.md", "# edited by hand\n");
        Sync.Once(config, new FakeGraph(Org().Where(u => (string?)u["id"] != "sa1").ToList()), today: new DateOnly(2026, 11, 9));
        dir.Write("org/tech.md", tech);
        var report = Sync.Once(config, new FakeGraph(Org().Where(u => (string?)u["id"] != "sa1").ToList()), today: new DateOnly(2026, 11, 10));
        Assert.Empty(report.Failed);
        Assert.Single(report.Written, w => w.EndsWith("tech.md", StringComparison.Ordinal));
    }

    [Fact]
    public void a_manager_loop_through_the_top_shows_each_person_once()
    {
        // Python recursed for ever here (RecursionError).
        var users = Org();
        users[1]["manager"] = new JsonObject { ["id"] = "em" };   // Tom reports to Eve, who reports to Tom
        var report = Sync.Once(Load(), new FakeGraph(users), today: new DateOnly(2026, 11, 8));
        Assert.Empty(report.Failed);
        var chart = dir.Read("org", "tech.md");
        Assert.Equal(1, chart.Split("**Tom Tech**").Length - 1);
        Assert.Contains("  - **Eve Manager**", chart, StringComparison.Ordinal);
    }

    [Fact]
    public void the_command_line_reports_json_and_exit_codes()
    {
        var output = new StringWriter();
        var notes = new List<string>();
        var users = Org().Where(u => (string?)u["id"] != "cto").ToList();
        var host = new CliHost
        {
            Out = output,
            Err = output,
            Graph = (_, interactive) =>
            {
                Assert.False(interactive);   // --scheduled never opens a browser
                return new FakeGraph(users);
            },
            Notify = (title, lines) => notes.Add(title + ": " + string.Join(" / ", lines)),
            HookRunner = (_, _, _, _) => new Completed(0, "", ""),
        };
        Assert.Equal(1, Cli.Run(["--config", dir.File("config.toml"), "sync", "--json", "--scheduled"], host));
        var report = JsonNode.Parse(output.ToString())!;
        Assert.Equal("tech: no enabled account for cto@example.com", (string?)report["failed"]![0]);
        Assert.Equal(["org-directory: a chart wasn't written: tech: no enabled account for cto@example.com"], notes);
        Assert.Contains("1 failed", File.ReadAllText(dir.File("log.txt")), StringComparison.Ordinal);
    }

    [Fact]
    public void a_scheduled_sign_in_failure_notifies_and_exits_2()
    {
        var notes = new List<string>();
        var errors = new StringWriter();
        var host = new CliHost
        {
            Out = TextWriter.Null,
            Err = errors,
            Graph = (_, _) => throw new M365Graph.SignInNeededException("Sign in needed. At a console, run: org-directory sign-in"),
            Notify = (title, _) => notes.Add(title),
        };
        Assert.Equal(2, Cli.Run(["--config", dir.File("config.toml"), "sync", "--scheduled"], host));
        Assert.Equal(["org-directory needs a sign-in"], notes);
        Assert.Contains("org-directory sign-in", errors.ToString(), StringComparison.Ordinal);
        Assert.Contains("sync failed: Sign in needed.", File.ReadAllText(dir.File("log.txt")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new string[0], "org-directory: error: the following arguments are required: command")]
    [InlineData(new[] { "bogus" }, "org-directory: error: argument command: invalid choice: 'bogus' (choose from 'config', 'sign-in', 'sync', 'who', 'people-first', 'autostart')")]
    [InlineData(new[] { "sync", "--x" }, "org-directory: error: unrecognized arguments: --x")]
    [InlineData(new[] { "who" }, "org-directory who: error: the following arguments are required: name")]
    [InlineData(new[] { "people-first", "nope" }, "org-directory people-first: error: argument action: invalid choice: 'nope' (choose from 'sign-in', 'check')")]
    public void usage_errors_are_argparse_s(string[] args, string last)
    {
        var errors = new StringWriter();
        Assert.Equal(2, Cli.Run(args, new CliHost { Out = TextWriter.Null, Err = errors }));
        Assert.Equal(last, errors.ToString().TrimEnd().Split("\r\n")[^1]);
    }

    private sealed class FakeTask : ITask
    {
        public List<(string Exe, IReadOnlyList<string> Args, string? Dir, TaskSchedule? Schedule)> Enabled { get; } = [];
        public string Name => Autostart.TaskName;
        public string Enable(string exe, IReadOnlyList<string> args, string? workingDir) => Enable(exe, args, workingDir, TaskSchedule.Watcher);

        public string Enable(string exe, IReadOnlyList<string> args, string? workingDir, TaskSchedule schedule)
        {
            Enabled.Add((exe, args, workingDir, schedule));
            return exe + " " + string.Join(' ', args);
        }

        public bool Disable() => true;
        public bool Stop() => true;
        public bool Start() => true;

        public IReadOnlyDictionary<string, string>? Status() => new Dictionary<string, string>
        {
            ["state"] = "Ready", ["last_run"] = "today", ["last_result"] = "0", ["next_run"] = "tomorrow", ["command"] = "x",
        };
    }

    [Fact]
    public void autostart_runs_the_background_exe_daily_and_at_logon()
    {
        var task = new FakeTask();
        File.WriteAllText(dir.File(Autostart.BackgroundExe), "");
        var output = new StringWriter();
        dir.Write("config.toml", "daily_at = \"07:45\"\n" + Settings);   // before [[chart]], or it's the chart's
        Assert.Equal(0, Cli.Run(["--config", dir.File("config.toml"), "autostart", "on"],
            new CliHost { Out = output, Tasks = _ => task, ProgramDir = dir.Path }));
        var (exe, args, workingDir, schedule) = Assert.Single(task.Enabled);
        Assert.Equal(dir.File(Autostart.BackgroundExe), exe);
        Assert.Equal(["--config", dir.File("config.toml"), "sync", "--hook", "--scheduled"], args);
        Assert.Equal(dir.Path, workingDir);
        Assert.Equal(new TaskSchedule { AtLogon = true, DailyAt = "07:45", KeepRunning = false }, schedule);
        Assert.StartsWith("Autostart is on: 'org-directory sync' runs daily at 07:45 and at logon", output.ToString(), StringComparison.Ordinal);
        output.GetStringBuilder().Clear();
        Cli.Run(["autostart", "status"], new CliHost { Out = output, Tasks = _ => task });
        Assert.Contains("  next run     tomorrow", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void people_first_numbers_are_taken_from_contactinfo()
    {
        using var info = JsonDocument.Parse("""
            {"workEmail": "  Sa1@Example.com ", "companyMobile": " 07700 900001 ", "personalMobile": "", "personalNumber": 5,
             "personalEmail": "home@example.org"}
            """);
        var (email, numbers) = PeopleFirst.Numbers(info.RootElement)!.Value;
        Assert.Equal("sa1@example.com", email);
        Assert.Equal("""{"companyMobile":"07700 900001"}""", numbers.ToJsonString());
        using var none = JsonDocument.Parse("""{"workEmail": "a@b", "personalMobile": "  "}""");
        Assert.Null(PeopleFirst.Numbers(none.RootElement));
    }

    [Fact]
    public void people_first_check_prints_counts_only()
    {
        dir.Write("config.toml", Settings + "\n[people_first]\nenabled = true\nurl = \"https://example.people-first.com\"\n");
        var output = new StringWriter();
        var host = new CliHost
        {
            Out = output,
            ReadPeopleFirst = (_, log) =>
            {
                log("people first: read 3 people in your reporting line, 2 with a number");
                return JsonNode.Parse("""{"a@x": {"personalMobile": "1", "companyMobile": "2"}, "b@x": {"personalMobile": "3"}}""")!.AsObject();
            },
        };
        Assert.Equal(0, Cli.Run(["--config", dir.File("config.toml"), "people-first", "check"], host));
        Assert.EndsWith("People First gives numbers for 2 people: companyMobile 1, personalMobile 2\r\n", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, Cli.Run(["--config", dir.File("config.toml"), "people-first", "check"], new CliHost
        {
            Out = TextWriter.Null,
            Err = TextWriter.Null,
            ReadPeopleFirst = (_, _) => throw new PeopleFirstClient.SignInNeededException("Sign in. At a console, run: org-directory people-first sign-in"),
        }));
    }

    [Fact]
    public void the_default_state_file_is_named_as_python_names_it()
    {
        var env = new Dictionary<string, string?> { ["LOCALAPPDATA"] = @"C:\Local", ["APPDATA"] = @"C:\Roaming" };
        dir.Write("config.toml", "");
        var config = Config.Load(dir.File("config.toml"), name => env.GetValueOrDefault(name));
        var key = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Py.Lower(config.Path))))[..12];
        Assert.Equal($@"C:\Local\org-directory\state-{key}.json", config.StateFile);
        Assert.Equal(@"C:\Local\org-directory\org-directory.log", config.LogFile);
        Assert.Equal(@"C:\Roaming\org-directory\config.toml", Config.DefaultPath(name => env.GetValueOrDefault(name)));
    }
}
