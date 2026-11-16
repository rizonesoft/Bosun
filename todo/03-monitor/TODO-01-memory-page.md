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
| 1 | §1 | Memory readers | D01 T01 §4, D02 T02 §3 | [ ] |
| 2 | §2 | Memory page | §1, D02 T02 §9, D02 T03 §5, D02 T03 §7 | [ ] |
| 3 | §3 | History chart with a table equivalent | §2 | [ ] |
| 4 | §4 | Pressure alerts | §1, D02 T02 §7 | [ ] |
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
- [ ] Commit: `memory: typed readers`

**Test checkpoint:** `bosun run -- dotnet test --solution Bosun.slnx --filter-class "*MemoryClientTests"` exits 0, and fails if the stale rule, the command's schema refusal or the snapshot's schema refusal is removed, each on its own.

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

A warning is worth having only if it arrives once, early enough, and doesn't nag.

**Phase:** 3
**Needs:** none
**Surface:** no

**Build order:**

1. Create `src/Bosun.Core/Memory/PressureWatch.cs`. Done when: one notice per episode.
2. Add the threshold setting. Done when: changing it changes behaviour.

- [ ] Enter pressure below the floor and leave only 1 GB above it. Done when: a fake-clock test fires once across a noisy dip. Cheaper substitute: a toast on every sample.
- [ ] Add the floor to Settings, default 2048 MB, with `PressureWatch` as its consumer. Done when: the setting reads back and drives the test.
- [ ] Run the check while in the tray at 30 second intervals, with the tray state. Done when: the icon changes to Pressure and the words say so.
- [ ] Add the Pressure alert threshold to the Settings page and to `PanelSettings` (**Added 2026-11-15** from `D02 T02 §5`, which builds each setting with the code that reads it; `design/SPEC.md` §15). Done when: a test changes the threshold on the page, reads it stored and read back, a value outside its range refused beside the field, and the alert raised at the new threshold with no restart. Cheaper substitute: a setting nothing reads.
- [ ] Commit: `memory: pressure alerts`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~PressureWatch` exits 0, and fails if the change is reverted.

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
