"""A daily sync through Windows Task Scheduler (win-background).

The directory changes slowly, so there's no watcher: a task runs `sync --hook --scheduled` once a
day at daily_at, and at logon, and catches up a missed run when the laptop wakes. A run that finds
nothing changed writes nothing, so the extra logon runs cost one Graph query. It runs as you, only
while you're logged on, with no admin rights needed, and runs pythonw directly, so there's no window.
"""

from __future__ import annotations

import pathlib

from win_background import Task, python_command

TASK = "org-directory sync"
_task = Task(TASK, "Writes org charts from the Microsoft 365 directory (org-directory sync).")


def command(config_path: pathlib.Path) -> tuple[str, list]:
    return python_command("org_directory", "--config", str(config_path.resolve()), "sync", "--hook", "--scheduled")


def enable(config_path: pathlib.Path, daily_at: str) -> str:
    return _task.enable(*command(config_path), working_dir=config_path.resolve().parent,
                        daily_at=daily_at, keep_running=False)


def disable() -> bool:
    return _task.disable()


def status() -> dict | None:
    return _task.status()
