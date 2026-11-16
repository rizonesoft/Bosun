---
schema_version: 1
id: instances-queue
domain: 03-monitor
status: active
title: "TODO-02 -- Instances and queue"
---

# TODO-02 -- Instances and queue

> **Goal:** Every agent, editor and build on Windows and WSL appears against its repo, and the queue's holders, waiters, history and limits can be seen and set.

> **Current state:** Measured 2026-11-07. The logger attributes processes to repos by working directory. `run_guard.py` already reads Claude's session records. Nothing lists instances today.

## Inputs

- `design/SPEC.md` §4, §5.
- `scripts/lib/run_guard.py` `session_records`, the logger's attribution in `src/core.hpp`.
- -> XREF: D03 T01 §5 -- consumes this file
- -> XREF: D01 T01 §3 -- consumed by this file
- -> XREF: D02 T02 §3 -- consumed by this file
- -> XREF: D02 T03 §4 -- consumed by this file
- -> XREF: D02 T03 §6 -- consumed by this file
- -> XREF: D02 T03 §7 -- consumed by this file

## Outcome

- An open Claude session on either side appears within a refresh.
- Queue limits set in the panel govern real jobs.

**Adjacency:** list=applicable @ D03 T02 §2; document=not-applicable (nothing carried); settings=applicable @ D03 T02 §3; reporting=applicable @ D03 T02 §3; notifications=not-applicable (none); permissions=not-applicable (per-user, read-only); audit=applicable @ D03 T02 §3; exchange=not-applicable (none); reverse=not-applicable (limits are re-editable)

**Adjacency rationale:** The Instances page only reads and opens: it never stops a process, because a repo's processes are that repo's to manage (owner decision, 2026-11-10). Saving the queue limits writes to Activity.

## Implementation Order

| Order | Section | Deliverable | Depends On | Status |
| ----- | ------- | ----------- | ---------- | ------ |
| 1 | §1 | Instance scanner | D01 T01 §3, D02 T02 §3 | [x] |
| 2 | §2 | Instances page | §1, D02 T02 §9, D02 T03 §4, D02 T03 §7 | [ ] |
| 3 | §3 | Queue page | D01 T01 §5, §2, D02 T03 §6 | [ ] |
| 4 | §4 | Editor windows by repo | §1 | [ ] |

## 1. Instance scanner

> **Started:** 2026-11-16T07:55:25Z

Agents are recognised by what they are, not by a window title. On WSL the existing shim reports, rather than each `wsl.exe` call scanning.

**Corrected 2026-11-16:** the section was three summary items and a checkpoint in a filter form the test platform refuses. Decomposed against the tree: the Windows logger already reads each process's working folder (`process_cwd`, `repo_for_dir` in `src/app.cpp`) and the WSL logger the same from `/proc` (`sample` in `wsl/bosun_log.py`); Claude Code's session records (`~/.claude/sessions/<pid>.json` on each side, `status` busy or idle, `procStart` the process's start as a FILETIME on Windows and `/proc/<pid>/stat` field 22 on Linux) are what `scripts/lib/run_guard.py` `session_records` reads; review marks are `build/review-in-flight/<pid>-<reviewer>.json` in a repo (`review_in_flight`); slot holders are the queue's records (`SlotRecord` in `src/core.hpp`). Defaults taken, each cheap to change later: the queue is one, kept on Windows, so only the Windows report lists jobs (both sides' holders, each with its side), and a WSL job's memory is null there, not measured from Windows; waiters are the Queue page's (`D03 T02 §3`), not instances; Antigravity's editor (`Antigravity.exe`) is listed beside VS Code as an editor of its own kind, since the Goal is every editor; an instance's memory is its own process and its descendants that are not themselves instances or jobs, so no memory is counted twice.

**Corrected 2026-11-16 (plan review):** WSL is asked only when a distro is running (`wsl.exe --list --running --quiet` starts none), and every running distro is asked, each document naming its `distro`; a WSL job's Linux process is named in its queue record (`guest_pid`, passed by the shim), so the WSL scanner leaves the job's tree out of its agent and the client joins the two halves; a holder whose command has not started (waiting for guest memory) reads `idle`, saying so, and a job's uptime runs from its command's start; every source that could not be read is said by name (`problems`), and a process list that could not be taken fails the command rather than reporting none; each instance carries its working folder (`cwd`) apart from its repo, for §2's actions; evidence is taken in the order slot, review mark, session record, and none is `unknown`.

**Corrected 2026-11-16 (review round 1):** WSL is no longer asked through `wsl.exe` at all, since a distro stopped between the list and the call was started again: each distro's logger writes `instances-wsl-<distro>.json` into the Windows log folder every sample (`write_instances` in `wsl/bosun_log.py`), and the client reads those files, a report older than 90 s being its distro's last word, not its rows; a holder's record names the distro as well as the pid (`guest_distro`), since a Linux pid is unique only within one distro, and a holder naming none is not joined; a review mark whose deadline is past a float's range is a problem, not the end of the scan.

**Phase:** 3
**Needs:** none
**Surface:** no

**Build order:**

1. Add `bosun instances --json` to `src/app.cpp` and `wsl/bosun`. Done when: both sides report a shared shape.
2. Create `src/Bosun.Core/Instances/InstanceClient.cs`. Done when: results merge by repo.

- [x] Add `dm::instance_kind` and `dm::find_instances` to `src/core.hpp`: a process is an instance when its image is `claude`, `codex`, `agy`, `Code` (kind `code`) or `Antigravity` (kind `antigravity`), with or without `.exe`, and no ancestor of it is of the same kind (**Corrected 2026-11-16:** was "its parent", which counted each sidecar Antigravity's language server starts through `cmd.exe` as an editor of its own); its memory is its own private memory and that of its descendants that are not instances or job processes. Done when: `tests/test_core.cpp` shows twelve `Code.exe` helpers as one instance with their memory summed, a `claude` under a `claude` as one, a job under a `claude` not counted in it, and a parent loop (a reused parent id) ending. Cheaper substitute: matching window titles, or counting every `Code.exe` as an editor.
- [x] Add `dm::instance_state` to `src/core.hpp`, evidence taken in this order: a job holding a slot is `working` once its command has started and `idle` while it holds the slot waiting for guest memory (`command_started_at` null), saying so; an instance under a live review mark's process in its repo (the mark's process alive, its deadline not passed) is `reviewing`; a session record whose pid and process start both match gives `working` (busy) or `idle` (idle), another status `unknown` naming it; nothing is `unknown` with null `evidence`. Done when: `tests/test_core.cpp` covers busy, idle, an unknown status, a record whose process start differs (a reused pid), a live mark, an expired one, a mark over a busy session (`reviewing` wins), a holder running, a holder waiting for guest memory, and no evidence at all, each to its state and its `evidence` words. Cheaper substitute: guessing Working from CPU use alone.
- [x] Add `dm::instances_json` to `src/core.hpp`, the shared shape: `schema_version` 1, `side`, `distro` (null on Windows), `at`, `instances` of `side`, `kind`, `name`, `pid`, `cwd` (null when it can't be read), `repo` (null outside a checkout), `private_mb` (null when not measured), `started_at` (a job's from its command's start; null when it can't be read), `state`, `evidence`, and for jobs `slot`, `command` and `guest_pid`; `unreadable`, the processes whose folder or memory couldn't be read; and `problems`, each source that couldn't be read (`sessions`, `review-marks`, `queue-records`) with why. Done when: a `tests/test_core.cpp` case writes a document with every field, nulls and a problem included, and escapes a path with a quote and a backslash.
- [x] Add `--guest-pid` to `bosun.exe acquire` (`src/app.cpp`), kept in the holder's `SlotRecord` as optional `guest_pid` so an older record still reads, and pass the shim's own pid from `wsl/bosun`. Done when: `tests/test_core.cpp` reads a record with and without it, and `wsl/queue_test.sh` finds the shim's pid in its holder's record.
- [x] Add `cmd_instances` to `src/app.cpp` (`bosun instances` and `--json`): the processes from `snapshot_processes`, each instance's folder through `process_cwd` and `repo_for_dir`, its start from `GetProcessTimes`, the session records under `%USERPROFILE%\.claude\sessions`, each instance repo's review marks, and the holders from `sweep_records`; a session record, mark or queue folder that can't be read is a `problem`, a record that vanished while read is skipped, and a process list that can't be taken exits 2 saying why; the text form is one line per instance. Done when: `tests/instances_test.ps1` starts the test sleeper copied as `claude.exe` with its working folder inside a temporary Git checkout and finds it with that repo, folder, memory and start; one outside a checkout with its folder and a null repo; reads `working` once a session record naming its pid and start says busy, and `unknown` with one naming another start; a sleeper copied as `codex.exe` under a process named by a live review mark in its repo reads `reviewing`, and `unknown` once the mark's deadline has passed; finds a job held through `bosun run` with its slot and side, its tree not counted in the agent that started it; and says a malformed session record and an unreadable records folder as problems while still listing the instances. Cheaper substitute: reading the logger's grouped snapshot, which has no process ids.
- [x] Add `instances` to `wsl/bosun_log.py` with the same rules from `/proc` (name, parent, folder, start from field 22 and the boot time, private memory), `~/.claude/sessions` and each repo's review marks, a `bosun run` shim's tree reported as a job (`pid` the shim's, its memory, its command) and left out of its agent, and `distro` from `WSL_DISTRO_NAME`; and `bosun instances` to `wsl/bosun`, which runs it from the installed `bosun_log.py`, and the logger's `write_instances`, which writes the same document to `instances-wsl-<distro>.json` beside its snapshot every sample. Done when: `tests/test_bosun_log.py` builds a fixture `/proc` tree and finds its instances, folders, repos, memory, starts and states (a busy session, a mark over a codex, an expired mark, none), a shim's job tree apart from the claude that started it, and a malformed session record said as a problem; and `wsl -- bosun instances --json` on this machine lists the running `claude` in its repo.
- [x] Create `src/Bosun.Core/Instances/InstanceDocs.cs` (the shape, read with source generation, refusing a missing field, an unknown kind or state, and another schema version) and `InstanceClient.cs`, which runs `bosun instances --json`, reads each distro's `instances-wsl-<distro>.json` from the log folder (never running `wsl.exe`, so no stopped distro is started; a report older than 90 s is stale, said, and its rows left out), and merges them by repo: a WSL path under `/mnt/<drive>/` is read as that drive's Windows path, another guest path stays its distro's (two distros' same path are two repos), the original path is kept for §2's actions, and a WSL holder from the Windows document is joined to the job of its own distro (`guest_distro`) whose `pid` is its `guest_pid` (slot from one, memory from the other). Done when: `tests/Bosun.Core.Tests/Instances/InstanceClientTests.cs` merges captured documents into groups by repo with `/mnt/d/Bosun` and `D:/Bosun` as one and two distros' `/home/x/app` as two, puts instances outside a checkout in a group of their own, joins a WSL job's halves only within the holder's distro (another distro's job of the same pid, listed first, stays its own), runs nothing but `bosun.exe` (its fake records every call), says when no distro has reported and when one's report is stale, keeps the Windows rows with a distro's reason when its report is unreadable (and the reverse), carries each document's problems, and refuses a document that isn't the shape with why. Cheaper substitute: grouping by repo folder name, which joins two repos that share one.
- [x] Commit: `instances: scanner on both sides`

**Test checkpoint:** `bosun run -- dotnet test --project tests/Bosun.Core.Tests --filter-class "*InstanceClientTests"` exits 0, as do `build/Release/test_core.exe instances`, `wsl --cd <repo> -- python3 -B tests/test_bosun_log.py` and `pwsh tests/instances_test.ps1` (**Corrected 2026-11-16:** named the core's group as `tests/test_core.cpp`'s instance cases and the Python tests through pytest, which the WSL side doesn't have; these are the commands that run them); it fails if the merge treats `/mnt/d/Bosun` and `D:/Bosun` as two repos, if an instance with no evidence at all (no slot, mark or session record) reads anything but `unknown`, or if a live mark or a running holder stops giving `reviewing` or `working`.

> **Verified:** 2026-11-16 | §1 | 743c5426035bfaa0e1b82c0ed18e7f158939df8b
> **Review:** `docs/reviews/d03-t02-s1.json`

## 2. Instances page

The direct answer to monitoring all the dev instances.

**Phase:** 3
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §4.
**Job:** see everything running, by repo, and get to it.
**Treatment:** grouped table with row actions. Cheaper substitute that fails: Task Manager's flat list.
**Chrome:** the shell's page frame.

**Corrected 2026-11-17** against the tree before building. Each correction says what the plan said before:

- The page lives in `src/Bosun.Panel/Instances/` beside `Memory/`, as every page does (it said `Views/InstancesView.xaml`).
- The queue slot comes from the instances document's own `slot`, already joined for WSL jobs by `InstanceClient.Merge` (it said "from `bosun status --json`", a second read for a field the page already has).
- A distro's stale report keeps its rows on screen. `Merge` drops them (§1, so a page never lists them as running), so `Merge` gains a way to keep them for a page that says they are old. The notice is the client's own words ("WSL (Ubuntu) last reported 4 min ago: it is stopped, or its logger is"), since a stopped logger looks the same as a stopped distro (it said "WSL is stopped").
- The checkpoint names the test classes the way this repository runs them (it said `--filter FullyQualifiedName~InstancesViewModel`, a class that would not exist).

**Build order:**

1. Read through a source the tests can fake, and keep a stale distro's rows when asked. Done when: `Merge` keeps them only when asked.
2. Build `InstancesPage` (rows, groups, words, states) and `InstancesView.xaml` (the grouped table). Done when: rows group by repo with every column in words.
3. Add Open folder and Open terminal through a launcher the tests can fake. Done when: each opens the right place on each side, and a failure is said.
4. Register the page, render each state in every theme, and run the smoke. Done when: the renders are looked at and the rail opens the page.

- [ ] Add `IInstanceSource` (`ReadAsync`) to `src/Bosun.Core/Instances/InstanceClient.cs`, implemented by `InstanceClient`, so the page reads through a seam its tests fake. Done when: the page takes `IInstanceSource` and `App.xaml.cs` passes one `InstanceClient.ForThisUser()`.
- [ ] Give `InstanceClient.Merge` a `keepStale` parameter (default false) that keeps a stale distro's rows, and add `InstancesView.Merged(keepStale)` for the page. Done when: an `InstanceClientTests` case finds a stale distro's rows only with `keepStale: true`, its holder still joined. Cheaper substitute: rows rebuilt in the page by hand, unjoined.
- [ ] Read through `PageBase.RefreshEvery(scheduler, log, RefreshScheduler.Slow, ...)` (`design/SPEC.md` §1: 30 s while visible, manual Refresh and Retry, nothing while hidden), the result applied on the UI thread. Done when: an `InstancesPageTests` case sees the loading state, then rows, then changed rows after a refresh, a failure and its Retry recovering, and no read while the page is hidden. Cheaper substitute: a page read once and never again.
- [ ] Create `src/Bosun.Panel/Instances/InstancesPage.cs`: `InstanceLine` rows (instance name and kind, repo, side as "Windows" or "WSL (Ubuntu)", state in words "Working", "Reviewing", "Idle", "Unknown" with the evidence as its tooltip, GB from `private_mb`, uptime from `started_at`, and where either is missing "Not available" with its reason (`design/SPEC.md` §1): "Not available: its memory could not be read", "Not available: its command has not started" for a job holding a slot before its command starts, "Not available: its start could not be read"), grouped by the client's own repo identity (`RepoGroup`: the repo path and, for a repo inside a distro's own file system, that distro), each group header naming both ("/home/me/app in WSL (Ubuntu)"; a Windows repo and the same folder under `/mnt/<drive>/` one group, as `Merge` makes it), instances outside any checkout under "Not in a repo". Done when: an `InstancesPageTests` case reads each column's words and each missing value's reason, two distros with the same `/home/me/app` are two groups with distinct headers, and a Windows repo with its `/mnt/c` instance is one group.
- [ ] Sort GB and Uptime by their numbers, not their words (`SortMemberPath` to the raw megabytes and seconds, "Not available" last both ways). Done when: a test sorts 2 GB against 10 GB and 5 min against 2 h, ascending and descending, an unavailable value last each time. Cheaper substitute: sorting the formatted text, where "10 GB" comes before "2 GB".
- [ ] Show a job's queue slot in its State cell ("Working, slot 1"; "Idle, slot 2: waiting for guest memory" from its evidence). Done when: a fixture job reads "Working, slot 1". Cheaper substitute: the slot in a column empty on every other row.
- [ ] Give the page its states: `LoadingShape = SkeletonShape.Table`; nothing running says so only when every side was read whole; Windows unread with WSL rows read keeps the WSL rows and says why; nothing readable is `Failed` with the reasons and Retry; the client's WSL notice shows above the table beside the Windows rows. Done when: a test per state reads its words, and the failed page's Retry reads again and recovers. Cheaper substitute: an empty table for a failure.
- [ ] Say every source that could not be read, by name, above the table, keeping the rows that were: a distro's report that is unreadable or malformed (its `SideReading` failure, which leaves `WslNotice` null), and a document's own `problems` and `unreadable` count ("Windows: 3 processes could not be read"). Done when: tests with a malformed distro report and with a Windows document carrying `problems` read each source's line beside the rows, and with zero rows the page says the scan was incomplete rather than that nothing is running. Cheaper substitute: "Nothing is running" over a scan that read nothing.
- [ ] Change the client's stale words at their source (`InstanceClient.GuestAsync`) to "WSL (Ubuntu) last reported 4 min ago: it is stopped, or its logger is", without "so its instances aren't listed", since a page now lists them as old. Done when: `InstanceClientTests` reads the new words.
- [ ] Keep each stale distro's last rows on screen, marked old on their own rows (the State cell reads "Last seen 4 min ago" with the client's why as its tooltip), and say every stale distro by name above the table with its own age; `PageBase.ShowLastKnown(<why>, <oldest stale report's time>)` only when every side read is stale. A distro that reports again is current at once while another stays stale (**Added 2026-11-16** from the completion review of `D02 T03 §7`; **Corrected 2026-11-17** by the plan review: one page-wide reason cannot say two distros of different ages). Done when: a fixture with two stale distros of different ages reads each one's line and its rows marked old, and a later refresh with one reporting again clears only that one. Cheaper substitute: an empty WSL table, or old rows shown as current.
- [ ] Create `src/Bosun.Panel/Instances/InstancesView.xaml(.cs)`: a `DataGrid` grouped by repo (group header: the repo's path and its count), columns Instance, Side, State, GB, Uptime, sortable, with an accessible name on the grid, each group and each action, and register its `DataTemplate` in the page frame. Done when: the view test finds the grid, its columns and its group headers on the drawn page. Cheaper substitute: an `ItemsControl` per repo that a screen reader reads as loose text.
- [ ] Create `src/Bosun.Panel/Instances/Opener.cs`: `OpenCommands.Folder(line)` and `Terminal(line)` build each `ProcessStartInfo` (Windows: `explorer.exe <folder>`, `wt.exe -d <folder>`; WSL: `explorer.exe \\wsl.localhost\<distro>\<path>` or the Windows path of a `/mnt/<drive>/` folder, `wt.exe wsl.exe -d <distro> --cd <path>`; without Windows Terminal, `cmd.exe /k` in the folder, or `wsl.exe -d <distro> --cd <path>`), the folder the instance's `cwd`, else its repo. An unjoined WSL holder (a Windows-reported WSL job whose distro's report was not read, `InstanceRow.Distro` null) opens in its `GuestDistro` only when that is known and its path is a Linux path; otherwise its actions are disabled with "Its WSL distro's report could not be read". An `ILauncher` starts them. Done when: `OpenCommandsTests` reads each command for a Windows row, a WSL row in its own file system and one under `/mnt/c`, an unjoined WSL holder with and without its `GuestDistro`, and none is built from a Windows path given to a WSL shell. Cheaper substitute: Windows paths passed to a WSL shell.
- [ ] Add the row actions Open folder and Open terminal (row buttons named for their instance, and page commands for the selected row in Ctrl+K), disabled with "Its folder couldn't be read" when neither `cwd` nor repo is known, and with "The folder is no longer there" when it is gone; with no row selected the palette commands are absent; a selected row that a refresh removes leaves nothing selected rather than another row; without Windows Terminal the terminal falls back as `OpenCommands` says. Every launch goes through `ActivityLog.Record`, an INFO line when it starts and a FAIL with why and a toast when it does not (`design/SPEC.md` §1: every panel action writes a line). Done when: tests run each action through a fake `ILauncher` from its row button and from the palette (`ShellViewModel.Palette`, the command run by name), see its Activity line, the failing launch's FAIL and toast, the fallback without Windows Terminal, the missing folder's refusal, no commands with nothing selected, and the selection cleared when its row goes.
- [ ] Render the page in each state in every theme: data (both sides, a job with its slot), WSL notice, a stale distro, a malformed distro report, Windows failed with WSL rows, everything failed with its Retry, empty, loading. Done when: the renders are in `RenderCatalog` and looked at.
- [ ] Commit: `instances: page`

**Test checkpoint:** `bosun run -- dotnet test --project tests/Bosun.Panel.Tests --filter-class "*InstancesPageTests" --filter-class "*OpenCommandsTests"` and `bosun run -- dotnet test --project tests/Bosun.Core.Tests --filter-class "*InstanceClientTests"` exit 0, and fail if the slot, the grouping or the WSL command is reverted.

## 3. Queue page

Slots, waiters with a reason, history and limits, all from `D01 T01`.

**Phase:** 3
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §5.
**Job:** see why a build is waiting, and change the limits.
**Treatment:** three tables and a form. Cheaper substitute that fails: limits that only apply to the panel.
**Chrome:** the shell's page frame and `Field` styles.

**Build order:**

1. Create `Views/QueueView.xaml`. Done when: live holders and waiters show.
2. Add history and the limits form. Done when: Save changes real behaviour.

- [ ] Show each waiter's reason, derived from status JSON. Done when: a memory-gated job says it needs 6 GB and how much is free.
- [ ] Show job history from `jobs-*.jsonl`. Done when: the last 200 appear, newest first, with exit codes.
- [ ] Add the limits form through `bosun config set`. Done when: a bad value shows beside its field and the file doesn't change, and a saved slot count governs a real job. Cheaper substitute: the panel writing `config.json` directly.
- [ ] Give the tray's Queue line its words through `IQueueSummary` (`src/Bosun.Panel/Tray/IQueueSummary.cs`) (**Added 2026-11-15** from `D02 T02 §7`, whose menu shows "Queue: Not available" until the queue can be read). Done when: a test with one job running and two waiting reads the line as that in words, and an unreadable queue reads Not available with the reason in the Activity pane. Cheaper substitute: a count of 0 for a queue that could not be read.
- [ ] Commit: `queue: page and limits`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~QueueViewModel` exits 0, and fails if the change is reverted.

## 4. Editor windows by repo

An editor is one process tree however many windows it has open, and the scanner (§1) puts it in the repo of the folder it was launched from, so a VS Code with windows on three repos shows in none of them, or in the wrong one. Each window's folder is in the editor's own state, never in its processes. Filed 2026-11-16 by the completion review of `D03 T02 §1` (`docs/reviews/03-monitor/D03-T02-s1.md`, Part 1 item 2).

**Phase:** 3
**Needs:** none
**Surface:** no

**Build order:**

1. Find where each editor records its windows' folders. Done when: this section names the file and field for each, read from the installed versions.
2. List an editor once per open folder. Done when: an editor with windows on two repos is in both.

- [ ] Read where VS Code (`%APPDATA%\Code\User\globalStorage\storage.json`, `windowsState`), Antigravity and VS Code Server in WSL record each open window's folder, on this machine's installed versions, and write the file, field and version of each into this section. Done when: the three are named here with the version read, or one that keeps no such record is named with why. Cheaper substitute: the launch folder, which §1 already has.
- [ ] Add `dm::editor_windows` to `src/core.hpp` (and its mirror in `wsl/bosun_log.py`), reading that state into one folder per open window, a remote or unsaved window kept as no folder. Done when: `tests/test_core.cpp` and `tests/test_bosun_log.py` read a fixture state of two windows on two repos, one empty window and a malformed file (a `problems` entry, never a crash).
- [ ] List an editor in `cmd_instances` (`src/app.cpp`) and the WSL scanner once per window folder, each row its window's `cwd` and `repo`, its memory on the first row only so no memory is counted twice. Done when: `tests/instances_test.ps1` with a fixture state file for its editor copy finds it in both repos, launched from outside both.
- [ ] Group an editor's rows by repo in `InstanceClient.Merge` (`src/Bosun.Core/Instances/InstanceClient.cs`), the same pid in two groups. Done when: `InstanceClientTests` shows one editor in two repo groups with its memory once.
- [ ] Commit: `instances: an editor in every repo it has open`

**Test checkpoint:** `bosun run -- dotnet test --project tests/Bosun.Core.Tests --filter-class "*InstanceClientTests"` exits 0, as do `build/Release/test_core.exe instances` and `pwsh tests/instances_test.ps1`; it fails if an editor with windows on two repos is listed in only one.

## Verification

- `dotnet test Bosun.slnx` passes.
- An attended check: a real build in Windows and WSL appears in Queue and Instances.
