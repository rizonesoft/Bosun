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
| 1 | §1 | Diagnostics | D02 T02 §3 | [x] |
| 2 | §2 | Preview, apply, verify, undo framework | §1 | [x] |
| 3 | §3 | Optimise page, WSL cap and startup entries | §2, §6, §7, D02 T03 §4, D02 T03 §7 | [ ] |
| 4 | §4 | Service checks for Dev Drive and Defender | §3 | [ ] |
| 5 | §5 | Pressure monitor and background memory priority | §2 | [ ] |
| 6 | §6 | Elevated change helper | §2 | [ ] |
| 7 | §7 | Changes made before Bosun, with honest baselines | §2 | [ ] |

## 1. Diagnostics

> **Started:** 2026-11-18T21:38:04Z

Read-only first. Every recommendation starts from a measured fact.

**Corrected 2026-11-18** against the tree before building. Each correction says what the plan said before:

- The plan named its sources and not how to read them. `Bosun.Core` is `net11.0` with no package for WMI, and a new dependency is not needed: each fact has a documented source an unelevated process reads. The pagefile is `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management` `PagingFiles` (with `ExistingPageFiles` and `TempPageFile`), the same data `Win32_PageFileSetting` serves. The memory modules are SMBIOS type 17 records from `GetSystemFirmwareTable('RSMB')` (`Speed`, the JEDEC speed the module reports, `Configured Memory Speed`, the speed it runs at, `Part Number`, `Manufacturer`, `Bank Locator`), the data `Win32_PhysicalMemory` serves. The WSL cap is `%USERPROFILE%\.wslconfig` `[wsl2] memory=`. Startup entries are both Run keys (`HKCU` and `HKLM` `...\CurrentVersion\Run`), with `StartupApproved\Run` saying which Windows turned off. (It said `Win32_PageFileSetting`, `Win32_PhysicalMemory` and "the Run key".)
- As `StartupEntry` does (`src/Bosun.Core/StartupEntry.cs`, with `RegistryRunKey` in the panel), the rules live in `Bosun.Core` behind small interfaces, the readers that need Windows in the panel, so every rule is tested with fixtures.
- A module's rated speed is not in SMBIOS: its `Speed` is the JEDEC speed (2133 here), and an XMP rating is printed only in the part number (3600 on this kit, `Current state` above). The rated speed is decoded from the part number by each maker's published format, and a part number no rule knows gives no finding, never a guess.
- The checkpoint used `--filter FullyQualifiedName~`, which Microsoft.Testing.Platform refuses: it is `--filter-class` now.

**Corrected 2026-11-18 (plan review):** six gaps, planned into the items below. The readers are proved on controlled inputs, never satisfied by "unavailable"; startup entries keep their hive and registry view (the 32-bit `Run` view and `StartupApproved\Run32` included), and an approval record that can't be read is never read as enabled; the pagefile keeps configured, active and temporary apart; SMBIOS fields are named and measured as the DMTF specification has them (`Speed` the maximum capable rate in MT/s, `Configured Memory Speed` in MT/s, 0 unknown), and an unknown configured speed gives no finding; the WSL cap is the configured value, read without starting WSL, and a plain byte count is a size; and each part-number format is proved at two ratings with a near-match left unknown.

**Read on this machine 2026-11-19** (SMBIOS 3.3, the section's own reader): four modules of 8,192 MB, maker `Corsair`, part number `CMG16GX4M2D3600C18` (two trailing spaces in the table, trimmed), in `Controller0-ChannelA-DIMM0`, `ChannelA-DIMM1`, `ChannelB-DIMM0` and `ChannelB-DIMM1` (banks `BANK 0`, `BANK 0`, `BANK 1`, `BANK 1`), each with `Speed` 2133 and `Configured Memory Speed` 2133 MT/s. The part number is Corsair's `CMG` (Vengeance RGB) family, rated 3600 by its speed field, so the finding fires: rated 3600, running 2133. The pagefile is configured `C:\pagefile.sys 32768 51200`, active `\??\C:\pagefile.sys`, with no temporary pagefile. `.wslconfig` sets `[wsl2] memory=12GB` (12,288 MB; the 14 GB in the design notes is older). HKCU's Run key holds 17 entries, the Bosun logger among them, and HKLM's 3.

**Phase:** 5
**Needs:** none
**Surface:** no

**Build order:**

1. Read the four facts behind interfaces, each a value or a reason. Done when: pagefile, WSL cap, memory modules and startup entries are read on this machine without elevation.
2. Decide the RAM finding from the part number only. Done when: a known kit below its rating is a finding with its evidence, and an unknown one is none.

- [x] Add `src/Bosun.Core/Optimise/Diagnostics.cs`: `Fact<T>` (a value, or the reason it could not be read), and records for the pagefile (configured files from `PagingFiles`, each path with initial and maximum MB or system-managed; active files from `ExistingPageFiles`; whether `TempPageFile` says Windows is running on a temporary one), the WSL cap (the configured `memory=` in MB, or "no cap set: WSL takes half of memory", or the value it could not parse; the configured value only, never the running VM's), each memory module (slot and bank locator, maker, part number, `Speed` and `Configured Memory Speed` in MT/s, each null when SMBIOS gives 0, unknown), and each startup entry (name, command, hive, registry view, and its approval: on, turned off by Windows, no record, or the record unreadable with why). Done when: `DiagnosticsTests` build each from fixtures, and each failure is a reason, never an exception or a 0. Cheaper substitute: strings shown as read.
- [x] Parse the sources in `Bosun.Core`: `PagingFiles` lines (`C:\pagefile.sys 32768 51200`, `?:\pagefile.sys` system-managed, `C:\pagefile.sys 0 0`) against `ExistingPageFiles` (`\??\C:\pagefile.sys`) and `TempPageFile`, so a move waiting on a restart, a temporary fallback and an empty configuration each say so; `.wslconfig` (sections, comments, `memory=14GB`, `16384MB`, a plain byte count `15032385536` equal to `14GB`, an absent file, an absent key, a bad value, each its own reason or value); and SMBIOS type 17 records by the DMTF layout (the structure walk, string sets, the extended speed fields over 65,534 MT/s, an empty slot with size 0 skipped, a truncated table a reason). Done when: `DiagnosticsTests` read each fixture with its exact value or reason.
- [x] Read them on Windows in `src/Bosun.Panel/Optimise/WindowsDiagnostics.cs` through small seams (a registry view, a file reader, a firmware table reader), with no elevation: both hives, the 64- and 32-bit views of the machine `Run` key and `StartupApproved\Run` and `Run32`, the profile's `.wslconfig` read as a file with WSL never started, and `GetSystemFirmwareTable('RSMB')`. Done when: `WindowsDiagnosticsTests` with controlled seams read exact values for all four facts, keep three when the fourth is denied, tell same-named entries apart by hive, view and command with their approvals, tell an absent approval record from an unreadable one, and fail if a reader is disconnected or always answers unavailable; and one case reads this machine's four facts and prints them.
- [x] Decode a part number's rated speed in `RatedSpeed.Of` (`Bosun.Core/Optimise`) by each maker's published format, each with its source in a comment and two documented part numbers at different ratings: Corsair (`CMK32GX4M2D3600C18`: 3600, `CMK16GX4M2B3200C16`: 3200), G.Skill (`F4-3600C16D-32GVKC`: 3600, `F4-3200C16D-16GVK`: 3200), Kingston Fury (`KF436C18BBK2/32`: 3600, `KF432C16BBK2/16`: 3200), HyperX (`HX436C18FB3K2/32`: 3600, `HX432C16PB3K2/16`: 3200), Crucial Ballistix (`BL2K16G36C16U4B`: 3600, `BL2K8G32C16U4B`: 3200), TeamGroup (`TF3D416G3600HC18J`: 3600, `TF3D416G3200HC16F`: 3200); a near-match of each (a prefix with no speed field, a JEDEC module's number) is unknown. Done when: `DiagnosticsTests` decode each pair and leave each near-match unknown.
- [x] Mark RAM below its rated speed only with the evidence attached: a finding when every populated module has a decoded rating and a known configured speed below it, naming each module's part number, rating, configured and maximum speed in MT/s; no finding for an unknown part number, an unknown (0) configured speed, a mixed kit whose ratings differ, an empty inventory, or a module already at its rating. Done when: `DiagnosticsTests` give the finding, with each evidence field asserted, for this machine's kit by its recorded part number and raw SMBIOS speed fields (written into this section when read), and none for each of the others. Cheaper substitute: assuming XMP is off.
- [x] Commit: `optimise: diagnostics`

**Test checkpoint:** `bosun run -- dotnet test --project tests/Bosun.Core.Tests --filter-class "*DiagnosticsTests"` and `bosun run -- dotnet test --project tests/Bosun.Panel.Tests --filter-class "*WindowsDiagnosticsTests"` exit 0, and fail if the change is reverted.

> **Verified:** 2026-11-19 | §1 | 81d80ce41dbab42df9584fdcd25b68eb82801607
> **Review:** `docs/reviews/d05-t02-s1.json`

## 2. Preview, apply, verify, undo framework

> **Started:** 2026-11-19T00:44:34Z

Machine changes are the panel's riskiest actions, so all of them go through one path that can't skip a step.

**Corrected 2026-11-19** against the tree before building. Each correction says what the plan said before:

- The Activity pane is the panel's (`src/Bosun.Panel/Shell/ActivityLog.cs`), so the framework in `Bosun.Core` reports each step through a small `IChangeLog` the panel adapts to it. (It said "Activity logs each step" with no seam.)
- A backup is the change's undo, so it is written temp-then-replace and read back before anything changes; an Apply whose backup can't be written and read back is refused. (It named the folder only.)
- The checkpoint used `--filter FullyQualifiedName~`, which Microsoft.Testing.Platform refuses: it is `--filter-class` now.

**Corrected 2026-11-19 (plan review):** nine gaps. The section held three subsystems, so it is split: the elevated helper is `§6` (gaps 2, 6 and 7, and 8's crossing of the elevation boundary) and the changes made before Bosun are `§7` (gap 5); `§3` waits on both. The rest are planned here: Apply is bound to the exact proposal the person confirmed (gap 1); every outcome after a write keeps its backup and reads the target again (gap 3); a backup is bound to its exact target and operation and checked on read-back and before a restore (gap 4); backups live in the panel's own data folder (`PanelPaths`, `BOSUN_PANEL_DATA`), never a literal path (gap 8); and one eligibility result tells the row and the palette alike whether Apply or Undo can run and why not, `§3` registering the actions (gap 9).

**Phase:** 5
**Needs:** none
**Surface:** no

**Build order:**

1. The one path: a confirmed proposal, a read-back backup bound to its target, Apply, Verify. Done when: Apply runs only the proposal the person confirmed, on an unchanged target.
2. Every outcome after a write is said and recoverable. Done when: no failed or interrupted change is ever reported as done.
3. One eligibility result for every place that offers a change. Done when: the row and the palette say the same why.

- [x] Define `IOptimisation` in `src/Bosun.Core/Optimise/IOptimisation.cs`: an id, a title, whether it needs elevation, `Read` (the target's current value), `Propose` (a `Proposal`: the exact target, its before value as read and the after value), `Apply`, `Verify` reading the setting back, and `Restore` (writing a backed-up value). Done when: `OptimisationFrameworkTests` build a fake item and read its proposal's target, before and after. Cheaper substitute: free-text previews.
- [x] Add `ChangeRunner.Apply(confirmed proposal)`, the only way to change anything: it reads the target again and refuses, changing nothing, when the before value is not what the person confirmed (the setting changed since the preview) or the proposal is not the one the item proposes now (edited); then writes the backup, applies and verifies. A cancelled confirmation never reaches it. Done when: `OptimisationFrameworkTests` find the target untouched and a fresh preview asked for after a cancellation, an edited proposal and a setting changed between preview and apply. Cheaper substitute: previewing again just before writing.
- [x] Write each backup to `<panel data>\backups\<item id>\<UTC time>-<operation>.json` (the folder `PanelPaths` gives, `BOSUN_PANEL_DATA` in tests) temp-then-replace, read back, holding the item id, the exact target (for a startup entry its hive, view and name), the operation, the before value and the proposal it was taken for; a backup is never overwritten. Done when: `OptimisationFrameworkTests` find an Apply refused (nothing changed) when the write or read-back fails, and a test run writes nothing under the operator's own data folder. Cheaper substitute: one backup per item, overwritten.
- [x] Undo from the backup of the change being reversed, named by the applied result, never "the newest": it is checked on read (well formed, same item, same target, same operation) and again against the target before restoring, and an Undo is itself backed up first, so it can be undone. Done when: `OptimisationFrameworkTests` undo after reopening the store, after a later failed attempt, refuse a well-formed backup for a different target and a truncated one, and undo twice asserting the exact values each time. Cheaper substitute: restoring the newest file.
- [x] Say every outcome after a write, the backup kept for each: a write that then errors, a Verify that fails, an Undo that reports success but leaves another value, each read again and reported as changed, restored, or could not be verified, with Undo offered whenever a write may have happened. Done when: `OptimisationFrameworkTests` drive each and none is reported as a successful change or recovery. Cheaper substitute: trusting the item's own return value.
- [x] Report each step through `IChangeLog` (`Bosun.Core`) and adapt it to the panel's `ActivityLog` in `src/Bosun.Panel/Optimise/ActivityChangeLog.cs`: the step, the target, the outcome and the backup's name. Done when: a Panel `OptimisationFrameworkTests` case runs a disposable item through the real adapter and reads the Activity lines of a failed verify with its Undo offered. Cheaper substitute: a fake log only.
- [x] Give one eligibility result, `ChangeRunner.CanApply` and `CanUndo` (can it run, and why not: "Preview it first", "The setting changed since the preview", "Nothing to undo", "Needs an administrator prompt"), for every place that offers a change; `§3` registers the row and palette actions on it (-> XREF: D05 T02 §3). Done when: `OptimisationFrameworkTests` read each reason. Cheaper substitute: each button deciding for itself.
- [x] Commit: `optimise: change framework`

**Test checkpoint:** `bosun run -- dotnet test --project tests/Bosun.Core.Tests --filter-class "*OptimisationFrameworkTests"` and `bosun run -- dotnet test --project tests/Bosun.Panel.Tests --filter-class "*OptimisationFrameworkTests"` exit 0, and fail if the change is reverted.

> **Verified:** 2026-11-19 | §2 | 3a06397791d88697c5b3cdda63f4c27c36d79c30
> **Review:** `docs/reviews/d05-t02-s2.json`

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
- [ ] Register each row's Preview, Apply and Undo in the page's Ctrl+K commands (`PageBase.PaletteCommands`) on `§2`'s one eligibility result (**Added 2026-11-19** from the plan review of `D05 T02 §2`; -> XREF: D05 T02 §2): an Apply chosen before its Preview says why it can't run, and one chosen after uses the same confirmation and proposal as the row's button. Done when: a page test runs Apply from the palette before and after Preview and reads the same reason and target as the row.
- [ ] Show what a waiting row waits for and until when, in `Views/OptimiseView.xaml`. Done when: a fixture row reads its date in words.
- [ ] Draw each finding value the Optimise page can't read (a counter, a WSL cap whose `.wslconfig` can't be read or parsed) as a `NotAvailable` with why, through the `Value` style (**Added 2026-11-16** from the completion review of `D02 T03 §7`). **Corrected 2026-11-19** (completion review of `D05 T02 §1`): the cap is the configured one, read from the file with WSL never started, so WSL stopped is no reason it is unavailable; it said "with WSL stopped". Done when: a test with WSL stopped and `memory=12GB` shows 12,288 MB, and one whose `.wslconfig` holds a bad value shows "Not available" with its reason, never 0. Cheaper substitute: a blank cell.
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

## 6. Elevated change helper

An item that changes a machine setting needs an administrator, and only that one action may ask: the panel never runs elevated. Filed 2026-11-19 from the plan review of `D05 T02 §2` (gaps 2, 6, 7 and 8), which split it out of `§2`.

**Phase:** 5
**Needs:** none
**Surface:** no

**Build order:**

1. One fixed elevated command that does exactly the confirmed change, or Undo, once, with no window. Done when: the entry point runs each operation once on a disposable item.
2. The panel receives every step's result. Done when: a lost or failed helper is said, and its backup still offered.

- [ ] Add `IElevation` (`Bosun.Core`) and its Windows form in the panel: `ShellExecute` with the `runas` verb on `Bosun.Panel.exe --optimise <apply|undo> <request file>`, for an item that needs elevation only; UAC refused (error 1223) is "Not changed: you declined the administrator prompt", with nothing changed. Done when: `ElevatedHelperTests` with a fake refusal find no change, and an item that needs no elevation never calls `IElevation`. A real UAC prompt is proven in `D99 T01 §5`. Cheaper substitute: elevating the whole panel.
- [ ] Handle `--optimise` in `src/Bosun.Panel/App.xaml.cs` before `PanelStartup.Begin()` (so the launch is never forwarded to the running panel) and before any window: dispatch Apply or Undo through `§2`'s `ChangeRunner` exactly once, open no window, and exit. Done when: `ElevatedHelperTests` run the real entry point on a disposable registered item, each operation once, no window, and a declined elevated Undo leaves the target untouched. Cheaper substitute: a general elevated helper that runs what it is told.
- [ ] Trust nothing in the request: the operation, the item id (the same on the command line and in the request), the target the registered item derives (never one the request names), and the values matching the confirmed proposal exactly, refusing any mismatch or malformed request before writing. Done when: `ElevatedHelperTests` refuse an in-range value substituted for the confirmed one, an out-of-range one, a mismatched id, another target, a malformed and a missing file, each with nothing changed. Cheaper substitute: a range check alone.
- [ ] Write the helper's results where the panel finds them: each step, the outcome and the backup's name, to a result file beside the request in the initiating panel's data folder, named by the panel (the request names no write destination); the backup is written to that same folder, so a panel relaunched later finds it, even when another account's administrator credentials were given. Done when: `ElevatedHelperTests` read the result and the Activity lines of a failed verify, a launch failure, and a helper that exits without a result (said as "could not be verified", the target read again and its backup offered), and a relaunched store finds the backup. Cheaper substitute: an exit code only.
- [ ] Commit: `optimise: elevated change helper`

**Test checkpoint:** `bosun run -- dotnet test --project tests/Bosun.Panel.Tests --filter-class "*ElevatedHelperTests"` exits 0, and fails if the change is reverted.

## 7. Changes made before Bosun, with honest baselines

On 2026-11-07 the pagefile moved to `C:` at 32 to 50 GB and the Discord and Creative Cloud startup entries were turned off, with backups kept only in that session's scratchpad. They are shown for what they are now, never with an invented original. Filed 2026-11-19 from the plan review of `D05 T02 §2` (gap 5), which split it out of `§2`.

**Phase:** 5
**Needs:** none
**Surface:** no

**Build order:**

1. Each earlier change's state is derived from `§1`'s diagnostics, never hard-coded. Done when: matching, drifted, missing and unreadable each read as such.
2. A baseline is saved once and kept. Done when: a later visit never replaces it.

- [ ] Derive each earlier change's state from `§1`'s facts in `src/Bosun.Core/Optimise/EarlierChanges.cs`: Applied when the setting still holds the change (the entry turned off; the pagefile configured and active as moved), Changed since when it does not (Discord turned on again; the configured pagefile not yet active until a restart, said as such), Gone when the entry is no longer there, and Not available with why when it can't be read. Done when: `EarlierChangesTests` read each of the four for each change. Cheaper substitute: three rows hard-coded as Applied.
- [ ] Save each change's baseline once, the first time it is read, as a `§2` backup with "original not known: the 2026-11-07 backup was kept only in that session's scratchpad", reopened after a restart and never replaced by a later visit. Done when: `EarlierChangesTests` read it again after reopening the store, and find it unchanged after a later read with a different value. Cheaper substitute: today's value saved on every visit.
- [ ] Tell an Undo to the baseline apart from reversing the 2026-11-07 change: restoring the baseline returns the setting to how Bosun first found it, said so, and the original before 2026-11-07 is never offered or invented. Done when: `EarlierChangesTests` offer the baseline only, and its words name it as the state Bosun first found. Cheaper substitute: claiming a backup exists from the old session.
- [ ] Commit: `optimise: changes made before Bosun`

**Test checkpoint:** `bosun run -- dotnet test --project tests/Bosun.Core.Tests --filter-class "*EarlierChangesTests"` exits 0, and fails if the change is reverted.

## Verification

- `dotnet test Bosun.slnx` passes.
- A disposable item round-trips: preview, apply, verify, undo. A real item round-trips in `D99 T01 §5`.
