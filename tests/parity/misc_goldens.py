"""Smaller goldens from the Python version: the csv module's quoting, tel: links, html.escape, the vCard escaping,
sorting by str.lower(), and the settings errors. Writes tests/OrgDirectory.Tests/Golden/misc.json.

    python tests/parity/misc_goldens.py
"""

from __future__ import annotations

import html
import json
import pathlib
import random
import shutil
import sys
import tempfile

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import oracle  # noqa: E402,F401  (puts src/ on the path)

from org_directory import config as cfg, contacts  # noqa: E402

ALPHABET = list("ab ,;\"'\\\t\r\n\x0b\x0c\x1c\x1d\x85\xa0&<>+0123456789") + [" ", " ", "０", "٠", "²",
                                                                         "\U0001d7d8", "\U0001f600", "İ", "Σ", ""]


def text(rng: random.Random, longest: int = 12) -> str:
    return "".join(rng.choice(ALPHABET) for _ in range(rng.randrange(0, longest)))


SETTINGS = [
    'daily_at = "9:00"',
    'daily_at = "24:00"',
    "daily_at = 900",
    '[[chart]]\nname = "x"\nroot = "a@b"',
    '[[chart]]\nroot = "a@b"',
    '[[chart]]\nname = "x"\noutput = "x.md"',
    '[[chart]]\nname = "x"\nroot = ""\noutput = ""',
    '[[chart]]\nname = "x"\nroot = "a"\noutput = "a.md"\n[[chart]]\nname = "x"\nroot = "b"\noutput = "b.md"',
    '[contacts]\ngroup = []',
    '[contacts]\nfolder = "p"',
    '[contacts]\nfolder = "p"\n[[contacts.group]]\nname = "g"',
    '[contacts]\nfolder = "p"\n[[contacts.group]]\ndepth = 1',
    '[people_first]\nenabled = true',
    '[people_first]\nenabled = true\nurl = "http://x.people-first.com"',
    '[people_first]\nenabled = true\nurl = "https://example.com"',
    '[people_first]\nenabled = true\nurl = "https://acme-live.people-first.com//"',
    '[people_first]\nenabled = true\nurl = "https://example.com"\ntenant_code = "t"',
    '[people_first]\nenabled = true\nurl = "https://ACME.people-first.com"\naccount = "me@x"',
    'login_hint = "hint@x"\n[people_first]\nenabled = true\nurl = "https://acme.people-first.com"',
    '[people_first]\nenabled = false\nurl = "nonsense"',
    'tenant = ""',
    'state_file = "~/x.json"\nlog_file = "%NO_SUCH_VAR%/l.txt"\nchanges_file = "a/../b/c.md"',
]


def settings_case(source: str) -> dict:
    folder = pathlib.Path(tempfile.mkdtemp(prefix="org-directory-oracle-")).resolve()
    try:
        path = folder / "config.toml"
        path.write_text(source + "\n", encoding="utf-8")
        try:
            c = cfg.load(path)
            pf = c.people_first
            result = {"ok": {"tenant": c.tenant, "daily_at": c.daily_at, "charts": [ch.name for ch in c.charts],
                             "state_file": oracle.placeholder(str(c.state_file), folder) if c.state_file.is_relative_to(folder) else "(elsewhere)",
                             "changes_file": oracle.placeholder(str(c.changes_file), folder) if c.changes_file else None,
                             "people_first": [pf.enabled, getattr(pf, "url", ""), getattr(pf, "tenant_code", ""),
                                              getattr(pf, "account", "")]}}
        except (ValueError, FileNotFoundError) as error:
            result = {"error": oracle.placeholder(str(error), folder)}
        return {"settings": source, **result}
    finally:
        shutil.rmtree(folder, ignore_errors=True)


def main() -> None:
    rng = random.Random(11)
    rows = [[text(rng) for _ in range(rng.randrange(1, 5))] for _ in range(3000)] + [[""], ["", ""], [" "], ["\r"]]
    csv_cases = [{"row": r, "out": _csv(r)} for r in rows]
    tel = [{"in": t, "out": contacts._tel(t)} for t in (text(rng, 20) for _ in range(3000))]
    escape = [{"in": t, "html": html.escape(t), "vcf": contacts._v(t)} for t in (text(rng, 20) for _ in range(2000))]
    names = [text(rng, 6) for _ in range(400)]
    order = sorted(range(len(names)), key=lambda i: names[i].lower())
    lower = [{"in": t, "out": t.lower()} for t in names]
    out = pathlib.Path(oracle.ROOT, "tests", "OrgDirectory.Tests", "Golden", "misc.json")
    out.write_text(json.dumps({"python": sys.version.split()[0], "csv": csv_cases, "tel": tel, "escape": escape,
                               "names": names, "order": order, "lower": lower,
                               "settings": [settings_case(s) for s in SETTINGS], "locks": locks(),
                               "repr": [{"in": t, "out": repr(t)} for t in (text(rng, 10) for _ in range(2000))]}, ensure_ascii=True), encoding="utf-8")
    print(f"written {out}")


def locks() -> list[dict]:
    """The run lock's name for a few settings paths, as locks.run names it (win_background.key)."""
    from win_background import key
    paths = [r"C:\Example\Tools\org-directory.toml", r"C:\Users\Someone\OneDrive - Example Ltd\repo\tools\org-directory.toml",
             r"D:\ÜBER\Ståle\config.toml", "C:\\x\\\u0130\\config.toml"]
    return [{"path": p, "name": f"org-directory-run-{key(pathlib.Path(p))}"} for p in paths]


def _csv(row: list[str]) -> str:
    import csv
    import io
    buffer = io.StringIO()
    csv.writer(buffer, lineterminator="\n").writerow(row)
    return buffer.getvalue()


if __name__ == "__main__":
    main()
