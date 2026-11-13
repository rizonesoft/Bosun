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
| 1 | §1 | Slot holder and waiter records | D00 T03 §10 | [x] |
| 2 | §2 | Job history log | §1 | [x] |
| 3 | §3 | status --json | §1 | [ ] |
| 4 | §4 | report --json and logger snapshots | §2 | [ ] |
| 5 | §5 | One config file for queue limits | §3 | [ ] |

## 1. Slot holder and waiter records

> **Started:** 2026-11-13T04:11:25Z

A mutex can't name its owner. Holders and waiters publish a small record beside the mutex. The mutex stays the only authority, and a stale record is ignored.

**Corrected 2026-11-13:** checked against the tree before the build. The slot mutexes are per queue namespace (`Local\<ns>.Slot.N`, `open_slot` in `src/app.cpp`), and `tests/queue_test.ps1` runs in a namespace of its own, so the records go per namespace too (`%LOCALAPPDATA%\Bosun\run\<ns>\`); otherwise a test's holders would read as the machine's. The WSL shim (`wsl/bosun`) holds a slot through a `bosun.exe acquire` it keeps on a pipe, so that process writes the record, and the shim passes the command beside the repo and side, since the record names all three. A Windows job's repo is the nearest folder above its working directory holding `.git` (`src/core.hpp:99`, as the logger attributes a process).

**Corrected 2026-11-13 (plan review):** the plan review in `docs/reviews/01-core/D01-T01-s1.md` returned six gaps, planned in below: each record's lifecycle on every path a job takes; cleanup that can't remove a successor's record, by construction; a record that can't be written or read changing nothing the queue does; integration proof on real jobs on both sides; records scoped to the Windows session as `Local\` mutexes are; and a written data contract.

**Phase:** 1
**Needs:** none
**Surface:** no

**Build order:**

1. Define the record and its validation in `src/core.hpp`. Done when: `test_core.exe records` covers the contract and every validation case.
2. Write and remove records in `cmd_run` and `cmd_acquire` on every path. Done when: `tests/queue_test.ps1` reads the real files through each transition.
3. Carry WSL jobs' repo, side and command. Done when: a top-level WSL run's record names them.

- [x] Define the record in `src/core.hpp` (`dm::SlotRecord`, `dm::record_json`, `dm::parse_record`): `schema_version` 1, `kind` `"holder"` or `"waiter"`, `pid` (integer), `process_start` (the process's creation time as a 64-bit FILETIME integer, as `GetProcessTimes` gives it), `started_at` (UTC, `YYYY-MM-DDTHH:MM:SSZ`), `command` (string, the command line as run), `repo` (string, or `null` outside a Git checkout), `side` (`"windows"` or `"wsl"`), `slot` (integer, holders only), `session` (integer) and `ns` (string). Strings are UTF-8 and JSON-escaped (quotes, backslashes, control characters as `\u00XX`), with `dm::json_escape` the one escaper `§3` reuses. Done when: `test_core.exe records` round-trips a record whose command holds quotes, backslashes and non-ASCII text, and one with no repo, and `parse_record` refuses a truncated, non-object or wrong-version record without throwing. Cheaper substitute: string concatenation with no escaping.
- [x] Place records per Windows session and queue namespace: `%LOCALAPPDATA%\Bosun\run\<session>\<ns>\`, the session from `ProcessIdToSessionId`, as the `Local\` mutexes are per session, and `<ns>` through `dm::ns_dir`, an injective mapping onto file-name-safe text (`[a-z0-9_-]` kept, every other UTF-8 byte as `%XX` in upper-case hex, behind an `ns-` prefix). (**Corrected 2026-11-13 (build):** it kept `.`; Windows drops a trailing dot and refuses `.`, `..`, an empty name and the device names such as `CON`, so `.` is encoded too and the prefix keeps every name valid. **Corrected 2026-11-13 (review round 1):** it kept upper-case letters, but mutex names are case-sensitive and folder names are not, so `ReviewCase` and `reviewcase` shared a folder; upper-case letters are encoded too.) Done when: `test_core.exe records` shows two namespaces differing only in a character outside the safe set map to different folders, and each mapped name is a valid file name, `.`, `..`, an empty name and `CON` included.
- [x] Validate a record against the live process in `src/core.hpp` (`dm::record_state`, given a probe that reports a pid's start time, or that it is gone, or that it can't be told): `live` when the pid runs with that start time, `stale` when it is gone or started at another time, `unknown` when the probe can't tell (an access denied is not a death). Done when: `test_core.exe records` covers live, gone, reused pid and can't-tell. Cheaper substitute: trusting the pid alone.
- [x] Write a waiter record `wait-<pid>-<process_start>.json` in `src/app.cpp` before the slot wait starts, by temporary file then replace, and remove it when the wait ends: on acquiring a slot (the holder record replaces it), on the wait timing out (no holder record is written for a job that runs without a slot) and on any later exit path. Done when: `tests/queue_test.ps1` sees a second job's waiter file while the first holds the only slot, and sees it gone once that job starts, and gone after a job that timed out without a slot. Cheaper substitute: one shared waiters file rewritten by every job.
- [x] Write the holder record `slot-<N>.json` only while holding slot N's mutex, after acquiring it, and remove it before releasing the mutex, on every path `cmd_run` and `cmd_acquire` take: the command's normal end, a command that can't start, the write budget stopping it, and Ctrl+C (which `bosun` already outlives). A run inside a slot (`BOSUN_SLOT` set) writes no record and never touches the outer holder's. Done when: `tests/queue_test.ps1` reads `slot-0.json` with the running job's pid, command, repo and side while it runs and finds it gone after each of those ends, and a nested run leaves the outer record unchanged.
- [x] Clean up by construction: only the holder of slot N's mutex ever writes or removes `slot-<N>.json`, so a record left by a killed holder is replaced by the next one, and readers never delete a slot record; a reader removes a waiter record only when its process is `stale`, and the name carries the process start time, so it can't be a successor's. Done when: `test_core.exe records` shows the cleanup choice keeps a live and an `unknown` waiter, and `tests/queue_test.ps1` kills a holder, lets the next job take the slot, runs a reader's cleanup pass and finds the new holder's record intact.
- [x] Keep the queue unchanged when records fail: a record that can't be written (a full disk, a denied folder) prints one diagnostic (`[bosun] can't publish this job's slot record (...): the queue works, but status can't name it`) and changes neither admission nor the command's exit code; a records folder that can't be read is reported as unavailable, never as an empty queue. Done when: `tests/queue_test.ps1`, with the records folder made unwritable, runs a job that exits 37 and sees exit 37 and the diagnostic.
- [x] Pass repo, side and command from `wsl/bosun` to `bosun.exe acquire` (`--repo`, `--side wsl`, `--label`), the label the command's words each quoted with `printf %q` and joined by spaces, and the repo the Git top level of the working folder, or none. Done when: `tests/queue_test.ps1 -Wsl` runs a top-level WSL job (no `BOSUN_SLOT`) through `acquire` and reads its record naming `wsl`, the Linux repo path and the command. WSL is not on the CI runner, so this case runs here, in the full local gate with `-Wsl`.
- [x] Prove the integration can fail: with record publication disabled in `src/app.cpp`, or the WSL forwarding dropped from `wsl/bosun`, the matching `tests/queue_test.ps1` cases fail. Done when: both mutations are run and recorded in the commit body.
- [x] Commit: `core: publish slot holders and waiters`

**Test checkpoint:** `bosun run -- pwsh scripts/build.ps1`, then `build\Release\test_core.exe records` and `pwsh tests\queue_test.ps1` exit 0 (CI runs both), and `pwsh tests\queue_test.ps1 -Wsl` exits 0 here; each fails if publication or the WSL forwarding is reverted.

> **Verified:** 2026-11-13 | §1 | b8078b102cbbe4d2f689f9e5494d5cf229c72e2e
> **Review:** `docs/reviews/d01-t01-s1.json`

## 2. Job history log

> **Started:** 2026-11-13T06:09:17Z

The Queue page and the overlap analysis both need to know what ran when. One file per day, kept as long as the memory logs.

**Corrected 2026-11-13:** checked against the tree before the build. The log's pruning is `prune_logs` in `src/app.cpp` (the section named `src/logger_main.cpp`, which is only the logger's windowless entry point), and it reads a file's date only from a `.csv` name (`file_date`), so a `.jsonl` file would never go. A WSL job is run by `wsl/bosun` while `bosun.exe acquire` holds its slot, and the section named no writer for it: `acquire` writes its line, as it writes its record (`D01 T01 §1`), learning the exit code from the shim over the pipe they already share. A run inside a slot (`BOSUN_SLOT`) writes no line, as it writes no record: the outer job's line covers it.

**Corrected 2026-11-13 (plan review):** the plan review in `docs/reviews/01-core/D01-T01-s2.md` returned six gaps, planned in below: the times are defined, and a WSL job's start is told by the shim, since it waits for guest memory after its slot; the checkpoint names the harness's real switch (`-SkipWsl`) and puts the append's mutation on the integration tests; `bosun log --once` prunes too; a log that can't be written has its own test, and the logs folder is created without throwing; a real crash, an interruption and a nested run are each proved; and six appends are proved simultaneous with a barrier.

**Phase:** 1
**Needs:** none
**Surface:** no

**Build order:**

1. Define the line in `src/core.hpp`. Done when: `test_core.exe jobs` covers the contract.
2. Append on job end in `src/app.cpp`, from both `run` and `acquire`. Done when: each finished job adds one line, a crashed or interrupted command included.
3. Prune with the existing retention. Done when: job logs older than 14 days go, by the running logger and by `bosun log --once`.

- [x] Define the line in `src/core.hpp` (`dm::JobLine`, `dm::job_line_json`, `dm::parse_job_line`): `schema_version` 1; `started_at` (UTC, `YYYY-MM-DDTHH:MM:SSZ`, when bosun took the job) and `ended_at` (when its command ended); `pid` (the bosun process); `command`; `repo` (or `null`); `side` (`"windows"` or `"wsl"`); `ns`; `slot` (the slot held, or `null` for a job that ran without one); `waited_s` (seconds, one decimal, from `started_at` until the command started: the slot wait, and for a WSL job the guest-memory wait after it) and `ran_s` (from the command's start to its end); `exit_code` (integer, or `null` when the WSL shim went away without reporting one); and `timed_out_into_queue` (true when the slot wait ran out and the job ran without a slot); escaped with `dm::json_escape`. Done when: `test_core.exe jobs` round-trips a line with quotes and non-ASCII text in its command, one with no repo, no slot and no exit code, and refuses a truncated line. Cheaper substitute: CSV, which a command holding a comma breaks.
- [x] Append one line per finished job to `%LOCALAPPDATA%\Bosun\logs\jobs-YYYY-MM-DD.jsonl` (the UTC date the job ended) in `src/app.cpp`, from `cmd_run` after its command ends, whatever ended it, as one `WriteFile` to a handle opened for appending, so lines from jobs ending at once never interleave; a run inside a slot (`BOSUN_SLOT`) writes none. The logs folder is created with the non-throwing `std::filesystem` calls, in `log_dir` too. A line that can't be written prints one diagnostic and changes neither the exit code nor the slot. Done when: `tests/queue_test.ps1` finds one line with the right fields for a job that exits 0, one that exits 37, one whose command can't start (127), one stopped at its write budget (74), one that timed out into the queue, one whose command crashes (an access violation, `0xC0000005`) and one interrupted by Ctrl+Break; and a nested run leaves exactly one line, the outer job's. Cheaper substitute: only recording successes.
- [x] Prove a line that can't be written changes nothing else: with the logs folder unwritable (a file where it would be), a job that exits 37 still exits 37 with one diagnostic, and the next job still takes the slot. Done when: `tests/queue_test.ps1` shows both.
- [x] Write a WSL job's line from `cmd_acquire`: the shim sends `start` down the pipe as its command starts (after any guest-memory wait) and `exit <code>` once it has ended, and `acquire` times the wait and the run from them and writes the line, with `exit_code` `null` when the pipe closes with no code (the shim was killed). Done when: `tests/queue_test.ps1` (its WSL half, which runs unless `-SkipWsl` is given) finds a top-level WSL job's line with side `wsl`, its command and its exit code; one held about three seconds for guest memory (another job on slot 0, `--min-guest-mb` out of reach, `--max-wait 3`) then running `sleep 2`, whose `waited_s` is at least 2.5 and `ran_s` between 1.5 and 3.5; and one with no exit code after its shim is killed.
- [x] Prune job logs with the memory logs in `prune_logs` in `src/app.cpp`, reading the date from a `.jsonl` name too, and prune on `bosun log --once`, which ended before pruning. Done when: `tests/queue_test.ps1`, with a job log dated 20 days ago and one from today in the test's own home, sees the old one removed and today's kept by `bosun log --once`.
- [x] Prove simultaneous appends: six jobs on six slots whose commands all wait at a barrier, released together, each leave one whole line. Done when: `tests/queue_test.ps1` releases the six at once and parses six lines, each with its own pid.
- [x] Prove the append can fail the tests: with the append removed from `cmd_run`, the `tests/queue_test.ps1` job-log cases fail. Done when: the mutation is run and recorded in the commit body.
- [x] Commit: `core: job history log`

**Test checkpoint:** `bosun run -- pwsh scripts/build.ps1`, then `build\Release\test_core.exe jobs` and `pwsh tests\queue_test.ps1` exit 0 here (with its WSL half), and CI runs `test_core.exe` and `pwsh tests\queue_test.ps1 -SkipWsl`; the job-log cases of `queue_test.ps1` fail if the append is removed.

> **Verified:** 2026-11-13 | §2 | fe89dcf643eefa870ee45aa44da52766fe783606
> **Review:** `docs/reviews/d01-t01-s2.json`

## 3. status --json

> **Started:** 2026-11-13T07:59:58Z

This is the panel's main read, and must be cheap to call every five seconds.

**Corrected 2026-11-13:** checked against the tree before the build. `cmd_status` is at `src/app.cpp:578` (the file said 269, then 246). Its reader pass `sweep_records` (`src/app.cpp:373`) parses every record but keeps none, so the live holders and waiters must come back from it rather than be read twice. The WSL shim's `status` branch (`wsl/bosun:20`) only adds `--slots` and execs `bosun.exe`, and the WSL logger (`wsl/bosun_log.py`) publishes no singleton marker, so the guest's figures and its logger's state can only come from the shim. The section said "the same output comes from either side" and "logger state" without saying how; decided here (industry default, one producer): the shim measures the guest and passes it to `bosun.exe status` as flags, so one C++ writer builds the whole object and bash never edits JSON. Reversal cost: a WSL-side JSON writer would duplicate the schema in two languages.

**Corrected 2026-11-13 (plan review):** the plan review in `docs/reviews/01-core/D01-T01-s3.md` returned seven gaps (2 P1, 5 P2), all planned in below. The Queue page (`design/SPEC.md` §5) shows each holder's waited and running time and each waiter's reason, which only the producer knows, so the slot records gain those fields (additive and optional, still `schema_version` 1, so a record from an older `bosun.exe` reads with them null); a partial read is reported, never shown as zero, free or empty; an unknown (access denied) record is exported with its state; the WSL shim forwards the Windows memory floor too; each logger is detected from its own singleton (the Windows mutex, the WSL `bosun-log.service` unit) and proved against an independent check; and every numeric flag is parsed strictly. Found while planning: `parse_options` read numbers with `std::stoi` and `std::stoull`, which took `12x` as 12 and `-1` as the largest value, so all numeric flags move to the strict parser here.

**Phase:** 1
**Needs:** none
**Surface:** no

**Build order:**

1. Add the timing and reason fields to the records, and the strict number parser, in `src/core.hpp`. Done when: `test_core.exe records` and `test_core.exe options` cover them.
2. Define the status view, its selection from the records and its writer in `src/core.hpp`. Done when: `test_core.exe status_json` covers every field, every record state and the escaping.
3. Publish the new record fields from `cmd_run` and `cmd_acquire`, and gather and print the view in `cmd_status`. Done when: `tests/queue_test.ps1` parses a real holder and waiter from `bosun status --json`.
4. Forward the Windows floor, guest memory and the WSL logger's state from `wsl/bosun`. Done when: the WSL half of `tests/queue_test.ps1` reads them.

- [ ] Parse every numeric flag strictly in `src/core.hpp` (`dm::parse_count`): ASCII digits only, no sign, no space, no trailing text, at most the flag's bound (`--slots` 1 to 64, `--days` and `--interval` 1 to 3650 and 1 to 86400, the MB and second values up to 2^40), used by `parse_options` in `src/app.cpp` for `--slots`, `--min-avail-mb`, `--max-wait`, `--max-write-gb`, `--days`, `--interval` and the new flags. Done when: `test_core.exe options` refuses `""`, `-1`, `+1`, `12x`, ` 12`, `0x10` and a 30-digit value and accepts `0` and the bounds, and `tests/queue_test.ps1` checks `bosun status --slots 12x` exits 64 with nothing on stdout. Cheaper substitute: catching the `std::stoull` exception.
- [ ] Add the optional fields to `dm::SlotRecord`, `dm::record_json` and `dm::parse_record` in `src/core.hpp`: a holder's `acquired_at` (UTC when it took the slot) and `command_started_at` (UTC when its command started, null until then), and its `min_guest_mb` (a WSL holder's guest floor, null on Windows); a waiter's `waiting_for` (`"slot"` when every slot it may take is held, `"memory"` when a slot is free but its memory floor is not met) with the `min_avail_mb` and `max_wait_s` it waits under. A missing field reads as null; a field of the wrong type refuses the record, as today. Done when: `test_core.exe records` round-trips each field, reads a §1 record without them, and refuses a string `acquired_at` of the wrong shape and an unknown `waiting_for`. Cheaper substitute: the reason worked out by `status` from its own flags.
- [ ] Publish the fields in `src/app.cpp`: `acquire_slot` writes the waiter record with its reason and limits, and rewrites it (temporary file, then replace) only when the reason changes, not on each poll; the holder record gets `acquired_at` when the slot is taken and is rewritten with `command_started_at` once the command starts (`cmd_run` after `CreateProcessW` succeeds, `cmd_acquire` on the shim's `start`), and `cmd_acquire` takes the shim's `--min-guest-mb` into the record. Done when: `tests/queue_test.ps1` reads a waiter's `waiting_for` of `slot` with both slots held and of `memory` with slot 1 free under a floor above the machine's memory, and a running holder's `command_started_at` no earlier than its `acquired_at`.
- [ ] Define `dm::StatusView`, `dm::status_view` and `dm::status_json` in `src/core.hpp`. `status_view` takes each slot's busy state (`true`, `false` or unknown when its mutex can't be opened or waited on), the parsed records with their `dm::RecordState`, and the current UTC time, and selects: slot N's holder is its `slot-N.json` record only when slot N is busy and the record is live or unknown, so a killed holder's leftover never names a holder of a free slot; a busy slot without such a record has `holder` null; waiters are the live and unknown waiter records not superseded by their own holder record, oldest `started_at` first; stale records are left out. The writer prints one line: `{"schema_version":1,"ns":"...","at":"<UTC>","slots":[{"index":N,"busy":true|false|null,"holder":null|{<record>,"state":"live"|"unknown","waited_s":N,"running_s":N|null,"phase":"waiting for guest memory"|"starting"|"running"}}],"waiters":[{<record>,"state":"live"|"unknown","waited_s":N}],"memory":{"available_mb":N|null,"total_mb":N|null,"min_avail_mb":N},"guest":null|{"available_mb":N,"total_mb":N,"min_guest_mb":N},"logger":{"windows":"running"|"not running","wsl":null|"running"|"not running"|"unknown"},"records":{"readable":true|false,"skipped":N,"dir":"..."}}`; `waited_s` runs from `started_at` to `acquired_at` (or to now for a waiter), `running_s` from `command_started_at` to now, and a WSL holder with no `command_started_at` and a `min_guest_mb` is `waiting for guest memory`. Every string goes through `dm::json_escape`. Done when: `test_core.exe status_json` parses the output back and asserts each field for a view with a free slot holding a leftover record, a busy slot with no record, a live and an unknown holder, a stale waiter (left out), a superseded waiter (left out), two waiters given newest first (printed oldest first), a WSL holder waiting for guest memory, an unknown busy state, a failed memory read (`null`, never 0) and a skipped record count. Cheaper substitute: changing the existing text output.
- [ ] Return the parsed records from the reader pass in `src/app.cpp` (`sweep_records`): its readable flag, every record it parsed with its state, and the count of `.json` files it could not read or parse (`skipped`); it still removes only what `dm::reader_removes` allows. Done when: the text `status` behaves as before and `--json` reuses the one pass.
- [ ] Gather and print the view in `cmd_status` (`src/app.cpp`): each slot's busy state from its mutex (unknown when `open_slot` fails or the wait returns `WAIT_FAILED`), memory from `GlobalMemoryStatusEx` (both figures null when it fails), the Windows logger from the `Local\Bosun.Logger` mutex, the guest and WSL logger from the flags below; `--json` prints the object and nothing else on stdout, exit 0, and an unreadable records folder still prints the object with `readable` false and the slots it could probe; without `--json` the text lines stay exactly as they are. Done when: `tests/queue_test.ps1` runs `bosun status --json` with one job holding slot 0 under `--slots 1` and one waiting, and reads `slots[0].holder.pid`, its `phase` `running`, a `waited_s` and `running_s`, the waiter's pid with `waiting_for` `slot`, the memory figures and `records.readable`; after the holder ends, `slots[0].busy` is false and `holder` null; with the records folder replaced by a file, `records.readable` is false and the output still parses; and `logger.windows` agrees with whether a `bosun-logger` process runs (`Get-Process`), which proves `running` on this machine and `not running` on CI, where no logger runs. Cheaper substitute: holders inferred from waiter files.
- [ ] Take the WSL side's figures as flags in `parse_options` (`src/app.cpp`): `--guest-avail-mb` and `--guest-total-mb` (strict numbers, given together or not at all), `--min-guest-mb` (a strict number) and `--wsl-logger` (`running`, `stopped` or `unknown`); a missing value, a bad value, one guest figure without the other or an unknown word is the usual bad-flag refusal (exit 64, nothing on stdout). Done when: `tests/queue_test.ps1` checks exit 64 and empty stdout for `--guest-avail-mb` with no value, `-5`, `5x`, a 30-digit value, `--guest-avail-mb 5` alone and `--wsl-logger maybe`, and that `--guest-avail-mb 5 --guest-total-mb 9 --wsl-logger stopped` prints those figures and `not running`.
- [ ] Forward in `wsl/bosun`: `status` gets the same `--slots`, `--min-avail-mb`, `--max-wait` and `--ns` flags from the environment as `run` does (one shared builder), then `--guest-avail-mb` and `--guest-total-mb` from `/proc/meminfo`, `--min-guest-mb` from `BOSUN_MIN_GUEST_MB`, and `--wsl-logger` from `systemctl --user is-active bosun-log.service` (`running` when active, `stopped` when it answers otherwise, `unknown` when there is no user systemd), the typed flags still later and winning; and `run` passes `--min-guest-mb` to `bosun.exe acquire`. Done when: the WSL half of `tests/queue_test.ps1` runs the shim's `status --json` and reads guest `available_mb` and `total_mb` within 10% of `/proc/meminfo`, `memory.min_avail_mb` from `BOSUN_MIN_AVAIL_MB=1234` and then `5678` from a later typed `--min-avail-mb 5678`, and a `logger.wsl` that agrees with `systemctl --user is-active bosun-log.service` (`running` here, `not running` or `unknown` on CI); and a WSL job held for guest memory shows `phase` `waiting for guest memory`. Cheaper substitute: the guest figures left out of the WSL call.
- [ ] Add cases to `tests/test_core.cpp` (`status_json`) for JSON escaping of commands with quotes, backslashes and a control character, and of a namespace and folder path with backslashes. Done when: each string round-trips through a JSON parse unchanged. Cheaper substitute: string concatenation with no escaping.
- [ ] Prove the integration can fail: with the holder dropped from the view in `cmd_status`, the waiter's reason fixed at `slot`, or the guest flags dropped from `wsl/bosun`, the matching `tests/queue_test.ps1` cases fail. Done when: the three mutations are run and recorded in the commit body.
- [ ] Commit: `core: status --json`

**Test checkpoint:** `bosun run -- pwsh scripts/build.ps1` then `build\Release\test_core.exe status_json`, `records` and `options` exit 0 and `pwsh tests/queue_test.ps1` (with its WSL half) prints `all passed`; each fails if the change is reverted.

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
