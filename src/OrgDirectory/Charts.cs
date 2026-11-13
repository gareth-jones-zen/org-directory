using System.Text;
using PyCompat;

namespace OrgDirectory;

/// <summary>A chart as markdown, what changed in it, and the running changes file.</summary>
public static class Charts
{
    /// <summary>render(): root and everyone under them (depth levels down, 0 for all), as nested bullets, with each
    /// manager's report counts. A department shows only where it differs from the person's manager's.</summary>
    public static string Render(Org org, string root, string heading, int depth, string changed)
    {
        var members = org.Under(root, depth);
        var people = org.People;
        var top = people[root];
        var lines = new List<string>
        {
            $"# {heading}",
            "",
            $"*{Org.Marker} from the Microsoft 365 directory: {top.Name} and everyone under them"
            + (depth != 0 ? $", {depth} levels down" : "")
            + $". Don't edit it, as the next sync rewrites it. {members.Count} people; the directory last changed on "
            + $"{changed}. A department in brackets is shown where it differs from the person's manager's.*",
            "",
        };
        // Python recursed here, so a manager loop that came back to someone already shown never ended (RecursionError).
        // Each person is shown once; with no loop the output is the same.
        var shown = new HashSet<string>();

        void Line(string id, int level)
        {
            if (!shown.Add(id))
                return;
            var person = people[id];
            var text = new StringBuilder().Append(' ', 2 * level).Append("- **").Append(person.Name).Append("**");
            if (person.Title.Length > 0)
                text.Append(", ").Append(person.Title);
            var manager = id != root ? org.Get(person.Manager) : null;
            if (person.Department.Length > 0 && (manager is null || manager.Department != person.Department))
                text.Append(" (").Append(person.Department).Append(')');
            var children = org.Reports.GetValueOrDefault(id, []).Where(members.ContainsKey).ToList();
            if (children.Count > 0)
            {
                var everyone = org.Under(id, 0).Count - 1;
                text.Append(" · ").Append(children.Count).Append(" report").Append(children.Count != 1 ? "s" : "");
                if (everyone != children.Count)
                    text.Append(", ").Append(everyone).Append(" in all");
            }
            lines.Add(text.ToString());
            foreach (var child in children)
                Line(child, level + 1);
        }

        Line(root, 0);
        return string.Join("\n", lines) + "\n";
    }

    private static string Describe(string name, string title, string department, string? managerId, Org org)
    {
        var text = $"**{name}**";
        if (title.Length > 0)
            text += $", {title}";
        if (department.Length > 0)
            text += $" ({department})";
        if (org.Get(managerId) is { } manager)
            text += $", reporting to {manager.Name}";
        return text;
    }

    private static string Describe(Person person, Org org) => Describe(person.Name, person.Title, person.Department, person.Manager, org);

    /// <summary>changes(): what changed in one chart, as sentences. changesDepth limits them to people that many
    /// levels from the top (0: everyone). Python sorts sets, so people with the same name came out in no fixed order;
    /// here they keep the chart's order.</summary>
    public static List<string> Changes(Org before, Org after, IReadOnlyDictionary<string, int> chartBefore,
        IReadOnlyDictionary<string, int> chartAfter, int changesDepth = 0)
    {
        bool Counts(string id)
        {
            var level = Math.Min(chartBefore.GetValueOrDefault(id, 99), chartAfter.GetValueOrDefault(id, 99));
            return changesDepth == 0 || level <= changesDepth;
        }

        var output = new List<string>();
        foreach (var id in Py.Sorted(chartAfter.Keys.Where(id => !chartBefore.ContainsKey(id)), id => Py.Lower(after.People[id].Name)))
        {
            if (!Counts(id))
                continue;
            var person = after.People[id];
            if (before.People.TryGetValue(id, out var was))
                output.Add($"Moved into this chart: {Describe(person, after)}. Was {Describe(was, before)}.");
            else
                output.Add($"New in the directory: {Describe(person, after)}.");
        }
        foreach (var id in Py.Sorted(chartBefore.Keys.Where(id => !chartAfter.ContainsKey(id)), id => Py.Lower(before.People[id].Name)))
        {
            if (!Counts(id))
                continue;
            if (after.People.TryGetValue(id, out var now))
                output.Add($"Moved out of this chart: now {Describe(now, after)}.");
            else
                output.Add($"Gone from the directory (left, or the account was disabled): {Describe(before.People[id], before)}.");
        }
        foreach (var id in Py.Sorted(chartBefore.Keys.Where(chartAfter.ContainsKey), id => Py.Lower(after.People[id].Name)))
        {
            if (!Counts(id))
                continue;
            var old = before.People[id];
            var @new = after.People[id];
            var name = $"**{@new.Name}**";
            if (old.Name != @new.Name)
                output.Add($"Renamed: **{old.Name}** is now {name}.");
            if (old.Title != @new.Title)
                output.Add($"New title: {name}, {Or(old.Title)} → {Or(@new.Title)}.");
            if (old.Department != @new.Department)
                output.Add($"New department: {name}, {Or(old.Department)} → {Or(@new.Department)}.");
            if (old.Manager != @new.Manager)
            {
                var was = before.Get(old.Manager);
                var now = after.Get(@new.Manager);
                output.Add($"New manager: {name}, {(was is null ? "(none)" : was.Name)} → {(now is null ? "(none)" : now.Name)}.");
            }
        }
        return output;
    }

    private static string Or(string text) => text.Length > 0 ? text : "(none)";

    public static string ChangesHeader =>
        "# Directory changes\n\n"
        + $"*{Org.Marker}: what each sync found had changed in the Microsoft 365 directory, newest first.*\n";

    /// <summary>log_changes(): today's changes at the top of the changes file, newest first.</summary>
    public static void LogChanges(string path, string chart, IReadOnlyList<string> found, DateOnly when)
    {
        var text = File.Exists(path) ? PyStr.ReadText(path) : ChangesHeader;
        var cut = text.IndexOf("\n## ", StringComparison.Ordinal);
        var head = cut < 0 ? text : text[..cut];
        var rest = cut < 0 ? "" : text[(cut + 4)..];
        var entry = $"## {Py.IsoDate(when)}: {chart}\n\n" + string.Concat(found.Select(item => $"- {item}\n"));
        rest = rest.Length > 0 ? "## " + rest : "";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        PyStr.WriteText(path, head.TrimEnd('\n') + "\n\n" + entry + (rest.Length > 0 ? "\n" + rest : ""));
    }
}
