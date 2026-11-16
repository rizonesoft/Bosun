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
| 2 | §2 | Memory page | §1, D02 T02 §9, D02 T03 §5, D02 T03 §7 | [x] |
| 3 | §3 | History chart with a table equivalent | §2 | [ ] |
| 4 | §4 | Pressure alerts | §1, D02 T02 §7 | [x] |
| 5 | §5 | Overview page | §2, §4, D03 T02 §1, D03 T02 §3 | [ ] |
| 6 | §6 | Decision card and the report's evidence for it | §3 | [ ] |

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

> **Started:** 2026-11-16T20:37:22Z

This is the question the whole project exists to answer.

**Corrected 2026-11-16:** checked against the tree before the plan review. The page is `src/Bosun.Panel/Memory/MemoryPage.cs` (a `PageBase`) and its view `src/Bosun.Panel/Memory/MemoryView.xaml`, beside the Settings page's folder, not a `Views` folder; `WindowStart.NewShell` gives it to the rail by its key, `memory`, in place of its placeholder, with the panel's one `RefreshScheduler` and a `MemoryClient` (`D03 T01 §1`). What each part reads, as the mockup draws it: **Windows used** is the Windows snapshot's `total_mb` less `available_mb`, its note the committed memory (`commit_mb`); **WSL VM** is what the VM costs Windows (the Windows snapshot's `wsl_vm.private_mb`, "Not running" when it isn't), its note the guest's cap (the WSL snapshot's `total_mb`); **Overlapping builds** is today's most builds at once from `bosun report --json --days 1` (`builds.max_overlap`), its note today's lowest headroom (`windows.min_available_mb`). The **By repo** table joins each side's snapshot (repo, side, private GB now, processes now) with today's peaks from the report (`repos[].peak_private_mb`), sortable by Private GB, Peak GB and Processes, highest private first, with tabular figures (`Cell.Number`). The snapshots are read every 5 s (design/SPEC.md §1, memory) and the report every 30 s, each on the scheduler and only while the page is shown. A Windows snapshot that can't be read is the page's failed state with the reason and Retry; one gone stale shows the last data with its age (`ShowLastKnown`); a WSL snapshot or report that can't be read makes only its own values "Not available" with the reason. The history chart and the decision card are `§3` and later sections. Tests: `tests/Bosun.Panel.Tests/Memory/MemoryPageTests.cs` over snapshot files and a fake `bosun report`, and renders of each state; the checkpoint takes the test platform's filter form.

**Corrected 2026-11-16 (plan review):** the page is drawn through an implicit template for `MemoryPage` added to `Shell/PageFrame.xaml`, proven by going to it in the real shell. "Today" is the last 24 hours, which is what `report --days 1` reads (`src/app.cpp`: the 86,400 seconds before now), and every such figure says so ("most at once in the last 24 hours"). Rows join on side and repo, so a repo on both sides is two rows with their own peaks; a row with no peak yet says "Not in the history yet". WSL: when the Windows snapshot says the VM is not running, the WSL card says "Not running" and no WSL rows are shown; a WSL snapshot gone stale keeps its rows with their age said beside them, the Windows rows untouched. History that is not whole is said where its figures are: a report with `read.complete` false says how many files could not be read and rows were rejected beside the overlap card and the peaks, and a Windows snapshot with `history_written` false says history is not being written. The two reads are two refreshes on the scheduler, both started when the page is shown and stopped (their work cancelled, a late result dropped) when it is hidden: `PageBase` gains a second registration (`RefreshAlso`) whose runs join the page's Refresh, Retry and the palette's Refresh, and a slow report never holds the snapshots back. The page is never empty, since memory always has a value: with no repo running and none in the last 24 hours the table says so in a line of its own, and fills when one appears. Every card's value is checked against the snapshot or report field it comes from; the table sorts each of its three number columns both ways, numerically, and keeps the chosen sort when it is read again; each failed source recovers when it can be read again.

**Corrected 2026-11-16 (while building):** the page reads through `IMemorySource` (`src/Bosun.Core/Memory/MemoryClient.cs`), which `MemoryClient` implements, so its tests hand it documents. The report names the Windows side `win` and the snapshot `windows` (`src/core.hpp`), so peaks are matched by side read as one; a repo is shown by its folder's name, its whole path kept for the join. A card's value is a `Metric` with its own template, so a `NotAvailable` takes the theme's (the render showed a fixed template drawing it as large text). The page is given to the rail by `WindowStart.NewShell` with the panel's scheduler and one `MemoryClient`, which the tray's pressure check now shares.

**Phase:** 3
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §3.
**Job:** see in one look how much room is left and who took the rest.
**Treatment:** cards above a sortable table. Cheaper substitute that fails: a raw dump of `bosun report`.
**Chrome:** the shell's page frame and generated `DataGrid` style.

**Build order:**

1. Create `src/Bosun.Panel/Memory/MemoryView.xaml`. Done when: cards and the by-repo table show live data.
2. Add sort and the four states. Done when: each state renders.

- [x] Build the Windows used, WSL VM and Overlapping builds cards per `design/SPEC.md` §3. Done when: each card's value and note match the field of the snapshot or report it is read from, in a test over those files and a fake report. Cheaper substitute: mock numbers left in the view model.
- [x] Build the sortable by-repo table. Done when: each of Private GB, Peak GB and Processes sorts numerically both ways (10 after 9), the chosen sort survives a refresh, and the figures are tabular.
- [x] Add a Side column to the by-repo table in `src/Bosun.Panel/Memory/MemoryView.xaml`. Done when: a fixture shows a Windows and a WSL repo with their words.
- [x] Give the Memory page the gauge skeleton (`LoadingShape = SkeletonShape.Gauge`) and draw each value it can't read as a `NotAvailable` with why, through the `Value` style (**Added 2026-11-16** from the completion review of `D02 T03 §7`, which built them on test pages). Done when: a render of the page loading shows the gauge shape, and a test with an unreadable counter shows "Not available" with its reason, never 0. Cheaper substitute: a card skeleton, or a 0 for a counter that failed.
- [x] Commit: `memory: page with live cards and repo table`

**Test checkpoint:** `bosun run -- dotnet test --project tests/Bosun.Panel.Tests --filter-class "*MemoryPageTests"` exits 0, and fails if an unreadable counter is shown as 0.

> **Verified:** 2026-11-16 | §2 | 96dbd7f526f0bc32bd20bdb8a24f037f3c1e2f0c
> **Review:** `docs/reviews/d03-t01-s2.json`

## 3. History chart with a table equivalent

> **Started:** 2026-11-16T21:35:21Z

Peaks are the whole story, and a drawn chart needs an accessible equal. A small custom element costs less memory than a chart library.

**Corrected 2026-11-16:** checked against the tree before the plan review. Paths: `src/Bosun.Panel/Controls/SeriesChart.cs` and `src/Bosun.Panel/Memory/MemoryView.xaml` (the Memory page of `§2`). The points are the report's `series` (`win_available_mb` by minute) from `bosun report --json`: the 24 hours from the page's day report (read every 30 s), 7 and 14 days from a 14-day report read every 5 minutes while the page is shown, a third read `PageBase` gains beside `RefreshAlso` (a page may hold up to three). The chart and its table draw the same points: the lowest available in each bucket (5 minutes for 24 hours, 30 minutes for 7 days, an hour for 14 days), so the table's rows are the chart's points, never 20,000 minutes. The pressure line is the stored threshold (`PanelSettings.PressureFloorMb`), moving when Settings saves a new one. The range is a `SegmentedControl` in the chart card's header, and Show as table switches the card between the chart and its table. The Decision card reads the 14-day report. There is no written rule for the 64 GB question, so the default taken, and recorded here, is: with fewer than 7 days logged (`days_covered`) it says how many and recommends nothing; with 7 or more it recommends more memory when Windows was below the pressure line for any minutes (`windows.low_samples`), when committed memory reached 90% of its limit (`max_commit_mb` against `commit_limit_mb`), or when WSL had out-of-memory kills (`wsl.max_oom_kills`), and otherwise says 32 GB has been enough so far, with each figure it rests on beside it. Changing that rule is a change to one function and its tests. Tests: `tests/Bosun.Panel.Tests/Controls/SeriesChartTests.cs` and `tests/Bosun.Panel.Tests/Memory/`; the checkpoint takes the test platform's filter form.

**Corrected 2026-11-16 (plan review):** the Decision card moves to its own section, `§6`, with the evidence it needs added to the report first: the plan review found `windows.low_samples` counted against a fixed 2048 MB rather than the saved threshold, `wsl.max_oom_kills` a cumulative counter rather than kills in the period, committed memory and its limit maximised apart, and `days_covered` counting days with no Windows samples, so no honest verdict can rest on today's report. This section is the chart and its table. History has its own states, apart from the live cards: before the first report the chart card says it is reading the history; a report that can't be read says why with Retry, the last drawn points kept with their age; an incomplete read says so beside the chart; no Windows samples in the range says "No history in this range yet" with what to do (leave the logger running); switching range while a read is pending shows the new range's state, never the old range's points. The series is plotted against elapsed time: a bucket with no Windows sample is missing, the line breaks across it, and nothing joins two measured points over a gap; the y axis is GB available, the x axis the range's local time, the pressure line labelled with its value ("2.0 GB pressure line"), and each point is the lowest minute in its bucket, at that minute's time. Show as table and Show as chart are buttons, reachable by keyboard and listed for Ctrl+K (`PageBase.Commands`); the table's columns are Time and Lowest available, a missing bucket "No sample"; the chart's automation name says the range, the lowest point and its time, and the pressure line, and follows a range change. `PageBase` holds up to three refreshes, each started, stopped (cancelled, a late result dropped) and refreshed with the page and run by Refresh, Retry and the palette's Refresh, a slow report never holding the snapshots. Tests assert the bucket minima and range bounds against points worked out apart, the drawn line's points and the pressure line's place, the toggle and the automation name; render cases cover the chart, the table, a gap, no history and a failed report in every theme; removing a behaviour fails its test.

**Phase:** 3
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §3.
**Job:** understand when memory ran short and whether 64 GB is justified.
**Treatment:** line chart with threshold, range picker and table equivalent. Cheaper substitute that fails: an image with no data behind it.
**Chrome:** the generated theme; one custom element.

**Build order:**

1. Create `src/Bosun.Panel/Controls/SeriesChart.cs`. Done when: a day of points draws with the pressure line.
2. Add ranges, table and the Decision card. Done when: each range and the table show the same data.

- [ ] Draw with a `DrawingVisual` and no chart dependency. Done when: 14 days of minute data draws in under 50 ms, in a test. Cheaper substitute: adding a charting package.
- [ ] Add Show as table, and an automation summary of the lowest point. Done when: a screen reader string names lowest value and time.
- [ ] ~~Build the Decision card from `report --json`, stating days logged.~~ Struck 2026-11-16 by the plan review: the report holds no honest evidence for the verdict yet. -> XREF: D03 T01 §6
- [ ] Add the 24 hours, 7 days and 14 days `SegmentedControl` to `src/Bosun.Panel/Memory/MemoryView.xaml`. Done when: each range changes the data, and the table shows the same points.
- [ ] Draw the pressure line from `PanelSettings` in `src/Bosun.Panel/Controls/SeriesChart.cs`. Done when: a threshold change moves the line with no restart.
- [ ] Commit: `memory: history chart and decision card`

**Test checkpoint:** `bosun run -- dotnet test --project tests/Bosun.Panel.Tests --filter-class "*SeriesChartTests" --filter-class "*MemoryHistoryTests"` exits 0, and fails if a gap is drawn as a joined line.

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


## 6. Decision card and the report's evidence for it

Filed 2026-11-16 by the plan review of `§3` (`docs/reviews/03-monitor/D03-T01-s3.md`), which found the report could not carry an honest answer to the 64 GB question. The card is the answer; the report first gains the evidence it needs, in the layer that owns it.

**Phase:** 3
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §3 (the Decision card), `README.md` (evidence before hardware).
**Job:** decide whether more memory is justified, from measured peaks, never from too little data.
**Treatment:** one card under the chart: a verdict only when the evidence supports one, and each figure it rests on beside it. Cheaper substitute that fails: a verdict from a fixed floor and cumulative counters.
**Chrome:** the Memory page's cards (`§2`).

**Build order:**

1. Extend `dm::aggregate` and `report_json` in `src/core.hpp`, and `bosun report` in `src/app.cpp`. Done when: the report carries the evidence below, tested in `tests/test_core.cpp`.
2. Build the card in `src/Bosun.Panel/Memory/`. Done when: every branch renders with its evidence.

- [ ] Add `--low-mb N` to `bosun report` (`src/app.cpp`), the floor `low_samples` and a new `low_minutes` count against, default the stored threshold the panel passes (`PanelSettings.PressureFloorMb`), with `low_minutes` counting distinct minutes below it, not samples. Done when: a `tests/test_core.cpp` case with a 3000 MB floor and two samples in one minute counts one minute.
- [ ] Add `windows.peak_commit_ratio` to the report, the highest committed over limit taken from the same sample, never from two maxima. Done when: a case whose limit grows mid-period finds the true peak ratio.
- [ ] Add `wsl.oom_kills_in_period`, the counter's increases within the period, a drop read as a VM restart counting from zero, and null when no two samples bound the period. Done when: cases with an unchanged positive counter (0), an increase (its size) and a restart (the kills after it) pass.
- [ ] Add `windows.days_with_samples`, the days that hold Windows memory samples, and `windows.total_mb`, the installed memory the samples report. Done when: seven days of only WSL rows count 0, and the total is read from the samples.
- [ ] Build the Decision card in `src/Bosun.Panel/Memory/MemoryView.xaml` and its rule in one function of `MemoryPage.cs` (or a class of its own), from the 14-day report: under 7 days with Windows samples it says how many and gives no verdict; an incomplete read or a null figure it rests on is said and withholds the verdict that needs it; otherwise "More memory would help" when there were minutes below the line, commit reached 90% of its limit, or WSL had out-of-memory kills in the period, else "{total} has been enough so far", each figure beside it. Done when: a test per branch (too few days, incomplete, null counter, each of the three reasons, enough) reads the verdict and its evidence, and a machine of another capacity is named by its own total.
- [ ] Render the card in each branch in every theme. Done when: the renders are in `RenderCatalog` and looked at.
- [ ] Commit: `memory: decision card and its evidence`

**Test checkpoint:** `bosun run -- dotnet test --project tests/Bosun.Panel.Tests --filter-class "*DecisionTests"` exits 0, and fails if a verdict is given from fewer than 7 days of Windows samples; `build\Release\test_core.exe` passes the report's new cases.

## Verification

- `dotnet test Bosun.slnx` passes.
- A render of each page state exists under `build/renders`.
