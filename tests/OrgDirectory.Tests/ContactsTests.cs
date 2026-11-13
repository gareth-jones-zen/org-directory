using System.Text.Json.Nodes;
using static OrgDirectory.Tests.Users;

namespace OrgDirectory.Tests;

/// <summary>The Python tests in tests/test_contacts.py, by name.</summary>
public sealed class ContactsTests : IDisposable
{
    private const string Settings = """
        state_file = "state.json"
        log_file = "log.txt"
        [[chart]]
        name = "tech"
        root = "cto@example.com"
        output = "org/tech.md"
        [contacts]
        folder = "private"
        [[contacts.group]]
        name = "My team"
        root = "arch@example.com"
        depth = 1
        [[contacts.group]]
        name = "Peers"
        people = ["em@example.com", "nobody@example.com"]

        """;

    private const string PeopleFirstSection = """

        [people_first]
        enabled = {0}
        url = "https://example.people-first.com"

        """;

    private readonly TempFolder dir = new();

    public void Dispose() => dir.Dispose();

    internal static List<JsonObject> WithPhones(List<JsonObject> users)
    {
        foreach (var u in users)
        {
            u["businessPhones"] = new JsonArray($"0161 000 {u["id"]}");
            u["mobilePhone"] = (string?)u["id"] == "sa2" ? "07700 900001" : null;
        }
        return users;
    }

    private Config Load(string? peopleFirst = null)
    {
        dir.Write("config.toml", Settings + (peopleFirst is null ? "" : string.Format(System.Globalization.CultureInfo.InvariantCulture, PeopleFirstSection, peopleFirst)));
        return Config.Load(dir.File("config.toml"));
    }

    private (Report Report, List<string> Logs) Sync(Config config, PeopleFirstReader? read = null)
    {
        var logs = new List<string>();
        var report = OrgDirectory.Sync.Once(config, new FakeGraph(WithPhones(Org())), false, new DateOnly(2026, 11, 8), logs.Add, read);
        return (report, logs);
    }

    private string Read(string name) => dir.Read("private", name);

    private static PeopleFirstReader Numbers(string json) => (_, _) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void settings_parse_and_people_first_defaults()
    {
        var config = Load("true");
        Assert.Equal(["My team", "Peers"], config.Contacts!.Groups.Select(g => g.Name));
        Assert.Equal(dir.File("private"), config.Contacts.Folder);
        Assert.True(config.PeopleFirst.Enabled);
        Assert.Equal("example", config.PeopleFirst.TenantCode);
        Assert.False(Load("false").PeopleFirst.Enabled);
        Assert.False(Load().PeopleFirst.Enabled);
    }

    [Fact]
    public void sheets_from_the_directory_alone()
    {
        var config = Load();
        var (report, logs) = Sync(config, (_, _) => throw new InvalidOperationException("People First is switched off"));
        Assert.Equal(3, report.Contacts!.Count);
        Assert.DoesNotContain(dir.File("private", "contacts.html"), report.Written);   // never to the hook
        var page = Read("contacts.html");
        Assert.Contains("<h2>My team (3)</h2>", page, StringComparison.Ordinal);
        Assert.Contains("Ann Architect", page, StringComparison.Ordinal);
        Assert.Contains("href='tel:07700900001'>Work mobile: 07700 900001</a>", page, StringComparison.Ordinal);   // the directory's mobile
        Assert.Contains("People First isn&#x27;t used.", page, StringComparison.Ordinal);
        Assert.Contains("nobody@example.com isn't in the directory", string.Join(" ", logs), StringComparison.Ordinal);
        var rows = Read("contacts.csv").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["Group", "Name", "Title"], rows[0].Split(',')[..3]);
        Assert.Equal(1 + 3 + 1, rows.Length);
        var cards = Read("contacts.vcf");
        Assert.Equal(4, cards.Split("BEGIN:VCARD").Length - 1);
        Assert.Contains("TEL;TYPE=WORK,CELL:07700 900001", cards, StringComparison.Ordinal);
        // Phones never reach the charts or the snapshot.
        Assert.DoesNotContain("0161", dir.Read("org", "tech.md"), StringComparison.Ordinal);
        Assert.DoesNotContain("0161", dir.Read("state.json"), StringComparison.Ordinal);
    }

    [Fact]
    public void people_first_numbers_are_added_by_work_email()
    {
        var config = Load("true");
        Sync(config, Numbers("""{"sa1@example.com": {"personalMobile": "07700 900123", "personalNumber": "0161 496 0000"}}"""));
        var page = Read("contacts.html");
        Assert.Contains("Mobile: 07700 900123", page, StringComparison.Ordinal);
        Assert.Contains("Home: 0161 496 0000", page, StringComparison.Ordinal);
        Assert.Contains("People First read", page, StringComparison.Ordinal);
        Assert.Contains("TEL;TYPE=CELL:07700 900123", Read("contacts.vcf"), StringComparison.Ordinal);
        var state = JsonNode.Parse(dir.Read("state.json"))!;
        Assert.Equal("07700 900123", (string?)state["people_first"]!["numbers"]!["sa1@example.com"]!["personalMobile"]);
    }

    [Fact]
    public void a_failed_people_first_read_keeps_the_last_numbers()
    {
        var config = Load("true");
        Sync(config, Numbers("""{"sa1@example.com": {"personalMobile": "07700 900123"}}"""));
        var (report, _) = Sync(config, (_, _) => throw new InvalidOperationException("People First answered 503"));
        Assert.Equal("People First answered 503", report.PeopleFirstFailed);
        Assert.False(report.PeopleFirstStale);
        var page = Read("contacts.html");
        Assert.Contains("Mobile: 07700 900123", page, StringComparison.Ordinal);
        Assert.Contains("(the latest read failed)", page, StringComparison.Ordinal);
    }

    [Fact]
    public void a_first_read_that_fails_is_stale_at_once()
    {
        var config = Load("true");
        var (report, _) = Sync(config, (_, _) => throw new InvalidOperationException("no sign-in"));
        Assert.True(report.PeopleFirstStale);
        Assert.Contains("personal numbers are missing", Read("contacts.html"), StringComparison.Ordinal);
    }

    [Fact]
    public void switching_people_first_off_drops_its_numbers()
    {
        Sync(Load("true"), Numbers("""{"sa1@example.com": {"personalMobile": "07700 900123"}}"""));
        Sync(Load("false"));
        Assert.DoesNotContain("07700 900123", Read("contacts.html"), StringComparison.Ordinal);
        Assert.False(JsonNode.Parse(dir.Read("state.json"))!.AsObject().ContainsKey("people_first"));
    }
}
