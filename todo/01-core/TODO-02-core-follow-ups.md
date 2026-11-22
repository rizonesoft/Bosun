---
schema_version: 1
id: core-follow-ups
domain: 01-core
status: active
title: "TODO-02 -- Core follow-ups"
---

# TODO-02 -- Core follow-ups

> **Goal:** `bosun.exe` and the WSL shim keep a job's whole record (why it ended, its exact times, its full command and folder), say what went wrong in plain words with its fix, react to a changed limit while jobs wait, and stay fast and bounded as the history grows.

> **Current state:** Measured 2026-11-22 at `b4b2a9a` by `D00 T06 §2`. `TODO-01-core-contract.md` shipped the JSON contract. Still true: `JobLine` in `src/core.hpp` keeps an exit code but no reason, `waited_s` and `ran_s` in tenths (`ended_ms` and `ran_ms` were added by `4fbe65a`, `waited` was not) and the command as one label; `wsl/bosun` cuts a label past 4096 characters; `Records::write` in `src/app.cpp` warns with a folder and an error number; `status_json` lists waiters oldest first with no stated policy and reports skipped records as a count; `prune_logs` keeps 14 days with no size budget; `cmd_report` holds every row in memory; `cmd_log` samples every 30 seconds only; `acquire_slot` reads the limits once; `cmd_config` saves one value per call; no part of this has a measured latency target.

## Inputs

- `todo/findings.md`, base lines 49 to 63 (as of `b8c4e0b3`), struck with these sections' references by `D00 T06 §2`.
- `TODO-01-core-contract.md`, which shipped the records, the job log, `status --json`, `report --json` and `config.json` these sections extend; `docs/specs/2026-11-07-bosun-design.md` for the queue's rules.
- -> XREF: D03 T02 §3 -- consumes this file

## Outcome

- A job's line in the job log says why it ended, keeps its times in milliseconds, and holds its full argument list and folder.
- Every warning `bosun.exe` prints about its records names the cause in plain words with one fix.
- A saved limit reaches jobs already waiting within one poll, and the Limits form saves all its values at once or none.
- `status --json`, `report --json` and the record publish each have a measured p95 against a target, recorded here.

**Adjacency:** all=not-applicable (the queue's own records: a job line, a slot record and a log file are written and read by bosun.exe alone, with temporary-file-then-replace writes TODO-01 already enforces)

**Adjacency rationale:** These sections extend records `TODO-01-core-contract.md` defined; they add fields and measurements, and every write keeps its existing atomic path.

## Implementation Order

| Order | Section | Deliverable | Depends On | Status |
| ----- | ------- | ----------- | ---------- | ------ |
| 1 | §1 | Job line keeps why it ended, milliseconds, the full command and folder | -- | [ ] |
| 2 | §2 | WSL shim carries the whole command, and its idle proxy is found | §1 | [ ] |
| 3 | §3 | Record warnings in plain words, and the publish cost measured | -- | [ ] |
| 4 | §4 | Status JSON states its admission order and names skipped records | -- | [ ] |
| 5 | §5 | History within a size budget, a streamed report and a burst capture | -- | [ ] |
| 6 | §6 | A saved limit reaches waiting jobs, and the Limits form saves at once | -- | [ ] |

## 1. Job line keeps why it ended, milliseconds, the full command and folder

Filed 2026-11-22 by `D00 T06 §2` from `todo/findings.md` (base lines 52 to 54, completion review of `D01 T01 §2`). `JobLine` in `src/core.hpp` records `exit_code` but not why a job ended, keeps `waited_s` and `ran_s` in tenths of a second (`ended_ms` and `ran_ms` exist since `4fbe65a`; the wait does not), and keeps the command only as a display label, so a user-started retry needs a hand-built command.

**Phase:** 1
**Needs:** none
**Surface:** no

**Build order:**

1. Record why a job ended. Done when: ordinary failure, interruption and the write-budget stop each have a reason code.
2. Keep exact times and the whole command. Done when: the wait is in milliseconds, and the argument list and folder are in the line.

- [ ] Add `end_reason` to `JobLine` in `src/core.hpp` (`exited`, `failed`, `interrupted`, `write-budget`, `shim-gone`) with one plain explanation each, set where `src/app.cpp` ends a job, as GNU Parallel's job log separates the signal (base line 52). Done when: a test in `tests/` drives each ending and reads its reason; an old line without it still reads.
- [ ] Add `waited_ms` beside `waited_s` in `JobLine` and have readers (`report --json`, the panel) prefer the millisecond fields, rounding only for display (base line 53). Done when: a test writes two jobs a quarter second apart and reads their waits and runs apart in milliseconds; lines from before still read.
- [ ] Add `argv` (the whole argument list) and `cwd` to `JobLine`, written by `bosun run` on Windows and passed by `wsl/bosun` (`acquire`) for WSL jobs, bounded in size (base line 54). An explicit `--label` is how a user keeps a command out of the history, so a job started with one stores no `argv`, only its label and `cwd` (**corrected 2026-11-22, round 1 of D00 T06 §2:** the item said "redacted as the label is", and the label redacts nothing). Done when: a test reads both back for a Windows and a WSL job; a job run as `bosun run --label fetch -- curl -H "Authorization: Bearer TEST_ONLY" https://example.invalid` leaves no `TEST_ONLY` anywhere in the job history; and no retry is ever started by Bosun itself (an explicit user action only). Cheaper substitute: parsing the label back into arguments.
- [ ] Commit: `core: a job's line keeps why it ended, milliseconds, its whole command and folder (D01 T02 §1)`

**Test checkpoint:** `pwsh scripts/check-all.ps1 -Only core-tests` exits 0 (or the green CI run of the candidate), with the three new tests failing against `JobLine` as it stands.

## 2. WSL shim carries the whole command, and its idle proxy is found

Filed 2026-11-22 by `D00 T06 §2` from `todo/findings.md` (base lines 49 and 55, completion reviews of `D01 T01 §1` and `§2`). `wsl/bosun` cuts a command past 4096 characters into its label (`label="${label:0:4090} [...]"`), so two commands that differ after that look alike; and when the shim is SIGKILLed before its command starts, WSL's `/init` interop proxy for `bosun.exe acquire` stays in poll, one idle process per kill, which `tests/queue_test.ps1` cleans as a `WORKAROUND`.

**Phase:** 1
**Needs:** none
**Surface:** no

**Build order:**

1. Carry the whole command. Done when: a command past 4096 characters reaches the record whole, with a short label beside it.
2. Find the proxy's cause. Done when: the stdio wiring that lets the proxy exit is found and used, or the reason none does is recorded.

- [ ] Pass the whole command from `wsl/bosun` to `bosun.exe acquire` through its stdin pipe or a file in the run folder, not the Windows command line, keeping `--label` as a short display label (base line 49). Done when: a WSL test runs two commands of 5000 characters that differ only at the end and reads both whole from their records.
- [ ] Reproduce the idle `/init` proxy after a SIGKILL before the command starts, then try the stdio wirings in `wsl/bosun` (stderr redirected to the pipe or `/dev/null`, stdin closed after the handshake, `exec` of the acquire) and record each result here (base line 55). Done when: one wiring leaves no idle proxy and the `WORKAROUND` cleanup in `tests/queue_test.ps1` is removed, or every wiring is recorded with why the proxy stays and the cleanup keeps its marked reason.
- [ ] Commit: `wsl: the shim carries the whole command, and the idle proxy after a kill is settled (D01 T02 §2)`

**Test checkpoint:** `pwsh tests/queue_test.ps1` exits 0 under WSL, and the 5000-character test fails with the 4096-character cut restored.

## 3. Record warnings in plain words, and the publish cost measured

Filed 2026-11-22 by `D00 T06 §2` from `todo/findings.md` (base lines 50 and 51, completion review of `D01 T01 §1`). `Records::write` in `src/app.cpp` warns that it can't publish a job's slot record with the folder and a numeric error, once per job; nothing measures what publishing adds to a job's start or what reading the records costs `bosun status`.

**Phase:** 1
**Needs:** none
**Surface:** no

**Build order:**

1. Say the cause. Done when: a full disk, a denied folder and a held file each get their own words and fix.
2. Measure. Done when: the publish cost and the read cost with ten jobs are recorded against their targets.

- [ ] Map the write's Windows error in `Records::write` (`src/app.cpp`) to plain words with one fix each: `ERROR_DISK_FULL`, `ERROR_ACCESS_DENIED` and `ERROR_SHARING_VIOLATION`, and the number for any other, keeping the once-per-job limit (base line 50). Done when: a test injects each error and reads its line once.
- [ ] Measure what publishing a record adds to a job's start and what reading them costs `bosun status` with ten jobs at once, p95 over 100 runs on this machine, against 10 ms and 100 ms (base line 51), and record both here. Done when: the numbers are written here with the commit they ran on, and a cost over its target is fixed in this section or filed with its measurement.
- [ ] Commit: `core: record warnings in plain words, and the publish cost measured (D01 T02 §3)`

**Test checkpoint:** `pwsh scripts/check-all.ps1 -Only core-tests` exits 0, and each injected error's test fails with the numeric warning restored.

## 4. Status JSON states its admission order and names skipped records

Filed 2026-11-22 by `D00 T06 §2` from `todo/findings.md` (base lines 56 to 58, completion review of `D01 T01 §3`). `status_json` in `src/core.hpp` lists waiters oldest first, but the queue does not promise first-come admission (`docs/specs/2026-11-07-bosun-design.md`), and the panel labels the list "Jobs waiting, oldest first"; `records.skipped` and `records.readable` say something went wrong but not which file or why; `cmd_status` in `src/app.cpp` has no measured latency.

**Phase:** 1
**Needs:** none
**Surface:** no

**Build order:**

1. State the order. Done when: the object names its admission policy and the panel's label matches it.
2. Name what was skipped. Done when: a bounded list of affected files with a reason code each is in the object.
3. Measure. Done when: `status --json` with ten jobs and a damaged record has a recorded p95.

- [ ] Add `admission` to the `waiters` object in `status_json` (`src/core.hpp`) naming how a slot is given (the design's rule, read from the code that grants it) and the sort the list uses, and set the Queue page's label in `src/Bosun.Panel/Theme/Controls.xaml` from it, as Slurm's `squeue` names its sort (base line 56). Done when: a test reads the field, and the panel label says "Waiting longest first" unless the policy says otherwise.
- [ ] Replace the bare `skipped` count with a list of at most 20 entries (`file`, `reason`: `unreadable`, `malformed`, `foreign-version`, `locked`), keeping the count, so the panel can say how to recover without stderr (base line 57). Done when: a test damages one record of each kind and reads each entry, and a 21st is counted, not listed.
- [ ] Measure `bosun status --json` end to end with ten jobs and one damaged record, p95 over 100 runs on Windows and through a running WSL guest, against 250 ms and 500 ms (base line 58), and record both here. Done when: the numbers are written here with their commit, and a cost over target is fixed here or filed with its measurement.
- [ ] Commit: `core: status JSON states its admission order and names skipped records (D01 T02 §4)`

**Test checkpoint:** `pwsh scripts/check-all.ps1 -Only core-tests` exits 0, and the panel's tests (`dotnet test tests/Bosun.Panel.Tests`) pass with the new label.

## 5. History within a size budget, a streamed report and a burst capture

Filed 2026-11-22 by `D00 T06 §2` from `todo/findings.md` (base lines 59 to 61, completion review of `D01 T01 §4`). `prune_logs` in `src/app.cpp` keeps history by age only (14 days); `cmd_report` holds every accepted row in a vector before the join; `cmd_log` samples every 30 seconds, which can miss a short build peak.

**Phase:** 1
**Needs:** none
**Surface:** no

**Build order:**

1. Bound the history. Done when: a total size budget keeps the newest usable history.
2. Stream the report. Done when: fourteen days of two-sided logs report within the memory and time targets.
3. Catch short peaks. Done when: a bounded capture mode sees a five-second pressure episode.

- [ ] Add a total size budget to `prune_logs` in `src/app.cpp` (a setting in `config.json`, read where the age limit is), removing the oldest files first, as Logman's circular logs do (base line 59). Done when: a sustained-collection test fills past the budget and reads that the total stays within it and the newest day survives whole.
- [ ] Benchmark `cmd_report` on fourteen days of two-sided logs with a damaged tail, then join without holding every row when it misses p95 under one second or adds over 100 MB of working set (base line 60). Done when: the before and after numbers are recorded here, and the report's output is byte-identical before and after.
- [ ] Add a bounded capture mode to `cmd_log` that, while memory pressure is high, samples every second for at most a set time, and returns to the 30-second interval after (base line 61). Done when: a test raises a five-second pressure episode and reads it in the log within two seconds, and the idle cost outside it is measured unchanged.
- [ ] Commit: `core: history within a size budget, a streamed report and a burst capture (D01 T02 §5)`

**Test checkpoint:** `pwsh scripts/check-all.ps1 -Only core-tests` exits 0, with the budget test failing against age-only pruning.

## 6. A saved limit reaches waiting jobs, and the Limits form saves at once

Filed 2026-11-22 by `D00 T06 §2` from `todo/findings.md` (base lines 62 and 63, completion review of `D01 T01 §5`). `acquire_slot` in `src/app.cpp` opens `o.slots` handles once and polls only those, so lowering `slots` doesn't reach jobs already waiting; `cmd_config` takes one key and value per call, so the Limits form's three saves can be seen half done by a job starting between them.

**Phase:** 1
**Needs:** none
**Surface:** no

**Build order:**

1. Let waiters see a change. Done when: a saved slot count reaches waiting jobs within one poll.
2. Save at once. Done when: one call saves every value or none, then reads them back.

- [ ] Have `acquire_slot` in `src/app.cpp` re-read `config.json` each poll while waiting, as GNU Parallel's file-backed job count does, never stopping a running job and never overriding a limit typed on the command line (base line 62). Done when: a test lowers `slots` while two jobs wait and reads that only the new count is admitted, and a typed `--slots` is kept.
- [ ] Add a batch form to `cmd_config` (`bosun config set slots=N ... --json`) that validates every value, writes them in one temporary-file-then-replace, and prints the read-back, and have the panel's Limits form use it (base line 63). Done when: a test makes one value invalid and reads that none were written, and a job started during a valid save sees only all old or all new values.
- [ ] Commit: `core: a saved limit reaches waiting jobs, and the Limits form saves at once (D01 T02 §6)`

**Test checkpoint:** `pwsh scripts/check-all.ps1 -Only core-tests` exits 0, with the waiting-jobs test failing against a single read of the limits.

## Verification

- Every section's Test checkpoint passes on the stamped candidate, and `python scripts/todo-graph.py validate` passes.
- The measurements in §3, §4 and §5 are recorded with their commits.
