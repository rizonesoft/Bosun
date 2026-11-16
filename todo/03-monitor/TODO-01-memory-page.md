---
schema_version: 1
id: memory-page
domain: 03-monitor
status: active
title: "TODO-01 -- Memory and overview"
---

# TODO-01 -- Memory and overview

> **Goal:** The operator sees current headroom, what uses it and the logged peaks, and is warned before pressure becomes a freeze.

> **Current state:** Measured 2026-11-07. The logger has run since 2026-11-07 and CSV logs exist. No UI exists. The 64 GB decision waits on these numbers.

## Inputs

- `design/SPEC.md` §2, §3.
- `docs/specs/2026-11-07-bosun-design.md`, problem statement.
- -> XREF: D01 T01 §4 -- consumed by this file
- -> XREF: D02 T02 §3 -- consumed by this file
- -> XREF: D03 T02 §1 -- consumed by this file
- -> XREF: D02 T03 §5 -- consumed by this file
- -> XREF: D02 T03 §7 -- consumed by this file
- -> XREF: D02 T02 §4 -- consumed by this file
- -> XREF: D07 T01 §4 -- consumes this file

## Outcome

- The Memory page shows live data and history from `D01 T01` JSON.
- A pressure toast fires below the configured floor, once per episode.

**Adjacency:** list=applicable @ D03 T01 §2; document=not-applicable (nothing carried); settings=applicable @ D03 T01 §4; reporting=applicable @ D03 T01 §3; notifications=applicable @ D03 T01 §4; permissions=not-applicable (per-user); audit=not-applicable (read-only); exchange=not-applicable (CSV logs are already files); reverse=not-applicable (read-only)

**Adjacency rationale:** Read-only pages over logged data, with one tunable threshold.

## Implementation Order

| Order | Section | Deliverable | Depends On | Status |
| ----- | ------- | ----------- | ---------- | ------ |
| 1 | §1 | Memory readers | D01 T01 §4, D02 T02 §3 | [x] |
| 2 | §2 | Memory page | §1, D02 T02 §9, D02 T03 §5, D02 T03 §7 | [ ] |
| 3 | §3 | History chart with a table equivalent | §2 | [ ] |
| 4 | §4 | Pressure alerts | §1, D02 T02 §7 | [x] |
| 5 | §5 | Overview page | §2, §4, D03 T02 §1, D03 T02 §3 | [ ] |

## 1. Memory readers

> **Started:** 2026-11-16T05:42:49Z

Typed access to the core contract, with schema refusal, so pages only deal in values or a reason.

**Phase:** 3
**Needs:** none
**Surface:** no

**Corrected 2026-11-16:** checked against the tree before the build. The two items were summaries; they are decomposed below. What the tree holds: `bosun status --json` prints the queue and memory (`dm::status_json`, `src/core.hpp:887`), `bosun report --json --days N` the logged history (`dm::report_json`, `:1100`), and each side's logger replaces its snapshot `current-windows.json` (`bosun.exe`, `dm::snapshot_json`, `:1185`) and `current-wsl.json` (`wsl/bosun_log.py:103`) in `%LOCALAPPDATA%\Bosun\logs` every interval, each with `schema_version` 1. `ProcessRunner.RunJsonAsync` (`src/Bosun.Core/ProcessRunner.cs:210`) already runs a tool and refuses an unknown `schema_version` as `Unsupported`. The decisions, each with what reversing it costs:

- The readers are `MemoryClient` in `src/Bosun.Core/Memory/`, with one record per document (`StatusDoc`, `SnapshotDoc`, `ReportDoc`) read through a source-generated `JsonSerializerContext`, and every answer a `Reading<T>`: the value, or why there is none (not installed, not started, failed with its message, an unknown schema, a file that is not one, or stale). A page never sees an exception or a default standing for a value. Reversal cost: none.
- `bosun.exe` is found by `BosunTool.Locate`: the variable `BOSUN_EXE` when set (tests, a checkout), else the installed `%LOCALAPPDATA%\Bosun\bin\bosun.exe`; one that is not there reads as not installed, with the path. Reversal cost: one method.
- A snapshot file is read as a reader of a file that is replaced whole must read it (`tests/queue_test.ps1`'s lesson of 2026-11-16): shared for delete, asked again for up to a second while it is held in a replace, and bounded to 1 MB before it is read.
- Stale is per side: a snapshot whose `at` is more than 90 seconds before the client's clock (`TimeProvider`) reads as stale, with its age, and still carries its values, so a page can show the last data with `PageBase.ShowLastKnown` (`D02 T03 §7`). A clock behind the snapshot is not stale.
- The fixtures are real output: `bosun status --json`, `bosun report --json --days 7` and both snapshots captured on this machine on 2026-11-16, with the user's profile path and name replaced, in `tests/Bosun.Core.Tests/Fixtures/memory/`.
- The checkpoint named `dotnet test Bosun.slnx --filter FullyQualifiedName~MemoryClient`, which the test platform here takes as `dotnet test --solution Bosun.slnx --filter-class "*MemoryClientTests"`.

**Corrected 2026-11-16 (plan review):** the plan review in `docs/reviews/03-monitor/D03-T01-s1.md` returned six gaps (3 P1, 3 P2), planned in below. A report of 14 days holds a record a minute and is larger than the runner's default 1 MB of kept output, so the report call keeps up to 16 MB and the status call 1 MB, each refusing more as `Invalid`. The snapshot files are not commands, so their `schema_version` is checked by the client itself, as `RunJsonAsync` checks a command's. The fixtures add, beside the captured ones, documents written to the C++ writers' rules for what this machine didn't show: null counters, no guest, `history_written` false, an incomplete report read and a WSL snapshot's own memory fields. Every reason is reached from the real failure: a file denied, a file held past the second, a program that can't start, a timeout, and a cancellation, which is thrown to the caller (the page that left), never a reading. The fake `bosun` (`Bosun.TestChild`'s `answer` mode, through `MemoryClient`'s environment for its calls) checks the exact arguments it was given before it prints a fixture, for 1, 7 and 14 days.

**Build order:**

1. Create `src/Bosun.Core/Memory/MemoryClient.cs`. Done when: fixture JSON gives typed values.
2. Handle absence. Done when: a stale snapshot reads as stale.

- [x] Add the documents `StatusDoc`, `SnapshotDoc` and `ReportDoc` and their `MemoryJson` serializer context in `src/Bosun.Core/Memory/MemoryDocs.cs`, after `dm::status_json`, `dm::snapshot_json` and `dm::report_json`. Done when: each fixture in `tests/Bosun.Core.Tests/Fixtures/memory/` reads with every field the C++ writes, and the partial ones (null counters, no guest, `history_written` false, an incomplete read, the WSL snapshot's fields) read with what is missing kept missing and every value beside it kept (**Added 2026-11-16** from the plan review). Cheaper substitute: parsing the CSV logs in C#.
- [x] Add `Reading<T>` in `src/Bosun.Core/Memory/Reading.cs`: a value with its read time and age, or the reason there is none (`NotInstalled`, `Failed`, `Unsupported`, `Invalid`, `Missing`), each with words for the page. Done when: a test sees each reason carry its words and no value, the reasons being `NotInstalled`, `Failed`, `Unsupported`, `Invalid`, `Missing` and `Unreadable` (denied, or held past its second). Cheaper substitute: null for every failure.
- [x] Add `BosunTool.Locate` in `src/Bosun.Core/Memory/BosunTool.cs`. Done when: a test sees `BOSUN_EXE` win, the installed path used otherwise, and a missing file read as not installed with its path.
- [x] Add an `answer` mode to `tests/Bosun.TestChild/Program.cs` that, given `BOSUN_FAKE_EXPECT` and `BOSUN_FAKE_ANSWER`, checks its exact arguments and prints the answer file, or exits 64 naming the arguments it got. Done when: a test of the child sees both. Cheaper substitute: a child that prints whatever it is asked for.
- [x] Read `bosun status --json` (1 MB kept) and `bosun report --json --days N` (16 MB kept) through `ProcessRunner.RunJsonAsync` in `MemoryClient`. Done when: through the fake, each fixture gives its typed document, 1, 7 and 14 days each send their own arguments, a 14-day report of a record a minute reads whole, one printing `schema_version` 2 gets `Unsupported`, one exiting 1 gets `Failed` with its stderr, one that can't start and one past its timeout each get `Failed` with why, and a cancelled call throws to its caller. Cheaper substitute: running `cmd /c bosun` and parsing text.
- [x] Read `current-windows.json` and `current-wsl.json` in `MemoryClient`, shared for delete, asked again while held, bounded to 1 MB, and refused by their own `schema_version`. Done when: a test reads each fixture; a file held with no sharing at all for 300 ms is read once it is let go, and one held for 2 s is `Unreadable`; a file denied to the reader is `Unreadable`; the file replaced by a rename while the client holds it open is replaced (the logger's replace is never blocked); a 2 MB file, malformed JSON and a missing `schema_version` are each `Invalid`, `schema_version` 2 is `Unsupported`, and a missing file is `Missing`; a cancelled read throws to its caller. Cheaper substitute: `File.ReadAllText`.
- [x] Mark a snapshot older than 90 seconds as stale, per side, from the client's clock. Done when: with WSL stopped (its snapshot 10 minutes old) a test reads WSL stale with its age and its values kept, and Windows live. Cheaper substitute: showing 0 GB.
- [x] Commit: `memory: typed readers`

**Test checkpoint:** `bosun run -- dotnet test --solution Bosun.slnx --filter-class "*MemoryClientTests"` exits 0, and fails if the stale rule, the command's schema refusal or the snapshot's schema refusal is removed, each on its own.

> **Verified:** 2026-11-16 | §1 | b68f48c5eb35ca6c2b63c9948d7321930b11d410
> **Review:** `docs/reviews/d03-t01-s1.json`

## 2. Memory page

This is the question the whole project exists to answer.

**Phase:** 3
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §3.
**Job:** see in one look how much room is left and who took the rest.
**Treatment:** cards above a sortable table. Cheaper substitute that fails: a raw dump of `bosun report`.
**Chrome:** the shell's page frame and generated `DataGrid` style.

**Build order:**

1. Create `Views/MemoryView.xaml`. Done when: cards and the by-repo table show live data.
2. Add sort and the four states. Done when: each state renders.

- [ ] Build the Windows used, WSL VM and Overlapping builds cards per `design/SPEC.md` §3. Done when: values match `bosun status --json` within a refresh. Cheaper substitute: mock numbers left in the view model.
- [ ] Build the sortable by-repo table. Done when: sorting by peak puts the highest first, with tabular numbers.
- [ ] Add a Side column to the by-repo table in `Views/MemoryView.xaml`. Done when: a fixture shows a Windows and a WSL repo with their words.
- [ ] Give the Memory page the gauge skeleton (`LoadingShape = SkeletonShape.Gauge`) and draw each value it can't read as a `NotAvailable` with why, through the `Value` style (**Added 2026-11-16** from the completion review of `D02 T03 §7`, which built them on test pages). Done when: a render of the page loading shows the gauge shape, and a test with an unreadable counter shows "Not available" with its reason, never 0. Cheaper substitute: a card skeleton, or a 0 for a counter that failed.
- [ ] Commit: `memory: page with live cards and repo table`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~MemoryViewModel` exits 0, and fails if the change is reverted.

## 3. History chart with a table equivalent

Peaks are the whole story, and a drawn chart needs an accessible equal. A small custom element costs less memory than a chart library.

**Phase:** 3
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §3.
**Job:** understand when memory ran short and whether 64 GB is justified.
**Treatment:** line chart with threshold, range picker and table equivalent. Cheaper substitute that fails: an image with no data behind it.
**Chrome:** the generated theme; one custom element.

**Build order:**

1. Create `Controls/SeriesChart.cs`. Done when: a day of points draws with the pressure line.
2. Add ranges, table and the Decision card. Done when: each range and the table show the same data.

- [ ] Draw with a `DrawingVisual` and no chart dependency. Done when: 14 days of minute data draws in under 50 ms, in a test. Cheaper substitute: adding a charting package.
- [ ] Add Show as table, and an automation summary of the lowest point. Done when: a screen reader string names lowest value and time.
- [ ] Build the Decision card from `report --json`, stating days logged. Done when: under 7 days says so and gives no recommendation. Cheaper substitute: recommending from too little data.
- [ ] Add the 24 hours, 7 days and 14 days `SegmentedControl` to `Views/MemoryView.xaml`. Done when: each range changes the data, and the table shows the same points.
- [ ] Draw the pressure line from `PanelSettings` in `Controls/SeriesChart.cs`. Done when: a threshold change moves the line with no restart.
- [ ] Commit: `memory: history chart and decision card`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~SeriesChart` exits 0, and fails if the change is reverted.

## 4. Pressure alerts

> **Started:** 2026-11-16T06:33:27Z

A warning is worth having only if it arrives once, early enough, and doesn't nag.

**Phase:** 3
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §15 (General: Pressure alert threshold, range errors beside the field) and §17 (the tray's Pressure state and its notification).
**Job:** the person is told once, early, when memory is about to run out, and sets how early.
**Treatment:** a number field with its unit in the Settings page's General group, its range error beside it; the tray's own icon and words for the state. Cheaper substitute that fails: a toast on every sample.
**Chrome:** the Settings page in the shell's page frame (`D02 T02 §5`), and the tray (`D02 T02 §7`).

**Corrected 2026-11-16:** checked against the tree before the build. Items 2 and 4 both added the threshold to Settings and are one item now; the notifier (`src/Bosun.Panel/Tray/Notifier.cs`, `NotificationType.MemoryPressure`, honouring the notification setting and the hour's pause), the tray's Pressure state with its icon and words (`TrayViewModel.Pressure`) and the scheduler (`RefreshScheduler.Register`) exist from `D02 T02 §7` and `§8`, waiting for this section's check. The finding filed for this section (`todo/findings.md`: a pressure flag that is false can't tell healthy memory from a check that failed or went stale) is taken here. The decisions, each with what reversing it costs:

- The check reads Windows' available memory from `current-windows.json` through `MemoryClient.SnapshotAsync` (`D03 T01 §1`): the logger already samples every 30 s, so the tray check starts no process. A freeze is the host's, so the floor is Windows' available memory; WSL's own pressure is the Memory page's (`D03 T01 §2`). Reversal cost: one call.
- An episode starts when a sample is below the floor and ends only when one is more than 1 GB above it (`PressureWatch.Hysteresis`), so a noisy dip around the floor is one episode and one notification. A missing, unreadable or stale snapshot decides nothing: the episode stands as it was, and the tray says the check is unavailable or stale, with the reason, in place of a healthy state.
- The floor is `PanelSettings.PressureFloorMb`, 2048 MB by default, from 512 to 65536, stored as `pressure_floor_mb`; a value outside is refused beside the field. The check reads the stored value at each run, so a saved change takes effect at the next check with no restart.
- The check is registered with the panel's `RefreshScheduler` at 30 s and started when the panel starts, window shown or in the tray (design/SPEC.md §1: in the tray, only the pressure check runs), and stopped with the scheduler at exit.
- The checkpoint named `dotnet test Bosun.slnx --filter FullyQualifiedName~PressureWatch`, which the test platform here takes as `dotnet test --solution Bosun.slnx --filter-class "*PressureWatchTests"`.

**Corrected 2026-11-16 (plan review):** the plan review in `docs/reviews/03-monitor/D03-T01-s4.md` returned five gaps (2 P1, 3 P2), planned in below. The check starts after the tray has its notification sink (`TrayController.Start`), so a pressure already present at launch is notified, and the wiring is proved through the application's own start, window shown and in the tray. Every reading that decides nothing keeps the episode as it was and says why in the tray: a snapshot missing, unreadable, invalid, of another schema, stale, or fresh with `available_mb` null. Two whole episodes give two notifications. The threshold field refuses blank, non-numeric and too-large input beside itself, and refuses the save from the page and from the question asked on leaving it; Discard puts back the stored value, and a save that fails keeps the edit while the check goes on with the floor last saved. The Settings field makes this a surface section: its normal and error states are renders.

**Build order:**

1. Create `src/Bosun.Core/Memory/PressureWatch.cs`. Done when: one notice per episode.
2. Add the threshold setting. Done when: changing it changes behaviour.

- [x] Add `PressureWatch` in `src/Bosun.Core/Memory/PressureWatch.cs`, deciding from a sample of available memory and the floor whether an episode starts, goes on or ends, with the 1 GB hysteresis, and saying when a check could not decide. Done when: a test across a noisy dip around the floor sees one start and one end, two whole episodes give two starts and two ends, a sample exactly at the floor starts nothing and one 1 MB under starts one, a sample exactly 1 GB above ends nothing and 1 MB more ends it, and an undecided check leaves the episode as it was (**Added 2026-11-16** from the plan review). Cheaper substitute: a toast on every sample.
- [x] Add `PressureFloorMb` to `PanelSettings` (`src/Bosun.Core/PanelSettings.cs`) and `PanelSettingsStore`, default 2048, kept within 512 to 65536, stored as `pressure_floor_mb`. Done when: a test saves it, reads it back from the file, sees a file without it read as 2048 and one out of range refused or brought within its limits as the store does for the pane height. Cheaper substitute: a constant in the check.
- [x] Add the Pressure alert threshold to the Settings page (`src/Bosun.Panel/Settings/SettingsPage.cs`, `SettingsView.xaml`, design/SPEC.md §15), with Save, Discard and the read-back the page's other settings have. Done when: a test changes it on the page, saves, reads it stored; a value outside 512 to 65536, a blank, a word and a number too large for a whole number are each refused beside the field, with Save refused on the page and in the question asked on leaving it; Discard puts back the stored value; a save that fails keeps the edit, says why, and the check goes on with the floor last saved; and `RenderCatalog` draws the field in its normal and error states in every theme (**Added 2026-11-16** from the plan review). Cheaper substitute: a setting nothing reads.
- [x] Run the check in the panel, `PressureCheck` in `src/Bosun.Panel/Tray/PressureCheck.cs`, registered with the `RefreshScheduler` at 30 s from `App.MakeWindow` after `TrayController.Start` has given the notifier its sink, whether the window is shown or in the tray: it reads the snapshot, gives `PressureWatch` the sample and the stored floor, sets `TrayViewModel.Pressure`, raises one `MemoryPressure` notification as an episode starts (the notifier honouring its setting and pause) and writes the start and the end to the Activity pane. Done when: a test with a fake snapshot and clock sees the icon go to Pressure with the words, one notification across the dip and one more for a second episode, each start and end written, and a floor saved meanwhile used at the next check with no restart; and a test through the application's start (`WindowStart` and `TrayController` as `App.MakeWindow` wires them), window shown and in the tray, with a snapshot already low, sees the notification raised, the check going on while the window is hidden, and stopped at exit (**Added 2026-11-16** from the plan review). Cheaper substitute: a check only while the Memory page is open.
- [x] Say in the tray when the check can't decide (`TrayViewModel`): a snapshot missing, unreadable, invalid, of another schema, or fresh with `available_mb` null gives the tooltip `memory check unavailable` with its reason, and a stale one `memory check stale`, not a healthy state (taken from `todo/findings.md`). Done when: a test sees each, before an episode and during one (the Pressure state kept, no notification raised or repeated), and a fresh readable snapshot afterwards gives the plain state again (**Added 2026-11-16** from the plan review). Cheaper substitute: a false pressure flag that looks healthy.
- [x] Commit: `memory: pressure alerts`

**Test checkpoint:** `bosun run -- dotnet test --solution Bosun.slnx --filter-class "*PressureWatchTests" --filter-class "*PressureCheckTests"` exits 0, and fails if the hysteresis is removed or the floor is not read at each check; `pwsh scripts/render-pages.ps1` draws the threshold field's normal and error states.

> **Verified:** 2026-11-16 | §4 | 6e8e08b21ebcfbfbf27630ff4dcb22ff2f7ca9b5
> **Review:** `docs/reviews/d03-t01-s4.json`

## 5. Overview page

This is what opens first. It only assembles other pages' state, so it is built after them and owns no data.

**Phase:** 3
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §2.
**Job:** decide in five seconds whether to start more work and what needs attention.
**Treatment:** four cards, then Needs you. Cheaper substitute that fails: a static welcome page.
**Chrome:** the shell's page frame.

**Build order:**

1. Create `Views/OverviewView.xaml` with the cards. Done when: they mirror Memory and Queue.
2. Build Needs you. Done when: each row opens its owner.

- [ ] Build the four metric cards per `design/SPEC.md` §2. Done when: they match their pages in a test using shared fakes.
- [ ] Define `INeedsYouSource` in `src/Bosun.Core`, implemented by each page's owner. Done when: each later page adds rows without changing Overview. Cheaper substitute: Overview querying tools itself.
- [ ] Build the empty state. Done when: with nothing pending, it says "Nothing needs you."
- [ ] Build the Needs you table with state word, what, where and Open in `Views/OverviewView.xaml`. Done when: Open goes to the owning page.
- [ ] Feed rail count badges from `INeedsYouSource` by page in `src/Bosun.Panel/Shell/MainViewModel.cs`. Done when: a fake row on Queue shows a count of 1 on that rail row.
- [ ] Give the tray what needs the operator through `INeedsYouSource` (`src/Bosun.Panel/Tray/INeedsYouSource.cs`), from the Needs you table (**Added 2026-11-15** from `D02 T02 §7`, whose tray shows the Needs you state from this source and is tested with its own). Done when: a test with two rows in the table reads the tray's Needs you state and its tooltip saying two things need you, and with none reads Normal.
- [ ] Commit: `overview: page`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~Overview` exits 0, and fails if the change is reverted.

## Verification

- `dotnet test Bosun.slnx` passes.
- A render of each page state exists under `build/renders`.
