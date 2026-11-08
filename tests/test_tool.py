"""Run with: python -m unittest discover -s tests"""

import datetime as dt
import json
import pathlib
import sys
import tempfile
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / "src"))

from org_directory import config as cfg  # noqa: E402
from org_directory.directory import MARKER  # noqa: E402
from org_directory.run import sync  # noqa: E402

DAY1, DAY2 = dt.date(2026, 11, 8), dt.date(2026, 11, 9)


def user(uid, name, title, dept, manager=None, kind="Member"):
    return {"id": uid, "displayName": name, "jobTitle": title, "department": dept,
            "userPrincipalName": f"{uid}@example.com", "mail": f"{uid}@example.com", "userType": kind,
            "manager": {"id": manager} if manager else None}


def org():
    return [
        user("ceo", "Cara Chief", "CEO", "Directors"),
        user("cto", "Tom Tech", "Technology Director", "Technology", "ceo"),
        user("fd", "Fay Finance", "Finance Director", "Finance", "ceo"),
        user("arch", "Ann Architect", "Principal Architect", "Technology", "cto"),
        user("sa1", "Bob Builder", "Solutions Architect", "Technology", "arch"),
        user("sa2", "Dee Design", "Solutions Architect", "Architecture", "arch"),
        user("em", "Eve Manager", "Engineering Manager", "Technology", "cto"),
        user("guest", "Gus Guest", "", "", "cto", kind="Guest"),
    ]


class FakeGraph:
    def __init__(self, users):
        self.users = users

    def pages(self, url, scopes=None):
        assert "$expand=manager" in url and " " not in url
        yield from self.users


class Tool(unittest.TestCase):
    def setUp(self):
        self.dir = pathlib.Path(tempfile.mkdtemp()).resolve()
        (self.dir / "config.toml").write_text(f"""
state_file = "state.json"
log_file = "log.txt"
changes_file = "org/changes.md"
[[chart]]
name = "tech"
root = "cto@example.com"
output = "org/tech.md"
heading = "Technology"
[[chart]]
name = "top"
root = "ceo@example.com"
output = "org/top.md"
depth = 1
""", encoding="utf-8")
        self.config = cfg.load(self.dir / "config.toml")

    def sync(self, users, day=DAY1, **kw):
        return sync(self.config, graph=FakeGraph(users), today=day, log=lambda text: None, **kw)

    def read(self, name):
        return (self.dir / "org" / name).read_text(encoding="utf-8")

    def test_the_first_sync_writes_each_chart_with_no_changes(self):
        report = self.sync(org())
        self.assertEqual(len(report["written"]), 2)
        self.assertEqual(report["changes"], [])
        tech = self.read("tech.md")
        self.assertIn(MARKER, tech)
        self.assertIn("- **Tom Tech**, Technology Director (Technology) · 2 reports, 4 in all", tech)
        self.assertIn("  - **Ann Architect**, Principal Architect · 2 reports\n", tech)
        self.assertIn("    - **Bob Builder**, Solutions Architect\n", tech)
        # The department shows only where it differs from the manager's.
        self.assertIn("    - **Dee Design**, Solutions Architect (Architecture)\n", tech)
        self.assertNotIn("Gus Guest", tech)
        self.assertIn("5 people; the directory last changed on 2026-11-08", tech)
        self.assertFalse((self.dir / "org" / "changes.md").exists())

    def test_depth_limits_a_chart(self):
        self.sync(org())
        top = self.read("top.md")
        self.assertIn("**Tom Tech**", top)
        self.assertNotIn("Ann Architect", top)
        self.assertIn("1 levels down", top)

    def test_an_unchanged_directory_writes_nothing(self):
        self.sync(org())
        report = self.sync(org(), day=DAY2)
        self.assertEqual(report["written"], [])
        self.assertEqual(report["unchanged"], 2)
        self.assertIn("last changed on 2026-11-08", self.read("tech.md"))

    def test_changes_are_found_and_logged(self):
        self.sync(org())
        users = org()
        users[4]["jobTitle"] = "Senior Solutions Architect"                    # Bob: new title
        users[6]["manager"] = {"id": "arch"}                                   # Eve: new manager
        users[2]["manager"] = {"id": "cto"}                                    # Fay: moves into tech
        del users[5]                                                           # Dee: leaves
        users.append(user("new", "Nia New", "Developer", "Technology", "em"))  # Nia: joins
        report = self.sync(users, day=DAY2)
        tech = [c["text"] for c in report["changes"] if c["chart"] == "tech"]
        self.assertIn("New in the directory: **Nia New**, Developer (Technology), reporting to Eve Manager.", tech)
        self.assertIn("Moved into this chart: **Fay Finance**, Finance Director (Finance), reporting to Tom Tech. "
                      "Was **Fay Finance**, Finance Director (Finance), reporting to Cara Chief.", tech)
        self.assertIn("Gone from the directory (left, or the account was disabled): **Dee Design**, Solutions "
                      "Architect (Architecture), reporting to Ann Architect.", tech)
        self.assertIn("New title: **Bob Builder**, Solutions Architect → Senior Solutions Architect.", tech)
        self.assertIn("New manager: **Eve Manager**, Tom Tech → Ann Architect.", tech)
        top = [c["text"] for c in report["changes"] if c["chart"] == "top"]
        self.assertEqual(top, ["Moved out of this chart: now **Fay Finance**, Finance Director (Finance), "
                               "reporting to Tom Tech."])
        log = self.read("changes.md")
        self.assertLess(log.index("## 2026-11-09: Technology"), log.index("New title"))
        self.assertIn(str(self.dir / "org" / "changes.md"), report["written"])
        self.assertIn("last changed on 2026-11-09", self.read("tech.md"))

    def test_changes_depth_keeps_a_big_chart_quiet(self):
        text = (self.dir / "config.toml").read_text(encoding="utf-8").replace('heading = "Technology"',
                                                                              'heading = "Technology"\nchanges_depth = 1')
        (self.dir / "config.toml").write_text(text, encoding="utf-8")
        self.config = cfg.load(self.dir / "config.toml")
        self.sync(org())
        users = org()
        users[4]["jobTitle"] = "Senior Solutions Architect"   # two levels down: not reported
        users[1]["jobTitle"] = "CTO"                          # the top: reported
        tech = [c["text"] for c in self.sync(users, day=DAY2)["changes"] if c["chart"] == "tech"]
        self.assertEqual(tech, ["New title: **Tom Tech**, Technology Director → CTO."])
        self.assertIn("Senior Solutions Architect", self.read("tech.md"))   # the chart itself is complete

    def test_a_file_it_did_not_write_is_left_alone(self):
        (self.dir / "org").mkdir()
        (self.dir / "org" / "tech.md").write_text("# My hand-made chart\n", encoding="utf-8")
        report = self.sync(org())
        self.assertEqual(report["untracked"], [str(self.dir / "org" / "tech.md")])
        self.assertEqual(self.read("tech.md"), "# My hand-made chart\n")

    def test_a_sharp_shrink_is_not_written(self):
        big = org() + [user(f"d{i}", f"Dev {i:02}", "Developer", "Technology", "em") for i in range(20)]
        self.sync(big)
        report = self.sync(org(), day=DAY2)
        self.assertTrue(any("shrank from 25 to 5" in f for f in report["failed"]))
        self.assertIn("Dev 19", self.read("tech.md"))
        self.assertEqual([c for c in report["changes"] if c["chart"] == "tech"], [])

    def test_a_missing_root_fails_that_chart_only(self):
        users = [u for u in org() if u["id"] != "ceo"]
        users[0]["manager"] = None
        report = self.sync(users)
        self.assertEqual(report["failed"], ["top: no enabled account for ceo@example.com"])
        self.assertTrue((self.dir / "org" / "tech.md").exists())

    def test_a_dry_run_writes_nothing(self):
        report = self.sync(org(), dry_run=True)
        self.assertEqual(len(report["written"]), 2)
        self.assertFalse((self.dir / "org").exists())
        self.assertFalse((self.dir / "state.json").exists())

    def test_who_reads_the_last_sync(self):
        from org_directory.cli import main
        self.sync(org())
        import contextlib, io
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            main(["--config", str(self.dir / "config.toml"), "who", "bob"])
        self.assertIn("Bob Builder: Solutions Architect, Technology", out.getvalue())
        self.assertIn("reports to: Ann Architect (Principal Architect) > Tom Tech", out.getvalue())
        state = json.loads((self.dir / "state.json").read_text(encoding="utf-8"))
        self.assertNotIn("guest", state["people"])


if __name__ == "__main__":
    unittest.main()
