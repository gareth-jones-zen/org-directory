"""The Python version as an oracle: runs org-directory's own sync on given inputs, with the clock frozen and the
hook and People First faked, and records everything it produced.

Used by scenarios.py (synthetic, committed goldens) and live.py (the real directory, local only). It imports the
Python package from this repository's src/ (the same code as the v0.2.2 tag), never the installed tool.
"""

from __future__ import annotations

import base64
import contextlib
import copy
import hashlib
import datetime as real_dt
import io
import json
import pathlib
import subprocess
import sys
import types

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src"))

from org_directory import cli, config as cfg, run as run_mod  # noqa: E402

PLACEHOLDER = "{DIR}"


class FakeGraph:
    def __init__(self, users):
        self.users = users

    def pages(self, url, scopes=None):
        yield from copy.deepcopy(self.users)


def frozen(now: real_dt.datetime, today: real_dt.date):
    """A stand-in for run.py's `dt` module whose now() and today() are fixed."""
    class FrozenDatetime(real_dt.datetime):
        @classmethod
        def now(cls, tz=None):
            return cls(now.year, now.month, now.day, now.hour, now.minute, now.second, now.microsecond)

    class FrozenDate(real_dt.date):
        @classmethod
        def today(cls):
            return cls(today.year, today.month, today.day)

    return types.SimpleNamespace(datetime=FrozenDatetime, date=FrozenDate, timedelta=real_dt.timedelta)


def files(folder: pathlib.Path) -> dict[str, str]:
    """Every file under folder (bar the settings), by relative path with "/", as the SHA-256 of its bytes."""
    return {p.relative_to(folder).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest()
            for p in sorted(folder.rglob("*")) if p.is_file() and p.name != "config.toml"}


def placeholder(text: str, folder: pathlib.Path) -> str:
    raw = str(folder)
    return text.replace(json.dumps(raw)[1:-1], PLACEHOLDER).replace(raw, PLACEHOLDER)


def apply(ops: list[dict], folder: pathlib.Path) -> None:
    for op in ops:
        path = folder / op["path"]
        if op.get("delete"):
            if path.exists():
                path.unlink()
        else:
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(base64.b64decode(op["bytes"]))


def step(config_path: pathlib.Path, s: dict) -> dict:
    """One sync, as run() does it (lock, sync, hook), with everything it produced."""
    folder = config_path.parent
    apply(s.get("ops", []), folder)
    now = real_dt.datetime.fromisoformat(s["now"])
    today = real_dt.date.fromisoformat(s["today"])
    logs, hook_inputs = [], []

    def fake_run(args, input=None, **kwargs):
        hook_inputs.append(input)
        return subprocess.CompletedProcess(args, s.get("hook_exit", 0), s.get("hook_stdout", ""), s.get("hook_stderr", ""))

    pf = s.get("people_first")

    def read(settings, log=print):
        if pf is None:
            raise AssertionError("People First read when the scenario says it isn't")
        if "error" in pf:
            raise RuntimeError(pf["error"])
        log("people first: (fake read)")
        return copy.deepcopy(pf["numbers"])

    saved_dt, saved_run = run_mod.dt, run_mod.subprocess.run
    run_mod.dt, run_mod.subprocess.run = frozen(now, today), fake_run
    try:
        config = cfg.load(config_path)
        outcome = {}
        try:
            report = run_mod.run(config, dry_run=s.get("dry_run", False), hook=True, log=logs.append,
                                 graph=FakeGraph(s["users"]), read_people_first=read)
            outcome["report"] = placeholder(json.dumps(report, indent=1), folder)
            outcome["summary"] = placeholder(run_mod.summary(report), folder)
        except Exception as error:   # noqa: BLE001: recorded, compared by type of failure only
            outcome["error"] = type(error).__name__
    finally:
        run_mod.dt, run_mod.subprocess.run = saved_dt, saved_run
    outcome["log"] = [placeholder(line, folder) for line in logs]
    outcome["hook"] = [placeholder(text, folder) for text in hook_inputs]
    outcome["files"] = files(folder)
    return outcome


def who(config_path: pathlib.Path, query: str) -> dict:
    out, err = io.StringIO(), io.StringIO()
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
        code = cli.main(["--config", str(config_path), "who", query])
    folder = config_path.parent
    return {"query": query, "code": code, "out": placeholder(out.getvalue(), folder), "err": placeholder(err.getvalue(), folder)}
