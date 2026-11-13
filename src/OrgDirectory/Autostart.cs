using PyCompat;
using WinBackground;

namespace OrgDirectory;

/// <summary>A daily sync through Windows Task Scheduler (WinBackground).
///
/// The directory changes slowly, so there's no watcher: a task runs `sync --hook --scheduled` once a day at daily_at,
/// and at logon, and catches up a missed run when the laptop wakes. A run that finds nothing changed writes nothing, so
/// the extra logon runs cost one Graph query. It runs as you, only while you're logged on, with no admin rights needed,
/// and runs org-directory-background.exe directly, so there's no window. The same task name as the Python version, so
/// switching replaces the Python task rather than adding a second.</summary>
public static class Autostart
{
    public const string TaskName = "org-directory sync";
    public const string ConsoleExe = "org-directory.exe";
    public const string BackgroundExe = "org-directory-background.exe";

    public static ITask Create(string name) =>
        new ScheduledTask(name, "Writes org charts from the Microsoft 365 directory (org-directory sync).");

    /// <summary>The program the task runs (the windowless one beside the running program, if it's there) and its
    /// arguments.</summary>
    public static (string Exe, List<string> Args) Command(string programDir, string configPath)
    {
        var background = Path.Combine(programDir, BackgroundExe);
        var exe = File.Exists(background) ? background : Path.Combine(programDir, ConsoleExe);
        return (exe, ["--config", PyPath.Resolve(configPath), "sync", "--hook", "--scheduled"]);
    }

    /// <summary>When it runs: daily at daily_at and at logon, as a job (stopped after 30 minutes), started now.</summary>
    public static TaskSchedule Schedule(string dailyAt) => new() { AtLogon = true, DailyAt = dailyAt, KeepRunning = false };
}
