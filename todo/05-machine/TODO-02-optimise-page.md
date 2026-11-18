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

> **Started:** 2026-11-18T21:38:04Z

Read-only first. Every recommendation starts from a measured fact.

**Corrected 2026-11-18** against the tree before building. Each correction says what the plan said before:

- The plan named its sources and not how to read them. `Bosun.Core` is `net11.0` with no package for WMI, and a new dependency is not needed: each fact has a documented source an unelevated process reads. The pagefile is `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management` `PagingFiles` (with `ExistingPageFiles` and `TempPageFile`), the same data `Win32_PageFileSetting` serves. The memory modules are SMBIOS type 17 records from `GetSystemFirmwareTable('RSMB')` (`Speed`, the JEDEC speed the module reports, `Configured Memory Speed`, the speed it runs at, `Part Number`, `Manufacturer`, `Bank Locator`), the data `Win32_PhysicalMemory` serves. The WSL cap is `%USERPROFILE%\.wslconfig` `[wsl2] memory=`. Startup entries are both Run keys (`HKCU` and `HKLM` `...\CurrentVersion\Run`), with `StartupApproved\Run` saying which Windows turned off. (It said `Win32_PageFileSetting`, `Win32_PhysicalMemory` and "the Run key".)
- As `StartupEntry` does (`src/Bosun.Core/StartupEntry.cs`, with `RegistryRunKey` in the panel), the rules live in `Bosun.Core` behind small interfaces, the readers that need Windows in the panel, so every rule is tested with fixtures.
- A module's rated speed is not in SMBIOS: its `Speed` is the JEDEC speed (2133 here), and an XMP rating is printed only in the part number (3600 on this kit, `Current state` above). The rated speed is decoded from the part number by each maker's published format, and a part number no rule knows gives no finding, never a guess.
- The checkpoint used `--filter FullyQualifiedName~`, which Microsoft.Testing.Platform refuses: it is `--filter-class` now.

**Corrected 2026-11-18 (plan review):** six gaps, planned into the items below. The readers are proved on controlled inputs, never satisfied by "unavailable"; startup entries keep their hive and registry view (the 32-bit `Run` view and `StartupApproved\Run32` included), and an approval record that can't be read is never read as enabled; the pagefile keeps configured, active and temporary apart; SMBIOS fields are named and measured as the DMTF specification has them (`Speed` the maximum capable rate in MT/s, `Configured Memory Speed` in MT/s, 0 unknown), and an unknown configured speed gives no finding; the WSL cap is the configured value, read without starting WSL, and a plain byte count is a size; and each part-number format is proved at two ratings with a near-match left unknown.

**Phase:** 5
**Needs:** none
**Surface:** no

**Build order:**

1. Read the four facts behind interfaces, each a value or a reason. Done when: pagefile, WSL cap, memory modules and startup entries are read on this machine without elevation.
2. Decide the RAM finding from the part number only. Done when: a known kit below its rating is a finding with its evidence, and an unknown one is none.

- [ ] Add `src/Bosun.Core/Optimise/Diagnostics.cs`: `Fact<T>` (a value, or the reason it could not be read), and records for the pagefile (configured files from `PagingFiles`, each path with initial and maximum MB or system-managed; active files from `ExistingPageFiles`; whether `TempPageFile` says Windows is running on a temporary one), the WSL cap (the configured `memory=` in MB, or "no cap set: WSL takes half of memory", or the value it could not parse; the configured value only, never the running VM's), each memory module (slot and bank locator, maker, part number, `Speed` and `Configured Memory Speed` in MT/s, each null when SMBIOS gives 0, unknown), and each startup entry (name, command, hive, registry view, and its approval: on, turned off by Windows, no record, or the record unreadable with why). Done when: `DiagnosticsTests` build each from fixtures, and each failure is a reason, never an exception or a 0. Cheaper substitute: strings shown as read.
- [ ] Parse the sources in `Bosun.Core`: `PagingFiles` lines (`C:\pagefile.sys 32768 51200`, `?:\pagefile.sys` system-managed, `C:\pagefile.sys 0 0`) against `ExistingPageFiles` (`\??\C:\pagefile.sys`) and `TempPageFile`, so a move waiting on a restart, a temporary fallback and an empty configuration each say so; `.wslconfig` (sections, comments, `memory=14GB`, `16384MB`, a plain byte count `15032385536` equal to `14GB`, an absent file, an absent key, a bad value, each its own reason or value); and SMBIOS type 17 records by the DMTF layout (the structure walk, string sets, the extended speed fields over 65,534 MT/s, an empty slot with size 0 skipped, a truncated table a reason). Done when: `DiagnosticsTests` read each fixture with its exact value or reason.
- [ ] Read them on Windows in `src/Bosun.Panel/Optimise/WindowsDiagnostics.cs` through small seams (a registry view, a file reader, a firmware table reader), with no elevation: both hives, the 64- and 32-bit views of the machine `Run` key and `StartupApproved\Run` and `Run32`, the profile's `.wslconfig` read as a file with WSL never started, and `GetSystemFirmwareTable('RSMB')`. Done when: `WindowsDiagnosticsTests` with controlled seams read exact values for all four facts, keep three when the fourth is denied, tell same-named entries apart by hive, view and command with their approvals, tell an absent approval record from an unreadable one, and fail if a reader is disconnected or always answers unavailable; and one case reads this machine's four facts and prints them.
- [ ] Decode a part number's rated speed in `RatedSpeed.Of` (`Bosun.Core/Optimise`) by each maker's published format, each with its source in a comment and two documented part numbers at different ratings: Corsair (`CMK32GX4M2D3600C18`: 3600, `CMK16GX4M2B3200C16`: 3200), G.Skill (`F4-3600C16D-32GVKC`: 3600, `F4-3200C16D-16GVK`: 3200), Kingston Fury (`KF436C18BBK2/32`: 3600, `KF432C16BBK2/16`: 3200), HyperX (`HX436C18FB3K2/32`: 3600, `HX432C16PB3K2/16`: 3200), Crucial Ballistix (`BL2K16G36C16U4B`: 3600, `BL2K8G32C16U4B`: 3200), TeamGroup (`TF3D416G3600HC18J`: 3600, `TF3D416G3200HC16F`: 3200); a near-match of each (a prefix with no speed field, a JEDEC module's number) is unknown. Done when: `DiagnosticsTests` decode each pair and leave each near-match unknown.
- [ ] Mark RAM below its rated speed only with the evidence attached: a finding when every populated module has a decoded rating and a known configured speed below it, naming each module's part number, rating, configured and maximum speed in MT/s; no finding for an unknown part number, an unknown (0) configured speed, a mixed kit whose ratings differ, an empty inventory, or a module already at its rating. Done when: `DiagnosticsTests` give the finding, with each evidence field asserted, for this machine's kit by its recorded part number and raw SMBIOS speed fields (written into this section when read), and none for each of the others. Cheaper substitute: assuming XMP is off.
- [ ] Commit: `optimise: diagnostics`

**Test checkpoint:** `bosun run -- dotnet test --project tests/Bosun.Core.Tests --filter-class "*DiagnosticsTests"` and `bosun run -- dotnet test --project tests/Bosun.Panel.Tests --filter-class "*WindowsDiagnosticsTests"` exit 0, and fail if the change is reverted.

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
- [ ] Draw each finding value the Optimise page can't read (a counter, the WSL cap with WSL stopped) as a `NotAvailable` with why, through the `Value` style (**Added 2026-11-16** from the completion review of `D02 T03 §7`). Done when: a test with WSL stopped shows the cap as "Not available" with its reason, never 0. Cheaper substitute: a blank cell.
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
