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
| 1 | §1 | Graph loads once per command | -- | [x] |
| 2 | §2 | Pyright clean over the scripts | -- | [x] |
| 3 | §3 | Test fixtures in the scratch folder | -- | [x] |
| 4 | §4 | check-all keeps a log and says its waits and SDK | -- | [x] |
| 5 | §5 | Weakening scan reads blobs in one batch and shows lines | -- | [x] |
| 6 | §6 | Clone setup and pre-commit say what they did | -- | [x] |
| 7 | §7 | Build measurement compares against a baseline | -- | [x] |
| 8 | §8 | Build script stops configuring on every run | §7 | [ ] |
| 9 | §9 | One documented .NET test command with progress | -- | [ ] |
| 10 | §10 | Plan workflow checks first and states its policy | -- | [ ] |
| 11 | §11 | The overlay says where a value came from, and lists its keys | -- | [ ] |
| 12 | §12 | Sync previews what it would pull, and receipts each change | -- | [ ] |
| 13 | §13 | A leak refusal for a ref name says to rename the ref | -- | [ ] |
| 14 | §14 | The token: a process-scoped launch and one vault read | -- | [ ] |
| 15 | §15 | Campaign preflight shows the required checks beside its flags | -- | [ ] |
| 16 | §16 | Review rounds list their verdicts, and a lookup by commit | -- | [ ] |
| 17 | §17 | The CI gate's report latency measured, its active step shown | -- | [ ] |
| 18 | §18 | The run hold's cold start measured against Awake | -- | [ ] |
| 19 | §19 | The run guard: a quicker look, suspended reads and a watch view | -- | [ ] |
| 20 | §20 | The findings ledger: one lookup, derived totals, stable owners | -- | [ ] |
| 21 | §21 | A stamped section's text stays as stamped | -- | [ ] |
| 22 | §22 | Machine-path findings name the commit that introduced them | -- | [ ] |
| 23 | §23 | Default campaign badges shipped for a machine without Python | -- | [ ] |

## 1. Graph loads once per command

> **Started:** 2026-11-22T16:49:47Z

Filed 2026-11-21 by `D00 T06 §1` from `todo/findings.md` (base line 21, measured during `D00 T01 §4`). `scripts/todo-graph.py` loads the graph (`rows = graph()`), and its `plan` branch calls `projections` in `scripts/lib/todo_graph.py`, whose `projection` calls `graph(root)` again; the pre-commit hook runs `plan --check` on every plan commit, and it took 45 seconds on 2026-11-21.

**Phase:** 0
**Needs:** none
**Surface:** no

**Corrected 2026-11-22 (the plan review of `D00 T07 §1`):** the section counted loads for `plan --check` and `plan --sync` only, while `query deferred`, `query adjacency` and `query rounds` load twice, the writing commands (`stamp`, `unstamp`, `repair`, `park`, `unpark`, `wake --apply`) reload after they write, and `unstamp` loads a third time for `suspects()`. The rule is now one graph build per distinct graph state in every command, with a negative test for each check the change touches, an independent byte baseline and a second profile.

**Build order:**

1. Measure where the time goes. Done when: a profile is recorded in this section.
2. Build once per state. Done when: every command builds the graph once per distinct state it reads, proven per command by a test.
3. Keep every check. Done when: a negative test per changed check refuses through the CLI.
4. Repair the largest remaining cost. Done when: a second profile shows it reduced.

- [x] Profile `python scripts/todo-graph.py plan --check` with `cProfile` and record the total, the five largest cumulative costs and the five largest own-time costs in this section, with the commit they ran on, so duplicate loading is told apart from costly work inside one load. Done when: the numbers are written here. Cheaper substitute: cumulative costs alone, which include their callers, which the Done when refuses (**corrected 2026-11-22, the plan review of `D00 T07 §1`**).
- [x] Pin the baseline before any change: run `python scripts/todo-graph.py plan --sync` on a fixed copy of the tree at this section's Started commit and keep its `todo/implementation-plan.md` as bytes in `scripts/tests/fixtures/plan-baseline.md` (with the inputs it read). Done when: the fixture is committed beside a test that regenerates from the same inputs with the revised code and compares bytes, failing on one changed newline or trailing byte. Cheaper substitute: a baseline generated by the revised writer, which the Done when refuses (**corrected 2026-11-22, the plan review of `D00 T07 §1`**).
- [x] Pass the validated rows through `scripts/todo-graph.py` and `scripts/lib/todo_graph.py` (`projections`, `projection`, `suspects`, the `query` helpers, `stamp`, `unstamp`, `repair`, `park`, `unpark` and `wake`), so each command builds the graph once per distinct graph state, reusing a writing command's validated post-write rows for its sync and keeping its rollback on failure; `graph(root)` stays the default for other callers. Done when: `scripts/tests/test_todo_graph.py` counts `graph()` executions through each CLI path (`plan --check`, `plan --sync`, `validate`, each `query`, `stamp`, `unstamp`, `repair`, `park`, `unpark`, and `wake --apply` with two passing wakes and with none left), each count equal to its number of distinct states, and restoring any second load fails that path's test. Cheaper substitute: a cache keyed on nothing that can serve a stale graph, which the Done when refuses (**corrected 2026-11-22, the plan review of `D00 T07 §1`**).
- [x] Add a negative test through the CLI for each validation path the change touches, among them a stamped section whose receipt verdict disagrees with its findings file: `plan --check` and `plan --sync` both refuse it, a refused `plan --sync` leaves the previous `todo/implementation-plan.md` bytes, and removing the check fails the test. Done when: each test passes and fails with its check removed, recorded here. Cheaper substitute: the one-load count and the plan bytes alone, which pass with a check skipped, which the Done when refuses (**corrected 2026-11-22, the plan review of `D00 T07 §1`**).
- [x] Profile the same input again after the double load is gone, pick the largest remaining cost (the receipt checks, most likely), and repair it keeping every check. Done when: comparable before-and-after profiles of that cost are recorded here, the after lower, and the test suite passes. Cheaper substitute: two run times without the cost named, which the Done when refuses (**corrected 2026-11-22, the plan review of `D00 T07 §1`**).
- [x] Commit: `todo-graph: build the graph once per state in every command (D00 T07 §1)`

**Profile 2026-11-22 (at `667852d`, `python -m cProfile scripts/todo-graph.py plan --check`):** total 59.7 s. Cumulative: `graph` 59.6 s over 2 calls (29.8 s each), `receipt_ok` 58.8 s over 230 calls, `receipt_data_ok` 58.7 s, `subprocess.run` 54.0 s over 922 calls, `communicate` 48.3 s. Own time: reading subprocess output 46.8 s, `CreateProcess` 5.4 s over 922 starts, `_getfinalpathname` 0.6 s, `review-dispatch.py` `_read_findings` 0.6 s over 2,300 calls, regex `findall` 0.5 s. So the double load is half the time, and each load's cost is the receipt checks' 922 git processes, about four per receipt.

**After 2026-11-22:** with the graph built once per state, `plan --check` took 27.5 s under `cProfile` (one `graph`, its cost the receipts' git processes: `record_policy` 230 calls 12.4 s, `pin_schedule` 115 calls 6.1 s, `receipt_data_ok` 115 calls 6.2 s). Repaired: a file at a full commit SHA is read once through `show_committed` in `scripts/review-dispatch.py`, shared by `record_policy` and `pin_schedule`, and ancestry between two full SHAs is asked once through `is_ancestor`, only successes kept. The same profile then: 18.6 s, 231 git processes (from 922) in 15.2 s (from 54.0 s), every check kept; wall time of `python scripts/todo-graph.py plan --check` 14.7 s (from about 60). The 231 left are two a receipt (its own ancestry and stamp lineage), different for each. Each second load, mutated back in at the eleven places it was removed, fails its command's count test; the receipt-verdict test fails with that check removed.

**Test checkpoint:** `python scripts/run-tests.py test_todo_graph` exits 0; with any one command's second load restored, its count test fails; with the receipt-verdict check removed, its negative test fails; and the byte comparison against `scripts/tests/fixtures/plan-baseline.md` passes (**corrected 2026-11-22, the plan review of `D00 T07 §1`**).

> **Verified:** 2026-11-22 | §1 | de3c4646712248907b3832284bdb71f2436fd68e
> **Review:** `docs/reviews/d00-t07-s1.json`

## 2. Pyright clean over the scripts

> **Started:** 2026-11-22T18:10:08Z

Filed 2026-11-21 by `D00 T06 §1` from `todo/findings.md` (base line 12). Since `pyrightconfig.json` resolves `scripts/lib`, pyright reports type errors in the scripts and their tests (480 on 2026-11-21, most of them optional values used where a value is required, and `fcntl` on Windows). An editor full of red hides the one error that is real.

**Phase:** 0
**Needs:** none
**Surface:** no

**Corrected 2026-11-22 (the plan review of `D00 T07 §2`):** zero errors could be reached by switching a rule off in `pyrightconfig.json` or a file-wide `# pyright: ignore`, which the section did not catch; its CI proof used a branch push `build.yml` does not run; its inventory could omit errors and its repairs named only narrowing and platform checks; a real fault's test was named, not shown to fail; one platform target could hide the other's branch; and the baseline's environment was not stated. The 480 errors (2026-11-21) and the 24 in `scripts/lib/run_guard.py` are historical measurements.

**Environment:** the scripts job's, on this machine and in CI alike: Python 3.14 with `python -m pip install -r scripts/requirements-icons.txt`, and pyright 1.1.414 (`python -m pip install pyright==1.1.414`, the PyPI package whose version pins the checker; **corrected 2026-11-22:** it said npm, but this machine and CI use the PyPI package), run as `pyright --pythonplatform Windows scripts` and `pyright --pythonplatform Linux scripts`.

**Build order:**

1. Sort the errors. Done when: the counts of one pinned run on each platform are written here, reconciled.
2. Fix them at their source. Done when: both platforms report 0 errors, with nothing suppressed.
3. Keep them out. Done when: the weakening scan refuses a suppression, and CI fails on a new error.

- [x] Run the pinned pyright in the environment above on each platform and record here the total, the count by rule and the count by file, the rule and file totals each summing to the run's total, with the pyright and Python versions and the commit (**corrected 2026-11-22, the plan review of `D00 T07 §2`**). Done when: the tables are written and reconcile.
- [x] Give every group of errors a source disposition here: a missing narrowing (an early return or an `assert` with its reason), an incorrect inferred or declared type (as `scripts/ci_status.py:196`, a dictionary made with `None` values that later holds lists), a wrong call signature, platform code (a `sys.platform` check pyright reads), a missing import in the environment, or a real fault (**corrected 2026-11-22, the plan review of `D00 T07 §2`**). Done when: every group in the tables has one.
- [x] Fix each real fault with a regression test that drives the affected behaviour through its caller, and record its name, its failure on the original code and its pass with the fix (**corrected 2026-11-22, the plan review of `D00 T07 §2`**). Done when: each real fault has the three recorded here. Cheaper substitute: a test named but not run against the old code, which the Done when refuses.
- [x] Fix every other error at its source by the disposition above, never by a `# type: ignore`, a `# pyright: ignore`, a file-level `# pyright:` comment or a change to `pyrightconfig.json` that lowers what it checks. Done when: `pyright --pythonplatform Windows scripts` and `pyright --pythonplatform Linux scripts` each print `0 errors` in the environment above, and an error planted inside a `sys.platform == "win32"` branch and one inside the other branch each fail their platform's run, recorded here. Cheaper substitute: lowering `pyrightconfig.json`'s strictness, which the Done when refuses.
- [x] Extend `scripts/weakening-scan.py` to report a `# pyright: ignore`, a file-level `# pyright:` comment and a `pyrightconfig.json` change that turns a rule off, lowers `typeCheckingMode` or drops a path from what it checks, as it reports a `type: ignore` today, with cases in `scripts/tests/test_weakening_scan.py` (**corrected 2026-11-22, the plan review of `D00 T07 §2`**). Done when: the tests pass, and fail on a diff that disables a rule and on one that adds a blanket ignore hiding a known error.
- [x] Run the pinned pyright on both platforms in the scripts job of `.github/workflows/build.yml`, with no `continue-on-error`, so a new error fails the job and the workflow and a new pyright release cannot (**corrected 2026-11-22, the plan review of `D00 T07 §2`**). Done when: a `workflow_dispatch` run on a branch holding one planted error shows the scripts job and the workflow failed, and a run after removing only that error shows both green, the pyright version and invocation the same in both, their URLs recorded here.
- [x] Commit: `scripts: pyright reports no errors (D00 T07 §2)`

**Baseline 2026-11-22 (pyright 1.1.414, Python 3.14.10, `scripts/requirements-icons.txt` installed, at `662b5b0`):** 385 errors on Windows and 402 on Linux, in 25 files each; the rule totals and the file totals each sum to the run's total.

| Rule | Windows | Linux |
| ---- | ------- | ----- |
| `reportOptionalSubscript` | 145 | 145 |
| `reportArgumentType` | 146 | 135 |
| `reportOptionalMemberAccess` | 44 | 44 |
| `reportAttributeAccessIssue` | 21 | 49 |
| `reportCallIssue` | 13 | 13 |
| `reportPossiblyUnboundVariable` | 5 | 5 |
| `reportGeneralTypeIssues` | 3 | 3 |
| `reportIndexIssue` | 3 | 3 |
| `reportOperatorIssue` | 2 | 2 |
| `reportIncompatibleMethodOverride` | 1 | 1 |
| `reportAssignmentType` | 1 | 1 |
| `reportOptionalOperand` | 1 | 1 |
| total | 385 | 402 |

| File | Windows | Linux |
| ---- | ------- | ----- |
| `scripts/tests/test_review_dispatch.py` | 137 | 133 |
| `scripts/tests/test_run_guard.py` | 52 | 68 |
| `scripts/tests/test_review_policy.py` | 34 | 34 |
| `scripts/lib/todo_graph.py` | 27 | 27 |
| `scripts/tests/test_todo_graph.py` | 24 | 24 |
| `scripts/ci_status.py` | 16 | 16 |
| `scripts/review-dispatch.py` | 19 | 13 |
| `scripts/tests/test_campaign_e2e.py` | 14 | 14 |
| `scripts/tests/test_finish.py` | 10 | 10 |
| `scripts/dispatch_hook.py` | 8 | 8 |
| `scripts/tests/test_ci_status.py` | 6 | 6 |
| `scripts/tool-guard.py` | 5 | 5 |
| `scripts/lib/host_caps.py` | 1 | 7 |
| `scripts/lib/run_guard.py` | 3 | 5 |
| `scripts/tests/test_build_icons.py` | 4 | 4 |
| `scripts/tests/test_guard_e2e.py` | 4 | 4 |
| `scripts/tests/test_hook_commands.py` | 4 | 4 |
| `scripts/tests/test_dispatch_hook.py` | 3 | 3 |
| `scripts/tests/test_tool_guard.py` | 3 | 3 |
| `scripts/todo-graph.py` | 3 | 3 |
| `scripts/tests/test_host_caps.py` | 1 | 4 |
| `scripts/build-icons.py` | 2 | 2 |
| `scripts/tests/test_findings_ledger.py` | 2 | 2 |
| `scripts/weakening-scan.py` | 2 | 2 |
| `scripts/tests/test_native_checks.py` | 1 | 1 |
| total | 385 | 402 |


**Dispositions 2026-11-22:** every group traced to its source, and no real fault found: each error was a value the code already guarded that pyright could not see guarded, or a type it inferred wrongly. Missing narrowing (`reportOptionalSubscript`, `reportOptionalMemberAccess`, most `reportArgumentType`): a `require(x is not None, ...)` pyright reads as returning, closed by one typed `present(value, message)` in `scripts/lib/todo_graph.py` that returns the value or raises the same refusal; elsewhere a narrowing at the use. Incorrect inferred types: `scripts/ci_status.py` `push_filters` (a dictionary started with `None` that holds lists, now typed) and the gate state read from JSON (`dict[str, Any]`, which JSON is). Call signatures: `Popen` given a dictionary of mixed options in `scripts/review-dispatch.py`, now named arguments under `sys.platform`; `inner_command` typed `tuple[str, bool] | None`. Platform code: `scripts/lib/host_caps.py`, the `Job` object of `scripts/lib/run_guard.py` and the Windows-only test classes, guarded by `sys.platform`, which also stops `host_caps`' Windows reads failing at `ctypes.windll` if they were ever called elsewhere. Library stubs: `sys.stdout.reconfigure` (now only on a `TextIOWrapper`, as a replaced stream has no `reconfigure`) and Pillow's `getextrema` (the alpha band's `getbbox()` instead). Missing imports: none with `scripts/requirements-icons.txt` installed. Real faults: none, so no regression test is owed; the sites checked closest were `scripts/lib/run_guard.py:733` (`owner_pid` refused when absent, line 717), `scripts/tool-guard.py:82` and `:461` (a `None` folder refused or returned before the join), `scripts/ci_status.py:1014` and `scripts/lib/todo_graph.py`'s `policy` (each set under the same condition it is read).

**After 2026-11-22:** `pyright --pythonplatform Windows scripts` and `pyright --pythonplatform Linux scripts` each print `0 errors, 0 warnings, 0 informations`; no `type: ignore`, `pyright:` comment, `cast` or configuration change was added, every test kept and assertions only added (3,000 checked lines). An error planted inside `scripts/lib/host_caps.py`'s `sys.platform == "win32"` branch failed the Windows run and passed Linux; one in its other branch failed Linux and passed Windows. The weakening scan's new cases fail with its configuration rule or its pyright pattern removed.

**CI proof 2026-11-22:** on the disposable branch `pyright-proof` (from `bb95044`), `workflow_dispatch` run 45056011422 at `4a7b382`, which holds one planted error in `scripts/lib/host_caps.py`, concluded `failure`, its scripts job failing at `Pyright over the scripts, as on Windows` (https://github.com/rizonesoft/Bosun/actions/runs/45056011422); run 45057069671 at `3ccefbe`, which removes only that error, concluded `success` with every job green (https://github.com/rizonesoft/Bosun/actions/runs/45057069671). Both ran the same pinned `pyright==1.1.414` steps. The branch's two commits cancel each other; its local copy is deleted, and the remote one is left for the owner, since the campaign's deny list refuses a remote branch deletion.

**Test checkpoint:** in the environment above, `pyright --pythonplatform Windows scripts` and `pyright --pythonplatform Linux scripts` each print `0 errors`; `python -m unittest scripts.tests.test_weakening_scan` exits 0 with its disabled-rule and blanket-ignore cases; `python scripts/run-tests.py` exits 0; and the two `workflow_dispatch` runs are recorded here (**corrected 2026-11-22, the plan review of `D00 T07 §2`**).

> **Verified:** 2026-11-22 | §2 | 54770ad16928dccb6ecfad57214b2877157a6793
> **Review:** `docs/reviews/d00-t07-s2.json`

## 3. Test fixtures in the scratch folder

> **Started:** 2026-11-22T20:35:00Z

Filed 2026-11-21 by `D00 T06 §1` from `todo/findings.md` (base line 24, round 2 of `D00 T01 §6`). Most of `tests/*_test.ps1` and `scripts/tests/*.py` put their fixtures in the system temporary folder and ignore `BOSUN_SCRATCH`, each with its own fallback where it reads it, so a run fills the system drive instead of the scratch drive the owner set.

**Phase:** 0
**Needs:** none
**Surface:** no

**Corrected 2026-11-22:** the original search (`git grep -nE 'GetTempPath|tempfile\.mkdtemp|\$env:TEMP' tests scripts/tests`) missed `tempfile.TemporaryDirectory` (40+ call sites in `scripts/tests/*.py`), swept in C# tests under `tests/Bosun.*` (out of scope for base line 24's PowerShell/Python finding), and expected the helpers (`scripts/lib/scratch.*`) to appear in a search of `tests` and `scripts/tests`. The migration covers every PowerShell and Python test file (`tests/*.ps1`, `tests/ci/*.ps1`, `tests/linux/*.ps1`, `tests/test_bosun_log.py`, `scripts/tests/*.py`), preserves `test_guard_e2e.py`'s `Path.home()` fixture (which must sit outside scratch/temp to test `tool-guard.py` refusal) and `test_campaign_e2e.py`'s `BOSUN_E2E_SCRATCH` override, adds `tests/scratch_test.ps1` to `.github/workflows/build.yml` (required by `tests/check_all_test.ps1`), and requires pyright 0 errors on both platforms.

**Corrected 2026-11-22 (the plan review of `D00 T07 §3`):** added six gaps from Astra's plan review: (1) run `python3 -B tests/test_bosun_log.py` on Linux/WSL with a scratch dir distinct from system temp and forward Windows `BOSUN_SCRATCH` to WSL in `scripts/check-all.ps1 -Wsl` via `WSLENV` (`BOSUN_SCRATCH/p`); (2) move `tests/queue_test.ps1`'s `/tmp/bosunrec$PID` WSL fixture under `$work` translated via `wslpath -a` and include `/tmp/` in the PowerShell grep; (3) give `scratch_dir()` / `scratch_tempdir()` an optional `root` and `create_root` override for `test_campaign_e2e.py`'s `BOSUN_E2E_SCRATCH` and test distinct general vs campaign roots including an initially missing campaign root; (4) use `$dir` (not undefined `$work`) in `tests/smoke_input_test.ps1:601` and verify it runs from `$dir`; (5) narrow the grep allowlist to only `tests/scratch_test.ps1` (fallback check) and the exact `dir=str(Path.home())` call in `scripts/tests/test_guard_e2e.py`, proving a restored `mkdtemp` at line 40 fails; (6) require both helpers to fail naming the selected scratch path, return no usable directory, and leave system temp untouched when child creation fails inside an existing scratch root.

**Build order:**

1. One way to ask for a scratch folder in each language. Done when: both helpers are written and tested in `tests/scratch_test.ps1` and `scripts/tests/test_scratch.py`, including child-creation failure.
2. Every PowerShell and Python test uses it, and `check-all.ps1 -Wsl` forwards `BOSUN_SCRATCH` to Linux. Done when: the grep check lists only the two exact allowlisted lines, and Windows, Python, Linux/WSL logger and pyright checks all pass.

- [x] Add `Get-BosunScratchRoot` and `New-BosunScratchFolder [-Name <string>]` to `scripts/lib/scratch.ps1`: resolves `BOSUN_SCRATCH` when set, non-whitespace, and an existing directory, else `[IO.Path]::GetTempPath()`; rejects a `-Name` outside `[A-Za-z0-9._-]` or equal to `.`/`..` so no path escapes the root; creates and returns a unique `bosun-<Name>-<guid>` directory removed by its caller; when child creation fails inside the selected root, throws an error naming that selected path, returns no usable directory, and leaves system temp untouched. Done when: `tests/scratch_test.ps1` (also added to `.github/workflows/build.yml`) passes covering a fake existing `BOSUN_SCRATCH`, unset/empty, missing path, file path, uniqueness, traversal refusal, child-creation failure (selected path named, system temp untouched), and cleanup. Cheaper substitute: falling back to system temp on creation failure or accepting raw path separators in `-Name`, which the Done when refuses.
- [x] Add `scratch_root(root=None, create_root=False) -> Path`, `scratch_dir(name: str = "test", *, root=None, create_root: bool = False) -> Path` and `scratch_tempdir(name: str = "test", *, root=None, create_root: bool = False) -> tempfile.TemporaryDirectory[str]` to `scripts/lib/scratch.py` (stdlib only, pyright-clean on Windows and Linux): uses `root` when provided (creating it when `create_root=True`, else requiring an existing directory or falling back when `root` is `None`), otherwise resolves `BOSUN_SCRATCH` when set, non-whitespace, and an existing directory, else `tempfile.gettempdir()`; validates `name` with the same safe-name rule; when child creation fails inside the selected root, raises an error naming that selected path, returns no usable directory, and leaves system temp untouched. Done when: `scripts/tests/test_scratch.py` passes covering all cases above (including distinct `BOSUN_SCRATCH` vs caller `root` override with an initially missing `create_root=True` root and child-creation failure leaving system temp untouched) and `pyright --pythonplatform Windows scripts` and `pyright --pythonplatform Linux scripts` each report `0 errors`. Cheaper substitute: reading `BOSUN_SCRATCH` once at import time or silently falling back on creation failure, which the Done when refuses.
- [x] Move every PowerShell test fixture in `tests/*.ps1`, `tests/ci/*.ps1` and `tests/linux/*.ps1` onto `New-BosunScratchFolder`: replace `tests/smoke_input_test.ps1`'s `Push-Location $env:TEMP` with `$dir` (verifying the scheduled-task case runs from `$dir`), move `tests/overlay_clone_test.ps1`'s `$sepGit` onto `New-BosunScratchFolder`, move `tests/queue_test.ps1`'s WSL `/tmp/bosunrec$PID` repo beneath `$work` translated via `wslpath -a` (keeping the queue-record assertion against that scratch-backed path), and have `scripts/check-all.ps1 -Wsl` forward a configured Windows `BOSUN_SCRATCH` to WSL in Linux form via `WSLENV` (`BOSUN_SCRATCH/p`), verified in `tests/check_all_test.ps1`. Done when: `git grep -nE 'GetTempPath|\$env:TEMP|/tmp/' -- 'tests/*.ps1' 'tests/ci/*.ps1' 'tests/linux/*.ps1'` lists only `tests/scratch_test.ps1`'s fallback assertion, and `tests/scratch_test.ps1`, `tests/check_all_test.ps1` and the touched PowerShell suites pass with `BOSUN_SCRATCH` set. Cheaper substitute: setting `TEMP` for the whole run or leaving `/tmp/bosunrec$PID` in `queue_test.ps1`, which the Done when refuses.
- [x] Move every Python test fixture in `scripts/tests/*.py` and `tests/test_bosun_log.py` onto `scratch_dir()` / `scratch_tempdir()`, keeping only the exact `dir=str(Path.home())` refusal fixture in `scripts/tests/test_guard_e2e.py` and wiring `scripts/tests/test_campaign_e2e.py` to pass `BOSUN_E2E_SCRATCH` via `root=`/`create_root=True` (with a unit test covering distinct `BOSUN_SCRATCH` and `BOSUN_E2E_SCRATCH` roots including an initially missing campaign root). Done when: `git grep -nE 'tempfile\.mkdtemp|tempfile\.TemporaryDirectory' -- 'scripts/tests/*.py' 'tests/*.py'` lists only the single `dir=str(Path.home())` line in `scripts/tests/test_guard_e2e.py` (restoring `mkdtemp` at line 40 fails this check), `python scripts/run-tests.py` and Linux/WSL `python3 -B tests/test_bosun_log.py` both pass with `BOSUN_SCRATCH` set to an existing directory distinct from system temp, and pyright reports `0 errors` on both platforms. Cheaper substitute: leaving `tempfile.TemporaryDirectory()` calls untouched or skipping the Linux logger run, which the Done when refuses.
- [x] Commit: `tests: fixtures in the scratch folder (D00 T07 §3)`

**Test checkpoint:** `pwsh tests/scratch_test.ps1`, `pwsh tests/check_all_test.ps1`, `python scripts/run-tests.py` with `BOSUN_SCRATCH` set, Linux/WSL `python3 -B tests/test_bosun_log.py` with `BOSUN_SCRATCH` set to an existing directory distinct from system temp, and `pyright --pythonplatform Windows scripts` / `pyright --pythonplatform Linux scripts` all exit 0; `git grep -nE 'GetTempPath|\$env:TEMP|/tmp/' -- 'tests/*.ps1' 'tests/ci/*.ps1' 'tests/linux/*.ps1'` lists only `tests/scratch_test.ps1` (fallback assertion) and `git grep -nE 'tempfile\.mkdtemp|tempfile\.TemporaryDirectory' -- 'scripts/tests/*.py' 'tests/*.py'` lists only the single `dir=str(Path.home())` line in `scripts/tests/test_guard_e2e.py` (proving a restored `mkdtemp` at line 40 fails).

> **Verified:** 2026-11-23 | §3 | 67568542b9ffab78affaa928a920e5103c64a2d5
> **Review:** `docs/reviews/d00-t07-s3.json`

## 4. check-all keeps a log and says its waits and SDK

> **Started:** 2026-11-22T22:22:30Z

Filed 2026-11-21 by `D00 T06 §1` from `todo/findings.md` (base lines 25, 28 and 37, raised by the completion reviews of `D00 T01 §6`, `D00 T01 §13` and `D00 T02 §3`). `scripts/check-all.ps1` keeps no log, so an unattended failure is read only by running its step again; a queued step waits in silence; and the .NET SDK it builds with is never printed, though CI prints it.

**Phase:** 0
**Needs:** none
**Surface:** no

**Corrected 2026-11-22:** specified the exact log root (`Get-BosunScratchRoot` from `scripts/lib/scratch.ps1`, failing before step 1 if the log cannot be opened and creating no log on `-List` or unknown `-Only`), passing the log path to `Enter-GateMark` (updating `tests/check_all_test.ps1`'s mark-output check and copy lists to include `lib/scratch.ps1`), live streaming of each step's stdout/stderr to both console and log via `Measure-Job`, the `waiting`/`started` queue lines (including no `started` line on queue timeout), and printing `dotnet --version` once before the first `dotnet` step (failing cleanly if `dotnet --version` exits non-zero, keeping `global.json` `allowPrerelease: false`).

**Corrected 2026-11-22 (the plan review of `D00 T07 §4`):** added six gaps from Astra's plan review: (1) live streaming proof with distinct stdout and stderr markers observed in both console and the active gate mark's log while the child is still running, plus post-timeout retention of earlier step output, timed-out markers, and diagnosis; (2) live queue-wait proof observing `waiting` before releasing the fake slot and `started` while the child is still running, checking reported duration against the controlled wait within tolerance, plus the fast-exit `StartedPath` branch after `Wait(200)`; (3) launch from outside `$repo` with fake `dotnet` recording CWD and call order, asserting one `--version` probe in `$repo` before the first `.NET` step, exact version in console and log, no probe without `.NET` steps, and probe-failure diagnostic in the named log with no later step; (4) distinct log per invocation proven by two gates on one scratch root both remaining readable; (5) exact log boundary (`-List` and unknown `-Only` create/print no log; log creation failure names attempted path and reason; once created, gate-mark and launch failures record the cause in the log and print `log: <path>`); (6) mid-run log write failure exits non-zero, names the partial log on console, cleans up the active job and gate mark, and runs no later step.

**Build order:**

1. Keep each run's output incrementally in a distinct scratch log and pass its path to the gate mark. Done when: `tests/check_all_test.ps1` proves live stdout+stderr streaming while running, post-timeout retention, two distinct readable logs on one root, the exact no-log/post-log boundary, and mid-run write-failure cleanup.
2. Report queue wait start and slot acquisition with measured wait duration live. Done when: `tests/check_all_test.ps1` observes `waiting` before slot release and `started` before child exit with duration matching the controlled wait, covers fast-exit `StartedPath`, and verifies no `started` on queue timeout.
3. Print the effective .NET SDK once before the first `dotnet` step from `$repo`. Done when: `tests/check_all_test.ps1` launched outside `$repo` proves one probe in `$repo` before the first `.NET` step, exact version in console and log, no probe without `.NET` steps, and failure diagnosis in the log with no later step.

- [x] Add live incremental run logging to `scripts/check-all.ps1` (using `Get-BosunScratchRoot` from `scripts/lib/scratch.ps1` with a unique filename per invocation): `-List` and unknown `-Only` (exit 2) create no log and print no `log:` line; a log-creation failure names the attempted path and reason and exits 1 before any step or mark; once created, pass the log path to `Enter-GateMark`, stream each step's stdout and stderr incrementally to both console and log during `Measure-Job` polling without losing output on failure/timeout, record gate-mark/missing-program/launch/step failures in the log and print `log: <path>` on pass and on every post-log failure; on a mid-run log write failure, stop the active job, remove the gate mark, print a console diagnostic naming the partial log, run no later step, and exit 1. Done when: `tests/check_all_test.ps1` proves live stdout+stderr markers in console and active-mark log while the child runs, post-timeout log retention of earlier step + markers + diagnosis, two distinct readable logs on one scratch root, no log on `-List`/unknown `-Only`, startup log-creation failure, post-log gate-mark failure recorded in log, and mid-run log write failure cleanup with non-zero exit. Cheaper substitute: buffering until the end, reusing one filename, or swallowing mid-run write errors, which the Done when refuses.
- [x] Add `-OnStarted` and `-OnTick` hooks to `Measure-Job` in `scripts/lib/job-measure.ps1` (firing `-OnStarted` both during `Wait(200)` and in the post-loop `StartedPath` check for fast steps) and emit `waiting <step>: queued for a build slot` before a queued step starts and `started <step>: got build slot after <N.N> s` when its slot arrives in `scripts/check-all.ps1` (written to both console and log). Done when: `tests/check_all_test.ps1` holds the fake slot until `waiting` is observed in console and log, releases it and holds the child until `started` is observed in both with reported duration matching the controlled wait within tolerance, covers a fast step finishing between polls, and verifies `waiting` without `started` on queue timeout. Cheaper substitute: printing wait info only after exit or hardcoding a duration, which the Done when refuses.
- [x] Before the first step whose program is `dotnet` in `scripts/check-all.ps1`, run `dotnet --version` once with working directory `$repo` (preserving `global.json`'s `"allowPrerelease": false` release-only selection), emit `dotnet-sdk: <version>` to console and log, and fail cleanly (recording the diagnostic in the log and printing `log: <path>`, running no build or later step) if `dotnet --version` exits non-zero. Done when: `tests/check_all_test.ps1` launched from outside `$repo` with a fake `dotnet` recording CWD and call order proves one `--version` probe in `$repo` before the first `.NET` step with exact version in console and log across two `.NET` steps, zero probes when no `.NET` step is selected, and probe failure diagnosed in the named log with no later step run. Cheaper substitute: running `dotnet --version` in the caller's CWD, printing it on every run, or ignoring a failing probe, which the Done when refuses.
- [x] Commit: `check-all: a log, queue waits and the SDK (D00 T07 §4)`

**Test checkpoint:** `pwsh tests/check_all_test.ps1` exits 0 covering live stdout+stderr streaming, post-timeout retention, distinct logs, log-boundary and mid-run write-failure cleanup, live `waiting`/`started` queue transitions and fast-exit `StartedPath`, and out-of-repo `dotnet --version` probe order and failure; removing the `log:` line fails the failing-step test.

> **Verified:** 2026-11-23 | §4 | 2267a86a87129f8722a746b5dd2e08efe61bf530
> **Review:** `docs/reviews/d00-t07-s4.json`

## 5. Weakening scan reads blobs in one batch and shows lines

> **Started:** 2026-11-22T23:52:30Z

Filed 2026-11-21 by `D00 T06 §1` from `todo/findings.md` (base lines 26 and 27, raised by the completion review of `D00 T01 §13`). `scripts/weakening-scan.py` starts one `git show rev:path` per side of every changed file, and its report names each signal without the lines it saw, so understanding a plain removal takes another command.

**Phase:** 0
**Needs:** none
**Surface:** no

**Corrected 2026-11-22:** specified binary-framed `git cat-file --batch` blob reading (for both `committed` and `uncommitted`, raising `CannotRead` / exit 2 on missing, non-blob, truncated, or non-zero git batch output and normalizing line endings to match `Text`), preserving the per-file `show` reader for fixture equivalence testing and the 1,000-file benchmark (against both per-file `show` and plain `git diff`), indented `    - L<n>: <text>` / `    + L<n>: <text>` context lines (at most 3 before and 3 after) under every committed and uncommitted signal without breaking existing `answered`/`unanswered` parsers, updating the two `show`-mocking tests in `scripts/tests/test_weakening_scan.py` to test batch reads (answering any `weakening-scan.py` signal if needed), and keeping `pyright` at 0 errors on Windows and Linux.

**Corrected 2026-11-22 (the plan review of `D00 T07 §5`):** added four gaps from Astra's plan review: (1) retain hunk boundaries in `parse_diff` and attach exact before/after lines to each `Signal` at creation (tested with two distant changes in one file, named cases, `pyrightconfig.json` edits below line 3, and visible final-newline-only side indication); (2) assert exact context lines and numbers (and no invented absent-side lines) across answered committed, unanswered committed, staged-then-edited-in-worktree, unstaged, and untracked signals; (3) exercise every batch refusal through `main()` (missing, non-blob, short payload, missing frame terminator, nonzero git exit after valid output -> exit 2 with no summary) plus a multibyte UTF-8 blob followed by a second blob proving byte-length framing alignment; (4) require the batch scan to beat the per-file `show` scan on the 1,000-file range with identical results and count all Git subprocess starts on the production path to prove `O(1)` process count independent of changed-file count.

**Build order:**

1. Read a range's blobs in one batch. Done when: the batch reader beats the per-file reader on a 1,000-file range with identical signals and `O(1)` git subprocess starts.
2. Show each signal's lines. Done when: the report holds the exact before and after lines for every signal kind and source state.

- [x] Read both sides of every changed file through one `git cat-file --batch` in `scripts/weakening-scan.py` (covering both `committed` and `uncommitted` git blobs, binary-framed by `<oid> <type> <size>\n<bytes>\n`, raising `CannotRead` / exit 2 on missing, non-blob, short payload, missing frame terminator, or nonzero git exit even after valid output, and requesting only existing sides for created/deleted files), and measure a 1,000-file range against the per-file `show` reader and against plain `git diff` of the same range, recording all three times here and verifying batch beats per-file with identical results. Done when: `scripts/tests/test_weakening_scan.py` reads identical signals from both readers on its fixtures, proves total Git subprocess starts do not grow per changed file (including created/deleted single-side reads), proves byte-length framing across a multibyte UTF-8 blob followed by a second blob, exercises all batch refusal modes through `main()` (exit 2, diagnostic, no summary), and the three 1,000-file times are written here with the batch reader's overhead above `git diff` named. **Corrected 2026-11-21 (completion review of `D00 T06 §1`):** the comparison with plain `git diff` the finding asked for was missing. **Measured 2026-11-22 (1,000-file modified Python range, identical signals):** plain `git diff` = `0.347 s`, batch `git cat-file --batch` scan = `1.346 s` (`+1.000 s` overhead above plain `git diff`), per-file `git show` scan = `135.491 s` (`100.6x` speedup). Cheaper substitute: parallel `git show` processes.
- [x] Retain hunk boundaries in `parse_diff` and attach each signal's own before and after lines (at most three of each, with line numbers, formatted as `    - L<n>: <line>` / `    + L<n>: <line>`, omitting absent sides and marking whichever side lacks a trailing newline on a final-newline-only edit) under every committed and uncommitted signal in the report, without breaking `answered`/`unanswered` line parsing. Done when: `scripts/tests/test_weakening_scan.py` verifies exact numbered before/after lines and the 3-line cap across two distant changes in one file, named cases, `pyrightconfig.json` edits below line 3, final-newline-only edits, answered and unanswered committed signals, staged-then-edited-in-worktree, unstaged, and untracked signals (including a removed assertion reading the removed line in the report).
- [x] Commit: `weakening-scan: one batch read and the lines each signal saw (D00 T07 §5)`

**Test checkpoint:** `python scripts/run-tests.py test_weakening_scan` exits 0, `pyright --pythonplatform Windows scripts` and `pyright --pythonplatform Linux scripts` report 0 errors, and with the lines removed from the report the removed-assertion test fails.

> **Verified:** 2026-11-23 | §5 | 319e1fe90bf8f4b731f6e5b5475e676ea7750199
> **Review:** `docs/reviews/d00-t07-s5.json`

## 6. Clone setup and pre-commit say what they did

> **Started:** 2026-11-23T00:47:30Z

Filed 2026-11-21 by `D00 T06 §1` from `todo/findings.md` (base lines 22 and 23, raised by the completion review of `D00 T01 §4`). `scripts/setup-clone.ps1` shows the earlier `core.hooksPath` only in its first run's undo line and keeps it nowhere, so the undo is lost with the terminal; `scripts/hooks/pre-commit` runs the plan check with no word before or after, and the comment above it claims a saving nobody measured. Extended 2026-11-22 by `D00 T06 §6` (base lines 209 and 213).

**Phase:** 0
**Needs:** none
**Surface:** no

**Corrected 2026-11-23:** specified exact CLI modes in `scripts/setup-clone.ps1` (`-Undo`, `-OverlayUpdates off|on`, `-CheckPrePush`, mutually exclusive), storing `bosun.previousHooksPath` in `--local` when replacing an earlier `core.hooksPath` (unsetting stale `bosun.previousHooksPath` when none existed) and printing the undo command on every run including `already on`, refusing `-Undo` from clobbering an unrelated `core.hooksPath`, printing `pre-commit: checking the plan...` and elapsed seconds to stderr in `scripts/hooks/pre-commit` with a real measured comparison replacing the 2026-11-12 comment, `-OverlayUpdates off|on` toggling `.private`'s `--local bosun.overlayAutoUpdate` (keeping `bosun.overlay` intact) read by `scripts/hooks/post-merge`, `-CheckPrePush` checking active `core.hooksPath`, tracked executable `scripts/hooks/pre-push`, `scripts/leak-scan.ps1`, `pwsh` on `PATH`, and runnable `gitleaks`, and registering `tests/linux/setup_clone_wsl_test.ps1` in `.github/workflows/build.yml`.

**Corrected 2026-11-23 (the plan review of `D00 T07 §6`):** added seven gaps from Astra's plan review: (1) use a separate `bosun.overlayAutoUpdate` setting in `.private` read by `scripts/hooks/post-merge` so `bosun.overlay` stays intact for `sync.ps1` and `overlay_rules_hook.py` and survives ordinary setup runs, tested against a local upstream with a pending commit; (2) test every `-CheckPrePush` refusal (wrong effective `core.hooksPath`, missing/untracked `pre-push`, missing executable bit in index and on Linux disk, missing `leak-scan.ps1`, missing `pwsh` on `PATH`, missing or failing `gitleaks`) with platform-appropriate remedies; (3) test fresh-clone stale-key cleanup via `-Repo` from another checkout and execute the printed undo command; (4) enforce write-then-read-back ordering (save `bosun.previousHooksPath` before replacing `core.hooksPath`; restore `core.hooksPath` before clearing `bosun.previousHooksPath`) and extend the dropped-write fixture for partial-completion retry; (5) test conflicting mode flags, invalid `-OverlayUpdates`, and that `-CheckPrePush` makes zero mutations on pass or refusal; (6) hold a fake `todo-graph.py` open in `test_pre_commit.py` to observe `pre-commit: checking the plan...` live before release and verify non-zero measured duration on pass and failure; (7) document `-Undo`, `-OverlayUpdates off|on`, `-CheckPrePush`, and `-Repo` in `scripts/setup-clone.ps1`, `AGENTS.md`, and `README.md`.

**Build order:**

1. Keep the earlier hooks path where it outlives the terminal, with atomic-ordered read-back and `-Undo`. Done when: `tests/setup_clone_test.ps1` covers fresh-clone stale-key cleanup, second-run undo, `-Repo` from another clone, dropped-write retry, and conflicting mode refusals.
2. Say the pre-commit's plan check and its measured elapsed time live. Done when: `scripts/tests/test_pre_commit.py` observes the start line live before release and verifies measured duration on pass and failure, and the comment in `scripts/hooks/pre-commit` cites the measured comparison.
3. Add `-OverlayUpdates off|on` (`bosun.overlayAutoUpdate` in `post-merge`), `-CheckPrePush` (Windows and Linux/WSL), and doc updates. Done when: `tests/setup_clone_test.ps1` and `tests/linux/setup_clone_wsl_test.ps1` pass across all refusal and pass cases, and `AGENTS.md` + `README.md` document the commands.

- [x] Store the earlier `core.hooksPath` in the clone's own config (`bosun.previousHooksPath`) and read it back BEFORE replacing `core.hooksPath` in `scripts/setup-clone.ps1` (clearing any stale `bosun.previousHooksPath` when `core.hooksPath` was unset), and print the undo command on every run, `already on` included; `-Undo` restores the earlier value and reads it back BEFORE removing `bosun.previousHooksPath` (or unsets `core.hooksPath` when none was stored and `core.hooksPath` is `scripts/hooks`, never clobbering an unrelated value); refuse conflicting mode switches before any mutation. Done when: `tests/setup_clone_test.ps1` covers stale-key cleanup on a fresh clone run twice via `-Repo` from another checkout (executing the printed undo command and reading both keys absent), restoring an earlier value with `-Undo`, dropped-write retry preserving the original path, refusing unrelated `core.hooksPath`, and refusing conflicting mode switches. Cheaper substitute: printing the undo on the first run only.
- [x] Print `pre-commit: checking the plan...` to stderr before the plan check in `scripts/hooks/pre-commit` and its measured elapsed seconds (`pre-commit: plan check passed in <N.N> s` / `failed in <N.N> s`) after; measure a plan commit in a scratch clone with `plan --check` alone vs `validate` + `plan --check` and replace the comment's unmeasured saving with the measured one. Done when: `scripts/tests/test_pre_commit.py` holds a stand-in `todo-graph.py` open to observe `pre-commit: checking the plan...` live before releasing it, verifies the final status and measured elapsed time on both pass and failure, and the comment in `scripts/hooks/pre-commit` cites the measured numbers.
- [x] Add `-OverlayUpdates off|on` to `scripts/setup-clone.ps1` using a separate `.private` `--local` setting `bosun.overlayAutoUpdate` (`false` when `off`, `true` when `on`) read by `scripts/hooks/post-merge` (keeping `bosun.overlay = true` intact so `scripts/sync.ps1`, `scripts/overlay_rules_hook.py`, and ordinary `setup-clone.ps1` runs preserve it), reading back after each write and keeping every local branch, stash, and ignored owner file untouched (base line 209). Done when: `tests/setup_clone_test.ps1` tests against a local upstream with a pending commit proving `off` survives another `setup-clone.ps1` run and leaves `.private` HEAD unchanged after `post-merge`, `on` lets the next `post-merge` advance `.private`, `bosun.overlay` plus branches/stashes/ignored files survive both, and invalid/missing `.private` or invalid `-OverlayUpdates` value is refused. Cheaper substitute: the manual undo line, as now, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §6`**).
- [x] Add `-CheckPrePush` to `scripts/setup-clone.ps1` (read-only: zero checkout mutations on pass or refusal) to verify effective `core.hooksPath`, tracked executable `scripts/hooks/pre-push` (index `100755` and disk execute bit on Linux), `scripts/leak-scan.ps1`, `pwsh` on `PATH`, and runnable `gitleaks` (`gitleaks version` exit 0) together in one command on Windows and Linux/WSL with platform-appropriate install guidance (never `winget` on Linux), and update `scripts/setup-clone.ps1` help, `AGENTS.md`, and `README.md` with `-Undo`, `-OverlayUpdates off|on`, `-CheckPrePush`, and `-Repo` (findings base line 213, from the completion review of `D00 T05 §4`). Done when: `tests/setup_clone_test.ps1` exercises every `-CheckPrePush` refusal (wrong effective `core.hooksPath`, missing/untracked `pre-push`, non-executable index bit, missing `leak-scan.ps1`, missing `pwsh` on `PATH`, missing or failing `gitleaks`), proves zero mutation on pass and refusal, and reads one pass when all are present; and `tests/linux/setup_clone_wsl_test.ps1` (written here and registered in `.github/workflows/build.yml` under the `linux` job) does the same on Linux/WSL including the disk execute bit (**corrected 2026-11-22, the completion review of `D00 T06 §6`**). Cheaper substitute: checking the hook alone, as now, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §6`**).
- [x] Commit: `setup: the earlier hooks path is kept, and pre-commit says its check (D00 T07 §6)`

**Test checkpoint:** `pwsh tests/setup_clone_test.ps1` and `python scripts/run-tests.py test_pre_commit` exit 0, and with the stored key removed the second-run undo test fails. On WSL, `wsl -d Ubuntu --cd . pwsh -NoProfile -File tests/linux/setup_clone_wsl_test.ps1` exits 0, which it cannot before the test is written (**corrected 2026-11-22, the completion review of `D00 T06 §6`**).

> **Verified:** 2026-11-23 | §6 | 6a30f6db89a7b6b367c4a81142e6a464fda3cd44
> **Review:** `docs/reviews/d00-t07-s6.json`

## 7. Build measurement compares against a baseline

Filed 2026-11-21 by `D00 T06 §1` from `todo/findings.md` (base lines 14, 15, 38 and 39, raised by the completion reviews of `D00 T01 §12` and `D00 T02 §4`). `scripts/measure-build.ps1` reports each label's median but no change against a chosen baseline or spread, is silent through a long step such as the 291 second Python suite, and `scripts/lib/measure-dotnet.ps1` times its warm one-file build once, so one slow sample (35.2 s for `dotnet-cpu-1`) decided alone; nothing explains why a candidate won or lost.

**Phase:** 0
**Needs:** none
**Surface:** no
**Started:** 2026-11-23

**Corrected 2026-11-23 (the plan review of `D00 T07 §7`):** added nine gaps from Astra's plan review: (1) record the measured `target` (`native` or normalized solution/project path, inferring `native` / `Bosun.slnx` for historical labels) so `-Baseline` only computes numeric deltas between matching `kind` and `target` and reports incompatible or missing workloads without a numeric delta; (2) have `-Diagnose` reproduce the `one file changed` workload (touching `$TouchFile`, passing `$msbuildArgs`, proving `Assert-BuildEvidence 'diagnostic build' 'first'`, verifying reported names/durations/ranking against MSBuild's `PerformanceSummary`, and proving same-invocation isolation from `steps`); (3) have the fake `.NET` test count 3 actual warm invocations with varying results, verify per-sample reuse evidence (`warm_used_nodes` / `warm_used_compiler` across all warm samples), and test failing only the 2nd warm invocation leaving the prior label intact and jobs cleaned up; (4) state explicitly that `-Tests` (Python suite) remains a single-run exception (`1` sample, spread `0.0`) while every native and `.NET` step stores both `spread_seconds` and `spread_peak_mb`; (5) render raw peak-memory samples (`Samples (MB)`) alongside `Samples (s)` and spreads in `docs/build-performance.md`, assert stdout, Markdown, and JSON separately against historical data, test `spread == |delta|` threshold (no warning) vs `spread > |delta|` (warning), and prove missing `-Baseline` leaves both files byte-identical; (6) emit 5-second progress during the `.NET` linger wait as well as `Invoke-Step` and `Wait-Run`, and read redirected stdout live while the child process is alive on both native and `.NET` paths; (7) validate conflicting CLI modes (`-Diagnose` without `-Dotnet`, `-Diagnose` with `-RenderOnly`/`-DecideOnly`, `-Baseline` with `-DecideOnly`, `-RenderOnly` with `-DecideOnly`) before any early return (exit 2, zero mutations) and extend fake-Bosun forwarding to `-Baseline` and `-Diagnose`; (8) define and test `-Diagnose` failure paths (unwritable scratch, failed build, missing `.binlog`, missing `PerformanceSummary`) exiting 1, preserving existing labels, and cleaning up partial logs and jobs; (9) run the real `Bosun.Core` `-Diagnose` test on an isolated scratch copy of the project with `-AllowOutsideServers` (default `$false`, recorded in `observed`) so the test never cleans checkout outputs or shuts down another build's servers, and verify the retained `.binlog` comes from that invocation.

**Build order:**

1. Sample every step alike (including the `.NET` warm one-file build; `-Tests` remains a single-run exception), record `target`, and store `spread_seconds` and `spread_peak_mb` (`max - min`) on every step alongside median and raw samples. Done when: `tests/measure_build_test.ps1` reads 3 distinct warm invocations, their exact median and spreads, per-sample reuse evidence, and failure on the 2nd warm sample leaving prior data intact.
2. Add `-Baseline <label>` comparison (guarded by matching `kind` + `target`, compatible with historical labels, validated before early returns) to stdout and `docs/build-performance.md` (which also renders raw peak-memory samples and spreads). Done when: `tests/measure_build_test.ps1` checks stdout, Markdown, and JSON separately across comparable, incompatible, historical, wide-spread, equal-threshold, and missing-baseline cases.
3. Announce every step before it starts and print elapsed time at least every 5 seconds during long steps (native `Invoke-Step`, `.NET` `Wait-Run`, and `.NET` linger wait) without counting printing in the measurement. Done when: `tests/measure_build_test.ps1` reads redirected stdout live while the child is running on both native and `.NET` paths and verifies the start line and >= 2 timely progress lines.
4. Add optional `-Diagnose` (and `-AllowOutsideServers` for isolated test runs) to `scripts/measure-build.ps1` / `scripts/lib/measure-dotnet.ps1` reproducing the `one file changed` workload in a separate untimed job, saving an MSBuild `.binlog` in `Get-BosunScratchRoot`, and naming the 3 most expensive targets or tasks from `PerformanceSummary`. Done when: `tests/measure_build_test.ps1` tests `-Diagnose` on an isolated scratch copy of `Bosun.Core` plus all refusal/failure paths and fake-Bosun switch forwarding.

- [x] Time the warm one-file build (`one file changed, after a build`) `-Samples` times in `scripts/lib/measure-dotnet.ps1`, touching `$TouchFile` before each warm build, checking build evidence and outside servers on each, disposing each warm job between samples, verifying per-sample reuse evidence (`warm_used_nodes` / `warm_used_compiler` across all warm samples), recording `target` (`native` or normalized solution/project path), and storing `spread_seconds` and `spread_peak_mb` (`max - min`) on every native and `.NET` step (`-Tests` remains a documented single-run exception with spread `0.0`). Done when: `tests/measure_build_test.ps1` runs `-Dotnet` with `-Samples 3` against a fake that counts 3 distinct warm invocations with varying durations, reads back 3 warm samples plus exact medians and both spreads on every step (including single-sample steps), checks per-sample reuse, and verifies failing the 2nd warm sample exits 1, leaves prior data intact, and cleans up jobs. (findings base line 39)
- [x] Add `-Baseline <label>` to `scripts/measure-build.ps1` (valid with measurement runs and `-RenderOnly`, refused with exit 2 when combined with `-DecideOnly`, refused with exit 1 if `<label>` is absent leaving JSON and Markdown byte-identical, forwarded through `bosun run --`): compare only labels whose `kind` and `target` match `<label>` (inferring `native` / `Bosun.slnx` for historical labels) and report incompatible or missing workloads without a numeric delta; emit signed time and memory deltas, both spreads (computed from raw samples when historical labels lack stored spread fields), and a warning strictly when `max(candidate_spread, baseline_spread) > |delta|` (no warning on equality) to both stdout and `docs/build-performance.md`, keeping raw seconds, raw peak-memory samples, and medians intact in JSON and Markdown. Done when: `tests/measure_build_test.ps1` checks stdout, Markdown, and JSON separately across measurement and `-RenderOnly` runs with historical labels, mixed comparable/incompatible labels, wide spread, equal threshold, clear win, and missing baseline. Cheaper substitute: comparing medians without the spread. (findings base line 14)
- [x] Announce each step before it starts and print elapsed seconds at least every 5 seconds during long steps (in `Invoke-Step` via `Measure-Job -OnTick`, in `Wait-Run`, and during the `.NET` linger wait in `scripts/lib/measure-dotnet.ps1`) via direct flushed stdout (`[Console]::Out.WriteLine`), without polluting function return values or counting the printing in the child job's measured `Seconds`. Done when: `tests/measure_build_test.ps1` reads redirected stdout live while a fake 12-second native step and a `.NET` linger/build step are still alive, observes the start line before exit and at least two progress lines on time, and verifies `Seconds` matches the child duration. (findings base line 15)
- [x] Add optional `-Diagnose` (refused with exit 2 without `-Dotnet` or with `-RenderOnly`/`-DecideOnly`, forwarded through `bosun run --`) and `-AllowOutsideServers` (default `$false`, recorded in `observed`) to `scripts/measure-build.ps1` / `scripts/lib/measure-dotnet.ps1` (supporting `.csproj` as well as `.slnx` in `-Solution`), running a separate untimed MSBuild diagnostic build reproducing the `one file changed` workload (touching `$TouchFile`, passing `$msbuildArgs`, proving `Assert-BuildEvidence 'diagnostic build' 'first'`, `-bl:<scratch-binlog>`, `-clp:PerformanceSummary`) in its own job after timed samples, keeping the `.binlog` under `Get-BosunScratchRoot`, and reporting the 3 most expensive targets or tasks with durations without altering `steps`; on unwritable scratch, build failure, missing `.binlog`, or missing `PerformanceSummary`, exit 1, preserve existing labels, and clean up partial files and jobs. Done when: `tests/measure_build_test.ps1` tests `-Dotnet -Diagnose -AllowOutsideServers` on an isolated scratch copy of `Bosun.Core` (verifying the retained `.binlog` comes from that invocation, 3 names/durations/ranking match `PerformanceSummary`, checkout outputs are untouched, and `steps` are unchanged in the same invocation), tests all CLI mode conflicts and `-Diagnose` failure paths, and verifies `-Baseline` and `-Diagnose` reach the queued child through fake `bosun`. (findings base line 38)
- [x] Commit: `measure: samples alike, a baseline, progress and a diagnostic run (D00 T07 §7)`

**Test checkpoint:** `pwsh tests/measure_build_test.ps1` exits 0, and with the spread warning removed its test fails.

> **Verified:** 2026-11-23 | §7 | 53799d1abecf9fc471fe915c0070a3e980584ff4
> **Review:** `docs/reviews/d00-t07-s7.json`

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

Filed 2026-11-21 by `D00 T06 §1` from `todo/findings.md` (base lines 32 and 33, raised by the completion review of `D00 T01 §7`). `.github/workflows/plan.yml` sets up Node and the icon packages before its two plan checks, so a stale plan fails later than it could; and its header says it runs on every change without saying that, by the owner's trunk-based choice, no required check stops a direct push to `main`. Extended 2026-11-22 by `D00 T06 §6` (base lines 216 and 217, at `3f36a14`).

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Run the plan checks first. Done when: a stale plan fails before Node is set up.
2. Say the policy and read it back. Done when: the preflight reads any required-check policy that is claimed.

- [ ] Move `todo-graph.py validate` and `plan --check` straight after Python is set up in the `plan-gates` job of `.github/workflows/plan.yml`, and measure a stale-plan push before and after. Done when: a pushed stale-plan fixture fails before the Node step, with both times recorded here.
- [ ] Say in the workflow header that it runs on every change but stops no direct push to `main` (no required checks, the owner's trunk-based choice), and have `scripts/campaign-preflight.ps1` read back the branch's required checks and fail if the documents claim one that is not set. Done when: `tests/preflight_test.ps1` with a fake protection that lacks a claimed required check fails, and passes when none is claimed.
- [ ] Keep one durable, redacted record per distinct secret finding from the full-history scan of `.github/workflows/plan.yml` (its introducing commit and the rule), beside GitHub's alerts (findings base line 216, at `3f36a14`, from the completion review of `D00 T05 §6`). Done when: `tests/ci/secret_history_selftest.ps1` plants two distinct findings, one of them twice, and reads one redacted record per distinct finding with its introducing commit and its resolution, the secret absent (**corrected 2026-11-22, the completion review of `D00 T06 §6`**). Cheaper substitute: the finding in that run's log only, as now, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §6`**).
- [ ] Schedule a reviewed gitleaks pin update in `.github/workflows/plan.yml` and a full-history rescan for each adopted rules release (findings base line 217, at `3f36a14`, from the completion review of `D00 T05 §6`). Done when: the workflow holds a scheduled trigger that runs the full-history rescan with the pinned rules, `tests/ci/secret_history_selftest.ps1` reads that schedule and its rescan, and each adopted rules release's reviewed pin update is recorded here with its rescan's result (**corrected 2026-11-22, the completion review of `D00 T06 §6`**). Cheaper substitute: a rescan only on push, as now, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §6`**).
- [ ] Commit: `ci: plan checks first and the push policy stated (D00 T07 §10)`

**Test checkpoint:** `pwsh tests/preflight_test.ps1` exits 0, and the stale-plan run's failure is recorded before the Node step. `pwsh tests/ci/secret_history_selftest.ps1` exits 0 (**corrected 2026-11-22 by `D00 T06 §6`**).

## 11. The overlay says where a value came from, and lists its keys

Filed 2026-11-22 by `D00 T06 §6` from `todo/findings.md` (base lines 207 and 208). `scripts/lib/overlay.ps1` (`Get-BosunOverlayValue`) returns which source won without its line, and nothing lists the keys of `.private/config.toml`.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Say and list in `scripts/lib/overlay.ps1`. Done when: `tests/overlay_test.ps1` reads the source with its line and the key names, never a value.

- [ ] Add one documented call to `scripts/lib/overlay.ps1` (`Get-BosunOverlayValue`) that reports which source won, the variable name or the file and line, without the value (base line 207). Done when: `tests/overlay_test.ps1` reads the variable or the file and line for each source and no value in the output. Cheaper substitute: the source word alone, as now, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §6`**).
- [ ] Add a names-only listing of the keys in `.private/config.toml` to `scripts/lib/overlay.ps1`, under the same validation as a lookup, so a misspelled key is seen (base line 208). Done when: `tests/overlay_test.ps1` lists a config's keys and reads the names only, and a misspelled key refused. Cheaper substitute: printing the file, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §6`**).
- [ ] Commit: `overlay: it says where a value came from, and lists its keys`

**Test checkpoint:** `pwsh tests/overlay_test.ps1` exits 0.

## 12. Sync previews what it would pull, and receipts each change

Filed 2026-11-22 by `D00 T06 §6` from `todo/findings.md` (base lines 210 and 211). `scripts/sync.ps1` prints a fixed line per repository, with no preview of pending commits and no before and after IDs.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Preview and receipt in `scripts/sync.ps1`. Done when: `tests/sync_test.ps1` reads the preview's IDs and commits and each receipt's branch and IDs.

- [ ] Add an optional preview to `scripts/sync.ps1` listing each repository's local and upstream short commit IDs and up to five pending commits, keeping the default check (base line 210). Done when: `tests/sync_test.ps1` runs the preview over a repository behind by seven and reads both IDs and five commits. Cheaper substitute: a count of commits only, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §6`**).
- [ ] Give `scripts/sync.ps1` one receipt per changed repository naming the destination branch and the before and after short IDs, never a URL or a remote's message, as `git push` output does (base line 211). Done when: `tests/sync_test.ps1` pushes to a remote that prints a message and reads each receipt's branch and before and after IDs, with neither the URL nor the remote's message in it (**corrected 2026-11-22, the completion review of `D00 T06 §6`**). Cheaper substitute: the fixed text, as now, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §6`**).
- [ ] Commit: `sync: it previews what it would pull, and receipts each change`

**Test checkpoint:** `pwsh tests/sync_test.ps1` exits 0.

## 13. A leak refusal for a ref name says to rename the ref

Filed 2026-11-22 by `D00 T06 §6` from `todo/findings.md` (base line 214). `scripts/leak-scan.ps1` always says to rewrite the commits, even for findings in ref names only. Split 2026-11-22 into `§13` and `§22` by the completion review of `D00 T06 §6`, one shippable change each.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Name the fix in `scripts/leak-scan.ps1`. Done when: `tests/leak_scan_test.ps1` reads the rename advice for a ref-name finding.

- [ ] Have a refusal of `scripts/leak-scan.ps1` whose only findings are in ref names tell the owner to rename the ref, not to rewrite commits (findings base line 214, from the completion review of `D00 T05 §4`). Done when: `tests/leak_scan_test.ps1` pushes a ref whose name holds a finding and reads the rename advice and no rewrite advice. Cheaper substitute: one message for every finding, as now, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §6`**).
- [ ] Commit: `scripts: a leak refusal for a ref name says to rename the ref (D00 T07 §13)`

**Test checkpoint:** `pwsh tests/leak_scan_test.ps1` exits 0.

## 14. The token: a process-scoped launch and one vault read

Filed 2026-11-22 by `D00 T06 §6` from `todo/findings.md` (base lines 219 and 221 at `3f36a14`). `scripts/store-claude-token.ps1` stores the token as a user variable and reads the vault twice (the token, then the item's dates).

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Launch and read in `scripts/store-claude-token.ps1` and `scripts/start-run.ps1`. Done when: `tests/start_run_test.ps1` and `tests/preflight_test.ps1` read the scoped launch and one vault call.

- [ ] Offer an optional process-scoped launch through `op run` with a secret reference in `scripts/start-run.ps1`, so a campaign can run without the token in a user variable (findings base line 219, at `3f36a14`, from the completion review of `D00 T05 §7`). Done when: `tests/start_run_test.ps1` runs the launch with a fake `op` and reads the token given to the child only, no user variable written, and after the fake vault's value changes reads the next launch use the new one with no re-import (**corrected 2026-11-22, the completion review of `D00 T06 §6`**). Cheaper substitute: the persistent user variable alone, as now, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §6`**).
- [ ] Keep the token's issue day as a field of its 1Password recovery item and read both in one vault call in `scripts/store-claude-token.ps1` (findings base line 221, at `3f36a14`, from the completion review of `D00 T03 §23`). Done when: `tests/preflight_test.ps1` runs it with a fake `op` and reads one call asking for exactly the token and the issue day, and the pair updated in one place at renewal (**corrected 2026-11-22, the completion review of `D00 T06 §6`**). Cheaper substitute: a second call for the item's dates, as now, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §6`**).
- [ ] Commit: `scripts: the token launches process-scoped and is read in one call`

**Test checkpoint:** `pwsh tests/start_run_test.ps1` and `pwsh tests/preflight_test.ps1` exit 0.

## 15. Campaign preflight shows the required checks beside its flags

Filed 2026-11-22 by `D00 T06 §6` from `todo/findings.md` (base line 226 at `3f36a14`). `scripts/campaign-preflight.ps1` reads three protection flags and never the required checks or their app pins.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Read back the checks in `scripts/campaign-preflight.ps1` (`Test-Protection`). Done when: `tests/preflight_test.ps1` reads each required check and its app pin.

- [ ] Show every required check and its app pin beside the three campaign flags in the protection read-back of `scripts/campaign-preflight.ps1` (`Test-Protection`), so a missing check is seen before a run (base line 226, at `3f36a14`). Done when: `tests/preflight_test.ps1` reads each required check and app pin, and refuses a branch with a check missing. Cheaper substitute: the three flags alone, as now, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §6`**).
- [ ] Commit: `scripts: campaign preflight shows the required checks`

**Test checkpoint:** `pwsh tests/preflight_test.ps1` exits 0.

## 16. Review rounds list their verdicts, and a lookup by commit

Filed 2026-11-22 by `D00 T06 §6` from `todo/findings.md` (base line 227 at `3f36a14`). `scripts/review-dispatch.py --list-rounds` prints the reviewer, candidate, run and seconds of each change review, with no verdict and no lookup by commit.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. List verdicts in `scripts/review-dispatch.py`. Done when: `scripts/tests/test_review_dispatch.py` reads each round's verdict and a lookup by full SHA.

- [ ] Show each returned change review's terminal verdict on its `--list-rounds` line of `scripts/review-dispatch.py`, and give one read-only lookup by full SHA (base line 227, at `3f36a14`). Done when: a new case of `scripts/tests/test_review_dispatch.py` (`python -m unittest scripts.tests.test_review_dispatch`) reads each round's verdict on its line, and for one SHA each matching record with its verdict and its repository-relative path (**corrected 2026-11-22, the completion review of `D00 T06 §6`**). Cheaper substitute: the verdict read from the findings file by hand, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §6`**).
- [ ] Commit: `review-dispatch: rounds list their verdicts, and a lookup by commit`

**Test checkpoint:** `python -m unittest scripts.tests.test_review_dispatch` exits 0.

## 17. The CI gate's report latency measured, its active step shown

Filed 2026-11-22 by `D00 T06 §6` from `todo/findings.md` (base line 228 at `3f36a14`). `scripts/ci_status.py` shows the active job, not its step, with an extra call per run, and its completion-to-report latency is unmeasured.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Measure and show the step in `scripts/ci_status.py`. Done when: `scripts/tests/test_ci_status.py` reads the step from one call, and the latency is recorded here.

- [ ] Measure the completion-to-report latency of `scripts/ci_status.py --gate` against `gh run watch` on the same live runs (median and 95th percentile), and show the active step from the one call it makes (base line 228, at `3f36a14`). Done when: a new case of `scripts/tests/test_ci_status.py` reads the active step from the jobs payload with no extra `gh` call, and the measured median and 95th percentile are recorded here, each at least as good as `gh run watch`'s on the same runs, under 1,000 calls an hour (**corrected 2026-11-22, the completion review of `D00 T06 §6`**). Cheaper substitute: the job name from a second call, as now, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §6`**).
- [ ] Commit: `ci-status: the gate's latency measured, its active step shown`

**Test checkpoint:** `python -m unittest scripts.tests.test_ci_status` exits 0, and the measurement is in this section.

## 18. The run hold's cold start measured against Awake

Filed 2026-11-22 by `D00 T06 §6` from `todo/findings.md` (base line 229 at `3f36a14`). `scripts/lib/hold-awake.ps1` compiles its code on every start of `scripts/start-run.ps1`, unmeasured. Split 2026-11-22 into `§18` and `§23` by the completion review of `D00 T06 §6`, one shippable change each.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Measure the hold in `scripts/lib/hold-awake.ps1`. Done when: the cold start over 30 launches and the Awake comparison are recorded here.

- [ ] Measure the cold start of the hold in `scripts/start-run.ps1` and `scripts/lib/hold-awake.ps1` (invocation to a confirmed `held`, p95 over 30 launches) against the standalone Awake tool (base line 229, at `3f36a14`). Done when: `tests/hold_awake_test.ps1` passes, and the 95th percentile over 30 cold launches and the Awake comparison are recorded here, the cold start cut where it is slower (**corrected 2026-11-22, the completion review of `D00 T06 §6`**). Cheaper substitute: one warm launch, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §6`**).
- [ ] Commit: `scripts: the run hold's cold start measured against Awake (D00 T07 §18)`

**Test checkpoint:** `pwsh tests/hold_awake_test.ps1` exits 0, and the measurement is in this section.

## 19. The run guard: a quicker look, suspended reads and a watch view

Filed 2026-11-22 by `D00 T06 §6` from `todo/findings.md` (base lines 232, 233 and 235 at `3f36a14`). `scripts/lib/run_guard.py` (`look`) polls once a second, starts each read before assigning it to its job, and `scripts/run-guard.py status` prints once.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Look, start and watch in `scripts/lib/run_guard.py` and `scripts/run-guard.py`. Done when: `scripts/tests/test_run_guard.py` reads each.

- [ ] Bring a gate that exits just after a poll to `look` in `scripts/lib/run_guard.py` within 250 ms at p95, waiting on a file change instead of a one-second poll, with 90% fewer reads (base line 232, at `3f36a14`). Done when: a new case of `scripts/tests/test_run_guard.py` (`python -m unittest scripts.tests.test_run_guard`) ends waits and reads `look` report within 250 ms at the 95th percentile over 100 exits, and a quiet nine-minute look's reads are recorded here at least 90% fewer, a recovery scan beside the file-change wait (**corrected 2026-11-22, the completion review of `D00 T06 §6`**). Cheaper substitute: a shorter poll interval, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §6`**).
- [ ] Start each read of `run` in `scripts/lib/run_guard.py` suspended and assign it to its job before it runs (`CREATE_SUSPENDED`, then resume), so no descendant escapes, and measure the whole hook answer against its deadline (base line 233, at `3f36a14`). Done when: a new case of `scripts/tests/test_run_guard.py` starts a read whose first act starts a child and reads zero survivors after a timeout in the parent-alive and parent-exited cases, and the whole hook answer on a three-second budget, cleanup and later reads included, measured within five seconds (**corrected 2026-11-22, the completion review of `D00 T06 §6`**). Cheaper substitute: assigning after start, as now, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §6`**).
- [ ] Add an opt-in watch view to `scripts/run-guard.py status` that redraws the waits every few seconds and exits when they clear, beside `look`, and a concise human view naming each wait once, the JSON kept (base line 235, at `3f36a14`). Done when: a new case of `scripts/tests/test_run_guard.py` runs the watch over a clearing wait and reads a redraw at each interval before it exits, each wait named once, the JSON unchanged (**corrected 2026-11-22, the completion review of `D00 T06 §6`**). Cheaper substitute: a loop around status in a shell, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §6`**).
- [ ] Commit: `run-guard: a quicker look, suspended reads and a watch view`

**Test checkpoint:** `python -m unittest scripts.tests.test_run_guard` exits 0.

## 20. The findings ledger: one lookup, derived totals, stable owners

Filed 2026-11-22 by `D00 T06 §6` from `todo/findings.md` (base lines 224, 225, 230, 236, 237 and 239 to 242 at `3f36a14`). `scripts/findings-ledger.py` reports failures and one total; a disposition is plain text (`todo/findings.md`), the classification repeats it word for word, an owner is found by its ordinal, and nothing shows a finding beside its owner in one view (`docs/reviews/00-workspace/D00-T06-s2.md` and the later records).

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Derive and look up in `scripts/findings-ledger.py`. Done when: `scripts/tests/test_findings_ledger.py` reads the lookup, the totals, the stable owner and the derived disposition.

- [ ] Make each scheduled disposition's section in `todo/findings.md` a link to its owning checklist in rendered Markdown, which `scripts/findings-ledger.py` still parses (base line 224, at `3f36a14`). Done when: a new case of `scripts/tests/test_findings_ledger.py` (`python -m unittest scripts.tests.test_findings_ledger`) settles a linked disposition and refuses a link to another section. Cheaper substitute: plain text, as now, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §6`**).
- [ ] Report the disposition counts and the scheduled findings grouped by owning section in the success line of `scripts/findings-ledger.py`, derived from the classification lines so no evidence carries a hand-kept subtotal (base lines 225 and 239, at `3f36a14`, which repeat each other). Done when: a new case reads the disposition counts and the scheduled findings grouped by owning section, split into existing and new sections, equal to the classification's (**corrected 2026-11-22, the completion review of `D00 T06 §6`**). Cheaper substitute: a total alone, as now, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §6`**).
- [ ] Add one query to `scripts/findings-ledger.py` by base commit and line that prints the finding, its disposition, the owning item with its decisive check and thresholds, and the evidence location, so a triage record such as `docs/reviews/00-workspace/D00-T06-s2.md` need not be read beside it (base lines 230, 236 and 240, at `3f36a14`, which repeat each other). Done when: a new case queries a scheduled line and reads all four, the item's threshold among them, and a five-second result against a 500 ms obligation marked unmet (**corrected 2026-11-22, the completion review of `D00 T06 §6`**). Cheaper substitute: three files read by hand, as now, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §6`**).
- [ ] Find the owning item in `scripts/findings-ledger.py` (`record_problem`) by the base line it cites rather than by its ordinal, so inserting an unrelated item needs no classification edit (base lines 237 and 242, at `3f36a14`, which repeat each other). Done when: a new case inserts an item above the owner and reads the settlement still valid with no edit. Cheaper substitute: the ordinal, as now, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §6`**).
- [ ] Make the strike's owner the one authoritative edit in `scripts/findings-ledger.py`, the classification's disposition derived from it, so a reassignment is one change (base line 241, at `3f36a14`). Done when: a new case reassigns a strike and reads the classification follow with no second edit. Cheaper substitute: repeating the disposition word for word, as now, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §6`**).
- [ ] Commit: `findings-ledger: one lookup, derived totals, stable owners`

**Test checkpoint:** `python -m unittest scripts.tests.test_findings_ledger` exits 0.

## 21. A stamped section's text stays as stamped

Filed 2026-11-22 by `D00 T06 §6` from `todo/findings.md` (base line 238 at `3f36a14`). `scripts/todo-graph.py stamp` (`scripts/lib/todo_graph.py`) records no digest of the section's text, so a replace across a TODO file rewrote stamped `D00 T06 §3` unseen (restored in `e7ae9dc`).

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Digest at the stamp in `scripts/lib/todo_graph.py`. Done when: `scripts/tests/test_todo_graph.py` reads a changed stamped section refused.

- [ ] Record the section's digest at the stamp in `scripts/lib/todo_graph.py` (excluding the lines `stamp`, `unstamp` and `repair` append) and have `scripts/todo-graph.py validate` refuse a stamped section whose text changed (base line 238, at `3f36a14`). Done when: a new case of `scripts/tests/test_todo_graph.py` (`python -m unittest scripts.tests.test_todo_graph`) changes a stamped section's build step and reads `validate` refuse it, and appends a `Repair cycle` note and reads it accepted. Cheaper substitute: a check of the checklist only, which misses the replace that hit §3, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §6`**).
- [ ] Commit: `todo-graph: a stamped section's text stays as stamped`

**Test checkpoint:** `python -m unittest scripts.tests.test_todo_graph` exits 0.

## 22. Machine-path findings name the commit that introduced them

Filed 2026-11-22 by `D00 T06 §6` from `todo/findings.md` (base line 223 at `3f36a14`). `scripts/machine-path-check.ps1` names a finding's file and line, not the commit that introduced it. Split 2026-11-22 from `§13` by the completion review of `D00 T06 §6`, one shippable change each.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Name the commit in `scripts/machine-path-check.ps1` (`Add-Entry`). Done when: `tests/machine_path_test.ps1` reads the introducing commit's short ID.

- [ ] Name the abbreviated commit that introduced a machine-path finding in `scripts/machine-path-check.ps1` (`Add-Entry`), as gitleaks does (findings base line 223, at `3f36a14`, from the completion review of `D00 T05 §8`). Done when: `tests/machine_path_test.ps1` adds a path over two commits and reads the first commit's short ID beside the file and line. Cheaper substitute: the file and line alone, as now, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §6`**).
- [ ] Commit: `scripts: a machine-path finding names its commit (D00 T07 §22)`

**Test checkpoint:** `pwsh tests/machine_path_test.ps1` exits 0.

## 23. Default campaign badges shipped for a machine without Python

Filed 2026-11-22 by `D00 T06 §6` from `todo/findings.md` (base line 234 at `3f36a14`). `scripts/start-run.ps1` renders campaign badges only where Python and its packages are. Split 2026-11-22 from `§18` by the completion review of `D00 T06 §6`, one shippable change each.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Ship the badges for `scripts/start-run.ps1` (`-InstallShortcut`). Done when: `tests/start_run_test.ps1` reads two distinct shipped icons with Python missing.

- [ ] Ship the two default colour-only campaign badges, built from the masters, for `scripts/start-run.ps1` (`-InstallShortcut`), so a machine without Python or the icon packages still gets distinct runner icons (base line 234, at `3f36a14`). Done when: `tests/start_run_test.ps1` installs the shortcut with Python missing and reads a distinct shipped badge. Cheaper substitute: the plain icon, as now, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §6`**).
- [ ] Commit: `scripts: default campaign badges shipped (D00 T07 §23)`

**Test checkpoint:** `pwsh tests/start_run_test.ps1` exits 0.

## Verification

- Every row above is `[x]` through `stamp`.
- `python scripts/todo-graph.py validate` and `python scripts/todo-graph.py plan --check` pass.
