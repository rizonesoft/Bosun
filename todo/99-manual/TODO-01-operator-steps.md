---
schema_version: 1
id: operator-steps
domain: 99-manual
status: active
title: "TODO-01 -- Operator steps"
---

# TODO-01 -- Operator steps

> **Goal:** The steps only the operator can take are recorded and closed, each with its evidence.

> **Current state:** Measured 2026-11-07. RAM ran at 2133 MHz on a kit rated 3600 by part number. Logging began on 2026-11-07, so 7 days are available from 2026-11-14.

## Inputs

- `docs/specs/2026-11-07-bosun-design.md`.
- `design/SPEC.md`.
- -> XREF: D07 T01 §6 -- consumed by this file
- -> XREF: D05 T02 §3 -- consumed by this file
- -> XREF: D00 T03 §19 -- consumed by this file
- -> XREF: D00 T05 §8 -- does the scrub and leak scan the open checks in §4 name

## Outcome

- The BIOS and hardware decisions are recorded with their before and after.
- The operator has used the panel for real and any gaps are filed.

**Adjacency:** all=not-applicable (operator actions, no records)

**Adjacency rationale:** These are actions, not features.

## Implementation Order

| Order | Section | Deliverable | Depends On | Status |
| ----- | ------- | ----------- | ---------- | ------ |
| 1 | §1 | Enable XMP in the BIOS | -- | [ ] |
| 2 | §2 | Decide on 64 GB of RAM | -- | [ ] |
| 3 | §3 | First attended walkthrough | D07 T01 §6 | [ ] |
| 4 | §4 | Decide whether to make the repo public | -- | [ ] |
| 5 | §5 | Acceptance: real machine changes | D05 T02 §3 | [ ] |
| 6 | §6 | First Carbon campaign run | D00 T03 §19 | [ ] |

## 1. Enable XMP in the BIOS

This is a free speedup that software can't do. It's a BIOS step, so it's the operator's alone.

**Phase:** 99
**Needs:** operator,attended
**Surface:** no

**Build order:**

1. Enable XMP in the BIOS. Done when: the machine boots stably.
2. Record the result. Done when: the Optimise page reads the rated speed.

- [ ] Enable the XMP profile, and keep the old settings noted for undo. Done when: the machine passes a day of normal work. Cheaper substitute: overclocking past the rated profile.
- [ ] Record before and after in this section. Done when: `Win32_PhysicalMemory` shows the new speed.
- [ ] Commit: `manual: XMP enabled`

**Test checkpoint:** `Get-CimInstance Win32_PhysicalMemory | Select-Object ConfiguredClockSpeed` prints the rated speed.

## 2. Decide on 64 GB of RAM

The decision set aside for a week of logs. The panel's Decision card makes the evidence one click away.

**Phase:** 99
**Needs:** operator,owner-decision
**Surface:** no

**Build order:**

1. Read the Decision card, or `bosun report --days 7`. Done when: at least 7 days are logged.
2. Record the decision. Done when: the reason is written here.

- [ ] Read `bosun report --days 7` from 2026-11-14 onward. Done when: the peaks and overlaps are quoted here.
- [ ] Record buy or don't buy, with the reason. Done when: the note is committed. Cheaper substitute: deciding without the log numbers.
- [ ] Commit: `manual: 64 GB decision recorded`

**Test checkpoint:** `bosun report --days 7` prints at least 7 days of data.

## 3. First attended walkthrough

Unattended checks prove the parts. Only a person can say the whole thing is right. Gaps found become sections.

**Phase:** 99
**Needs:** operator,attended
**Surface:** no

**Build order:**

1. Install and use every page for a real day. Done when: each page has been seen with real data.
2. File what's wrong. Done when: gaps are sections or `todo/findings.md` entries.

- [ ] Run `pwsh scripts/install.ps1` for the real install, then walk each rail page in `design/SPEC.md` on real work. Done when: every page has been used. Cheaper substitute: a checklist ticked without using it.
- [ ] Commit: `manual: first attended walkthrough`

**Test checkpoint:** `(Get-Item $env:LOCALAPPDATA\Bosun\panel\Bosun.Panel.exe).VersionInfo.ProductVersion` holds the installed commit, and the walkthrough note is committed.

## 4. Decide whether to make the repo public

`rizonesoft/Bosun` was private (as `rizonetech/Bosun`), with the README, licence and community files of a public repo. Publishing cannot be fully undone, because forks and caches keep what was seen, so these checks come first. Staying private is a valid outcome: record it, and the publishing steps below are marked not applicable.

**Phase:** 99
**Needs:** owner-decision,operator
**Surface:** no

**Build order:**

1. Clean what should not be public. Done when: each check below passes.
2. Record the decision, and change the visibility only if it is to publish. Done when: the note is committed, and `gh repo view --json visibility` matches it.

- [x] Record publish or stay private in this section. `GPL-3.0-or-later` in `LICENSE` was confirmed by the owner on 2026-11-07. Done when: the decision is committed. Decided 2026-11-20 (owner): published. The repo went public with its full history and moved to `rizonesoft/Bosun` the same day. Development happens on the public repo, and a small private overlay repo holds the owner's machine and account specifics. The checks below that were still open run as part of that extraction.
- [ ] Revisit code signing in `scripts/signing-status.ps1` if publishing. Done when: the decision is recorded.
- [x] Move the alert recipient in `.conclave/automations/alert.py` out of the source into ignored local config. Done when: `git grep -n "@gmail" -- .conclave` prints nothing, including `.conclave/automations/README.md`. Resolved 2026-11-12: the whole of `.conclave/automations/` was removed with `D00 T01 §5` and `§9` (owner, 2026-11-12), and `git grep -n "@gmail" -- .conclave` prints nothing.
- [x] Replace personal absolute paths in `todo/`, `AGENTS.md` and `docs/specs/` with repo names, keeping a stamped section's text as history. Done when: `pwsh scripts/machine-path-check.ps1` exits 0 (every tracked file outside `docs/reviews/`, `docs/phase-runs/` and stamped sections). **Corrected 2026-11-21:** the criterion was `git grep -lE "[A-Z]:\\\\GitHub" -- AGENTS.md docs`, which also searched preserved history under `docs/`. Done 2026-11-21 by `D00 T05 §8` (`62613c1`): 37 live files rewritten, the check clean over 797 tracked files, and `scripts/hooks/pre-push` running it so a new one is refused.
- [x] Review the sibling repo names in `design/mockups/control-panel.html` and `docs/images/`. Done when: private client and project names are replaced with samples. Done 2026-11-21 by `D00 T05 §8`: the mockup's machine paths were rewritten and it holds no deny-list entry; `docs/images/panel-overview.png`, the README's only image, was looked at and shows no machine path or profile name, and its sibling project names are already public in this repository, so it was kept.
- [x] Install `gitleaks` per user, then scan the full history with `gitleaks detect`. Done when: it reports no leaks. Done 2026-11-21 by `D00 T05 §8`: `gitleaks` 8.30.1 is installed per user, and `pwsh scripts/leak-scan.ps1 -All` (gitleaks over every ref, tag and published file) in a fresh clone of the public repository with no overlay checked 1,275 commits with no finding; the `secrets` job in `.github/workflows/plan.yml` repeats it on every push to main.
- [x] Enable private vulnerability reporting and secret scanning with push protection on `rizonesoft/Bosun`. Done when: `gh api repos/rizonesoft/Bosun/private-vulnerability-reporting` says enabled. Done 2026-11-20: reporting enabled, secret scanning and push protection enabled, 0 open alerts.
- [x] Remove the private-repository paragraph from `SECURITY.md`. Done when: its only reporting route is private vulnerability reporting.
- [x] Run `gh repo edit rizonetech/Bosun --visibility public --accept-visibility-change-consequences`. Done when: the README renders with its badges for a signed-out visitor. Cheaper substitute: publishing before the checks above pass. Done 2026-11-20 by the owner, before the open checks above, and transferred to `rizonesoft/Bosun`.
- [ ] Commit: `manual: publish the repo`

**Test checkpoint:** `gh repo view rizonesoft/Bosun --json visibility` matches the decision recorded in this section.

## 5. Acceptance: real machine changes

The run proves these against disposable targets, as the owner chose. This is the one short pass where they touch the real machine, with the owner watching.

**Phase:** 99
**Needs:** operator,attended
**Surface:** no

**Build order:**

1. Round-trip one real machine item. Done when: preview, apply, verify and undo pass, with a real UAC prompt.
2. Undo the campaign's machine setup. Done when: the hold and the admin setup read back as they were.

- [ ] Accept a real UAC prompt, then refuse a second, through `IElevation` on one named Optimise item. Done when: the first applies, and the refusal leaves no change. Cheaper substitute: only the faked seam.
- [ ] Round-trip one real startup entry on the Optimise page, named before starting. Done when: undo restores the exact value.
- [ ] Run `pwsh scripts/campaign-hold.ps1 -Restore` and `pwsh scripts/campaign-admin-setup.ps1 -Restore`. Done when: settings read back as they were.
- [ ] Commit: `manual: acceptance of machine changes`

**Test checkpoint:** `Test-Path $env:LOCALAPPDATA\Bosun\campaign-hold.json, $env:LOCALAPPDATA\Bosun\admin-setup.json` prints `False` twice after the two restores, and `True` for either one still applied.

## 6. First Carbon campaign run

Filed 2026-11-13 from `D00 T03 §19`, which proves the guard's hold of an Antigravity session against a fake `agy`. The real one can't run on the repository's tree while a Claude Code run holds it, since two writers on one tree is the worse failure, so it is the owner's first start of the Carbon shortcut.

**Corrected 2026-11-14:** starting "Bosun Carbon campaign" on `agy` 1.5.9 exited 1 with `Error: --settings: unknown setting "maxStopHookContinuations"`, and `agy plugin validate` skipped `.agents/hooks.json` because it used Claude Code's nested `hooks`/`matcher` wrapper and a repo-root path instead of Antigravity's flat `<name>.Stop` list relative to `.agents/`. Fixed in `scripts/start-run.ps1` (starts `gemini-pro-orchestrator` under `/goal` with no `--settings`), `.agents/hooks.json` (`python ../scripts/run-guard.py hook --runner antigravity`), and `scripts/lib/run_guard.py` (`EXECUTOR_TERMINATION_REASON_` prefix).

**Phase:** 99
**Needs:** operator,attended
**Surface:** no

**Build order:**

1. Start the Carbon campaign with no other run live. Done when: `python scripts/run-guard.py status` names the Antigravity runner.
2. Let it ship or park one ready row. Done when: the outcome is in the phase run file.

- [ ] Close any Claude Code run, then start "Bosun Carbon campaign" from the desktop. Done when: `python scripts/run-guard.py status` prints `active` with runner `antigravity`.
- [ ] Watch one section through with no input: the Stop hook sends the session on while rows remain and while a review or CI gate is in flight. Done when: the session shipped or parked one ready row, recorded in the phase run file, or the blocker is recorded with its fix and filed through `add-todo`.
- [ ] Confirm `agy` opened on Orchestrator (Carbon) with `/goal` active and `.agents/hooks.json` loaded: `python scripts/run-guard.py status --json` keeps `hook_failures` empty across at least one end-of-turn `Stop` continuation. Done when: the phase run file records it, or records any fault with its fix and files it through `add-todo`.
- [ ] Close its window. Done when: `python scripts/run-guard.py status` prints `ended` and no `agy` process from it is left.
- [ ] Commit: `manual: first Carbon campaign run`

**Test checkpoint:** the phase run file names the section the Carbon session took and its outcome, and `python scripts/run-guard.py status` prints `ended` after the window closed.

## Verification

- Each section records its evidence.
- `python scripts/todo-graph.py validate` passes.
