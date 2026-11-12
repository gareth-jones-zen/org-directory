"""People First (MHR's HR system), read as you: the phone numbers your role lets you see.

Optional (`[people_first] enabled = true`), and needs Edge. **The sign-in and the API client are
people-first-client's (`people_first`)** since 0.2.2 (12 Nov 2026); until then they were here. This
module keeps the names other tools imported from it (Settings, Api, PeopleFirstError, SignInNeeded,
_launch, _sign_in, sign_in_window), so they work unchanged. What's org-directory's own:

- **`/hrm/people/{id}/contactinfo` holds the numbers**: work phone and mobile, personal phone and
  mobile, work and personal email. **For anyone outside your own reporting line the personal fields
  come back empty**, so it reads your own team only, down your reporting line.
- **Only phone numbers are taken**, matched to the directory by work email. Personal email, addresses
  and next of kin are left alone.
- **It holds the Edge profile's lock** (people_first.profile_lock) while Edge is open, so another tool
  on the same profile (team-leave, cover-rota) waits rather than fails.
"""

from __future__ import annotations

import dataclasses

import people_first
from people_first.client import (ACCOUNT_BUTTON, MAX_PEOPLE, Api, PeopleFirstError, SignInNeeded,  # noqa: F401
                                 _find_key, _launch, _sign_in, _token_watcher)

SIGN_IN_HINT = "At a console, run: org-directory people-first sign-in"


@dataclasses.dataclass
class Settings(people_first.Settings):
    enabled: bool = False
    sign_in_hint: str = SIGN_IN_HINT


def fetch(settings: Settings, log=print, headless: bool = True) -> dict[str, dict]:
    """Phone numbers for you and everyone down your reporting line, by work email (lower case):
    {"companyMobile": ..., "personalMobile": ..., "personalNumber": ...}, empty fields left out."""
    with people_first.session(settings, log=log, headless=headless) as api:
        people = people_first.reporting_line(api)
        found = {}
        for person in people:
            info = api.contact(person)
            email = (info.get("workEmail") or "").strip().lower()
            numbers = {k: info[k].strip() for k in ("companyMobile", "personalMobile", "personalNumber")
                       if isinstance(info.get(k), str) and info[k].strip()}
            if email and numbers:
                found[email] = numbers
        log(f"people first: read {len(people)} people in your reporting line, {len(found)} with a number")
        return found


def sign_in_window(settings: Settings, log=print, minutes: float = 5) -> None:
    """A visible window, for a first sign-in or one that needs a password or MFA."""
    people_first.sign_in_window(settings, log=log, minutes=minutes)
