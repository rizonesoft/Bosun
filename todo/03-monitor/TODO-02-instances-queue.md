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

> **Started:** 2026-11-16T07:55:25Z

Agents are recognised by what they are, not by a window title. On WSL the existing shim reports, rather than each `wsl.exe` call scanning.

**Corrected 2026-11-16:** the section was three summary items and a checkpoint in a filter form the test platform refuses. Decomposed against the tree: the Windows logger already reads each process's working folder (`process_cwd`, `repo_for_dir` in `src/app.cpp`) and the WSL logger the same from `/proc` (`sample` in `wsl/bosun_log.py`); Claude Code's session records (`~/.claude/sessions/<pid>.json` on each side, `status` busy or idle, `procStart` the process's start as a FILETIME on Windows and `/proc/<pid>/stat` field 22 on Linux) are what `scripts/lib/run_guard.py` `session_records` reads; review marks are `build/review-in-flight/<pid>-<reviewer>.json` in a repo (`review_in_flight`); slot holders are the queue's records (`SlotRecord` in `src/core.hpp`). Defaults taken, each cheap to change later: the queue is one, kept on Windows, so only the Windows report lists jobs (both sides' holders, each with its side), and a WSL job's memory is null there, not measured from Windows; waiters are the Queue page's (`D03 T02 §3`), not instances; Antigravity's editor (`Antigravity.exe`) is listed beside VS Code as an editor of its own kind, since the Goal is every editor; an instance's memory is its own process and its descendants that are not themselves instances or jobs, so no memory is counted twice.

**Corrected 2026-11-16 (plan review):** WSL is asked only when a distro is running (`wsl.exe --list --running --quiet` starts none), and every running distro is asked, each document naming its `distro`; a WSL job's Linux process is named in its queue record (`guest_pid`, passed by the shim), so the WSL scanner leaves the job's tree out of its agent and the client joins the two halves; a holder whose command has not started (waiting for guest memory) reads `idle`, saying so, and a job's uptime runs from its command's start; every source that could not be read is said by name (`problems`), and a process list that could not be taken fails the command rather than reporting none; each instance carries its working folder (`cwd`) apart from its repo, for §2's actions; evidence is taken in the order slot, review mark, session record, and none is `unknown`.

**Phase:** 3
**Needs:** none
**Surface:** no

**Build order:**

1. Add `bosun instances --json` to `src/app.cpp` and `wsl/bosun`. Done when: both sides report a shared shape.
2. Create `src/Bosun.Core/Instances/InstanceClient.cs`. Done when: results merge by repo.

- [ ] Add `dm::instance_kind` and `dm::find_instances` to `src/core.hpp`: a process is an instance when its image is `claude`, `codex`, `agy`, `Code` (kind `code`) or `Antigravity` (kind `antigravity`), with or without `.exe`, and its parent is not of the same kind; its memory is its own private memory and that of its descendants that are not instances or job processes. Done when: `tests/test_core.cpp` shows twelve `Code.exe` helpers as one instance with their memory summed, a `claude` under a `claude` as one, a job under a `claude` not counted in it, and a parent loop (a reused parent id) ending. Cheaper substitute: matching window titles, or counting every `Code.exe` as an editor.
- [ ] Add `dm::instance_state` to `src/core.hpp`, evidence taken in this order: a job holding a slot is `working` once its command has started and `idle` while it holds the slot waiting for guest memory (`command_started_at` null), saying so; an instance under a live review mark's process in its repo (the mark's process alive, its deadline not passed) is `reviewing`; a session record whose pid and process start both match gives `working` (busy) or `idle` (idle), another status `unknown` naming it; nothing is `unknown` with null `evidence`. Done when: `tests/test_core.cpp` covers busy, idle, an unknown status, a record whose process start differs (a reused pid), a live mark, an expired one, a mark over a busy session (`reviewing` wins), a holder running, a holder waiting for guest memory, and no evidence at all, each to its state and its `evidence` words. Cheaper substitute: guessing Working from CPU use alone.
- [ ] Add `dm::instances_json` to `src/core.hpp`, the shared shape: `schema_version` 1, `side`, `distro` (null on Windows), `at`, `instances` of `side`, `kind`, `name`, `pid`, `cwd` (null when it can't be read), `repo` (null outside a checkout), `private_mb` (null when not measured), `started_at` (a job's from its command's start; null when it can't be read), `state`, `evidence`, and for jobs `slot`, `command` and `guest_pid`; `unreadable`, the processes whose folder or memory couldn't be read; and `problems`, each source that couldn't be read (`sessions`, `review-marks`, `queue-records`) with why. Done when: a `tests/test_core.cpp` case writes a document with every field, nulls and a problem included, and escapes a path with a quote and a backslash.
- [ ] Add `--guest-pid` to `bosun.exe acquire` (`src/app.cpp`), kept in the holder's `SlotRecord` as optional `guest_pid` so an older record still reads, and pass the shim's own pid from `wsl/bosun`. Done when: `tests/test_core.cpp` reads a record with and without it, and `wsl/queue_test.sh` finds the shim's pid in its holder's record.
- [ ] Add `cmd_instances` to `src/app.cpp` (`bosun instances` and `--json`): the processes from `snapshot_processes`, each instance's folder through `process_cwd` and `repo_for_dir`, its start from `GetProcessTimes`, the session records under `%USERPROFILE%\.claude\sessions`, each instance repo's review marks, and the holders from `sweep_records`; a session record, mark or queue folder that can't be read is a `problem`, a record that vanished while read is skipped, and a process list that can't be taken exits 2 saying why; the text form is one line per instance. Done when: `tests/instances_test.ps1` starts the test sleeper copied as `claude.exe` with its working folder inside a temporary Git checkout and finds it with that repo, folder, memory and start; one outside a checkout with its folder and a null repo; reads `working` once a session record naming its pid and start says busy, and `unknown` with one naming another start; a sleeper copied as `codex.exe` under a process named by a live review mark in its repo reads `reviewing`, and `unknown` once the mark's deadline has passed; finds a job held through `bosun run` with its slot and side, its tree not counted in the agent that started it; and says a malformed session record and an unreadable records folder as problems while still listing the instances. Cheaper substitute: reading the logger's grouped snapshot, which has no process ids.
- [ ] Add `instances` to `wsl/bosun_log.py` with the same rules from `/proc` (name, parent, folder, start from field 22 and the boot time, private memory), `~/.claude/sessions` and each repo's review marks, a `bosun run` shim's tree reported as a job (`pid` the shim's, its memory, its command) and left out of its agent, and `distro` from `WSL_DISTRO_NAME`; and `bosun instances` to `wsl/bosun`, which runs it from the installed `bosun_log.py`. Done when: `tests/test_bosun_log.py` builds a fixture `/proc` tree and finds its instances, folders, repos, memory, starts and states (a busy session, a mark over a codex, an expired mark, none), a shim's job tree apart from the claude that started it, and a malformed session record said as a problem; and `wsl -- bosun instances --json` on this machine lists the running `claude` in its repo.
- [ ] Create `src/Bosun.Core/Instances/InstanceDocs.cs` (the shape, read with source generation, refusing a missing field, an unknown kind or state, and another schema version) and `InstanceClient.cs`, which runs `bosun instances --json`, asks `wsl.exe --list --running --quiet` which distros run (starting none), runs `wsl.exe -d <distro> -- bosun instances --json` in each, and merges them by repo: a WSL path under `/mnt/<drive>/` is read as that drive's Windows path, another guest path stays its distro's (two distros' same path are two repos), the original path is kept for §2's actions, and a WSL holder from the Windows document is joined to the WSL job whose `pid` is its `guest_pid` (slot from one, memory from the other). Done when: `tests/Bosun.Core.Tests/Instances/InstanceClientTests.cs` merges captured documents into groups by repo with `/mnt/d/Bosun` and `D:/Bosun` as one and two distros' `/home/x/app` as two, puts instances outside a checkout in a group of their own, joins a WSL job's halves, runs no guest command when no distro is running (its fake runner records every call) and says so beside the Windows rows, keeps the Windows rows with a distro's reason when it fails (and the reverse), carries each document's problems, and refuses a document that isn't the shape with why. Cheaper substitute: grouping by repo folder name, which joins two repos that share one.
- [ ] Commit: `instances: scanner on both sides`

**Test checkpoint:** `bosun run -- dotnet test --project tests/Bosun.Core.Tests --filter-class "*InstanceClientTests"` exits 0, as do `tests/test_core.cpp`'s instance cases, `python -m pytest tests/test_bosun_log.py -k instances` and `pwsh tests/instances_test.ps1`; it fails if the merge treats `/mnt/d/Bosun` and `D:/Bosun` as two repos, if an instance with no evidence at all (no slot, mark or session record) reads anything but `unknown`, or if a live mark or a running holder stops giving `reviewing` or `working`.

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
