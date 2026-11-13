using System.Text.Json.Nodes;
using static OrgDirectory.Tests.Users;

namespace OrgDirectory.Tests;

/// <summary>The Python tests in tests/test_tool.py, by name.</summary>
public sealed class ToolTests : IDisposable
{
    private static readonly DateOnly Day1 = new(2026, 11, 8), Day2 = new(2026, 11, 9);

    private const string Settings = """
        state_file = "state.json"
        log_file = "log.txt"
        changes_file = "org/changes.md"
        [[chart]]
        name = "tech"
        root = "cto@example.com"
        output = "org/tech.md"
        heading = "Technology"
        [[chart]]
        name = "top"
        root = "ceo@example.com"
        output = "org/top.md"
        depth = 1
        """;

    private readonly TempFolder dir = new();
    private Config config;

    public ToolTests()
    {
        dir.Write("config.toml", Settings);
        config = Config.Load(dir.File("config.toml"));
    }

    public void Dispose() => dir.Dispose();

    private Report Sync(IEnumerable<JsonObject> users, DateOnly? day = null, bool dryRun = false) =>
        OrgDirectory.Sync.Once(config, new FakeGraph(users), dryRun, day ?? Day1, _ => { });

    private string Read(string name) => dir.Read("org", name);

    [Fact]
    public void the_first_sync_writes_each_chart_with_no_changes()
    {
        var report = Sync(Org());
        Assert.Equal(2, report.Written.Count);
        Assert.Empty(report.Changes);
        var tech = Read("tech.md");
        Assert.Contains(OrgDirectory.Org.Marker, tech);
        Assert.Contains("- **Tom Tech**, Technology Director (Technology) · 2 reports, 4 in all", tech);
        Assert.Contains("  - **Ann Architect**, Principal Architect · 2 reports\n", tech);
        Assert.Contains("    - **Bob Builder**, Solutions Architect\n", tech);
        // The department shows only where it differs from the manager's.
        Assert.Contains("    - **Dee Design**, Solutions Architect (Architecture)\n", tech);
        Assert.DoesNotContain("Gus Guest", tech);
        Assert.Contains("5 people; the directory last changed on 2026-11-08", tech);
        Assert.False(File.Exists(dir.File("org", "changes.md")));
    }

    [Fact]
    public void depth_limits_a_chart()
    {
        Sync(Org());
        var top = Read("top.md");
        Assert.Contains("**Tom Tech**", top);
        Assert.DoesNotContain("Ann Architect", top);
        Assert.Contains("1 levels down", top);
    }

    [Fact]
    public void an_unchanged_directory_writes_nothing()
    {
        Sync(Org());
        var report = Sync(Org(), Day2);
        Assert.Empty(report.Written);
        Assert.Equal(2, report.Unchanged);
        Assert.Contains("last changed on 2026-11-08", Read("tech.md"));
    }

    [Fact]
    public void changes_are_found_and_logged()
    {
        Sync(Org());
        var users = Org();
        users[4]["jobTitle"] = "Senior Solutions Architect";                 // Bob: new title
        users[6]["manager"] = new JsonObject { ["id"] = "arch" };            // Eve: new manager
        users[2]["manager"] = new JsonObject { ["id"] = "cto" };             // Fay: moves into tech
        users.RemoveAt(5);                                                   // Dee: leaves
        users.Add(User("new", "Nia New", "Developer", "Technology", "em"));  // Nia: joins
        var report = Sync(users, Day2);
        var tech = report.Changes.Where(c => c.Chart == "tech").Select(c => c.Text).ToList();
        Assert.Contains("New in the directory: **Nia New**, Developer (Technology), reporting to Eve Manager.", tech);
        Assert.Contains("Moved into this chart: **Fay Finance**, Finance Director (Finance), reporting to Tom Tech. "
                        + "Was **Fay Finance**, Finance Director (Finance), reporting to Cara Chief.", tech);
        Assert.Contains("Gone from the directory (left, or the account was disabled): **Dee Design**, Solutions "
                        + "Architect (Architecture), reporting to Ann Architect.", tech);
        Assert.Contains("New title: **Bob Builder**, Solutions Architect → Senior Solutions Architect.", tech);
        Assert.Contains("New manager: **Eve Manager**, Tom Tech → Ann Architect.", tech);
        var top = report.Changes.Where(c => c.Chart == "top").Select(c => c.Text).ToList();
        Assert.Equal(["Moved out of this chart: now **Fay Finance**, Finance Director (Finance), reporting to Tom Tech."], top);
        var log = Read("changes.md");
        Assert.True(log.IndexOf("## 2026-11-09: Technology", StringComparison.Ordinal) < log.IndexOf("New title", StringComparison.Ordinal));
        Assert.Contains(dir.File("org", "changes.md"), report.Written);
        Assert.Contains("last changed on 2026-11-09", Read("tech.md"));
    }

    [Fact]
    public void changes_depth_keeps_a_big_chart_quiet()
    {
        dir.Write("config.toml", Settings.Replace("heading = \"Technology\"", "heading = \"Technology\"\nchanges_depth = 1", StringComparison.Ordinal));
        config = Config.Load(dir.File("config.toml"));
        Sync(Org());
        var users = Org();
        users[4]["jobTitle"] = "Senior Solutions Architect";   // two levels down: not reported
        users[1]["jobTitle"] = "CTO";                          // the top: reported
        var tech = Sync(users, Day2).Changes.Where(c => c.Chart == "tech").Select(c => c.Text).ToList();
        Assert.Equal(["New title: **Tom Tech**, Technology Director → CTO."], tech);
        Assert.Contains("Senior Solutions Architect", Read("tech.md"));   // the chart itself is complete
    }

    [Fact]
    public void a_file_it_did_not_write_is_left_alone()
    {
        Directory.CreateDirectory(dir.File("org"));
        dir.Write("org/tech.md", "# My hand-made chart\n");
        var report = Sync(Org());
        Assert.Equal([dir.File("org", "tech.md")], report.Untracked);
        Assert.Equal("# My hand-made chart\n", Read("tech.md"));
    }

    [Fact]
    public void a_sharp_shrink_is_not_written()
    {
        var big = Org().Concat(Enumerable.Range(0, 20).Select(i => User($"d{i}", $"Dev {i:00}", "Developer", "Technology", "em"))).ToList();
        Sync(big);
        var report = Sync(Org(), Day2);
        Assert.Contains(report.Failed, f => f.Contains("shrank from 25 to 5", StringComparison.Ordinal));
        Assert.Contains("Dev 19", Read("tech.md"));
        Assert.DoesNotContain(report.Changes, c => c.Chart == "tech");
    }

    [Fact]
    public void a_missing_root_fails_that_chart_only()
    {
        var users = Org().Where(u => (string?)u["id"] != "ceo").ToList();
        users[0]["manager"] = null;
        var report = Sync(users);
        Assert.Equal(["top: no enabled account for ceo@example.com"], report.Failed);
        Assert.True(File.Exists(dir.File("org", "tech.md")));
    }

    [Fact]
    public void a_dry_run_writes_nothing()
    {
        var report = Sync(Org(), dryRun: true);
        Assert.Equal(2, report.Written.Count);
        Assert.False(Directory.Exists(dir.File("org")));
        Assert.False(File.Exists(dir.File("state.json")));
    }

    [Fact]
    public void who_reads_the_last_sync()
    {
        Sync(Org());
        var output = new StringWriter();
        Cli.Run(["--config", dir.File("config.toml"), "who", "bob"], new CliHost { Out = output, Err = output });
        Assert.Contains("Bob Builder: Solutions Architect, Technology", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("reports to: Ann Architect (Principal Architect) > Tom Tech", output.ToString(), StringComparison.Ordinal);
        var state = JsonNode.Parse(File.ReadAllText(dir.File("state.json")))!;
        Assert.False(state["people"]!.AsObject().ContainsKey("guest"));
    }
}
