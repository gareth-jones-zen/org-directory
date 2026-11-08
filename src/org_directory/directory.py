"""The Microsoft 365 directory as org charts: fetch everyone once, cut charts from it, and say what changed.

What it's built on, from reading Zen's directory (Nov 2026):
- **One query gets everyone, with their manager** (`/users?$expand=manager`): about 1,600 accounts in
  seconds. Walking directReports person by person took a call each.
- **Only enabled member accounts count.** Guests (a quarter of the accounts) and disabled accounts are
  left out; a leaver's disabled account can keep its manager for weeks, and would otherwise still show.
- **Job title and department are well filled in; mobile numbers aren't** (2 in 171). Phones aren't
  written anywhere: a chart is for who's who, not for contacting people.
- **A chart is someone and everyone under them**, to a depth if set.
- **Changes are found by comparing with the last snapshot**: joiners, leavers, new titles, departments,
  managers and names. Someone appearing in or leaving a chart is told apart from someone joining or
  leaving the organisation, because the last snapshot of everyone is kept too.
"""

from __future__ import annotations

import datetime as dt
import pathlib
from dataclasses import dataclass, field

SCOPES = ("User.Read.All",)
FIELDS = "id,displayName,jobTitle,department,userPrincipalName,mail,userType"
URL = (f"/users?$filter=accountEnabled%20eq%20true&$select={FIELDS}"
       "&$expand=manager($select=id)&$top=999")
MARKER = "Written by org-directory"


@dataclass
class Person:
    id: str
    name: str
    title: str = ""
    department: str = ""
    manager: str | None = None
    login: str = ""          # userPrincipalName, lower case
    mail: str = ""           # lower case

    def record(self) -> dict:
        return {"name": self.name, "title": self.title, "department": self.department, "manager": self.manager}


@dataclass
class Directory:
    people: dict[str, Person] = field(default_factory=dict)

    def __post_init__(self):
        self.reports: dict[str, list[str]] = {}
        for person in self.people.values():
            if person.manager in self.people:
                self.reports.setdefault(person.manager, []).append(person.id)
        for ids in self.reports.values():
            ids.sort(key=lambda i: self.people[i].name.lower())

    def find(self, who: str) -> Person | None:
        who = who.strip().lower()
        for person in self.people.values():
            if who in (person.login, person.mail):
                return person
        named = [p for p in self.people.values() if p.name.lower() == who]
        return named[0] if len(named) == 1 else None

    def search(self, text: str) -> list[Person]:
        text = text.strip().lower()
        return sorted((p for p in self.people.values() if text in p.name.lower() or text in (p.login, p.mail)),
                      key=lambda p: p.name.lower())

    def under(self, root: str, depth: int = 0) -> dict[str, int]:
        """Everyone in root's chart, with their level (root is 0). depth 0 means no limit."""
        found, queue = {}, [(root, 0)]
        while queue:
            pid, level = queue.pop(0)
            if pid in found:
                continue   # a loop in the manager chain shouldn't hang a sync
            found[pid] = level
            if not depth or level < depth:
                queue += [(child, level + 1) for child in self.reports.get(pid, [])]
        return found

    def chain(self, pid: str) -> list[Person]:
        out, seen = [], set()
        person = self.people.get(pid)
        while person and person.manager in self.people and person.manager not in seen:
            seen.add(person.manager)
            person = self.people[person.manager]
            out.append(person)
        return out

    def snapshot(self) -> dict:
        return {pid: {**p.record(), "login": p.login, "mail": p.mail} for pid, p in self.people.items()}

    @classmethod
    def from_snapshot(cls, data: dict) -> "Directory":
        return cls({pid: Person(pid, r["name"], r.get("title", ""), r.get("department", ""), r.get("manager"),
                                r.get("login", ""), r.get("mail", "")) for pid, r in data.items()})


def fetch(graph) -> Directory:
    people = {}
    for user in graph.pages(URL, scopes=SCOPES):
        if user.get("userType") != "Member":
            continue
        people[user["id"]] = Person(
            id=user["id"], name=(user.get("displayName") or "").strip(), title=(user.get("jobTitle") or "").strip(),
            department=(user.get("department") or "").strip(), manager=(user.get("manager") or {}).get("id"),
            login=(user.get("userPrincipalName") or "").lower(), mail=(user.get("mail") or "").lower())
    return Directory(people)


# ---- the chart as markdown ------------------------------------------------------------------------

def render(directory: Directory, root: str, heading: str, depth: int, changed: str) -> str:
    members = directory.under(root, depth)
    people = directory.people
    top = people[root]
    lines = [
        f"# {heading}",
        "",
        f"*{MARKER} from the Microsoft 365 directory: {top.name} and everyone under them"
        + (f", {depth} levels down" if depth else "")
        + f". Don't edit it, as the next sync rewrites it. {len(members)} people; the directory last changed on "
        f"{changed}. A department in brackets is shown where it differs from the person's manager's.*",
        "",
    ]

    def total(pid: str) -> int:
        return len(directory.under(pid, 0))

    def line(pid: str, level: int) -> None:
        person = people[pid]
        text = f"{'  ' * level}- **{person.name}**"
        if person.title:
            text += f", {person.title}"
        manager = people.get(person.manager) if pid != root else None
        if person.department and (manager is None or manager.department != person.department):
            text += f" ({person.department})"
        children = [c for c in directory.reports.get(pid, []) if c in members]
        if children:
            everyone = total(pid) - 1
            text += f" · {len(children)} report{'s' if len(children) != 1 else ''}"
            if everyone != len(children):
                text += f", {everyone} in all"
        lines.append(text)
        for child in children:
            line(child, level + 1)

    line(root, 0)
    return "\n".join(lines) + "\n"


# ---- what changed ---------------------------------------------------------------------------------

def _describe(person: Person | dict, directory: Directory) -> str:
    record = person.record() if isinstance(person, Person) else person
    text = f"**{record['name']}**"
    if record.get("title"):
        text += f", {record['title']}"
    if record.get("department"):
        text += f" ({record['department']})"
    manager = directory.people.get(record.get("manager"))
    if manager:
        text += f", reporting to {manager.name}"
    return text


def changes(before: Directory, after: Directory, chart_before: dict[str, int], chart_after: dict[str, int],
            changes_depth: int = 0) -> list[str]:
    """What changed in one chart, as sentences. changes_depth limits them to people that many levels from
    the top (0: everyone)."""
    def counts(pid: str) -> bool:
        level = min(chart_before.get(pid, 99), chart_after.get(pid, 99))
        return not changes_depth or level <= changes_depth

    out = []
    for pid in sorted(chart_after.keys() - chart_before.keys(), key=lambda i: after.people[i].name.lower()):
        if not counts(pid):
            continue
        person = after.people[pid]
        if pid in before.people:
            out.append(f"Moved into this chart: {_describe(person, after)}. Was {_describe(before.people[pid], before)}.")
        else:
            out.append(f"New in the directory: {_describe(person, after)}.")
    for pid in sorted(chart_before.keys() - chart_after.keys(), key=lambda i: before.people[i].name.lower()):
        if not counts(pid):
            continue
        if pid in after.people:
            out.append(f"Moved out of this chart: now {_describe(after.people[pid], after)}.")
        else:
            out.append(f"Gone from the directory (left, or the account was disabled): "
                       f"{_describe(before.people[pid], before)}.")
    for pid in sorted(chart_before.keys() & chart_after.keys(), key=lambda i: after.people[i].name.lower()):
        if not counts(pid):
            continue
        old, new = before.people[pid], after.people[pid]
        name = f"**{new.name}**"
        if old.name != new.name:
            out.append(f"Renamed: **{old.name}** is now {name}.")
        if old.title != new.title:
            out.append(f"New title: {name}, {old.title or '(none)'} → {new.title or '(none)'}.")
        if old.department != new.department:
            out.append(f"New department: {name}, {old.department or '(none)'} → {new.department or '(none)'}.")
        if old.manager != new.manager:
            was = before.people.get(old.manager)
            now = after.people.get(new.manager)
            out.append(f"New manager: {name}, {was.name if was else '(none)'} → {now.name if now else '(none)'}.")
    return out


def log_changes(path: pathlib.Path, chart: str, found: list[str], when: dt.date) -> None:
    """Add today's changes at the top of the changes file, newest first."""
    header = ("# Directory changes\n\n"
              f"*{MARKER}: what each sync found had changed in the Microsoft 365 directory, newest first.*\n")
    text = path.read_text(encoding="utf-8") if path.exists() else header
    head, _, rest = text.partition("\n## ")
    entry = f"## {when.isoformat()}: {chart}\n\n" + "".join(f"- {item}\n" for item in found)
    rest = ("## " + rest) if rest else ""
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(head.rstrip("\n") + "\n\n" + entry + ("\n" + rest if rest else ""), encoding="utf-8")
