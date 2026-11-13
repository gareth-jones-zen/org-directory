# Plan: org-directory ported to .NET 11 Native AOT

*13 Nov 2026, written alongside the port (branch `dotnet`; the Python is tagged `v0.2.2`). Phases 1 to 4 are done;
5 and 6 wait for GJ.*

## The goal

**Every file it writes must be byte for byte the Python version's**: both charts, the changes file, the three
contact sheets and the state file, and the same goes for the report (`sync --json`), the summary line, the log
lines, the hook's stdin and the exit codes. Same settings file, state file, log, task name (`org-directory sync`)
and run lock (`org-directory-run-<key>`, which cover-rota also takes), so either version can replace the other with
no data moved. People First goes through background-tools-lib's PeopleFirstClient (Edge over its DevTools pipe, no
Playwright), with the same profile lock as the Python tools.

## Names

| | Python | .NET |
|---|---|---|
| Command | `python -m org_directory` (pythonw for the task) | `org-directory.exe`; `org-directory-background.exe` (no window) for the task |
| Installed to | pip, editable from `~/source/repos/org-directory` | unzip to `%LOCALAPPDATA%\Programs\org-directory\` |
| Settings, state, log, task, locks | as set in `tools/org-directory.toml` | unchanged, the same files and names |

## Module map

| Python | .NET | Notes |
|---|---|---|
| `directory.py` | `Org.cs`, `Charts.cs` | `OrderedDictionary` for dict order; stable sorts by code point with Python's `str.lower()` |
| `contacts.py` | `Contacts.cs` | csv QUOTE_MINIMAL, `html.escape`, `str.isdigit` for tel: links, the vCard's CRLF; contacts.vcf is rewritten on every sync, as in Python |
| `peoplefirst.py` | `PeopleFirst.cs` | PeopleFirstClient's `Session`, `ReportingLine`, `ContactAsync` |
| `run.py` | `Sync.cs` | `json.dumps` via PyCompat's `PyJson`; frozen clock and fakes injectable |
| `config.py` | `Config.cs` | Tomlyn; paths through PyCompat's `PyPath` and `RealPath` (Python's `resolve()`) |
| `cli.py`, `autostart.py`, `locks.py` | `Cli.cs`, `Autostart.cs` | argparse's messages and exit codes; the task through WinBackground |
| (new) | `Py.cs`, `Interim/` | Python behaviours; `RealPath`, `ShellRun` and `ConsoleText` copied from teams-channel-notes until they move into the library |

## How parity was checked

1. **The 16 Python tests, by name**, plus 23 new ones (39; one is the local golden, skipped without it).
2. **The Python as an oracle** (`tests/parity/oracle.py`): org-directory's own `run()` with the clock frozen and the
   hook and People First faked. `scenarios.py` makes synthetic scenarios (several syncs in a row, the directory
   changing, files edited by hand, People First failing at different ages, awkward text); 60 are committed, and a
   1,500-scenario local run matched as well. `misc_goldens.py`: csv, tel:, escaping, lower and sorting, repr,
   settings errors and lock names.
3. **Live, read-only, on copies** (`tests/parity/live.py`, `tests/ParityRunner`): one directory GET recorded on a
   copy of the token cache; People First read by each version in turn on a copy of the Edge profile; both render
   into their own folders seeded with copies of the real charts and state; compared by diff counts and SHA-256.
   Every copy deleted afterwards.
4. **Native AOT**: both exes publish with no warnings; the AOT-compiled ParityRunner matched Python on 120 synthetic
   rounds.

## Phases

1. **Set-up (done):** fresh clone at `~/source/repos/org-directory-dotnet`, tag `v0.2.2`, branch `dotnet`.
2. **Oracle (done):** the scenario and misc goldens.
3. **Port (done):** module by module against the goldens; CI builds, tests and publishes both exes.
4. **Side-by-side checks (done, read-only):** above.
5. **Shadow (GJ):** a week of the .NET exe run by hand beside the Python task, `sync --dry-run --json` on its own
   copy of the settings pointing at copies of the outputs, comparing the reports each morning.
6. **Switch (GJ):**
   1. `! python -m org_directory --config tools/org-directory.toml autostart off`;
   2. unzip the release to `%LOCALAPPDATA%\Programs\org-directory\`;
   3. `! "%LOCALAPPDATA%\Programs\org-directory\org-directory.exe" --config tools/org-directory.toml autostart on`;
   4. one EA commit: CLAUDE.md's "Org charts" section and the toml's comments (`python -m org_directory` becomes
      the exe). cover-rota keeps working: it imports the Python package, and takes the same run lock;
   5. **rollback:** `org-directory autostart off`, then the Python `autostart on`. Nothing else moves.

## Decisions taken overnight (GJ's to overrule)

| # | Decision | Why |
|---|---|---|
| 1 | Fix two Python crashes rather than copy them (stale member list: KeyError; manager loop through a chart's top: RecursionError) | Each stopped every later sync; neither changes output when the crash wouldn't have happened |
| 2 | Keep contacts.vcf's rewrite on every sync | Parity: same bytes, so harmless; "contact sheets written" in every summary is Python's too |
| 3 | The run lock's key uses Python's lower-casing, not `Locks.Key` | `Locks.Key` differs for U+0130, which would let a .NET and a Python sync run together |
| 4 | Sibling-checkout references to background-tools-lib | As the other ports |
| 5 | x64 only; the Python stays on the branch until the switch | As the other ports |

## Risks

- **People First's sign-in** is the fragile part, as in Python; the .NET client stops at once when a person is
  needed (Python waited 3 minutes).
- **The state file holds personal mobiles:** every parity check used copies, compared by hash only.
- **Python drift:** the goldens record the Python version; regenerate them if org-directory's Python changes.
