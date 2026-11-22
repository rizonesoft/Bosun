---
schema_version: 1
id: packaging
domain: 07-release
status: active
depends_on: ["core-contract", "brand-and-theme", "application-shell", "premium-components", "memory-page", "instances-queue", "optimise-page"]
title: "TODO-01 -- Packaging and budgets"
---

# TODO-01 -- Packaging and budgets

> **Goal:** The panel installs per user with one command, can be removed cleanly, meets its memory budget and is documented.

> **Current state:** Measured 2026-11-07. `scripts/install.ps1` installs the C++ tools and logger, with no panel. `README.md` covers the queue only.

## Inputs

- `design/SPEC.md` §16.
- `R:\src\Steward\publish.sh`, `R:\src\Steward\scripts\signing-status.ps1`.
- -> XREF: D00 T02 §2 -- consumed by this file
- -> XREF: D02 T02 §4 -- consumed by this file
- -> XREF: D05 T02 §3 -- consumed by this file
- -> XREF: D99 T01 §3 -- consumes this file
- -> XREF: D02 T03 §2 -- consumed by this file
- -> XREF: D02 T03 §8 -- consumed by this file
- -> XREF: D02 T03 §9 -- consumed by this file
- -> XREF: D00 T02 §3 -- consumed by this file
- -> XREF: D03 T01 §5 -- consumed by this file

## Outcome

- A clean install starts the panel from the Start menu with no administrator rights.
- The idle budget is measured and enforced.

**Adjacency:** list=not-applicable (no records); document=not-applicable (none); settings=not-applicable (none here); reporting=not-applicable (none); notifications=not-applicable (none); permissions=not-applicable (per-user install); audit=not-applicable (none); exchange=not-applicable (none); reverse=applicable @ D07 T01 §3

**Adjacency rationale:** Uninstall is the reverse of install, and leaves logs unless asked.

## Implementation Order

| Order | Section | Deliverable | Depends On | Status |
| ----- | ------- | ----------- | ---------- | ------ |
| 1 | §1 | Publish and install | D00 T02 §3 | [ ] |
| 2 | §2 | Signing status | §1 | [ ] |
| 3 | §3 | Uninstall | §1 | [ ] |
| 4 | §4 | Keyboard and accessibility audit | §1 | [ ] |
| 5 | §5 | Performance budget | §1 | [ ] |
| 6 | §6 | Documentation | §4, §5 | [ ] |
| 7 | §7 | Clean-system installer (Inno Setup) | §1, §3 | [ ] |
| 8 | §8 | Installed tools older than the panel, said with the fix | §1 | [ ] |

## 1. Publish and install

One command gives a self-contained panel with the existing tools.

**Phase:** 7
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §16.
**Job:** know what's installed.
**Treatment:** a simple About page. Cheaper substitute that fails: a version hidden in file properties.
**Chrome:** the shell's page frame.

**Build order:**

1. Create `scripts/publish-panel.ps1`. Done when: `dotnet publish` gives a self-contained win-x64 folder at `bin/panel`.
2. Extend `scripts/install.ps1`. Done when: the panel, shortcut and optional tray start are set.

- [ ] Publish `src/Bosun.Panel` self-contained to `bin/panel`, through `bosun run`, with intermediate output staying under `build/`. Done when: it runs on a machine with no .NET runtime in the path, checked by clearing `DOTNET_ROOT`. Cheaper substitute: requiring a preview runtime.
- [ ] Extend `tests/bin_output_test.ps1` to allow `bin/panel` alongside the two tools, still rejecting test binaries and PDBs at the top of `bin/`. Done when: the test passes after a publish and fails with `test_core.exe` copied into `bin/`.
- [ ] Install from `bin/panel` to `%LOCALAPPDATA%\Bosun\panel`, stopping a running copy first. Done when: `tests/install_test.ps1` reinstalls over a running copy in a temporary `LOCALAPPDATA`, leaving the real install alone. Cheaper substitute: a failed copy behind a locked file.
- [ ] Create a Start menu shortcut, with no desktop icon. Done when: the shortcut opens the panel with the right icon.
- [ ] Add the About page per `design/SPEC.md` §16. Done when: version and commit come from build properties.
- [ ] Create `tests/install_test.ps1`, installing to a temporary `LOCALAPPDATA`. Done when: it fails with the panel missing, and leaves the real install alone.
- [ ] Add .NET runtime, log folders, Open crash folder and licence text to the About page, in `Views/AboutView.xaml`. Done when: a test reads each, and the licence matches `LICENSE`.
- [ ] Commit: `release: publish and install`

**Test checkpoint:** `pwsh tests/install_test.ps1` exits 0: installing to a temporary `LOCALAPPDATA` gives `Bosun.Panel.exe` whose `ProductVersion` holds the current commit.

## 2. Signing status

Unsigned builds work for a private tool, but SmartScreen warns on other machines. The owner chose to ship unsigned on 2026-11-07 and revisit it when the repo goes public (`D99 T01 §4`).

**Phase:** 7
**Needs:** none
**Surface:** no

**Build order:**

1. Port `scripts/signing-status.ps1`. Done when: it reports all binaries.
2. Record the decision. Done when: the script reports unsigned as the expected state.

- [ ] Port from `R:\src\Steward\scripts\signing-status.ps1`. Done when: every `.exe`, `.dll` and `.ps1` lists its state.
- [ ] Record unsigned as the decision in `scripts/signing-status.ps1`. Done when: `STATUS` output says unsigned by decision, and a signed file is reported as a mismatch. Cheaper substitute: claiming signed while unsigned.
- [ ] Commit: `release: signing status`

**Test checkpoint:** `pwsh scripts/signing-status.ps1` lists every shipped binary and exits per the recorded decision.

## 3. Uninstall

Whatever install adds must come off, with logs left unless asked.

**Phase:** 7
**Needs:** none
**Surface:** no

**Build order:**

1. Create `scripts/uninstall.ps1`. Done when: panel, shortcut, Run entry and scheduled tasks go.
2. Offer data removal. Done when: logs and settings stay unless `-RemoveData` is given.

- [ ] Remove binaries, `Bosun Logger` and panel Run entries, and PATH entry. Done when: a test in a temporary profile shows nothing left. Cheaper substitute: leaving a Run entry pointing at nothing.
- [ ] Remove the logs and `panel.json` only with `-RemoveData`, and say so. Done when: the default run leaves both, and reports it left them.
- [ ] Create `tests/uninstall_test.ps1`, installing to and removing from a temporary `LOCALAPPDATA`. Done when: the real install is untouched, and a leftover file fails the test.
- [ ] Commit: `release: uninstall`

**Test checkpoint:** `pwsh tests\uninstall_test.ps1` exits 0.

## 4. Keyboard and accessibility audit

Page-level tests prove each page. This walks them all, so a gap can't hide between sections.

**Phase:** 7
**Needs:** none
**Surface:** no

**Build order:**

1. Create `tests/Bosun.Panel.Tests/AccessibilityAudit.cs`. Done when: every view is walked.
2. Fix findings at the view. Done when: the audit passes.

- [ ] Assert names on icon-only controls, 32 px targets, tab order and no colour-only status, for all views. Done when: removing a name fails the audit. Cheaper substitute: an allowlist that grows.
- [ ] Check live regions on Activity and status changes. Done when: a status change announces, in a test.
- [ ] Commit: `release: accessibility audit`

**Test checkpoint:** `bosun run -- dotnet test --solution Bosun.slnx --filter-class "*AccessibilityAudit*"` exits 0, and fails if the change is reverted (**corrected 2026-11-22, round 4 of `D00 T06 §4`:** written as VSTest arguments; until the tests exist it exits 8, no tests found).

## 5. Performance budget

A memory tool must be measurably cheap. Without a number that fails, it will grow.

**Phase:** 7
**Needs:** none
**Surface:** no

**Build order:**

1. Create `tests/perf/panel_budget.ps1`. Done when: it measures idle in the tray and on the Memory page.
2. Fix what it finds. Done when: under 150 MB and under 1% average CPU idle.

- [ ] Measure working set and CPU across 5 minutes in the published build. Done when: the script prints both, and exits nonzero over budget. Cheaper substitute: measuring a Debug build.
- [ ] Add to `scripts/check-all.ps1` as an opt-in `-Perf` step and a release gate. Done when: a deliberate leak fixture fails it.
- [ ] Prove hidden pages stop polling. Done when: no child processes start for hidden pages over the window, except tray checks.
- [ ] Commit: `release: performance budget`

**Test checkpoint:** `pwsh tests/perf/panel_budget.ps1` exits 0, printing working set and CPU.

## 6. Documentation

README, troubleshooting and a clear note on what the panel will and won't do.

**Phase:** 7
**Needs:** none
**Surface:** no

**Build order:**

1. Extend `README.md`. Done when: install and each page are covered.
2. Add `docs/troubleshooting.md`. Done when: each failed state has an entry.

- [ ] Add the panel to `README.md`, in question-shaped headings. Done when: every command shown was run. Cheaper substitute: documenting a command from memory.
- [ ] Create `scripts/check-doc-commands.py`, checking each command in `README.md` against the tools and scripts on disk. Done when: a fixture naming a missing script fails it.
- [ ] Write `docs/troubleshooting.md` covering a stale snapshot, WSL not running, a missing tool and an unsupported schema version. Done when: each page's failed state links to its entry.
- [ ] Update `docs/specs/2026-11-07-control-panel-design.md` with what changed in the build. Done when: the defaults table matches what shipped.
- [ ] Commit: `docs: panel readme and troubleshooting`

**Test checkpoint:** `python scripts/check-doc-commands.py` exits 0, verifying each documented command exists.

## 7. Clean-system installer (Inno Setup)

The owner asked on 2026-11-08 that everything set up by hand during development can be reproduced on a clean Windows machine, by script or one installer built with the latest stable Inno Setup. Today that state is spread over `scripts/install.ps1`, `scripts/add-cmake-to-path.ps1`, `scripts/campaign-hold.ps1`, `scripts/campaign-admin-setup.ps1` (both from `D00 T03 §10`) and the WSL side, plus tools installed by hand.

**Phase:** 7
**Needs:** none
**Surface:** no

**Build order:**

1. Write `installer/bosun.iss`. Done when: `iscc` builds one setup that installs what `scripts/install.ps1` does.
2. Prove it on a clean machine. Done when: Windows Sandbox goes from nothing to a passing `pwsh scripts/campaign-preflight.ps1 -Only bosun,cmake,denylist,dotnet,gh,codex,wsl`.

- [ ] List every piece of machine state the repo depends on in `docs/install-inventory.md` (tools, PATH entries, tasks, registry values, WSL packages, sign-ins), each with the script that sets it. Done when: `scripts/check-install-inventory.py` fails on a fixture naming a script that doesn't exist or a task no script registers. Cheaper substitute: a README list nobody checks.
- [ ] Add `scripts/bootstrap.ps1`, installing the prerequisites the inventory names through `winget` (Git, Python, PowerShell 7, GitHub CLI, the .NET SDK, Visual Studio Build Tools with CMake, Node for Codex), each skipped when present, then running `scripts/install.ps1`. Done when: a run on a machine that has everything changes nothing and says so. Cheaper substitute: telling the user what to install.
- [ ] Create `installer/bosun.iss` for the latest stable Inno Setup 6, per user with no administrator rights, installing the tools, panel and logger to `%LOCALAPPDATA%\Bosun`, the Start menu shortcut and PATH entry, with an optional elevated component that runs `scripts/campaign-admin-setup.ps1 -Apply`. Done when: `scripts/build-installer.ps1` finds `iscc` (or names the `winget install JRSoftware.InnoSetup` fix) and writes `bin/installer/Bosun-Setup-<version>.exe`. Cheaper substitute: a zip of `bin/`.
- [ ] Make the installer's uninstaller call `scripts/uninstall.ps1` and, when the elevated component was installed, `scripts/campaign-admin-setup.ps1 -Restore`. Done when: an install and uninstall round trip leaves no file, task, PATH entry or Run value behind, checked by the inventory.
- [ ] Prove a clean install in Windows Sandbox with `tests/clean_install_test.ps1`: bootstrap or the setup, then the preflight's tool checks. Done when: it passes from a clean sandbox and fails when a prerequisite is left out.
- [ ] Commit: `release: clean-system installer`

**Test checkpoint:** `pwsh tests/clean_install_test.ps1` exits 0 in Windows Sandbox, and `python scripts/check-install-inventory.py` exits 0.

## 8. Installed tools older than the panel, said with the fix

On 2026-11-19 the installed `bosun-logger.exe` (2026-11-12) predated the live snapshot it is meant to write, so `current-windows.json` stood at 2026-11-13 (left by a development run) and `current-wsl.json` was never written, while `bosun status` said the logger was running and the panel said only that the snapshot was old. Reinstalling (`scripts/install.ps1`) fixed it at once. Nothing said the installed tools were older than the panel, or how to fix it. Filed 2026-11-19 from what the `D05 T02 §5` build found.

**Phase:** 7
**Needs:** none
**Surface:** no

**Build order:**

1. The tools say which build they are. Done when: `bosun` and the logger name their build commit, and the snapshot carries it.
2. The panel says when they are older than it, with the fix. Done when: a stale or missing snapshot from a running old logger is said as an out-of-date install, with the command to run.

- [ ] Stamp the build commit into `bosun.exe` and `bosun-logger.exe` (`scripts/build.ps1` passes the commit to CMake, which defines it; `bosun --version` prints it) and write it into each live snapshot as `build`, the WSL logger writing its own from the installed files. Done when: `tests/test_core.cpp` and `tests/test_bosun_log.py` read `build` from a snapshot, and `bosun --version` prints the commit a build was made from. Cheaper substitute: the file's modified time.
- [ ] Tell an out-of-date install apart from a stopped logger in `MemoryClient` (`src/Bosun.Core/Memory/`): when the snapshot is missing or stale while the logger process runs, or its `build` is older than the panel's own (by the commit order the panel was built with, or a contract number both carry), the reading's reason says "The installed logger is older than this panel, so it does not write what the panel reads. Run scripts/install.ps1 to update it." Done when: `MemoryClientTests` cover a running logger with no snapshot, a snapshot from an older build, and a stopped logger, each with its own reason. Cheaper substitute: "the snapshot is old" for every cause.
- [ ] Commit: `install: out-of-date tools said with the fix`

**Test checkpoint:** `bosun run -- dotnet test --project tests/Bosun.Core.Tests --filter-class "*MemoryClientTests"` and `pwsh scripts/check-all.ps1 -Wsl -Only wsl:logger-tests` exit 0, and fail if the change is reverted.

## Verification

- `pwsh scripts/check-all.ps1 -Perf` passes on the published build.
- `python scripts/todo-graph.py query stats` shows every section outside phase 99 verified with a receipt, or parked and named in `docs/acceptance/`.
- A clean install and uninstall round-trip passes.
