using System.ComponentModel;
using System.Reflection;
using System.Text.Json.Nodes;
using PyCompat;
using WinBackground;

namespace OrgDirectory;

/// <summary>Everything the command line touches outside itself, so tests can give it fakes.</summary>
public sealed class CliHost
{
    public TextWriter Out { get; init; } = Console.Out;
    public TextWriter Err { get; init; } = Console.Error;

    /// <summary>Where a sync's log lines are echoed (the console exe: stdout; the background exe: nowhere).</summary>
    public TextWriter? LogEcho { get; init; }

    public Func<string, string?> Env { get; init; } = Config.Environment;

    /// <summary>client(config, interactive): Graph for these settings.</summary>
    public Func<Config, bool?, IGraph> Graph { get; init; } = (config, interactive) => M365GraphClient.For(config, interactive);

    public PeopleFirstReader? ReadPeopleFirst { get; init; }
    public Action<PeopleFirstSettings, Action<string>>? PeopleFirstSignIn { get; init; }
    public Action<string, IEnumerable<string>> Notify { get; init; } = Toast.Notify;
    public Func<IReadOnlyList<string>, string, string, TimeSpan, Completed>? HookRunner { get; init; }
    public Func<string, ITask> Tasks { get; init; } = Autostart.Create;
    public string ProgramDir { get; init; } = AppContext.BaseDirectory;
    public Func<DateTime>? Now { get; init; }
}

/// <summary>org-directory: org charts and emergency contact sheets from the Microsoft 365 directory. The same commands,
/// options and exit codes as the Python version: 0; 1 when a chart wasn't written (and for anything unexpected), and
/// from `who` when nobody matches; 2 for a sign-in needed, no settings file, settings that can't be used, a lock
/// timeout, a People First problem, or a usage error.</summary>
public static class Cli
{
    public const string Prog = "org-directory";

    private const string Description = """
        org-directory: org charts written from the Microsoft 365 directory, kept up to date.

            org-directory config             create the settings file (if missing) and say where it is
            org-directory sign-in            sign in to Microsoft (once; opens a browser)
            org-directory sync               write the charts that changed, and log what changed
            org-directory sync --dry-run     say what it would write and what changed
            org-directory sync --hook        then run the settings' after_run command, if anything was written
            org-directory who "<name>"       someone's title, department, manager chain and reports (last sync)
            org-directory people-first sign-in   sign in to People First in a window (first time, or if it asks)
            org-directory people-first check     what People First lets you see: counts only, no numbers
            org-directory autostart on       sync daily at daily_at and at logon, and now (off, status)

        options:
          -h, --help            show this help message and exit
          --version             show program's version number and exit
          --config CONFIG       settings file (default %APPDATA%\org-directory\config.toml)
        """;

    private static readonly string[] Commands = ["config", "sign-in", "sync", "who", "people-first", "autostart"];

    private const string Usage = "usage: org-directory [-h] [--version] [--config CONFIG]\n"
        + "                     {config,sign-in,sync,who,people-first,autostart} ...";

    public static string Version =>
        typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    private sealed class UsageError(string message, string usage = Usage, string prog = Prog) : Exception(message)
    {
        public string UsageText { get; } = usage;
        public string ProgText { get; } = prog;
    }

    private sealed record Args(string? Config, string Command, bool DryRun, bool Hook, bool Json, bool Scheduled, string? Positional);

    public static int Run(string[] argv, CliHost host)
    {
        Args args;
        try
        {
            var parsed = Parse(argv, host);
            if (parsed is null)
                return 0;   // --help or --version
            args = parsed;
        }
        catch (UsageError error)
        {
            Print(host.Err, $"{error.UsageText}\n{error.ProgText}: error: {error.Message}");
            return 2;
        }

        try
        {
            if (args.Command == "config")
            {
                Print(host.Out, !string.IsNullOrEmpty(args.Config) ? args.Config : OrgDirectory.Config.CreateDefault(host.Env));
                return 0;
            }
            if (args.Command == "autostart")
                return AutostartCommand(args, host);
            var config = OrgDirectory.Config.Load(args.Config, host.Env);
            switch (args.Command)
            {
                case "sign-in":
                    host.Graph(config, true).Token();
                    Print(host.Out, "Signed in.");
                    return 0;
                case "who":
                    return Who(config, args.Positional!, host);
                case "people-first":
                    return PeopleFirstCommand(config, args.Positional!, host);
            }
            return SyncCommand(config, args, host);
        }
        catch (Exception error) when (error is M365Graph.SignInNeededException or FileNotFoundException or PyValueError or TimeoutException)
        {
            Print(host.Err, error.Message);
            return 2;
        }
        catch (Exception error)
        {
            Print(host.Err, $"{error.GetType().Name}: {error.Message}");
            return 1;
        }
    }

    private static int SyncCommand(Config config, Args args, CliHost host)
    {
        var log = Logger.Create(config.LogFile, !args.Json && !args.Scheduled ? host.LogEcho ?? host.Out : null, stampEcho: false);
        Report report;
        try
        {
            report = Sync.Run(config, host.Graph(config, args.Scheduled ? false : null), args.DryRun, args.Hook, log,
                host.ReadPeopleFirst, host.HookRunner, now: host.Now);
        }
        catch (Exception error)
        {
            log($"sync failed: {Py.Head(error.Message, 300)}");
            if (args.Scheduled)
                host.Notify(error is M365Graph.SignInNeededException ? "org-directory needs a sign-in" : "org-directory sync failed",
                    [Py.Head(error.Message, 300)]);
            throw;
        }
        log(report.Summary());
        if (args.Scheduled && (report.Failed.Count > 0 || report.Untracked.Count > 0))
            host.Notify("org-directory: a chart wasn't written", [.. report.Failed, .. report.Untracked]);
        if (args.Scheduled && report.PeopleFirstStale == true)
            host.Notify("org-directory: People First isn't being read",
                [Py.Head(report.PeopleFirstFailed ?? "", 200), "The contact sheets' personal numbers are getting old."]);
        if (args.Json)
            Print(host.Out, report.ToJson());
        return report.Failed.Count > 0 ? 1 : 0;
    }

    private static int Who(Config config, string name, CliHost host)
    {
        var state = Sync.LoadState(config);
        if (!PyCompat.Py.Truthy(state["people"]))
        {
            Print(host.Err, "No sync yet. Run `org-directory sync` first.");
            return 2;
        }
        var org = Org.FromSnapshot(state["people"] as JsonObject);
        var synced = state.ContainsKey("synced") ? PyCompat.Py.Str(state["synced"]) : "?";
        var found = org.Search(name);
        if (found.Count == 0)
        {
            Print(host.Out, $"Nobody matching {PyCompat.Py.ReprString(name)} in the sync of {synced}.");
            return 1;
        }
        foreach (var person in found.Take(10))
        {
            Print(host.Out, $"{person.Name}: {Or(person.Title, "(no title)")}, {Or(person.Department, "(no department)")}");
            var chain = org.Chain(person.Id);
            if (chain.Count > 0)
                Print(host.Out, "  reports to: " + string.Join(" > ", chain.Select(p => p.Title.Length > 0 ? $"{p.Name} ({p.Title})" : p.Name)));
            var reports = org.Reports.GetValueOrDefault(person.Id, []);
            if (reports.Count > 0)
                Print(host.Out, $"  {reports.Count} report(s): " + string.Join(", ", reports.Select(r => org.People[r].Name)));
        }
        if (found.Count > 10)
            Print(host.Out, $"...and {found.Count - 10} more. Be more specific.");
        Print(host.Out, $"(as of the sync of {synced})");
        return 0;
    }

    private static string Or(string text, string otherwise) => text.Length > 0 ? text : otherwise;

    private static int PeopleFirstCommand(Config config, string action, CliHost host)
    {
        var settings = config.PeopleFirst;
        if (!settings.Enabled)
        {
            Print(host.Err, "People First is switched off: set enabled = true under [people_first] in the settings.");
            return 2;
        }
        void Log(string text) => Print(host.Out, text);
        try
        {
            if (action == "sign-in")
            {
                Print(host.Out, "A window opens: sign in to People First there. It closes by itself once you're in.");
                (host.PeopleFirstSignIn ?? ((s, log) => OrgDirectory.PeopleFirst.SignInWindow(s, log)))(settings, Log);
                Print(host.Out, "Signed in.");
                return 0;
            }
            var found = (host.ReadPeopleFirst ?? OrgDirectory.PeopleFirst.Fetch)(settings, Log);
            var fields = new SortedDictionary<string, int>(Py.CodePoints);
            foreach (var (_, numbers) in found)
                if (numbers is JsonObject entry)
                    foreach (var (key, _) in entry)
                        fields[key] = fields.GetValueOrDefault(key) + 1;
            Print(host.Out, $"People First gives numbers for {found.Count} people: "
                            + (fields.Count > 0 ? string.Join(", ", fields.Select(f => $"{f.Key} {f.Value}")) : "none"));
            return 0;
        }
        catch (PeopleFirstClient.PeopleFirstException error)
        {
            Print(host.Err, error.Message);
            return 2;
        }
    }

    private static int AutostartCommand(Args args, CliHost host)
    {
        var task = host.Tasks(Autostart.TaskName);
        try
        {
            if (args.Positional == "on")
            {
                var config = OrgDirectory.Config.Load(args.Config, host.Env);   // check the settings load before scheduling them
                var (exe, taskArgs) = Autostart.Command(host.ProgramDir, config.Path);
                var command = task.Enable(exe, taskArgs, Path.GetDirectoryName(config.Path), Autostart.Schedule(config.DailyAt));
                Print(host.Out, $"Autostart is on: '{Autostart.TaskName}' runs daily at {config.DailyAt} and at logon, and has "
                                + $"started now.\n  {command}\n  log: {config.LogFile}");
            }
            else if (args.Positional == "off")
                Print(host.Out, task.Disable() ? "Autostart is off." : "Autostart was already off.");
            else
            {
                var info = task.Status();
                if (info is null)
                    Print(host.Out, "Autostart is off.");
                else
                {
                    Print(host.Out, $"Autostart is on ('{Autostart.TaskName}').");
                    foreach (var key in new[] { "state", "last_run", "last_result", "next_run", "command" })
                        Print(host.Out, $"  {key.Replace('_', ' '),-12} {info.GetValueOrDefault(key, "")}");
                }
            }
            return 0;
        }
        catch (Exception error) when (error is InvalidOperationException or FileNotFoundException or PyValueError or Win32Exception or ArgumentException)
        {
            Print(host.Err, error.Message);
            return 2;
        }
    }

    /// <summary>print(): a line, with "\n" written as "\r\n" as Python's stdout does on Windows.</summary>
    private static void Print(TextWriter writer, string text)
    {
        try
        {
            writer.Write(PyStr.WindowsNewlines(text + "\n"));
            writer.Flush();
        }
        catch (IOException)
        {
            // a closed console mustn't stop the tool
        }
    }

    // ---- argparse, as far as these commands use it ------------------------------------------------------------

    private static string SubUsage(string command) => command switch
    {
        "sync" => "usage: org-directory sync [-h] [--dry-run] [--hook] [--json] [--scheduled]",
        "who" => "usage: org-directory who [-h] name",
        "people-first" => "usage: org-directory people-first [-h] {sign-in,check}",
        "autostart" => "usage: org-directory autostart [-h] {on,off,status}",
        _ => $"usage: org-directory {command} [-h]",
    };

    private static string Quoted(IEnumerable<string> choices) => string.Join(", ", choices.Select(c => $"'{c}'"));

    private static Args? Parse(string[] argv, CliHost host)
    {
        string? config = null;
        var i = 0;
        string[] mainOptions = ["--help", "--version", "--config"];
        while (i < argv.Length && IsOption(argv[i]))
        {
            var (name, value) = Option(argv[i], mainOptions);
            switch (name)
            {
                case "--help":
                    Print(host.Out, $"{Usage}\n\n{Description}");
                    return null;
                case "--version":
                    Print(host.Out, Version);
                    return null;
                case "--config":
                    config = value ?? (i + 1 < argv.Length && !IsOption(argv[i + 1]) ? argv[++i] : throw new UsageError("argument --config: expected one argument"));
                    break;
            }
            i++;
        }
        if (i >= argv.Length)
            throw new UsageError("the following arguments are required: command");
        var command = argv[i++];
        if (!Commands.Contains(command))
            throw new UsageError($"argument command: invalid choice: '{command}' (choose from {Quoted(Commands)})");

        string[] options = command == "sync" ? ["--help", "--dry-run", "--hook", "--json", "--scheduled"] : ["--help"];
        bool dryRun = false, hook = false, json = false, scheduled = false;
        string? positional = null;
        var unrecognised = new List<string>();
        var onlyPositional = false;
        for (; i < argv.Length; i++)
        {
            var arg = argv[i];
            if (!onlyPositional && arg == "--")
            {
                onlyPositional = true;
                continue;
            }
            if (!onlyPositional && IsOption(arg))
            {
                string name;
                string? value;
                try
                {
                    (name, value) = Option(arg, options);
                }
                catch (UsageError)
                {
                    unrecognised.Add(arg);
                    continue;
                }
                switch (name)
                {
                    case "--help":
                        Print(host.Out, SubUsage(command));
                        return null;
                    case "--dry-run": dryRun = Flag(value, name, command); break;
                    case "--hook": hook = Flag(value, name, command); break;
                    case "--json": json = Flag(value, name, command); break;
                    case "--scheduled": scheduled = Flag(value, name, command); break;
                }
                continue;
            }
            if (command is "who" or "people-first" or "autostart" && positional is null)
                positional = arg;
            else
                unrecognised.Add(arg);
        }
        var sub = $"{Prog} {command}";
        if (command == "who" && positional is null)
            throw new UsageError("the following arguments are required: name", SubUsage(command), sub);
        if (command is "people-first" or "autostart")
        {
            string[] choices = command == "people-first" ? ["sign-in", "check"] : ["on", "off", "status"];
            if (positional is null)
                throw new UsageError("the following arguments are required: action", SubUsage(command), sub);
            if (!choices.Contains(positional))
                throw new UsageError($"argument action: invalid choice: '{positional}' (choose from {Quoted(choices)})", SubUsage(command), sub);
        }
        if (unrecognised.Count > 0)
            throw new UsageError($"unrecognized arguments: {string.Join(' ', unrecognised)}");
        return new Args(config, command, dryRun, hook, json, scheduled, positional);
    }

    // argparse treats "-5" as a value, not an option, when no option looks like a number
    private static bool IsOption(string arg) => arg.Length > 1 && arg[0] == '-' && !char.IsAsciiDigit(arg[1]);

    private static bool Flag(string? value, string name, string command) =>
        value is null ? true : throw new UsageError($"argument {name}: ignored explicit argument '{value}'", SubUsage(command), $"{Prog} {command}");

    /// <summary>An option and its "=value", with argparse's unambiguous prefixes ("--dry" for "--dry-run").</summary>
    private static (string Name, string? Value) Option(string arg, string[] known)
    {
        if (arg == "-h")
            return ("--help", null);
        var eq = arg.IndexOf('=');
        var name = eq >= 0 ? arg[..eq] : arg;
        var value = eq >= 0 ? arg[(eq + 1)..] : null;
        if (known.Contains(name))
            return (name, value);
        if (name.StartsWith("--", StringComparison.Ordinal))
        {
            var matches = known.Where(k => k.StartsWith(name, StringComparison.Ordinal)).ToList();
            if (matches.Count == 1)
                return (matches[0], value);
            if (matches.Count > 1)
                throw new UsageError($"ambiguous option: {name} could match {string.Join(", ", matches)}");
        }
        throw new UsageError($"unrecognized arguments: {arg}");
    }
}
