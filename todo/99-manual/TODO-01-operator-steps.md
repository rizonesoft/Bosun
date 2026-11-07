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
- [ ] Start a real unattended run from the panel, and kill its session to prove the watchdog resumes it. Done when: the run resumes and the panel shows the restart.
- [ ] Commit: `manual: first attended walkthrough`

**Test checkpoint:** `(Get-Item $env:LOCALAPPDATA\Bosun\panel\Bosun.Panel.exe).VersionInfo.ProductVersion` holds the installed commit, and the walkthrough note is committed.

## 4. Decide whether to make the repo public

`rizonetech/Bosun` is private, with the README, licence and community files of a public repo. Publishing cannot be fully undone, because forks and caches keep what was seen, so these checks come first.

**Phase:** 99
**Needs:** owner-decision,operator
**Surface:** no

**Build order:**

1. Clean what should not be public. Done when: each check below passes.
2. Change the visibility. Done when: `gh repo view --json visibility` says PUBLIC.

- [ ] Confirm `GPL-3.0-or-later` in `LICENSE`, the licence of Recto, Isotone and Office, chosen by default on 2026-11-07. Done when: the owner's answer is recorded here. Cheaper substitute: publishing with an unconfirmed licence.
- [ ] Move the alert recipient in `.conclave/automations/alert.py` out of the source into ignored local config. Done when: `git grep -n "@gmail" -- .conclave` prints nothing.
- [ ] Replace personal absolute paths in `todo/`, `AGENTS.md` and `docs/specs/` with repo names. Done when: `git grep -lE "[A-Z]:\\\\GitHub" -- todo AGENTS.md docs` prints nothing.
- [ ] Review the sibling repo names in `design/mockups/control-panel.html` and `docs/images/`. Done when: private client and project names are replaced with samples.
- [ ] Scan the full history for secrets with `gitleaks detect`. Done when: it reports no leaks.
- [ ] Enable private vulnerability reporting and secret scanning with push protection on `rizonetech/Bosun`. Done when: `gh api repos/rizonetech/Bosun/private-vulnerability-reporting` says enabled.
- [ ] Remove the private-repository paragraph from `SECURITY.md`. Done when: its only reporting route is private vulnerability reporting.
- [ ] Run `gh repo edit rizonetech/Bosun --visibility public --accept-visibility-change-consequences`. Done when: the README renders with its badges for a signed-out visitor. Cheaper substitute: publishing before the checks above pass.
- [ ] Commit: `manual: publish the repo`

**Test checkpoint:** `gh repo view rizonetech/Bosun --json visibility` prints PUBLIC.

## Verification

- Each section records its evidence.
- `python scripts/todo-graph.py validate` passes.
