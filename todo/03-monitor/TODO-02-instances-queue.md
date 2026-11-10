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

**Phase:** 3
**Needs:** none
**Surface:** no

**Build order:**

1. Add `bosun instances --json` to `src/app.cpp` and `wsl/bosun`. Done when: both sides report a shared shape.
2. Create `src/Bosun.Core/Instances/InstanceClient.cs`. Done when: results merge by repo.

- [ ] List `claude`, `codex`, `agy`, `code` and queued jobs with repo, memory and start time, using the logger's attribution in `src/core.hpp`. Done when: a test process with a set working directory is attributed correctly. Cheaper substitute: matching window titles.
- [ ] Add the same in `wsl/bosun_log.py`. Done when: `wsl -- bosun instances --json` matches the schema, and a fixture test passes.
- [ ] Mark state from session records and in-flight marks, or Unknown. Done when: state with no evidence reads Unknown. Cheaper substitute: guessing Working from CPU use alone.
- [ ] Commit: `instances: scanner on both sides`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~InstanceClient` exits 0, and fails if the change is reverted.

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
- [ ] Commit: `queue: page and limits`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~QueueViewModel` exits 0, and fails if the change is reverted.

## Verification

- `dotnet test Bosun.slnx` passes.
- An attended check: a real build in Windows and WSL appears in Queue and Instances.
