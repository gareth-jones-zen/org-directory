# org-directory

Writes org charts from your Microsoft 365 directory, as markdown, and keeps them up to date.
A chart is someone and everyone under them: a department, a division, or the whole organisation.

- **It writes what the directory holds**: names, job titles, departments and who reports to whom. One query fetches everyone, so the whole organisation takes seconds.
- **It says what changed.** Each sync compares the directory with the last one and reports people who joined, left, or got a new title, department, manager or name. It also tells someone moving within the organisation apart from someone arriving or leaving.
- **It only writes when something changed**, so a daily sync of a quiet directory writes nothing.
- **It never overwrites a file it didn't write.** Keep your own notes about people in another file.
- **Phone numbers are never written into a chart.** A chart is for who's who. They go only into the emergency contact sheets, below, in a folder you choose.
- **It can also keep emergency contact sheets**: your team's and others' phone numbers, refreshed daily, for when Teams and Outlook are down. People First, MHR's HR system, can add the personal mobiles your role lets you see.

## Install

Windows (x64) only; nothing else is needed (People First, if you switch it on, also needs Microsoft Edge, which Windows has).

1. Download `org-directory-<version>-win-x64.zip` from [Releases](https://github.com/gareth-jones-zen/org-directory/releases).
2. Unzip it to a folder that will stay put, such as `%LOCALAPPDATA%\Programs\org-directory`. The scheduled task (below) runs the program from there.
3. Run the commands below from that folder, or add it to your `PATH`.

It holds two programs: `org-directory.exe`, for a console, and `org-directory-background.exe`, the same program with no window, which the daily task runs.

## Getting started

```
org-directory config       # create the settings file and say where it is
org-directory sign-in      # once: a browser opens to sign in to Microsoft
```

Then add a `[[chart]]` to the settings file:

```toml
login_hint = "you@example.com"
changes_file = "C:/Users/you/Documents/Org/changes.md"   # optional: a running log of changes

[[chart]]
name = "technology"
root = "head.of.technology@example.com"   # the person at the top, by email or sign-in name
output = "C:/Users/you/Documents/Org/technology.md"   # or relative to the settings file
heading = "Technology"                    # optional: the file's title (default: the person's name)
depth = 0                                 # optional: how many levels down (0: everyone)
changes_depth = 0                         # optional: report changes only this far down (0: everyone)
```

```
org-directory sync --dry-run   # what it would write, and what changed
org-directory sync             # write the charts that changed
org-directory who "Sam"        # someone's title, department, managers and reports, from the last sync
```

`sync --json` prints the report, changes included.

**A chart looks like this:**

```markdown
- **Tom Tech**, Technology Director (Technology) · 2 reports, 4 in all
  - **Ann Architect**, Principal Architect · 2 reports
    - **Bob Builder**, Solutions Architect
    - **Dee Design**, Solutions Architect (Architecture)
```

A department is shown where it differs from the person's manager's, which keeps a big chart readable.

**Changes look like this**, in the report and at the top of `changes_file`:

```markdown
## 2026-11-09: Technology

- New in the directory: **Nia New**, Developer (Technology), reporting to Eve Manager.
- New title: **Bob Builder**, Solutions Architect → Senior Solutions Architect.
- Moved out of this chart: now **Fay Finance**, Finance Director (Finance), reporting to Cara Chief.
```

**For a whole organisation, set `changes_depth`.** At `1`, only changes among the top person's direct reports are reported, while the chart still shows everyone.

## Emergency contact sheets

If Teams and Outlook are down, so is the directory. So a sync can also write contact sheets, while everything is working, to somewhere you can still reach:

```toml
[contacts]
folder = "%OneDriveCommercial%/Emergency contacts"   # never a git repo: they hold personal numbers

[[contacts.group]]
name = "My team"
root = "you@example.com"      # someone and everyone under them...
depth = 1                     # ...this many levels down (0: everyone)

[[contacts.group]]
name = "Peers"
people = ["peer.one@example.com", "peer.two@example.com"]   # ...or a list, by email
```

Each sync writes three files to `folder`, so there are three ways to reach them:
- **`contacts.html`**: one page, readable on a phone and printable, with numbers you can tap to call;
- **`contacts.csv`**: for a spreadsheet;
- **`contacts.vcf`**: to import into a phone's contacts.

Each file says when the directory, and People First if used, were last read. **The sheets are never passed to the after-run hook,** so a hook that commits what's written can't commit them.

**The directory rarely has mobile numbers.** At one organisation, 2 people in 160 had one. Desk numbers are usually there, but they may ring through Teams.

### Adding numbers from People First

If your organisation uses People First (MHR), it can add the phone numbers you can see there:

```toml
[people_first]
enabled = true                                   # the switch: false, or no section, uses the directory alone
url = "https://yourorg.people-first.com"
# tenant_code = "yourorg"                        # optional: the first part of url
# account = "you@example.com"                    # optional: the account to pick at sign-in (default login_hint)
```

```
org-directory people-first check                 # what it lets you see: counts only, no numbers
org-directory people-first sign-in               # only if check says it needs a sign-in
```

**How it works:**
- **People First signs in through your organisation's Microsoft sign-in, and its API accepts that sign-in.** It reads as you, with your permissions. No HR administrator is involved, and it sees nothing you couldn't see in the app. MHR's API docs call this the "OpenID Connect ID token" route.
- **That sign-in can only reach People First's own web address, so it needs a browser.** It drives Edge, hidden, with its own profile. If your organisation's single sign-on goes straight through, no window is needed; otherwise run `people-first sign-in` once.
- **It reads you and everyone down your reporting line**, because People First withholds personal numbers for anyone else. It matches them to the directory by work email.
- **It takes phone numbers only:** personal mobile and phone, and work mobile. Personal email, addresses and next of kin are left alone.
- **If a read fails, the sheets keep the last good numbers** and say when they were read. A scheduled run notifies once they're three days old.
- The last good numbers are kept in the state file, which stays on your machine.
- **The sign-in is shared with other tools that read People First** through the same Edge profile. **While Edge is open it holds a lock named after the profile**, so a tool using the same profile (and so the same sign-in) waits its turn rather than failing to open Edge.

## Keeping it current: `autostart`

```
org-directory autostart on       # sync daily at daily_at (default 09:00) and at logon, and now
org-directory autostart status
org-directory autostart off
```

The directory changes slowly, so there's no watcher: a Task Scheduler task, `org-directory sync`, runs `sync --hook --scheduled` once a day and at logon.
- It runs as you, only while you're logged on, with no admin rights.
- If the laptop was asleep at the time, it catches up when it wakes.
- It runs `org-directory-background.exe`, so there's no window.
- **It never opens a browser.** A lapsed sign-in, or a chart that couldn't be written, is notified. Run `org-directory sign-in` at a console to fix a sign-in.
- It logs to `%LOCALAPPDATA%\org-directory\org-directory.log` by default (set `log_file`).

## Safety checks

- **Only enabled member accounts count.** Guest accounts and disabled accounts are left out. A leaver's account often keeps its manager after it's disabled, and would otherwise still show.
- **A chart that shrinks sharply isn't written:** by more than a quarter, and by at least ten people. A partial answer from Graph would otherwise read as a wave of leavers. If the shrink is real, delete the chart's entry from the state file and sync again.
- **A chart whose top person can't be found isn't written.** The other charts still are.
- **One sync at a time:** a sync started by hand waits for the daily one.

## The after-run hook

To do something with what's written, such as commit it to git, set `after_run`:

```toml
after_run = ["powershell", "-NoProfile", "-File", "commit.ps1"]   # any command, and its arguments
```

It runs after a sync that wrote a file, with `--hook` (the daily task always passes it). It runs from the settings file's folder, with no window, and gets a JSON report on stdin:

```json
{"config": "C:\\…\\config.toml", "written": ["C:\\…\\technology.md", "C:\\…\\changes.md"],
 "failed": [], "unmatched": [], "summary": "2 written, 1 change(s), 1 unchanged, 1146 people in the directory",
 "changes": [{"chart": "technology", "text": "New title: **Bob Builder**, Solutions Architect → Senior Solutions Architect."}]}
```

It's the same shape as [doc-filer](https://github.com/gareth-jones-zen/doc-filer)'s and
[teams-channel-notes](https://github.com/gareth-jones-zen/teams-channel-notes)' hooks, plus `changes`, so one script can serve all three. Its output goes to the log.

## Sign-in and permissions

It signs in as Microsoft's "Graph Command Line Tools" app, so there's no app to register.

- **Reading the directory needs `User.Read.All`.** That usually needs an admin's approval: request it on the "Approval required" screen at sign-in. Some organisations have already granted it for this app.

**Settings:**
- `msal_cache` points at another tool's token cache, to share its sign-in.
- `tenant` pins sign-in to one organisation.
- `state_file` sets where the last snapshot is kept (default `%LOCALAPPDATA%\org-directory\`). It holds every member's name, title, department, manager and email address, and People First's numbers if used, so it stays on your machine.

## What it can't do

- **It only knows what HR or IT put in the directory.** A title changed in the HR system appears only once it reaches Microsoft 365, and a job title is often missing or out of date.
- **Teams and squads aren't in the directory**, only reporting lines and departments.

## Building

It's .NET 11, published as Native AOT. It needs [background-tools-lib](https://github.com/gareth-jones-zen/background-tools-lib)
checked out beside this repository (or set the `BackgroundToolsLib` property to a checkout elsewhere), then:

```
dotnet test -c Release
dotnet publish src/OrgDirectory.Console -c Release -r win-x64 -o out
dotnet publish src/OrgDirectory.Background -c Release -r win-x64 -o out
```

A tag `v*` builds and publishes a release.
