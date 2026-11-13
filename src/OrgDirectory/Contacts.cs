using System.Text;
using System.Text.Json.Nodes;
using PyCompat;

namespace OrgDirectory;

/// <summary>One group on the contact sheets: someone and everyone under them (depth levels down, 0 for all), and/or
/// a list of people by email.</summary>
public sealed record ContactGroup(string Name, string Root = "", int Depth = 0, IReadOnlyList<string>? People = null)
{
    public IReadOnlyList<string> PeopleList => People ?? [];
}

/// <summary>[contacts]: where the sheets go (never a repo: they hold personal numbers), and the groups on them.</summary>
public sealed record ContactSettings(string Folder, IReadOnlyList<ContactGroup> Groups);

/// <summary>One person on a sheet, with their numbers.</summary>
public sealed record ContactRow(Person Person, string Manager, string Mobile, string WorkMobile, string Home, string WorkPhone);

/// <summary>Contact sheets for an emergency: groups of people with their phone numbers, kept where you can reach them
/// when Teams and Outlook can't be.
///
/// The numbers come from the directory (work phone, work mobile) and, if switched on, People First (personal mobile
/// and phone, for your own reporting line). The sheets are written only to [contacts] folder, never beside the
/// charts, because they hold personal numbers. Three files, for three ways of reaching them:
/// - contacts.html: one page, readable on a phone and printable, with numbers you can tap to call;
/// - contacts.csv: for a spreadsheet;
/// - contacts.vcf: to import into a phone's contacts.
/// Each says when the directory and People First were last read, so a stale sheet is obvious.</summary>
public static class Contacts
{
    /// <summary>members(): the group's people, top first then by name, and any it names that the directory hasn't got.</summary>
    public static (List<Person> Found, List<string> Missing) Members(Org org, ContactGroup group)
    {
        var found = new List<Person>();
        var missing = new List<string>();
        if (group.Root.Length > 0)
        {
            var top = org.Find(group.Root);
            if (top is null)
                missing.Add(group.Root);
            else
            {
                var levels = org.Under(top.Id, group.Depth);
                found.AddRange(levels.Keys.Select(id => org.People[id])
                    .OrderBy(p => levels[p.Id]).ThenBy(p => Py.Lower(p.Name), Py.CodePoints));
            }
        }
        foreach (var who in group.PeopleList)
        {
            var person = org.Find(who);
            if (person is null)
                missing.Add(who);
            else if (!found.Contains(person))
                found.Add(person);
        }
        return (found, missing);
    }

    /// <summary>A number from People First's entry for someone: null if it isn't there (or is null or empty).</summary>
    private static string? Number(JsonObject extra, string key) =>
        extra[key] is { } value && PyCompat.Py.Truthy(value) ? PyCompat.Py.Str(value) : null;

    private static JsonObject? Entry(JsonObject numbers, string key) =>
        numbers[key] is JsonObject entry && entry.Count > 0 ? entry : null;

    public static List<ContactRow> Rows(Org org, IEnumerable<Person> people, JsonObject numbers)
    {
        var output = new List<ContactRow>();
        foreach (var person in people)
        {
            var extra = Entry(numbers, person.Mail) ?? Entry(numbers, person.Login) ?? [];
            var manager = org.Get(person.Manager);
            output.Add(new ContactRow(person, manager?.Name ?? "", Number(extra, "personalMobile") ?? "",
                Number(extra, "companyMobile") ?? person.Mobile, Number(extra, "personalNumber") ?? "", person.WorkPhone));
        }
        return output;
    }

    /// <summary>A number for a tel: link: its digits and any "+".</summary>
    public static string Tel(string number)
    {
        var b = new StringBuilder();
        for (var i = 0; i < number.Length; i++)
        {
            var width = char.IsSurrogatePair(number, i) ? 2 : 1;
            if (number[i] == '+' || Py.IsDigit(number, i))
                b.Append(number, i, width);
            i += width - 1;
        }
        return b.ToString();
    }

    private const string Css = """

        :root{color-scheme:light dark;--bg:#fff;--fg:#111;--muted:#666;--line:#ddd;--accent:#0b5cad}
        @media (prefers-color-scheme:dark){:root{--bg:#121212;--fg:#eee;--muted:#aaa;--line:#333;--accent:#7ab7ff}}
        body{background:var(--bg);color:var(--fg);font:15px/1.4 system-ui,sans-serif;margin:0 auto;max-width:960px;padding:16px}
        h1{font-size:1.4em;margin:0 0 4px}h2{font-size:1.15em;margin:24px 0 8px;border-bottom:2px solid var(--line)}
        .stamp{color:var(--muted);font-size:.9em}.person{padding:8px 0;border-bottom:1px solid var(--line)}
        .name{font-weight:600}.role{color:var(--muted);font-size:.9em}.nums{margin-top:2px}
        .nums a{color:var(--accent);text-decoration:none;margin-right:14px;white-space:nowrap}
        .none{color:var(--muted);font-style:italic}
        @media print{body{max-width:none;font-size:11pt}.person{break-inside:avoid}a{color:#000}}

        """;

    public static string RenderHtml(IReadOnlyList<(string Name, List<ContactRow> Rows)> sections, string stamp)
    {
        var output = new List<string>
        {
            "<!doctype html><html lang='en-GB'><head><meta charset='utf-8'>",
            "<meta name='viewport' content='width=device-width,initial-scale=1'>",
            "<title>Emergency contacts</title><style>", Css.ReplaceLineEndings("\n"), "</style></head><body>",
            "<h1>Emergency contacts</h1>", $"<p class='stamp'>{Py.HtmlEscape(stamp)}</p>",
        };
        foreach (var (name, group) in sections)
        {
            output.Add($"<h2>{Py.HtmlEscape(name)} ({group.Count})</h2>");
            foreach (var row in group)
            {
                var role = string.Join(", ", new[] { row.Person.Title, row.Manager.Length > 0 ? $"reports to {row.Manager}" : "" }.Where(x => x.Length > 0));
                var links = string.Concat(new[] { ("Mobile", row.Mobile), ("Work mobile", row.WorkMobile), ("Home", row.Home), ("Work", row.WorkPhone) }
                    .Where(n => n.Item2.Length > 0)
                    .Select(n => $"<a href='tel:{Tel(n.Item2)}'>{n.Item1}: {Py.HtmlEscape(n.Item2)}</a>"));
                output.Add($"<div class='person'><div class='name'>{Py.HtmlEscape(row.Person.Name)}</div>"
                           + $"<div class='role'>{Py.HtmlEscape(role)}</div><div class='nums'>"
                           + $"{(links.Length > 0 ? links : "<span class=none>No number</span>")}</div></div>");
            }
        }
        output.Add("</body></html>\n");
        return string.Join("\n", output);
    }

    public static string RenderCsv(IReadOnlyList<(string Name, List<ContactRow> Rows)> sections)
    {
        var b = new StringBuilder(Py.CsvRow(["Group", "Name", "Title", "Department", "Manager", "Mobile", "Work mobile", "Home", "Work phone", "Email"]));
        foreach (var (name, group) in sections)
            foreach (var row in group)
            {
                var p = row.Person;
                b.Append(Py.CsvRow([name, p.Name, p.Title, p.Department, row.Manager, row.Mobile, row.WorkMobile, row.Home,
                    row.WorkPhone, p.Mail.Length > 0 ? p.Mail : p.Login]));
            }
        return b.ToString();
    }

    private static string V(string text) => text.Replace("\\", "\\\\").Replace(",", "\\,").Replace(";", "\\;").Replace("\n", "\\n");

    public static string RenderVcf(IReadOnlyList<(string Name, List<ContactRow> Rows)> sections, string stamp)
    {
        var cards = new List<string>();
        var seen = new HashSet<string>();
        foreach (var (_, group) in sections)
            foreach (var row in group)
            {
                var p = row.Person;
                if (!seen.Add(p.Id))
                    continue;
                string last, first;
                var space = p.Name.LastIndexOf(' ');
                if (space >= 0)
                    (last, first) = (p.Name[..space], p.Name[(space + 1)..]);
                else
                    (last, first) = ("", p.Name);
                var card = new List<string> { "BEGIN:VCARD", "VERSION:3.0", $"FN:{V(p.Name)}",
                    $"N:{V(last.Length > 0 ? first : "")};{V(last.Length > 0 ? last : p.Name)};;;" };
                if (p.Title.Length > 0)
                    card.Add($"TITLE:{V(p.Title)}");
                foreach (var (kind, number) in new[] { ("CELL", row.Mobile), ("WORK,CELL", row.WorkMobile), ("HOME", row.Home), ("WORK,VOICE", row.WorkPhone) })
                    if (number.Length > 0)
                        card.Add($"TEL;TYPE={kind}:{number}");
                if (p.Mail.Length > 0)
                    card.Add($"EMAIL;TYPE=WORK:{p.Mail}");
                card.Add($"NOTE:{V(stamp)}");
                card.Add("END:VCARD");
                cards.Add(string.Join("\r\n", card));
            }
        return string.Join("\r\n", cards) + "\r\n";
    }

    /// <summary>write(): the three sheets, each only if it changed; returns the paths written.
    ///
    /// As in Python, a sheet is compared with the file read back in text mode, which turns the vCard's "\r\n" into
    /// "\n", so contacts.vcf never compares equal and is rewritten on every sync (with the same bytes).</summary>
    public static List<string> Write(ContactSettings settings, Org org, JsonObject numbers, string stamp, bool dryRun, Action<string> log)
    {
        var sections = new List<(string, List<ContactRow>)>();
        var written = new List<string>();
        foreach (var group in settings.Groups)
        {
            var (people, missing) = Members(org, group);
            foreach (var who in missing)
                log($"contacts: {group.Name}: {who} isn't in the directory");
            sections.Add((group.Name, Rows(org, people, numbers)));
        }
        var outputs = new (string Name, string Text)[]
        {
            ("contacts.html", RenderHtml(sections, stamp)),
            ("contacts.csv", RenderCsv(sections)),
            ("contacts.vcf", RenderVcf(sections, stamp)),
        };
        foreach (var (name, text) in outputs)
        {
            var path = PyPath.Pure(Path.Combine(settings.Folder, name));
            if (File.Exists(path) && PyStr.ReadText(path) == text)
                continue;
            written.Add(path);
            if (!dryRun)
            {
                Directory.CreateDirectory(settings.Folder);
                File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text));
            }
        }
        var count = sections.Sum(s => s.Item2.Count);
        var withMobile = sections.Sum(s => s.Item2.Count(r => r.Mobile.Length > 0 || r.WorkMobile.Length > 0));
        log($"contacts: {count} entries in {sections.Count} group(s), {withMobile} with a mobile" + (written.Count > 0 ? "" : "; unchanged"));
        return written;
    }
}
