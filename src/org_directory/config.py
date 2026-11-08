"""The settings file: who signs in, and which org charts to write.

    login_hint = "you@example.com"          # optional
    tenant = "organizations"                # optional: a tenant id pins sign-in to one organisation
    msal_cache = "..."                      # optional: share another tool's sign-in
    state_file = "..."                      # optional: the last snapshot of the directory
    log_file = "..."                        # optional
    after_run = ["python", "commit.py"]     # optional: run after a sync that wrote files (JSON on stdin)
    daily_at = "09:00"                      # optional: when autostart's daily sync runs
    changes_file = "org/changes.md"         # optional: a running log of what changed, newest first

    [[chart]]
    name = "technology"
    root = "head.of.tech@example.com"       # the person at the top, by email or sign-in name
    output = "org/technology.md"            # relative to this file
    heading = "Technology"                  # optional: the file's title
    depth = 0                               # optional: levels below the top to include (0: all)
    changes_depth = 0                       # optional: report changes only this far down (0: all)

    [contacts]                              # optional: emergency contact sheets, with phone numbers
    folder = "~/OneDrive/Emergency contacts"   # NOT a repo: they hold personal numbers

    [[contacts.group]]
    name = "My team"
    root = "you@example.com"                # someone and everyone under them...
    depth = 1                               # ...this many levels down (0: all)
    people = ["peer@example.com"]           # ...and/or a list, by email

    [people_first]                          # optional: add the numbers People First lets you see
    enabled = true                          # the switch: false (or no section) uses the directory alone
    url = "https://yourorg.people-first.com"
    tenant_code = "yourorg"                 # optional: default, the first part of url
    account = "you@example.com"             # optional: the account to pick at sign-in (default login_hint)
    edge_profile = "..."                    # optional: its own Edge profile (default under LOCALAPPDATA)

Paths may use ~ and %VARIABLES%; relative paths are relative to the settings file.
"""

from __future__ import annotations

import hashlib
import os
import pathlib
import re
import tomllib
from dataclasses import dataclass, field

from . import contacts as contacts_mod, peoplefirst

APPDATA = pathlib.Path(os.environ.get("APPDATA", pathlib.Path.home()))
LOCAL = pathlib.Path(os.environ.get("LOCALAPPDATA", pathlib.Path.home())) / "org-directory"
DEFAULT_CONFIG = APPDATA / "org-directory" / "config.toml"

TEMPLATE = """\
# org-directory settings. See the README.
# login_hint = "you@example.com"
# changes_file = "org/changes.md"

# One [[chart]] per org chart: someone, and everyone under them.
# [[chart]]
# name = "my-department"
# root = "head.of.department@example.com"
# output = "org/my-department.md"
"""


@dataclass
class Chart:
    name: str
    root: str
    output: pathlib.Path
    heading: str = ""
    depth: int = 0
    changes_depth: int = 0


@dataclass
class Config:
    path: pathlib.Path
    charts: list[Chart] = field(default_factory=list)
    login_hint: str | None = None
    tenant: str = "organizations"
    msal_cache: pathlib.Path | None = None
    state_file: pathlib.Path = LOCAL / "state.json"
    log_file: pathlib.Path = LOCAL / "org-directory.log"
    after_run: list = field(default_factory=list)
    daily_at: str = "09:00"
    changes_file: pathlib.Path | None = None
    contacts: contacts_mod.Settings | None = None
    people_first: peoplefirst.Settings = field(default_factory=peoplefirst.Settings)


def _path(value: str, base: pathlib.Path) -> pathlib.Path:
    path = pathlib.Path(os.path.expandvars(os.path.expanduser(value)))
    return path if path.is_absolute() else (base / path).resolve()


def load(path: pathlib.Path | None = None) -> Config:
    path = pathlib.Path(path or os.environ.get("ORG_DIRECTORY_CONFIG") or DEFAULT_CONFIG).resolve()
    if not path.exists():
        raise FileNotFoundError(f"No settings file at {path}. Run `org-directory config` to create one.")
    raw = tomllib.loads(path.read_text(encoding="utf-8"))
    base = path.parent
    key = hashlib.sha256(str(path).lower().encode()).hexdigest()[:12]
    daily_at = str(raw.get("daily_at", "09:00"))
    if not re.fullmatch(r"([01]\d|2[0-3]):[0-5]\d", daily_at):
        raise ValueError(f"daily_at in {path} should be a time such as \"09:00\".")
    config = Config(path=path, login_hint=raw.get("login_hint"), tenant=raw.get("tenant", "organizations"),
                    msal_cache=_path(raw["msal_cache"], base) if raw.get("msal_cache") else None,
                    state_file=_path(raw["state_file"], base) if raw.get("state_file") else LOCAL / f"state-{key}.json",
                    log_file=_path(raw["log_file"], base) if raw.get("log_file") else LOCAL / "org-directory.log",
                    after_run=list(raw.get("after_run", [])), daily_at=daily_at,
                    changes_file=_path(raw["changes_file"], base) if raw.get("changes_file") else None)
    names = set()
    for i, item in enumerate(raw.get("chart", []), 1):
        name = item.get("name", f"chart{i}")
        missing = [k for k in ("root", "output") if not item.get(k)]
        if missing:
            raise ValueError(f"[[chart]] {name} in {path} has no {', '.join(missing)}.")
        if name in names:
            raise ValueError(f"Two charts in {path} are called {name}.")
        names.add(name)
        config.charts.append(Chart(name=name, root=item["root"], output=_path(item["output"], base),
                                   heading=item.get("heading", ""), depth=int(item.get("depth", 0)),
                                   changes_depth=int(item.get("changes_depth", 0))))
    config.contacts = _contacts(raw.get("contacts"), path, base)
    config.people_first = _people_first(raw.get("people_first"), config, path)
    return config


def _contacts(raw: dict | None, path: pathlib.Path, base: pathlib.Path) -> contacts_mod.Settings | None:
    if not raw:
        return None
    if not raw.get("folder"):
        raise ValueError(f"[contacts] in {path} has no folder.")
    groups = []
    for i, item in enumerate(raw.get("group", []), 1):
        name = item.get("name", f"Group {i}")
        if not item.get("root") and not item.get("people"):
            raise ValueError(f"[[contacts.group]] {name} in {path} needs a root, people, or both.")
        groups.append(contacts_mod.Group(name=name, root=item.get("root", ""), depth=int(item.get("depth", 0)),
                                         people=list(item.get("people", []))))
    if not groups:
        raise ValueError(f"[contacts] in {path} has no [[contacts.group]].")
    return contacts_mod.Settings(folder=_path(raw["folder"], base), groups=groups)


def _people_first(raw: dict | None, config: Config, path: pathlib.Path) -> peoplefirst.Settings:
    if not raw or not raw.get("enabled", False):
        return peoplefirst.Settings(enabled=False)
    url = str(raw.get("url", "")).rstrip("/")
    match = re.fullmatch(r"https://([a-z0-9-]+)\.people-first\.com", url, re.I)
    if not url.startswith("https://"):
        raise ValueError(f"[people_first] in {path} needs url, such as \"https://yourorg.people-first.com\".")
    tenant = raw.get("tenant_code") or (match.group(1).split("-")[0] if match else "")
    if not tenant:
        raise ValueError(f"[people_first] in {path} needs tenant_code: it couldn't be worked out from the url.")
    return peoplefirst.Settings(
        enabled=True, url=url, tenant_code=tenant, account=raw.get("account") or config.login_hint or "",
        edge_profile=_path(raw["edge_profile"], config.path.parent) if raw.get("edge_profile") else LOCAL / "edge-profile")


def create_default() -> pathlib.Path:
    if not DEFAULT_CONFIG.exists():
        DEFAULT_CONFIG.parent.mkdir(parents=True, exist_ok=True)
        DEFAULT_CONFIG.write_text(TEMPLATE, encoding="utf-8")
    return DEFAULT_CONFIG
