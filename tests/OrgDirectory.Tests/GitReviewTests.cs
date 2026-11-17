using System.Text;
using System.Text.Json.Nodes;
using static OrgDirectory.Tests.Users;

namespace OrgDirectory.Tests;

/// <summary>[git] and [review]: what's committed and queued after a sync is exactly what the EA's after-run hook
/// (doc_filer_hook.py, which did both for this tool until [git] and [review]) did for the same report. Golden/hooks.json
/// is that hook run, unchanged, on each case in a temporary repository with a fixed clock: its commits (message,
/// name-status), git status after, its notifications and the pending.json items it left. Two lines differ, by design:
/// the message names this tool and its settings rather than the hook ("Filed automatically by org-directory
/// (tools/org-directory.toml)." for "... by doc-filer (tools/doc_filer_hook.py)."), and a notification points at this
/// tool's log rather than doc-filer's.</summary>
public sealed class GitReviewTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 17, 9, 30, 5);
    private readonly string root = Directory.CreateTempSubdirectory("od-git-").FullName;
    private readonly List<string> log = [];
    private readonly List<(string Title, IReadOnlyList<string> Lines)> notes = [];

    public void Dispose() => GitRepo.Delete(root);

    private static JsonArray Golden() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", "hooks.json")))!.AsArray();

    public static TheoryData<string> Cases() => [.. Golden().Select(g => (string)g!["name"]!)];

    private static Config Settings(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return Config.Load(path);
    }

    private string Queue => Path.Combine(root, "pending.json").Replace('\\', '/');

    private static string Lock() => $"od-review-test-{Guid.NewGuid():N}";

    private void Notify(string title, IReadOnlyList<string> lines) => notes.Add((title, lines));

    [Theory]
    [MemberData(nameof(Cases))]
    public void Commits_and_items_are_the_python_hooks(string name)
    {
        var golden = Golden().Single(g => (string)g!["name"]! == name)!.AsObject();
        var repo = new GitRepo(Path.Combine(root, "repo"), golden["committed"] as JsonObject, golden["failing_hook"] is not null);
        repo.Write(golden["written"] as JsonObject);
        var config = Settings(Path.Combine(repo.Path, "tools", "org-directory.toml"), $"""
            state_file = "state.json"
            log_file = "org-directory.log"

            [git]
            repo = ".."
            trailers = ["Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"]

            [review]
            file = "{Queue}"
            source = "transcripts"
            lock = "{Lock()}"
            """);
        var report = new Report();
        foreach (var path in golden["report"]!["written"]!.AsArray())
            report.Written.Add(repo.File(((string)path!)["<repo>/".Length..]));
        foreach (var change in golden["report"]!["changes"]!.AsArray())
            report.Changes.Add(((string)change!["chart"]!, (string)change["text"]!));

        Sync.Review(config, report, log.Add, () => Now);
        Sync.Commit(config, report, log.Add, Notify);

        var expected = golden["commits"]!.ToJsonString().Replace("by doc-filer (tools/doc_filer_hook.py).",
            "by org-directory (tools/org-directory.toml).", StringComparison.Ordinal);
        Assert.Equal(expected, repo.Commits().ToJsonString());
        Assert.Equal(golden["status"]!.ToJsonString(), repo.Status().ToJsonString());
        Assert.Equal(golden["notes"]!.ToJsonString().Replace("See doc-filer.log.", "See org-directory.log.", StringComparison.Ordinal),
            new JsonArray([.. notes.Select(n => (JsonNode?)new JsonArray(JsonValue.Create(n.Title),
                new JsonArray([.. n.Lines.Select(l => (JsonNode?)JsonValue.Create(l))])))]).ToJsonString());
        var items = File.Exists(Queue) ? config.Review!.Open().Items() : [];
        Assert.Equal(PyCompat.PyJson.Dump(golden["items"]), PyCompat.PyJson.Dump(new JsonArray([.. items])));
    }

    [Fact]
    public void A_sync_commits_and_queues_only_with_hook_not_dry_run_and_something_written()
    {
        var repo = new GitRepo(Path.Combine(root, "repo"));
        var config = Settings(Path.Combine(repo.Path, "config.toml"), $"""
            state_file = "state.json"
            log_file = "log.txt"
            changes_file = "org/changes.md"

            [git]
            repo = "."

            [review]
            file = "{Queue}"
            source = "org-directory"
            lock = "{Lock()}"

            [[chart]]
            name = "tech"
            root = "cto@example.com"
            output = "org/tech.md"
            heading = "Technology"
            """);
        Sync.Run(config, new FakeGraph(Org()), dryRun: true, hook: true, log: log.Add, notify: Notify, now: () => Now);
        Sync.Run(config, new FakeGraph(Org()), hook: false, log: log.Add, notify: Notify, now: () => Now, today: new DateOnly(2026, 11, 8));
        Assert.Empty(repo.Commits());

        File.Delete(repo.File("org/tech.md"));
        File.Delete(Path.Combine(repo.Path, "state.json"));
        Sync.Run(config, new FakeGraph(Org()), hook: true, log: log.Add, notify: Notify, now: () => Now, today: new DateOnly(2026, 11, 8));
        var first = Assert.Single(repo.Commits());
        Assert.Equal("Ingest org tech\n\nFiled automatically by org-directory (config.toml).", (string)first!["message"]!);
        Assert.False(File.Exists(Queue));   // the first sync has nothing to compare with: no changes

        Sync.Run(config, new FakeGraph(Org().Where(u => (string?)u["id"] != "sa1").ToList()), hook: true, log: log.Add, notify: Notify,
            now: () => Now, today: new DateOnly(2026, 11, 9));
        var second = repo.Commits()[^1]!;
        Assert.StartsWith("Ingest org tech, org changes\n\n", (string)second["message"]!);   // changed tracked files first, then new
        var item = Assert.Single(config.Review!.Open().Items());
        Assert.Equal(("org-directory", "org-changes", "2026-11-17T09:30:05"),
            ((string)item["source"]!, (string)item["type"]!, (string)item["arrived"]!));
        Assert.DoesNotContain("**", (string)item["title"]!);
        Assert.Contains(log, l => l.StartsWith("review: queued ", StringComparison.Ordinal));
        Assert.Empty(notes);
    }

    [Fact]
    public void Neither_section_commits_or_queues_anything()
    {
        var config = Settings(Path.Combine(root, "config.toml"), "state_file = \"state.json\"\n");
        Assert.Null(config.Git);
        Assert.Null(config.Review);
        var report = new Report { Changes = { ("tech", "Joined: someone") } };
        Assert.Null(Sync.Commit(config, report, log.Add, Notify));
        Sync.Review(config, report, log.Add, () => Now);
        Assert.Empty(log);
    }

    [Theory]
    [InlineData("[git]\nrepo = \".\"\nrepository = \"x\"\n", "repository under [git] in ")]
    [InlineData("[git]\nrepo = \"no-such-folder\"\n", "isn't a folder: ")]
    [InlineData("[review]\nsource = \"s\"\n", "needs file")]
    [InlineData("[review]\nfile = \"q.json\"\nsource = \"s\"\nact = true\n", "act under [review] in ")]
    public void Bad_settings_are_refused(string toml, string message) =>
        Assert.Contains(message, Assert.Throws<PyValueError>(() => Settings(Path.Combine(root, "bad.toml"), toml)).Message);
}
