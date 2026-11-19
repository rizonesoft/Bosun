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

- Earlier changes appear as they stand now (applied, changed since, gone, or not readable), each with its evidence, and with a restore to the baseline Bosun first found only where that baseline was saved and the setting can be written. **Corrected 2026-11-19** (plan review of `D05 T02 §7`): it said "as Applied with an undo", which drifted or unreadable settings can't honestly be.
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
| 5 | §5 | Pressure monitor: queued jobs throttled under pressure | §2 | [ ] |
| 6 | §6 | Elevated change helper | §2 | [ ] |
| 7 | §7 | Changes made before Bosun, with honest baselines | §2, §6 | [ ] |
| 8 | §8 | Low memory priority for listed apps during pressure | §5 | [ ] |
| 9 | §9 | Pressure automation settings, previewed before enabling | §5, §8 | [ ] |

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
- [ ] Toggle Run key entries through `§7`'s startup approval item (**Corrected 2026-11-19**, plan review of `D05 T02 §7`: the writer is `§7`'s, so the page and the earlier changes share one; it said a saved copy of the page's own), tested against a disposable key under `HKCU\Software\Bosun\Test`. Done when: undo restores the exact value, and the real Run key is untouched by tests.
- [ ] Show the three earlier changes (`§7`) as rows with their evidence, state and baseline restore (**Added 2026-11-19** from the plan review of `D05 T02 §7`): after reopening the panel, restore from the row and from Ctrl+K reaches the same target and confirmation, or gives the same refusal. Done when: a page test drives both after a reopen and reads the same target, confirmation and refusal.
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

## 5. Pressure monitor: queued jobs throttled under pressure

> **Started:** 2026-11-19T04:02:26Z

Earlier design left these waiting for real peaks. The owner chose on 2026-11-07 to build it now with automatic action off, so nothing acts until they set thresholds from `bosun report --days 7`, possible from 2026-11-14.

**Corrected 2026-11-19** against this machine before building. Each correction says what the plan said before:

- `user.slice` is the wrong target, and out of reach. Read in WSL on 2026-11-19: a shell or agent started through `wsl.exe` runs in the cgroup `/non-systemd`, outside `user.slice` altogether, and `/sys/fs/cgroup/user.slice/memory.high` is root's (9,663,676,416 bytes now, set by the root drop-in `/etc/systemd/system/user.slice.d/50-memory.conf`), which the logger, a systemd user unit (`bosun-log.service`, `wsl/install.sh`), can't write. What the logger can reach is its own user manager's subtree: `user@1000.service` has `memory` delegated (`DelegateControllers=cpu memory pids`, `wsl/enable-write-budget.sh`), and `bosun run` puts each queued job in a scope under its `app.slice` (`wsl/bosun`). So the monitor lowers `app.slice`'s `MemoryHigh` (`systemctl --user set-property --runtime`), throttling queued builds, the work that makes the peaks, and restores it after. (It said `user.slice`.)
- The thresholds and the off switch are the panel's settings (`PanelSettings`), on Windows; the WSL logger can't read them there, so the panel writes them as a small policy file into the shared logs folder, which the logger already writes to, temp-then-replace, and the logger reads it each sample. No file, or the switch off, is no action. (It said the settings alone.)
- The checkpoint named `test_core.exe pressure`, but the rules live in the WSL logger (Python) and the panel (C#): it is their tests now.

**Corrected 2026-11-19 (plan review):** ten gaps. The section held three subsystems, so it is split: the Windows memory priority is `§8` (gaps 5 and 7's app identity) and the Settings surface is `§9` (gaps 6, 7's preview, 8 and 9); this section is the WSL throttle and the policy it reads. Planned here: the trigger is Windows' available memory from `current-windows.json` against the panel's floor and its 1,024 MB recovery band (gap 1); queued jobs run in a slice of their own, so nothing else is throttled (gap 2); the original limit is kept until a verified restore, through disable, policy loss and restarts, and an operator's own change is never overwritten (gap 3); a limit is only ever lowered, with units, bounds and read-back (gap 4); and the checkpoint names the WSL step that runs the disposable-slice test (gap 10).

**Corrected 2026-11-19 (built):** `.wslconfig` holds `autoMemoryReclaim=dropcache` (the plan said absent). systemd nests a slice by the dashes in its name, so the job slice is `bosun.slice/bosun-jobs.slice`; the shim's scope path and write-budget probe follow it. The checkpoint's WSL step needs `-Wsl` (`pwsh scripts/check-all.ps1 -Wsl -Only wsl:logger-tests`). Measured from 6,674 minutes of 2026-11-12 to 2026-11-19 samples (`docs/specs/2026-11-07-bosun-design.md`): WSL gives memory back slowly and unreliably; after 38 guest drops of 1,500 MB or more, the VM fell by half the drop within two hours in 14, with a median of 13.5 minutes, so the throttle acts on Windows' own available memory and never waits on reclaim.

**Phase:** 5
**Needs:** none
**Surface:** no

**Build order:**

1. Measure what the monitor would act on. Done when: how fast WSL gives memory back is recorded, from the logs.
2. Queued jobs in their own slice, and a policy both sides read. Done when: a `bosun run` job is in `bosun-jobs.slice` and the policy reads back on both sides.
3. The throttle: lowered under Windows pressure, restored after, never stranded or loosening. Done when: every path ends restored or says why not.

- [x] Measure how quickly WSL returns memory to Windows from the logs: the WSL VM's private memory against the guest's own use in the history `bosun report` reads, over the 2026-11-12 to 2026-11-19 samples, with `.wslconfig`'s reclaim setting (`autoMemoryReclaim`, absent here: WSL's default) read and stated. Done when: the figure (how long after the guest frees memory the VM's footprint falls, and by how much) is recorded in `docs/specs/2026-11-07-bosun-design.md` with the samples it came from, or the reason the logs can't show it. Cheaper substitute: picking thresholds from memory.
- [x] Run each queued job in a slice of its own: `wsl/bosun` adds `--slice=bosun-jobs.slice` to its `systemd-run --user --scope`, so `bosun-jobs.slice` holds Bosun's jobs and nothing else, and when it runs a job without containment it says so (as it does now). Done when: `wsl/queue_test.sh` finds a job's scope under `bosun-jobs.slice`, and a sibling scope in `app.slice` outside it. Cheaper substitute: throttling `app.slice`, which holds every user service.
- [x] Add the policy to `PanelSettings` (`src/Bosun.Core/PanelSettings.cs`): the automatic-action switch, off by default, and the job limit to apply under pressure in MB, bounded (at least 1,024 MB, at most the guest's total); the floor is the panel's existing `PressureFloorMb`. Done when: `PanelSettingsStoreTests` read each back, a fresh store has the switch off, and an out-of-bound limit is refused with why. Cheaper substitute: thresholds set in code.
- [x] Write the policy for the logger as `pressure-policy.json` in the logs folder (`src/Bosun.Core/Memory/PressurePolicy.cs`): schema version, switch, floor in MB, recovery band in MB (1,024, `PressureWatch.Hysteresis`), job limit in MB; temp-then-replace, read back; written by the same code the Settings save calls. Done when: a C# test writes it through the save path and `tests/test_bosun_log.py` reads that very file (committed as a fixture by the C# test run), so the two sides agree on names and units. Cheaper substitute: a hand-built fixture on each side.
- [x] Throttle in `wsl/bosun_log.py`: the trigger is Windows' available memory from `current-windows.json` (never the guest's), entering under the floor and leaving only above the floor plus the band, as `PressureWatch` does; a stale, missing or invalid Windows sample starts nothing and keeps the state as it is. Done when: `tests/test_bosun_log.py` drives host and guest readings on opposite sides of the floor, the recovery boundary, and stale, missing and invalid host samples. Cheaper substitute: the guest's `MemAvailable`.
- [x] Apply and restore safely: read `bosun-jobs.slice`'s `MemoryHigh` first (bytes or `infinity`), only ever lower it (a limit already at or under the policy's is left alone), persist the original in the logger's state file before the first write, read back after the write and after the restore, keep the original through repeated low samples and failed restores, and clear it only after a verified restore. Switch off or policy gone while it is lowered: restore. An operator's own change since Bosun's write (the slice reads neither Bosun's value nor the original): leave it, drop the state, and say so. Every failure (no delegation, a refused write, a read-back mismatch, a failed read) is written to `pressure-status.json` with why, and sampling carries on. Done when: `tests/test_bosun_log.py` with a stand-in `systemctl` covers a tighter existing limit, `infinity`, each failure, disable, policy loss and a logger restart after throttling, and an operator change preserved; and the disposable-slice integration test lowers and restores `bosun-test.slice`'s `memory.high` for real, never skipped where it runs. Cheaper substitute: killing work under pressure.
- [ ] Commit: `machine: queued jobs throttled under pressure`

**Test checkpoint:** `pwsh scripts/check-all.ps1 -Wsl -Only wsl:logger-tests` (which runs `tests/test_bosun_log.py` in WSL, the disposable-slice lowering and restore among them) and `bosun run -- dotnet test --project tests/Bosun.Core.Tests --filter-class "*PanelSettingsStoreTests" --filter-class "*PressurePolicyTests"` exit 0, and fail if the change is reverted.

## 6. Elevated change helper

An item that changes a machine setting needs an administrator, and only that one action may ask: the panel never runs elevated. Filed 2026-11-19 from the plan review of `D05 T02 §2` (gaps 2, 6, 7 and 8), which split it out of `§2`.

**Corrected 2026-11-19 (plan review):** seven gaps, all planned below. Eligibility tells "ready after an administrator prompt" apart from a refusal, and the shared path prompts exactly once (gap 1); the fixed command names the item, resolved through one registry that derives the target and proposal itself, and the helper checks it really is elevated (gap 2); a confirmed Undo carries the exact backup and the value the person saw (gap 3); the helper writes only into the initiating panel's own data folder, verified, never one a request names (gap 4); the invocation is linked to its backup before any write, and recovery after a lost helper goes by the target read again and `§2`'s Undo rules (gap 5); the panel tracks the helper off the UI thread and reads only a whole result bound to this invocation (gap 6); helper mode is chosen before the panel's crash handlers, so a helper fault never shows a dialog or restarts the panel (gap 7).

**Phase:** 5
**Needs:** none
**Surface:** no

**Build order:**

1. One registry and one eligibility answer, elevation included. Done when: a ready elevated action prompts once through the shared path, and a stale one never.
2. One fixed, headless command that does exactly the confirmed change or Undo. Done when: the real executable runs a disposable item once, with no window, and refuses every mismatch.
3. The panel always learns what happened. Done when: a lost, failed or mismatched result is said, with recovery only where `§2` allows it.

- [ ] Add `OptimisationRegistry` (`src/Bosun.Core/Optimise/`): the one list of items by id, from which the helper and the panel both resolve an item, its target and its current proposal; a test can register a disposable item. Extend `Eligibility` with `NeedsAdministrator` (allowed once the person accepts the prompt), so the row and the palette can tell it from a refusal. Done when: `ElevatedHelperTests` resolve a registered item, refuse an unknown id, and read `NeedsAdministrator` for an eligible elevated action and a refusal for a stale one. Cheaper substitute: items constructed by each caller.
- [ ] Add `IElevation` (`Bosun.Core`) and its Windows form in the panel, called only from the shared change path for an action whose eligibility is `NeedsAdministrator`: `ShellExecute` with the `runas` verb on `Bosun.Panel.exe --optimise <apply|undo> <item id> <invocation id>`; UAC refused (error 1223) reaches Activity as "Not changed: you declined the administrator prompt". Done when: `ElevatedHelperTests` through the shared path see a confirmed eligible action call `IElevation` exactly once, a stale or unconfirmed one never, and the refusal's Activity line; an item that needs no elevation never calls it. A real UAC prompt is proven in `D99 T01 §5`. Cheaper substitute: elevating the whole panel.
- [ ] Choose helper mode first in `App` (`src/Bosun.Panel/App.xaml.cs`), before `PanelStartup` is built and its crash handlers installed: `--optimise` runs headless, with a failure path that writes its fault to the result and exits, never a message box, a restart, or a forward to the running panel. Done when: `ElevatedHelperTests` run the real executable with a disposable item whose Apply throws, with a panel instance already running, and find the exit code and the fault in the result, no dialog, no restart and the running panel not activated. Cheaper substitute: catching the fault after start-up.
- [ ] Verify the helper's own standing before acting: it checks its token is elevated (else it refuses, so `ChangeRunner`'s elevated mode is never enabled by the command line alone), and it resolves the initiating panel's data folder from the invocation, accepting only that user's `PanelPaths` folder (the profile of the user whose panel wrote the invocation, recorded with its SID), never a path the request names; it writes nothing elsewhere. Done when: `ElevatedHelperTests` refuse an unelevated run, a redirected folder and an unrelated one, and with two account contexts (the initiating user and another administrator) find the backup and result in the initiating user's folder, readable by that user. Cheaper substitute: the helper's own `PanelPaths.Data()`.
- [ ] Bind the request to what the person confirmed: the invocation file (written by the panel temp-then-replace, read back) holds the operation, the item id (the same as on the command line), and for Apply the confirmed proposal, for Undo the exact `BackupRef` and the restore value the person saw; the helper derives the target from the registry and refuses any mismatch (an in-range substituted value, another target, another backup, a changed proposal) before writing. Done when: `ElevatedHelperTests` with two valid backups for one target refuse a substituted backup and a changed target, undo by the confirmed one, and undo that Undo by the backup it produced; and refuse an in-range substituted value, an unknown id, a mismatched operation and a changed proposal. Cheaper substitute: a range check alone.
- [ ] Link the invocation to its backup before any write: the helper records, in the invocation's own result file, each step as it happens (started, backup written with its name, write returned, verified, done), each record replacing the last whole (temp-then-replace). Done when: `ElevatedHelperTests` stop the helper before the backup, after it, and after the write, each with an older backup already present, and find the panel's recovery offers Undo only for the eligible backup (by `§2`'s rules and the target read again) and otherwise says why. Cheaper substitute: an exit code only.
- [ ] Track the helper in the panel off the UI thread (the process handle), and read the result only when the helper has exited, accepting only a whole result bound to this invocation id; a running helper is "Waiting for the administrator prompt" or "Applying", a helper that exited without a result, or with a truncated, stale or unrelated one, is said and recovered as above. Done when: `ElevatedHelperTests` drive the real result reader and Activity adapter through a successful Apply and Undo, a failed verify, a missing, truncated and mismatched result, and check the step order, the final value and the recovery offered. Cheaper substitute: a canned failure message.
- [ ] Commit: `optimise: elevated change helper`

**Test checkpoint:** `bosun run -- dotnet test --project tests/Bosun.Core.Tests --filter-class "*ElevatedHelperTests"` and `bosun run -- dotnet test --project tests/Bosun.Panel.Tests --filter-class "*ElevatedHelperTests"` exit 0, and fail if the change is reverted.

## 7. Changes made before Bosun, with honest baselines

On 2026-11-07 the pagefile moved to `C:` at 32 to 50 GB and the Discord and Creative Cloud startup entries were turned off, with backups kept only in that session's scratchpad. They are shown for what they are now, never with an invented original. Filed 2026-11-19 from the plan review of `D05 T02 §2` (gap 5), which split it out of `§2`.

**Corrected 2026-11-19 (plan review):** six gaps, all planned below. A baseline is a record of its own kind, restored through a fresh confirmed proposal (gap 1); this section owns the two writers the restores need, the pagefile item (elevated, through `§6`, so this section now waits on it) and the startup approval item (which `§3` reuses) (gap 2); each historical target is identified exactly, and only a proven absence is Gone (gap 3); a baseline is saved only from a restorable reading, and a failed or damaged one is said and never silently replaced (gap 4); `§3`'s rows and palette consume this section's evidence and restore, and the file's Outcome is corrected (gap 5); the pagefile is classified on its full evidence (gap 6).

**Phase:** 5
**Needs:** none
**Surface:** no

**Build order:**

1. Each earlier change identified exactly and judged on its full evidence. Done when: every state has its evidence and reason.
2. A baseline saved once, from a restorable reading, and never silently replaced. Done when: every failed or damaged baseline is said.
3. The two writers, so a restore is real. Done when: both restore on disposable targets, read back whole.

- [ ] Identify each historical target exactly in `src/Bosun.Core/Optimise/EarlierChanges.cs`: the pagefile by the configured list, and each startup entry by its value name, hive, view and command (Discord's and Creative Cloud's as `§1` reads them now); a name that matches more than one entry, or a command replaced since, is said as ambiguous or replaced, never guessed. Done when: `EarlierChangesTests` cover a unique entry, two same-named entries in different hives, and a replaced command. Cheaper substitute: the name alone.
- [ ] Judge each from `§1`'s facts with its evidence and reason: a startup entry is Applied (turned off), Changed since (on again: a present entry with no approval record is on), Gone only on a proven absence (its key read and the value not there), or Not available (its key or approval unreadable); the pagefile is Applied when the configured list is exactly `C:\pagefile.sys 32768 51200` and it is active with no temporary fallback, Changed since for other sizes, a system-managed file, another or an extra active file, Pending restart for a configuration not yet active, Temporary for a fallback, and Gone for an empty configuration (with a still-active file said beside it). Done when: `EarlierChangesTests` assert the label, the evidence and the reason for each case. Cheaper substitute: checking whether `C:\pagefile.sys` appears.
- [ ] Save each baseline once, as its own record kind in `BackupStore` (an observed baseline, with the target, the value, when it was first read, and "original not known: the 2026-11-07 backup was kept only in that session's scratchpad"), only from a reading that can be restored (readable and unambiguous); a first visit with unreadable data saves nothing and says so; a denied write or a failed read-back keeps the state shown and says the baseline is missing, with restore disabled and why; a damaged existing baseline is said and never replaced with today's value; a later readable visit saves a baseline that is still missing. Done when: `EarlierChangesTests` cover each, and a reopened store reads the saved one unchanged after a later read with a different value. Cheaper substitute: today's value saved on every visit.
- [ ] Add the startup approval item (`src/Bosun.Core/Optimise/StartupApprovalItem.cs`, with its writer in the panel): an `IOptimisation` that reads and writes one entry's approval (by hive, view and name), elevated for the machine hive through `§6`, and restores the exact approval value or its absence; unrelated entries untouched. Done when: tests on a disposable key under `HKCU\Software\Bosun\Test` write, read back the exact value or absence, and find the neighbouring values unchanged. Cheaper substitute: deleting and recreating the value.
- [ ] Add the pagefile item (`src/Bosun.Core/Optimise/PagefileItem.cs`, with its writer in the panel): an `IOptimisation`, elevated through `§6`, that writes the whole configured list (`PagingFiles`) and reads it back complete, saying a restart is needed once the configuration reads back but is not yet active. Done when: tests on a disposable key standing in for `Memory Management` write and read back the full list, and the restart notice follows a configuration that differs from the active files. Cheaper substitute: writing one line of the list.
- [ ] Restore to a baseline through a fresh, confirmed proposal from the current value to the saved one, through `§2`'s `ChangeRunner` (with `§6` where elevated): a target already at the baseline says so; an edit made after the preview is preserved (the proposal is refused); a successful restore can itself be undone. Its words say it returns the setting to how Bosun first found it, and the original before 2026-11-07 is never offered or invented. Done when: `EarlierChangesTests` cover each with the two items on disposable targets. Cheaper substitute: claiming a backup exists from the old session.
- [ ] Commit: `optimise: changes made before Bosun`

**Test checkpoint:** `bosun run -- dotnet test --project tests/Bosun.Core.Tests --filter-class "*EarlierChangesTests"` and `bosun run -- dotnet test --project tests/Bosun.Panel.Tests --filter-class "*EarlierChangesTests"` exit 0, and fail if the change is reverted.

## 8. Low memory priority for listed apps during pressure

An app the operator lists runs at low memory priority while Windows is under pressure, so Windows trims it first, and gets its own priority back after. Filed 2026-11-19 from the plan review of `D05 T02 §5` (gaps 5 and 7), which split it out of `§5`.

**Phase:** 5
**Needs:** none
**Surface:** no

**Build order:**

1. An app is identified exactly. Done when: two executables of one file name are told apart.
2. Priority is lowered only during an episode and restored exactly after. Done when: every way an episode ends restores each process Bosun changed.

- [ ] Identify a listed app by its executable's full path, compared as Windows compares paths (case-insensitive), in `src/Bosun.Core/Optimise/ListedApps.cs`; a process is the app when its image path matches. Done when: `ListedAppsTests` match `C:\Tools\a\app.exe` and not `C:\Tools\b\app.exe`. Cheaper substitute: the file name alone.
- [ ] Lower each listed app's running processes to `MEMORY_PRIORITY_LOW` through `SetProcessInformation` (`ProcessMemoryPriority`) only while the panel's `PressureCheck` holds a pressure episode and the switch is on, recording each process's prior priority by its id and start time (so a reused id is never taken for it); a process started during the episode is lowered too. Done when: `MemoryPriorityTests` drive the real `PressureCheck` through healthy, pressure and recovery samples with a child process they start, and read its priority low only during the episode. Cheaper substitute: a working-set cleaner, rejected in the design.
- [ ] Restore each changed process's exact prior priority when the episode ends, the switch goes off, the app leaves the list, or the panel exits; the record kept in the panel's data folder (temp-then-replace) so a panel restarted after a crash restores those still running by id and start time. A process that has exited, or refuses (access denied), is said, and the others are still acted on: the outcome is partial, never all or nothing. Done when: `MemoryPriorityTests` cover each end, a reused id left alone, an inaccessible process beside an actionable one, and a restart restoring from the record. Cheaper substitute: leaving priorities low.
- [ ] Commit: `machine: low memory priority for listed apps`

**Test checkpoint:** `bosun run -- dotnet test --project tests/Bosun.Core.Tests --filter-class "*ListedAppsTests"` and `bosun run -- dotnet test --project tests/Bosun.Panel.Tests --filter-class "*MemoryPriorityTests"` exit 0, and fail if the change is reverted.

## 9. Pressure automation settings, previewed before enabling

The switch, the job limit and the listed apps on the Settings page, with what they would do shown before they are turned on. Filed 2026-11-19 from the plan review of `D05 T02 §5` (gaps 6, 7, 8 and 9), which split it out of `§5`.

**Phase:** 5
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §15 (Settings), §1.
**Job:** turn on pressure automation knowing exactly what it will do.
**Treatment:** a group on the Settings page: the switch, the job limit, the listed apps, and a preview. Cheaper substitute that fails: a checkbox with no preview.
**Chrome:** the Settings page's own form, its Save and Discard, and Ctrl+K's Save and Discard.

**Build order:**

1. The controls join the Settings form. Done when: they take part in unsaved edits, validation, Save, Discard and the keyboard.
2. Enabling is previewed, and the saved policy is the effective one. Done when: the page says what is in force on both sides.

- [ ] Add the switch, the job limit and the listed apps (add by choosing an executable, remove) to the Settings page in `src/Bosun.Panel/Settings/`, in its form: unsaved-edit marks, validation with why, Save, Discard, Ctrl+K's Save and Discard, keyboard and automation names. Done when: Settings tests drive each through the view, a failed Save keeps the edits, and Discard returns them. Cheaper substitute: settings only in the file.
- [ ] Preview before enabling: turning the switch on shows the floor, the job limit, the slice it applies to, and each listed app with the processes it matches now, and is saved only when the person confirms; cancelling saves nothing. Done when: a Settings test reads the preview, cancels (nothing saved) and confirms (saved). Cheaper substitute: enabling at once.
- [ ] Save both files as one setting: `panel.json`, then `pressure-policy.json`; a policy that fails to write is said on Save ("Saved, but the WSL logger still has the previous policy: <why>"), retried on the next start until it matches, and the page shows the policy in force as the logger reads it (`pressure-status.json`). A failed disable against a policy that was on is said loudest. Done when: Settings tests fail the policy write on a disable and read the warning, the retry on reopen, and the effective policy shown. Cheaper substitute: two saves that can disagree silently.
- [ ] Warn when the floor is under the queue's own memory floor as configured (`bosun config`, `min_avail_mb`, 6 GB by default), saying when it can't be read. Done when: Settings tests with a configured floor of 8 GB warn at 7 GB and not at 9 GB, and with the config unreadable say so. Cheaper substitute: a 6 GB constant.
- [ ] Render the group in each state and theme (`RenderCatalog`): off, previewing, on, a failed policy write. Done when: the renders are in `build/renders/` and looked at.
- [ ] Commit: `settings: pressure automation`

**Test checkpoint:** `bosun run -- dotnet test --project tests/Bosun.Panel.Tests --filter-class "*SettingsPageTests" --filter-class "*PressureSettingsTests"` exits 0, and fails if the change is reverted.

## Verification

- `dotnet test Bosun.slnx` passes.
- A disposable item round-trips: preview, apply, verify, undo. A real item round-trips in `D99 T01 §5`.
