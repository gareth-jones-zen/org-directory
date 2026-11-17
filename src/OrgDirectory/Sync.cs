using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using PyCompat;
using RepoCommit;
using WinBackground;

namespace OrgDirectory;

/// <summary>What a sync did. Its JSON (sync --json) has Python's keys, in Python's order.</summary>
public sealed class Report
{
    public List<string> Written { get; } = [];
    public List<string> Failed { get; } = [];
    public List<string> Untracked { get; } = [];
    public List<(string Chart, string Text)> Changes { get; } = [];
    public int Unchanged { get; set; }
    public int People { get; set; }
    public string? PeopleFirstFailed { get; set; }
    public bool? PeopleFirstStale { get; set; }

    /// <summary>The contact sheets written; null when there's no [contacts].</summary>
    public List<string>? Contacts { get; set; }

    public JsonArray ChangesJson() =>
        new(Changes.Select(c => (JsonNode)new JsonObject { ["chart"] = c.Chart, ["text"] = c.Text }).ToArray());

    public JsonObject ToJsonObject()
    {
        var json = new JsonObject
        {
            ["written"] = Strings(Written),
            ["failed"] = Strings(Failed),
            ["untracked"] = Strings(Untracked),
            ["changes"] = ChangesJson(),
            ["unchanged"] = PyJson.Int(Unchanged),
            ["people"] = PyJson.Int(People),
        };
        if (PeopleFirstFailed is not null)
            json["people_first_failed"] = PeopleFirstFailed;
        if (PeopleFirstStale is { } stale)
            json["people_first_stale"] = stale;
        if (Contacts is not null)
            json["contacts"] = Strings(Contacts);
        return json;
    }

    /// <summary>json.dumps(report, indent=1).</summary>
    public string ToJson() => PyJson.Dump(ToJsonObject(), indent: 1);

    internal static JsonArray Strings(IEnumerable<string> items) => new(items.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray());

    /// <summary>summary(): the line a sync ends with.</summary>
    public string Summary()
    {
        var text = $"{Written.Count} written, {Changes.Count} change(s), {Unchanged} unchanged, {People} people in the directory";
        if (Failed.Count > 0)
            text += $", {Failed.Count} failed";
        if (Contacts is not null)
            text += $"; contact sheets {(Contacts.Count > 0 ? "written" : "unchanged")}";
        if (!string.IsNullOrEmpty(PeopleFirstFailed))
            text += $"; People First failed ({Py.Head(PeopleFirstFailed, 120)})";
        return text;
    }
}

/// <summary>Reads People First's numbers (PeopleFirst.Fetch, or a fake in tests).</summary>
public delegate JsonObject PeopleFirstReader(PeopleFirstSettings settings, Action<string> log);

/// <summary>One locked sync: fetch the directory, write the charts that changed, log the changes, then [review]'s
/// item, [git]'s commit and the hook.
///
/// [review] puts the changes in a review queue as one item; [git] commits exactly the files written. The hook is for
/// anything else a repo wants done. It gets a JSON report on stdin, the same shape doc-filer and teams-channel-sync
/// give theirs, plus the changes:
///
///     {"config": "...", "written": ["C:\\...\\technology.md"], "failed": [], "unmatched": [],
///      "summary": "1 written", "changes": [{"chart": "technology", "text": "New title: ..."}]}
///
/// It runs from the settings file's folder, with no window.
///
/// Contact sheets, if [contacts] is set, are written after the charts, to their own folder and never passed to the
/// hook, as they hold personal numbers. With People First switched on, its numbers are read on every sync and kept in
/// the state file; if a read fails, the last good numbers are used, the sheets say how old they are, and a scheduled
/// run notifies once they're more than StaleDays old.
///
/// A chart is only rewritten when the directory changed, so a daily sync of an unchanged directory writes nothing and
/// commits nothing. A chart that shrinks sharply isn't written (a quarter of it, and at least ten people): a partial
/// answer from Graph would otherwise read as a wave of leavers.</summary>
public static class Sync
{
    public const double Shrink = 0.25;
    public const int StaleDays = 3;
    public static readonly TimeSpan HookTimeout = TimeSpan.FromSeconds(1800);
    public const double LockTimeoutSeconds = 600;

    /// <summary>The run lock, named as the Python version named it, so a .NET sync, a Python sync and cover-rota (which
    /// takes it too) exclude each other. The key is win_background's key(): WinBackground's Locks.Key lower-cases as
    /// .NET does, which differs from Python's str.lower() for U+0130 and a final capital sigma, so it's worked out
    /// here with Python's lower-casing.</summary>
    public static string RunLockName(Config config) => $"org-directory-run-{LockKey(config.Path)}";

    internal static string LockKey(string text) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Py.Lower(text))))[..16];

    /// <summary>load_state(): the last sync's state; empty if it's missing or isn't JSON. Text that isn't UTF-8 is a
    /// PyValueError, as Python's UnicodeDecodeError was.</summary>
    public static JsonObject LoadState(Config config)
    {
        string text;
        try
        {
            text = PyJson.ReadStrict(config.StateFile);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        catch (System.Text.DecoderFallbackException error)
        {
            throw new PyValueError($"'utf-8' codec can't decode the state file {config.StateFile}: {error.Message}", error);
        }
        JsonNode? node;
        try
        {
            node = PyJson.Parse(text);
        }
        catch (JsonException)
        {
            return [];
        }
        return node as JsonObject ?? throw new InvalidDataException($"The state file {config.StateFile} isn't a JSON object.");
    }

    /// <summary>numbers(): People First's numbers, the sheets' line about them, and what to keep in the state file.</summary>
    public static (JsonObject Found, string Line, JsonObject? Kept) Numbers(Config config, JsonObject state, Report report,
        Action<string> log, PeopleFirstReader? read, Func<DateTime> now)
    {
        if (!config.PeopleFirst.Enabled)
            return ([], "People First isn't used.", null);
        var kept = state["people_first"];
        try
        {
            var found = (read ?? PeopleFirst.Fetch)(config.PeopleFirst, log);
            var at = now();
            return (found, $"People First read {Py.Stamp(at)}.",
                new JsonObject { ["read"] = Py.IsoSeconds(at), ["numbers"] = found.DeepClone() });
        }
        catch (Exception error)
        {
            report.PeopleFirstFailed = error.Message;
            log($"people first: {error.Message}");
            if (kept is not JsonObject { Count: > 0 } last)
            {
                report.PeopleFirstStale = true;
                return ([], "People First couldn't be read, so personal numbers are missing.", null);
            }
            var when = Py.FromIsoFormat(PyCompat.Py.Str(last["read"]));
            report.PeopleFirstStale = Py.Days(now(), when) >= StaleDays;
            return (last["numbers"] as JsonObject ?? [], $"People First last read {Py.Stamp(when)} (the latest read failed).", last);
        }
    }

    /// <summary>sync(): one sync, without the lock (Run takes it).</summary>
    public static Report Once(Config config, IGraph graph, bool dryRun = false, DateOnly? today = null, Action<string>? log = null,
        PeopleFirstReader? readPeopleFirst = null, Func<DateTime>? now = null)
    {
        now ??= () => DateTime.Now;
        log ??= _ => { };
        var day = today ?? DateOnly.FromDateTime(now());
        var state = LoadState(config);
        var after = Org.Fetch(graph);
        var before = Org.FromSnapshot(state["people"] as JsonObject);
        var report = new Report { People = after.People.Count };
        var charts = state["charts"] is JsonObject savedCharts ? (JsonObject)savedCharts.DeepClone() : [];
        foreach (var chart in config.Charts)
        {
            var root = after.Find(chart.Root);
            if (root is null)
            {
                report.Failed.Add($"{chart.Name}: no enabled account for {chart.Root}");
                log($"{chart.Name}: can't find {chart.Root} in the directory; chart not written");
                continue;
            }
            var members = after.Under(root.Id, chart.Depth);
            var old = charts[chart.Name] as JsonObject is { Count: > 0 } saved0 ? saved0 : null;   // Python: `if old`
            var oldMembers = new OrderedDictionary<string, int>();
            if (old is not null && old["members"] is JsonObject savedMembers)
                foreach (var (id, level) in savedMembers)
                    oldMembers[id] = (int)PyCompat.Py.Int(level);
            if (old is not null && members.Count < oldMembers.Count * (1 - Shrink) && oldMembers.Count - members.Count >= 10)
            {
                report.Failed.Add($"{chart.Name}: shrank from {oldMembers.Count} to {members.Count} people");
                log($"{chart.Name}: shrank from {oldMembers.Count} to {members.Count} people, so it wasn't written. "
                    + "If that's real, delete the chart's entry from the state file and sync again.");
                continue;
            }
            var found = old is not null ? Charts.Changes(before, after, oldMembers, members, chart.ChangesDepth) : [];
            var moved = old is null || !SameMembers(oldMembers, members)
                        || members.Keys.Any(id => before.Get(id) is not { } was || !was.SameRecord(after.People[id]));
            var changed = moved ? Py.IsoDate(day) : PyCompat.Py.Str(old!["changed"]);
            var text = Charts.Render(after, root.Id, chart.Heading.Length > 0 ? chart.Heading : root.Name, chart.Depth, changed);
            var existing = File.Exists(chart.Output) ? PyStr.ReadText(chart.Output) : null;
            if (existing is not null && !Py.Head(existing, 2000).Contains(Org.Marker, StringComparison.Ordinal))
            {
                report.Untracked.Add(chart.Output);
                log($"{chart.Name}: {chart.Output} wasn't written by org-directory, so it's left alone");
                continue;
            }
            if (existing == text)
                report.Unchanged++;
            else
            {
                report.Written.Add(chart.Output);
                log($"{chart.Name}: {(dryRun ? "would write" : "wrote")} {chart.Output} ({members.Count} people)");
                if (!dryRun)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(chart.Output)!);
                    PyStr.WriteText(chart.Output, text);
                }
            }
            foreach (var item in found)
                log($"{chart.Name}: {item}");
            report.Changes.AddRange(found.Select(item => (chart.Name, item)));
            if (found.Count > 0 && config.ChangesFile is not null && !dryRun)
                Charts.LogChanges(config.ChangesFile, chart.Heading.Length > 0 ? chart.Heading : chart.Name, found, day);
            var memberJson = new JsonObject();
            foreach (var (id, level) in members)
                memberJson[id] = PyJson.Int(level);
            charts[chart.Name] = new JsonObject { ["root"] = root.Id, ["members"] = memberJson, ["changed"] = changed };
        }
        if (report.Changes.Count > 0 && config.ChangesFile is not null)
            report.Written.Add(config.ChangesFile);
        var kept = state["people_first"]?.DeepClone();
        if (config.Contacts is not null)
        {
            var (numbers, line, keep) = Numbers(config, state, report, log, readPeopleFirst, now);
            kept = keep?.DeepClone();
            var stamp = $"Directory read {Py.Stamp(now())}. {line}";
            report.Contacts = Contacts.Write(config.Contacts, after, numbers, stamp, dryRun, log);
        }
        if (!dryRun)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(config.StateFile)!);
            var saved = new JsonObject
            {
                ["synced"] = Py.IsoSeconds(now()),
                ["people"] = after.Snapshot(),
                ["charts"] = charts,
            };
            if (PyCompat.Py.Truthy(kept) && config.PeopleFirst.Enabled)
                saved["people_first"] = kept;   // personal numbers: the state file stays on this machine
            PyStr.WriteText(config.StateFile, PyJson.Dump(saved));
        }
        return report;
    }

    /// <summary>Python's == on two {id: level} dicts: the same keys and levels, in any order.</summary>
    private static bool SameMembers(OrderedDictionary<string, int> a, OrderedDictionary<string, int> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var level) && level == pair.Value);

    /// <summary>The hook's stdin: json.dumps({...}) on one line.</summary>
    public static string Payload(Config config, Report report) => PyJson.Dump(new JsonObject
    {
        ["config"] = config.Path,
        ["written"] = Report.Strings(report.Written),
        ["failed"] = new JsonArray(),
        ["unmatched"] = new JsonArray(),
        ["summary"] = report.Summary(),
        ["changes"] = report.ChangesJson(),
    });

    /// <summary>after_run(): "failed" is doc-filer's list of files it couldn't convert; a chart that wasn't written is
    /// reported by the sync itself (logged, and notified when scheduled), so it isn't passed on as one.</summary>
    public static void AfterRun(Config config, Report report, Action<string> log,
        Func<IReadOnlyList<string>, string, string, TimeSpan, Completed>? runner = null)
    {
        runner ??= (args, input, cwd, timeout) => Shell.Run(args[0], args.Skip(1), timeout, input, cwd, utf8: true);
        var payload = Payload(config, report);
        log($"after-run hook: {string.Join(' ', config.AfterRun)}");
        try
        {
            var done = runner(config.AfterRun, payload, Path.GetDirectoryName(config.Path)!, HookTimeout);
            foreach (var line in PyStr.SplitLines(done.StandardOutput + done.StandardError))
            {
                var text = PyStr.Strip(line);
                if (text.Length > 0)
                    log($"  hook: {text}");
            }
            if (done.ExitCode != 0)
                log($"after-run hook exited {done.ExitCode}");
        }
        catch (Win32Exception error)
        {
            log($"after-run hook failed: {Shell.WinErrorText(error)}");
        }
        catch (Exception error)
        {
            log($"after-run hook failed: {error.Message}");
        }
    }

    /// <summary>"directory technology": a file's folder and its name without the extension, as a commit names it.</summary>
    public static string Named(string repoPath)
    {
        var parts = repoPath.Split('/');
        var name = parts[^1];
        var dot = name.LastIndexOf('.');
        return $"{(parts.Length > 1 ? parts[^2] : "")} {(dot > 0 && dot < name.Length - 1 ? name[..dot] : name)}";
    }

    /// <summary>[git]: commit what the sync wrote, only the files git sees as changed, in git status's order:
    /// "Ingest directory changes, directory technology", then "Filed automatically by org-directory ({settings})." and
    /// the trailers. If the commit fails, a notification ("Filed, not committed"); a push that fails is only logged. The
    /// contact sheets are never passed to it. Null when there's no [git] (or commit = false).</summary>
    public static CommitResult? Commit(Config config, Report report, Action<string> log, Action<string, IReadOnlyList<string>> notify)
    {
        if (config.Git is not { Commit: true } git)
            return null;
        var committer = git.Open(log);
        var names = "";
        void Tell() => notify("Filed, not committed", [Py.Head(names, 300), $"See {Path.GetFileName(config.LogFile)}."]);
        try
        {
            var paths = committer.Changed(report.Written.Where(File.Exists));
            if (paths.Count == 0)
            {
                log("git: nothing to commit");
                return CommitResult.Nothing;
            }
            names = string.Join(", ", paths.Select(Named));
            var settings = committer.RepoPath(config.Path) ?? Path.GetFileName(config.Path);
            var result = committer.Commit(Committer.Message($"Ingest {names}", $"Filed automatically by org-directory ({settings})."),
                paths, trailers: git.Trailers, push: git.Push);
            if (result.Error is not null)
                Tell();
            return result;
        }
        catch (Exception error) when (error is GitException or InvalidOperationException or Win32Exception or TimeoutException or IOException)
        {
            log($"git: commit failed: {error.Message}");
            if (names.Length == 0)
                names = string.Join(", ", report.Written.Select(Path.GetFileNameWithoutExtension));
            Tell();
            return CommitResult.Failed(error.Message);
        }
    }

    /// <summary>[review]: the sync's changes as one item, type "org-changes": arrived (when), changes (the list, as the
    /// report has them), summary_needed (false) and dictation (empty), which keep it the shape of the other items in a
    /// queue of transcripts, and a title, each change's text joined with "; " and the bold marks taken out. Logged with
    /// a count only; nothing it does stops the run.</summary>
    public static void Review(Config config, Report report, Action<string> log, Func<DateTime> now)
    {
        if (config.Review is not { } review || report.Changes.Count == 0)
            return;
        try
        {
            var title = string.Join("; ", report.Changes.Select(c => c.Text.Replace("**", "", StringComparison.Ordinal)));
            review.Open(now).Add(review.Source, "org-changes", new JsonObject
            {
                ["arrived"] = Py.IsoSeconds(now()),
                ["changes"] = report.ChangesJson(),
                ["summary_needed"] = false,
                ["dictation"] = new JsonArray(),
                ["title"] = title.Length > 0 ? title : "org-changes",
            });
            log($"review: queued {report.Changes.Count} directory change(s) under {review.Source}");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log($"review queue failed: {error.Message}");
        }
    }

    /// <summary>run(): the sync under the run lock (TimeoutException if another doesn't finish within ten minutes),
    /// then, if wanted, not a dry run and something was written: [review]'s item, [git]'s commit, and the hook if
    /// after_run is set.</summary>
    public static Report Run(Config config, IGraph graph, bool dryRun = false, bool hook = false, Action<string>? log = null,
        PeopleFirstReader? readPeopleFirst = null, Func<IReadOnlyList<string>, string, string, TimeSpan, Completed>? runner = null,
        DateOnly? today = null, Func<DateTime>? now = null, Action<string, IReadOnlyList<string>>? notify = null)
    {
        log ??= _ => { };
        Report report;
        using (var held = new NamedLock(RunLockName(config)))
        {
            if (!held.Acquire(LockTimeoutSeconds))
                throw new TimeoutException($"Another sync for {config.Path} didn't finish within {LockTimeoutSeconds / 60:0} minutes.");
            report = Once(config, graph, dryRun, today, log, readPeopleFirst, now);
        }
        if (hook && !dryRun && report.Written.Count > 0)
        {
            Review(config, report, log, now ?? (() => DateTime.Now));
            Commit(config, report, log, notify ?? Toast.Notify);
            if (config.AfterRun.Count > 0)
                AfterRun(config, report, log, runner);
        }
        return report;
    }
}
