"""Synthetic sync scenarios, run through the Python version (oracle.py), saved as goldens for the .NET tests.

    python tests/parity/scenarios.py                       # the committed set: tests/OrgDirectory.Tests/Golden/scenarios.json
    python tests/parity/scenarios.py --count 2000 --seed 7 --out <file outside the repo>   # a bigger local run

Every person, number and email here is made up. Each scenario is a settings file and a few syncs in a row, with the
directory changing between them (titles, departments, managers, names, joiners, leavers, guests, a sharp shrink, a
root that goes), People First answering or failing at different ages, files changed by hand in between, dry runs, and
awkward text (quotes, commas, semicolons, ampersands, angle brackets, emoji, U+0130, final sigma, characters either
side of the UTF-16 surrogates, Unicode digits in phone numbers).
"""

from __future__ import annotations

import argparse
import base64
import datetime as dt
import json
import pathlib
import random
import shutil
import sys
import tempfile

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import oracle  # noqa: E402

FIRST = ["Ann", "Bob", "Cara", "Dee", "Eve", "Fay", "Gus", "Hal", "Ivy", "Jon", "Kim", "Lee", "Mo", "Nia", "Oli", "Pat",
         "Zoë", "Ægir", "İlker", "Σοφία", "李", "José", "Siobhán", "O'Neil", "Ann-Marie", "\ue000Priv", "\U0001F600Smile",
         "Émile", "ölaf", "Ørjan", "Łukasz", "Nguyễn", "Ann & Co", "<b>Bold", 'Quote "Q"', "Comma, Jr", "Semi;Colon",
         "Back\\slash", "Tab\tName", "Æsir", "ǅemal", "ΣΑΣ", "ΌΣΟΣ"]
LAST = ["Smith", "Jones", "Brown", "O'Brien", "Müller", "Ångström", "van der Berg", "Smith-Jones", "Wright", "Patel",
        "Khan", "Lewis", "Ng", "Ó Súilleabháin", "Dąbrowski", "ÇELİK", "Σ", "名", "\U0001F680", "&amp;", "Lee;Park",
        "Ali,Khan", '"Nick"']
TITLES = ["", "Developer", "Senior Developer", "Engineering Manager", "Solutions Architect", "Head of Stuff",
          "Director, Finance", "R&D Lead", "Tester <QA>", 'The "Boss"', "Chief; Officer", "Ops\u00a0Lead", "  padded  ",
          "Line\u2028Sep", "Café owner", "İnspector"]
DEPTS = ["", "Technology", "Finance", "Architecture", "Sales & Marketing", "People", "Ops", "Customer Service",
         "Directors", "Ünits"]
PHONES = ["", "0161 000 0000", "+44 (0) 7700 900123", "07700-900-456", "０７７００ ９００７８９", "ext. 1234", "+1;2,3",
          "  01234 567890  ", "٠١٢٣٤٥", "²³", "(no number)", "07700 900\U0001D7D8"]
EMAIL_SAFE = "abcdefghijklmnopqrstuvwxyz"


def name(rng: random.Random, used: set[str]) -> str:
    for _ in range(200):
        text = f"{rng.choice(FIRST)} {rng.choice(LAST)}"
        if rng.random() < 0.05:
            text = rng.choice(FIRST)   # a one-word name (vCard N with no family name)
        if rng.random() < 0.05:
            text = f"  {text} "        # stripped on the way in
        key = text.strip().lower()
        if key not in used:
            used.add(key)
            return text
    text = f"Person {len(used)}"
    used.add(text.lower())
    return text


def user(rng: random.Random, uid: str, used: set[str], manager: str | None) -> dict:
    u = {"id": uid, "displayName": name(rng, used), "jobTitle": rng.choice(TITLES), "department": rng.choice(DEPTS),
         "userPrincipalName": f"{uid}@Example.com" if rng.random() < 0.3 else f"{uid}@example.com",
         "mail": f"{uid}@example.com" if rng.random() < 0.9 else None, "userType": "Member",
         "manager": {"id": manager} if manager else None,
         "businessPhones": [rng.choice(PHONES)] if rng.random() < 0.7 else [],
         "mobilePhone": rng.choice(PHONES) if rng.random() < 0.3 else None}
    r = rng.random()
    if r < 0.05:
        u["userType"] = "Guest"
    elif r < 0.08:
        del u["jobTitle"]
    elif r < 0.11:
        u["department"] = None
    elif r < 0.13:
        u["manager"] = {"id": "nobody-" + uid}   # a manager the directory hasn't got
    elif r < 0.15:
        u["businessPhones"] = [None]
    return u


def org(rng: random.Random, size: int) -> list[dict]:
    used: set[str] = set()
    users = [user(rng, "u0", used, None)]
    users[0]["userType"] = "Member"
    for i in range(1, size):
        manager = f"u{rng.randrange(0, i)}" if rng.random() > 0.03 else None
        users.append(user(rng, f"u{i}", used, manager))
    if rng.random() < 0.1 and size > 6:   # a manager loop away from the top
        a, b = users[-1], users[-2]
        a["manager"], b["manager"] = {"id": b["id"]}, {"id": a["id"]}
    return users


def mutate(rng: random.Random, users: list[dict], used: set[str]) -> list[dict]:
    users = json.loads(json.dumps(users))
    for _ in range(rng.randrange(0, 6)):
        if len(users) < 3:
            break
        u = rng.choice(users[1:])
        kind = rng.randrange(0, 9)
        if kind == 0:
            u["jobTitle"] = rng.choice(TITLES)
        elif kind == 1:
            u["department"] = rng.choice(DEPTS)
        elif kind == 2:
            u["manager"] = {"id": rng.choice(users)["id"]}
        elif kind == 3:
            u["displayName"] = name(rng, used)
        elif kind == 4:
            users.remove(u)
        elif kind == 5:
            users.append(user(rng, f"n{rng.randrange(10**6)}", used, rng.choice(users)["id"]))
        elif kind == 6:
            u["userType"] = "Guest" if u.get("userType") == "Member" else "Member"
        elif kind == 7:
            u["manager"] = None
        else:
            u["mobilePhone"] = rng.choice(PHONES)
    if rng.random() < 0.05 and len(users) > 20:   # a sharp shrink
        users = users[: len(users) // 3]
    if rng.random() < 0.03:                         # the top goes
        users = users[1:]
    return users


def settings(rng: random.Random, users: list[dict]) -> str:
    def who(u):
        r = rng.random()
        if r < 0.6:
            return f"{u['id']}@example.com"
        if r < 0.75:
            return f"{u['id'].upper()}@EXAMPLE.COM"
        if r < 0.9:
            return u["displayName"].strip().replace("\\", "\\\\").replace('"', '\\"').replace("\t", "\\t")
        return "missing@example.com"

    lines = ['state_file = "state.json"', 'after_run = ["python", "hook.py"]']
    if rng.random() < 0.8:
        lines.append('changes_file = "org/changes.md"')
    for i in range(rng.randrange(1, 4)):
        lines += ["[[chart]]", f'name = "c{i}"', f'root = "{who(rng.choice(users[: max(1, len(users) // 3)]))}"',
                  f'output = "org/c{i}.md"']
        if rng.random() < 0.6:
            lines.append(f'heading = "Heading {i} & co"')
        if rng.random() < 0.4:
            lines.append(f"depth = {rng.randrange(0, 4)}")
        if rng.random() < 0.4:
            lines.append(f"changes_depth = {rng.randrange(0, 3)}")
    pf = rng.random() < 0.5
    if rng.random() < 0.7:
        lines += ["[contacts]", 'folder = "private"']
        for g in range(rng.randrange(1, 4)):
            lines += ["[[contacts.group]]", f'name = "Group {g} <&>"']
            if rng.random() < 0.7:
                lines.append(f'root = "{who(rng.choice(users))}"')
                if rng.random() < 0.5:
                    lines.append(f"depth = {rng.randrange(0, 3)}")
            if rng.random() < 0.5 or not lines[-1].startswith(("root", "depth")):
                people = [who(rng.choice(users)) for _ in range(rng.randrange(1, 5))]
                lines.append("people = [" + ", ".join(f'"{p}"' for p in people) + "]")
    if pf or rng.random() < 0.2:
        lines += ["[people_first]", f"enabled = {'true' if pf else 'false'}", 'url = "https://example.people-first.com"']
    return "\n".join(lines) + "\n"


def numbers(rng: random.Random, users: list[dict]) -> dict:
    found = {}
    for u in rng.sample(users, min(len(users), rng.randrange(0, 8))):
        entry = {}
        for key in ("companyMobile", "personalMobile", "personalNumber"):
            if rng.random() < 0.5:
                entry[key] = rng.choice(PHONES[1:])
        key = (u.get("mail") or u["userPrincipalName"]).lower() if rng.random() < 0.9 else "stranger@example.com"
        if rng.random() < 0.1:
            entry = {}
        found[key] = entry
    return found


def b64(text: str | bytes) -> str:
    return base64.b64encode(text.encode() if isinstance(text, str) else text).decode()


def ops(rng: random.Random) -> list[dict]:
    r = rng.random()
    if r < 0.04:
        return [{"path": "org/c0.md", "bytes": b64("# My hand-made chart\r\n")}]
    if r < 0.07:
        return [{"path": "org/c0.md", "bytes": b64("# x\r\n*Written by org-directory, edited by hand*\r\n")}]
    if r < 0.10:
        return [{"path": "state.json", "delete": True}]
    if r < 0.12:
        return [{"path": "state.json", "bytes": b64("{not json")}]
    if r < 0.13:
        return [{"path": "state.json", "bytes": b64(b"\xef\xbb\xbf" + b'{"people": {}}')}]
    if r < 0.15:
        return [{"path": "org/changes.md", "bytes": b64("no headings here\n\n\n")}]
    if r < 0.17:
        return [{"path": "org/changes.md", "bytes": b64("# Directory changes\r\n\r\n## 2026-01-01: old\r\n\r\n- something\r\n")}]
    if r < 0.18:
        return [{"path": "org/changes.md", "bytes": b64("head\n## ")}]
    if r < 0.20:
        return [{"path": "private/contacts.csv", "delete": True}]
    return []


def scenario(rng: random.Random, index: int) -> dict:
    users = org(rng, rng.randrange(3, 45))
    used = {(u.get("displayName") or "").strip().lower() for u in users}
    steps = []
    day = dt.datetime(2026, 11, 2, 9, 0, 0) + dt.timedelta(days=rng.randrange(0, 30))
    for n in range(rng.randrange(1, 5)):
        if n:
            users = mutate(rng, users, used)
            day += dt.timedelta(days=rng.choice([0, 1, 1, 2, 3, 4, 7]), minutes=rng.randrange(0, 600))
        r = rng.random()
        pf = {"numbers": numbers(rng, users)} if r < 0.7 else {"error": rng.choice(
            ["People First answered 503", "no sign-in", "Timed out \u2192 again"])}
        steps.append({"users": users, "now": day.isoformat(), "today": day.date().isoformat(),
                      "dry_run": rng.random() < 0.1, "people_first": pf, "ops": ops(rng) if n else [],
                      "hook_exit": rng.choice([0, 0, 0, 3]), "hook_stdout": rng.choice(["", "committed 2 files\n", "a\n\n  b  \r\nc"]),
                      "hook_stderr": rng.choice(["", "warning: x\n"])})
    queries = [rng.choice(users)["displayName"].strip().split(" ")[0] if users else "x", "a", "zzz-nobody", "@example.com"]
    return {"name": f"s{index:04}", "settings": settings(rng, users), "steps": steps, "who": queries}


def run(case: dict) -> dict:
    folder = pathlib.Path(tempfile.mkdtemp(prefix="org-directory-oracle-")).resolve()
    try:
        config = folder / "config.toml"
        config.write_text(case["settings"], encoding="utf-8")
        results = [oracle.step(config, s) for s in case["steps"]]
        whos = [oracle.who(config, q) for q in case["who"]]
        return {**case, "results": results, "who_results": whos}
    finally:
        shutil.rmtree(folder, ignore_errors=True)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--count", type=int, default=120)
    parser.add_argument("--seed", type=int, default=1)
    parser.add_argument("--out", default=str(oracle.ROOT / "tests" / "OrgDirectory.Tests" / "Golden" / "scenarios.json"))
    args = parser.parse_args()
    rng = random.Random(args.seed)
    cases = [run(scenario(rng, i)) for i in range(args.count)]
    out = pathlib.Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps({"python": sys.version.split()[0], "seed": args.seed, "cases": cases}, ensure_ascii=True,
                              indent=None), encoding="utf-8")
    steps = sum(len(c["steps"]) for c in cases)
    errors = sum(1 for c in cases for r in c["results"] if "error" in r)
    print(f"{len(cases)} scenarios, {steps} syncs ({errors} raised), written to {out}")


if __name__ == "__main__":
    main()
