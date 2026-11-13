---
schema_version: 1
id: core-contract
domain: 01-core
status: active
title: "TODO-01 -- The core data contract"
---

# TODO-01 -- The core data contract

> **Goal:** `bosun` and both loggers publish versioned JSON for everything the panel shows, so no consumer parses human text or growing CSV files.

> **Current state:** Measured 2026-11-07. `src/app.cpp:246` `cmd_status` prints three human lines (slots busy, available, logger). `cmd_report` at `src/app.cpp:471` prints text. A mutex-based slot says nothing about its holder. Logs are CSV per side per day.

## Inputs

- `docs/specs/2026-11-07-bosun-design.md`, the build queue and logger.
- `src/app.cpp`, `src/logger_main.cpp`, `wsl/bosun`, `wsl/bosun_log.py`.
- -> XREF: D03 T01 §1 -- consumes this file
- -> XREF: D03 T02 §1 -- consumes this file
- -> XREF: D00 T03 §10 -- consumed by this file

## Outcome

- `bosun status --json` names each slot's holder and each waiter, on either side.
- A job log and live snapshot exist, and limits live in one config file with defined precedence.

**Adjacency:** list=applicable @ D01 T01 §3; document=not-applicable (no carried documents); settings=applicable @ D01 T01 §5; reporting=applicable @ D01 T01 §4; notifications=not-applicable (the panel owns notices); permissions=not-applicable (per-user files, no roles); audit=applicable @ D01 T01 §2; exchange=not-applicable (the files are the exchange); reverse=not-applicable (records are append-only)

**Adjacency rationale:** This domain only adds reporting. The queue's guarantees (a dead owner frees its slot, no daemon) are unchanged, and records never gain authority over the mutexes.

## Implementation Order

| Order | Section | Deliverable | Depends On | Status |
| ----- | ------- | ----------- | ---------- | ------ |
| 1 | §1 | Slot holder and waiter records | D00 T03 §10 | [ ] |
| 2 | §2 | Job history log | §1 | [ ] |
| 3 | §3 | status --json | §1 | [ ] |
| 4 | §4 | report --json and logger snapshots | §2 | [ ] |
| 5 | §5 | One config file for queue limits | §3 | [ ] |

## 1. Slot holder and waiter records

A mutex can't name its owner. Holders and waiters publish a small record beside the mutex. The mutex stays the only authority, and a stale record is ignored.

**Corrected 2026-11-13:** checked against the tree before the build. The slot mutexes are per queue namespace (`Local\<ns>.Slot.N`, `open_slot` in `src/app.cpp`), and `tests/queue_test.ps1` runs in a namespace of its own, so the records go per namespace too (`%LOCALAPPDATA%\Bosun\run\<ns>\`); otherwise a test's holders would read as the machine's. The WSL shim (`wsl/bosun`) holds a slot through a `bosun.exe acquire` it keeps on a pipe, so that process writes the record, and the shim passes the command beside the repo and side, since the record names all three. A Windows job's repo is the nearest folder above its working directory holding `.git` (`src/core.hpp:99`, as the logger attributes a process).

**Phase:** 1
**Needs:** none
**Surface:** no

**Build order:**

1. Write records in `cmd_run` and `cmd_acquire`. Done when: a holder's record exists for as long as it holds the slot.
2. Read them with staleness checks. Done when: a killed job's record is ignored and cleaned up.

- [ ] Write `%LOCALAPPDATA%\Bosun\run\<ns>\slot-N.json` and `wait-<pid>.json` with `pid`, process start time, command, repo, side and start time, atomically, in `src/app.cpp`. Done when: both files appear and disappear with a real job. Cheaper substitute: treating a record as the lock.
- [ ] Validate a record against the live process and start time in `src/core.hpp`. Done when: a record of a dead or reused pid reads as absent, and a truncated record does not crash. Cheaper substitute: trusting the pid alone.
- [ ] Pass repo, side and command from `wsl/bosun` through to `bosun.exe acquire` (`--repo`, `--side`, `--label`). Done when: a WSL job's record says `wsl`, names the Linux repo path and its command.
- [ ] Add cases to `tests/test_core.cpp` and `tests/queue_test.ps1`. Done when: killed holder, reused pid and malformed record are all covered.
- [ ] Commit: `core: publish slot holders and waiters`

**Test checkpoint:** `bosun run -- pwsh scripts/build.ps1` then `build\Release\test_core.exe records` exits 0, and fails if the change is reverted.

## 2. Job history log

The Queue page and the overlap analysis both need to know what ran when. One file per day, kept as long as the memory logs.

**Phase:** 1
**Needs:** none
**Surface:** no

**Build order:**

1. Append on job end in `src/app.cpp`. Done when: each finished job adds one line.
2. Prune with the existing retention. Done when: files older than 14 days go.

- [ ] Append JSON lines to `%LOCALAPPDATA%\Bosun\logs\jobs-YYYY-MM-DD.jsonl` with command, repo, side, slot, waited, ran, exit code and whether it timed out into the queue. Done when: a job that crashes still leaves a line, written by the waiting parent. Cheaper substitute: only recording successes.
- [ ] Prune old job logs in `src/logger_main.cpp`. Done when: a test with an old file sees it removed.
- [ ] Add a concurrency case to `tests/queue_test.ps1`. Done when: six parallel jobs give six parseable lines.
- [ ] Commit: `core: job history log`

**Test checkpoint:** `pwsh tests\queue_test.ps1` exits 0, and fails if the append is removed.

## 3. status --json

This is the panel's main read, and must be cheap to call every five seconds.

**Phase:** 1
**Needs:** none
**Surface:** no

**Build order:**

1. Add `--json` to `cmd_status` at `src/app.cpp:269` (**Corrected 2026-11-13:** it said 246). Done when: it prints one versioned object.
2. Add the WSL shim's forwarding. Done when: the same output comes from either side.

- [ ] Emit `schema_version`, slots with holders, waiters, available and total memory, and logger state in `src/app.cpp`. Done when: the output parses and a test asserts each field. Cheaper substitute: changing the existing text output.
- [ ] Forward the flag in `wsl/bosun`, adding guest memory. Done when: `wsl -- bosun status --json` includes guest available and total.
- [ ] Add cases to `tests/test_core.cpp` for JSON escaping of commands with quotes and paths. Done when: a command holding quotes and backslashes round-trips. Cheaper substitute: string concatenation with no escaping.
- [ ] Commit: `core: status --json`

**Test checkpoint:** `bosun run -- pwsh scripts/build.ps1` then `build\Release\test_core.exe status_json` exits 0, and fails if the change is reverted.

## 4. report --json and logger snapshots

History is a query over CSV that `report` already joins. Live data should not need a CSV read at all.

**Phase:** 1
**Needs:** none
**Surface:** no

**Build order:**

1. Add `--json` to `cmd_report`. Done when: peaks, overlaps and per repo numbers come back as one object.
2. Write `current-windows.json` and `current-wsl.json` per sample. Done when: each is replaced atomically every 30 seconds.

- [ ] Emit per-minute series, per-repo peak and private memory, overlap count and days covered from `cmd_report` at `src/app.cpp:498` (**Corrected 2026-11-13:** it said 471). Done when: a fixture CSV gives the expected numbers. Cheaper substitute: the panel re-implementing the join.
- [ ] Write the snapshot in `src/logger_main.cpp` with temp-then-rename. Done when: a reader polling during writes never sees a partial file. Cheaper substitute: writing in place.
- [ ] Write the same format from `wsl/bosun_log.py` to the shared logs folder. Done when: `tests/test_bosun_log.py` asserts the format and atomic rename.
- [ ] Commit: `core: report --json and live snapshots`

**Test checkpoint:** `bosun run -- pwsh scripts/build.ps1` then `build\Release\test_core.exe report_json` exits 0, and fails if the change is reverted.

## 5. One config file for queue limits

The panel's Limits form needs a place to write. Flags and environment keep winning, so existing scripts behave the same.

**Phase:** 1
**Needs:** none
**Surface:** no

**Build order:**

1. Read `%LOCALAPPDATA%\Bosun\config.json` in `src/app.cpp`. Done when: precedence is flag, then environment, then file, then default.
2. Add `bosun config get|set` with range checks. Done when: bad values are refused without changing the file.

- [ ] Parse `slots`, `min_avail_mb`, `max_wait` and `min_guest_mb` in `src/core.hpp`. Done when: each precedence level is tested, and a malformed file warns and uses defaults. Cheaper substitute: a malformed file aborting every build.
- [ ] Add `config` to the command dispatch at `src/app.cpp:535` (**Corrected 2026-11-13:** it said a command table at 511), writing atomically. Done when: `bosun config set slots 0` exits nonzero and changes nothing.
- [ ] Read the same limits in `wsl/bosun` through `bosun.exe config get`. Done when: a WSL job honours a file-set guest floor.
- [ ] Commit: `core: config file for limits`

**Test checkpoint:** `bosun run -- pwsh scripts/build.ps1` then `build\Release\test_core.exe config` exits 0, and fails if the change is reverted.

## Verification

- `build\Release\test_core.exe` and `pwsh tests\queue_test.ps1` pass.
- `wsl -- python3 -B tests/test_bosun_log.py` passes.
