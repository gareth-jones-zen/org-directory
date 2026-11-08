"""Run with: python -m unittest discover -s tests"""

import datetime as dt
import json
import pathlib
import sys
import tempfile
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / "src"))

from org_directory import config as cfg  # noqa: E402
from org_directory.run import sync  # noqa: E402
from test_tool import FakeGraph, org  # noqa: E402

SETTINGS = """
state_file = "state.json"
log_file = "log.txt"
[[chart]]
name = "tech"
root = "cto@example.com"
output = "org/tech.md"
[contacts]
folder = "private"
[[contacts.group]]
name = "My team"
root = "arch@example.com"
depth = 1
[[contacts.group]]
name = "Peers"
people = ["em@example.com", "nobody@example.com"]
"""
PEOPLE_FIRST = """
[people_first]
enabled = {enabled}
url = "https://example.people-first.com"
"""


def with_phones(users):
    for u in users:
        u["businessPhones"] = [f"0161 000 {u['id']}"]
        u["mobilePhone"] = "07700 900001" if u["id"] == "sa2" else None
    return users


class Contacts(unittest.TestCase):
    def setUp(self):
        self.dir = pathlib.Path(tempfile.mkdtemp()).resolve()

    def load(self, people_first=None):
        text = SETTINGS + (PEOPLE_FIRST.format(enabled=people_first) if people_first is not None else "")
        (self.dir / "config.toml").write_text(text, encoding="utf-8")
        return cfg.load(self.dir / "config.toml")

    def sync(self, config, read=None, users=None):
        logs = []
        report = sync(config, graph=FakeGraph(users or with_phones(org())), today=dt.date(2026, 11, 8),
                      log=logs.append, read_people_first=read)
        return report, logs

    def read(self, name):
        return (self.dir / "private" / name).read_text(encoding="utf-8")

    def test_settings_parse_and_people_first_defaults(self):
        config = self.load(people_first="true")
        self.assertEqual([g.name for g in config.contacts.groups], ["My team", "Peers"])
        self.assertEqual(config.contacts.folder, self.dir / "private")
        self.assertTrue(config.people_first.enabled)
        self.assertEqual(config.people_first.tenant_code, "example")
        self.assertFalse(self.load(people_first="false").people_first.enabled)
        self.assertFalse(self.load().people_first.enabled)

    def test_sheets_from_the_directory_alone(self):
        config = self.load()
        report, logs = self.sync(config, read=lambda *a, **k: self.fail("People First is switched off"))
        self.assertEqual(len(report["contacts"]), 3)
        self.assertNotIn(str(self.dir / "private" / "contacts.html"), report["written"])   # never to the hook
        page = self.read("contacts.html")
        self.assertIn("<h2>My team (3)</h2>", page)
        self.assertIn("Ann Architect", page)
        self.assertIn("href='tel:07700900001'>Work mobile: 07700 900001</a>", page)   # the directory's mobile
        self.assertIn("People First isn&#x27;t used.", page)
        self.assertIn("nobody@example.com isn't in the directory", " ".join(logs))
        rows = self.read("contacts.csv").splitlines()
        self.assertEqual(rows[0].split(",")[:3], ["Group", "Name", "Title"])
        self.assertEqual(len(rows), 1 + 3 + 1)
        cards = self.read("contacts.vcf")
        self.assertEqual(cards.count("BEGIN:VCARD"), 4)
        self.assertIn("TEL;TYPE=WORK,CELL:07700 900001", cards)
        # Phones never reach the charts or the snapshot.
        self.assertNotIn("0161", (self.dir / "org" / "tech.md").read_text(encoding="utf-8"))
        self.assertNotIn("0161", (self.dir / "state.json").read_text(encoding="utf-8"))

    def test_people_first_numbers_are_added_by_work_email(self):
        config = self.load(people_first="true")
        read = lambda settings, log=print: {"sa1@example.com": {"personalMobile": "07700 900123",
                                                                "personalNumber": "0161 496 0000"}}
        report, _ = self.sync(config, read=read)
        page = self.read("contacts.html")
        self.assertIn("Mobile: 07700 900123", page)
        self.assertIn("Home: 0161 496 0000", page)
        self.assertIn("People First read", page)
        self.assertIn("TEL;TYPE=CELL:07700 900123", self.read("contacts.vcf"))
        state = json.loads((self.dir / "state.json").read_text(encoding="utf-8"))
        self.assertEqual(state["people_first"]["numbers"]["sa1@example.com"]["personalMobile"], "07700 900123")

    def test_a_failed_people_first_read_keeps_the_last_numbers(self):
        config = self.load(people_first="true")
        self.sync(config, read=lambda settings, log=print: {"sa1@example.com": {"personalMobile": "07700 900123"}})

        def broken(settings, log=print):
            raise RuntimeError("People First answered 503")
        report, _ = self.sync(config, read=broken)
        self.assertEqual(report["people_first_failed"], "People First answered 503")
        self.assertFalse(report["people_first_stale"])
        page = self.read("contacts.html")
        self.assertIn("Mobile: 07700 900123", page)
        self.assertIn("(the latest read failed)", page)

    def test_a_first_read_that_fails_is_stale_at_once(self):
        config = self.load(people_first="true")

        def broken(settings, log=print):
            raise RuntimeError("no sign-in")
        report, _ = self.sync(config, read=broken)
        self.assertTrue(report["people_first_stale"])
        self.assertIn("personal numbers are missing", self.read("contacts.html"))

    def test_switching_people_first_off_drops_its_numbers(self):
        self.sync(self.load(people_first="true"),
                  read=lambda settings, log=print: {"sa1@example.com": {"personalMobile": "07700 900123"}})
        self.sync(self.load(people_first="false"))
        self.assertNotIn("07700 900123", self.read("contacts.html"))
        self.assertNotIn("people_first", json.loads((self.dir / "state.json").read_text(encoding="utf-8")))


if __name__ == "__main__":
    unittest.main()
