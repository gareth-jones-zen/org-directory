using System.Text.Json.Nodes;
using WinBackground;

namespace OrgDirectory.Tests;

/// <summary>Golden/misc.json, from the Python version (tests/parity/misc_goldens.py): csv quoting, tel: links,
/// html.escape and the vCard escaping, str.lower() and sorting by it, and every settings error.</summary>
public sealed class MiscTests
{
    private static readonly JsonNode Golden = JsonNode.Parse(File.ReadAllText(ScenarioTests.GoldenFile("misc.json")))!;

    private static IEnumerable<JsonNode> Cases(string name) => Golden[name]!.AsArray().Select(n => n!);

    [Fact]
    public void csv_rows_are_quoted_as_the_csv_module_quotes_them()
    {
        foreach (var c in Cases("csv"))
            Assert.Equal((string)c["out"]!, Py.CsvRow(c["row"]!.AsArray().Select(s => (string)s!).ToList()));
    }

    [Fact]
    public void tel_links_keep_what_python_keeps()
    {
        foreach (var c in Cases("tel"))
            Assert.Equal((string)c["out"]!, Contacts.Tel((string)c["in"]!));
    }

    [Fact]
    public void html_and_vcard_escaping_match()
    {
        var v = typeof(Contacts).GetMethod("V", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        foreach (var c in Cases("escape"))
        {
            Assert.Equal((string)c["html"]!, Py.HtmlEscape((string)c["in"]!));
            Assert.Equal((string)c["vcf"]!, (string)v.Invoke(null, [(string)c["in"]!])!);
        }
    }

    [Fact]
    public void lower_and_sorting_by_it_match()
    {
        foreach (var c in Cases("lower"))
            Assert.Equal((string)c["out"]!, Py.Lower((string)c["in"]!));
        var names = Golden["names"]!.AsArray().Select(n => (string)n!).ToList();
        var order = Py.Sorted(Enumerable.Range(0, names.Count), i => Py.Lower(names[i]));
        Assert.Equal(Golden["order"]!.AsArray().Select(n => (int)n!), order);
    }

    [Fact]
    public void settings_load_or_fail_as_python_does()
    {
        foreach (var c in Cases("settings"))
        {
            using var dir = new TempFolder();
            var source = (string)c["settings"]!;
            dir.Write("config.toml", source + "\n");
            string Mask(string text) => text.Replace(dir.Path, "{DIR}", StringComparison.Ordinal);
            try
            {
                var config = Config.Load(dir.File("config.toml"));
                var ok = c["ok"] ?? throw new Xunit.Sdk.XunitException($"Python refused, .NET loaded: {source}");
                Assert.Equal((string)ok["tenant"]!, config.Tenant);
                Assert.Equal((string)ok["daily_at"]!, config.DailyAt);
                Assert.Equal(ok["charts"]!.AsArray().Select(n => (string)n!), config.Charts.Select(ch => ch.Name));
                Assert.Equal((string)ok["state_file"]!, config.StateFile.StartsWith(dir.Path, StringComparison.Ordinal) ? Mask(config.StateFile) : "(elsewhere)");
                Assert.Equal((string?)ok["changes_file"], config.ChangesFile is null ? null : Mask(config.ChangesFile));
                var pf = ok["people_first"]!.AsArray();
                Assert.Equal((bool)pf[0]!, config.PeopleFirst.Enabled);
                Assert.Equal((string)pf[1]!, config.PeopleFirst.Url);
                Assert.Equal((string)pf[2]!, config.PeopleFirst.TenantCode);
                Assert.Equal((string)pf[3]!, config.PeopleFirst.Account);
            }
            catch (PyValueError error)
            {
                Assert.Equal((string?)c["error"] ?? $"(loaded) {source}", Mask(error.Message));
            }
        }
    }

    [Fact]
    public void the_run_lock_is_named_as_python_names_it()
    {
        foreach (var c in Cases("locks"))
        {
            var config = new Config { Path = (string)c["path"]!, StateFile = "s", LogFile = "l" };
            Assert.Equal((string)c["name"]!, Sync.RunLockName(config));
        }
    }
}
