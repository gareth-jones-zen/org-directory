"""A daily sync through Windows Task Scheduler.

The directory changes slowly, so there's no watcher: a task runs `sync --hook --scheduled` once a
day at daily_at, and at logon, and catches up a missed run when the laptop wakes. A run that finds
nothing changed writes nothing, so the extra logon runs cost one Graph query. It runs as you, only
while you're logged on, with no admin rights needed, and runs pythonw directly, so there's no window.
"""

from __future__ import annotations

import pathlib
import subprocess
import sys

from .run import NO_WINDOW

TASK = "org-directory sync"


def command(config_path: pathlib.Path) -> tuple[str, list]:
    python = pathlib.Path(sys.executable)
    windowless = python.with_name("pythonw.exe")
    return str(windowless if windowless.exists() else python), [
        "-m", "org_directory", "--config", str(config_path.resolve()), "sync", "--hook", "--scheduled"]


def _ps(script: str) -> subprocess.CompletedProcess:
    return subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-Command", script],
                          capture_output=True, text=True, timeout=120, creationflags=NO_WINDOW)


def _q(text: str) -> str:
    return "'" + text.replace("'", "''") + "'"


def enable(config_path: pathlib.Path, daily_at: str) -> str:
    if sys.platform != "win32":
        raise RuntimeError("autostart needs Windows Task Scheduler.")
    exe, args = command(config_path)
    done = _ps(f"""
$ErrorActionPreference = 'Stop'
$me = "$env:USERDOMAIN\\$env:USERNAME"
$action = New-ScheduledTaskAction -Execute {_q(exe)} -Argument {_q(subprocess.list2cmdline(args))} -WorkingDirectory {_q(str(config_path.resolve().parent))}
$triggers = @((New-ScheduledTaskTrigger -Daily -At {_q(daily_at)}), (New-ScheduledTaskTrigger -AtLogOn -User $me))
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 30) -MultipleInstances IgnoreNew
$principal = New-ScheduledTaskPrincipal -UserId $me -LogonType Interactive -RunLevel Limited
Register-ScheduledTask -TaskName {_q(TASK)} -Action $action -Trigger $triggers -Settings $settings -Principal $principal `
    -Description 'Writes org charts from the Microsoft 365 directory (org-directory sync).' -Force | Out-Null
Start-ScheduledTask -TaskName {_q(TASK)}
""")
    if done.returncode:
        raise RuntimeError(f"Couldn't create the task: {(done.stderr or done.stdout).strip()[:400]}")
    return f"{exe} {subprocess.list2cmdline(args)}"


def disable() -> bool:
    done = _ps(f"""
$task = Get-ScheduledTask -TaskName {_q(TASK)} -ErrorAction SilentlyContinue
if ($task) {{ Unregister-ScheduledTask -TaskName {_q(TASK)} -Confirm:$false; 'removed' }}
exit 0
""")
    if done.returncode:
        raise RuntimeError(f"Couldn't remove the task: {(done.stderr or done.stdout).strip()[:400]}")
    return "removed" in done.stdout


def status() -> dict | None:
    done = _ps(f"""
$task = Get-ScheduledTask -TaskName {_q(TASK)} -ErrorAction SilentlyContinue
if ($task) {{
  $info = Get-ScheduledTaskInfo -TaskName {_q(TASK)}
  "state=$($task.State)"
  "command=$($task.Actions[0].Execute) $($task.Actions[0].Arguments)"
  "last_run=$($info.LastRunTime)"
  "last_result=$($info.LastTaskResult)"
  "next_run=$($info.NextRunTime)"
}}
exit 0
""")
    if not done.stdout.strip():
        return None
    info = dict(line.split("=", 1) for line in done.stdout.strip().splitlines() if "=" in line)
    if info.get("last_result") == "267011":   # SCHED_S_TASK_HAS_NOT_RUN
        info["last_run"], info["last_result"] = "never", "-"
    elif info.get("last_result") == "267009":   # SCHED_S_TASK_RUNNING
        info["last_result"] = "running"
    return info
