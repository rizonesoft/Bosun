---
schema_version: 1
id: campaign-readiness
domain: 00-workspace
status: active
title: "TODO-03 -- Campaign readiness"
---

# TODO-03 -- Campaign readiness

> **Goal:** An unattended `process plan` runs to "implementation complete" with no prompt, no permanent dead end, no unlimited retry, no spend on paid APIs and no false green, and hands the owner one acceptance list.

> **Current state:** Measured 2026-11-07. An independent review and the code agree on these faults. `scripts/review-dispatch.py:241` refuses after a completion review with gaps, and `scripts/lib/todo_graph.py:1222` only unstamps stamped sections, so such a section can never stamp. `scripts/review-dispatch.py:248` needs operator recovery on exhausted rounds, and `scripts/lib/run_guard.py:325` still offers that row. `scripts/lib/run_guard.py:47` always excludes `display-session`. `scripts/lib/run_guard.py:205` ends a run for good when no row is runnable. `scripts/ci_status.py:120` exits 0 with no successful run. `tests/test_core.cpp:22` ignores its arguments. `cmake` is not on PATH.

**Corrected 2026-11-10:** the owner retired two things `§10` and `§11` shipped, and their sections stay as history. The global heartbeat (`scripts/heartbeat.py`, `bosun-heartbeat`, the `Bosun heartbeat` task) is removed: each repo keeps its own watchdog task from `python scripts/run-guard.py install`, and nothing in a repo depends on Bosun. The `D:` volume and its `Bosun attach D` task are removed with `R:\Conclave.vhdx`: the preflight checks free space on the drive the repo is on.

## Inputs

- The owner's campaign decisions in `docs/specs/2026-11-07-control-panel-design.md`, "How does the unattended campaign run?".
- `scripts/lib/run_guard.py`, `scripts/run-guard.py`, `scripts/review-dispatch.py`, `scripts/lib/todo_graph.py`, `scripts/ci_status.py`, `.conclave/reviewers.toml`.
- `.claude/skills/process-plan`, `process-phase`, `process-todo-section` and `review-todo-section`, mirrored in `.agents/skills` and `.codex/skills`.
- -> XREF: D00 T01 §3 -- consumed by this file
- -> XREF: D00 T01 §4 -- consumes this file
- -> XREF: D00 T02 §1 -- consumes this file
- -> XREF: D00 T02 §3 -- consumes this file
- -> XREF: D01 T01 §1 -- consumes this file
- -> XREF: D99 T01 §6 -- consumes this file

## Outcome

- `python scripts/run-guard.py status --json` reports one of complete, waiting, parked or stopped, with what each waits for.
- `pwsh scripts/campaign-preflight.ps1` exits 0 only when every check below has passed on this machine, through the exact resume command.

**Adjacency:** all=not-applicable (internal tooling and contracts with no records of their own)

**Adjacency rationale:** This is the machinery that builds the product. The operator sees its results through `python scripts/run-guard.py status`; the panel stays out of each repo's workflow (owner decision, 2026-11-10).

## Implementation Order

| Order | Section | Deliverable | Depends On | Status |
| ----- | ------- | ----------- | ---------- | ------ |
| 1 | §1 | CI status that proves the commit | -- | [x] |
| 2 | §2 | One section disposition across graph, guard and skills | -- | [x] |
| 3 | §3 | Review repair cycles and parking | -- | [x] |
| 4 | §4 | Reviewer attempt limits and diagnostics | §2 | [x] |
| 5 | §5 | Usage limits wait and resume | §2 | [x] |
| 6 | §6 | Run end states and wake checks | §3, §5 | [x] |
| 7 | §7 | Owned session recovery | §5 | [x] |
| 8 | §8 | Campaign permissions | -- | [x] |
| 9 | §9 | Named core tests and CMake resolution | -- | [x] |
| 10 | §10 | Preflight and machine hold | §1, §4, §6, §7, §8, §9, §11, §12, §14, §15, D00 T01 §3 | [x] |
| 11 | §11 | One global heartbeat | -- | [x] |
| 12 | §12 | Review caps of 33 | §3 | [x] |
| 13 | §13 | CI gate recovery that names its commit | §1 | [x] |
| 14 | §14 | Stamp filed leftovers from the third cycle | §3 | [x] |
| 15 | §15 | One completion pass, as Steward and Recto run | §14 | [x] |
| 16 | §16 | Captured rate-limit output for the reviewer classifier | §4 | [ ] |
| 17 | §17 | Branch protection on main, read back | §8 | [x] |
| 18 | §18 | Campaign setup hardening from the preflight's completion review | §10 | [x] |
| 19 | §19 | Run guard holds an Antigravity run | -- | [x] |
| 20 | §20 | Runner badge and campaign names on the run shortcuts | -- | [x] |
| 21 | §21 | Run guard waits on a long local gate | -- | [x] |
| 22 | §22 | CI gate waits for a push's own run before starting one | -- | [x] |
| 23 | §23 | Token and issue day stored together | -- | [x] |
| 24 | §24 | Campaign hold restores itself and shows its state | -- | [x] |
| 25 | §25 | Preflight shows progress, times its checks and ends with one fix list | -- | [x] |
| 26 | §26 | Review dispatcher shows round times and settled sources, and takes non-section reviews | -- | [x] |
| 27 | §27 | CI gate sees a finished run sooner and says what it is waiting on | -- | [x] |
| 28 | §28 | Campaign hold as a Windows power request the run's process holds | §24 | [x] |
| 29 | §29 | Antigravity waits see their result within seconds | -- | [x] |
| 30 | §30 | Antigravity sees a failed guard, within the hook's time | -- | [x] |
| 31 | §31 | Shortcut install says one fix for each way a badge fails | -- | [x] |
| 32 | §32 | Guard status names each wait, its elapsed time and its output | -- | [x] |

## 1. CI status that proves the commit

> **Started:** 2026-11-07T21:15:10Z

Exit 0 means only that no red run was seen today, so a commit with no successful run reads as passing. A gate has to say which commit passed, and what the run does next. **Corrected 2026-11-07** after the plan review: the push range, a gate action and the exit table were added, and the item naming the wrong file was fixed.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Extend `scripts/ci_status.py`. Done when: `pending`, `none` and unavailable are separate from `green`, and a proven skip is separate from missing evidence.
2. Add `--sha`, `--base` and `--gate`. Done when: a green run of an older commit can't satisfy a newer one, and `--gate` prints one action.

- [x] Report a verdict of `green`, `red`, `pending`, `none` or `skipped` per workflow in `scripts/ci_status.py`. Done when: a new `scripts/tests/test_ci_status.py` covers an empty run list, all runs pending and an older green with a newer run pending, through both text and JSON output. Cheaper substitute: treating no red as green.
- [x] Add `--sha <commit>` in `scripts/ci_status.py`, reading runs with `gh run list --commit <sha>` so the lookup can't miss it, and judging by the newest attempt of that commit. Done when: tests prove an exact green passes, a green older commit doesn't, a commit outside the latest ten branch runs is still found, and a newer pending or failed run of the same commit decides.
- [x] Add `--base <commit>` in `scripts/ci_status.py`, reading the workflow's `paths` and `paths-ignore` at `--sha` and comparing every file changed in `base..sha`, to report `skipped` with the reason. Done when: tests prove a Markdown-only push is `skipped`, a code commit followed by a Markdown stamp in one push is not, and a missing base, an unreadable workflow or a failed diff reads `none`, never `skipped`. Cheaper substitute: checking only the last commit.
- [x] Return one overall exit in `scripts/ci_status.py`: red first as 1, then unavailable as 3, pending as 4, none as 5, and 0 only when every workflow is `green` or `skipped`. Done when: a table test covers mixed results, and the module docstring states this order.
- [x] Add `--gate` in `scripts/ci_status.py`, printing one action for a pushed commit: `proceed` on exit 0, `wait` on 3, 4 or 5, `repair` on red with attempts left, and `exhausted` after three. Done when: scenario tests walk each transition, including a repair that goes green. Cheaper substitute: rules only in skill text.
- [x] Hold the repair count in `build/ci-repairs.json` with an atomic replace, keyed by the first red commit, reset by a green. Done when: a test proves the count survives a new process, and a corrupt file reads as 0 attempts used with one warning line.
- [x] Bound `wait` in `--gate` with `--wait-minutes` in `scripts/ci_status.py`, polling every 60 seconds. Done when: a fake clock shows pending for the whole time ends in `wait`, and pending turning green ends in `proceed`.
- [x] Add the red-first policy to `.claude/skills/process-phase/SKILL.md` and its two mirrors: after each stamped section, push, run `--gate` with the pushed commit, which proves any skip against the last proven commit it recorded (**Corrected 2026-11-07:** this passed the previous remote head), and act on its one action. `exhausted` files the fault through `add-todo` and carries on with local gates, since the owner said the run must not block itself, and `D00 T03 §6` refuses `complete` while `main` is red. Done when: a test proves the three mirrors hold the same text and name each action.
- [x] Reset the repair budget durably in `gate_action` in `scripts/ci_status.py`, keeping gate state in one file, `build/ci-gate.json`: a green replaces the record, and when that fails it writes a `build/ci-gate.cleared` marker that the next read honours. When neither can be written, the action is `unverified`. Done when: `scripts/tests/test_ci_status.py` locks the file through a green, then in a separate process unlocks it and the next independent red gets a full three repairs. Cheaper substitute: a warning, which lets one locked file exhaust the next fault's budget.
- [x] Bound `--wait-minutes` in `scripts/ci_status.py` by the remaining time, in the sleep and in each tool call. Done when: tests with a fake clock prove a six-second budget sleeps at most six seconds, a slow tool's timeout shrinks to what remains, and fractions work.
- [x] Limit total waiting on one commit across every invocation to two hours, recording when it began in `build/ci-gate.json`, after which `--gate` prints `unverified`. Done when: tests across separate processes prove it. `unverified` carries on like `exhausted`, because the run must not block itself, and `D00 T03 §6` refuses `complete` while the head has no green. Cheaper substitute: a run stuck on `wait` forever when `gh` auth is lost.
- [x] Record the last proven commit in `build/ci-gate.json` and use it as the base when `--base` is omitted, so a restart after a push still has it. Done when: tests in separate processes prove a Markdown-only push after a crash is `skipped` with no `--base` given, and the skill's command needs none.
- [x] Print a line to stderr while waiting in `scripts/ci_status.py`, once at the first look and once per poll, with each workflow's verdict, elapsed and remaining time, keeping one JSON object on stdout. Done when: tests read both streams, and stdout still parses as one object. Cheaper substitute: thirty silent minutes.
- [x] Prove a skip only against the last proven commit in `scripts/ci_status.py`, so it means the workflow's inputs haven't changed since something passed. **Corrected 2026-11-07** after the plan review: this tracked the push boundary, which no record can hold through a crash between a push and its gate, and the question it answered was the wrong one. Done when: tests with A proven, then a failed or unverified B, then Markdown-only C, read `none` and keep the fault; A, B, then C fully reverting B reads `skipped`, since its inputs are A's; a real run of C still decides first; and no proven commit proves no skip. Cheaper substitute: the previous remote head, which proves a skip over a failed commit.
- [x] Write `build/ci-gate.cleared` by temporary file then replace as a JSON record, ignoring an incomplete one with a warning, in `scripts/ci_status.py`. Done when: tests prove a partial marker resets nothing, and a marker that can't be saved reads `unverified`.
- [x] Apply the deadline before the first tool call in `scripts/ci_status.py`, to `git` as well as `gh`, launching no call once it has passed. Done when: fake clock tests with slow tools prove total elapsed time stays within a six-second budget, and an expired two-hour limit launches no `gh` call and reads `unverified`.
- [x] Run the wait limit and base recovery tests in separate processes in `scripts/tests/test_ci_status.py`. Done when: both start a new interpreter per step.
- [x] Refuse `--workflow` with `--gate` in `scripts/ci_status.py`, so a pass always covers every gating workflow and the record it leaves means that. Done when: a test sees exit 2 naming why, and `--workflow` without `--gate` still works. Cheaper substitute: a proof recorded from a partial check.
- [x] Record when waiting began before the first lookup in `scripts/ci_status.py`, and clear it only on green or red. Done when: tests interrupt the first lookup and the start stands, alternating pending, none and unavailable across separate processes reaches `unverified` at exactly two hours, and a green or red leaves no wait behind.
- [x] Add `--use-proven` to `scripts/ci_status.py`, a read-only check for `--sha` that takes the recorded proven commit as its base and treats that commit itself as proven unless a real run says otherwise. Done when: tests prove a skipped build on a final Markdown stamp exits 0, code after it exits 5, a red run of the proven commit exits 1, and the record's bytes don't change.
- [x] Give every `unverified` a reason in `scripts/ci_status.py`, naming the elapsed time and limit or the state that couldn't be saved, on its own line before the action and as `reason` in JSON. Done when: tests read it in both forms for each way to reach it, and the action stays the last line.
- [x] Recognise an exact revert to the last proven commit as unchanged inputs in `skip_reason` in `scripts/ci_status.py`: an empty diff from a proven base, on a later commit, is a proven skip, and from any other base it still proves nothing. Done when: tests prove a pure revert with no other change reads `skipped` under `--gate` and `--use-proven`, and reads `none` with an explicit `--base`.
- [x] Apply the two hour limit to every gate call in `scripts/ci_status.py`, with or without `--wait-minutes`: no tool starts after it, and each stops at it. A call without `--wait-minutes` still looks once and doesn't poll. Done when: tests with slow tools and one second left prove total time stays within that second and no tool starts after it, and a call with time to spare keeps each tool's own limit.
- [x] Read `HEAD` from the `.git` files with no process in `scripts/ci_status.py`, covering a branch, a detached head, packed refs and a linked worktree, and bound any `git` call still needed by the recorded wait. Done when: tests against real repos match `git rev-parse` for all four layouts, `--sha HEAD` starts no tool past the limit, an unreadable `HEAD` past the limit gives a gate answer with no traceback and no tool, and a slow `git` with one second left costs at most that second. Cheaper substitute: a short timeout on the call.
- [x] Print the recovery when a workflow has no run and nothing proves a skip, as after a fresh clone, in `scripts/ci_status.py`: the `gh workflow run <file>` command, which starts a real run of `HEAD`, as `recovery` in JSON and a line before the action. Add it to the `wait` step in `.claude/skills/process-phase/SKILL.md` and its mirrors. Done when: tests read it in both forms naming only the workflows that need it, a real run then decides in a separate process, and a run that fails stays red.
- [x] Accept only `HEAD` or a full SHA with `--gate` in `scripts/ci_status.py`, since nothing needs another name and a wait is recorded by commit. Done when: tests prove a branch name is refused with exit 2 and a message saying what to pass, a new commit reached through `HEAD` or its full SHA gets its own wait after an older one expired, and `--sha main` still works without `--gate`.
- [x] Commit: `ci: status proves the commit`

**Test checkpoint:** `python -m unittest discover -s scripts/tests -p test_ci_status.py` exits 0, and fails if an empty run list exits 0 or a code commit before a Markdown stamp reads `skipped`.

> **Repair cycle:** 1: 2026-11-07: the completion review found the repair reset, the wait bound and wait feedback short (after: its completion review returned gaps)

> **Repair cycle:** 2: 2026-11-07: the completion review found a false skip, a partial marker and an unbounded wait (after: its completion review returned gaps)

> **Repair cycle:** 3: 2026-11-07: the completion review found partial checks, the first lookup's time and silent unverified results (after: its completion review returned gaps)

> **Repair granted:** 2026-11-07: the owner granted one more repair for two small gaps

> **Repair cycle:** 4: 2026-11-07: the completion review found an exact revert and calls with no wait unbounded (after: its completion review returned gaps)

> **Repair cycle:** 5: 2026-11-07: the completion review held that HEAD must respect the limit too (after: its completion review returned gaps)

> **Repair cycle:** 6: 2026-11-07: the completion review found branch names can't recover; nothing needs them (after: its completion review returned gaps)

> **Verified:** 2026-11-08 | §1 | 583766f2953f23ae508898c41f53c22b4bb10a2b
> **Review:** `docs/reviews/d00-t03-s1.json`

## 2. One section disposition across graph, guard and skills

> **Started:** 2026-11-07T20:03:09Z

The graph, the guard and the skills each decide differently what can run. One function decides, so a row can't be offered by one and refused by another. **Corrected 2026-11-07:** it also holds the dead-end check the guard kept as `stranded`, and an active remote session counts as a desktop, where this said console only.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Add `section_disposition` in `scripts/lib/todo_graph.py`. Done when: each open row has one state and reason.
2. Use it in `scripts/lib/run_guard.py`. Done when: `HOST_NEEDS` is gone.

- [x] Create `scripts/lib/host_caps.py` reporting `windows-host`, `arm64-host`, `office`, `libreoffice` and `display-session`. Done when: `scripts/tests/test_host_caps.py` proves each from faked probes. Cheaper substitute: a hardcoded list of what this machine has.
- [x] Report `display-session` in `scripts/lib/host_caps.py` only for an active, unlocked session of this process, through `WTSQuerySessionInformationW` and `OpenInputDesktop` showing the `Default` desktop. Done when: faked locked and disconnected sessions read false, a failed probe reads false, a non-Windows host reads false, and this machine reads true while unlocked. Cheaper substitute: assuming a logged-in user has a desktop.
- [x] Add `section_disposition(row, rows, caps)` in `scripts/lib/todo_graph.py`, keeping the existing `disposition(row, rows)` checks for dependencies and suspect ancestors, and returning `ready`, `waiting-host`, `waiting-person`, `waiting-dependency`, `needs-repair`, `parked` or `verified` with a reason. `needs-repair` covers both dead ends: a latest cycle whose completion returned gaps, and a latest cycle with its returned rounds at the pin's cap and no clean last round, without being confused with a clean last round awaiting completion. `parked` reads a `> **Parked:**` line, which `D00 T03 §3` adds. Done when: `scripts/tests/test_todo_graph.py` covers every Need in the closed list, including `store-account` and `attended`, and `resolve`, `classify` and plan generation keep their documented exit codes.
- [x] Replace `HOST_NEEDS` and `stranded` in `scripts/lib/run_guard.py` with `query disposition`, offering `ready` rows, and `needs-repair` rows once `D00 T03 §3` routes them (**Corrected 2026-11-07:** this said only `ready` rows), and keeping `real_rows` returning None when it can't tell. Done when: a test proves a `display-session` row runs with a desktop and waits without one, and an unreadable answer never ends a run. Cheaper substitute: adding `display-session` to an exclusion list.
- [x] Add `query disposition` to `scripts/todo-graph.py`, printing JSON with each open row's ref, state and reason, leaving `query ready`, `resolve` and `classify` unchanged. Done when: a test proves the guard and skills read the same output, and those three keep their documented results.
- [x] Add `query next` to `scripts/todo-graph.py`, printing the first row this session can run, by phase then table order, or null. Done when: a test with a host-blocked earlier phase names the later phase's row, a repairable row counts, and it prints null when nothing can run.
- [x] Choose the phase through `query next` at every step of `.claude/skills/process-plan/SKILL.md` and `process-phase/SKILL.md` and their mirrors, using `query disposition` for the leftovers. Done when: no skill names its own list of host Needs, and a test proves none chooses a phase from `query ready`.
- [x] Read unreadable findings or an unreadable review policy as `waiting-person` with the reason in `section_disposition` in `scripts/lib/todo_graph.py`, and never fabricate a cap, which `D00 T03 §3` shipped at `14254833`. Done when: `test_a_findings_file_that_cant_be_read_waits_and_recovers`, `test_a_cycle_whose_policy_cant_be_read_waits_and_spends_nothing` and `test_an_unusable_pin_refuses_rather_than_assuming_a_cap` in `scripts/tests/test_todo_graph.py` pass, and fail if the code is reverted.
- [x] Check that a registered program really exists, reading per-user and per-machine App Paths, in `app_path_exists` in `scripts/lib/host_caps.py`. Done when: `scripts/tests/test_host_caps.py` proves a stale entry reads false, a quoted path and a per-user entry read true, and both hives are read. Cheaper substitute: trusting that the registry key exists.
- [x] Re-read `query disposition` before each row in `.claude/skills/process-phase/SKILL.md` and its mirrors, remove the route through `resolve` exit 0, and say in `.claude/skills/process-plan/SKILL.md` and its mirrors that a section with completion gaps is repaired rather than passed over. Done when: a test reads each from all three mirrors, and `next_work` tests prove a host-blocked row is skipped and a repairable row chosen.
- [x] Test `session_active` and `input_desktop` in `scripts/lib/host_caps.py` through simulated Windows calls, covering a failed call, a disconnected session, a locked desktop and an open one. Done when: `scripts/tests/test_host_caps.py` fails if either wrapper always answers yes.
- [x] Test the real `query next` command in `scripts/tests/test_todo_graph.py`, including its null answer and exit status. Done when: removing its registration in `scripts/todo-graph.py` fails the test.
- [x] Commit: `graph: one section disposition`

**Test checkpoint:** `python -m unittest scripts/tests/test_todo_graph.py scripts/tests/test_host_caps.py scripts/tests/test_run_guard.py` exits 0, and fails if `section_disposition` returns ready for an `attended` row, a locked session reads as a desktop, or the guard offers a `waiting-host` row.

> **Repair cycle:** 1: 2026-11-07: the completion review found unreadable evidence read as ready; D00 T03 §3 fixed it (after: its completion review returned gaps)

> **Repair cycle:** 2: 2026-11-07: the completion review found the registry probe and the query next command unproven (after: its completion review returned gaps)

> **Verified:** 2026-11-07 | §2 | 9092587489aa58689a4d5ef03775694a18e923ec
> **Review:** `docs/reviews/d00-t03-s2.json`

## 3. Review repair cycles and parking

> **Started:** 2026-11-07T19:47:37Z

A section that fails review must either get a real chance to repair or step aside, never hold up the run in silence. The owner set the cap at three repair cycles. No section ever ships unapproved. **Corrected 2026-11-07:** this depended on §2, but §2 and every other section can only recover from a completion gap through this section, so it can't wait for them. It uses `section_disposition` from the §2 code in the tree.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Add `schedule.max_repair_cycles` to `.conclave/reviewers.toml`. Done when: the pin reads 3.
2. Add `repair`, `park` and `unpark` to `scripts/todo-graph.py`. Done when: a dead-end section starts a new cycle, parks at the cap, and comes back only when the operator grants it.

- [x] Set `max_repair_cycles = 3` in `.conclave/reviewers.toml` and report it from `--check-pin --json` in `scripts/review-dispatch.py`. Done when: a test reads it back, and a value over 3 is refused. Cheaper substitute: a constant in the skill text.
- [x] Add `python scripts/todo-graph.py repair '<ref>' --reason '<text>'` for an unstamped section whose completion returned gaps or whose rounds ran out, in `scripts/lib/todo_graph.py`, writing only the section's line, atomically, and restoring it if the plan sync fails. The findings file appears at the cycle's plan review, so nothing can disagree. Done when: a test proves `section_findings` names a new cycle file `D<NN>-T<NN>-s<N>-cycle<K>.md` with the old findings kept, an injected sync failure leaves the tree unchanged, and running it again spends no cycle. Cheaper substitute: deleting the findings file.
- [x] Offer `needs-repair` rows as work in `scripts/lib/run_guard.py`, and route them to `repair` in `.claude/skills/process-plan/SKILL.md`, `process-phase/SKILL.md` and their mirrors, since `D00 T03 §2` offers only `ready` rows until this lands. Done when: tests restart a run with only repairable work left and it starts the repair rather than ending, and read that route from all three mirrors of both skills.
- [x] Check Needs, dependencies and hosts before the dead end in `section_disposition` in `scripts/lib/todo_graph.py`, so `needs-repair` only ever means runnable now. Done when: tests prove a dead-end row that is `attended`, missing its host or waiting on a prerequisite reads that state, not `needs-repair`.
- [x] Refuse `repair` on a stamped section, on a section with no dead end, and past the cap with a message naming `park`, in `scripts/lib/todo_graph.py`. Done when: all three are tested, and the old `unstamp` route is unchanged.
- [x] Add a `> **Parked:** <date>: <reason>` line and `python scripts/todo-graph.py park '<ref>' --reason '<text>'`. Done when: `section_disposition` returns `parked`, its dependants return `waiting-dependency`, and `stamp` refuses a parked section.
- [x] Add `python scripts/todo-graph.py unpark '<ref>' --reason '<text>'` for the operator, removing `Parked:` and adding a `> **Repair granted:** <date>: <reason>` line that raises that section's cap by one, keeping all findings. Done when: a test goes through exhaustion, parking, a grant, a repair and a successful stamp, and no skill tells a run to use it. Cheaper substitute: deleting the line by hand, which leaves `repair` refusing at the cap.
- [x] Refuse `--record` in `scripts/review-dispatch.py` on a findings file older than the section's newest cycle file. `check_sequence` already reads one file per cycle, so a new file allows plan, rounds and completion again with no change there. **Corrected 2026-11-07:** this said to change `check_sequence`. Done when: a test runs a whole second cycle and refuses mixing findings across cycles.
- [x] Teach `.claude/skills/review-todo-section/SKILL.md` and its mirrors: repair at a dead end, park at the cap, then carry on. Done when: a test reads that rule from all three.
- [x] Persist the repair cycle in a `> **Repair cycle:** <K>: <date>: <reason>` line, and count those lines with `Unstamped:` notes through a new `reopenings` in `section_findings`, `build_start` and `review_dead_end` in `scripts/lib/todo_graph.py`, while `suspects` and `settled` keep counting `Unstamped:` notes only, since a section never stamped has no consumers to suspect. Done when: a test goes completion gaps, repair, clean reviews, then a successful `stamp` and `validate`, with the earlier findings kept. Cheaper substitute: changing only the dispatcher.
- [x] Write a fresh `> **Started:**` line after the repair plan review in `.claude/skills/process-todo-section/SKILL.md` and its mirrors. Done when: a test proves the new plan review precedes the new build start.
- [x] Validate `max_repair_cycles` in `repair_cap` in `scripts/lib/todo_graph.py` as the dispatcher does, refusing `repair` on a bad value, and reading such a row as `waiting-person` naming the setting, so a run passes over it rather than retrying, and other rows carry on. Done when: tests with 4, `true`, `"3"` and -1 refuse, disposition still answers for every row, and correcting the pin brings the row back to `needs-repair` with its findings intact.
- [x] Report a failed rollback in `rewrite_section` in `scripts/lib/todo_graph.py`, naming what the section holds and the command that finishes it. Done when: tests inject a failing file write, a failing plan write and a failing rollback, and a fresh read each time selects exactly one cycle and keeps earlier findings. Cheaper substitute: a test that fails before any write.
- [x] Prove exhaustion, parking, a grant, a repair, then a successful stamp and validate, in `scripts/tests/test_todo_graph.py`. Done when: the test ends with the section stamped and the whole tree valid.
- [x] Treat an unreadable pin, malformed TOML and a `schedule` of the wrong shape in `repair_cap` in `scripts/lib/todo_graph.py` as a bad value too, with no fallback to a default when the file exists. Done when: tests prove each refuses `repair` with no write and reads `waiting-person`, other rows still answer, a tree with no pin uses 3, and correcting the file restores `needs-repair`. Cheaper substitute: catching every error and assuming 3.
- [x] Judge exhausted rounds in `review_dead_end` in `scripts/lib/todo_graph.py` by the cycle's initiating committed policy, through the dispatcher's `record_policy`, as the dispatcher does. Done when: tests change the pin both ways after a cycle starts, and the disposition follows the initiating policy.
- [x] Report a findings file that can't be read in `review_dead_end` in `scripts/lib/todo_graph.py` as `waiting-person`, naming the file and the error, not as `ready`. Done when: tests with a lock and a permission failure refuse `repair` with no write, another row still answers, and the row recovers when the file reads again. Cheaper substitute: treating an unreadable file as no dead end.
- [x] Commit: `review: repair cycles and parking`

**Test checkpoint:** `python -m unittest scripts/tests/test_review_dispatch.py scripts/tests/test_todo_graph.py scripts/tests/test_run_guard.py` exits 0, and fails if an ungranted fourth repair succeeds, or repairable work ends a run.

> **Repair cycle:** 1: 2026-11-07: the completion review found three in-scope gaps (after: its completion review returned gaps)

> **Repair cycle:** 2: 2026-11-07: the completion review found the pin's unreadable and malformed cases (after: its completion review returned gaps)

> **Repair cycle:** 3: 2026-11-07: the completion review found a locked findings file reads as ready (after: its completion review returned gaps)

> **Verified:** 2026-11-07 | §3 | 14254833faa59c4bed892a1339b42e69ab0a739d
> **Review:** `docs/reviews/d00-t03-s3.json`

## 4. Reviewer attempt limits and diagnostics

> **Started:** 2026-11-07T23:24:52Z

Failed attempts don't count as reviews, and each one changes the tree, so a broken reviewer looks like progress and retries forever. Only Astra reviews Claude's work, and there is no fallback, so an outage must wait.

**Corrected 2026-11-08:** a failed attempt is written into the findings file today as `## Round N: none` (`record` in `scripts/review-dispatch.py`), which is what changes the tree; this section moves failed attempts to `build/review-attempts.json`, so the findings file holds returned reviews only. `build/` is gitignored, so `real_signature` already leaves it out; the item below proves that rather than changing it. The run has no waiting state yet, so this section adds one (`wait_until` and a reason) to `scripts/lib/run_guard.py`, and `§5` reuses it for usage limits instead of building its own.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Classify failures in `scripts/review-dispatch.py`. Done when: each failure has a class and a retry rule.
2. Keep attempts out of the progress signal. Done when: a failed attempt doesn't count as a change.

- [x] Classify a failed attempt as `auth`, `model`, `rate-limit`, `timeout`, `invalid-verdict` or `other`, with any retry-after time, in `scripts/review-dispatch.py`. Done when: `scripts/tests/test_review_dispatch.py` covers each from captured CLI output. Cheaper substitute: reducing every failure to an exit number.
- [x] Keep up to 4 KB of redacted stderr in `build/review-failures/` from `scripts/review-dispatch.py`. Done when: a planted key in stderr never reaches the file. Cheaper substitute: discarding stderr.
- [x] Cap attempts per pass at 5 with exponential backoff, recording each in `build/review-attempts.json` rather than the findings file, keyed by findings file, kind and position so the count carries across invocations and clears when the pass returns. Backoff is 30 seconds doubling, slept only while the next wait is 5 minutes or less; `auth` and `model` don't retry, since waiting can't fix them. Done when: a fake failing CLI stops at 5 across separate invocations, the findings file is unchanged, and the tree's signature is unchanged.
- [x] Keep `build/` state out of `signature` in `scripts/lib/run_guard.py`, which `.gitignore` already does. Done when: a test proves an attempt record does not reset `idle` or `restarts`.
- [x] Add a waiting state to `scripts/lib/run_guard.py` (`wait_until` and a reason, set by `python scripts/run-guard.py wait --reason <why> --minutes <n>`), during which `look` spawns nothing and counts no restart, `on_stop` lets the turn end without counting it idle, and `MAX_QUIET_HOURS` doesn't run. Done when: a fake clock across the wait ends in a resume, not `stalled` or `expired`.
- [x] Mark the run waiting with reason `reviewer-unavailable` from `scripts/review-dispatch.py` when a pass reaches the attempt cap, or at once for `auth` and `model`, for 30 minutes or until a longer `rate-limit` retry-after, exiting 3; and route exit 3 in `process-phase/SKILL.md` and its mirrors to end the turn. Done when: the guard rechecks every 30 minutes, and a recovered reviewer resumes the run. Cheaper substitute: switching to a different reviewer.
- [x] Open a new window of 5 attempts for a pass once its wait has passed, in `scripts/review-dispatch.py`, leaving its position in the schedule as it was. Done when: a test runs five failures, a fresh process, the wait passing and a returned review, which is recorded as that same position.
- [x] Respect a `rate-limit` retry-after on every retry, not only at the cap: the pass's `not_before` is kept in `build/review-attempts.json`, and no CLI runs before it, sleeping when it is 5 minutes away or less and waiting through the guard when longer. Done when: a test across two processes sees no CLI call before the deadline.
- [x] Give a run back a fresh no-progress budget when its wait ends, in `look` in `scripts/lib/run_guard.py`: `restarts` back to 0 and `changed_at` to the end of the wait, so neither the resume nor the time spent waiting counts toward `MAX_RESTARTS` or `MAX_QUIET_HOURS`. Done when: an outage across several waits and longer than `MAX_QUIET_HOURS` ends in a resume, not `stalled` or `expired`.
- [x] Save `build/review-attempts.json` atomically and reserve each attempt before its CLI starts, refusing with exit 2 when the file can't be read or saved, and exiting 3 only once the guard's wait is saved, saying so when there is no run to wait. Done when: tests cover a kill after the reservation, an unreadable file and a failed save, each read back in a fresh process.
- [x] Show the wait in `python scripts/run-guard.py status`: its reason, its deadline, the failure class, the diagnostic file in `build/review-failures/` and what to do (for `auth`, sign the reviewer CLI in; for `model`, fix the pin). Done when: a test reads each from `status` and `status --json`.
- [x] Teach `process-todo-section/SKILL.md`, `review-todo-section/SKILL.md` and `process-phase/SKILL.md`, in all three mirrors, that a failed attempt writes nothing to the findings file, and that exit 3 from any plan, round or completion pass means the run waits: end the turn without building, retrying or stamping. Done when: a test reads it in each mirror, and no skill still says a failed round is recorded in the findings file.
- [x] Commit: `review: attempt limits and failure classes`

**Test checkpoint:** `python -m unittest scripts/tests/test_review_dispatch.py scripts/tests/test_run_guard.py` exits 0, and fails if a sixth attempt runs.

> **Verified:** 2026-11-08 | §4 | a9d215fe1e70f3cf63da32aa4454200dfafeef4e
> **Review:** `docs/reviews/d00-t03-s4.json`

## 5. Usage limits wait and resume

> **Started:** 2026-11-08T00:37:48Z

The owner chose subscription only, so hitting a limit costs time and never money. Today three restarts with nothing changed call the run stalled, which can happen long before a limit resets.

**Corrected 2026-11-08:** Claude Code's `StopFailure` hook input (https://code.claude.com/docs/en/hooks) carries `error`, one of `rate_limit`, `overloaded`, `authentication_failed`, `oauth_org_not_allowed`, `account_on_hold`, `billing_error`, `invalid_request`, `model_not_found`, `server_error`, `max_output_tokens`, `cloud_credential_error` or `unknown`, with `error_details` and `last_assistant_message` as text. A usage limit arrives as `rate_limit`, and no reset-time field is documented, so the reset is read from that text when it gives one. `D00 T03 §4` already built the waiting state (`set_wait`, `waiting`, `end_wait`), during which `look` counts no restart and a fresh budget follows; this section uses it.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Add wait state to `scripts/lib/run_guard.py`. Done when: a usage limit records when to retry.
2. Keep paid keys away from the run. Done when: a resumed session can't see one.

- [x] Classify `StopFailure` data in `scripts/lib/run_guard.py` by its `error` field: `rate_limit` as `usage-limit`; `authentication_failed`, `oauth_org_not_allowed`, `account_on_hold`, `billing_error` and `cloud_credential_error` as `auth`; `overloaded` and `server_error` as `overloaded`; anything else as `other`. A usage limit waits until the reset its text gives (an epoch after `|`, `in N minutes` or `hours`, or `at H[:MM]am/pm`, the next such local time), or 30 minutes after the failure when none is given; `overloaded` waits 5 minutes; `other` only records. Done when: `scripts/tests/test_run_guard.py` covers each `error` value and each reset form. Cheaper substitute: only recording the failure text.
- [x] Leave `restarts` and `MAX_QUIET_HOURS` unchanged while `wait_until` is in the future. Done when: a fake clock across a 5 hour limit ends in a resume, not `stalled`. Already shipped: `D00 T03 §4` (fc5190c), `look` and `end_wait` in `scripts/lib/run_guard.py`; the test across a 5 hour usage limit is this section's.
- [x] Remove `ANTHROPIC_API_KEY`, `ANTHROPIC_AUTH_TOKEN`, `OPENAI_API_KEY`, `CLAUDE_CODE_USE_BEDROCK`, `CLAUDE_CODE_USE_VERTEX` and `CLAUDE_CODE_USE_FOUNDRY` from the environment passed by `resume_command`'s launch in `scripts/lib/run_guard.py`. Done when: a test with all of them set reads a clean child environment. Cheaper substitute: relying on them being unset.
- [x] Write one guard log line in `scripts/lib/run_guard.py` when one of those variables was removed, naming the variable and no value. Done when: a test sees the line, and the run carries on.
- [x] Before every launch, read Claude Code's effective sign-in with `claude auth status --json` under that same scrubbed environment (30 second timeout) in `scripts/lib/run_guard.py`, and launch only when it is signed in with `authMethod` `claude.ai` and `apiProvider` `firstParty`, since the CLI's own answer covers every credential source (an `apiKeyHelper`, a key in a settings `env` block, saved Console credentials, a cloud provider). Otherwise wait 30 minutes with a reason saying which (not signed in, an API account, a cloud provider, or the check itself failing) and what to do. Done when: tests prove each case waits without launching, and a subscription sign-in launches.
- [x] Treat `auth` as `waiting` with reason `sign-in-needed`, and write one line to the guard log, the detail telling an expired sign-in (sign in again) from an account, billing or organisation restriction (the owner's to sort out). When the wait passes, the sign-in check above runs before any launch, and a failing one waits another 30 minutes without counting a restart. Done when: a test proves repeated failures never spin or spend the stall budget, that it resumes once sign-in returns, and `status` and `status --json` show the reason and detail.
- [x] Prove the path end to end through the registered hook entry in `scripts/tests/test_run_guard.py`: a `StopFailure` payload through `hook` saves the wait, a fresh read finds its reason and deadline, `watch_tick` launches nothing before it and resumes after; another session's failure sets nothing, and an operator halt during the wait is never resumed. Done when: the test passes.
- [x] Commit: `guard: wait out usage limits without paid keys`

**Test checkpoint:** `python -m unittest discover -s scripts/tests -p test_run_guard.py` exits 0, and fails if `restarts` grows during a wait.

> **Verified:** 2026-11-08 | §5 | 83a2877e6d289b83d493fdde40050d37b521d1f9
> **Review:** `docs/reviews/d00-t03-s5.json`

## 6. Run end states and wake checks

> **Started:** 2026-11-08T03:11:41Z

"No runnable rows" is not one state. A run waiting for the .NET release is not finished, and a finished build must not end before its closeouts are written. The owner's finish line is implementation complete, then an acceptance pass.

**Corrected 2026-11-08:** the records this reads already exist. A file's Verification has run when `process-todo-file` has moved it under Completed in `todo/TODO-00-INDEX.md` (its step 5), and a phase is closed out when a `docs/phase-runs/*-phase-<N>.md` holds a `## Closeout` heading (`process-phase` step 4). A file that also holds phase 99 sections can't close before acceptance, so it is listed in the acceptance file rather than required for `complete`. The guard reads all of it from one new `python scripts/todo-graph.py query finish`, so the graph stays the one place that knows what is left, and `D00 T03 §4`'s waiting state carries a run that only waits.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Split `drained` in `scripts/lib/run_guard.py`. Done when: complete, waiting, parked and stopped are distinct.
2. Add `todo/wakes.json`. Done when: a temporary Need clears by itself when its check passes.

- [x] Define `complete` in `scripts/lib/run_guard.py`, through `query finish`, as every section outside phase 99 stamped, each of those files closed under Completed in `todo/TODO-00-INDEX.md` and each of those phases closed out in `docs/phase-runs/`, where a closeout or a Completed entry counts only when committed after the newest stamp it covers (so one written before a section was reopened and stamped again is stale). Done when: tests show a stamped section with no closeout, and a file reopened and stamped again after its old closeout, each not complete, and phase 99 rows not preventing it. Cheaper substitute: treating an empty ready list as done.
- [x] Refuse `complete` in `scripts/lib/run_guard.py` unless `scripts/ci_status.py --sha <pushed head> --use-proven` exits 0, with each other answer a defined next step: local work not yet on `origin/main` is a `needs-push` work item; a red run is a `needs-ci-repair` work item, which `process-phase`'s red-first policy fixes; a pending or missing run waits 10 minutes and looks again; CI that can't be asked waits 30 minutes. The acceptance file is written only once CI is proven. Done when: a test covers each answer and its recovery to green.
- [x] Add pending file verification and phase closeouts to `query disposition` in `scripts/lib/todo_graph.py`. Done when: a crash after the last stamp leaves work the watchdog resumes.
- [x] Classify a run with no runnable row by the root blockers of what is left outside phase 99 (the open sections blocking it that wait on nothing open themselves): any root that waits on a host, a person or CI keeps the run active through `D00 T03 §4`'s waiting state, reason `rows-waiting`, rechecked every 30 minutes, so the 72 hour quiet limit and the restart budget are suspended while it waits; only roots that are all parked end it. Done when: tests cover one waiting root with many dependants, a mix of parked and waiting roots, a wait longer than 72 hours, and the run resuming with no prompt when the root clears.
- [x] End a run whose remaining work outside phase 99 is only parked sections and their dependants as `parked`, never as complete, and write `docs/acceptance/<date>.md` in both `parked` and `complete` (atomically, read back before the run ends, never over an earlier one, which a second the same day keeps as `<date>-2.md`, and a write that fails leaving the run active to try again on the next tick), listing every parked section, each blocked dependant with its root blocker, every phase 99 row with how to run it, and every file that waits on phase 99 to close. Done when: fixtures for both states produce the file, a failed and an interrupted write each leave the run active and retry, a second one the same day keeps the first, and `parked` is never reported as implementation complete.
- [x] Create `todo/wakes.json`, mapping a section and Need to a check command, with `python scripts/todo-graph.py wake --check` (read-only, 60 second timeout a check, listing those that pass) and `wake --apply` (clearing each passing Need from its section, idempotent, so an interrupted apply is finished by the next). The watchdog's tick runs `--check` while a run waits and resumes the run when one passes; the resumed session runs `--apply` first and commits it through its normal steps. Done when: tests cover the tick-to-resume-to-apply path, a check that passes and one that fails, a missing executable, a timeout, a repeated apply and a commit that fails. Cheaper substitute: an operator editing the section.
- [x] Create `scripts/dotnet-final-available.py`, which exits 0 when `dotnet --list-sdks` lists a final 11.0.1xx SDK and 1 otherwise, naming what it found, and add the first wake in `todo/wakes.json` for `D00 T02 §3` `third-party` with it. Done when: a faked `dotnet --list-sdks` with a final 11.0.1xx SDK passes, with only the RC fails, and with no `dotnet` fails saying so.
- [x] Extend `.claude/skills/process-plan/SKILL.md` and `process-phase/SKILL.md` and their mirrors to dispatch work items from `query disposition`: section execution, file verification and phase closeout. Done when: a test simulating a crash after the final stamp resumes only the verification and closeout, then reads complete. Cheaper substitute: only listing the pending work.
- [x] Commit: `guard: end states and wake checks`

**Test checkpoint:** `python -m unittest discover -s scripts/tests -p test_run_guard.py` exits 0, and fails if an empty ready list reads complete.

> **Verified:** 2026-11-08 | §6 | c17292d7acefd927053b584bece842d73500c9f7
> **Review:** `docs/reviews/d00-t03-s6.json`

## 7. Owned session recovery

> **Started:** 2026-11-08T03:59:12Z

A hung resumed session is only reported today, and any Claude session in the checkout blocks recovery. Over days, sessions will end, hang and lose their transcripts.

**Phase:** 0
**Needs:** none
**Surface:** no

**Corrected 2026-11-08:** The items said `bosun status --json` shows whether a child holds or waits for a slot, and that `bosun heartbeat list` names the heartbeat's repos. Neither exists: `bosun status` prints slot counts only and can't name a holder (`src/app.cpp` `cmd_status`), its JSON with holders and waiters is `D01 T01`'s (reserved as `status_json` in `tests/test_core.cpp`), which runs in phase 1, after this; and the heartbeat's repos are in `%LOCALAPPDATA%\Bosun\heartbeat\repos.json`, which `on_heartbeat` in `scripts/lib/run_guard.py` already reads. A queued child is told instead by a `bosun run` process in its own process tree, which holds a slot or waits for one either way. Claude Code's session records (`~/.claude/sessions/<pid>.json`) carry a `status` (`busy` or `idle`), which is what "holding the tree" reads. The guard's files sit beside its state in `.git/run-guard/` (`STATE` in `scripts/lib/run_guard.py`, moved with `RUN_GUARD_STATE`), not in `build/`, so `last-tick.json` and `guard-health.json` are read there. An item was added for a headless resumed session that exits while its background review runs, seen in `D00 T01 §3`'s end-to-end runs.

**Build order:**

1. Track the run's own session in `scripts/lib/run_guard.py`. Done when: an unrelated session does not count as the run.
2. Recover it. Done when: a hung child and a missing session both resume.

- [x] Hold the run's session and child identity in `scripts/lib/run_guard.py`, and treat only those as live: `real_live_here` returns each session record's id and `status`, and `look` waits for the run's own session whatever its status, and for an unrelated one only while it isn't `idle` (an unknown status counts as busy). Done when: `scripts/tests/test_run_guard.py` proves an idle unrelated session does not block `look`, and that a busy one, a session of unknown status and the run's own idle session still do.
- [x] Stop an owned child and its process tree after `CHILD_HANG_HOURS` with no change (`taskkill /T /F` on Windows, each descendant on Linux), unless a `bosun run` process is in that tree. Before stopping it, check again that the pid still has the child's recorded start time (a reused pid is a stranger: it is never stopped, and the child counts as gone); after, confirm no process of the tree it listed is still alive. Only a confirmed stop resumes; a refused or partial one launches nothing, logs it, and ends the run `stalled` with the pids that survived. Done when: tests stop a hung child, log it and resume once; leave one with `bosun run` in its tree running; and launch nothing when the stop is refused, when a descendant survives, and when the pid was reused. Cheaper substitute: reporting the hang and leaving it running.
- [x] Start a new session with `claude -p "process plan"` when `--resume` fails because the transcript is gone (the child's output since its launch says `No conversation found with session ID`), through the same launch as a resume, so its permission mode and the subscription check hold. The fresh start is bounded by `fresh_starts` in the saved state: one per run, so a fresh start that fails the same way ends the run `stalled`. The new session's `process plan` prompt makes it the run's owner, as a takeover from a dead session does. Done when: a fake CLI returning that error leads to one fresh start, recorded in the guard log, with the mode it was given; across ticks whose state is saved and read back, the new session's hook makes it the owner and the next recovery resumes its id; and a second such failure isn't retried fresh.
- [x] When the run's child exits while its review is in flight, wait for the review, then resume the same session once. Done when: a test with a dead child and a live review mark waits, and with the mark gone resumes once with `--resume`.
- [x] Add `--verify` to `scripts/run-guard.py status`, checking the task's action (its program and script exist and it runs this repo's `run-guard.py look`, or the heartbeat's `heartbeat.py tick`), principal (an interactive token for this user), state (enabled) and a recent tick (beside the state), for the repo's own task or for `Bosun heartbeat` when its `repos.json` lists the repo. A tick is recent within two of its task's intervals: `last-tick.json` within 20 minutes for the repo's own task (every 10); for the heartbeat (every 5, from `scripts/install.ps1`), its `last_pass` in `status.json` within 10 minutes, and this repo's tick within one more pass per active repo, since a pass stops after the first resume and rotates, or a `hold` the heartbeat reports, which is healthy. Each check gives a reason; exit 0 healthy, 1 not, in text and `--json`. Done when: tests read unhealthy, each with its reason, for a wrong action path, a wrong subcommand or repository script, another user or a password logon, a disabled task, a missing, stale or failed tick, unreadable task data and a missing registration; and read healthy for a repo on the heartbeat with no task of its own, during a heartbeat hold, and with the repo's tick lagging behind a rotation. Cheaper substitute: checking that the task exists.
- [x] Record a failed hook in `guard-health.json` beside the state from the fail-open path in `scripts/run-guard.py` (atomic write, the last 10 failures kept, cleared by the next hook that runs clean), the write itself failing open, and let `status` read a corrupt state as `unreadable` with its error instead of raising, so health still shows. Done when: a corrupt state file shows up in `status` (text and `--json`), a hook against it still exits 0 with its message, an unwritable record still lets the hook exit 0, the failure reads back, and a clean hook clears it.
- [x] Commit: `guard: owned session recovery`

**Test checkpoint:** `python -m unittest discover -s scripts/tests -p test_run_guard.py` exits 0, and fails if an idle unrelated session blocks `look`, or a busy one or one of unknown status doesn't.

> **Verified:** 2026-11-08 | §7 | 890c5987a1065eab1489e6e7418d6c7bd1b5a9be
> **Review:** `docs/reviews/d00-t03-s7.json`

## 8. Campaign permissions

> **Started:** 2026-11-08T01:04:39Z

A hidden session can't answer a prompt, so any prompt stalls the run. The owner chose bypass mode with a deny list, and told us on 2026-11-07 not to let caution block the run. So the list covers only what can't be undone, and a refusal returns its reason so the session carries on another way.

**Corrected 2026-11-08:** the deny list is a `PreToolUse` hook of its own, `scripts/tool-guard.py`, because it runs on every tool call and must not take the run guard's lock; `scripts/tests/test_hook_commands.py` today expects every hook to be `run-guard.py`, so it learns to accept any project script. `claude --help` lists the modes `acceptEdits`, `auto`, `bypassPermissions`, `manual`, `dontAsk` and `plan`. The resume keeps the operator's own mode, which the owner chose to be `bypassPermissions`, so a run started in another mode is told that its resumed sessions would be refused at every prompt. The end-to-end test starts a real Claude session, which spends subscription usage, so it runs when `BOSUN_E2E=1` is set and once here as evidence.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Add the deny rules to `.claude/settings.json`. Done when: each is refused in a real session.
2. Fix `resume_command`. Done when: a resumed session runs in the approved mode.

- [x] Add deny rules in `.claude/settings.json` for force-push, `git push --delete`, `gh repo delete` and changing the repo's visibility, and for recursive deletes outside the repo, scratch, temp, and `build/`, through a `PreToolUse` hook. Done when: `scripts/tests/test_hook_commands.py` proves each is refused with a reason, while commits, pushes, branch work and cleaning scratch all pass. Cheaper substitute: an instruction asking the session not to.
- [x] Parse each Bash and PowerShell command into its parts in `scripts/tool-guard.py`: split on `;`, `&&`, `||`, `|` and newlines, and look inside `bash -c`, `sh -c`, `pwsh` or `powershell -Command` and `cmd /c`, so a protected command is found wherever it sits. A push is refused for `-f`, `--force`, `--force-with-lease`, `--force-if-includes`, `--mirror`, `--delete`, `-d`, or a refspec starting with `+` or `:`. Done when: tests refuse each form, alone and inside a chain or a wrapper, and pass a plain `git push`, `git push -u origin main` and `git push origin main:main`.
- [x] Decide a recursive delete (`rm -r`, `-R`, `-rf` and `--recursive`; `Remove-Item -Recurse` and its aliases; `rd /s`, `rmdir /s` and `del /s`) on each target resolved against the command's working folder, through links and junctions, with Windows paths compared without case: allowed only strictly inside the project folder (never the folder itself or its `.git`), the scratch folder or the temp folder; and a target that can't be resolved, such as an unknown variable, is refused. Done when: tests cover `..`, a sibling sharing an allowed prefix, several targets with one outside, an allowed folder's link pointing outside, the project folder itself, and cleaning `build/` and scratch passing.
- [x] Fail closed in `scripts/tool-guard.py`: unreadable hook input, or an error while deciding a command that could be protected, is refused with a diagnostic naming `python scripts/tool-guard.py --self-test`; and that self-test, which runs the hook on known commands and checks each decision, is added to `scripts/campaign-preflight.ps1`, so a hook that can't run is found before the campaign. Done when: tests cover malformed input, a forced internal error, and the self-test passing and failing.
- [x] Refuse mail and chat sends in the same hook: the send tools of Gmail, Outlook and Teams connectors. Done when: a test sees those tool names refused, and web lookups and documentation tools pass.
- [x] Validate a run's `permission_mode` against the modes `claude --help` lists, plus `default`, in one function in `scripts/lib/run_guard.py` that both `on_prompt` and `start` call before they change the state, refusing to start a run with an unknown one and naming it; a missing mode is `default`, which the resume leaves off its command line; and any mode but `bypassPermissions` is told its resumed sessions would meet prompts no one can answer. Done when: tests through both entry points cover an unknown mode, a missing one, `bypassPermissions` and another known mode, and a fresh process reads the saved mode back into the resume command. Cheaper substitute: forwarding any saved string.
- [x] Prove a write, a queued build and a commit in a disposable repo through the exact `resume_command`, in `scripts/tests/test_guard_e2e.py`, run when `BOSUN_E2E=1` is set since it spends subscription usage: seed a real session with the task, resume it with `resume_command` in `bypassPermissions` within 10 minutes, then assert the file's exact contents, the build's output written through `bosun run`, and a commit whose tree holds both, failing on any refusal or skipped step. Done when: the test passes on this machine with no prompt, and its output is quoted in the findings file.
- [x] Commit: `guard: campaign permissions`

**Test checkpoint:** `python -m unittest scripts/tests/test_tool_guard.py scripts/tests/test_hook_commands.py` exits 0, and fails if force-push is allowed; `BOSUN_E2E=1 python -m unittest scripts/tests/test_guard_e2e.py` proves the refusals in a real session. **Corrected 2026-11-08:** it named only `test_hook_commands.py`, which checks the hook is wired, not what it decides.

> **Verified:** 2026-11-08 | §8 | 336d4b11be1ee0e87350cb46d5c9b73630d7a8be
> **Review:** `docs/reviews/d00-t03-s8.json`

## 9. Named core tests and CMake resolution

> **Started:** 2026-11-08T00:19:56Z

Five test checkpoints pass a name to a test program that ignores it, so they can't fail for the reason they state, and the build command is not on PATH.

**Corrected 2026-11-08:** `scripts/campaign-preflight.ps1` doesn't exist yet; `§10` owns the preflight. This section creates the script with the CMake check as its first check, and `§10` adds its own. The names are reserved for `D01 T01` (`records`, `status_json`, `report_json`, `config`) and `D05 T02` (`pressure`), whose checkpoints already call them.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Add a name filter to `tests/test_core.cpp`. Done when: a name runs only its tests.
2. Resolve CMake. Done when: `cmake --version` works in a fresh session.

- [x] Group the cases in `tests/test_core.cpp` by name and accept one name as an argument. Done when: an unknown name exits nonzero, and with no argument every implemented group runs. Cheaper substitute: ignoring the argument.
- [x] Reserve the names `records`, `status_json`, `report_json`, `config` and `pressure` in `tests/test_core.cpp`, failing when selected and left out of the default run until implemented. Done when: selecting each exits nonzero, so a section can't pass without its tests, and an implemented group joins the default run.
- [x] Prove the names in `tests/core_names_test.ps1`: a valid name runs exactly its group, as `--list` and the printed group names show; `TEST_CORE_FORCE_FAIL=<group>` makes that group's run and the default run exit 1 while another group passes; every group `--list` calls implemented is in the default run and every reserved one isn't; and each of the five reserved names exits nonzero. Done when: the script passes, and fails if a name is ignored or a failure is swallowed.
- [x] Create `scripts/campaign-preflight.ps1` with a CMake check on the executable PATH resolves, requiring version 3.25 or later as `CMakeLists.txt` does, and finding Visual Studio's CMake through `vswhere` to name the folder to add. Done when: `tests/preflight_test.ps1` proves each case fails with its fix named (no CMake, `vswhere` missing, Visual Studio without CMake, an older or broken `cmake` earlier on PATH) and a good one passes. Cheaper substitute: a hardcoded Visual Studio path in a section.
- [x] Add `scripts/add-cmake-to-path.ps1`, which previews the exact folder it would add to the user PATH, applies it with `-Apply` keeping every existing entry and reading the result back, records what it added in `%LOCALAPPDATA%\Bosun\path-changes.json`, adds nothing twice, and with `-Undo` removes only the entry it added. Done when: `tests/preflight_test.ps1` runs preview, apply, a repeat and undo against a test variable and checks each read-back.
- [x] Add the Visual Studio CMake folder to the user PATH on this machine with that script. Done when: a new shell and the watchdog's environment both run `cmake --version`.
- [x] Commit: `tests: named core tests; preflight finds CMake`

**Test checkpoint:** `build\Release\test_core.exe no-such-test` and `build\Release\test_core.exe records` exit nonzero, `build\Release\test_core.exe` exits 0, and `pwsh tests/core_names_test.ps1` and `pwsh tests/preflight_test.ps1` exit 0.

> **Verified:** 2026-11-08 | §9 | 97c5582f955e09da70f492623890e546f15f594c
> **Review:** `docs/reviews/d00-t03-s9.json`

## 10. Preflight and machine hold

> **Started:** 2026-11-08T09:15:26Z

One command proves the run can finish before anyone walks away. It holds the owner's decisions: logged in and unlocked, no sleep or update restarts, and the final .NET SDK installed when it ships. Every other section waits for this one. It is attended on purpose: it runs once with the owner present, by naming it to `process-todo-section`, and the unattended campaign starts only after it is stamped.

**Phase:** 0
**Needs:** attended
**Surface:** no

**Corrected 2026-11-08:** `scripts/campaign-preflight.ps1` and its harness `tests/preflight_test.ps1` already exist from `D00 T03 §9` (CMake and the deny list), so the checks join them rather than a new `tests/campaign_preflight_test.ps1`. The SDK task said `dotnet-install.ps1 -Channel 11.0 -Quality GA`, but a dry run today resolves that to `11.0.100-rc.3.26522.148`, so it would install a release candidate: the task reads `latest-sdk` from the 11.0 `releases.json` and installs only a version with no prerelease suffix, by `-Version`. D: is a dynamic ReFS VHDX, `R:\Conclave.vhdx`, that reports 1 TB while R: has about 357 GB free, and nothing attaches it at boot, so the free space check covers R: too, admin setup adds a startup task that attaches it, and an owner reboot proves it returns. "Exits nonzero with `gh` signed out" is proven with a faked `gh` in the harness, never by signing the owner out. The hold covers both AC and DC power values. Defaults taken: 50 GB free on each drive (`-MinFreeGB`), and Windows Update keeps installing but never restarts while the owner is signed in (`NoAutoRebootWithLoggedOnUsers` with `AUOptions` 4, which that policy needs), so security updates still land; reversing either costs one `-Restore`.

**Corrected 2026-11-08** after the plan review: the SDK task check accepts a finished install, since the task removes itself; both scripts preview by default, save before changing, keep the first saved state on a repeat and keep it when a restore fails; the `dotnet` check names the SDK the campaign needs; the push check asks the remote rather than the tracking ref; every probe is bounded; tasks are checked by their live definition; and the disposable campaign names how its session starts, restarts and is cleaned up, and what it must have produced.

**Build order:**

1. Extend `scripts/campaign-preflight.ps1`. Done when: each check prints pass or fail with its fix.
2. Create `scripts/campaign-hold.ps1` and `scripts/campaign-admin-setup.ps1`. Done when: holds apply and restore.
3. Apply them here, have the owner reboot, and run the preflight and a disposable campaign. Done when: the preflight exits 0 after the reboot.

- [x] Check the tools in `scripts/campaign-preflight.ps1`: `bosun`, a .NET 11 SDK (11.0.1xx, a release candidate or final) in `dotnet --list-sdks`, `gh auth status`, `codex login status` and a real reply from `codex exec -m gpt-6-astra`, WSL `Ubuntu` and `/mnt/d` from inside it, each resolved from PATH and run under a time limit (`-ProbeSeconds`, default 120) that stops the probe's whole process tree when it passes, with `-Only <names>` to run a subset. Done when: `tests/preflight_test.ps1` proves each fails with a faked tool and prints the command that fixes it, a faked good one passes, a faked signed-out `gh` exits 1, `dotnet` with no SDKs or only older ones fails while the release candidate and a final both pass, and a faked probe that hangs fails at its limit with its fix and leaves no process behind. Cheaper substitute: a checklist in a document.
- [x] Check the repository in `scripts/campaign-preflight.ps1`, against `-Repo` (this checkout by default): a clean tree, `HEAD` equal to `main` on the remote as `git ls-remote origin refs/heads/main` reads it now (failing when the remote can't be read), `python scripts/ci_status.py --sha HEAD --use-proven` exiting 0, and `python scripts/run-guard.py status --verify` healthy. Done when: the harness proves a dirty tree, an unpushed commit, a remote that moved on while the tracking ref still matches `HEAD`, an unreachable remote, a faked red or unreachable CI and an unhealthy guard each fail with their fix.
- [x] Check the machine in `scripts/campaign-preflight.ps1`: at least `-MinFreeGB` free on D: and on R:, which backs D:'s dynamic VHDX; the hold applied (its record present and every held value live); admin setup applied, by each task's live definition (enabled, its action, the SYSTEM principal and its triggers) and the update values, where `Bosun dotnet final` may instead be gone once a final 11.0.1xx SDK is in its folder; and no machine inactivity lock (`InactivityTimeoutSecs`) a policy would force. Done when: the harness proves a free space shortfall, a missing hold record, a held value that drifted, a missing setup, a task disabled or with a changed action, principal or trigger, and an absent SDK task with no final SDK each fail with their fix, and an absent SDK task with a final SDK passes.
- [x] Check the owner's record in `scripts/campaign-preflight.ps1`: `docs/acceptance/preflight.md` must say extra usage is off, with a date, and that the reboot check passed, with a date. Done when: the harness proves a missing file, extra usage recorded on or unconfirmed, and no reboot entry each fail, naming what the owner confirms.
- [x] Create `scripts/campaign-hold.ps1`, previewing by default every power value and registry value it would change, with `-Apply` setting the AC and DC sleep, hibernate and display timeouts to never through `powercfg` and the screen saver lock (`ScreenSaverIsSecure`) off, after saving every prior value to `%LOCALAPPDATA%\Bosun\campaign-hold.json` by temporary file and replace, before the first change; a second `-Apply` keeps the first saved values; `-Restore` writes them back and removes the record only after every value reads back, keeping it and naming each value it couldn't restore otherwise; a corrupt record is refused, never overwritten. Done when: `tests/campaign_hold_test.ps1`, with a faked `powercfg` and a test registry key, proves the preview changes nothing, every value reads back after apply and byte for byte after restore, a repeat apply and an apply interrupted after its first change keep the original values, a corrupt record is refused with its path, and a failed restore keeps the record and a retry then succeeds. Cheaper substitute: asking the operator to remember.
- [x] Create `scripts/install-dotnet-final.ps1`, the SDK task's body: it reads `latest-sdk` from the 11.0 `releases.json`; with a prerelease suffix it changes nothing; with a final version it installs it by `dotnet-install.ps1 -Version <it> -InstallDir <folder>` side by side, and unregisters its task only once `<folder>\dotnet.exe --list-sdks` shows a final 11.0.1xx SDK, appending each run to `%ProgramData%\Bosun\logs\dotnet-final.log`. Done when: `tests/campaign_admin_test.ps1`, with faked metadata, a faked `dotnet` and a faked installer, proves a prerelease changes nothing and keeps the task, a final version installs into the given folder then removes the task, a failed install keeps it, and an SDK already present removes it with no install. Cheaper substitute: `-Quality GA`, which installs a release candidate today.
- [x] Create `scripts/campaign-admin-setup.ps1`, previewing by default every value, file and task definition it would change, and with `-Apply` refusing to run unelevated with the command that elevates it: it saves the prior state of the Windows Update values above (each value or its absence) under `HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU` to `%LOCALAPPDATA%\Bosun\admin-setup.json` by temporary file and replace before the first change, keeping the first saved state on a repeat, then sets them; copies `scripts/install-dotnet-final.ps1` to `%ProgramData%\Bosun\bin`; and registers two SYSTEM tasks, `Bosun dotnet final` (daily and at startup, given the folder of the `dotnet.exe` the user's PATH from the registry resolves, as the watchdog's task inherits it) and `Bosun attach D` (at startup, `Mount-DiskImage` on `R:\Conclave.vhdx` when it isn't attached). Done when: the harness, with a test policy key and test task names, proves the preview changes nothing, reads each value and task definition back after apply, keeps the original state through a repeat and an apply interrupted after its first change, and refuses a corrupt record. Cheaper substitute: the installer's default folder, which belongs to the SYSTEM account.
- [x] Add `-Restore` to `scripts/campaign-admin-setup.ps1`, putting each update value back as saved (removing one that was absent) and unregistering both tasks, removing the record only after everything reads back, and otherwise keeping it and naming what it couldn't restore; list it as a step in `docs/acceptance/preflight.md`. Done when: the harness reads the prior state back byte for byte after a restore, and a failed restore keeps the record and a retry then succeeds.
- [x] Apply the hold and run admin setup elevated on this machine. Done when: `powercfg`, the policy key and `Get-ScheduledTask` read the held values and both tasks back, and a manual run of `Bosun dotnet final` logs "prerelease, nothing changed".
- [x] Have the owner confirm that extra usage (paid overflow past the subscription's included usage) is off in the Claude account's settings, and record the answer and date in `docs/acceptance/preflight.md`, since the CLI can't report it and the owner chose subscription only. Filed from `D00 T03 §5`'s completion review. Done when: the record shows it off, or the preflight refuses to clear.
- [x] Have the owner reboot once, then check that D: is attached with its letter, the heartbeat ticks Bosun within 10 minutes of sign-in, and this session resumes, recording the result and date in `docs/acceptance/preflight.md`. Done when: the record shows it passed.
- [x] Check Claude in `scripts/campaign-preflight.ps1` as the watchdog's sessions sign in: the user's `CLAUDE_CODE_OAUTH_TOKEN` from the registry passed to the probe (never printed) and the guard's `SCRUBBED` variables removed; `claude auth status --json` signed in on `firstParty`; a `claude.ai` sign-in ending no sooner than `-MinLoginDays` (default 30) away, or a `setup-token` token (`oauth_token`); and a real reply, since `auth status` reads any token as signed in without trying it. Add `scripts/store-claude-token.ps1`, which keeps a pasted or piped token in the user variable without echo and refuses one with whitespace inside, with `-Remove` to undo. Found 2026-11-08: a probe session warned the sign-in ends between 2026-11-15 and 2026-11-18, which would stop a campaign of more than a week, and nothing checked it. Done when: `tests/preflight_test.ps1` proves a signed-out claude, a sign-in ending within 30 days (naming `claude setup-token`), one with no end date, a cloud provider and a mute claude each fail with their fix; an API key in the shell is stripped; a stored token passes without being printed; a token only the shell has doesn't count; and a token that doesn't reply fails.
- [x] Accept `oauth_token` as a subscription sign-in in `subscription_verdict` in `scripts/lib/run_guard.py`, since `claude setup-token` needs the subscription, while `api_key` is still refused. Done when: `test_only_a_subscription_sign_in_may_launch` passes `oauth_token` on `firstParty` and refuses it on another provider.
- [x] Have the owner run `claude setup-token` in their own terminal, keep the token with `pwsh scripts/store-claude-token.ps1` and a copy in the 1Password Conclave vault. Done when: `pwsh scripts/campaign-preflight.ps1 -Only claude` passes with a long-lived token. The owner stored it on 2026-11-08; the check passed with "claude signed in with a long-lived token (claude setup-token), and replied".
- [x] Run the preflight on this machine after the reboot. Done when: it exits 0 and its output is quoted here. Ran 2026-11-08 at `98111b2`, exit 0, with the Claude check and the stored token. An earlier run at `fa9b3fd` failed CI with the fix "push HEAD" on a commit already pushed, because no proven commit was recorded and `ci_status.py` named the real-run recovery only under `--gate`; `1799735` names it on a read-only check too.

  ```text
  ok   bosun (available: 6066 of 32601 MB / logger: running)
  ok   cmake 4.3.1 at C:\Program Files\Microsoft Visual Studio\18\Professional\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.EXE
  ok   deny list self-test (9 cases)
  ok   .NET 11 SDK 11.0.100-rc.2.26504.105 (a release candidate; the final installs when it ships)
  ok   gh signed in
  ok   claude signed in with a long-lived token (claude setup-token), and replied
  ok   codex signed in, and gpt-6-astra replied
  ok   WSL Ubuntu sees /mnt/d/GitHub/Bosun
  ok   clean tree
  ok   HEAD 98111b263252 is main on the remote
  ok   CI green on HEAD (build.yml: green at 98111b263252 (https://github.com/rizonetech/Bosun/actions/runs/42473188764) / plan.yml: green at 98111b263252 (https://github.com/rizonetech/Bosun/actions/runs/42473188916))
  ok   watchdog healthy (run-guard.py status --verify)
  ok   drive D:\ has 981 GB free
  ok   drive R:\ has 332 GB free
  ok   hold applied: no sleep, hibernate or display timeout, no screen saver lock
  ok   admin setup in place: update restarts held, tasks 'Bosun dotnet final' and 'Bosun attach D' as defined
  ok   no machine inactivity lock
  ok   owner record: extra usage off, reboot check passed
  preflight passed
  ```
- [x] Run a disposable campaign, `scripts/tests/test_campaign_e2e.py` behind `BOSUN_E2E=1`: it clones this repo into scratch with its run state in the clone (`RUN_GUARD_STATE`), keeps five trivial sections open, one for each gate action and one to carry on to (corrected 2026-11-08: four actions need four sections), and puts a faked `gh` that answers each commit from a script and a faked `codex` that returns clean reviews first on a PATH that every process it starts inherits; it launches `claude -p "process plan" --permission-mode bypassPermissions` in the clone, whose own prompt hook makes it the run's owner, and drives recovery by calling `run-guard.py look` itself, so the real heartbeat is never touched; it stops every process tree it started and removes the clone at the end, pass or fail. Done when: its log, commits and `query disposition` show `wait` starting no next row, `repair` committing a repair and rechecking, and `exhausted` and `unverified` filing the fault as a section and carrying on to the next row, each still true after the session restarts. Run 3, 2026-11-08 at `3680514`, its checks passed (the run had settled; it was stopped before its own loop noticed, fixed in `cb1c281`): `wait`: CI on §1's stamp `8f6b84f` read pending 72 times from 14:00:52 to 14:36:06, and `todo: start D00 T01 §2` came at 14:36:41, after it cleared; `repair`: `5c940f7` read red, `0d4255f` repaired it and read green; `exhausted`: §3 read red on `7ad2140` and its three repairs, then `todo: file the exhausted CI fault on main in D00 T01 §6` and `todo: start D00 T01 §4`; `unverified`: §4's `ebcee75` had no run from 14:44:16 to 15:13:37, then `todo: file the unverified CI fault on main in D00 T01 §7` and §5 started and stamped; `query disposition` ends `D00 T01 §6 waiting-person, D00 T01 §7 waiting-person`, all after the session was killed and resumed. Runs 1 and 2 found the defects this section then fixed: a receipt spelling the fake CI did not share (`98111b2`, `receipt_for`), the Stop hook starting the next row while a gate polled (`06946f9`, `ci_in_flight`), a nested campaign from an inherited `BOSUN_E2E` (`8fd9d97`), and work folders left by git's read-only files (`b0a4402`).
- [x] Prove recovery once: kill the session on the disposable repo mid-row and let `run-guard.py look` resume it. Done when: the guard log shows the resume and the resumed session commits the next step of that same row. Run 3: the test killed the session tree at 14:36:45, just after `71bb275 todo: start D00 T01 §2` (14:36:41); the guard log reads `2026-11-08T12:36:46Z watch: resumed, attempt 1`, and the resumed session's first commit was `e46d940 e2e: write two (D00 T01 §2)` at 14:37:04, the next step of that same row.
- [x] Commit: `campaign: preflight and machine hold`

**Test checkpoint:** `pwsh scripts/campaign-preflight.ps1` exits 0 on this machine; `pwsh tests/preflight_test.ps1`, `pwsh tests/campaign_hold_test.ps1` and `pwsh tests/campaign_admin_test.ps1` exit 0, and the first fails if a faked signed-out `gh` lets the preflight exit 0.

> **Verified:** 2026-11-08 | §10 | 8ecc761e847d15aecc71c64110373c3eb475cdba
> **Review:** `docs/reviews/d00-t03-s10.json`

## 11. One global heartbeat

> **Started:** 2026-11-07T18:49:50Z

The owner decided on 2026-11-07 that one heartbeat runs every repo from day one, including Bosun while it is being built, so other repos can be developed in parallel. Each repo keeps its run state, rules and hooks. One scheduled task runs the schedule, from an installed copy, so a campaign rebuilding Bosun can't disturb it. A task is used rather than a loop in the logger because it keeps firing if a process crashes. The panel's view of this comes later, in `D04 T03 §6`. **Corrected 2026-11-07** after the plan review: this was C++ in `src/app.cpp`, which has no JSON or crash-safe state, and it is now Python like the guard it drives, with the handoff, lock, durable state, WSL and config contracts added.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Create `scripts/heartbeat.py`. Done when: `tick` runs each registered repo's guard in turn, and `add`, `remove`, `list` and `status` work.
2. Install it from `scripts/install.ps1` with the `Bosun heartbeat` task. Done when: a real scheduled run updates the status.

- [x] Hold repos in `%LOCALAPPDATA%\Bosun\heartbeat\repos.json` with an atomic write, each with a path, a distro for WSL and a state of `adding`, `active` or `removing`. Done when: `scripts/tests/test_heartbeat.py` proves a missing file means no repos and a corrupt one ticks nothing and reports it. Cheaper substitute: a repo list in code.
- [x] Add `add <path> [--distro <name>]` in `scripts/heartbeat.py`, refusing a repo with no `scripts/run-guard.py`, naming what to copy, and refusing a duplicate. Done when: tests prove both refusals and that `list` reads the repo back.
- [x] Make the handoff crash safe in `scripts/heartbeat.py`: record `adding`, remove the repo's own task, then record `active`; record `removing`, restore its own task, then delete the entry. Done when: tests crash after each step and the next `tick` finishes the handoff, so a repo never ends with no watchdog. Cheaper substitute: running the two steps and hoping.
- [x] Treat an absent own task as already removed in `scripts/heartbeat.py`, checking with `schtasks /Query` first. Done when: tests prove a repo with no task adds cleanly, and a real permission error refuses and keeps the old state.
- [x] Remove a repo's own task again in each `tick` if it reappears, for example because an older kit's prompt told a session to install it. Done when: a test sees one removal with a note in the status, and `Steward` and `Resolute` need no change.
- [x] Update `real_task_installed` in `scripts/lib/run_guard.py` to count a repo listed in `repos.json` as installed, with the prompt hook naming `heartbeat.py add`. Done when: `scripts/tests/test_run_guard.py` proves a listed repo never recommends a second watchdog, and one with neither still warns.
- [x] Take a lock around `tick`, `add` and `remove` in `scripts/heartbeat.py` that the operating system releases on a crash. Done when: tests prove a second process's `tick` reports busy and changes nothing, and `add` waits, then refuses. Cheaper substitute: relying on Task Scheduler's instance policy.
- [x] Run `scripts/run-guard.py look` per Windows repo, with an argument list, the repo as working directory, no window and a 120 second limit. Done when: tests prove a fake guard ticks, a hung one is stopped, the next repo still ticks, and a spaced path survives. Cheaper substitute: a shell string.
- [x] Run a WSL repo through `wsl.exe -d <distro> --cd <path> -- python3 scripts/run-guard.py look`, only when `wsl.exe --list --running` shows the distro. Done when: tests prove a stopped distro stays stopped and reads "WSL not running". Cheaper substitute: a tick that wakes the VM.
- [x] Support WSL in `add` and `remove` with no task handoff, since a WSL repo's guard has no Windows task, refusing while the distro is stopped. Done when: `tests/heartbeat_test.ps1` adds, ticks and removes a temporary repo in the real distro with a fake guard.
- [x] Stop a pass after a guard prints exactly `resumed, attempt <N>`, not `resumed session still running`, saving where to carry on. Done when: tests in separate processes prove three dead repos resume across three passes, and a live child doesn't use the pass. Cheaper substitute: matching the word `resumed`.
- [x] Hold a pass while available memory is below `heartbeat_floor_mb` in `%LOCALAPPDATA%\Bosun\config.json`, default 2048, saving when the hold began, for at most 60 minutes, then tick anyway. Done when: tests across separate processes prove the hold, its reason in the status, the tick after the limit, a non-default value changing the result, and a malformed file using the default with a warning. `D01 T01 §5` later adds `config get` and `set` for this key.
- [x] Add `status` and `status --json` with `schema_version`: each repo's state, last tick, result and error, any hold, whether the last pass is stale, and the task's existence, enabled state and action. Done when: tests cover empty, never ticked, unreadable, stale, and a missing or disabled task. Cheaper substitute: printing a saved file.
- [x] Install `scripts/heartbeat.py` to `%LOCALAPPDATA%\Bosun\bin` with a `bosun-heartbeat.cmd` shim in `scripts/install.ps1`, and create the `Bosun heartbeat` task every 5 minutes through `pythonw.exe`, per user. Done when: `schtasks` shows that action, reinstalling keeps `repos.json`, and `schtasks /Run` updates the status with no window.
- [x] Move this machine to the heartbeat: add `D:\GitHub\Bosun`, `R:\GitHub\Steward` and `R:\GitHub\Resolute`, and delete the leftover `guard-e2e run watchdog` task. Done when: `bosun-heartbeat status` shows a tick for all three, with the task healthy, and `schtasks` shows no repo watchdog for them.
- [x] Commit: `heartbeat: one global heartbeat`

**Test checkpoint:** `python -m unittest discover -s scripts/tests -p test_heartbeat.py` and `pwsh tests/heartbeat_test.ps1` exit 0, and fail if a stopped distro is started, a live child uses the pass, or a crash mid-handoff leaves no watchdog.

> **Verified:** 2026-11-08 | §11 | f3f7bc32115585547efa56c7a3b26e8bbcacb628
> **Review:** `docs/reviews/d00-t03-s11.json`

## 12. Review caps of 33

> **Started:** 2026-11-07T20:51:34Z

The owner decided on 2026-11-07 that a low cap makes sections fail to ship and the run block itself, as `§1` showed by parking on two small gaps after three repairs. So a cycle may spend 33 review rounds, one adversarial and 32 low, and a section may use 33 repair cycles. A review still stops at its first clean round, so the higher caps cost only what a section really needs. This revises `§3`, which is stamped, so its text stands as history.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Change `.conclave/reviewers.toml`. Done when: the pin reads 32 low rounds and 33 repair cycles.
2. Change the limits in `scripts/review-dispatch.py` and `scripts/lib/todo_graph.py`. Done when: a value over 33 is refused.

- [x] Set `max_low_rounds = 32` and `max_repair_cycles = 33` in `.conclave/reviewers.toml`. Done when: `--check-pin --json` reads them back, and `schedule_cap` gives 33.
- [x] Allow `max_repair_cycles` from 0 to 33 in `check_schedule` in `scripts/review-dispatch.py`. Done when: `scripts/tests/test_review_dispatch.py` accepts 33 and refuses 34.
- [x] Use 33 as the default and the limit in `repair_cap` in `scripts/lib/todo_graph.py`. Done when: `scripts/tests/test_todo_graph.py` allows a 33rd repair with no pin, refuses the 34th naming `park`, and still honours a lower value in the pin.
- [x] Prove the whole new limit in `scripts/tests/test_review_policy.py`, through the dispatcher's `check_sequence` on the live pin: rounds 10 to 33 run, round 34 is refused, a clean round stops early, completion follows a clean round 33, and an unresolved round 33 reads `needs-repair`. Done when: the test fails if the pin falls back to 8.
- [x] Update the tests that assert the old live values, keeping fixtures that are about history as they are, in `scripts/tests/test_review_policy.py`, `test_review_dispatch.py` and `test_todo_graph.py`. Done when: all three suites pass.
- [x] Prove a cycle already under way keeps its own cap in `review_dead_end`. Done when: a test with rounds at an older pin's cap reads `needs-repair` after the pin rises.
- [x] Update `AGENTS.md`, `docs/specs/2026-11-07-control-panel-design.md` and `todo/README.md` to the new caps. Done when: a test reads 33 from the design doc's review cap row and from the pin, so they can't drift.
- [x] Prove on the live pin in `scripts/tests/test_todo_graph.py` that round 32 goes on and an unresolved round 33 reads `needs-repair`. Done when: the test passes, and fails if the pin's cap changes.
- [x] Replace "operator recovery required" in `scripts/review-dispatch.py` with the repair and park route. Done when: `scripts/tests/test_review_policy.py` reads the route in the message and no mention of an operator.
- [x] Commit: `review: caps of 33`

**Test checkpoint:** `python -m unittest scripts/tests/test_review_policy.py scripts/tests/test_review_dispatch.py scripts/tests/test_todo_graph.py` exits 0, and fails if a 34th repair is allowed, round 34 runs, or the pin falls back to 3 or 8.

> **Repair cycle:** 1: 2026-11-07: the completion review found round 33 unproven and a stale message (after: its completion review returned gaps)

> **Verified:** 2026-11-07 | §12 | b4b2087235e4375df2a86cfd966d6c58de4fb4d1
> **Review:** `docs/reviews/d00-t03-s12.json`

## 13. CI gate recovery that names its commit

> **Started:** 2026-11-12T02:25:27Z

Filed from `D00 T03 §1`'s seventh cycle, under the owner's rule that leftovers are filed from a section's third cycle on. The gate names `gh workflow run <file> --ref <branch>` when nothing proves a commit, which runs whatever the branch holds then, and it prints this only after polling.

**Corrected 2026-11-12 (plan review):** the plan review in `docs/reviews/00-workspace/D00-T03-s13.md` returned three gaps, and they change the design. Printing the command early has no reader: an unattended run ends its turn until the gate finishes, and this session's push of `6fcc122` waited over twenty minutes on `plan.yml`, which its path filter had skipped, before the command was run by hand. Checking the head before printing a branch command also binds nothing, since the branch can move before anyone runs it. So the gate does the recovery itself: at the first look, before any sleep, `--gate` starts each workflow that has no run and no proof, only when the branch's remote head is the commit being gated, once per commit and workflow. A run is still counted for a commit only by its own `headSha` (`gh run list --commit`), so one started after the branch moved can never turn the commit green. A read-only look (no `--gate`) starts nothing and keeps printing the command for `scripts/campaign-preflight.ps1`, which reads the `to get a real run:` line.

**Corrected 2026-11-12 (build):** three items were added while building. The first real look against this repository showed a commit that was simply not pushed yet reported as a branch that had moved on, so the two are told apart. `scripts/campaign-preflight.ps1` reads the gate's output and offered "push HEAD" whenever no command was printed, which is wrong for a moved branch or an unreadable head. And `tests/preflight_test.ps1` failed three older checks on this machine when started from Git Bash, with or without this change, because it picked the inner `git.exe`, which cannot reach a remote from the test's short `PATH`.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Read the branch's remote head in `scripts/ci_status.py`. Done when: a moved branch and an unreadable head are each explained, in text and JSON, and neither is offered or given a run.
2. Start the missing runs from `--gate` at the first look. Done when: no sleep comes before it, and a restart does not start them twice.
3. Tell the run what changed. Done when: the three `process-phase` skill mirrors describe what the gate now does and what to do when the branch has moved.

- [x] Add `remote_head(ask, branch)` to `scripts/ci_status.py`, reading `gh api repos/{owner}/{repo}/branches/<branch> --jq .commit.sha` through the same bounded caller as every other `gh` call, and raising `Unavailable` when the call fails, times out or returns anything but a 40 character SHA. Done when: `scripts/tests/test_ci_status.py` covers a SHA, a failed call and a malformed reply, and shows no lookup starts once the deadline has passed.
- [x] In a read-only look (no `--gate`), print `to get a real run: gh workflow run <file> --ref <branch>` and keep `recovery` in the JSON only when the remote head is the checked commit; when it differs, say the branch has moved on and name its head (`moved_to` in the JSON), and when it can't be read, say why (`recovery_error`), each with no command. Done when: tests cover a match, a moved branch and an unreadable head, and the exit code and every verdict are the same in all three. Cheaper substitute: printing the command with the SHA in a comment, which still runs whatever the branch holds.
- [x] With `--gate`, at the first look and before any sleep, start each workflow whose verdict is `none` with `gh workflow run <file> --ref <branch>` when the remote head is the gated commit, print `recovery: started <file> on <branch> at <sha>` to stderr, and put `started` (the workflows) in the JSON, keeping one JSON object on stdout. Done when: a test with a 30 minute wait sees the dispatch before the first sleep, then reads the run as pending and green on later looks. Cheaper substitute: printing the command and waiting for someone to run it.
- [x] Record each started workflow under the commit in `build/ci-gate.json` (temporary file, then replace, as the gate's other state), and never start one twice for the same commit. Done when: tests show a second invocation, and a second look in the same one, start nothing more, and that a state file that can't be written means nothing is started and the reason is given. Cheaper substitute: starting on every look, which queues a run a minute.
- [x] Prove a run on a newer commit can't answer for the gated one: when the branch advances between the head check and the dispatch, the started run carries the newer `headSha`, the gated commit's verdict stays `none`, and the next look reports the branch moved. Done when: a test with exactly that sequence never reaches `proceed` for the older commit.
- [x] With `--gate`, when the remote head differs or can't be read, start nothing, keep the action the verdicts give (`wait`), and report `moved_to` or `recovery_error` in text and JSON; a dispatch that fails is reported the same way and is not recorded as started. Done when: tests assert the action, the exit code and the message for a moved branch, an unreadable head and a refused dispatch.
- [x] Update the module docstring of `scripts/ci_status.py` and the Red-first CI part of `process-phase` in `.claude/skills/`, `.agents/skills/` and `.codex/skills/`: the gate starts a missing run itself, a `wait` no longer asks for a command to be run, and when it says the branch has moved on the run gates the commit it names with `--sha <that commit>`. Done when: the three mirrors say the same and `python scripts/run-tests.py test_todo_graph` (which checks the skills) passes.
- [x] Tell a commit that is not on the remote yet apart from a branch that moved, in `recover` in `scripts/ci_status.py`: when the remote head is an ancestor of the commit, report `not_pushed` and say to push, with no run started or offered. Done when: `scripts/tests/test_ci_status.py` covers an ancestor head and a head that is not one.
- [x] Give `scripts/campaign-preflight.ps1` the right fix when the gate prints no command: `git pull --ff-only` for a branch that moved on, `gh auth status` for a head that can't be read, and pushing only otherwise. Done when: `tests/preflight_test.ps1` covers both new cases and the two existing ones.
- [x] Use Git for Windows' own launcher (`Git/cmd/git.exe`) for the short `PATH` in `tests/preflight_test.ps1` when it is on `PATH`. Done when: `pwsh tests/preflight_test.ps1` prints `all passed` from a shell started by Git Bash, where three of its checks failed before.
- [x] Commit: `ci: the gate starts a missing run for its own commit`

**Test checkpoint:** `python -m unittest scripts/tests/test_ci_status.py` and `pwsh tests/preflight_test.ps1` exit 0, and fails if a moved branch is offered or given a run, if a run on another commit counts, or if a workflow is started twice for one commit.

> **Verified:** 2026-11-12 | §13 | c353f1e29a2c208a3bd3d35b9a0f3ffe60eb56d6
> **Review:** `docs/reviews/d00-t03-s13.json`

## 14. Stamp filed leftovers from the third cycle

> **Started:** 2026-11-07T21:57:17Z

The owner decided on 2026-11-07 that from a section's third review cycle on, once its regular rounds are clean, completion gaps are filed as open sections and the section stamps, so one section's polish can't stall the run. `review-todo-section` says so, and `stamp` still refuses `filed` on any current-policy cycle, which this section changes. In the first two cycles only `complete` stamps, because the gaps found early have been the serious ones.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Allow `filed` from the third cycle in `schedule_ok` in `scripts/lib/todo_graph.py`. Done when: the stamp accepts it there and refuses it before.

- [x] Accept a `filed` completion verdict in `schedule_ok` in `scripts/lib/todo_graph.py` when the findings file is a third or later cycle, `-cycle3.md` onwards, and keep refusing it in cycles one and two, naming the rule. Done when: `scripts/tests/test_review_policy.py` proves cycle 2 refuses, cycle 3 is accepted, and `complete` is accepted in any cycle.
- [x] Keep every other requirement of a filed stamp: a clean last round, and each `- Filed:` reference resolving. Done when: tests prove a filed stamp with an unresolved reference, or with no clean round, is still refused.
- [x] Require each filed target to be an open section when `stamp` runs, in `scripts/lib/todo_graph.py`, while validation later doesn't, so the receipt stays valid once that work ships. Done when: tests refuse a stamped target, and a receipt filing to a section later stamped still validates.
- [x] Add a `needs-filing` state to `section_disposition` for a third or later cycle whose completion returned gaps after a clean last round, offered by the guard and routed by `process-plan`, `process-phase` and their mirrors to file and stamp. Done when: tests prove the state after a restart, that no repair cycle is spent, and that the runners route it.
- [x] Correct `review-todo-section/SKILL.md` and its mirrors so only cycles one and two require a genuine `complete`. Done when: a test finds no unconditional rule left in any mirror.
- [x] Prove a real filed stamp in `scripts/tests/test_todo_graph.py` on the stamp fixtures, which use the older pin shape, so the cycle rule itself is proven through `schedule_ok` above: the receipt is written, and a fresh process validates the tree with the parent verified and its filed section open. Done when: the test passes and fails if `stamp` drops the filed list.
- [x] Count only Part 1's missing items as gaps in `docs/completion-review-prompt.md`, and have `review-todo-section/SKILL.md` and its mirrors record Part 2's excellence suggestions in `todo/findings.md`, as the owner decided on 2026-11-07 so the plan can finish. Done when: a test reads the prompt's rule and each mirror's.
- [x] Recover a stamp cut off between its receipt and section writes in `stamp` in `scripts/lib/todo_graph.py`: an unstamped section's existing receipt is kept beside its name as an orphan, and the stamp is written afresh. Done when: a test cuts power after the receipt write and a retry stamps, keeping the orphan.
- [x] Prove through `stamp` itself that filing to a stamped section is refused, and that the filing receipt still validates once its target ships, in `scripts/tests/test_todo_graph.py`. Done when: the test passes and fails if `require_open_targets` is dropped.
- [x] Prove a filed stamp through the real current-policy `stamp` in `scripts/tests/test_todo_graph.py`: a committed live pin, two real repair lines, dispatcher records of a clean third cycle with completion gaps, then `stamp`, the receipt's filed list and a fresh-process `validate` with the target still open; and the same path refusing in cycle two. Done when: both pass and the first fails if `schedule_ok` refuses filed.
- [x] Route third-cycle completion gaps to filing in the per-row step of `.claude/skills/process-phase/SKILL.md` and in `process-plan/SKILL.md`, and their mirrors, with no sentence left sending every gap to repair. Done when: a test rejects the old sentences in every mirror.
- [x] Correct `docs/todo-workflow.md`, which still says to stamp only on complete. Done when: a test finds the third-cycle rule there.
- [x] Restore the section before deleting the receipt in the rollback of `stamp` in `scripts/lib/todo_graph.py`, so a rollback the disk also refuses leaves a stamped section with its receipt rather than one without. Done when: a test that fails every write after the section's, plan regeneration and rollback included, finds the graph valid with the section stamped, and `plan --sync` completes it once the disk clears; and the test fails with the old order.
- [x] Commit: `review: stamp filed leftovers from the third cycle`

**Test checkpoint:** `python -m unittest scripts/tests/test_review_policy.py scripts/tests/test_todo_graph.py scripts/tests/test_run_guard.py` exits 0 in PowerShell or bash, and fails if cycle 2 may stamp `filed`.

> **Repair cycle:** 1: 2026-11-07: the completion review found a stamp cut off between writes, untested open targets and a stale doc line (after: its completion review returned gaps)

> **Repair cycle:** 2: 2026-11-07: the completion review found a rollback that deletes the receipt before restoring the section (after: its completion review returned gaps)

> **Verified:** 2026-11-08 | §14 | 16f0e65f857df7f3e90df7f4c555cd84afea9a03
> **Review:** `docs/reviews/d00-t03-s14.json`

## 15. One completion pass, as Steward and Recto run

> **Started:** 2026-11-07T22:22:16Z

On 2026-11-08 the owner chose the review shape Steward and Recto use, because repairing on completion gaps kept a section looping and an unattended run can't afford that. Their receipts show it: Recto stamped 45 of 45 completion reviews `filed`, Steward 23 `filed`, 6 `completed` and 2 `complete`, and neither repo has a single repair cycle. So the completion review is one pass in every cycle: its missing items are fixed in the same cycle and stamped `completed`, or filed as open sections and stamped `filed`, and nothing is re-reviewed. `repair` stays only for a cycle whose rounds ran out without a clean one, and `park` at its cap. This replaces `§14`'s third-cycle rule.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Accept `completed` and `filed` in any cycle in `schedule_ok` in `scripts/lib/todo_graph.py`. Done when: a first cycle stamps either.
2. Route gaps after a clean round to `needs-filing` in any cycle, in `filing_due`. Done when: a first cycle reads `needs-filing`.
3. Say so in the skills, the workflow doc and the design doc. Done when: none still sends gaps to repair.

- [x] Accept `complete`, `completed` and `filed` in any current-policy cycle in `schedule_ok` in `scripts/lib/todo_graph.py`, keeping every other check. Done when: `scripts/tests/test_review_policy.py` stamps `filed` and `completed` in a first cycle through the helper and through the real current-policy `stamp`.
- [x] Return true from `filing_due` in `scripts/lib/todo_graph.py` in any cycle whose completion returned gaps after a clean last round, with `section_disposition` giving a reason that says fix or file. Done when: a test finds a first-cycle row `needs-filing`, and a cycle whose rounds never came clean still `needs-repair`.
- [x] Replace the third-cycle rule in `review-todo-section/SKILL.md`, `process-todo-section/SKILL.md`, `process-plan/SKILL.md` and `process-phase/SKILL.md`, all three mirrors, and in `docs/todo-workflow.md` and the design doc's decisions table, with the one-pass rule: fix and stamp `completed`, or file and stamp `filed`, never re-review. Done when: a test finds the rule in each and none of the old sentences.
- [x] Stop treating completion gaps as a dead end in `review_dead_end` in `scripts/lib/todo_graph.py`, and refuse `repair` on them with the fix-or-file route. Done when: a test proves the refusal leaves the section text and findings unchanged, and a cycle whose rounds ran out still repairs.
- [x] Point the dispatcher's completion-already-returned refusal in `scripts/review-dispatch.py` and the repair paragraph in `todo/README.md` at fix or file. Done when: a test reads both and neither asks for a new repair cycle after completion.
- [x] Commit: `review: one completion pass, as Steward and Recto run`

**Test checkpoint:** `python -m unittest scripts/tests/test_review_policy.py scripts/tests/test_todo_graph.py scripts/tests/test_run_guard.py` exits 0, and fails if a first cycle can't stamp `filed`.

> **Verified:** 2026-11-08 | §15 | d739c38da88f0fb9848fbb49db8e94dcef756afc
> **Review:** `docs/reviews/d00-t03-s15.json`

## 16. Captured rate-limit output for the reviewer classifier

Filed from `D00 T03 §4`'s completion review. Its classifier is tested on Codex's real output for a CLI that isn't signed in and for a model the account lacks, but a rate limit can't be caused on purpose to capture, so that case uses representative wording. The first real limit lands, redacted, in `build/review-failures/`, and this section turns it into the fixture.

**Phase:** 0
**Needs:** third-party
**Surface:** no

**Build order:**

1. Copy the first real rate-limit failure into `scripts/tests/fixtures/review-failures/`. Done when: the fixture holds the CLI's own words, with no credential.

- [ ] Copy the first `rate-limit` file from `build/review-failures/` to `scripts/tests/fixtures/review-failures/codex-rate-limit.txt`, after checking it holds no credential. Done when: the file is the CLI's own output.
- [ ] Classify it in `test_each_class_from_cli_output` in `scripts/tests/test_review_dispatch.py`, with its retry-after if it gives one, adjusting `classify` in `scripts/review-dispatch.py` only if the real wording needs it. Done when: the test passes on the captured file and fails if `classify` calls it `other`.
- [ ] Commit: `review: classify a captured rate limit`

**Test checkpoint:** `python -m unittest scripts/tests/test_review_dispatch.py` exits 0, and fails if the captured rate limit isn't `rate-limit`.

## 17. Branch protection on main, read back

> **Started:** 2026-11-12T09:27:44Z

Filed from `D00 T01 §3`'s second cycle. The deny list and the dispatch hook catch the campaign session's own mistakes in the spellings a session writes, but text can always be spelled another way; what makes a force push or a deletion of `main` impossible is the remote's branch protection, which this section turns on once the owner agrees, since it changes the repository's settings on GitHub.

**Corrected 2026-11-12 (owner):** the owner agreed on 2026-11-12 to turn branch protection on, so the Need is cleared. Checked the same day: `main` reads `Branch not protected` and the organization's plan offers protection on a private repository.

**Phase:** 0
**Needs:** none
**Surface:** no

**Corrected 2026-11-12 (plan review):** the plan review in `docs/reviews/00-workspace/D00-T03-s17.md` returned four gaps, all planned in below. "No bypass" means admins are held to it too (`enforce_admins`), and each setting is read as the object GitHub returns. The check is one of the preflight's default checks, runs through its bounded `Probe`, and fails closed on any answer it cannot read. The undo is recorded, and proved in the test rather than on the live repository, so protection is never switched off to check it.

**Build order:**

1. Turn on branch protection for `main` on `rizonetech/Bosun`, refusing force pushes and deletion for everyone, admins included. Done when: a read-back shows all three.
2. Check it in the preflight. Done when: a default preflight fails, naming the fix, whenever protection is off, partial or unreadable.

- [x] Turn on protection for `main` with `gh api --method PUT repos/rizonetech/Bosun/branches/main/protection`, with `enforce_admins` true, `allow_force_pushes` and `allow_deletions` false, and no required checks, reviews or push restrictions (those are not this section's). Done when: `gh api repos/rizonetech/Bosun/branches/main/protection` reads back `enforce_admins.enabled` true and `allow_force_pushes.enabled` and `allow_deletions.enabled` both false, and a normal push to `main` still lands.
- [x] Add a `protection` check to `$Checks` in `scripts/campaign-preflight.ps1`, so a default run includes it, reading that endpoint through `Probe` (bounded by `-ProbeSeconds`). It passes only when the answer is JSON with `enforce_admins.enabled` exactly true and `allow_force_pushes.enabled` and `allow_deletions.enabled` exactly false; a 404 (`Branch not protected`) fails as off, and a failed call, a timeout, an answer that is not JSON and a missing field each fail with what was wrong, each naming the fix. Done when: `tests/preflight_test.ps1`, with a stand-in `gh` returning GitHub-shaped JSON, passes the full answer and fails each of: force pushes allowed, deletion allowed, admins not enforced, not protected, malformed, a field missing, and a call that fails.
- [x] Prove the check is bounded: a stand-in `gh` that hangs is stopped at `-ProbeSeconds` with its fix named and leaves no process behind, and a default preflight (no `-Only`) runs the check. Done when: `tests/preflight_test.ps1` asserts both.
- [x] Record the undo in the section and in the check's fix text: `gh api --method DELETE repos/rizonetech/Bosun/branches/main/protection` puts `main` back as it was, after which the read-back says `Branch not protected` and the preflight's `protection` check fails. Done when: the stand-in test shows that read-back failing the check; the live repository is not unprotected to prove it.
- [x] Commit: `preflight: branch protection on main`

**Test checkpoint:** `pwsh tests/preflight_test.ps1` exits 0, and fails if protection reads off, partial or unreadable; `gh api repos/rizonetech/Bosun/branches/main/protection` reads back admins enforced, force pushes and deletion refused.

> **Verified:** 2026-11-12 | §17 | fb7205c3e538acf30835ebe86821c305e78558c6
> **Review:** `docs/reviews/d00-t03-s17.json`

## 18. Campaign setup hardening from the preflight's completion review

> **Started:** 2026-11-12T22:29:57Z

Filed from `D00 T03 §10`'s completion review (`docs/reviews/00-workspace/D00-T03-s10.md`, 2026-11-08). The preflight, hold and admin setup work and are proven, so the campaign runs on them; these make their undo and their checks stricter in cases no run has met yet, and nothing waits on this section.

**Corrected 2026-11-13:** checked against the tree before the build. Admin setup now registers one task (`<TaskPrefix> dotnet final`, `Get-CampaignTaskDefs` in `scripts/lib/campaign.ps1`) and copies two bodies (`install-dotnet-final.ps1`, `lib\campaign.ps1`) to `BinDir`; its record holds `PolicyKey` and the prior update values, and `-Restore` writes back to the `-PolicyKey` argument, not the recorded key. The fourth item said to detect "a restart Windows Update has already scheduled", but Windows exposes no readable record of one: the UpdateOrchestrator reboot tasks are hidden from an unelevated shell (measured on this machine). What overrides `NoAutoRebootWithLoggedOnUsers` is a compliance deadline policy, which Windows documents and keeps under the same policy key, so the item now checks those, and names a pending restart (`RebootRequired`) in its pass line. Default taken: reversing it costs one probe in `Test-Setup`.

**Corrected 2026-11-13 (plan review):** the plan review in `docs/reviews/00-workspace/D00-T03-s18.md` returned six gaps, planned in below. A deadline restarts the machine once it and its grace period pass even with `ConfigureDeadlineNoAutoReboot` set, so any deadline fails. A restart the owner schedules can't be read, so the preflight refuses to start while one could be scheduled (a restart pending) and the scripts' restart-hold wording names both exceptions. `-Apply` keeps to the recorded targets as `-Restore` does. `Test-Setup` validates the record and its targets. A lost record is said to have lost the originals, never rebuilt as if it had them. And every external call runs in a job, so an interrupted preflight or hold leaves nothing running.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Make admin setup's undo use exactly what apply changed, and check its record before trusting it. Done when: the admin harness covers a custom target round trip and an incomplete record.
2. Make the preflight check what keeps admin setup runnable and the update hold honest. Done when: the preflight harness covers each new failure.
3. Bound the remaining external calls. Done when: a hung `powercfg` or `vswhere` fails at its limit.

- [x] Record every target admin setup changes (`PolicyKey`, `TaskPrefix` and so the task names, `BinDir`) in `%LOCALAPPDATA%\Bosun\admin-setup.json` and have `-Restore` in `scripts/campaign-admin-setup.ps1` use the recorded ones, refusing a `-PolicyKey`, `-TaskPrefix` or `-BinDir` given that disagrees, with both values named and nothing changed. A record written before this keeps working: a target it lacks is the argument, as today. Done when: `tests/campaign_admin_test.ps1` applies with custom targets, restores with none and reads every original back, and a restore given a different target is refused and changes nothing.
- [x] Have a repeated `-Apply` in `scripts/campaign-admin-setup.ps1` use the recorded targets too, refusing a `-PolicyKey`, `-TaskPrefix` or `-BinDir` that disagrees before any change. Done when: `tests/campaign_admin_test.ps1` applies to one set of targets, then refuses a second `-Apply` with each target changed, and the first targets' originals still restore.
- [x] When `-Apply` finds no record and the update values already held, record the originals as unknown (`Originals: unknown`) and say so, and have `-Restore` of such a record remove both values (Windows' default) and say the originals were not known. Done when: `tests/campaign_admin_test.ps1` deletes the record after an apply, applies again, and shows both messages and the values absent after restore.
- [x] Validate the hold and admin records' fields and types in `scripts/lib/campaign.ps1` (one `Assert-Record` beside `Read-Json`, called by `scripts/campaign-hold.ps1`, `scripts/campaign-admin-setup.ps1`, and the preflight's hold check and `Test-Setup`) before any change, refusing an incomplete one with its path and the field that is wrong. Done when: both harnesses prove a parseable but incomplete record is refused and changes nothing.
- [x] Check in `Test-Setup` in `scripts/campaign-preflight.ps1` that the task bodies in `BinDir` (`C:\ProgramData\Bosun\bin` by default) match this checkout by hash, and that the admin record is valid and names the targets this check reads (`PolicyKey`, `TaskPrefix`, `BinDir`). A changed body names `-Apply` as the fix; a missing record says the originals it held are lost, so `-Apply` would set the hold up again but `-Restore` could only return Windows' defaults. Done when: `tests/preflight_test.ps1` covers a missing body, a changed body, a missing record, an incomplete record and a record for other targets.
- [x] Fail `Test-Setup` in `scripts/campaign-preflight.ps1` when a compliance deadline policy under `PolicyKey`'s parent (`SetComplianceDeadline`, `ConfigureDeadlineForQualityUpdates`, `ConfigureDeadlineForFeatureUpdates`) is set, whatever `ConfigureDeadlineNoAutoReboot` says, since a passed deadline restarts the machine whatever `NoAutoRebootWithLoggedOnUsers` says, naming the value and how to clear it. Done when: the harness, with the policy key pointed at a test key, covers each deadline set, a deadline with `ConfigureDeadlineNoAutoReboot` = 1 (still refused), and none.
- [x] Fail `Test-Setup` while a restart is pending (the `RebootRequired` key under `...\WindowsUpdate\Auto Update`, a `-PendingRestartKey` parameter), since the owner can schedule a pending restart and no readable record of one exists, with the fix "restart now, then run the preflight again"; the pass line says no deadline is set and no restart is pending. Done when: the harness covers a pending restart refused and none passing, with that wording.
- [x] Correct the restart-hold wording in the headers of `scripts/campaign-admin-setup.ps1` and `scripts/campaign-preflight.ps1` and in `AGENTS.md` where it says restarts never happen: they are held while the owner is signed in, except at a compliance deadline or a restart the owner schedules, which the preflight refuses to start beside. Done when: no file says the hold is unconditional (`rg -n "never restarts"` finds only the corrected lines).
- [x] Run `powercfg` (its reads and writes, in `Get-ActiveScheme`, `Get-PowerValue` and `Set-PowerValue`) and `vswhere` through `Invoke-Probe` in `scripts/lib/campaign.ps1` and `scripts/campaign-preflight.ps1`, so every external call has a limit and stops its tree; `scripts/campaign-hold.ps1` passes its `-ProbeSeconds`. Done when: the harnesses prove a hung fake `vswhere`, a hung `powercfg` read (preflight) and a hung `powercfg` write (hold) each fail at `-ProbeSeconds` and leave no process.
- [x] Run each probe of `Invoke-Probe` in a job object (`scripts/lib/job-measure.ps1`) that ends its whole tree when the job closes, so a preflight or hold that is interrupted or killed leaves nothing running. Done when: `tests/preflight_test.ps1` kills a preflight while a fake probe hangs and finds the probe and its child gone.
- [x] Commit: `campaign: hardening from the preflight's completion review`

**Test checkpoint:** `pwsh tests/preflight_test.ps1`, `pwsh tests/campaign_hold_test.ps1` and `pwsh tests/campaign_admin_test.ps1` exit 0.

> **Verified:** 2026-11-13 | §18 | 18948a5577c7b9e05f40c41ec06958b071bf00a9
> **Review:** `docs/reviews/d00-t03-s18.json`

## 19. Run guard holds an Antigravity run

> **Started:** 2026-11-13T00:19:06Z

Filed 2026-11-12 on the owner's request for a desktop shortcut that runs the plan unattended in the Antigravity CLI. `pwsh scripts/start-run.ps1 -Agent antigravity` and its shortcut "Bosun unattended run (Antigravity)" exist (commit after `9c11bcf`): `agy --dangerously-skip-permissions --effort medium --prompt-interactive <goal>` (medium on the owner's word, 2026-11-12), refused while the guard holds a Claude Code run. Measured on this machine the same day: `--mode auto` is not offered to this account, and the default model `gemini-4-carbon` offers efforts low, medium and high. Nothing holds an Antigravity session open between turns or resumes it after a crash: `scripts/run-guard.py` hooks only Claude Code's Stop and session events and its watchdog resumes only `claude`. This section builds the guard here. **Corrected 2026-11-12:** this said the guard Conclave-SDK plans for its kit could be taken with `forge.py update`; the owner decided the same day that Bosun imports nothing from the SDK.

**Corrected 2026-11-13:** checked against the tree and the installed CLI before the build. The owner's rule of 2026-11-12 removed the watchdog and every resume, so nothing here continues a stalled session or uses `agy --continue` or `--conversation`: the guard holds a live Antigravity session inside its window, as it does Claude Code's, and nothing more. What `agy` 1.5.7 offers, with sources:

- An end-of-turn hook: a `Stop` hook in `hooks.json`, read from the user's `~/.gemini/hooks.json` and the workspace's `<workspace>/.agents/hooks.json` (`agy changelog`: "workspace-local hooks defined in `<workspace>/.agents/hooks.json`"). Its JSON input carries `hookEventName`, `cwd`, `conversationId`, `terminationReason` and `continuation_active` (`agy changelog`), and a reply of `{"decision": "continue", "reason": ...}` sends the session on, as the owner's working Isotone adapter does (`~/.gemini/hooks/isotone_stop_hook.py`, which continues only when `terminationReason` is `NO_TOOL_CALL`). Antigravity ends a turn itself after a configurable number of consecutive continuations (`agy changelog`), so a hook can't hold one forever.
- Session events: a startup `SessionStart` hook (`agy changelog`); no session-end event is documented, and `agy` keeps no session record a guard could read, so whether the run lives is told by its launcher's window process (`scripts/start-run.ps1`, whose job ends the run with the window).
- Resuming: `--continue` and `--conversation <id>` exist (`agy --help`) and are not used, by the owner's rule.

`agy` has no in-session heartbeat (no `CronCreate`), so nothing wakes a session whose turn ended while it waited: the Antigravity run keeps its in-prompt "wait in this session" line, and its Stop hook, during a review or a CI gate in flight, sends the session on with that instruction instead of letting the turn end. A real Antigravity session can't be proved on this tree while a Claude Code run holds it (two writers), so the one real run is the owner's acceptance step `D99 T01 §6`.

**Corrected 2026-11-13 (plan review):** the plan review in `docs/reviews/00-workspace/D00-T03-s19.md` returned four gaps, planned in below: a timed wait (a usage limit, an unavailable reviewer, rows waiting) is answered with its end, not let through to a turn nothing would wake; the guard's stall rule and Antigravity's own continuation ceiling each end in a recorded state, the ceiling raised at launch so the guard's rule decides; the run is recorded `ended` when `agy` returns or fails, since the launcher's `-NoExit` window outlives it; and an operator's stop reaches the guard through `lastUserInput`.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Find what Antigravity offers to hold a turn open. Done when: the hook that blocks an end of turn is named with its source, or its absence is recorded.
2. Hold the run while its window lives. Done when: the checkpoint passes.

- [x] Record in this section what the installed `agy` (its `--version`) offers for an end-of-turn hook, session events and resuming a conversation (`--conversation`, `--continue`), with the command or page each fact came from. Done when: each of the three is named with its source or recorded as absent. (Recorded in the correction above, 2026-11-13.)
- [x] Teach `scripts/run-guard.py start` a `--runner antigravity` value with `--owner-pid <pid>`, kept in the run state with the owner process's start time, so `status` and the halts name the CLI that owns the run, and an Antigravity run counts as alive while that process (the launcher's window) runs with that start time; a Claude Code run is unchanged. Done when: `scripts/tests/test_run_guard.py` starts an Antigravity-owned run, reads its runner back from `status`, records it `ended` once its owner process is gone (and not before, nor for a reused pid), and refuses a Claude Code start while it lives and an Antigravity start while a Claude Code run lives.
- [x] Add `python scripts/run-guard.py end --session <id> --reason <why>`, which records the run of that session `ended` with the reason and leaves a run already in an end state (complete, parked, stalled, halted, ended) or owned by another session as it is. Done when: a test ends a live run, then shows a second `end`, an `end` after a halt and an `end` for another session each change nothing.
- [x] Answer Antigravity's `Stop` event through `python scripts/run-guard.py hook --runner antigravity`: read its JSON (`conversationId`, `cwd`, `terminationReason`, `continuation_active`, `lastUserInput`), take the run's session id from `BOSUN_RUN_SESSION` in the hook's environment, act only on `terminationReason` `NO_TOOL_CALL`, and print `{"decision": "continue", "reason": ...}` where the guard would send a Claude Code turn back; while a review or a CI gate is in flight, with "wait for it in this session"; while a timed wait is pending (`wait_until`: a usage limit, an unavailable reviewer, rows waiting), with the wait's reason and its end ("wait in this session until HH:MM UTC, in sleeps of at most ten minutes, then continue"), since nothing would wake the session. Print nothing at the run's end states and for another session. Done when: the guard harness feeds Antigravity-shaped events through the CLI and shows each of those answers, a timed wait answered with its end and, once it has passed, the ordinary continuation, and malformed input answering nothing and recorded as a hook failure.
- [x] Carry the operator's stop across: when `lastUserInput` asks to stop or pause (the guard's own `MENTIONS_STOP`, as for Claude Code's `UserPromptSubmit`), record the halt (`operator`, with the operator's words) and print nothing, so the session is not sent back; the goal prompt also names `python scripts/run-guard.py halt --ground operator --reason <their words>`. Done when: a test's `Stop` with `lastUserInput` "stop" leaves the run halted and prints nothing, and an ordinary `lastUserInput` still continues.
- [x] Bound the continuations: a run that changes nothing across the guard's idle and yield limits is recorded `stalled` with its recovery ("close the window and start the run again") and its Stop answers nothing after that; and `scripts/start-run.ps1` raises Antigravity's own ceiling for the session with `--settings '{"maxStopHookContinuations": 500}'`, so the guard's rule, not that ceiling, ends a run that stopped progressing (the key is `max_stop_hook_continuations` in the installed `agy`, JSON `maxStopHookContinuations`; `D99 T01 §6` confirms `agy` honours it). Done when: a test feeds repeated Stops with no change until the run is recorded `stalled`, and the next Stop prints nothing, while Stops with progress keep continuing.
- [x] Wire it in `.agents/hooks.json` (a workspace `Stop` hook running `python scripts/run-guard.py hook --runner antigravity`, with a timeout), and say in `AGENTS.md` where the guard's hooks are wired for each runner. Done when: a test reads `.agents/hooks.json` and finds the one `Stop` hook with that command.
- [x] Have `scripts/start-run.ps1 -Agent antigravity` claim the guard with `run-guard.py start --session <id> --runner antigravity --owner-pid $PID` after its window joins its job and before starting `agy`, with `BOSUN_RUN_SESSION` set to that id for `agy`, keep the in-prompt "wait in this session" line and name the halt command in it, and run `run-guard.py end --session <id>` once `agy` returns or fails to start, since the `-NoExit` window outlives it; a live Claude Code run still refuses the start. Done when: `tests/start_run_test.ps1`, with a fake `agy` and a state file of its own, shows the claim, the variable and the settings reaching `agy`, the run recorded `ended` after a fake `agy` that exits 0 and one that exits 1 with the launcher still alive, a new start then succeeding, and the run recorded `ended` after the launcher's window process is killed.
- [x] Say in `.claude/skills/process-plan/SKILL.md` and `.claude/skills/process-phase/SKILL.md`, and their two mirrors, that an Antigravity run has no heartbeat, so where they say to end the turn and let the heartbeat take the run up, it waits in the session instead, as its Stop hook tells it. Done when: the mirror test passes with the new text in all three copies.
- [x] Commit: `run: the guard holds an Antigravity run in its window`

**Test checkpoint:** `python scripts/run-tests.py test_run_guard` and `pwsh tests/start_run_test.ps1` exit 0; removing the Antigravity branch from the hook makes the guard harness fail.

> **Verified:** 2026-11-13 | §19 | 497a32e37cc09eb873960ce9bbf0c4367504894a
> **Review:** `docs/reviews/d00-t03-s19.json`
## 20. Runner badge and campaign names on the run shortcuts

> **Started:** 2026-11-13T01:29:34Z

Filed 2026-11-12 on the owner's request: the two desktop shortcuts carried the same Bosun icon (`resources/bosun.ico`), so they could not be told apart at a glance. Reshaped the same day on the owner's instruction to match the launchers of the owner's other campaigns (Office, Isotone): the shortcuts are named "Bosun Claude campaign" and "Bosun Carbon campaign", and each carries the Bosun icon with a round badge in the bottom-right corner, in its runner's colour with a white outline (`#D97757` for Claude Code, `#4C8DF6` for Antigravity on Gemini 4 Carbon). A runner's mark is its company's trademark, so none is committed here: when `marks\claude.svg` or `marks\gemini.svg` exists in the icon folder on this PC it is drawn in white inside the badge from 32 px up, and without it the badge carries its colour alone. The frames come from the masters in `design/icon/`, as `AGENTS.md` requires of every app icon. Decision recorded: the earlier design (a badge cut from the agent program's own icon, a lettered fallback, a PowerShell compositor) is withdrawn in favour of the owner's own pattern; reversing it costs one rewrite of `badged` in `scripts/build-icons.py`. The Carbon shortcut now starts Antigravity on `gemini-4-carbon-medium` (the `-AgyModel` default), so its name is true; reversing that is one default.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Render the badge in the icon builder. Done when: `python scripts/run-tests.py test_build_icons` exits 0.
2. Use it from `scripts/start-run.ps1 -InstallShortcut`. Done when: both shortcuts on this machine show their badge.

- [x] Add `--badge-fill <#RRGGBB>` and `--badge-mark <svg>` to `scripts/build-icons.py`: every frame of `FRAMES` gets the badge (radius 24% of the frame, white outline, the mark in white from 32 px up), written atomically to `--out`, which must be outside the repository. Done when: `scripts/tests/test_build_icons.py` shows a badged icon keeps the eight frame sizes, leaves the top-left quarter of the 256 px frame equal to the plain icon and changes the bottom-right quarter, differs with and without a mark at 256 px and not at 16 px, and refuses an `--out` inside the repository. Cheaper substitute: one 32 px image, which Windows blurs on a large-icon desktop.
- [x] Write both shortcuts from one `pwsh scripts/start-run.ps1 -InstallShortcut` ("Bosun Claude campaign" and "Bosun Carbon campaign"), each pointing its `IconLocation` at its own icon (`bosun-campaign-claude.ico`, `bosun-campaign-carbon.ico`) in the `Bosun` folder under the user's local application data, and remove the shortcuts' earlier names. Done when: `tests/start_run_test.ps1` shows each shortcut naming its own icon file, with `-IconDir` pointing the test at a temporary folder, the two icons differing, and the earlier names gone. Cheaper substitute: writing the icon into the repo, which makes a generated file look like a source.
- [x] Fall back to `resources/bosun.ico` in `scripts/start-run.ps1` when the badged icon cannot be made, with a one-line notice, so the shortcut is still written. Done when: the test shows an icon folder that cannot be made gives shortcuts with the plain Bosun icon and exit 0.
- [x] Rewrite both shortcuts on this machine with `pwsh scripts/start-run.ps1 -InstallShortcut`. Done when: each shortcut's `IconLocation` read back names its badged icon and a rendered copy of each icon is looked at once at 32 and 256 pixels.
- [x] Document the shortcuts in `AGENTS.md` and the header of `scripts/start-run.ps1`. Done when: both say each shortcut carries its runner's badge, that its icon is rendered into `%LOCALAPPDATA%\Bosun` (never the repository), that a mark is drawn when `marks\claude.svg` or `marks\gemini.svg` is in that folder, and that running `-InstallShortcut` again applies a mark supplied later. (**Corrected 2026-11-13 (plan review):** ticked in `ee6a9ea`, but `AGENTS.md` named neither the icon folder nor the marks.)
- [x] Prove the marks through the installer in `tests/start_run_test.ps1`: with distinct mark files in the test's `-IconDir\marks`, each installed icon is byte for byte what `python scripts/build-icons.py --badge-fill <its colour> --badge-mark <its mark>` writes (the build is reproducible), and differs from the same badge with the other runner's mark. Done when: the test fails when `--badge-mark` is dropped or the two marks are swapped.
- [x] Prove the atomic write in `scripts/tests/test_build_icons.py`: when the replace of an existing `--out` fails (the file held open by another process, as Windows refuses), the old icon's bytes survive and no temporary file is left beside it. Done when: the test fails against a direct write to `--out`.
- [x] Read both shortcuts back in the fallback case of `tests/start_run_test.ps1`. Done when: both links exist and name `resources\bosun.ico` after the badge can't be rendered.
- [x] Commit: `run: the campaign shortcuts' marks, atomic write and fallback are proven` (the build itself shipped in `ee6a9ea`, "run: campaign shortcuts carry their runner's badge, and nothing resumes a run")

**Test checkpoint:** `python scripts/run-tests.py test_build_icons` and `pwsh tests/start_run_test.ps1` exit 0, and fail if the badge is not drawn, both shortcuts name the same icon, a mark is dropped or swapped, or an icon is written in place.

> **Verified:** 2026-11-13 | §20 | fb9b97c5bb97f795642c221f04366d65ec5866b1
> **Review:** `docs/reviews/d00-t03-s20.json`

## 21. Run guard waits on a long local gate

> **Started:** 2026-11-13T02:29:56Z

Filed 2026-11-12 from `D00 T02 §4`. The guard lets a turn end while a review (`review_in_flight`) or the CI gate (`ci_in_flight`) runs, but not while `scripts/check-all.ps1` (about 15 minutes) or `scripts/measure-build.ps1` runs in the background: the Stop hook sends the turn straight back, three times in that section, and the session can only wait in a foreground loop, which holds the tool for up to ten minutes and is cut off past it (`docs/phase-runs/2026-11-12-phase-0.md`, Lessons). The fix follows the review marks: each long gate leaves a mark while it runs, and a live mark is a wait.

**Corrected 2026-11-13:** checked against the tree before the build. Since this was filed, the owner's test cadence (`process-todo-section`, "Test cadence") made CI the full gate of record, so a local `check-all` runs only for what CI can't run, to reproduce a red run, or when the CI gate answers `unverified`; a real-machine measurement (`scripts/measure-build.ps1`) still runs here for many minutes, so the wait is still worth having. And `D00 T03 §19` added the Antigravity runner, whose Stop hook sends a session on to wait inside it where a Claude Code turn may end (`on_antigravity_stop` in `scripts/lib/run_guard.py`): a gate in flight is one more such wait there. Nothing of this section exists in the tree yet (`gate_in_flight`, `gating` and `build/gate-in-flight/` are absent).

**Phase:** 0
**Needs:** none
**Surface:** no

**Corrected 2026-11-13 (plan review):** the plan review in `docs/reviews/00-workspace/D00-T03-s21.md` returned four gaps, planned in below. There is no overall gate limit to use as a deadline (`check-all` limits each step and each queue wait apart, and `-QueueMinutes 0` waits without limit), so a mark holds while its gate process runs, judged by its pid and start time, and a killed gate holds nothing. `status` says `waiting` while a review, a CI gate or a local gate is in flight, so the heartbeat never takes the next row under one (this was true of reviews and CI gates too). The production environment is wired and proved through the CLI, and both producers' lifecycles are proved.

**Build order:**

1. Mark a running gate. Done when: `scripts/check-all.ps1` and `scripts/measure-build.ps1` leave a mark under `build/gate-in-flight/` while they run and remove it however they end.
2. Wait on it. Done when: the checkpoint passes.

- [x] Add `gate_in_flight(root)` to `scripts/lib/run_guard.py`, reading `build/gate-in-flight/*.json` (`pid`, `started`, `what`): a mark counts while the process with that pid runs and started at that time (`process_started`), however long that is, and a dead pid, a reused pid, an unreadable file or a mark with no pid counts as nothing. Done when: `scripts/tests/test_run_guard.py` shows a live mark counted (one older than any step's limit too), and a dead pid, a reused pid and an unreadable file each not counted.
- [x] Add `gating` to `Env`, wire it in `REAL` to `gate_in_flight`, and treat it in `on_stop` as `reviewing` and `ci_waiting` are: the turn may end, and it is not idleness; and in `on_antigravity_stop` as they are there, sending the session on to wait for it in the session. Done when: tests show a Claude Code Stop with a live gate mark let through and the same Stop without one sent back, an Antigravity Stop with a live mark answered with the wait for the gate, repeated Stops under a live mark leaving the idle and yield counts unchanged, and the ordinary continuation once the mark is gone.
- [x] Prove the production wiring through the CLI: `python scripts/run-guard.py hook` (a Claude Code Stop) and `hook --runner antigravity` (an Antigravity Stop), with an actual mark of a live process in the repository's `build/gate-in-flight/` and a state file of the test's own, answer as above. Done when: the test fails when `REAL` is not wired to `gate_in_flight`.
- [x] Have `python scripts/run-guard.py status` print `waiting` while a review, a CI gate or a local gate is in flight, naming it (`waiting for <what> in flight`), in its text and in `--json` (`state` `waiting`, `in_flight`), and `active` again once it has ended. Done when: a test shows each of the three in flight in both forms, and `active` after.
- [x] Write the mark from `scripts/check-all.ps1` and `scripts/measure-build.ps1` through one shared helper in `scripts/lib/` (`gate-mark.ps1`), atomically (a temporary file, then a replace), with this process's id, its start time and what runs, before the first step or queue wait, and remove it in a `finally`, keeping the gate's own exit code. Done when: `tests/check_all_test.ps1` and `tests/measure_build_test.ps1` each show the mark present while a step runs and gone after a pass and after a failure, with the exit code unchanged. Cheaper substitute: a lock file with no pid, which a crash leaves holding the run.
- [x] Name the local gate as a wait in `.claude/skills/process-phase/SKILL.md` and its two mirrors, beside the CI gate, and strike the foreground-loop lesson in `docs/phase-runs/2026-11-12-phase-0.md`. Done when: the mirror test passes with the new text.
- [x] Commit: `run: the guard waits on a long local gate`

**Test checkpoint:** `python scripts/run-tests.py test_run_guard`, `pwsh tests/check_all_test.ps1` and `pwsh tests/measure_build_test.ps1` exit 0, and removing the `gating` check from `on_stop`, or the `REAL` wiring, makes the guard harness fail.

> **Verified:** 2026-11-13 | §21 | 0362d7229e96e3ddda118e2b589aa8693ab648e1
> **Review:** `docs/reviews/d00-t03-s21.json`

## 22. CI gate waits for a push's own run before starting one

> **Started:** 2026-11-13T17:31:49Z

Filed 2026-11-13 from the phase 2 run, when the owner asked why the run seemed to block itself. `scripts/ci_status.py --gate` starts a workflow that has no run for its commit at its first look (`recover`, called from `main` before any sleep). Called seconds after `git push`, as `process-phase` has it called, that look comes before GitHub lists the push's own run, so the gate starts a second one (`workflow_dispatch`). `build.yml`'s concurrency group (`build-${{ github.ref }}`, with nothing cancelled on `main`) queues the second behind the first, and the gate waits for the newer of the two: measured on `9d23bad`, `2fd2c64` and `ab72045`, the push's run and the gate's were created three or four seconds apart, the Windows job took 12.5 minutes, and the gate waited about 25. Every section gates twice, so this doubles the longest wait of every section left in the plan.

**Corrected 2026-11-13 (plan review):** the plan review in `docs/reviews/00-workspace/D00-T03-s22.md` returned four gaps (3 P2, 1 P3), planned in below: the short-wait rule includes equality and holds for the persisted two-hour limit too; the hold has a stated place in the output (the `waiting:` line while it lasts, and one `start_held` object in the final JSON, so stdout stays one object); the tests read the fake clock at each look, so ten-second polling during the hold and ordinary polling after it are proved; and the documents say when the gate starts at once.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Hold the start in `scripts/ci_status.py`. Done when: a run that appears within the grace is never doubled, in `scripts/tests/test_ci_status.py`.
2. Say what the gate is doing, and keep the documents true. Done when: the waiting line names the hold, and the skill text and its mirrors match.

- [x] Add `START_GRACE_SECONDS` (90) and `GRACE_POLL_SECONDS` (10) to `scripts/ci_status.py`, and in `main` with `--gate` and `--sha` hold the start of a workflow that reads `none`: `recover` starts nothing until that long has passed since this gate's first look (by the `clock` it is given), and until then the gate looks again every `GRACE_POLL_SECONDS` instead of every `POLL_SECONDS`. Done when: with a fake runner whose push run appears on the second look, no `gh workflow run` call is made, nothing is recorded under `started` in the gate's state, the gate goes on to judge that run, and the fake clock reads 0 and 10 seconds at those two looks and 60 seconds apart at the looks after it (ordinary polling once no start is held). Cheaper substitute: a fixed sleep before the first look, which delays every gate, the ones whose run is already listed too.
- [x] Start a workflow that still reads `none` once the grace has passed, once per commit and workflow, as today. Done when: with a fake runner that never lists a run, the looks come at 0, 10, 20 and so on to 90 seconds by the fake clock, exactly one `gh workflow run` per workflow is made, at the 90 second look and not before, it is recorded under `started` before it is made, and the looks after it are 60 seconds apart.
- [x] Never let the hold cost the only chance to start: when the wait left is no longer than the grace left plus one grace poll (a short `--wait-minutes`, none, or the persisted two-hour limit on one commit nearly spent), the workflow is started on that look. Done when: `--gate --wait-minutes 0` with no run starts each workflow on its one look, as it does today; `--wait-minutes 1.5`, where the wait equals the grace, and `--wait-minutes 1` each start it at the first look; and a gate whose two-hour limit has 30 seconds left starts it at the first look.
- [x] Leave a read-only look (no `--gate`) as it is: it starts nothing and prints the command under `recovery`. Done when: the existing read-only tests pass unchanged.
- [x] Say the hold: while a start is held, the `waiting:` line on stderr ends with `holding the start of <workflows> for <N>s more in case the push's own run is about to be listed`, naming every workflow held; and the one JSON object `--json` prints at the end carries `start_held` (`{"seconds": N, "workflows": [...]}`: how long this gate held, and for which) when it held any, and no such key when it held none, so stdout stays a single object. Done when: tests through `main` with captured stdout and stderr read the line on each held look and not after, parse stdout as one object, and read `start_held` for a run listed on the second look (`seconds` 10, both workflows) and no key for a run listed at the first.
- [x] Correct the module docstring of `scripts/ci_status.py` (it says the gate starts the workflow "at the first look, before any sleep") and the `wait` action in `.claude/skills/process-phase/SKILL.md` and its mirrors in `.agents/skills/` and `.codex/skills/` ("the gate starts that workflow itself at its first look"): the gate starts it once it has had no run for 90 seconds, or at once when the wait it was given leaves no room for that. Done when: the three skill files are the same text and the skill mirror test passes, and the docstring names the grace, the ten-second looks during it and the start-at-once case.
- [x] Prove it on the real thing: with this on `main`, push a commit and run `python scripts/ci_status.py --gate --sha HEAD --wait-minutes 30` at once. Done when: `gh run list --commit <sha>` shows one `build.yml` run and one `plan.yml` run, both from `push`, the gate prints `proceed`, and its wait is recorded in the run file beside the 25 minutes measured before.
- [x] Settle the reviews' findings in `scripts/ci_status.py`: `recover` asks whether the start is still held after it has read the remote head, and a held start that comes due is looked at again at once (round 1); `start_held.seconds` adds up the time actually held across an interrupted hold (completion review). Done when: `scripts/tests/test_ci_status.py` starts both workflows at 95 s after a 15 s head lookup at the 80 s look, and reads 30 s held for a hold a failed look interrupts.
- [x] Commit: `ci: the gate waits for a push's own run before starting one`

**Test checkpoint:** `python scripts/run-tests.py test_ci_status` exits 0, and with the hold removed (the start made at the first look again) the test of a run that appears on the second look fails.

> **Verified:** 2026-11-13 | §22 | 4e7f3bc9e3f06a80f3f5520ebc89095c8d6e9b1c
> **Review:** `docs/reviews/d00-t03-s22.json`

## 23. Token and issue day stored together

> **Started:** 2026-11-21T13:22:29Z

Filed 2026-11-21 from the completion review of `D00 T05 §7` (`docs/reviews/00-workspace/D00-T05-s7.md`). `scripts/store-claude-token.ps1` writes the token to the user variable before it writes the day it was made to `%LOCALAPPDATA%\Bosun\claude-token.json`, so a record that cannot be written (held open, a full disk) leaves a new token beside the old token's day. And when the new day is unknown, a failed removal of the old record is ignored and the import reports success. `scripts/campaign-preflight.ps1` then judges the token's expiry by the wrong day.

**Corrected 2026-11-21 (plan review):** the plan review in `docs/reviews/00-workspace/D00-T03-s23.md` found that writing the day first only reverses the mismatch (an import stopped between the two writes leaves the old token with the new day, and a preflight can read that pair), and that a rollback cannot cover a record created or deleted. The day is therefore tied to its token instead of ordered around it: the record holds a fingerprint of the token it describes, and the preflight trusts the day only for that token.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Tie the day to its token. Done when: a record written for one token is never read as the day of another, whatever order the two writes are in or where an import stops.
2. Make the importer's own failures plain. Done when: a token or record that cannot be stored exits 1 naming which store needs repair, and never prints the token.

- [x] Record with the day a fingerprint of the token it belongs to (`token`: the first 16 hex digits of the token's SHA-256, which says nothing of the token) in `scripts/store-claude-token.ps1`, for a vault import, a pasted token and `-RecordOnly` (the fingerprint of the token then stored). Done when: `tests/start_run_test.ps1` reads back a record whose `token` matches the stored token's fingerprint after each of the three. Cheaper substitute: storing the token's last characters, which leaks part of it.
- [x] Trust the day in `scripts/campaign-preflight.ps1` only when the record's `token` is the fingerprint of the token the preflight read; a missing, different or absent fingerprint is "the day is not recorded for this token", with the same `-RecordOnly -IssuedOn` remedy. Done when: `tests/preflight_test.ps1`, with a still-valid old token and a record written for a newer one (inside and outside the minimum-days threshold) refuses it, and passes once the record is for that token.
- [x] Read the token back exactly (case-sensitive, the same string) before reporting success, and on any failure say which store needs repair, never "nothing changed" once a store was written: the token not stored (the record left as it was), or the token stored but its day not recorded (with the `-RecordOnly -IssuedOn` remedy, the stored day now distrusted by the preflight). Done when: tests with the key unwritable and with the record held open without sharing each exit 1 with that store named, the token in neither output stream. **Corrected 2026-11-21:** plan review gap 3.
- [x] Cover the three ways a day is set, each with distinct old and new values seeded and the record held open: a pasted token with a known day, a vault import with a known day, and a vault import whose day cannot be read (which removes the old record). Done when: each exits 1 with its stated reason, and the preflight then distrusts the day for the new token. **Corrected 2026-11-21:** plan review gap 4; a pasted token's day is today when none is given, so only a vault import can have an unknown day.
- [x] Remove `Save-Issued`'s temporary file (`<record>.<pid>.tmp`) when its write or rename fails. Done when: the held-record tests find no temporary file left in the record's folder. **Corrected 2026-11-21:** plan review gap 5.
- [x] Migrate the owner's existing record, written before the fingerprint, with `pwsh scripts/store-claude-token.ps1 -RecordOnly -IssuedOn <its own recorded day>`. Done when: `pwsh scripts/campaign-preflight.ps1 -Only claude` passes on this machine, the token never printed.
- [x] Commit: `campaign: the token and its issue day are stored together`

> **Evidence 2026-11-21:** the owner's record, written before fingerprints, was migrated with `-RecordOnly -IssuedOn` and its own day (2026-11-10, kept); `pwsh scripts/campaign-preflight.ps1 -Only claude` then passed here, the token never printed.

**Test checkpoint:** `pwsh tests/start_run_test.ps1` and `pwsh tests/preflight_test.ps1` exit 0, and fail when the preflight trusts a record whose fingerprint is another token's.

> **Verified:** 2026-11-21 | §23 | 204219c557da95a41610664171a42cbcdb9db617
> **Review:** `docs/reviews/d00-t03-s23.json`

## 24. Campaign hold restores itself and shows its state

> **Started:** 2026-11-21T18:38:27Z

Filed 2026-11-21 by `D00 T06 §1` from `todo/findings.md` (base lines 6 and 7, raised by the completion reviews of `§10` and `§18`). `scripts/campaign-hold.ps1` has only `-Apply` and `-Restore` (`scripts/campaign-hold.ps1:14`): a hold applied for a run stays on after the run ends until someone remembers to restore it, and nothing shows how old a hold is or how to undo it. Inside the owner's rule that nothing restarts a run, the run's own guard can still put the machine back when the run ends.

**Corrected 2026-11-21 (plan review):** the plan review in `docs/reviews/00-workspace/D00-T03-s24.md` returned ten gaps (7 P1, 3 P2), all answered by one design below: the hold record, which `scripts/campaign-hold.ps1` owns, carries when it was applied, its expiry and whether an automatic restore was tried, so the guard needs no state of its own about the hold. A hold belongs to a run that ends after it was applied (a hold applied after a run ended is never undone by that run); an automatic restore is tried once per hold, marked in the record before anything changes; every guard path that ends a run, and every hook or status read, makes the same decision; the earlier items, which kept the attempt in the run state, are replaced.

**Corrected 2026-11-21 (a live restore, and round 1):** two rules above were wrong and are replaced. (1) A record without `Expires` (written before this section) was read as expiring 24 hours after its file was written; the first guard event after the build read the owner's live hold from 2026-11-08 as expired and restored it mid-campaign (put back 25 seconds later, `-Hours 168`). Such a record now has no expiry: only the end of the run that claimed it restores it. (2) A hold belonged to a run that ended after it was applied, judged by the time the guard recorded the end, which for a run whose session died is the next status read, so a hold applied for the next campaign was undone (round 1, P1). An active run now claims the hold it sees (`hold_for`, on every hook event before it is handled and in `end` and `halt`), and only that run's end restores it; the guard's own attempt is keyed on the hold, so a restore that failed before marking the record is not tried again, and status asks for a manual restore only for the hold in place now. A restore also leaves the plan the owner switched to in force (`Set-PowerValue` activates a scheme only when it is already the active one).

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Give the hold record its life, a status and a once-only automatic restore. Done when: `tests/campaign_hold_test.ps1` reads them back.
2. Restore it from every path that ends or reads a run. Done when: `scripts/tests/test_run_guard.py` and the end-to-end test restore a held machine once.

- [x] Record the hold's life in the record `-Apply` writes in `scripts/campaign-hold.ps1` (temporary file, then replace, read back): `Applied` (UTC, written with the originals before the first change) and `Expires` (`Applied` plus `-Hours`, 1 to 168, default 24, checked before anything is written or changed); a repeat `-Apply` keeps the record whole, its originals, `Applied` and `Expires` included. A record written before this has no `Applied`: it is read as the file's last-write time (the record is never rewritten, so that time is when the hold began) and expires 24 hours later; a present but unreadable `Applied`, `Expires` or `RestoreTried` is refused by `Assert-Record` in `scripts/lib/campaign.ps1`, naming the field, before anything changes. Done when: `tests/campaign_hold_test.ps1` reads back both fields and the default expiry, a repeat `-Apply` with another `-Hours` that keeps all of them, the old three-field record shape read with its last-write time, `-Hours 0` and `-Hours 169` refused with nothing written or changed, and a malformed `Expires` refused by name. Cheaper substitute: the time the guard first saw the record. (findings base lines 6 and 7)
- [x] Add `-Status` to `scripts/campaign-hold.ps1`: with a record, print when it was applied and its age, the expiry, each held setting of the saved scheme with its value now and its original (an originally absent lock shown as absent), any automatic restore tried and its result, and the restore command with the targets this run was given; without one, `no hold` and exit 0; an unreadable record or a failed power query is one `FAIL` line naming what failed and the fix, exit 1. Done when: the test changes a live value after `-Apply` and switches the active scheme, and reads the current and original values of the saved scheme, the absent lock as absent, and `no hold` after `-Restore`; it runs the printed command as printed and reads every value restored; an unreadable record and a failing fake power tool each give their `FAIL` line. Cheaper substitute: printing the record file's raw JSON. (findings base line 7)
- [x] Add `-Auto` to `-Restore`: before anything changes, it writes `RestoreTried` (UTC) into the record (temporary file, then replace, read back) and refuses, changing nothing, a record that already carries one, printing the manual command (`-Restore` without `-Auto`, which still restores and ignores the mark); a failed automatic restore leaves the record with its mark and its originals for that manual restore. Done when: the test reads the mark written before the first change (by the fault injection stopping after one change), a second `-Auto` refused with the manual command, and the manual command restoring after it.
- [x] Read the hold's targets from the environment where the guard runs it: `scripts/campaign-hold.ps1` takes `-Record`, `-PowerCfg` and `-LockKey` from `BOSUN_HOLD_RECORD`, `BOSUN_HOLD_POWERCFG` and `BOSUN_HOLD_LOCKKEY` when they are set and no parameter is given, and the guard reads the record path the same way, so a test and the real guard act on the same isolated fixture. Done when: the end-to-end test below runs with only those variables set.
- [x] Decide and restore in one place, `release_hold(state, env, now)` in `scripts/lib/run_guard.py`: with a record (`env.hold()`, which reads `Applied`, `Expires` and `RestoreTried` the way the script does) and no `RestoreTried`, it restores when the hold has expired, or when the run is in an end state and ended after `Applied`; it runs `pwsh -NoProfile -NonInteractive -File scripts/campaign-hold.ps1 -Restore -Auto -ProbeSeconds 2` with `CREATE_NO_WINDOW`, stopped with its whole tree (`taskkill /T /F`) after 7 seconds, inside the 10 seconds the shortest hook (`SessionEnd`) is given, and keeps its last result (`hold_restore`: when, for which `Applied`, exit code and its first lines) in the run state, which `start()` keeps for a new run. It is called by every path that ends a run or reads one: `handle_event` (every Claude Code hook event, whether the run changed or not), `hook_antigravity`, the `end` and `halt` commands of `scripts/run-guard.py` and `settle_gone` (`status`), each under the guard's state lock, so two of them never restore at once. Done when: `scripts/tests/test_run_guard.py`, with a fake hold and restore, reads: one restore when a run ends through each of `SessionEnd`, the Stop hook's drain, `hook_antigravity`, `end` and `halt`; none from an `end` naming another session; none for a hold applied after the run ended (`-Apply`, a status read before launch, a new run, its end, in that order, restores at that end and not at the status read); one at expiry on a status read of a live, quiet run and none just before it; none a second time once `RestoreTried` is set, across a new run's start; and a 9 second fake restore stopped at 7 seconds with its tree, the failure kept and shown. Cheaper substitute: telling the operator to run `-Restore`. (findings base line 6)
- [x] Prove it on the real script end to end: with the `BOSUN_HOLD_*` variables pointing at the fixture's record, fake power tool and test registry key, apply a hold, start a run in a scratch guard state, and end it through `python scripts/run-guard.py end`; then read, without calling `-Restore` or `status` from the test, every held AC and DC value and the lock value equal to their originals and the record removed, within five seconds of the end. Done when: that test passes, and fails with `release_hold`'s call in `end` removed.
- [x] Say it in `python scripts/run-guard.py status` and `status --json`: a hold shows `hold: applied <age> ago, restored at the first hook event or status read after the run ends or after <expiry>` (a closed session cannot act at the deadline itself), and a failed automatic restore shows its result and the manual command; `--json` carries `hold` (`applied`, `expires`, `restore_tried`) and `hold_restore`. Done when: the status tests read those lines and keys with a hold, a failed restore, and no hold.
- [x] Refuse an expired hold in `Test-Hold` in `scripts/campaign-preflight.ps1`, using the same expiry the guard reads, with the fix `pwsh scripts/campaign-hold.ps1 -Restore` then `-Apply`. Done when: `tests/preflight_test.ps1` with an expired record whose live values still match fails with that fix, and an unexpired one passes.
- [x] Say it in the script's header help: `-Hours`, `-Status`, `-Auto`, the `BOSUN_HOLD_*` variables, and when the guard restores. Done when: the header names each.
- [x] Commit: `campaign: the hold restores itself when the run ends and shows its state (D00 T03 §24)`

**Evidence 2026-11-21:** `pwsh tests/campaign_hold_test.ps1` -> all passed (the life fields, a repeat apply with other hours kept whole, `-Hours 0` and `169` refused with nothing written, a malformed `Expires` refused by name, the old three-field record read with its write time, `-Status` after a drift and a plan switch, the printed command run as printed, the `FAIL` lines, `-Auto` marked before its first change and refused a second time, the `BOSUN_HOLD_*` targets); `pwsh tests/preflight_test.ps1` -> 137 checks passed, an expired hold with matching values failing with restore then apply; `python -m unittest scripts.tests.test_run_guard` -> all passed, with `CampaignHold` (each hook path restores once, Antigravity, a hold applied after a run ended, expiry on a quiet live run at its second and not before, another session's end, a failed restore tried once and kept across a new run, status lines and keys, a 9 s restore stopped at 7 s with its child) and `CampaignHoldEndToEnd` (the real guard's `end` and `halt` restore the fixture's six values, the lock and the record in under 5 s, and another session's `end` leaves it). Mutations caught: `release_hold` removed from `end` fails the end-to-end test; removed from `handle_event` fails four hook tests.

**Test checkpoint:** `pwsh tests/campaign_hold_test.ps1`, `pwsh tests/preflight_test.ps1` and `python scripts/run-tests.py test_run_guard` exit 0, and with `release_hold`'s call in `end` removed the end-to-end test fails.

> **Verified:** 2026-11-21 | §24 | 8aa2606bb239ceaf1f1797ce8a52bf0b6476438a
> **Review:** `docs/reviews/d00-t03-s24.json`

## 25. Preflight shows progress, times its checks and ends with one fix list

> **Started:** 2026-11-21T20:26:50Z

Filed 2026-11-21 by `D00 T06 §1` from `todo/findings.md` (base lines 8, 9, 29, 30 and 31, raised by the completion reviews of `§10`, `§17` and `§18`). `scripts/campaign-preflight.ps1` prints each result after its check finishes, with no name while a slow probe runs (the branch-protection call can sit silent for two minutes), no durations, the protection fix as prose without its JSON body, a success line that carries the undo command, and a failing run that ends with only a count (`$failed check(s) failed`).

**Corrected 2026-11-21 (plan review):** the plan review in `docs/reviews/00-workspace/D00-T03-s25.md` returned seven gaps (4 P1, 3 P2), planned in below: a protection fix that never writes over a state it could not read, and one that keeps the rules a partial protection already has; the printed commands proved by running them against a recording `gh`; the five-second target defined for failures that need no external process, and proved on a stream stamped as it arrives; each result timed by its own check; the undo checked in the help; each fix once, with counts; the recheck command carrying this run's options.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Say each check as it starts and time it. Done when: the output order is proven in `tests/preflight_test.ps1`.
2. End a failing run with one ordered fix list and the recheck command. Done when: a run with several failures ends with that list.

- [x] Name each check as it starts and give each result its own duration (a clock started at that check, not one for the run), and put first the checks that need no external process (`space`, `owner`, `lock`), so a failure they find is on screen before any probe starts; `-Only` runs in the same order whatever order it is given in. Done when: `tests/preflight_test.ps1` reads the output as a stream, each line stamped as it arrives while the preflight runs, and sees the `owner` failure within five seconds of the start beside a slow `git` (the `tree` check) and a slow `gh`, in the default order and in `-Only protection,tree,owner`; and two checks of different lengths (an instant one and a 3 second probe) show durations that differ by at least two seconds, which a run-wide clock or a constant fails. Cheaper substitute: timing only the whole run. (findings base lines 8 and 30)
- [x] Before the branch-protection request, name the host, the repository and the limit (`asking <host> for the branch protection of <owner>/<repo>'s main, up to <N> s`). Done when: the streaming test sees that line, with this checkout's origin and the given `-ProbeSeconds`, before the request ends. (findings base line 30)
- [x] Give each protection state its own fix: protection that cannot be read (no `gh`, a timeout, a refusal) gets the diagnostic (`gh auth status`, the account's access) and never a write; confirmed absence gets one copyable `PUT` with its whole JSON body on stdin, then the read-back; confirmed partial protection gets a `PUT` whose body keeps the rules GitHub returned (required status checks and pull request reviews as they are), or, when it holds push restrictions a body cannot carry over, the settings page to change it by hand. Done when: the test runs each printed command, as printed, against a recording `gh` and reads the `PUT`'s stdin body (admins enforced, force pushes and deletion off, an existing required status check kept) and the read-back's host, repository and branch equal to the origin's; an unreadable state prints no `PUT`; and the preflight itself issues only the read. Cheaper substitute: naming the endpoint in prose. (findings base line 29)
- [x] Keep the protection success to one short result line, and put its undo in the script's header help: `gh api --hostname <host> --method DELETE repos/<owner>/<repo>/branches/main/protection` removes protection the preflight's fix created on a branch that had none. Done when: the test of a protected repository reads one result line and no undo in the output, and the header help's undo, run against a recording `gh`, names the same host, repository and branch. (findings base line 31)
- [x] End a failing run with one ordered list: the fixes this account can make, then the elevated steps, then what only the owner can confirm, each fix once however many failures share it, then the one command that runs the same checks again with the options this run was given (`-Repo`, `-Only`, and every limit not at its default, single-quoted). Done when: a test with one failure of each kind and two setup failures sharing one elevated fix reads each fix once in its group, the exact count per group, and a recheck command that, run as printed against a checkout path with spaces and a nondefault threshold, finds the same failure. Cheaper substitute: repeating each check's inline fix. (findings base line 9)
- [x] Commit: `preflight: progress, timings and one fix list (D00 T03 §25)`

**Evidence 2026-11-21:** `pwsh tests/preflight_test.ps1` -> 155 checks passed, read as a stream stamped as it arrives: in `-Only protection,tree,owner` the owner failure is on screen within five seconds beside a slow `git` and `gh`; a check is named before its result; an instant check after a slow one has its own duration (tree 3 s, then hold under 1 s); the protection read names its host, repository and limit seconds before it ends; each protection state's fix, run as printed against a recording `gh`: an absent one's `PUT` sends the whole body to this origin's main, its read-back and undo name the same target, a partial one's mend keeps its required status check and linear history, push restrictions go to the settings page, and an unreadable, garbled or timed-out read gets a diagnosis and no write; the preflight itself only reads; a failing run's list has each fix once in its group and a recheck command that, run as printed on a checkout path with spaces, finds the same failure. Mutations caught: a clock for the whole run, the start line after the check, a write fix for an unread state. `-FreeDrives` is split on commas, as `-Only` is, since `pwsh -File` passes a list as one string.

**Test checkpoint:** `pwsh tests/preflight_test.ps1` exits 0, and with each check's clock replaced by one for the whole run the duration test fails, and with the start line printed after the check the streaming test fails.

> **Verified:** 2026-11-21 | §25 | 54cda884b10bd6061be9f95196882a8f7e6999c5
> **Review:** `docs/reviews/d00-t03-s25.json`

## 26. Review dispatcher shows round times and settled sources, and takes non-section reviews

> **Started:** 2026-11-21T22:02:47Z

Filed 2026-11-21 by `D00 T06 §1` from `todo/findings.md` (base lines 10, 11 and 13, raised by the completion review of `D00 T01 §2`). `scripts/review-dispatch.py --list-rounds` shows each pass's `seconds` only under `--json`; the settled items carried into a prompt do not say which file and line they came from; and a review of a change that belongs to no TODO section is refused ("exactly one returned plan review is required before rounds"), although the `independent-review` skill sends every nontrivial change to the dispatcher.

**Corrected 2026-11-21 (plan review):** the plan review in `docs/reviews/00-workspace/D00-T03-s26.md` returned five gaps (2 P1, 3 P2), planned in below: a verdict contract for `--kind change` (candidate, reviewer, effort, lenses, the round's verdict, retry on an empty reply, refusal of a second); both readers (`--list-rounds` and `stamp`) knowing the record; the destination enforced by place and by name; settled sources across repair cycles; a duration on every kind's own line.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Show what the records already hold. Done when: plain `--list-rounds` and the settled block are read back in `scripts/tests/test_review_dispatch.py`.
2. Give a change outside every section a review path its readers know. Done when: a change review runs, is recorded only where it may be, and is listed and refused as section evidence.

- [x] Show each pass's `seconds` on its own line of plain `--list-rounds` output in `scripts/review-dispatch.py`, for every kind it lists (plan, round, completion and change), as the whole seconds recorded (`0` for a measured zero-second pass) or `unmeasured` for a record written before seconds were kept. Done when: the test lists a findings file holding one pass of each kind, measured, plus a legacy one with no `seconds`, and reads the exact duration on each kind's own line, `0` and `unmeasured` included. (findings base line 10)
- [x] Name the source of each carried settled item in the prompt block, as `<repository-relative findings file>:<one-based line>`, where `settled_items` found it across the base file and its repair-cycle files. Done when: the test with a base file and a cycle file, one item repeated in both and one only in the cycle file, reads each item once in the prompt the fake reviewer receives, the repeated one with the base file's path and line and the cycle-only one with the cycle file's. (findings base line 13)
- [x] Add a one-pass `--kind change --candidate <the full 40-character SHA of the tip>` (the range goes in the prompt, as a round's does) for work outside every TODO section: it takes the pinned reviewer and the effort and lenses of the schedule's first round, needs no plan review and no section, and is recorded as `## Change review` with the round format's verdict (`HIGHEST:` then `OVERALL: approve` or `OVERALL: needs-attention`). An empty, timed-out or malformed reply records nothing and leaves the pass open for the next attempt; a returned one consumes it, and a second `--kind change` on that file is refused before any reviewer starts. Done when: the test runs a change review through the documented command with a fake reviewer answering `approve`, then on a new file `needs-attention`, and reads both records; an empty reply leaves no record and a retry runs; a second run on a returned file is refused with no reviewer started. (findings base line 11)
- [x] Record a change review only under `docs/reviews/changes/`, under a name that is not a section's (`DNN-TNN-sN`, a `-cycleN` repair file or an `audit-` file), refused before any reviewer starts or any file is written. Done when: the test is refused for an ordinary `.md` outside `docs/reviews/changes/` and for a section name and a repair-cycle name inside it, each leaving no file and no reviewer call.
- [x] Make the readers know it: `--list-rounds` lists a change record in plain and `--json` output, and `python scripts/todo-graph.py stamp` refuses a change record as a section's plan, round or completion evidence. Done when: the listing test reads the change record in both outputs, and a stamp test naming a change file in `--plan` and in `--review` is refused. Cheaper substitute: a change record nothing lists.
- [x] Point `.claude/skills/independent-review/SKILL.md` and its mirrors in `.agents/skills/` and `.codex/skills/` at `--kind change` for work outside a section. Done when: the three copies match and name the command and the `docs/reviews/changes/` place. Cheaper substitute: telling the skill to skip review for such changes.
- [x] Commit: `review-dispatch: round times, settled sources and reviews outside a section (D00 T03 §26)`

**Corrected 2026-11-22 (round 2):** the change item said `--candidate` took a commit or a range; it takes the tip's full SHA, which the dispatcher requires, as the skill says.

**Evidence 2026-11-22:** `python -m unittest scripts.tests.test_review_dispatch` -> Ran 119 tests, OK (skipped=3), with `ChangeReviews` (approve and needs-attention change reviews recorded under `docs/reviews/changes/`; a second refused before any reviewer starts; an empty reply leaves the pass open and a retry records; refused outside the place, and for a section, repair-cycle or audit name inside it, with no file written and no reviewer started; a section's pass refused there; plain `--list-rounds` shows each kind's duration on its own line, `0 s` and `unmeasured` included, and lists the change in plain and JSON output) and `Settled.test_each_settled_item_names_where_it_was_first_written` (an item in two files once, with the base file's relative path and line; a cycle-only item with the cycle file's); `python -m unittest scripts.tests.test_todo_graph` -> OK, with `Stamp.test_a_change_review_is_no_sections_evidence`. A measured zero-second pass now records `Seconds: 0`. The three `independent-review` skill copies match. Mutation caught: the destination check removed fails all five refusal cases.

**Evidence 2026-11-22 (completion review and CI):** `b04726a` closes the completion review's six gaps (the verdict pair at the end, each once; the record's folder by its real path, a junction to another review folder refused; the candidate a commit here and named in the prompt; one dispatcher per record by an exclusive claim; `--record` under any pin; the scheduled first round's effort and lenses read back) and CI's red plan run 44958958652 (a record spelled through the runner's RUNNER~1 temp folder was refused; the folder is now compared by its real, case-folded path). `python -m unittest scripts.tests.test_review_dispatch` -> Ran 126 tests, OK (skipped=3).

**Test checkpoint:** `python scripts/run-tests.py test_review_dispatch test_todo_graph` exits 0, and with the destination check removed from `--kind change` the refusal tests fail.

> **Verified:** 2026-11-22 | §26 | cd85267c1c536b8f9dc9ece18cb8caedddc2e878
> **Review:** `docs/reviews/d00-t03-s26.json`

## 27. CI gate sees a finished run sooner and says what it is waiting on

> **Started:** 2026-11-22T00:03:57Z

Filed 2026-11-21 by `D00 T06 §1` from `todo/findings.md` (base lines 18, 19 and 20, raised by the completion review of `§13`). `scripts/ci_status.py` polls every 60 seconds (`POLL_SECONDS`), so a section waits on average half a minute after its CI has finished; its `waiting:` line names each workflow's verdict but not the run's URL or its current job; and a confirmed refusal, an unanswered start and a successful start all print under `no run was started:`.

**Corrected 2026-11-22 (plan review):** the plan review in `docs/reviews/00-workspace/D00-T03-s27.md` returned six gaps (3 P1, 3 P2), planned in below: what a start prints when its run is not listed yet, and a restart that never starts twice; looks timed from their start, with the error pause, the start grace and the deadline in order; the newest run's URL and job, and a failed job read; the API count's unit; the old wording kept where no start was tried, with its readers; the measurement from the last workflow to finish on a gate that waited.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Look every 15 seconds while a run is open, within the API budget. Done when: the schedule is proven by the fake clock in `scripts/tests/test_ci_status.py`.
2. Say what is running and word each start outcome apart. Done when: golden-output tests read each wording.

- [x] Measure the delay on a real gate before the change: start `python scripts/ci_status.py --gate` while CI runs (at least one `waiting:` line seen), timestamp its `proceed`, and record in the run file the seconds from the last required workflow's completion (`gh run view <id> --json updatedAt`) to that `proceed`, with the run IDs and any workflow skipped by path filters. Done when: the record is in the run file. (findings base line 18)
- [x] Time each look from its start in `scripts/ci_status.py`: while any workflow's run for the commit is in progress or queued, the next look starts 15 seconds after the last one started (or at once if that look took longer); after a look in which any read failed, the next starts 120 seconds after it, and the 15-second pace returns after a look that read everything; the start grace's 10-second pace takes precedence while a start is held; no look starts past the wait's deadline. Done when: `scripts/tests/test_ci_status.py`, with fake calls that each take 3 seconds on the injected clock, reads a run finishing just after a look seen by a look starting at most 15 seconds after the previous look started, the exit code and `action: proceed`; a failed read followed by the 120-second pause and the return to 15; and a held start polled at 10. Cheaper substitute: polling every 5 seconds throughout. (findings base line 18)
- [x] Count the gate's API calls: every `gh` invocation this gate run makes (each run list, job read, remote-head read and workflow start, failed ones included), counted where the runner is called, carried in the JSON as `api_calls` (0 when it made none). Done when: the test compares `api_calls` with the fake runner's own record across several looks, and a two-workflow gate polling at 15 seconds with job details stays under 1,000 calls an hour (GitHub's limit for an authenticated user is 5,000).
- [x] Show, for each workflow in the `waiting:` line on stderr, its newest run for the commit (never an older finished one), its URL and its current job (or `queued` when it has none yet); a job read that fails shows `job unknown (<why>)` and leaves the verdict as it was; stdout stays one JSON object under `--json`. Done when: the test with an older green run and a newer queued run with no job reads the newer run's URL and `queued` on one look and its active job on the next, a failed job read reads `job unknown` with the pending verdict kept, and `--json` output parses as one object. (findings base line 19)
- [x] Word the start outcomes apart, each naming its workflow: `started <workflow>: GitHub accepted it at <time>; its run shows on its line once listed` for a start GitHub accepted (**Corrected 2026-11-22 (the completion review, a redesign):** this item paired a start with a `workflow_dispatch` run created after it, a guess that twice named the wrong run, since GitHub's dispatch returns no run identity; no run is credited to a start now, and the run's URL is on its workflow's own line), `start not answered: <workflow> (<what to check>)` for a start that got no answer, and `start refused: <workflow>: <why>`; a restart that finds an unanswered start in the gate's state neither starts it again nor calls it started. The causes where no start was tried (an unreadable remote head, a held lock, an unwritable state) keep `no run was started: <why>`, which `scripts/campaign-preflight.ps1` reads, and the `wait` action in `.claude/skills/process-phase/SKILL.md`, its mirrors in `.agents/skills/` and `.codex/skills/`, and their policy test say the new wording. Done when: golden-output tests for each outcome alone and for a mixed result read each outcome once, tied to its workflow, with no line on stdout contradicting one on stderr; the restart case starts nothing; the preflight's consumer still reads its line; the three skill copies match. (findings base line 20)
- [x] Measure the delay on a real gate after the change, as before it. Done when: both measurements are in the run file, side by side.
- [x] Commit: `ci: the gate sees a finished run sooner and says what it waits on (D00 T03 §27)`

**Evidence 2026-11-22:** `python -m unittest scripts.tests.test_ci_status` -> Ran 150 tests, OK, with `OpenRuns` (fake calls of 3 s each: a run finishing just after a look is seen by a look starting 15 s after the last one started, exit 0 and `proceed`; a failed read pauses 120 s and the 15 s pace returns; `api_calls` equals the fake's own record and stays under 1,000 an hour for two workflows with job reads; the waiting line names the newer run, `queued` then its job, then `job unknown`; each start outcome once, tied to its workflow, stdout and stderr agreeing; a start tied to its listed run names it; a restart with an unanswered start neither starts it nor calls it started). The existing pace and wording tests were updated to the new contract. `pwsh tests/preflight_test.ps1` -> 163 passed (its consumer reads the new start lines too). The `process-phase` policy test passes with the new wording in all three copies. Mutation caught: the 15 s pace removed fails seven tests.

**Evidence 2026-11-22 (completion review):** the six gaps are fixed (a finished sibling's URL kept; no run credited to a start, a redesign; an unrecorded acceptance still a start; a lost record or unanswered start never offered again in a read-only look; a refusal tried once per gate; the preflight's lookup naming the SHA). `python -m unittest scripts.tests.test_ci_status` -> Ran 158 tests, OK; `pwsh tests/preflight_test.ps1` -> 164 passed. Mutations caught: a refusal retried each look, a lost record offering a start, an unrecorded acceptance called unanswered.

**Test checkpoint:** `python scripts/run-tests.py test_ci_status` exits 0, and with the 15-second pace removed the schedule test fails.

> **Verified:** 2026-11-22 | §27 | 28f4873f62d887356a3665339cc40ca0c55f5664
> **Review:** `docs/reviews/d00-t03-s27.json`

## 28. Campaign hold as a Windows power request the run's process holds

> **Started:** 2026-11-22T01:33:25Z

Filed 2026-11-21 by the completion review of `§24` (`docs/reviews/00-workspace/D00-T03-s24.md`). `§24` keeps the machine awake by editing the power plan's sleep, hibernate and display timeouts and the screen saver lock, then restores them from the run guard's hooks; every gap its reviews found came from that: a restore must run inside a hook's ten seconds, must not act on a hold it did not choose, and must be visible when it fails before it can mark the record. Windows has the mechanism that needs none of this, as PowerToys Awake uses: a power request (`PowerCreateRequest` with `PowerRequestSystemRequired` and `PowerRequestDisplayRequired`, or `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED)`), held by a process and released by Windows the moment that process ends, crash included. The display never idles, so the screen saver never starts and its lock never engages, with no setting changed.

**Corrected 2026-11-22 (plan review):** the plan review in `docs/reviews/00-workspace/D00-T03-s28.md` returned seven gaps (3 P1, 3 P2, 1 P3), planned in below: the request does not stop a secure screen saver (Microsoft documents it), so the lock is the owner's setting the preflight refuses, never one the campaign writes; the holder released when the writer returns, not only when its window dies; this holder's own evidence, never the machine-wide state; `-Minutes` on the launcher, with its limits; the preflight's wording for each hold state; the retirement proven with zero setting writes and a manual restore; no scheduled task as a substitute.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Hold the machine through a power request for as long as the run's writer runs. Done when: the holder's own record says it holds while the writer runs, and it is gone within a second of the writer returning or the window closing.
2. Retire the power-plan edits and their restore. Done when: no campaign path writes a power setting or the lock value, and the preflight checks the hold and the owner's screen-saver lock instead.

- [x] Add the holder, `scripts/lib/hold-awake.ps1`: it calls `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED)`, checks its return value, writes one line saying whether it holds (`held` or `failed: <why>`) to standard output for its launcher and to `build/run-hold.json` (temporary file, then replace: its pid, the parent's pid, `acquired`, the minutes asked, when), then waits until the first of: its parent process ends (a crash or a closed window), its standard input closes (the launcher's release), or the minutes asked run out; Windows ends the request with the holder. Done when: a test starts the holder under a fake parent and reads `held` and the record; kills the parent and sees the holder gone within one second; closes its input and sees it gone; with `-Minutes 1` on the fake clock sees it held just before the minute and gone at it; and, with the call made to fail, reads `failed:` and exit code 3. Its evidence is this holder's own: the system's execution state can be held by any process (another campaign, a media player), so it is never read as this run's. Cheaper substitute: the power-plan edits of `§24`.
- [x] Hold in the launcher, `scripts/start-run.ps1`, for both runners: start the holder before the writer, say its line, start the writer even when the hold failed (a start is never blocked), and release the holder when the writer returns or fails to start, since the window (`-NoExit`) outlives it; the window's end or a crash releases it through the holder's parent wait. Add `-Minutes` (1 to 10080; omitted, the hold lasts while the writer runs), refused outside that range before anything starts. Done when: `tests/start_run_test.ps1`, with each fake writer and the shell kept alive after it returns, sees the holder started before the writer, released within one second of the writer returning and of a writer that fails to start, kept for `-Minutes 1` until its minute and gone after it while the writer runs on, and `-Minutes 0` and `-Minutes 10081` refused with nothing started; every console child started with no window.
- [x] Refuse the lock the request cannot stop: Microsoft documents that `SetThreadExecutionState` does not stop the screen saver, and a secure one locks the desktop. The preflight's `lock` check in `scripts/campaign-preflight.ps1` also fails on an active screen saver that locks on resume (`ScreenSaveActive` 1 with `ScreenSaverIsSecure` 1 under `HKCU:\Control Panel\Desktop`), as an owner fix (turn off "On resume, display logon screen", or the screen saver), which the campaign never writes itself. Done when: `tests/preflight_test.ps1` fails a secure active saver with that fix and passes an inactive one and a non-secure one, alongside the existing `InactivityTimeoutSecs` cases; the launcher's help says the hold keeps the machine awake and the display on, and that the screen saver's lock is the owner's setting the preflight checks, with the documentation link; and the `D99 T01 §3` item observes the desktop with the owner's own saver settings, not with the saver disabled.
- [x] Say the hold's state in the preflight's `hold` check: a live run whose holder holds passes (`held by run <pid>`); a live run whose holder failed fails with its reason; no live run, or one whose timed hold has ended, passes with what the launcher will do (`no run holds the machine now; the run's launcher holds it while its writer runs`); a hold record an older version left (`campaign-hold.json`) fails with `pwsh scripts/campaign-hold.ps1 -Restore`, and one that cannot be read fails with what to move aside. Done when: the preflight test reads each of the five states with its own wording.
- [x] Retire the power-plan edits and their restore: `scripts/campaign-hold.ps1 -Apply` refuses, pointing at the launcher; `-Status` and `-Restore` stay for a record an older version left; `claim_hold`, `hold_due`, `release_hold`, `real_hold` and `real_restore_hold` and their calls leave `scripts/lib/run_guard.py` and `scripts/run-guard.py`, so no hook, status read, `end` or `halt` writes a setting. Done when: with a real legacy record seeded and the BOSUN_HOLD_* targets on a fixture, the guard's hook, status, `end` and `halt` paths run with zero setting writes and the record unchanged; then a manual `-Restore` puts back the saved AC/DC values and the lock before removing the record, and a failed read-back leaves the record for a retry; the hold, preflight and guard tests pass; the help of `-Apply -Hours` and the manual walkthrough name the launcher instead.
- [x] Commit: `campaign: the hold is a power request the run's process holds (D00 T03 §28)`

**Evidence 2026-11-22:** `pwsh tests/hold_awake_test.ps1` -> 9 passed; `pwsh tests/start_run_test.ps1` -> 78 passed (the Claude and Antigravity writers held and released, `-Minutes 1`, 0 and 10081); `pwsh tests/campaign_hold_test.ps1` -> all passed (`-Apply` refused naming the launcher, `-Status` and `-Restore` on a legacy record); `pwsh tests/preflight_test.ps1` -> 164 passed (the five hold states, the secure screen saver refused, an inactive or non-secure one passing); `python -m unittest scripts.tests.test_run_guard` -> 111 OK, with `LegacyHoldIsNotTouched` proving the hook, status, `end` and `halt` paths leave a seeded legacy record, the power values and the lock byte for byte, then a failed manual restore keeping the record and a retry putting every value back. The guard's hold code left by restoring `scripts/lib/run_guard.py` and `scripts/run-guard.py` to `29badd5`, before `§24`, since no later section touched them. The walkthrough is `docs/acceptance/preflight.md` and the script's own help: both name the launcher; no other document described `-Apply`.

**Test checkpoint:** the holder test reads `held` while its parent lives and the holder gone within one second of its parent's death and of its release, and `pwsh tests/start_run_test.ps1`, `pwsh tests/campaign_hold_test.ps1`, `pwsh tests/preflight_test.ps1` and `python scripts/run-tests.py test_run_guard` exit 0; with the launcher's release removed, the writer-returns test fails.

> **Verified:** 2026-11-22 | §28 | 97e56f1903db14ef267aef7f5a0ecb55548de230
> **Review:** `docs/reviews/d00-t03-s28.json`

## 29. Antigravity waits see their result within seconds

> **Started:** 2026-11-22T04:45:11Z

Filed 2026-11-22 by `D00 T06 §2` from `todo/findings.md` (base lines 41 and 47, completion reviews of `§19` and `§21`). An Antigravity run has no heartbeat, so while a review, a CI gate or a local gate is in flight its Stop hook answers with `wait_for_flight` in `scripts/lib/run_guard.py`, which tells the session to sleep in steps of `WAIT_SLICE_MINUTES = 10` and look again; the session can see the result up to ten minutes late. Nothing may resume a run from outside its session (the owner's rule of 2026-11-12), so the fix is a shorter wait inside the session, never a watchdog.

**Corrected 2026-11-22 (validation before the build):** the section said to measure "when the session is told to look again" with fake gates; the guard only answers with an instruction and the session does the sleeping, so what bounds the delay is the step that instruction gives, and a local gate is watched by a command the session runs. The items below say so. Timed waits (`wait_in_session`) end at a known time and are not in this section.

**Corrected 2026-11-22 (plan review of §29):** the plan review in `docs/reviews/00-workspace/D00-T03-s29.md` returned five gaps (3 P1, 2 P2), all from giving each kind of wait its own instruction: a timed wait is answered before work in flight is checked, a review or CI gate can be in flight beside a local gate, and neither instruction said what to run again or what to read after. One command replaces them: `run-guard.py look` waits in the session on everything the run waits for at once and returns as soon as any of it changes, and the hook's answer is that one command with what to do with each result.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. One look for every wait. Done when: `look` returns within 5 seconds of any in-flight item ending, and not before.
2. One instruction. Done when: the Antigravity Stop hook's answer, for every combination of waits, is the runnable `look` command with what to do on each result.

- [x] Add `python scripts/run-guard.py look [--max-seconds N]` (default 540, under the shell tool's ten-minute limit) in `scripts/run-guard.py`, with its rule in `scripts/lib/run_guard.py`: it reads `in_flight` (a review, a CI gate, a local gate) and the run's timed wait every second, starts nothing and leaves nothing running, and returns with exit 0 and one of three first lines: `ended: <what ended>; still waiting: <the rest, or nothing>` as soon as any in-flight item ends or the timed wait passes; `nothing to wait for` at once when none is live; `still waiting: <all of it>` at its limit (base lines 41 and 47). Done when: `scripts/tests/test_run_guard.py` holds a fake local gate whose mark is live (`BOSUN_GATE_MARK_DIR`) and a fake review mark, checks `look` is still blocked after 3 seconds, ends the gate and reads `ended:` naming it with the review still waiting within 5 seconds of the gate's exit, then ends the review and reads `ended:` with nothing left; runs it with no mark and reads `nothing to wait for` within a second; and runs it with `--max-seconds 2` against a live mark and reads `still waiting:` after 2 seconds, never sooner. Cheaper substitute: lowering `WAIT_SLICE_MINUTES` to one minute, which leaves every result up to a minute late.
- [x] Have the Antigravity Stop hook (`on_antigravity_stop`, `wait_for_flight` and `wait_in_session` in `scripts/lib/run_guard.py`) answer every wait, timed, in flight or both, with one instruction: run `python scripts/run-guard.py look` in the foreground; on `still waiting:` run it again; on `ended:` read that tool's own result (the dispatcher's final output and exit code for a review, the action the CI gate printed, the local gate's printed result) and act on it as process-plan says, then keep watching whatever it says remains; build nothing while anything remains. Done when: tests drive the hook with a timed wait alone, a CI gate alone, a local gate alone, a timed wait with a CI gate, and a review with a local gate, and each answer holds the exact command line and those three actions, with no step longer than `look`'s own.
- [x] Rewrite the Antigravity wait instructions in `process-plan` and `process-phase` (`.claude/skills/`, `.agents/skills/` and `.codex/skills/`, six files) to the same `look` loop, replacing the ten-minute sleeps, and say that for Antigravity a long local gate is watched, not a turn ended on, while Claude Code still ends its turn for its heartbeat. Done when: a test reads the Antigravity wait paragraph of all six files and finds the `look` command and no ten-minute sleep in each, and fails when one still tells Antigravity to end its turn on a local gate.
- [x] Commit: `run-guard: Antigravity waits on one look that returns as soon as anything changes (D00 T03 §29)`

**Evidence 2026-11-22:** `python -m unittest scripts.tests.test_run_guard` -> 126 tests OK (115 at the build, 120 after five review rounds, 126 after the completion review). A review dispatcher's real mark beside a local gate's: the gate ends first with the review still waiting, then the review ends (round 5). `Look` holds two real gate processes with live marks: `look` is still blocked after 3 s, returns within 5 s of the first ending naming it with the other still waiting, then `nothing to wait for`; with no mark it returns at once, and with `--max-seconds 2` against a live gate after 2 s with `still waiting:`; a timed wait passing is an end, a failed state read is not. `AntigravityRuns` drives the Stop hook with a review, a CI gate, a timed wait, a CI gate beside a timed wait and a review beside a local gate, each answered with the one `look` loop. `AntigravityWaitWording` reads the six skill files (it failed before their rewrite). Mutation: `look` ignoring an ending -> both `Look` timing tests fail. The completion review's seven in-scope gaps are built: what the hook named is where `look` starts (`look_for`) and what is left is kept; every live review is read; a reader that cannot tell keeps that wait as last seen (and `status` still does not hold on it); a state turning unreadable stays listed; a halt ends the look; `--max-seconds` must be finite, 0 to 540; and the timing tests enforce one second for nothing live, three seconds blocked and five seconds to report. Mutations: the handoff dropped, and the can't-tell carry dropped, each fail their test. The command is `look`, not `watch`: `NoAutoResume` keeps the retired watchdog's name retired.

**Test checkpoint:** `python scripts/run-tests.py test_run_guard` exits 0; the blocked-then-ended test fails against a `look` that returns at its limit regardless, and the skill test fails with one Antigravity paragraph still saying ten minutes.

> **Verified:** 2026-11-22 | §29 | 4bbfc06e269925a9bf682112ac78bd8b3bc1a10a
> **Review:** `docs/reviews/d00-t03-s29.json`
## 30. Antigravity sees a failed guard, within the hook's time

> **Started:** 2026-11-22T06:33:28Z

Filed 2026-11-22 by `D00 T06 §2` from `todo/findings.md` (base line 42, completion review of `§19`). `scripts/run-guard.py` fails open on an Antigravity hook error and prints nothing (`hook --runner antigravity`), recording the failure only for `status`; the completion review of `§29` added a hook that cannot read or save the run state.

**Corrected 2026-11-22 (validation before the build):** `agy` 1.5.14's changelog documents one output a hook can show the operator without sending it to the model, `errorMessage.userMessage`, beside `{"decision": "continue", "reason"}`, which sends the session on; the guard uses it, so a failed guard is said and the session is not held.

**Corrected 2026-11-22 (plan review of §30):** the plan review in `docs/reviews/00-workspace/D00-T03-s30.md` returned eight gaps (5 P1, 3 P2). The five on `status` naming a gate's stage, elapsed time and output (base line 48) are a subject of their own and move to `§32`, with finding 48. The live check through Antigravity itself needs an attended `agy` session and is `D99 T01 §6`'s. This section keeps the notice, read input inside the fail-open boundary, a failure kept for `status` until a clean hook, and a deadline under the host's 60 seconds, since `snapshot` can wait up to 120 seconds on `query finish`.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Tell the operator a guard failed. Done when: every failure of the Antigravity hook prints one notice for the operator, holds nothing, and stays visible in `status` until a clean hook clears it.
2. Answer within the host's time. Done when: a stalled read cannot keep the hook past its deadline.

- [x] Move the Antigravity hook's fail-open from `scripts/run-guard.py` into `scripts/lib/run_guard.py` (`hook_antigravity_answer`), with reading standard input inside it, and on any failure (bad input, the hook raising, the run state unreadable, a save that fails, as after ending a woken hold or recording what `look` starts from) print `{"errorMessage": {"userMessage": "Run guard failed and is not holding this run: <error>; see python scripts/run-guard.py status"}}` with no `decision`, record the failure, and clear the failures only after a hook that succeeded (base line 42; the `agy changelog` line on `errorMessage` is the source). Done when: tests in `scripts/tests/test_run_guard.py` drive malformed input, a raising hook, a state that is a folder, and a failing save through the real CLI, and each exits 0 with that one notice and no `decision`, `status --json` shows the failure until a clean hook, and a failure record that cannot be written still prints the notice. Cheaper substitute: a log line nobody reads.
- [x] Give the hook a deadline inside the host's 60 seconds (`.agents/hooks.json`): `HOOK_SECONDS = 45`, set by `hook_antigravity_answer`, which the guard's `run` helper honours by bounding every subprocess it starts to what is left, so a `query finish`, git, CI or wake check that stalls is stopped and read as unknown, and the hook answers. Done when: a test stalls a reader's subprocess (a child that sleeps 120 seconds) under a 3-second deadline and reads the hook's answer within 5 seconds, with the child stopped. Cheaper substitute: lowering only `real_finish`'s timeout, which leaves the wake and git reads unbounded.
- [x] Commit: `run-guard: Antigravity sees a failed guard, within the hook's time (D00 T03 §30)`

**Evidence 2026-11-22:** `python -m unittest scripts.tests.test_run_guard` -> 135 tests OK after the completion review (strict UTF-8 input, a failure record that cannot be written or cleared logged and leaving no temporary file, nothing started past the deadline, a Windows job object stopping a descendant whose parent exited, and a raising hook and a failing save through the real CLI; each fails its mutation); 130 at round 3 (128 at the build; rounds 1 to 3 added a timed-out read stopping its whole tree, every stop the event decides taking no slow reads, and the real failure recorder failing its write). Through the real CLI, malformed input and a run state that is a folder each print one `errorMessage` notice with no `decision` and exit 0, and `status --json` keeps the failure until a clean hook clears it; a raising hook and a failing save, with the failure record unwritable, still give the notice. A reader's subprocess sleeping 120 s under a 3-second deadline is stopped and the hook answers within 6 s, the deadline cleared after. Mutations: the notice removed, and the deadline clamp removed, each fail their test.

**Test checkpoint:** `python scripts/run-tests.py test_run_guard` exits 0, and the notice tests fail with the hook's notice removed.

> **Verified:** 2026-11-22 | §30 | e63f1396a553fd5703ad50d2e6a3bd2394ff6922
> **Review:** `docs/reviews/d00-t03-s30.json`

## 31. Shortcut install says one fix for each way a badge fails

> **Started:** 2026-11-22T07:42:00Z

Filed 2026-11-22 by `D00 T06 §2` from `todo/findings.md` (base line 43, completion review of `§20`). `scripts/start-run.ps1 -InstallShortcut` renders each runner's badged icon through `scripts/build-icons.py --badge-fill` and takes its last output line; every cause exits 1, a missing `resvg-py` or Pillow and a refused write end in a Python traceback, `check_mark` turns a mark it cannot read into the same `ValueError` as a malformed one, and the launcher always advises installing the icon packages.

**Corrected 2026-11-22 (plan review of §31):** the plan review in `docs/reviews/00-workspace/D00-T03-s31.md` returned ten gaps (9 P1, 1 P2). Base lines 44 and 45 are declined and struck from this section: an unchanged rerun of `-InstallShortcut` measured 0.89 s against a cold 1.97 s on 2026-11-22, already under the finding's one-second target, so a digest cache would add four P1s of staleness and damaged-output cases to save nothing; and a hotkey, held system-wide by any application, honoured only from the desktop or Start menu, and an install-only switch that would start a campaign on its own, is not worth its four gaps for a campaign the owner starts by hand. Gaps 1 to 3 are built below: the launcher's own two failures, a shared exit-code map that `D02 T05 §4` reuses, and the builder tested directly.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. One exit code per cause. Done when: `build-icons.py --badge-fill` ends every failure in one line with its own code and fix, the old icon kept.
2. One fix per cause in the launcher. Done when: each cause, the launcher's own two included, gets its fix and the plain-icon shortcut.

- [x] Define the exit-code map in `scripts/build-icons.py`, its docstring and a `FAILURES` table: 2 a package missing (`resvg-py` or Pillow: `python -m pip install -r scripts/requirements-icons.txt`), 3 an input that cannot be read (a master or the mark missing or denied: its path and that it must exist and be readable), 4 an input that is not valid (a malformed mark or master: mend or remove it), 5 the renderer failing on valid input (the file and the renderer's own words), 6 the output that cannot be written (the cause from the error: a full disk, a file another program holds, a denied folder, each with its own remedy, the old icon kept); have `check_mark` tell a mark it cannot read from one that is malformed; and have `main` catch each on the badge path and print one line to stderr (base line 43; `D02 T05 §4` reuses the map for the app icon). Done when: a test in `scripts/tests/` drives each cause with real inputs (Pillow and `resvg-py` hidden from the import, a mark denied and one malformed, a master missing, a renderer error, an existing ICO the replace cannot take) and reads its code, exactly one diagnostic line, and the old ICO unchanged. Cheaper substitute: one code for every cause.
- [x] Have `-InstallShortcut` in `scripts/start-run.ps1` print the fix for the builder's code, and give its own two failures their own: python not on `PATH` (install Python 3) and an `-IconDir` it cannot create (the folder and why). Done when: `tests/start_run_test.ps1` drives a missing package, a malformed mark, an unwritable icon, no python and an `-IconDir` it cannot create, and for each reads the one fix that applies (no pip advice where pip is not the cause) and reopens the `.lnk` to find the plain Bosun icon; every console child started with no window.
- [x] Commit: `campaign: shortcut install says one fix for each way a badge fails (D00 T03 §31)`

**Evidence 2026-11-22:** `python -m unittest scripts.tests.test_build_icons` -> 26 tests OK after five review rounds and the completion review (a fill that is empty, bare or not a colour and an output inside the repository each code 4; a master that cannot be read code 3 and one that is not SVG code 4; the renderer failing on a master, on a valid mark, and in compositing each code 5 naming its file; a mark whose lookup is denied code 3; disk-full, held-file and denied writes each their own remedy). `pwsh tests/start_run_test.ps1` -> 95 passed after them (a drive that is not there, a builder that exits without a word, whose printed retry runs as pasted with the same arguments, a python Windows will not start, and native exits made to throw each fall back). At the build, its new test drove each cause on the badge path with real inputs (`resvg_py` and Pillow hidden from the import, a mark denied, a malformed mark, a master folder with no masters, the renderer raising, and a read-only ICO the replace cannot take) and reads codes 2, 2, 3, 4, 3, 5 and 6, exactly one stderr line each with its fix, and the old icon unchanged. `pwsh tests/start_run_test.ps1` -> 90 passed: a hidden `resvg_py`, a malformed mark, an icon it cannot replace, no python and an icon folder a file stands in each give their one fix (pip advice only for the missing package) and reopen to the plain Bosun icon. The launcher now checks the icon folder really is a folder, since `New-Item -Force` takes a file there without complaint. Mutation: every cause returning 1 -> 7 subtests fail.

**Test checkpoint:** `python scripts/run-tests.py test_build_icons` and `pwsh tests/start_run_test.ps1` exit 0, and the builder test fails with every cause mapped to exit 1.

> **Verified:** 2026-11-22 | §31 | 54bf86320ed3f4205335aff798cc0a5383252f76
> **Review:** `docs/reviews/d00-t03-s31.json`
## 32. Guard status names each wait, its elapsed time and its output

> **Started:** 2026-11-22T08:54:31Z

Filed 2026-11-22 from `D00 T03 §30`'s plan review, which moved finding 48 here (`todo/findings.md`, base line 48, completion review of `§21`). `status` says a review, a CI gate or a local gate is in flight (`in_flight` in `scripts/lib/run_guard.py`) without its stage, elapsed time or output location, and no record holds a stage or an output: a review's mark (`scripts/review-dispatch.py`, `build/review-in-flight/`) has its pid, reviewer, `started` (UTC text) and deadline; a local gate's (`scripts/lib/gate-mark.ps1`, written by `scripts/check-all.ps1` and `scripts/measure-build.ps1`) its pid, `started` (a process FILETIME, for identity) and name; the CI gate's wait (`scripts/ci_status.py`, `build/ci-gate.json`, one `waiting` slot) its commit, `since` (epoch seconds) and pid.

**Corrected 2026-11-22 (plan review of §32):** the plan review in `docs/reviews/00-workspace/D00-T03-s32.md` returned six gaps (3 P1, 3 P2), and three of the heaviest came from rewriting a gate's mark at every step: a temporary copy beside the live mark that `look` would read as a second gate, a measurement whose output moves between logs mid-run, and a queued outer process whose child does the work. The finding asks that a running gate be diagnosed without a process search: what it is, how long it has run, and where its output goes, which each tool knows when it starts. So every record is written once, at its start, and never rewritten; a per-step stage within a gate is not taken (its cost is those three gaps, for a line `check-all` and the measurement already print to their own console). Gap 2 (elapsed from each record's own start), gap 5's first look with no run yet, and gap 6 (one observation for every view) are built below.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Each tool records what it is and where its output goes. Done when: a review, a local gate and a CI gate each write those once, at their start, beside the start time they already keep.
2. `status` names them. Done when: one line per item in flight gives what it is, its elapsed time and its output, from one observation, and `look` still compares only stable identities.

- [x] Have `scripts/review-dispatch.py` write its pass (`plan`, `round N`, `completion` or `change`) and its `--record` path into its in-flight mark when it writes it; have `Enter-GateMark` in `scripts/lib/gate-mark.ps1` take an `-Output` it writes into the mark once, beside `since` (the gate's own UTC start) and `started` (the process FILETIME, for identity), with `scripts/check-all.ps1` giving `the console that started it` and `scripts/measure-build.ps1` its log folder (an outer process that queues a child naming the child's own mark); and have `scripts/ci_status.py` write, when it creates its wait, that its runs are listed by `gh run list --commit <sha>`. Done when: `scripts/tests/test_review_dispatch.py`, `tests/check_all_test.ps1`, `tests/measure_build_test.ps1` and `scripts/tests/test_ci_status.py` each read the new field while the tool runs, the CI one on a first look with no run listed yet.
- [x] Keep the readers' identity strings (`review_in_flight`, `ci_in_flight`, `gate_in_flight`) unchanged, which `look` and `look_for` compare, and add `wait_details` in `scripts/lib/run_guard.py` that reads each live item's kind, start and output once; have `status_view` build its word, its `in_flight` string, its lines and its JSON from that one reading, so a gate ending between two reads can't leave `waiting` with nothing named. Done when: a test reads `status` while a gate ends and finds the word and the items agreeing, the `in_flight` string unchanged for its callers, and `look` still waiting on a live gate whose details changed.
- [x] Have `status` (`python scripts/run-guard.py status`, its lines from `status_view` in `scripts/lib/run_guard.py`) print one line per item in flight (what it is, its elapsed time from its own start field, its output) and put the same in `--json` as a list, naming `unknown` for a field an older mark lacks. Done when: with a fixed clock and three distinct times (the process start, the gate start, the mark's file time), a test reads the exact elapsed time from the gate start for a local mark (`since`), a review mark (`started`, UTC text) and a CI wait (`since`, epoch seconds); two reviews and two local gates in one call each with its own values; an older mark of each kind showing `unknown`; and a legacy and a new CI wait in two calls; and the JSON list holds one entry per item, none duplicated or missing.
- [x] Commit: `run-guard: status names each wait, its elapsed time and its output (D00 T03 §32)`

**Evidence 2026-11-22:** `python -m unittest scripts.tests.test_run_guard` -> 143 OK after the completion review (bad starts through the real readers keep every wait, a wait that cannot be looked at is said and not counted, an older review mark says its pass is unknown, and the one-reading test fails on a second read); 140 after two rounds (a start no clock can show is unknown, and the tests run on today's clock); at the build, 139: `StatusNamesEachWait` reads, on a fixed clock with the process start, the gate start and the mark's file time all distinct, each item's exact elapsed time from its own start (a review's `started`, a local gate's `since`, a CI wait's `since`), two reviews and two gates in one call each with its own pass and output, an older mark `unknown`, a legacy and a new CI wait, one JSON entry per item with a temporary copy beside a mark not counted, the `in_flight` string unchanged, and `look` still comparing identities. `python -m unittest scripts.tests.test_review_dispatch` -> 127 OK (the mark's pass and record); `scripts.tests.test_ci_status` (the wait's output on a first look with no run); `pwsh tests/check_all_test.ps1` and `pwsh tests/measure_build_test.ps1` exit 0 (each mark's output while it runs). Mutation: elapsed from the process FILETIME -> 2 tests fail.

**Test checkpoint:** `python scripts/run-tests.py test_run_guard test_review_dispatch test_ci_status`, `pwsh tests/check_all_test.ps1` and `pwsh tests/measure_build_test.ps1` exit 0, and the elapsed-time test fails when elapsed is read from the process FILETIME or the mark's file time.

> **Verified:** 2026-11-22 | §32 | 2a0599f966bfbd1b2059d4489212718453ef2905
> **Review:** `docs/reviews/d00-t03-s32.json`
## Verification

- `pwsh scripts/campaign-preflight.ps1` exits 0.
- `python -m unittest discover -s scripts/tests` passes.
- `python scripts/todo-graph.py validate` and `python scripts/todo-graph.py plan --check` pass.
