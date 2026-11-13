using System.Text.Json.Nodes;
using PyCompat;

namespace OrgDirectory;

/// <summary>Someone in the directory. Phones are for the contact sheets only: never in a chart or the snapshot.</summary>
public sealed record Person(string Id, string Name, string Title = "", string Department = "", string? Manager = null,
    string Login = "", string Mail = "", string WorkPhone = "", string Mobile = "")
{
    /// <summary>record(): what a chart shows, and what a change is measured on.</summary>
    public bool SameRecord(Person other) =>
        Name == other.Name && Title == other.Title && Department == other.Department && Manager == other.Manager;
}

/// <summary>The Microsoft 365 directory as org charts: fetch everyone once, cut charts from it, and say what changed.
///
/// What it's built on, from reading Zen's directory (Nov 2026):
/// - One query gets everyone, with their manager (/users?$expand=manager): about 1,600 accounts in seconds.
/// - Only enabled member accounts count. Guests and disabled accounts are left out; a leaver's disabled account can
///   keep its manager for weeks, and would otherwise still show.
/// - Job title and department are well filled in; mobile numbers aren't. Phones are never written into a chart or the
///   state file, only into the contact sheets.
/// - A chart is someone and everyone under them, to a depth if set.
/// - Changes are found by comparing with the last snapshot.</summary>
public sealed class Org
{
    public static readonly string[] Scopes = ["User.Read.All"];
    public const string Fields = "id,displayName,jobTitle,department,userPrincipalName,mail,userType,businessPhones,mobilePhone";
    public const string Url = "/users?$filter=accountEnabled%20eq%20true&$select=" + Fields + "&$expand=manager($select=id)&$top=999";
    public const string Marker = "Written by org-directory";

    /// <summary>Everyone, by id, in the order the directory gave them (a later duplicate id keeps the first place).</summary>
    public OrderedDictionary<string, Person> People { get; }

    /// <summary>Each manager's direct reports, by name (code point order, stable).</summary>
    public Dictionary<string, List<string>> Reports { get; } = [];

    public Org(OrderedDictionary<string, Person> people)
    {
        People = people;
        var reports = new Dictionary<string, List<string>>();
        foreach (var person in people.Values)
            if (person.Manager is { } manager && people.ContainsKey(manager))
            {
                if (!reports.TryGetValue(manager, out var list))
                    reports[manager] = list = [];
                list.Add(person.Id);
            }
        foreach (var (manager, ids) in reports)
            Reports[manager] = Py.Sorted(ids, id => Py.Lower(people[id].Name));
    }

    public Person? Get(string? id) => id is not null && People.TryGetValue(id, out var person) ? person : null;

    /// <summary>By email or sign-in name (the first match), else by name if exactly one person has it.</summary>
    public Person? Find(string who)
    {
        who = Py.Lower(PyStr.Strip(who));
        foreach (var person in People.Values)
            if (who == person.Login || who == person.Mail)
                return person;
        Person? named = null;
        var count = 0;
        foreach (var person in People.Values)
            if (Py.Lower(person.Name) == who)
            {
                named ??= person;
                count++;
            }
        return count == 1 ? named : null;
    }

    public List<Person> Search(string text)
    {
        text = Py.Lower(PyStr.Strip(text));
        return Py.Sorted(People.Values.Where(p => Py.Lower(p.Name).Contains(text, StringComparison.Ordinal)
                                                  || text == p.Login || text == p.Mail), p => Py.Lower(p.Name));
    }

    /// <summary>Everyone in root's chart, with their level (root is 0), breadth first. depth 0 means no limit.</summary>
    public OrderedDictionary<string, int> Under(string root, int depth = 0)
    {
        var found = new OrderedDictionary<string, int>();
        var queue = new Queue<(string Id, int Level)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (id, level) = queue.Dequeue();
            if (found.ContainsKey(id))
                continue;   // a loop in the manager chain shouldn't hang a sync
            found[id] = level;
            if (depth == 0 || level < depth)
                foreach (var child in Reports.GetValueOrDefault(id, []))
                    queue.Enqueue((child, level + 1));
        }
        return found;
    }

    public List<Person> Chain(string id)
    {
        var output = new List<Person>();
        var seen = new HashSet<string>();
        var person = Get(id);
        while (person is not null && person.Manager is { } manager && People.ContainsKey(manager) && seen.Add(manager))
        {
            person = People[manager];
            output.Add(person);
        }
        return output;
    }

    /// <summary>The state file's "people": {id: {name, title, department, manager, login, mail}}.</summary>
    public JsonObject Snapshot()
    {
        var snapshot = new JsonObject();
        foreach (var (id, p) in People)
            snapshot[id] = new JsonObject
            {
                ["name"] = p.Name,
                ["title"] = p.Title,
                ["department"] = p.Department,
                ["manager"] = p.Manager,
                ["login"] = p.Login,
                ["mail"] = p.Mail,
            };
        return snapshot;
    }

    public static Org FromSnapshot(JsonObject? data)
    {
        var people = new OrderedDictionary<string, Person>();
        foreach (var (id, node) in data ?? new JsonObject())
        {
            if (node is not JsonObject r || !r.TryGetPropertyValue("name", out var name))
                throw new InvalidDataException($"The state file's entry for {id} has no name.");
            people[id] = new Person(id, Text(name), Text(r["title"]), Text(r["department"]),
                r["manager"] is { } manager ? Text(manager) : null, Text(r["login"]), Text(r["mail"]));
        }
        return new Org(people);
    }

    private static string Text(JsonNode? node) => node is null ? "" : PyCompat.Py.Str(node);

    private static string Field(JsonObject user, string key) =>
        user[key] is JsonValue value && PyCompat.Py.Truthy(value) ? PyCompat.Py.Str(value) : "";

    /// <summary>fetch(): every enabled member account, through the directory's paging.</summary>
    public static Org Fetch(IGraph graph)
    {
        var people = new OrderedDictionary<string, Person>();
        foreach (var node in graph.Pages(Url, Scopes))
        {
            if (node is not JsonObject user || user["userType"] is not JsonValue kind || PyCompat.Py.Str(kind) != "Member"
                || kind.GetValueKind() != System.Text.Json.JsonValueKind.String)
                continue;
            var id = PyCompat.Py.Str(user["id"]);
            var phones = user["businessPhones"] is JsonArray { Count: > 0 } list ? list[0] : null;
            people[id] = new Person(
                Id: id,
                Name: PyStr.Strip(Field(user, "displayName")),
                Title: PyStr.Strip(Field(user, "jobTitle")),
                Department: PyStr.Strip(Field(user, "department")),
                Manager: user["manager"] is JsonObject manager && manager["id"] is { } managerId ? PyCompat.Py.Str(managerId) : null,
                Login: Py.Lower(Field(user, "userPrincipalName")),
                Mail: Py.Lower(Field(user, "mail")),
                WorkPhone: phones is JsonValue phone && PyCompat.Py.Truthy(phone) ? PyStr.Strip(PyCompat.Py.Str(phone)) : "",
                Mobile: PyStr.Strip(Field(user, "mobilePhone")));
        }
        return new Org(people);
    }
}
