---
schema_version: 1
id: kit-follow-ups
domain: 00-workspace
status: active
title: "TODO-07 -- Kit follow-ups"
---

# TODO-07 -- Kit follow-ups

> **Goal:** The gates, hooks, test harness and build tooling that `TODO-01` and `TODO-02` shipped are quick, say what they are doing, and leave a record to read after a failure, so a run spends its time building rather than waiting on or re-running its own tools.

> **Current state:** Measured 2026-11-21 at `4d9b6e6`. `D00 T06 §1` checked each finding below against the code: all are still open. `python scripts/todo-graph.py plan --check` took 45 seconds on this machine, since the CLI loads the graph and `projection` loads it again; pyright reports 480 errors over `scripts/`; 14 of 26 PowerShell test files and 11 of 15 Python test files ignore `BOSUN_SCRATCH`; `scripts/check-all.ps1` keeps no log; `scripts/weakening-scan.py` starts one `git show` per side of every changed file.

## Inputs

- `todo/findings.md`, base lines 12, 14 to 16, 21 to 28, 32, 33 and 35 to 39 (as of `b8c4e0b3`), struck with these sections' references by `D00 T06 §1`.
- `TODO-01-kit-completion.md` and `TODO-02-dotnet-foundation.md`, which shipped the tools these sections improve; `docs/build-performance.md` for the measured build settings.

## Outcome

- `python scripts/todo-graph.py plan --check` loads the graph once, and its time before and after is recorded.
- `pyright` over `scripts/` reports no errors.
- A failed `pwsh scripts/check-all.ps1` names the log that holds its output.

**Adjacency:** all=not-applicable (internal tooling with no records of its own)

**Adjacency rationale:** These are the repository's own development tools; the product's records and users are untouched.

## Implementation Order

| Order | Section | Deliverable | Depends On | Status |
| ----- | ------- | ----------- | ---------- | ------ |
| 1 | §1 | Graph loads once per command | -- | [ ] |
| 2 | §2 | Pyright clean over the scripts | -- | [ ] |
| 3 | §3 | Test fixtures in the scratch folder | -- | [ ] |
| 4 | §4 | check-all keeps a log and says its waits and SDK | -- | [ ] |
| 5 | §5 | Weakening scan reads blobs in one batch and shows lines | -- | [ ] |
| 6 | §6 | Clone setup and pre-commit say what they did | -- | [ ] |
| 7 | §7 | Build measurement compares against a baseline | -- | [ ] |
| 8 | §8 | Build script stops configuring on every run | §7 | [ ] |
| 9 | §9 | One documented .NET test command with progress | -- | [ ] |
| 10 | §10 | Plan workflow checks first and states its policy | -- | [ ] |

## 1. Graph loads once per command

Filed 2026-11-21 by `D00 T06 §1` from `todo/findings.md` (base line 21, measured during `D00 T01 §4`). `scripts/todo-graph.py` loads the graph (`rows = graph()`), and its `plan` branch calls `projections` in `scripts/lib/todo_graph.py`, whose `projection` calls `graph(root)` again; the pre-commit hook runs `plan --check` on every plan commit, and it took 45 seconds on 2026-11-21.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Measure where the time goes. Done when: a profile is recorded in this section.
2. Load once per command. Done when: `plan --check` builds the graph once, proven by a test.

- [ ] Profile `python scripts/todo-graph.py plan --check` with `cProfile` and record the five largest cumulative costs and the total in this section. Done when: the numbers are written here with the commit they ran on.
- [ ] Pass the loaded rows into `projections` and `projection` in `scripts/lib/todo_graph.py` instead of loading again, keeping `graph(root)` as the default for other callers. Done when: `scripts/tests/test_todo_graph.py` counts one graph load for `plan --check` and `plan --sync`, and the generated plan is byte-identical before and after. Cheaper substitute: a cache keyed on nothing that can serve a stale graph.
- [ ] Fix the largest cost the profile names when it is not the double load (the receipt checks, most likely), keeping every check it makes. Done when: the same test suite passes and the run time after is recorded beside the time before.
- [ ] Commit: `todo-graph: load the graph once per command (D00 T07 §1)`

**Test checkpoint:** `python scripts/run-tests.py test_todo_graph` exits 0, and with the second load restored the one-load test fails.

## 2. Pyright clean over the scripts

Filed 2026-11-21 by `D00 T06 §1` from `todo/findings.md` (base line 12). Since `pyrightconfig.json` resolves `scripts/lib`, pyright reports type errors in the scripts and their tests (480 on 2026-11-21, most of them optional values used where a value is required, and `fcntl` on Windows). An editor full of red hides the one error that is real.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Sort the errors. Done when: each class is counted here, real faults apart.
2. Fix them at their source. Done when: pyright reports 0 errors, and CI keeps it there.

- [ ] Run pyright over `scripts/` and count the errors by rule and file, recording here which are platform noise (a POSIX module on Windows), which are missing narrowing and which are real faults. Done when: the table is written with the pyright version.
- [ ] Fix each real fault with a regression test that fails before its fix. Done when: each has its test named here.
- [ ] Narrow optional values where they are used (an early return or an `assert` with a reason), and mark the platform modules as platform code (`sys.platform` checks pyright understands), never a blanket `# type: ignore`. Done when: `pyright scripts` reports 0 errors and no new `type: ignore` is added. Cheaper substitute: lowering `pyrightconfig.json`'s strictness.
- [ ] Run pyright in CI's scripts job in `.github/workflows/build.yml`, so a new error turns the run red. Done when: a pushed branch with one type error shows the step red, recorded here.
- [ ] Commit: `scripts: pyright reports no errors (D00 T07 §2)`

**Test checkpoint:** `pyright scripts` prints `0 errors`, and `python scripts/run-tests.py` exits 0.

## 3. Test fixtures in the scratch folder

Filed 2026-11-21 by `D00 T06 §1` from `todo/findings.md` (base line 24, round 2 of `D00 T01 §6`). Most of `tests/*_test.ps1` and `scripts/tests/*.py` put their fixtures in the system temporary folder and ignore `BOSUN_SCRATCH`, each with its own fallback where it reads it, so a run fills the system drive instead of the scratch drive the owner set.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. One way to ask for a scratch folder in each language. Done when: both helpers are tested.
2. Every test uses it. Done when: a search finds no fixture made in the system temporary folder directly.

- [ ] Add `New-BosunScratchFolder` to `scripts/lib/scratch.ps1` and `scratch_dir()` to `scripts/lib/scratch.py`: a new folder under `BOSUN_SCRATCH`, else the system temporary folder, named for the test, removed by its caller's cleanup. Done when: a test of each reads a folder under a fake `BOSUN_SCRATCH` and under the temporary folder without it.
- [ ] Move every `tests/*_test.ps1` and `scripts/tests/*.py` fixture onto the helpers. Done when: `git grep -nE 'GetTempPath|tempfile\.mkdtemp|\$env:TEMP' tests scripts/tests` lists only the helpers, and the suites pass. Cheaper substitute: setting `TEMP` for the whole run.
- [ ] Commit: `tests: fixtures in the scratch folder (D00 T07 §3)`

**Test checkpoint:** `python scripts/run-tests.py` exits 0 with `BOSUN_SCRATCH` set, and the search above lists only the two helpers.

## 4. check-all keeps a log and says its waits and SDK

Filed 2026-11-21 by `D00 T06 §1` from `todo/findings.md` (base lines 25, 28 and 37, raised by the completion reviews of `D00 T01 §6`, `D00 T01 §13` and `D00 T02 §3`). `scripts/check-all.ps1` keeps no log, so an unattended failure is read only by running its step again; a queued step waits in silence; and the .NET SDK it builds with is never printed, though CI prints it.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Keep each run's output. Done when: a failure names its log, in `tests/check_all_test.ps1`.
2. Say queue waits and the SDK. Done when: both lines are read back.

- [ ] Write each run's output to a log under the scratch folder (`BOSUN_SCRATCH`, else the system temporary folder) as it goes, and print its path at the end and at a failure. Done when: the test of a failing step reads the log path, and the log holds that step's output. Cheaper substitute: telling the reader to run the step again.
- [ ] Print one line when a queued step starts waiting and one when its turn comes, with how long it waited. Done when: the test with a fake busy queue reads both lines and a wait time.
- [ ] Print the effective .NET SDK once (`dotnet --version`) before the local .NET build, keeping the release-only selection. Done when: the test reads the version line once in a run that builds .NET, and none in one that does not.
- [ ] Commit: `check-all: a log, queue waits and the SDK (D00 T07 §4)`

**Test checkpoint:** `pwsh tests/check_all_test.ps1` exits 0, and with the log path line removed the failing-step test fails.

## 5. Weakening scan reads blobs in one batch and shows lines

Filed 2026-11-21 by `D00 T06 §1` from `todo/findings.md` (base lines 26 and 27, raised by the completion review of `D00 T01 §13`). `scripts/weakening-scan.py` starts one `git show rev:path` per side of every changed file, and its report names each signal without the lines it saw, so understanding a plain removal takes another command.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Read a range's blobs in one batch. Done when: the same signals come back faster on a large range.
2. Show each signal's lines. Done when: the report holds the before and after lines.

- [ ] Read both sides of every changed file through one `git cat-file --batch` in `scripts/weakening-scan.py`, and measure a 1,000-file range against the current reader and against plain `git diff` of the same range, recording all three times here. Done when: `scripts/tests/test_weakening_scan.py` reads identical signals from both readers on its fixtures, and the three times are written here, the batch reader's overhead above `git diff` named. **Corrected 2026-11-21 (completion review of `D00 T06 §1`):** the comparison with plain `git diff` the finding asked for was missing. Cheaper substitute: parallel `git show` processes.
- [ ] Print each signal's before and after lines (at most three of each, with line numbers) under it in the report. Done when: the test of a removed assertion reads the removed line in the report.
- [ ] Commit: `weakening-scan: one batch read and the lines each signal saw (D00 T07 §5)`

**Test checkpoint:** `python scripts/run-tests.py test_weakening_scan` exits 0, and with the lines removed from the report the removed-assertion test fails.

## 6. Clone setup and pre-commit say what they did

Filed 2026-11-21 by `D00 T06 §1` from `todo/findings.md` (base lines 22 and 23, raised by the completion review of `D00 T01 §4`). `scripts/setup-clone.ps1` shows the earlier `core.hooksPath` only in its first run's undo line and keeps it nowhere, so the undo is lost with the terminal; `scripts/hooks/pre-commit` runs the plan check with no word before or after, and the comment above it claims a saving nobody measured.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Keep the earlier hooks path where it outlives the terminal. Done when: a second run still prints the undo.
2. Say the pre-commit's plan check and its time. Done when: both lines are read back.

- [ ] Store the earlier `core.hooksPath` in the clone's own config (`bosun.previousHooksPath`) when `scripts/setup-clone.ps1` replaces it, and print the one undo command on every run, `already on` included; `-Undo` puts it back and removes the key. Done when: `tests/setup_clone_test.ps1` reads the undo on a second run and the earlier value restored by `-Undo`. Cheaper substitute: printing the undo on the first run only.
- [ ] Print `pre-commit: checking the plan...` before the plan check in `scripts/hooks/pre-commit` and its elapsed seconds after; measure a plan commit with and without the check and replace the comment's claimed saving with the measured one. Done when: a test commit in a scratch clone reads both lines, and the comment cites the measurement.
- [ ] Commit: `setup: the earlier hooks path is kept, and pre-commit says its check (D00 T07 §6)`

**Test checkpoint:** `pwsh tests/setup_clone_test.ps1` exits 0, and with the stored key removed the second-run undo test fails.

## 7. Build measurement compares against a baseline

Filed 2026-11-21 by `D00 T06 §1` from `todo/findings.md` (base lines 14, 15, 38 and 39, raised by the completion reviews of `D00 T01 §12` and `D00 T02 §4`). `scripts/measure-build.ps1` reports each label's median but no change against a chosen baseline or spread, is silent through a long step such as the 291 second Python suite, and `scripts/lib/measure-dotnet.ps1` times its warm one-file build once, so one slow sample (35.2 s for `dotnet-cpu-1`) decided alone; nothing explains why a candidate won or lost.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Sample every step alike and compare to a baseline. Done when: the comparison and its spread are read back in `tests/measure_build_test.ps1`.
2. Say progress and explain a result. Done when: progress lines and the optional diagnostic run are tested.

- [ ] Time the warm one-file build `-Samples` times in `scripts/lib/measure-dotnet.ps1`, as the other steps are, and store its median and spread. Done when: the test reads `-Samples` warm samples and their median. (findings base line 39)
- [ ] Add `-Baseline <label>` to `scripts/measure-build.ps1`: each label's change in time and memory against it, with the sample spread and a warning when the spread is wider than the change, keeping the raw samples and medians. Done when: the test with fake samples reads each change, and the warning when the spread is wide. Cheaper substitute: comparing medians without the spread. (findings base line 14)
- [ ] Announce each step before it starts and print the elapsed time at least every five seconds during a long one, without counting the printing in the measurement. Done when: the test with a fake 12 second step reads the start line and at least two progress lines. (findings base line 15)
- [ ] Add an optional `-Diagnose` run to `scripts/lib/measure-dotnet.ps1`, separate from the timed samples, that keeps an MSBuild binary log in the scratch folder and names the three most expensive targets or tasks. Done when: the test of `-Diagnose` on the smallest project reads three names and a log path, and the timed samples are unchanged. (findings base line 38)
- [ ] Commit: `measure: samples alike, a baseline, progress and a diagnostic run (D00 T07 §7)`

**Test checkpoint:** `pwsh tests/measure_build_test.ps1` exits 0, and with the spread warning removed its test fails.

## 8. Build script stops configuring on every run

Filed 2026-11-21 by `D00 T06 §1` from `todo/findings.md` (base line 16, raised by the completion review of `D00 T01 §12`). `scripts/build.ps1` configures CMake on every run, which `cmake --build --preset release` does not need: CMake reconfigures by itself when its inputs change.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Measure the cost. Done when: ten no-op and ten one-file builds of each way are recorded.
2. Cut what the script adds. Done when: the script configures only when the build tree is missing or its preset changed.

- [ ] Measure, with `scripts/measure-build.ps1` from `D00 T07 §7`, ten no-op and ten one-file builds through `scripts/build.ps1` and through `cmake --build --preset release` in an initialised developer shell, and record the medians and spreads in `docs/build-performance.md`. Done when: both are recorded with the commit.
- [ ] Configure in `scripts/build.ps1` only when the build tree has no cache or the preset's configure inputs changed, keeping CMake's own automatic reconfiguration. Done when: a test reads no configure step on a second run, a configure after the cache is removed, and a reconfigure after a `CMakeLists.txt` change. Cheaper substitute: never configuring.
- [ ] Commit: `build: configure only when needed (D00 T07 §8)`

**Test checkpoint:** the build script's test exits 0, and the measured no-op median after is recorded beside the one before in `docs/build-performance.md`.

## 9. One documented .NET test command with progress

Filed 2026-11-21 by `D00 T06 §1` from `todo/findings.md` (base lines 35 and 36, raised by the completion review of `D00 T02 §1`). `CONTRIBUTING.md` describes `check-all` but gives no one command that restores, builds and runs both .NET foundation suites with the SDK they need, and `tests/dotnet_nodes_test.ps1` is silent while a case builds for minutes.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Document one command. Done when: it runs as written in a fresh clone.
2. Say progress in the nodes test. Done when: progress lines are read back.

- [ ] Add to `CONTRIBUTING.md` one queued command (`bosun run -- dotnet test ...`, or `pwsh scripts/check-all.ps1 -Only dotnet` if that is the existing step) that restores, builds and runs both .NET foundation suites, with the required SDK from `global.json` beside it. Done when: the command runs as written in a fresh clone and both suites report, recorded here.
- [ ] Print the elapsed time at least every 30 seconds in `tests/dotnet_nodes_test.ps1` while a case builds, without its build log. Done when: a run of the test shows a progress line in a case longer than 30 seconds, recorded here. Cheaper substitute: printing the build log.
- [ ] Commit: `dotnet: one documented test command and progress (D00 T07 §9)`

**Test checkpoint:** the documented command exits 0 in a fresh clone, and `pwsh tests/dotnet_nodes_test.ps1` prints a progress line during a long case.

## 10. Plan workflow checks first and states its policy

Filed 2026-11-21 by `D00 T06 §1` from `todo/findings.md` (base lines 32 and 33, raised by the completion review of `D00 T01 §7`). `.github/workflows/plan.yml` sets up Node and the icon packages before its two plan checks, so a stale plan fails later than it could; and its header says it runs on every change without saying that, by the owner's trunk-based choice, no required check stops a direct push to `main`.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Run the plan checks first. Done when: a stale plan fails before Node is set up.
2. Say the policy and read it back. Done when: the preflight reads any required-check policy that is claimed.

- [ ] Move `todo-graph.py validate` and `plan --check` straight after Python is set up in the `plan-gates` job of `.github/workflows/plan.yml`, and measure a stale-plan push before and after. Done when: a pushed stale-plan fixture fails before the Node step, with both times recorded here.
- [ ] Say in the workflow header that it runs on every change but stops no direct push to `main` (no required checks, the owner's trunk-based choice), and have `scripts/campaign-preflight.ps1` read back the branch's required checks and fail if the documents claim one that is not set. Done when: `tests/preflight_test.ps1` with a fake protection that lacks a claimed required check fails, and passes when none is claimed.
- [ ] Commit: `ci: plan checks first and the push policy stated (D00 T07 §10)`

**Test checkpoint:** `pwsh tests/preflight_test.ps1` exits 0, and the stale-plan run's failure is recorded before the Node step.

## Verification

- Every row above is `[x]` through `stamp`.
- `python scripts/todo-graph.py validate` and `python scripts/todo-graph.py plan --check` pass.
