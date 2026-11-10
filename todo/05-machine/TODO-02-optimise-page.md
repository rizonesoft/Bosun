---
schema_version: 1
id: optimise-page
domain: 05-machine
status: active
title: "TODO-02 -- Optimise"
---

# TODO-02 -- Optimise

> **Goal:** Each tuning the machine has had or could use is shown with its evidence, and can be previewed, applied, verified and undone, including those waiting on data.

> **Current state:** Measured 2026-11-07. On 2026-11-07 the pagefile moved to `C:` at 32 to 50 GB and Discord and Creative Cloud startup entries were disabled, with backups kept only in that session's scratchpad. RAM runs at 2133 MHz on a kit rated 3600 by its part number. Rejections stand in `docs/specs/2026-11-07-bosun-design.md`.

## Inputs

- `design/SPEC.md` §12.
- `docs/specs/2026-11-07-bosun-design.md`, phase 2 and rejected.
- -> XREF: D02 T02 §3 -- consumed by this file
- -> XREF: D07 T01 §4 -- consumes this file
- -> XREF: D02 T03 §4 -- consumed by this file
- -> XREF: D02 T03 §7 -- consumed by this file
- -> XREF: D99 T01 §5 -- consumed by this file

## Outcome

- Earlier changes appear as Applied with an undo.
- Nothing changes without a preview and a read-back.

**Adjacency:** list=applicable @ D05 T02 §3; document=not-applicable (none); settings=applicable @ D05 T02 §5; reporting=not-applicable (none); notifications=not-applicable (none); permissions=applicable @ D05 T02 §2; audit=applicable @ D05 T02 §2; exchange=not-applicable (none); reverse=applicable @ D05 T02 §2

**Adjacency rationale:** The framework enforces reverse: an item without an undo can't be applied, only described.

## Implementation Order

| Order | Section | Deliverable | Depends On | Status |
| ----- | ------- | ----------- | ---------- | ------ |
| 1 | §1 | Diagnostics | D02 T02 §3 | [ ] |
| 2 | §2 | Preview, apply, verify, undo framework | §1 | [ ] |
| 3 | §3 | Optimise page, WSL cap and startup entries | §2, D02 T03 §4, D02 T03 §7 | [ ] |
| 4 | §4 | Service checks for Dev Drive and Defender | §3 | [ ] |
| 5 | §5 | Pressure monitor and background memory priority | §2 | [ ] |

## 1. Diagnostics

Read-only first. Every recommendation starts from a measured fact.

**Phase:** 5
**Needs:** none
**Surface:** no

**Build order:**

1. Create `src/Bosun.Core/Optimise/Diagnostics.cs`. Done when: pagefile, WSL cap, RAM speed and startup entries are read.
2. Fixture them. Done when: tests cover each.

- [ ] Read `Win32_PageFileSetting`, `.wslconfig`, `Win32_PhysicalMemory` speeds and part numbers, and the Run key. Done when: each reports a value or a reason, and the data needs no elevation. Cheaper substitute: reporting rated speed from memory instead of the part number.
- [ ] Mark RAM below its rated speed only with the evidence attached. Done when: an unknown part number gives no finding. Cheaper substitute: assuming XMP is off.
- [ ] Commit: `optimise: diagnostics`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~Diagnostics` exits 0, and fails if the change is reverted.

## 2. Preview, apply, verify, undo framework

Machine changes are the panel's riskiest actions, so all of them go through one path that can't skip a step.

**Phase:** 5
**Needs:** none
**Surface:** no

**Build order:**

1. Create `IOptimisation`. Done when: an item can't apply without a saved backup.
2. Add per-action elevation. Done when: only that action prompts UAC.

- [ ] Define Preview, Apply, Verify and Undo in `src/Bosun.Core/Optimise/IOptimisation.cs`, with backups in `%LOCALAPPDATA%\Bosun\backups\`. Done when: a failing verify automatically offers undo, and Activity logs each step. Cheaper substitute: applying and hoping.
- [ ] Run elevated steps through `runas` with one fixed command, not a general elevated helper, behind an `IElevation` seam. Done when: a faked refusal leaves no change in a test. A real UAC prompt is proven in `D99 T01 §5`. Cheaper substitute: elevating the whole panel.
- [ ] Show Discord, Creative Cloud and pagefile as Applied, recording the current values as their baseline. Done when: the original values, where unknown, say so instead of being invented. Cheaper substitute: claiming a backup exists from the old session.
- [ ] Commit: `optimise: change framework`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~OptimisationFramework` exits 0, and fails if the change is reverted.

## 3. Optimise page, WSL cap and startup entries

The first real items, chosen because they are the ones this machine has already needed.

**Phase:** 5
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §12.
**Job:** see what would help, why, and change it safely.
**Treatment:** a table with evidence and a guided change flow. Cheaper substitute that fails: a one-click optimise button.
**Chrome:** the shell's page frame and `Btn.Danger`.

**Build order:**

1. Create `Views/OptimiseView.xaml`. Done when: findings list with evidence.
2. Implement the WSL cap and startup items. Done when: each round-trips, with a restart notice for WSL.

- [ ] Build the table per `design/SPEC.md` §12. Done when: items for the operator show instructions, like XMP. Cheaper substitute: an Apply button for a BIOS setting.
- [ ] Edit `.wslconfig` preserving all other keys and comments. Done when: a fixture keeps unrelated content byte for byte, and a running WSL gets a restart notice with no forced shutdown. Cheaper substitute: rewriting the file from a template, or running `wsl --shutdown` unasked.
- [ ] Toggle Run key entries with a saved copy, tested against a disposable key under `HKCU\Software\Bosun\Test`. Done when: undo restores the exact value, and the real Run key is untouched by tests.
- [ ] Add per-row Preview, Apply, Verify and Undo buttons in `Views/OptimiseView.xaml`. Done when: Apply is disabled until Preview has run, and a test walks all four against a disposable target. Cheaper substitute: one Apply button.
- [ ] Show what a waiting row waits for and until when, in `Views/OptimiseView.xaml`. Done when: a fixture row reads its date in words.
- [ ] Commit: `optimise: page, WSL cap, startup entries`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~OptimisePage` exits 0, and fails if the change is reverted.

## 4. Service checks for Dev Drive and Defender

These are later speedups rather than memory fixes, so they are reported, not pushed.

**Phase:** 5
**Needs:** none
**Surface:** no

**Build order:**

1. Add Dev Drive and exclusions to `Diagnostics`. Done when: both report.
2. Show them as information. Done when: no apply action exists.

- [ ] Read whether repos sit on a Dev Drive and whether Defender excludes them. Done when: each shows a state and a reason. Cheaper substitute: changing Defender settings, which is a security decision for the operator.
- [ ] Label them as build speed, not memory, per `docs/specs/2026-11-07-bosun-design.md`. Done when: the wording test passes.
- [ ] Commit: `optimise: build speed information`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~BuildSpeedInfo` exits 0, and fails if the change is reverted.

## 5. Pressure monitor and background memory priority

Earlier design left these waiting for real peaks. The owner chose on 2026-11-07 to build it now with automatic action off, so nothing acts until they set thresholds from `bosun report --days 7`, possible from 2026-11-14.

**Phase:** 5
**Needs:** none
**Surface:** no

**Build order:**

1. Add thresholds and an off switch to `src/Bosun.Core/PanelSettings.cs`. Done when: automatic action is off by default and reads back.
2. Add the monitor. Done when: `MemoryHigh` lowers under pressure, and memory priority applies to named background apps.

- [ ] Measure how quickly WSL returns memory, from the logs and a controlled test. Done when: the measured figure is recorded in `docs/specs/2026-11-07-bosun-design.md`. Cheaper substitute: picking thresholds from memory.
- [ ] Lower the live `user.slice` `MemoryHigh` in `wsl/bosun_log.py` under pressure, restoring it after. Done when: a controlled test on a disposable slice shows it lowered and restored. Cheaper substitute: killing work under pressure.
- [ ] Set low memory priority through `SetProcessInformation` for operator-listed apps. Done when: the priority reads back, and nothing is killed or trimmed. Cheaper substitute: a working-set cleaner, rejected in the design.
- [ ] Add the pressure thresholds and listed apps to Settings, with `PanelSettings` as store, warning below the 6 GB queue floor by default. Done when: a test reads them back, and no action runs while the switch is off. Cheaper substitute: thresholds set in code.
- [ ] Keep the listed apps in `PanelSettings` empty by default. Done when: a fresh install lowers the priority of nothing.
- [ ] Commit: `machine: pressure monitor and memory priority`

**Test checkpoint:** `bosun run -- pwsh scripts/build.ps1` then `build\Release\test_core.exe pressure` exits 0, and fails if the change is reverted.

## Verification

- `dotnet test Bosun.slnx` passes.
- A disposable item round-trips: preview, apply, verify, undo. A real item round-trips in `D99 T01 §5`.
