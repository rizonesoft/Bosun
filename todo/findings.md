# Findings

Unscheduled defects noticed while working on another section. Record each entry with `file:line` and strike it with its `DNN TNN §N` reference once scheduled via `add-todo`.

- `scripts/ci_status.py`: an optional attended watch mode with three second polling, like `gh run watch`. The campaign polls every 60 seconds by design, so this waits for someone to want it (D00 T03 §1 completion review, 2026-11-07).
- `scripts/campaign-hold.ps1`: an optional campaign-bound hold that restores itself within one heartbeat of the run ending, with a maximum expiry, as PowerToys Awake does (D00 T03 §10 completion review, 2026-11-08).
- `scripts/campaign-hold.ps1`: one status command showing the hold's age, its settings and the exact restore command, later on the panel's machine page (D00 T03 §10 completion review, 2026-11-08).
- `scripts/campaign-preflight.ps1`: show local failures within five seconds and each slow probe's elapsed time, measured before optimizing (D00 T03 §10 completion review, 2026-11-08).
- `scripts/campaign-preflight.ps1`: end a failing run with one ordered list of fixes, elevated steps and owner confirmations apart, then the command that rechecks them (D00 T03 §10 completion review, 2026-11-08).
- `scripts/review-dispatch.py:1308`: show each pass's `seconds` in plain `--list-rounds` output, marking older records as unmeasured, so tuning the schedule needs no JSON (D00 T01 §2 completion review, 2026-11-08).
- `scripts/review-dispatch.py:265`: a review of a change that belongs to no TODO section is refused ("exactly one returned plan review is required before rounds"), though the `independent-review` skill sends every nontrivial change here. Either give the dispatcher a one-pass mode for such changes, or say in the skill that they go to `codex exec --sandbox read-only` (noticed while reviewing the launcher change, 2026-11-10).
- `pyrightconfig.json`: pyright now resolves `scripts/lib`, and reports 24 type errors in `scripts/lib/run_guard.py` and its callers (optional values passed where a value is required); none is a known runtime fault (2026-11-10).
- `scripts/review-dispatch.py:456`: name the source file and line of each carried settled item in the prompt block, so a stale decision is one navigation away (D00 T01 §2 completion review, 2026-11-08).
- `scripts/measure-build.ps1:75`: show each label's change in time and memory against a chosen baseline, with the sample spread and a warning when the spread is wide, keeping the raw samples and medians (D00 T01 §12 completion review, 2026-11-12).
- `scripts/measure-build.ps1:193`: announce each step before it starts and show elapsed time at least every five seconds during a long one, such as the 291 second Python suite, without touching the measurement (D00 T01 §12 completion review, 2026-11-12).
- `scripts/build.ps1:45`: compare at least ten no-op and one-file builds against `cmake --build --preset release` in an already initialised developer shell, and cut what the script adds (it configures on every run) while keeping automatic reconfiguration (D00 T01 §12 completion review, 2026-11-12).
- `scripts/review-dispatch.py:616`: a prompt file that is not UTF-8 ends the dispatcher in a traceback (`UnicodeDecodeError`) and exit 0 from a pipeline; refuse it with exit 2, naming the file and that it must be UTF-8 (noticed dispatching round 1 of D00 T03 §13, 2026-11-12).
