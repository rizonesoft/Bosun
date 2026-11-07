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
- -> XREF: D04 T03 §5 -- consumes this file
- -> XREF: D05 T01 §3 -- consumes this file
- -> XREF: D02 T03 §5 -- consumed by this file
- -> XREF: D02 T03 §7 -- consumed by this file
- -> XREF: D02 T02 §4 -- consumed by this file
- -> XREF: D04 T01 §5 -- consumes this file
- -> XREF: D04 T04 §2 -- consumes this file
- -> XREF: D07 T01 §4 -- consumes this file
- -> XREF: D06 T02 §5 -- consumes this file

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

Typed access to the core contract, with schema refusal, so pages only deal in values or a reason.

**Phase:** 3
**Needs:** none
**Surface:** no

**Build order:**

1. Create `src/Bosun.Core/Memory/MemoryClient.cs`. Done when: fixture JSON gives typed values.
2. Handle absence. Done when: a stale snapshot reads as stale.

- [ ] Read `bosun status --json`, `current-*.json` and `report --json`. Done when: fixtures from `D01 T01` parse, and an unknown schema reads as `Unsupported`. Cheaper substitute: parsing the CSV in C#.
- [ ] Mark a snapshot older than 90 seconds as stale, per side. Done when: WSL stopped shows WSL stale and Windows live. Cheaper substitute: showing 0 GB.
- [ ] Commit: `memory: typed readers`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~MemoryClient` exits 0, and fails if the change is reverted.

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
- [ ] Commit: `memory: pressure alerts`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~PressureWatch` exits 0, and fails if the change is reverted.

## 5. Overview page

This is what opens first. It only assembles other pages' state, so it is built after them and owns no data.

**Phase:** 3
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §2.
**Job:** decide in five seconds whether to start more work and what needs attention.
**Treatment:** four cards, then Needs you, then Runs. Cheaper substitute that fails: a static welcome page.
**Chrome:** the shell's page frame; Runs rows deferred to the run page's source.

**Build order:**

1. Create `Views/OverviewView.xaml` with the cards. Done when: they mirror Memory and Queue.
2. Build Needs you and Runs. Done when: each row opens its owner.

- [ ] Build the four metric cards per `design/SPEC.md` §2. Done when: they match their pages in a test using shared fakes.
- [ ] Define `INeedsYouSource` in `src/Bosun.Core`, implemented by each page's owner. Done when: each later page adds rows without changing Overview. Cheaper substitute: Overview querying tools itself.
- [ ] Build the empty state. Done when: with nothing pending, it says "Nothing needs you."
- [ ] Build the Needs you table with state word, what, where and Open in `Views/OverviewView.xaml`. Done when: Open goes to the owning page.
- [ ] Define `IRunsSource` in `src/Bosun.Core` and build the Runs table with repo, state, ready, verified and heartbeat. Done when: a fake source fills it. `D04 T03 §2` implements the source.
- [ ] Feed rail count badges from `INeedsYouSource` by page in `src/Bosun.Panel/Shell/MainViewModel.cs`. Done when: a fake row on Plan shows a count of 1 on that rail row.
- [ ] Commit: `overview: page`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~Overview` exits 0, and fails if the change is reverted.

## Verification

- `dotnet test Bosun.slnx` passes.
- A render of each page state exists under `build/renders`.
