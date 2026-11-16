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
| 1 | §1 | Instance scanner | D01 T01 §3, D02 T02 §3 | [ ] |
| 2 | §2 | Instances page | §1, D02 T02 §9, D02 T03 §4, D02 T03 §7 | [ ] |
| 3 | §3 | Queue page | D01 T01 §5, §2, D02 T03 §6 | [ ] |

## 1. Instance scanner

Agents are recognised by what they are, not by a window title. On WSL the existing shim reports, rather than each `wsl.exe` call scanning.

**Corrected 2026-11-16:** the section was three summary items and a checkpoint in a filter form the test platform refuses. Decomposed against the tree: the Windows logger already reads each process's working folder (`process_cwd`, `repo_for_dir` in `src/app.cpp`) and the WSL logger the same from `/proc` (`sample` in `wsl/bosun_log.py`); Claude Code's session records (`~/.claude/sessions/<pid>.json` on each side, `status` busy or idle, `procStart` the process's start as a FILETIME on Windows and `/proc/<pid>/stat` field 22 on Linux) are what `scripts/lib/run_guard.py` `session_records` reads; review marks are `build/review-in-flight/<pid>-<reviewer>.json` in a repo (`review_in_flight`); slot holders are the queue's records (`SlotRecord` in `src/core.hpp`). Defaults taken, each cheap to change later: the queue is one, kept on Windows, so only the Windows report lists jobs (both sides' holders, each with its side), and a WSL job's memory is null there, not measured from Windows; waiters are the Queue page's (`D03 T02 §3`), not instances; Antigravity's editor (`Antigravity.exe`) is listed beside VS Code as an editor of its own kind, since the Goal is every editor; an instance's memory is its own process and its descendants that are not themselves instances or jobs, so no memory is counted twice.

**Phase:** 3
**Needs:** none
**Surface:** no

**Build order:**

1. Add `bosun instances --json` to `src/app.cpp` and `wsl/bosun`. Done when: both sides report a shared shape.
2. Create `src/Bosun.Core/Instances/InstanceClient.cs`. Done when: results merge by repo.

- [ ] Add `dm::instance_kind` and `dm::find_instances` to `src/core.hpp`: a process is an instance when its image is `claude`, `codex`, `agy`, `Code` (kind `code`) or `Antigravity` (kind `antigravity`), with or without `.exe`, and its parent is not of the same kind; its memory is its own private memory and that of its descendants that are not instances or job processes. Done when: `tests/test_core.cpp` shows twelve `Code.exe` helpers as one instance with their memory summed, a `claude` under a `claude` as one, a job under a `claude` not counted in it, and a parent loop (a reused parent id) ending. Cheaper substitute: matching window titles, or counting every `Code.exe` as an editor.
- [ ] Add `dm::instance_state` to `src/core.hpp`: `working` or `idle` from a session record whose pid and process start both match (another status reads `unknown`, naming it), `reviewing` for an instance under a live review mark's process in its repo (the mark's process alive and its deadline not passed), `working` with its slot for a job holding one, and `unknown` with no evidence. Done when: `tests/test_core.cpp` covers busy, idle, an unknown status, a record whose process start differs (a reused pid), a live mark, an expired one, a holder, and no evidence at all, each to its state and its `evidence` words. Cheaper substitute: guessing Working from CPU use alone.
- [ ] Add `dm::instances_json` to `src/core.hpp`, the shared shape: `schema_version` 1, `side`, `at`, `instances` of `side`, `kind`, `name`, `pid`, `repo` (null outside a checkout), `private_mb` (null when not measured), `started_at` (null when it can't be read), `state`, `evidence`, `slot` and `command` (jobs), and `unreadable`, the processes whose folder or memory couldn't be read. Done when: a `tests/test_core.cpp` case writes a document with every field, nulls included, and escapes a repo path with a quote and a backslash.
- [ ] Add `cmd_instances` to `src/app.cpp` (`bosun instances` and `--json`): the processes from `snapshot_processes`, each instance's folder through `process_cwd` and `repo_for_dir`, its start from `GetProcessTimes`, the session records under `%USERPROFILE%\.claude\sessions`, each instance repo's review marks, and the holders from `sweep_records`; the text form is one line per instance. Done when: `tests/instances_test.ps1` starts the test sleeper copied as `claude.exe` with its working folder inside a temporary Git checkout and finds it with that repo, memory and start; reads `working` once a session record naming its pid and start says busy, and `unknown` with one naming another start; finds a job held through `bosun run` with its slot and side; and refuses nothing when the records folder is missing. Cheaper substitute: reading the logger's grouped snapshot, which has no process ids.
- [ ] Add `instances` to `wsl/bosun_log.py` with the same rules from `/proc` (name, parent, folder, start from field 22 and the boot time, private memory) and `~/.claude/sessions`, and `bosun instances` to `wsl/bosun`, which runs it from the installed `bosun_log.py`. Done when: `tests/test_bosun_log.py` builds a fixture `/proc` tree and finds its instances, repos, memory, states and starts, and `wsl -- bosun instances --json` on this machine parses as the shared shape.
- [ ] Create `src/Bosun.Core/Instances/InstanceDocs.cs` (the shape, read with source generation, refusing a missing field, an unknown kind or state, and another schema version) and `InstanceClient.cs`, which runs `bosun instances --json` and `wsl.exe -- bosun instances --json` through `ProcessRunner` and merges them by repo, a WSL path under `/mnt/<drive>/` read as that drive's Windows path. Done when: `tests/Bosun.Core.Tests/Instances/InstanceClientTests.cs` merges a captured document from each side into groups by repo with `/mnt/d/Bosun` and `D:/Bosun` as one, puts instances outside a checkout in a group of their own, keeps the Windows rows with WSL's reason when WSL is unavailable (and the reverse), and refuses a document that isn't the shape with why. Cheaper substitute: grouping by repo folder name, which joins two repos that share one.
- [ ] Commit: `instances: scanner on both sides`

**Test checkpoint:** `bosun run -- dotnet test --project tests/Bosun.Core.Tests --filter-class "*InstanceClientTests"` exits 0, as do `tests/test_core.cpp`'s instance cases, `python -m pytest tests/test_bosun_log.py -k instances` and `pwsh tests/instances_test.ps1`; it fails if the merge treats `/mnt/d/Bosun` and `D:/Bosun` as two repos, or if an instance with no session record reads anything but `unknown`.

## 2. Instances page

The direct answer to monitoring all the dev instances.

**Phase:** 3
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §4.
**Job:** see everything running, by repo, and get to it.
**Treatment:** grouped table with row actions. Cheaper substitute that fails: Task Manager's flat list.
**Chrome:** the shell's page frame.

**Build order:**

1. Create `Views/InstancesView.xaml`. Done when: rows group by repo.
2. Add actions. Done when: each works.

- [ ] Build the table per `design/SPEC.md` §4. Done when: WSL down shows Windows rows and a notice.
- [ ] Add Open folder and Open terminal here. Done when: a WSL row opens the right distro and path. Cheaper substitute: Windows paths passed to a WSL shell.
- [ ] Show a build job's queue slot on its row in `Views/InstancesView.xaml`, from `bosun status --json`. Done when: a fixture job shows "Slot 1".
- [ ] Give the Instances page the table skeleton (`LoadingShape = SkeletonShape.Table`), and with WSL stopped keep its last WSL rows on screen through `PageBase.ShowLastKnown("WSL is stopped", readAt)` (**Added 2026-11-16** from the completion review of `D02 T03 §7`, which built them on test pages). Done when: a fixture with WSL stopped reads "WSL is stopped. Showing what was read <age> ago." over the last rows, and a later refresh with WSL running clears it. Cheaper substitute: an empty WSL table, or old rows shown as current.
- [ ] Commit: `instances: page`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~InstancesViewModel` exits 0, and fails if the change is reverted.

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

## Verification

- `dotnet test Bosun.slnx` passes.
- An attended check: a real build in Windows and WSL appears in Queue and Instances.
