"""People First (MHR's HR system), read as you: the phone numbers your role lets you see.

Optional (`[people_first] enabled = true`), and needs Playwright and Edge. What it's built on, from
reading Zen's People First (8 Nov 2026):
- **People First signs in through the organisation's own Microsoft sign-in, and its API accepts that
  sign-in's ID token**, acting as you with your permissions: MHR's docs call it the "OpenID Connect ID
  token" route. No HR administrator is involved, and nothing is seen that you couldn't see in the app.
- **There's no way to get that token without a browser**, as only People First's own web address can
  receive it. So it drives Edge, hidden, with its own profile, and takes the token from the web app's
  own requests. The token lasts about an hour, longer than a run.
- **Every API call also needs a `TenantCode` header**, which the web app sends; without it People First
  answers "Could not resolve tenant environment".
- **`/hrm/people/{id}/contactinfo` holds the numbers**: work phone and mobile, personal phone and
  mobile, work and personal email. **For anyone outside your own reporting line the personal fields
  come back empty**, so it reads your own team only, down your reporting line.
- **Only phone numbers are taken**, matched to the directory by work email. Personal email, addresses
  and next of kin are left alone.
"""

from __future__ import annotations

import dataclasses
import pathlib
import time
import urllib.parse

ACCOUNT_BUTTON = "Organisation account"   # People First's sign-in button for the organisation's own sign-in
MAX_PEOPLE = 1000


class PeopleFirstError(RuntimeError):
    pass


class SignInNeeded(PeopleFirstError):
    pass


@dataclasses.dataclass
class Settings:
    enabled: bool = False
    url: str = ""
    tenant_code: str = ""
    account: str = ""
    edge_profile: pathlib.Path | None = None


def _token_watcher(store: dict, api_root: str):
    def on_request(request):
        auth = request.headers.get("authorization") or ""
        if request.url.startswith(api_root) and auth.lower().startswith("bearer "):
            store["authorization"] = auth
    return on_request


def _sign_in(ctx, page, settings: Settings, headless: bool, minutes: float, log) -> str:
    """Load People First and return the Authorization header its web app uses. Unattended, it gets
    through the organisation's single sign-on by picking the account; anything more (a password,
    MFA) raises SignInNeeded. In a window, it waits for the person to finish."""
    store: dict = {}
    ctx.on("request", _token_watcher(store, settings.url.rstrip("/") + "/api/v1/"))
    page.goto(settings.url, wait_until="domcontentloaded", timeout=90_000)
    clicked, picked, deadline = False, False, time.time() + minutes * 60
    while time.time() < deadline:
        # Wait inside Playwright, never time.sleep: the sync API only handles browser events during a call.
        page.wait_for_timeout(2_000)
        if store.get("authorization"):
            return store["authorization"]
        if not clicked and "Login" in page.title():
            button = page.get_by_text(ACCOUNT_BUTTON, exact=True)
            if button.count():
                button.first.click()
                clicked = True
                log("people first: signing in through the organisation account")
            continue
        if clicked and not picked:
            for other in ctx.pages:
                if "login.microsoftonline.com" not in other.url:
                    continue
                tile = other.get_by_text(settings.account, exact=False) if settings.account else None
                if tile is not None and tile.count():
                    tile.first.click()
                    picked = True
                elif headless and other.locator("input[type=password]").count():
                    raise SignInNeeded("People First's sign-in wants a password. At a console, run: "
                                       "org-directory people-first sign-in")
    raise SignInNeeded(f"People First didn't finish signing in within {minutes:g} minutes. At a console, "
                       "run: org-directory people-first sign-in")


class Api:
    def __init__(self, ctx, settings: Settings, authorization: str):
        self.ctx, self.root = ctx, settings.url.rstrip("/") + "/api/v1"
        self.headers = {"Authorization": authorization, "TenantCode": settings.tenant_code,
                        "EnvironmentCode": "", "Accept": "application/json"}

    def get(self, path: str) -> dict:
        response = self.ctx.request.get(self.root + path, headers=self.headers, timeout=60_000)
        if not response.ok:
            raise PeopleFirstError(f"People First answered {response.status} to {path.split('?')[0]}")
        return response.json()

    def me(self) -> str:
        """Your own person id. The profile endpoint has it, under one name or another."""
        for path in ("/profile", "/hrm/app/profile", "/iam/profile"):
            try:
                found = _find_key(self.get(path), ("personId", "personID", "PersonId"))
            except PeopleFirstError:
                continue
            if found:
                return found
        raise PeopleFirstError("Couldn't find your own person id in People First's profile.")

    def team(self, person: str) -> list[str]:
        out, offset = [], 0
        while True:
            query = urllib.parse.urlencode({"page[Limit]": 100, "page[Offset]": offset})
            body = self.get(f"/hrm/people/{person}/team/people?{query}")
            items = (body.get("data") or {}).get("team") or []
            out += [item["personId"] for item in items if item.get("personId")]
            offset += len(items)
            if not items or offset >= int((body.get("meta") or {}).get("count") or 0):
                return out

    def contact(self, person: str) -> dict:
        return (self.get(f"/hrm/people/{person}/contactinfo").get("data") or {}).get("personcontacts") or {}


def _find_key(value, names):
    if isinstance(value, dict):
        for key, item in value.items():
            if key in names and isinstance(item, str) and item:
                return item
        for item in value.values():
            found = _find_key(item, names)
            if found:
                return found
    elif isinstance(value, list):
        for item in value:
            found = _find_key(item, names)
            if found:
                return found
    return None


def _launch(p, settings: Settings, headless: bool):
    settings.edge_profile.mkdir(parents=True, exist_ok=True)
    try:
        return p.chromium.launch_persistent_context(str(settings.edge_profile), channel="msedge", headless=headless)
    except Exception as error:
        raise PeopleFirstError(f"Couldn't start Edge for People First (is another run using its profile?): "
                               f"{str(error).splitlines()[0][:200]}") from error


def fetch(settings: Settings, log=print, headless: bool = True) -> dict[str, dict]:
    """Phone numbers for you and everyone down your reporting line, by work email (lower case):
    {"companyMobile": ..., "personalMobile": ..., "personalNumber": ...}, empty fields left out."""
    from playwright.sync_api import sync_playwright   # optional dependency
    with sync_playwright() as p:
        ctx = _launch(p, settings, headless)
        try:
            page = ctx.pages[0] if ctx.pages else ctx.new_page()
            api = Api(ctx, settings, _sign_in(ctx, page, settings, headless, 3, log))
            me = api.me()
            people, queue = [], [me]
            while queue and len(people) < MAX_PEOPLE:
                person = queue.pop(0)
                if person in people:
                    continue
                people.append(person)
                queue += api.team(person)
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
        finally:
            ctx.close()


def sign_in_window(settings: Settings, log=print, minutes: float = 5) -> None:
    """A visible window, for a first sign-in or one that needs a password or MFA."""
    from playwright.sync_api import sync_playwright
    with sync_playwright() as p:
        ctx = _launch(p, settings, headless=False)
        try:
            page = ctx.pages[0] if ctx.pages else ctx.new_page()
            _sign_in(ctx, page, settings, headless=False, minutes=minutes, log=log)
        finally:
            ctx.close()
