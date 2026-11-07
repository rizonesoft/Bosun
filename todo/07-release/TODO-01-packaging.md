---
schema_version: 1
id: packaging
domain: 07-release
status: active
title: "TODO-01 -- Packaging and budgets"
---

# TODO-01 -- Packaging and budgets

> **Goal:** The panel installs per user with one command, can be removed cleanly, meets its memory budget and is documented.

> **Current state:** Measured 2026-11-07. `scripts/install.ps1` installs the C++ tools and logger, with no panel. `README.md` covers the queue only.

## Inputs

- `design/SPEC.md` §16.
- `R:\GitHub\Steward\publish.sh`, `R:\GitHub\Steward\scripts\signing-status.ps1`.
- -> XREF: D00 T02 §2 -- consumed by this file
- -> XREF: D02 T02 §4 -- consumed by this file
- -> XREF: D04 T04 §3 -- consumed by this file
- -> XREF: D05 T02 §3 -- consumed by this file
- -> XREF: D06 T01 §8 -- consumed by this file
- -> XREF: D99 T01 §3 -- consumes this file

## Outcome

- A clean install starts the panel from the Start menu with no administrator rights.
- The idle budget is measured and enforced.

**Adjacency:** list=not-applicable (no records); document=not-applicable (none); settings=not-applicable (none here); reporting=not-applicable (none); notifications=not-applicable (none); permissions=not-applicable (per-user install); audit=not-applicable (none); exchange=not-applicable (none); reverse=applicable @ D07 T01 §3

**Adjacency rationale:** Uninstall is the reverse of install, and leaves logs unless asked.

## Implementation Order

| Order | Section | Deliverable | Depends On | Status |
| ----- | ------- | ----------- | ---------- | ------ |
| 1 | §1 | Publish and install | D00 T02 §2, D02 T02 §4 | [ ] |
| 2 | §2 | Signing status | §1 | [ ] |
| 3 | §3 | Uninstall | §1 | [ ] |
| 4 | §4 | Keyboard and accessibility audit | D04 T04 §3, D05 T02 §3, D06 T01 §8 | [ ] |
| 5 | §5 | Performance budget | §1, D04 T04 §3, D06 T01 §8 | [ ] |
| 6 | §6 | Documentation | §4, §5 | [ ] |

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

1. Create `scripts/publish-panel.ps1`. Done when: `dotnet publish` gives a self-contained win-x64 folder.
2. Extend `scripts/install.ps1`. Done when: the panel, shortcut and optional tray start are set.

- [ ] Publish `src/DevMode.Panel` self-contained, through `devmode run`. Done when: it runs on a machine with no .NET runtime in the path, checked by clearing `DOTNET_ROOT`. Cheaper substitute: requiring a preview runtime.
- [ ] Install to `%LOCALAPPDATA%\DevMode\panel`, stopping a running copy first. Done when: `tests/install_test.ps1` reinstalls over a running copy in a temporary `LOCALAPPDATA`, leaving the real install alone. Cheaper substitute: a failed copy behind a locked file.
- [ ] Create a Start menu shortcut, with no desktop icon. Done when: the shortcut opens the panel with the right icon.
- [ ] Add the About page per `design/SPEC.md` §16. Done when: version and commit come from build properties.
- [ ] Commit: `release: publish and install`

**Test checkpoint:** `pwsh tests/install_test.ps1` exits 0: installing to a temporary `LOCALAPPDATA` gives `DevMode.Panel.exe` whose `ProductVersion` holds the current commit.

## 2. Signing status

Unsigned builds work for a private tool but SmartScreen will complain. Whether to pay for a certificate is the operator's call.

**Phase:** 7
**Needs:** owner-decision
**Surface:** no

**Build order:**

1. Port `scripts/signing-status.ps1`. Done when: it reports all binaries.
2. Sign, once answered. Done when: the answer is recorded and the gate reflects it.

- [ ] Port from `R:\GitHub\Steward\scripts\signing-status.ps1`. Done when: every `.exe`, `.dll` and `.ps1` lists its state.
- [ ] Record the decision here, and make it a gate if signing is chosen. Done when: `STATUS` output matches the decision. Cheaper substitute: claiming signed while unsigned.
- [ ] Commit: `release: signing status`

**Test checkpoint:** `pwsh scripts/signing-status.ps1` lists every shipped binary and exits per the recorded decision.

## 3. Uninstall

Whatever install adds must come off, with logs left unless asked.

**Phase:** 7
**Needs:** none
**Surface:** no

**Build order:**

1. Create `scripts/uninstall.ps1`. Done when: panel, shortcut, Run entry and scheduled tasks go.
2. Offer data removal. Done when: logs and secrets stay unless `-RemoveData` is given.

- [ ] Remove binaries, `DevMode Logger` and panel Run entries, and PATH entry. Done when: a test in a temporary profile shows nothing left. Cheaper substitute: leaving a Run entry pointing at nothing.
- [ ] Remove the API key only with `-RemoveData`, and say so. Done when: the default run leaves `secrets\`, and reports it left it.
- [ ] Create `tests/uninstall_test.ps1`, installing to and removing from a temporary `LOCALAPPDATA`. Done when: the real install is untouched, and a leftover file fails the test.
- [ ] Commit: `release: uninstall`

**Test checkpoint:** `pwsh tests\uninstall_test.ps1` exits 0.

## 4. Keyboard and accessibility audit

Page-level tests prove each page. This walks them all, so a gap can't hide between sections.

**Phase:** 7
**Needs:** none
**Surface:** no

**Build order:**

1. Create `tests/DevMode.Panel.Tests/AccessibilityAudit.cs`. Done when: every view is walked.
2. Fix findings at the view. Done when: the audit passes.

- [ ] Assert names on icon-only controls, 32 px targets, tab order and no colour-only status, for all views. Done when: removing a name fails the audit. Cheaper substitute: an allowlist that grows.
- [ ] Check live regions on Activity and status changes. Done when: a status change announces, in a test.
- [ ] Commit: `release: accessibility audit`

**Test checkpoint:** `devmode run -- dotnet test DevMode.slnx --filter FullyQualifiedName~AccessibilityAudit` exits 0, and fails if the change is reverted.

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

1. Extend `README.md`. Done when: install, each page, and the API key are covered.
2. Add `docs/troubleshooting.md`. Done when: each failed state has an entry.

- [ ] Add the panel to `README.md`, in question-shaped headings. Done when: every command shown was run. Cheaper substitute: documenting a command from memory.
- [ ] Create `scripts/check-doc-commands.py`, checking each command in `README.md` against the tools and scripts on disk. Done when: a fixture naming a missing script fails it.
- [ ] Write `docs/troubleshooting.md` covering the unsupported kit, stale snapshot, stalled run, missing reviewer and no key. Done when: each page's failed state links to its entry.
- [ ] Update `docs/specs/2026-11-07-control-panel-design.md` with what changed in the build. Done when: the defaults table matches what shipped.
- [ ] Commit: `docs: panel readme and troubleshooting`

**Test checkpoint:** `python scripts/check-doc-commands.py` exits 0, verifying each documented command exists.

## Verification

- `pwsh scripts/check-all.ps1 -Perf` passes on the published build.
- A clean install and uninstall round-trip passes.
