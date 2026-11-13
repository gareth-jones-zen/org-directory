using System.Text.Json.Nodes;

namespace OrgDirectory.Tests;

/// <summary>Graph replaying a list of users, as the Python tests' FakeGraph.</summary>
public sealed class FakeGraph(IEnumerable<JsonNode?> users) : IGraph
{
    public IEnumerable<JsonNode?> Pages(string url, IReadOnlyList<string> scopes)
    {
        Assert.Contains("$expand=manager", url);
        Assert.DoesNotContain(" ", url);
        return users.Select(u => u?.DeepClone()).ToList();
    }

    public void Token()
    {
    }
}

public static class Users
{
    public static JsonObject User(string id, string name, string title, string dept, string? manager = null, string kind = "Member") => new()
    {
        ["id"] = id,
        ["displayName"] = name,
        ["jobTitle"] = title,
        ["department"] = dept,
        ["userPrincipalName"] = $"{id}@example.com",
        ["mail"] = $"{id}@example.com",
        ["userType"] = kind,
        ["manager"] = manager is null ? null : new JsonObject { ["id"] = manager },
    };

    public static List<JsonObject> Org() =>
    [
        User("ceo", "Cara Chief", "CEO", "Directors"),
        User("cto", "Tom Tech", "Technology Director", "Technology", "ceo"),
        User("fd", "Fay Finance", "Finance Director", "Finance", "ceo"),
        User("arch", "Ann Architect", "Principal Architect", "Technology", "cto"),
        User("sa1", "Bob Builder", "Solutions Architect", "Technology", "arch"),
        User("sa2", "Dee Design", "Solutions Architect", "Architecture", "arch"),
        User("em", "Eve Manager", "Engineering Manager", "Technology", "cto"),
        User("guest", "Gus Guest", "", "", "cto", kind: "Guest"),
    ];
}

/// <summary>A folder in the temp directory, deleted afterwards.</summary>
public sealed class TempFolder : IDisposable
{
    public string Path { get; } = PyCompat.PyPath.Resolve(
        Directory.CreateTempSubdirectory("org-directory-tests-").FullName);

    public string File(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string Read(params string[] parts) => PyCompat.PyStr.ReadText(File(parts));

    public void Write(string name, string text) => System.IO.File.WriteAllText(File(name), text);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
        }
        catch (IOException)
        {
        }
    }
}
