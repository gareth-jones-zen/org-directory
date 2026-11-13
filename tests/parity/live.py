"""Live parity on GJ's laptop: the Python and .NET versions render the same recorded directory, side by side.

Never run by CI, and it never touches the real outputs: everything happens in copies under a work folder, which the
caller deletes afterwards. It prints counts, hashes and line numbers only, never a name, title, email or number.

    python tests/parity/live.py settings <EA toml> <work folder> <side>      settings for one side (A or B), in <work>/<side>
    python tests/parity/live.py people-first <work>/<side>/config.toml <out.json>
    python tests/parity/live.py render <work>/<side>/config.toml <directory.json> <numbers.json> <now> <report.json>
    python tests/parity/live.py compare <work>/A <work>/B

`settings` copies the settings' charts, contacts groups and People First switch, with every output, the state file
and the changes file pointing into the side's folder; the caller seeds those with copies of the real files first.
"""

from __future__ import annotations

import datetime as real_dt
import difflib
import hashlib
import json
import pathlib
import sys
import tomllib

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import oracle  # noqa: E402

from org_directory import config as cfg, peoplefirst, run as run_mod  # noqa: E402


def q(text: str) -> str:
    return json.dumps(text, ensure_ascii=False)


def settings(ea_toml: str, work: str, side: str, profile: str) -> None:
    raw = tomllib.loads(pathlib.Path(ea_toml).read_text(encoding="utf-8"))
    folder = pathlib.Path(work) / side
    folder.mkdir(parents=True, exist_ok=True)
    lines = ['state_file = "state.json"', 'changes_file = "charts/changes.md"']
    if raw.get("login_hint"):
        lines.append(f"login_hint = {q(raw['login_hint'])}")
    for chart in raw.get("chart", []):
        lines += ["[[chart]]", f"name = {q(chart['name'])}", f"root = {q(chart['root'])}",
                  f"output = {q('charts/' + pathlib.Path(chart['output']).name)}"]
        for key in ("heading", "depth", "changes_depth"):
            if key in chart:
                lines.append(f"{key} = {json.dumps(chart[key], ensure_ascii=False)}")
    contacts = raw.get("contacts")
    if contacts:
        lines += ["[contacts]", 'folder = "contacts"']
        for group in contacts.get("group", []):
            lines += ["[[contacts.group]]"] + [f"{k} = {json.dumps(v, ensure_ascii=False)}" for k, v in group.items()]
    pf = raw.get("people_first")
    if pf:
        lines += ["[people_first]", f"enabled = {'true' if pf.get('enabled') else 'false'}", f"url = {q(pf['url'])}",
                  f"edge_profile = {q(profile)}"]
    (folder / "config.toml").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"settings for {side}: {len(raw.get('chart', []))} charts, "
          f"{len((contacts or {}).get('group', []))} contact groups, People First {'on' if pf and pf.get('enabled') else 'off'}")


def people_first(config_path: str, out: str) -> None:
    config = cfg.load(pathlib.Path(config_path))
    found = peoplefirst.fetch(config.people_first, log=lambda text: None)
    text = json.dumps(found)
    pathlib.Path(out).write_text(text, encoding="utf-8")
    print(f"people first: {len(found)} with a number, sha256 {hashlib.sha256(text.encode()).hexdigest()}")


def render(config_path: str, directory: str, numbers: str, now: str, out: str) -> None:
    config = cfg.load(pathlib.Path(config_path))
    users = json.loads(pathlib.Path(directory).read_text(encoding="utf-8"))
    found = None if numbers == "FAIL" else json.loads(pathlib.Path(numbers).read_text(encoding="utf-8"))

    def read(settings, log=print):
        if found is None:
            raise RuntimeError("People First answered 503")   # the failure path: the last good numbers are kept
        return json.loads(json.dumps(found))
    when = real_dt.datetime.fromisoformat(now)
    logs = []
    saved = run_mod.dt
    run_mod.dt = oracle.frozen(when, when.date())
    try:
        report = run_mod.sync(config, graph=oracle.FakeGraph(users), log=logs.append,
                              read_people_first=read)
    finally:
        run_mod.dt = saved
    pathlib.Path(out).write_text(json.dumps({"report": json.dumps(report, indent=1), "log": logs}), encoding="utf-8")
    print(f"rendered: {len(report['written'])} written, {len(report['changes'])} change(s), {report['unchanged']} unchanged, "
          f"{report['people']} people, {len(report['failed'])} failed")


def compare(a: str, b: str) -> int:
    a_dir, b_dir = pathlib.Path(a).resolve(), pathlib.Path(b).resolve()
    differences = 0
    for folder in ("charts",):
        for path in sorted((a_dir / folder).glob("*.md")):
            other = b_dir / folder / path.name
            left = path.read_text(encoding="utf-8").splitlines()
            right = other.read_text(encoding="utf-8").splitlines() if other.exists() else []
            changed = [i + 1 for i, (x, y) in enumerate(zip(left, right)) if x != y]
            extra = abs(len(left) - len(right))
            same = path.read_bytes() == (other.read_bytes() if other.exists() else b"")
            print(f"{folder}/{path.name}: {len(left)} lines; {'byte-identical' if same else 'DIFFERENT'}; "
                  f"{len(changed)} differing line(s){' at ' + ', '.join(map(str, changed[:20])) if changed else ''}"
                  + (f"; {extra} line(s) only on one side" if extra else ""))
            differences += 0 if same else 1
    for name in ("contacts/contacts.html", "contacts/contacts.csv", "contacts/contacts.vcf", "state.json"):
        x, y = a_dir / name, b_dir / name
        hx = hashlib.sha256(x.read_bytes()).hexdigest() if x.exists() else "missing"
        hy = hashlib.sha256(y.read_bytes()).hexdigest() if y.exists() else "missing"
        rows = ""
        if name.endswith(".csv") and x.exists():
            rows = f", {len(x.read_text(encoding='utf-8').splitlines()) - 1} rows"
        elif name.endswith(".vcf") and x.exists():
            rows = f", {x.read_text(encoding='utf-8').count('BEGIN:VCARD')} cards"
        elif name.endswith(".html") and x.exists():
            rows = f", {x.read_text(encoding='utf-8').count(chr(39) + 'person' + chr(39))} people"
        print(f"{name}: {'identical' if hx == hy else 'DIFFERENT'} (sha256 {hx[:16]}... vs {hy[:16]}...{rows})")
        differences += 0 if hx == hy else 1
    ra = json.loads((a_dir / "report.json").read_text(encoding="utf-8"))
    rb = json.loads((b_dir / "report.json").read_text(encoding="utf-8"))
    def mask(value, folder):
        return [oracle.placeholder(v, folder) for v in value] if isinstance(value, list) else oracle.placeholder(value, folder)

    for key in ("report", "log"):
        ta = json.dumps(mask(ra[key], a_dir))
        tb = json.dumps(mask(rb[key], b_dir))
        same = ta == tb
        print(f"{key}: {'identical' if same else 'DIFFERENT'} (sha256 {hashlib.sha256(ta.encode()).hexdigest()[:16]}...)")
        differences += 0 if same else 1
    print(f"{differences} difference(s)")
    return 1 if differences else 0


def who(config_path: str, *queries: str) -> None:
    """Each query's exit code and the SHA-256 of what `who` printed, as ParityRunner who prints them."""
    for query in queries:
        result = oracle.who(pathlib.Path(config_path).resolve(), query)
        text = result["out"] + result["err"]
        print(result["code"], hashlib.sha256(text.replace(oracle.PLACEHOLDER, str(pathlib.Path(config_path).resolve().parent)).encode()).hexdigest())


if __name__ == "__main__":
    command, rest = sys.argv[1], sys.argv[2:]
    if command == "settings":
        settings(*rest)
    elif command == "people-first":
        people_first(*rest)
    elif command == "render":
        render(*rest)
    elif command == "who":
        who(*rest)
    elif command == "compare":
        sys.exit(compare(*rest))
