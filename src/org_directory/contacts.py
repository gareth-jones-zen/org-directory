"""Contact sheets for an emergency: groups of people with their phone numbers, kept where you can
reach them when Teams and Outlook can't be.

The numbers come from the directory (work phone, work mobile) and, if switched on, People First
(personal mobile and phone, for your own reporting line). **The sheets are written only to
`[contacts] folder`**, never beside the charts, because they hold personal numbers. Three files,
for three ways of reaching them:
- **`contacts.html`**: one page, readable on a phone and printable, with numbers you can tap to call;
- **`contacts.csv`**: for a spreadsheet;
- **`contacts.vcf`**: to import into a phone's contacts.
Each says when the directory and People First were last read, so a stale sheet is obvious.
"""

from __future__ import annotations

import csv
import dataclasses
import html
import io
import pathlib

from .directory import Directory, Person


@dataclasses.dataclass
class Group:
    name: str
    root: str = ""
    depth: int = 0
    people: list[str] = dataclasses.field(default_factory=list)


@dataclasses.dataclass
class Settings:
    folder: pathlib.Path | None = None
    groups: list[Group] = dataclasses.field(default_factory=list)


@dataclasses.dataclass
class Row:
    person: Person
    manager: str
    mobile: str          # personal mobile (People First)
    work_mobile: str     # People First's company mobile, else the directory's
    home: str            # personal phone (People First)
    work_phone: str      # the directory's


def members(directory: Directory, group: Group) -> tuple[list[Person], list[str]]:
    """The group's people, top first then by name, and any it names that the directory hasn't got."""
    found, missing = [], []
    if group.root:
        top = directory.find(group.root)
        if top is None:
            missing.append(group.root)
        else:
            levels = directory.under(top.id, group.depth)
            found += sorted((directory.people[i] for i in levels), key=lambda p: (levels[p.id], p.name.lower()))
    for who in group.people:
        person = directory.find(who)
        if person is None:
            missing.append(who)
        elif person not in found:
            found.append(person)
    return found, missing


def rows(directory: Directory, people: list[Person], numbers: dict[str, dict]) -> list[Row]:
    out = []
    for person in people:
        extra = numbers.get(person.mail) or numbers.get(person.login) or {}
        manager = directory.people.get(person.manager)
        out.append(Row(person, manager.name if manager else "", extra.get("personalMobile", ""),
                       extra.get("companyMobile") or person.mobile, extra.get("personalNumber", ""), person.work_phone))
    return out


def _tel(number: str) -> str:
    return "".join(c for c in number if c.isdigit() or c == "+")


def render_html(sections: list[tuple[str, list[Row]]], stamp: str) -> str:
    css = """
:root{color-scheme:light dark;--bg:#fff;--fg:#111;--muted:#666;--line:#ddd;--accent:#0b5cad}
@media (prefers-color-scheme:dark){:root{--bg:#121212;--fg:#eee;--muted:#aaa;--line:#333;--accent:#7ab7ff}}
body{background:var(--bg);color:var(--fg);font:15px/1.4 system-ui,sans-serif;margin:0 auto;max-width:960px;padding:16px}
h1{font-size:1.4em;margin:0 0 4px}h2{font-size:1.15em;margin:24px 0 8px;border-bottom:2px solid var(--line)}
.stamp{color:var(--muted);font-size:.9em}.person{padding:8px 0;border-bottom:1px solid var(--line)}
.name{font-weight:600}.role{color:var(--muted);font-size:.9em}.nums{margin-top:2px}
.nums a{color:var(--accent);text-decoration:none;margin-right:14px;white-space:nowrap}
.none{color:var(--muted);font-style:italic}
@media print{body{max-width:none;font-size:11pt}.person{break-inside:avoid}a{color:#000}}
"""
    out = ["<!doctype html><html lang='en-GB'><head><meta charset='utf-8'>",
           "<meta name='viewport' content='width=device-width,initial-scale=1'>",
           "<title>Emergency contacts</title><style>", css, "</style></head><body>",
           "<h1>Emergency contacts</h1>", f"<p class='stamp'>{html.escape(stamp)}</p>"]
    for name, group in sections:
        out.append(f"<h2>{html.escape(name)} ({len(group)})</h2>")
        for row in group:
            role = ", ".join(x for x in (row.person.title, f"reports to {row.manager}" if row.manager else "") if x)
            nums = [(label, n) for label, n in (("Mobile", row.mobile), ("Work mobile", row.work_mobile),
                                                ("Home", row.home), ("Work", row.work_phone)) if n]
            links = "".join(f"<a href='tel:{_tel(n)}'>{label}: {html.escape(n)}</a>" for label, n in nums)
            out.append(f"<div class='person'><div class='name'>{html.escape(row.person.name)}</div>"
                       f"<div class='role'>{html.escape(role)}</div><div class='nums'>"
                       f"{links or '<span class=none>No number</span>'}</div></div>")
    out.append("</body></html>\n")
    return "\n".join(out)


def render_csv(sections: list[tuple[str, list[Row]]]) -> str:
    buffer = io.StringIO()
    writer = csv.writer(buffer, lineterminator="\n")
    writer.writerow(["Group", "Name", "Title", "Department", "Manager", "Mobile", "Work mobile", "Home", "Work phone",
                     "Email"])
    for name, group in sections:
        for row in group:
            p = row.person
            writer.writerow([name, p.name, p.title, p.department, row.manager, row.mobile, row.work_mobile, row.home,
                             row.work_phone, p.mail or p.login])
    return buffer.getvalue()


def _v(text: str) -> str:
    return text.replace("\\", "\\\\").replace(",", "\\,").replace(";", "\\;").replace("\n", "\\n")


def render_vcf(sections: list[tuple[str, list[Row]]], stamp: str) -> str:
    cards, seen = [], set()
    for name, group in sections:
        for row in group:
            p = row.person
            if p.id in seen:
                continue
            seen.add(p.id)
            last, _, first = p.name.rpartition(" ") if " " in p.name else ("", "", p.name)
            card = ["BEGIN:VCARD", "VERSION:3.0", f"FN:{_v(p.name)}", f"N:{_v(first if last else '')};{_v(last or p.name)};;;"]
            if p.title:
                card.append(f"TITLE:{_v(p.title)}")
            for kind, number in (("CELL", row.mobile), ("WORK,CELL", row.work_mobile), ("HOME", row.home),
                                 ("WORK,VOICE", row.work_phone)):
                if number:
                    card.append(f"TEL;TYPE={kind}:{number}")
            if p.mail:
                card.append(f"EMAIL;TYPE=WORK:{p.mail}")
            card += [f"NOTE:{_v(stamp)}", "END:VCARD"]
            cards.append("\r\n".join(card))
    return "\r\n".join(cards) + "\r\n"


def write(settings: Settings, directory: Directory, numbers: dict[str, dict], stamp: str, dry_run: bool = False,
          log=print) -> list[str]:
    """Write the three sheets if they changed. Returns the paths written."""
    sections, written = [], []
    for group in settings.groups:
        people, missing = members(directory, group)
        for who in missing:
            log(f"contacts: {group.name}: {who} isn't in the directory")
        sections.append((group.name, rows(directory, people, numbers)))
    outputs = {"contacts.html": render_html(sections, stamp), "contacts.csv": render_csv(sections),
               "contacts.vcf": render_vcf(sections, stamp)}
    for name, text in outputs.items():
        path = settings.folder / name
        if path.exists() and path.read_text(encoding="utf-8") == text:
            continue
        written.append(str(path))
        if not dry_run:
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(text, encoding="utf-8", newline="")
    count = sum(len(g) for _, g in sections)
    with_mobile = sum(1 for _, g in sections for r in g if r.mobile or r.work_mobile)
    log(f"contacts: {count} entries in {len(sections)} group(s), {with_mobile} with a mobile"
        + ("" if written else "; unchanged"))
    return written
