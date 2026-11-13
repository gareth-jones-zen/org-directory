using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PyCompat;
using Tomlyn;
using Tomlyn.Model;

namespace OrgDirectory;

/// <summary>One [[chart]]: someone, and everyone under them.</summary>
public sealed record Chart(string Name, string Root, string Output, string Heading = "", int Depth = 0, int ChangesDepth = 0);

/// <summary>The settings file: who signs in, and which org charts to write.
///
///     login_hint = "you@example.com"          # optional
///     tenant = "organizations"                # optional: a tenant id pins sign-in to one organisation
///     msal_cache = "..."                      # optional: share another tool's sign-in
///     state_file = "..."                      # optional: the last snapshot of the directory
///     log_file = "..."                        # optional
///     after_run = ["python", "commit.py"]     # optional: run after a sync that wrote files (JSON on stdin)
///     daily_at = "09:00"                      # optional: when autostart's daily sync runs
///     changes_file = "org/changes.md"         # optional: a running log of what changed, newest first
///
///     [[chart]]
///     name = "technology"
///     root = "head.of.tech@example.com"       # the person at the top, by email or sign-in name
///     output = "org/technology.md"            # relative to this file
///     heading = "Technology"                  # optional: the file's title
///     depth = 0                               # optional: levels below the top to include (0: all)
///     changes_depth = 0                       # optional: report changes only this far down (0: all)
///
///     [contacts]                              # optional: emergency contact sheets, with phone numbers
///     folder = "~/OneDrive/Emergency contacts"   # NOT a repo: they hold personal numbers
///
///     [[contacts.group]]
///     name = "My team"
///     root = "you@example.com"                # someone and everyone under them...
///     depth = 1                               # ...this many levels down (0: all)
///     people = ["peer@example.com"]           # ...and/or a list, by email
///
///     [people_first]                          # optional: add the numbers People First lets you see
///     enabled = true                          # the switch: false (or no section) uses the directory alone
///     url = "https://yourorg.people-first.com"
///     tenant_code = "yourorg"                 # optional: default, the first part of url
///     account = "you@example.com"             # optional: the account to pick at sign-in (default login_hint)
///     edge_profile = "..."                    # optional: its own Edge profile (default under LOCALAPPDATA)
///
/// Paths may use ~ and %VARIABLES%; relative paths are relative to the settings file. The same file the Python version
/// read, read the same way: paths come out as the same text, so the state, log, locks and hook are shared.</summary>
public sealed partial class Config
{
    public const string EnvConfig = "ORG_DIRECTORY_CONFIG";

    public const string Template = """
        # org-directory settings. See the README.
        # login_hint = "you@example.com"
        # changes_file = "org/changes.md"

        # One [[chart]] per org chart: someone, and everyone under them.
        # [[chart]]
        # name = "my-department"
        # root = "head.of.department@example.com"
        # output = "org/my-department.md"

        """;

    public required string Path { get; init; }
    public List<Chart> Charts { get; } = [];
    public string? LoginHint { get; init; }
    public string Tenant { get; init; } = "organizations";
    public string? MsalCache { get; init; }
    public required string StateFile { get; init; }
    public required string LogFile { get; init; }
    public List<string> AfterRun { get; init; } = [];
    public string DailyAt { get; init; } = "09:00";
    public string? ChangesFile { get; init; }
    public ContactSettings? Contacts { get; set; }
    public PeopleFirstSettings PeopleFirst { get; set; } = new();

    public static string? Environment(string name) =>
        string.IsNullOrEmpty(name) || name.Contains('\0') ? null : System.Environment.GetEnvironmentVariable(name);

    /// <summary>%APPDATA%\org-directory\config.toml (or under the home folder if APPDATA isn't set).</summary>
    public static string DefaultPath(Func<string, string?>? env = null)
    {
        env ??= Environment;
        var appData = env("APPDATA");
        return PyPath.Pure(System.IO.Path.Combine(string.IsNullOrEmpty(appData) ? PyPath.Home(env) : appData, "org-directory", "config.toml"));
    }

    /// <summary>%LOCALAPPDATA%\org-directory: the default state, log and Edge profile.</summary>
    public static string LocalFolder(Func<string, string?>? env = null)
    {
        env ??= Environment;
        var local = env("LOCALAPPDATA");
        return PyPath.Pure(System.IO.Path.Combine(string.IsNullOrEmpty(local) ? PyPath.Home(env) : local, "org-directory"));
    }

    private static string PathSetting(string value, string folder, Func<string, string?> env)
    {
        var expanded = PyPath.Pure(PyPath.ExpandVars(PyPath.ExpandUser(value, env), env));
        return PyPath.IsAbsolute(expanded) ? expanded : PyPath.Resolve(System.IO.Path.Combine(folder, expanded));
    }

    /// <summary>12 hex characters from the settings path, ignoring case: the default state file's name.</summary>
    internal static string StateKey(string path) =>
        System.Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Py.Lower(path))))[..12];

    [GeneratedRegex(@"\A([01][0-9]|2[0-3]):[0-5][0-9]\z", RegexOptions.CultureInvariant)]
    private static partial Regex DailyAtPattern();

    /// <summary>load(path): the given path, else ORG_DIRECTORY_CONFIG, else the default. Throws FileNotFoundException if
    /// there's no file there, and PyValueError for settings that can't be used.</summary>
    public static Config Load(string? path = null, Func<string, string?>? env = null)
    {
        env ??= Environment;
        var given = !string.IsNullOrEmpty(path) ? path : env(EnvConfig) is { Length: > 0 } fromEnv ? fromEnv : DefaultPath(env);
        var full = PyPath.Resolve(given);
        if (!File.Exists(full))
            throw new FileNotFoundException($"No settings file at {full}. Run `org-directory config` to create one.");
        TomlTable raw;
        try
        {
            var text = PyStr.ReadText(full);
            if (text.StartsWith('﻿'))
                throw new PyValueError("Invalid statement (at line 1, column 1)");   // tomllib refuses a byte order mark
            raw = TomlSerializer.Deserialize(text, TomlContext.Default.TomlTable) ?? [];
        }
        catch (Exception error) when (error is not (IOException or UnauthorizedAccessException or PyValueError))
        {
            throw new PyValueError($"{full}: {error.Message}");
        }
        var folder = System.IO.Path.GetDirectoryName(full)!;
        var local = LocalFolder(env);
        string? Text(string key) => raw.TryGetValue(key, out var value) ? Str(value, key) : null;
        string? OptionalPath(string key) => Truthy(raw, key) ? PathSetting(Str(raw[key], key), folder, env) : null;

        var dailyAt = Text("daily_at") ?? "09:00";
        // Python's \d matches any Unicode digit; the times Task Scheduler takes are ASCII, and so is this check.
        if (!DailyAtPattern().IsMatch(dailyAt))
            throw new PyValueError($"daily_at in {full} should be a time such as \"09:00\".");
        var config = new Config
        {
            Path = full,
            LoginHint = Text("login_hint"),
            Tenant = Text("tenant") ?? "organizations",
            MsalCache = OptionalPath("msal_cache"),
            StateFile = OptionalPath("state_file") ?? PyPath.Pure(System.IO.Path.Combine(local, $"state-{StateKey(full)}.json")),
            LogFile = OptionalPath("log_file") ?? PyPath.Pure(System.IO.Path.Combine(local, "org-directory.log")),
            AfterRun = raw.TryGetValue("after_run", out var hook) ? Strings(hook, "after_run") : [],
            DailyAt = dailyAt,
            ChangesFile = OptionalPath("changes_file"),
        };

        var names = new HashSet<string>();
        var i = 0;
        foreach (var item in Tables(raw, "chart", full))
        {
            i++;
            var name = item.TryGetValue("name", out var named) ? Str(named, "name") : $"chart{i}";
            var missing = new[] { "root", "output" }.Where(key => !Truthy(item, key)).ToList();
            if (missing.Count > 0)
                throw new PyValueError($"[[chart]] {name} in {full} has no {string.Join(", ", missing)}.");
            if (!names.Add(name))
                throw new PyValueError($"Two charts in {full} are called {name}.");
            config.Charts.Add(new Chart(name, Str(item["root"], "root"), PathSetting(Str(item["output"], "output"), folder, env),
                item.TryGetValue("heading", out var heading) ? Str(heading, "heading") : "",
                item.TryGetValue("depth", out var depth) ? Int(depth, "depth") : 0,
                item.TryGetValue("changes_depth", out var changesDepth) ? Int(changesDepth, "changes_depth") : 0));
        }
        config.Contacts = ContactsSection(raw.TryGetValue("contacts", out var contacts) ? contacts : null, full, folder, env);
        config.PeopleFirst = PeopleFirstSection(raw.TryGetValue("people_first", out var peopleFirst) ? peopleFirst : null, config, local, env);
        return config;
    }

    private static ContactSettings? ContactsSection(object? value, string path, string folder, Func<string, string?> env)
    {
        if (value is not TomlTable raw || raw.Count == 0)
        {
            if (value is null || value is TomlTable)
                return null;
            throw new PyValueError($"{path}: contacts must be a [contacts] table.");
        }
        if (!Truthy(raw, "folder"))
            throw new PyValueError($"[contacts] in {path} has no folder.");
        var groups = new List<ContactGroup>();
        var i = 0;
        foreach (var item in Tables(raw, "group", path))
        {
            i++;
            var name = item.TryGetValue("name", out var named) ? Str(named, "name") : $"Group {i}";
            if (!Truthy(item, "root") && !Truthy(item, "people"))
                throw new PyValueError($"[[contacts.group]] {name} in {path} needs a root, people, or both.");
            groups.Add(new ContactGroup(name,
                item.TryGetValue("root", out var root) ? Str(root, "root") : "",
                item.TryGetValue("depth", out var depth) ? Int(depth, "depth") : 0,
                item.TryGetValue("people", out var people) ? Strings(people, "people") : []));
        }
        if (groups.Count == 0)
            throw new PyValueError($"[contacts] in {path} has no [[contacts.group]].");
        return new ContactSettings(PathSetting(Str(raw["folder"], "folder"), folder, env), groups);
    }

    private static PeopleFirstSettings PeopleFirstSection(object? value, Config config, string local, Func<string, string?> env)
    {
        if (value is not TomlTable raw || !Truthy(raw, "enabled"))
            return new PeopleFirstSettings(Enabled: false);
        var url = (raw.TryGetValue("url", out var u) ? Str(u, "url") : "").TrimEnd('/');
        if (!url.StartsWith("https://", StringComparison.Ordinal))
            throw new PyValueError($"[people_first] in {config.Path} needs url, such as \"https://yourorg.people-first.com\".");
        var tenant = Truthy(raw, "tenant_code") ? Str(raw["tenant_code"], "tenant_code") : PeopleFirstClient.Settings.DefaultTenantCode(url);
        if (tenant.Length == 0)
            throw new PyValueError($"[people_first] in {config.Path} needs tenant_code: it couldn't be worked out from the url.");
        var account = Truthy(raw, "account") ? Str(raw["account"], "account") : !string.IsNullOrEmpty(config.LoginHint) ? config.LoginHint : "";
        return new PeopleFirstSettings(true, url, tenant, account,
            Truthy(raw, "edge_profile") ? PathSetting(Str(raw["edge_profile"], "edge_profile"), System.IO.Path.GetDirectoryName(config.Path)!, env)
                                        : PyPath.Pure(System.IO.Path.Combine(local, "edge-profile")));
    }

    /// <summary>create_default(): the default settings file, from the template if it isn't there yet.</summary>
    public static string CreateDefault(Func<string, string?>? env = null)
    {
        var path = DefaultPath(env);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            PyStr.WriteText(path, Template.ReplaceLineEndings("\n"));
        }
        return path;
    }

    private static IEnumerable<TomlTable> Tables(TomlTable raw, string key, string path)
    {
        if (!raw.TryGetValue(key, out var value))
            return [];
        return value switch
        {
            TomlTableArray tables => tables,
            TomlArray { Count: 0 } => [],
            _ => throw new PyValueError($"{path}: {key} must be a list of [[{key}]] tables."),
        };
    }

    /// <summary>Python's truthiness of a setting that's there (bool(raw.get(key))).</summary>
    private static bool Truthy(TomlTable table, string key) =>
        table.TryGetValue(key, out var value) && value switch
        {
            null => false,
            string s => s.Length > 0,
            bool b => b,
            long l => l != 0,
            double d => d != 0,
            TomlArray a => a.Count > 0,
            TomlTableArray a => a.Count > 0,
            TomlTable t => t.Count > 0,
            _ => true,
        };

    /// <summary>Python's str() of a setting, for the settings that are text.</summary>
    private static string Str(object? value, string key) => value switch
    {
        string s => s,
        long l => l.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "True" : "False",
        _ => throw new PyValueError($"{key} must be text."),
    };

    /// <summary>Python's int() of a setting: whole numbers, decimals truncated, or digits in text.</summary>
    private static int Int(object? value, string key) => value switch
    {
        long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
        double d when double.IsFinite(d) && Math.Abs(d) < int.MaxValue => (int)Math.Truncate(d),
        bool b => b ? 1 : 0,
        string s when int.TryParse(PyStr.Strip(s), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => throw new PyValueError($"{key} must be a whole number."),
    };

    private static List<string> Strings(object? value, string key) => value switch
    {
        TomlArray array => array.Select(item => Str(item, key)).ToList(),
        _ => throw new PyValueError($"{key} must be a list, as [\"python\", \"commit.py\"]."),
    };
}

[Tomlyn.Serialization.TomlSerializable(typeof(TomlTable))]
internal partial class TomlContext : Tomlyn.Serialization.TomlSerializerContext;
