// Local parity checks against the Python version, on GJ's laptop only (see tests/parity/live.py). Never published,
// never run by CI. It prints counts and hashes, never a name, title, email or number.
//
//   ParityRunner record <cache copy> <tenant> <login hint> <out.json>     one read-only directory GET (silent sign-in)
//   ParityRunner people-first <settings.toml> <out.json>                  People First's numbers, on the settings' profile
//   ParityRunner who <settings.toml> <query>...                           `who`: exit codes and output hashes
//   ParityRunner render <settings.toml> <directory.json> <numbers.json> <now> <report.json>
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using M365Graph;
using OrgDirectory;
using PyCompat;

static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

switch (args.FirstOrDefault())
{
    case "record":
    {
        var graph = new GraphClient(new GraphClientOptions
        {
            CachePath = args[1],
            Tenant = args[2],
            LoginHint = args[3],
            Scopes = Org.Scopes,
            Interactive = false,
            SignInHint = "(parity: silent only)",
        });
        var users = new JsonArray();
        await foreach (var user in graph.PagesAsync(Org.Url, Org.Scopes))
            users.Add(user?.DeepClone());
        var bytes = Encoding.UTF8.GetBytes(users.ToJsonString());
        File.WriteAllBytes(args[4], bytes);
        Console.WriteLine($"recorded {users.Count} accounts, {bytes.Length} bytes");
        return 0;
    }
    case "people-first":
    {
        var config = Config.Load(args[1]);
        var found = await PeopleFirst.FetchAsync(config.PeopleFirst, _ => { });
        var text = PyJson.Dump(found);
        File.WriteAllText(args[2], text, new UTF8Encoding(false));
        Console.WriteLine($"people first: {found.Count} with a number, sha256 {Sha(Encoding.UTF8.GetBytes(text))}");
        return 0;
    }
    case "render":
    {
        var config = Config.Load(args[1]);
        var users = JsonNode.Parse(File.ReadAllText(args[2]))!.AsArray();
        var numbers = args[3] == "FAIL" ? null : JsonNode.Parse(File.ReadAllText(args[3]))!.AsObject();
        var now = DateTime.ParseExact(args[4], "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        var logs = new List<string>();
        var report = Sync.Once(config, new Replay(users), false, DateOnly.FromDateTime(now), logs.Add, (_, _) => numbers?.DeepClone().AsObject()
            ?? throw new InvalidOperationException("People First answered 503"), () => now);
        File.WriteAllText(args[5], new JsonObject
        {
            ["report"] = report.ToJson(),
            ["log"] = new JsonArray(logs.Select(l => (JsonNode)JsonValue.Create(l)!).ToArray()),
        }.ToJsonString(), new UTF8Encoding(false));
        Console.WriteLine($"rendered: {report.Written.Count} written, {report.Changes.Count} change(s), {report.Unchanged} unchanged, "
                          + $"{report.People} people, {report.Failed.Count} failed");
        return 0;
    }
    case "who":
    {
        // ParityRunner who <settings.toml> <query>...: each query's exit code and the SHA-256 of its output
        foreach (var query in args.Skip(2))
        {
            var output = new StringWriter();
            var code = Cli.Run(["--config", args[1], "who", query], new CliHost { Out = output, Err = output });
            var text = output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
            Console.WriteLine($"{code} {Sha(Encoding.UTF8.GetBytes(text))}");
        }
        return 0;
    }
    default:
        Console.Error.WriteLine("usage: ParityRunner record|people-first|render ...");
        return 2;
}

sealed class Replay(JsonArray users) : IGraph
{
    public IEnumerable<JsonNode?> Pages(string url, IReadOnlyList<string> scopes) => users.Select(u => u?.DeepClone());

    public void Token()
    {
    }
}
