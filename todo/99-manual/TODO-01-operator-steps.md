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

- `docs/specs/2026-11-07-devmode-design.md`.
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

1. Read the Decision card, or `devmode report --days 7`. Done when: at least 7 days are logged.
2. Record the decision. Done when: the reason is written here.

- [ ] Read `devmode report --days 7` from 2026-11-14 onward. Done when: the peaks and overlaps are quoted here.
- [ ] Record buy or don't buy, with the reason. Done when: the note is committed. Cheaper substitute: deciding without the log numbers.
- [ ] Commit: `manual: 64 GB decision recorded`

**Test checkpoint:** `devmode report --days 7` prints at least 7 days of data.

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

**Test checkpoint:** `(Get-Item $env:LOCALAPPDATA\DevMode\panel\DevMode.Panel.exe).VersionInfo.ProductVersion` holds the installed commit, and the walkthrough note is committed.

## Verification

- Each section records its evidence.
- `python scripts/todo-graph.py validate` passes.
