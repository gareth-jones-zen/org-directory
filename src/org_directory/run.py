"""One locked sync: fetch the directory, write the charts that changed, log the changes, run the hook.

The hook is how a repo commits what was written: the tool knows nothing about git. It gets a JSON
report on stdin, the same shape doc-filer and teams-channel-notes give theirs, plus the changes:

    {"config": "...", "written": ["C:\\\\...\\\\technology.md"], "failed": [], "unmatched": [],
     "summary": "1 written", "changes": [{"chart": "technology", "text": "New title: ..."}]}

It runs from the settings file's folder, with no window.

**A chart is only rewritten when the directory changed**, so a daily sync of an unchanged directory
writes nothing and commits nothing. **A chart that shrinks sharply isn't written** (a quarter of it,
and at least ten people): a partial answer from Graph would otherwise read as a wave of leavers.
"""

from __future__ import annotations

import datetime as dt
import json
import subprocess
import sys

from m365_graph import Client

from . import locks
from .config import Config
from .directory import MARKER, SCOPES, Directory, changes, fetch, log_changes, render

NO_WINDOW = 0x08000000 if sys.platform == "win32" else 0
SHRINK = 0.25


def client(config: Config, interactive: bool | None = None) -> Client:
    kwargs = {"cache_path": config.msal_cache} if config.msal_cache else {}
    return Client(tenant=config.tenant, login_hint=config.login_hint, scopes=list(SCOPES), interactive=interactive,
                  sign_in_hint="At a console, run: org-directory sign-in", **kwargs)


def logger(config: Config, echo: bool = True):
    def log(text: str) -> None:
        line = f"{dt.datetime.now():%Y-%m-%d %H:%M:%S}  {text}"
        if echo:
            try:
                print(text, flush=True)
            except Exception:
                pass   # pythonw has no console
        try:
            config.log_file.parent.mkdir(parents=True, exist_ok=True)
            with open(config.log_file, "a", encoding="utf-8") as handle:
                handle.write(line + "\n")
        except OSError:
            pass
    return log


def notify(title: str, lines: list) -> None:
    """A Windows notification, through PowerShell. Best effort."""
    if sys.platform != "win32":
        return
    text = "\n".join(lines)[:600].replace("'", "''")
    script = f"""
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
$t = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02)
$t.GetElementsByTagName('text').Item(0).AppendChild($t.CreateTextNode('{title.replace("'", "''")}')) | Out-Null
$t.GetElementsByTagName('text').Item(1).AppendChild($t.CreateTextNode('{text}')) | Out-Null
$app = '{{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}}\\WindowsPowerShell\\v1.0\\powershell.exe'
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($app).Show([Windows.UI.Notifications.ToastNotification]::new($t))
"""
    try:
        subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-Command", script],
                       capture_output=True, timeout=30, creationflags=NO_WINDOW)
    except Exception:
        pass


def summary(report: dict) -> str:
    text = (f"{len(report['written'])} written, {len(report['changes'])} change(s), "
            f"{report['unchanged']} unchanged, {report['people']} people in the directory")
    if report["failed"]:
        text += f", {len(report['failed'])} failed"
    return text


def load_state(config: Config) -> dict:
    try:
        return json.loads(config.state_file.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return {}


def sync(config: Config, graph=None, dry_run: bool = False, today: dt.date | None = None, log=print) -> dict:
    today = today or dt.date.today()
    state = load_state(config)
    after = fetch(graph or client(config))
    before = Directory.from_snapshot(state.get("people", {}))
    report = {"written": [], "failed": [], "untracked": [], "changes": [], "unchanged": 0, "people": len(after.people)}
    charts = dict(state.get("charts", {}))
    for chart in config.charts:
        root = after.find(chart.root)
        if root is None:
            report["failed"].append(f"{chart.name}: no enabled account for {chart.root}")
            log(f"{chart.name}: can't find {chart.root} in the directory; chart not written")
            continue
        members = after.under(root.id, chart.depth)
        old = charts.get(chart.name)
        old_members = {k: int(v) for k, v in old["members"].items()} if old else {}
        if old and len(members) < len(old_members) * (1 - SHRINK) and len(old_members) - len(members) >= 10:
            report["failed"].append(f"{chart.name}: shrank from {len(old_members)} to {len(members)} people")
            log(f"{chart.name}: shrank from {len(old_members)} to {len(members)} people, so it wasn't written. "
                "If that's real, delete the chart's entry from the state file and sync again.")
            continue
        found = changes(before, after, old_members, members, chart.changes_depth) if old else []
        moved = not old or old_members != members or any(
            before.people.get(pid) is None or before.people[pid].record() != after.people[pid].record()
            for pid in members)
        changed = today.isoformat() if moved else old["changed"]
        text = render(after, root.id, chart.heading or root.name, chart.depth, changed)
        existing = chart.output.read_text(encoding="utf-8") if chart.output.exists() else None
        if existing is not None and MARKER not in existing[:2000]:
            report["untracked"].append(str(chart.output))
            log(f"{chart.name}: {chart.output} wasn't written by org-directory, so it's left alone")
            continue
        if existing == text:
            report["unchanged"] += 1
        else:
            report["written"].append(str(chart.output))
            log(f"{chart.name}: {'would write' if dry_run else 'wrote'} {chart.output} ({len(members)} people)")
            if not dry_run:
                chart.output.parent.mkdir(parents=True, exist_ok=True)
                chart.output.write_text(text, encoding="utf-8")
        for item in found:
            log(f"{chart.name}: {item}")
        report["changes"] += [{"chart": chart.name, "text": item} for item in found]
        if found and config.changes_file and not dry_run:
            log_changes(config.changes_file, chart.heading or chart.name, found, today)
        charts[chart.name] = {"root": root.id, "members": members, "changed": changed}
    if report["changes"] and config.changes_file:
        report["written"].append(str(config.changes_file))
    if not dry_run:
        config.state_file.parent.mkdir(parents=True, exist_ok=True)
        config.state_file.write_text(json.dumps({"synced": dt.datetime.now().isoformat(timespec="seconds"),
                                                 "people": after.snapshot(), "charts": charts}), encoding="utf-8")
    return report


def after_run(config: Config, report: dict, log=print) -> None:
    # "failed" is doc-filer's list of files it couldn't convert; a chart that wasn't written is reported
    # by the sync itself (logged, and notified when scheduled), so it isn't passed on as one.
    payload = json.dumps({"config": str(config.path), "written": report["written"], "failed": [],
                          "unmatched": [], "summary": summary(report), "changes": report["changes"]})
    log(f"after-run hook: {' '.join(config.after_run)}")
    try:
        done = subprocess.run(config.after_run, input=payload, cwd=config.path.parent, capture_output=True,
                              text=True, encoding="utf-8", errors="replace", timeout=1800, creationflags=NO_WINDOW)
        for line in (done.stdout + done.stderr).splitlines():
            if line.strip():
                log(f"  hook: {line.strip()}")
        if done.returncode:
            log(f"after-run hook exited {done.returncode}")
    except Exception as error:
        log(f"after-run hook failed: {error}")


def run(config: Config, dry_run: bool = False, hook: bool = False, interactive: bool | None = None,
        log=print, graph=None) -> dict:
    with locks.run(config):
        report = sync(config, graph=graph or client(config, interactive), dry_run=dry_run, log=log)
    if hook and not dry_run and report["written"] and config.after_run:
        after_run(config, report, log)
    return report
