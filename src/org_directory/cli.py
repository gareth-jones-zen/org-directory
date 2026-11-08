"""org-directory: org charts written from the Microsoft 365 directory, kept up to date.

    org-directory config             create the settings file (if missing) and say where it is
    org-directory sign-in            sign in to Microsoft (once; opens a browser)
    org-directory sync               write the charts that changed, and log what changed
    org-directory sync --dry-run     say what it would write and what changed
    org-directory sync --hook        then run the settings' after_run command, if anything was written
    org-directory who "<name>"       someone's title, department, manager chain and reports (last sync)
    org-directory autostart on       sync daily at daily_at and at logon, and now (off, status)
"""

from __future__ import annotations

import argparse
import json
import sys

from m365_graph import SignInNeeded

from . import __version__, config as cfg
from .directory import Directory
from .run import client, load_state, logger, notify, run, summary


def main(argv: list | None = None) -> int:
    # Names and the arrows in `who` aren't all in a Windows console's code page.
    for stream in (sys.stdout, sys.stderr):
        if stream and hasattr(stream, "reconfigure"):
            stream.reconfigure(errors="replace")
    parser = argparse.ArgumentParser(prog="org-directory", description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--version", action="version", version=__version__)
    parser.add_argument("--config", help="settings file (default %%APPDATA%%\\org-directory\\config.toml)")
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("config", help="create the settings file if missing, and say where it is")
    sub.add_parser("sign-in", help="sign in to Microsoft (opens a browser)")
    p = sub.add_parser("sync", help="write the charts that changed")
    p.add_argument("--dry-run", action="store_true")
    p.add_argument("--hook", action="store_true", help="run the after_run command if anything was written")
    p.add_argument("--json", action="store_true", help="print the report as JSON")
    p.add_argument("--scheduled", action="store_true",
                   help="as the daily task runs it: never open a browser, and notify a problem")
    who = sub.add_parser("who", help="look someone up in the last sync")
    who.add_argument("name", help="all or part of a name, or an email address")
    auto = sub.add_parser("autostart", help="sync daily and at logon (Task Scheduler)")
    auto.add_argument("action", choices=["on", "off", "status"])
    args = parser.parse_args(argv)

    if args.command == "config":
        print(args.config or cfg.create_default())
        return 0
    if args.command == "autostart":
        return _autostart(args)
    try:
        config = cfg.load(args.config)
        if args.command == "sign-in":
            client(config, interactive=True).token()
            print("Signed in.")
            return 0
        if args.command == "who":
            return _who(config, args.name)
        log = logger(config, echo=not args.json and not args.scheduled)
        try:
            report = run(config, dry_run=args.dry_run, hook=args.hook,
                         interactive=False if args.scheduled else None, log=log)
        except Exception as error:
            log(f"sync failed: {str(error)[:300]}")
            if args.scheduled:
                title = "org-directory needs a sign-in" if isinstance(error, SignInNeeded) else "org-directory sync failed"
                notify(title, [str(error)[:300]])
            raise
        log(summary(report))
        if args.scheduled and (report["failed"] or report["untracked"]):
            notify("org-directory: a chart wasn't written", report["failed"] + report["untracked"])
        if args.json:
            print(json.dumps(report, indent=1))
        return 1 if report["failed"] else 0
    except (SignInNeeded, FileNotFoundError, ValueError, TimeoutError) as error:
        print(error, file=sys.stderr)
        return 2


def _who(config: cfg.Config, name: str) -> int:
    state = load_state(config)
    if not state.get("people"):
        print("No sync yet. Run `org-directory sync` first.", file=sys.stderr)
        return 2
    directory = Directory.from_snapshot(state["people"])
    found = directory.search(name)
    if not found:
        print(f"Nobody matching {name!r} in the sync of {state.get('synced', '?')}.")
        return 1
    for person in found[:10]:
        print(f"{person.name}: {person.title or '(no title)'}, {person.department or '(no department)'}")
        chain = directory.chain(person.id)
        if chain:
            print("  reports to: " + " > ".join(f"{p.name} ({p.title})" if p.title else p.name for p in chain))
        reports = directory.reports.get(person.id, [])
        if reports:
            print(f"  {len(reports)} report(s): " + ", ".join(directory.people[r].name for r in reports))
    if len(found) > 10:
        print(f"...and {len(found) - 10} more. Be more specific.")
    print(f"(as of the sync of {state.get('synced', '?')})")
    return 0


def _autostart(args) -> int:
    from . import autostart
    try:
        if args.action == "on":
            config = cfg.load(args.config)   # check the settings load before scheduling them
            command = autostart.enable(config.path, config.daily_at)
            print(f"Autostart is on: '{autostart.TASK}' runs daily at {config.daily_at} and at logon, and has "
                  f"started now.\n  {command}\n  log: {config.log_file}")
        elif args.action == "off":
            print("Autostart is off." if autostart.disable() else "Autostart was already off.")
        else:
            info = autostart.status()
            if info is None:
                print("Autostart is off.")
            else:
                print(f"Autostart is on ('{autostart.TASK}').")
                for key in ("state", "last_run", "last_result", "next_run", "command"):
                    print(f"  {key.replace('_', ' '):12} {info.get(key, '')}")
        return 0
    except (RuntimeError, FileNotFoundError, ValueError) as error:
        print(error, file=sys.stderr)
        return 2
