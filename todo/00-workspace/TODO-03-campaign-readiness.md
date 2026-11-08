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

## Inputs

- The owner's campaign decisions in `docs/specs/2026-11-07-control-panel-design.md`, "How does the unattended campaign run?".
- `scripts/lib/run_guard.py`, `scripts/run-guard.py`, `scripts/review-dispatch.py`, `scripts/lib/todo_graph.py`, `scripts/ci_status.py`, `.conclave/reviewers.toml`.
- `.claude/skills/process-plan`, `process-phase`, `process-todo-section` and `review-todo-section`, mirrored in `.agents/skills` and `.codex/skills`.
- -> XREF: D00 T01 §3 -- consumed by this file
- -> XREF: D00 T01 §4 -- consumes this file
- -> XREF: D00 T02 §1 -- consumes this file
- -> XREF: D00 T02 §3 -- consumes this file
- -> XREF: D01 T01 §1 -- consumes this file

## Outcome

- `python scripts/run-guard.py status --json` reports one of complete, waiting, parked or stopped, with what each waits for.
- `pwsh scripts/campaign-preflight.ps1` exits 0 only when every check below has passed on this machine, through the exact resume command.

**Adjacency:** all=not-applicable (internal tooling and contracts with no records of their own)

**Adjacency rationale:** This is the machinery that builds the product. The operator sees its results in the Unattended run page, owned by `D04 T03`.

## Implementation Order

| Order | Section | Deliverable | Depends On | Status |
| ----- | ------- | ----------- | ---------- | ------ |
| 1 | §1 | CI status that proves the commit | -- | [x] |
| 2 | §2 | One section disposition across graph, guard and skills | -- | [x] |
| 3 | §3 | Review repair cycles and parking | -- | [x] |
| 4 | §4 | Reviewer attempt limits and diagnostics | §2 | [x] |
| 5 | §5 | Usage limits wait and resume | §2 | [ ] |
| 6 | §6 | Run end states and wake checks | §3, §5 | [ ] |
| 7 | §7 | Owned session recovery | §5 | [ ] |
| 8 | §8 | Campaign permissions | -- | [ ] |
| 9 | §9 | Named core tests and CMake resolution | -- | [ ] |
| 10 | §10 | Preflight and machine hold | §1, §4, §6, §7, §8, §9, §11, §12, §14, §15, D00 T01 §3 | [ ] |
| 11 | §11 | One global heartbeat | -- | [x] |
| 12 | §12 | Review caps of 33 | §3 | [x] |
| 13 | §13 | CI gate recovery that names its commit | §1 | [ ] |
| 14 | §14 | Stamp filed leftovers from the third cycle | §3 | [x] |
| 15 | §15 | One completion pass, as Steward and Recto run | §14 | [x] |
| 16 | §16 | Captured rate-limit output for the reviewer classifier | §4 | [ ] |

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
- [x] Add a waiting state to `scripts/lib/run_guard.py` (`wait_until` and a reason, set by `python scripts/run-guard.py wait --reason <why> --minutes <n>`), during which `watch` spawns nothing and counts no restart, `on_stop` lets the turn end without counting it idle, and `MAX_QUIET_HOURS` doesn't run. Done when: a fake clock across the wait ends in a resume, not `stalled` or `expired`.
- [x] Mark the run waiting with reason `reviewer-unavailable` from `scripts/review-dispatch.py` when a pass reaches the attempt cap, or at once for `auth` and `model`, for 30 minutes or until a longer `rate-limit` retry-after, exiting 3; and route exit 3 in `process-phase/SKILL.md` and its mirrors to end the turn. Done when: the guard rechecks every 30 minutes, and a recovered reviewer resumes the run. Cheaper substitute: switching to a different reviewer.
- [x] Open a new window of 5 attempts for a pass once its wait has passed, in `scripts/review-dispatch.py`, leaving its position in the schedule as it was. Done when: a test runs five failures, a fresh process, the wait passing and a returned review, which is recorded as that same position.
- [x] Respect a `rate-limit` retry-after on every retry, not only at the cap: the pass's `not_before` is kept in `build/review-attempts.json`, and no CLI runs before it, sleeping when it is 5 minutes away or less and waiting through the guard when longer. Done when: a test across two processes sees no CLI call before the deadline.
- [x] Give a run back a fresh no-progress budget when its wait ends, in `watch` in `scripts/lib/run_guard.py`: `restarts` back to 0 and `changed_at` to the end of the wait, so neither the resume nor the time spent waiting counts toward `MAX_RESTARTS` or `MAX_QUIET_HOURS`. Done when: an outage across several waits and longer than `MAX_QUIET_HOURS` ends in a resume, not `stalled` or `expired`.
- [x] Save `build/review-attempts.json` atomically and reserve each attempt before its CLI starts, refusing with exit 2 when the file can't be read or saved, and exiting 3 only once the guard's wait is saved, saying so when there is no run to wait. Done when: tests cover a kill after the reservation, an unreadable file and a failed save, each read back in a fresh process.
- [x] Show the wait in `python scripts/run-guard.py status`: its reason, its deadline, the failure class, the diagnostic file in `build/review-failures/` and what to do (for `auth`, sign the reviewer CLI in; for `model`, fix the pin). Done when: a test reads each from `status` and `status --json`.
- [x] Teach `process-todo-section/SKILL.md`, `review-todo-section/SKILL.md` and `process-phase/SKILL.md`, in all three mirrors, that a failed attempt writes nothing to the findings file, and that exit 3 from any plan, round or completion pass means the run waits: end the turn without building, retrying or stamping. Done when: a test reads it in each mirror, and no skill still says a failed round is recorded in the findings file.
- [x] Commit: `review: attempt limits and failure classes`

**Test checkpoint:** `python -m unittest scripts/tests/test_review_dispatch.py scripts/tests/test_run_guard.py` exits 0, and fails if a sixth attempt runs.

> **Verified:** 2026-11-08 | §4 | a9d215fe1e70f3cf63da32aa4454200dfafeef4e
> **Review:** `docs/reviews/d00-t03-s4.json`

## 5. Usage limits wait and resume

The owner chose subscription only, so hitting a limit costs time and never money. Today three restarts with nothing changed call the run stalled, which can happen long before a limit resets.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Add wait state to `scripts/lib/run_guard.py`. Done when: a usage limit records when to retry.
2. Keep paid keys away from the run. Done when: a resumed session can't see one.

- [ ] Classify `StopFailure` data as `usage-limit`, `auth`, `overloaded` or `other` in `scripts/lib/run_guard.py`, storing `wait_until` from any reset time, or 30 minutes after the last failure when none is given. Done when: `scripts/tests/test_run_guard.py` covers each case. Cheaper substitute: only recording the failure text.
- [ ] Leave `restarts` and `MAX_QUIET_HOURS` unchanged while `wait_until` is in the future. Done when: a fake clock across a 5 hour limit ends in a resume, not `stalled`.
- [ ] Remove `ANTHROPIC_API_KEY`, `ANTHROPIC_AUTH_TOKEN`, `OPENAI_API_KEY` and `CLAUDE_CODE_USE_BEDROCK` from the environment passed by `resume_command`'s launch in `scripts/lib/run_guard.py`. Done when: a test with all of them set reads a clean child environment. Cheaper substitute: relying on them being unset.
- [ ] Write one guard log line in `scripts/lib/run_guard.py` when one of those variables was removed, naming the variable and no value. Done when: a test sees the line, and the run carries on.
- [ ] Treat `auth` as `waiting` with reason `sign-in-needed`, and write one line to the guard log. Done when: a test proves it never spins and resumes after sign-in returns.
- [ ] Commit: `guard: wait out usage limits without paid keys`

**Test checkpoint:** `python -m unittest discover -s scripts/tests -p test_run_guard.py` exits 0, and fails if `restarts` grows during a wait.

## 6. Run end states and wake checks

"No runnable rows" is not one state. A run waiting for the .NET release is not finished, and a finished build must not end before its closeouts are written. The owner's finish line is implementation complete, then an acceptance pass.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Split `drained` in `scripts/lib/run_guard.py`. Done when: complete, waiting, parked and stopped are distinct.
2. Add `todo/wakes.json`. Done when: a temporary Need clears by itself when its check passes.

- [ ] Define `complete` in `scripts/lib/run_guard.py` as every section outside phase 99 stamped, with the Verification of each of those files run and each of those phases closed out in `docs/phase-runs/`. Done when: a test with a stamped section but no closeout does not read complete, and phase 99 rows do not prevent it. Cheaper substitute: treating an empty ready list as done.
- [ ] Refuse `complete` in `scripts/lib/run_guard.py` unless `scripts/ci_status.py --sha <pushed head> --use-proven` exits 0. Done when: a test with a red head does not read complete, and its acceptance file names the failing workflow.
- [ ] Add pending file verification and phase closeouts to `query disposition` in `scripts/lib/todo_graph.py`. Done when: a crash after the last stamp leaves work the watchdog resumes.
- [ ] Keep a run with only `waiting-host` or `waiting-person` rows active in state `waiting`, with `watch` rechecking each tick. Done when: a test proves a row becoming ready resumes the run with no prompt.
- [ ] End a run whose remaining work outside phase 99 is only parked sections and their dependants as `parked`, never as complete, and write `docs/acceptance/<date>.md` in both `parked` and `complete`, listing every parked section, each blocked dependant with its root blocker, and every phase 99 row with how to run it. Done when: fixtures for both states produce the file, and `parked` is never reported as implementation complete.
- [ ] Create `todo/wakes.json`, mapping a section and Need to a check command, and clear that Need from the section through `scripts/todo-graph.py`. Done when: a test proves a passing check removes the Need in a commit, and a failing one leaves it. Cheaper substitute: an operator editing the section.
- [ ] Add the first wake in `todo/wakes.json` for `D00 T02 §3` `third-party`, using `scripts/dotnet-final-available.py`. Done when: a faked `dotnet --list-sdks` with a final 11.0.1xx SDK passes, and with only the RC fails.
- [ ] Extend `.claude/skills/process-plan/SKILL.md` and `process-phase/SKILL.md` and their mirrors to dispatch work items from `query disposition`: section execution, file verification and phase closeout. Done when: a test simulating a crash after the final stamp resumes only the verification and closeout, then reads complete. Cheaper substitute: only listing the pending work.
- [ ] Commit: `guard: end states and wake checks`

**Test checkpoint:** `python -m unittest discover -s scripts/tests -p test_run_guard.py` exits 0, and fails if an empty ready list reads complete.

## 7. Owned session recovery

A hung resumed session is only reported today, and any Claude session in the checkout blocks recovery. Over days, sessions will end, hang and lose their transcripts.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Track the run's own session in `scripts/lib/run_guard.py`. Done when: an unrelated session does not count as the run.
2. Recover it. Done when: a hung child and a missing session both resume.

- [ ] Hold the run's session and child identity in `scripts/lib/run_guard.py`, and treat only those as live. Done when: `scripts/tests/test_run_guard.py` proves an idle unrelated session does not block `watch`, and that a second writer is still refused while its session is holding the tree.
- [ ] Stop an owned child and its process tree after `CHILD_HANG_HOURS` with no change, unless `bosun status --json` shows it holding or waiting for a slot. Done when: a test stops a hung child and leaves one waiting in the queue. Cheaper substitute: reporting the hang and leaving it running.
- [ ] Start a new session with `claude -p "process plan"` when `--resume` fails because the transcript is gone. Done when: a fake CLI returning that error leads to one fresh start, recorded in the guard log.
- [ ] Add `--verify` to `scripts/run-guard.py status`, checking the task's action, principal, state and a tick within two intervals, for the repo's own task or for `Bosun heartbeat` when `bosun heartbeat list` holds the repo. Done when: a wrong action path reads unhealthy, and a repo on the heartbeat reads healthy with no task of its own. Cheaper substitute: checking that the task exists.
- [ ] Record a failed hook in `build/guard-health.json` from the fail-open path in `scripts/run-guard.py`. Done when: a corrupt state file shows up in `status`, and the session still is not blocked.
- [ ] Commit: `guard: owned session recovery`

**Test checkpoint:** `python -m unittest discover -s scripts/tests -p test_run_guard.py` exits 0, and fails if an unrelated session blocks `watch`.

## 8. Campaign permissions

A hidden session can't answer a prompt, so any prompt stalls the run. The owner chose bypass mode with a deny list, and told us on 2026-11-07 not to let caution block the run. So the list covers only what can't be undone, and a refusal returns its reason so the session carries on another way.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Add the deny rules to `.claude/settings.json`. Done when: each is refused in a real session.
2. Fix `resume_command`. Done when: a resumed session runs in the approved mode.

- [ ] Add deny rules in `.claude/settings.json` for force-push, `git push --delete`, `gh repo delete` and changing the repo's visibility, and for recursive deletes outside the repo, scratch, temp, and `build/`, through a `PreToolUse` hook. Done when: `scripts/tests/test_hook_commands.py` proves each is refused with a reason, while commits, pushes, branch work and cleaning scratch all pass. Cheaper substitute: an instruction asking the session not to.
- [ ] Refuse mail and chat sends in the same hook: the send tools of Gmail, Outlook and Teams connectors. Done when: a test sees those tool names refused, and web lookups and documentation tools pass.
- [ ] Set `permission_mode` to `bypassPermissions` for a run in `scripts/lib/run_guard.py`, validating it against `claude --help`. Done when: a test with an unknown mode refuses to start and names it. Cheaper substitute: forwarding any saved string.
- [ ] Prove a write, a queued build and a commit in a disposable repo through the exact `resume_command`, in `scripts/tests/test_guard_e2e.py`. Done when: the test passes on this machine with no prompt.
- [ ] Commit: `guard: campaign permissions`

**Test checkpoint:** `python -m unittest discover -s scripts/tests -p test_hook_commands.py` exits 0, and fails if force-push is allowed.

## 9. Named core tests and CMake resolution

Five test checkpoints pass a name to a test program that ignores it, so they can't fail for the reason they state, and the build command is not on PATH.

**Corrected 2026-11-08:** `scripts/campaign-preflight.ps1` doesn't exist yet; `§10` owns the preflight. This section creates the script with the CMake check as its first check, and `§10` adds its own. The names are reserved for `D01 T01` (`records`, `status_json`, `report_json`, `config`) and `D05 T02` (`pressure`), whose checkpoints already call them.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Add a name filter to `tests/test_core.cpp`. Done when: a name runs only its tests.
2. Resolve CMake. Done when: `cmake --version` works in a fresh session.

- [ ] Group the cases in `tests/test_core.cpp` by name and accept one name as an argument. Done when: an unknown name exits nonzero, and with no argument every implemented group runs. Cheaper substitute: ignoring the argument.
- [ ] Reserve the names `records`, `status_json`, `report_json`, `config` and `pressure` in `tests/test_core.cpp`, failing when selected and left out of the default run until implemented. Done when: selecting each exits nonzero, so a section can't pass without its tests, and an implemented group joins the default run.
- [ ] Create `scripts/campaign-preflight.ps1` with a CMake check, finding Visual Studio's CMake through `vswhere` and requiring it on PATH. Done when: `tests/preflight_test.ps1` proves a PATH without CMake fails with the folder to add named, and one with it passes. Cheaper substitute: a hardcoded Visual Studio path in a section.
- [ ] Add the Visual Studio CMake folder to the user PATH on this machine. Done when: a new shell and the watchdog's environment both run `cmake --version`.
- [ ] Commit: `tests: named core tests; preflight finds CMake`

**Test checkpoint:** `build\Release\test_core.exe no-such-test` and `build\Release\test_core.exe records` exit nonzero, `build\Release\test_core.exe` exits 0, and `pwsh tests/preflight_test.ps1` exits 0.

## 10. Preflight and machine hold

One command proves the run can finish before anyone walks away. It holds the owner's decisions: logged in and unlocked, no sleep or update restarts, and the final .NET SDK installed when it ships. Every other section waits for this one. It is attended on purpose: it runs once with the owner present, by naming it to `process-todo-section`, and the unattended campaign starts only after it is stamped.

**Phase:** 0
**Needs:** attended
**Surface:** no

**Build order:**

1. Create `scripts/campaign-preflight.ps1`. Done when: each check prints pass or fail with its fix.
2. Create `scripts/campaign-hold.ps1` and `scripts/campaign-admin-setup.ps1`. Done when: holds apply and restore.

- [ ] Check in `scripts/campaign-preflight.ps1`: `bosun`, CMake, `dotnet`, `gh` auth, Codex sign-in and a real `gpt-6-astra` reply, WSL `Ubuntu`, `/mnt/d`, D: free space, a clean pushed tree, green CI on HEAD and `run-guard.py status --verify`. Done when: `tests/campaign_preflight_test.ps1` proves each fails with a faked environment, and each failure prints the command that fixes it. Cheaper substitute: a checklist in a document.
- [ ] Create `scripts/campaign-hold.ps1 -Apply` for sleep, display timeout and lock, saving prior values, with `-Restore`. Done when: settings read back and restore byte for byte. Cheaper substitute: asking the operator to remember.
- [ ] Create `scripts/campaign-admin-setup.ps1`, run once elevated, to pause Windows Update restarts and register a SYSTEM task that installs the final .NET 11 SDK side by side with `dotnet-install.ps1 -Channel 11.0 -Quality GA -InstallDir`, given the folder of the `dotnet.exe` that the watchdog's launch environment resolves, retrying daily. Done when: the task exists, before release its run changes nothing, and it removes itself only after that environment's `dotnet --list-sdks` shows a final 11.0.1xx SDK. Cheaper substitute: the script's default folder, which belongs to the SYSTEM account.
- [ ] Add `-Restore` to `scripts/campaign-admin-setup.ps1` for update settings. Done when: `docs/acceptance` lists this as a step.
- [ ] Run the preflight on this machine. Done when: it exits 0 and its output is quoted here.
- [ ] Run a disposable campaign in a real session on a disposable repo with a fake CI answer, and read its log. Done when: `wait` starts no next row, `repair` repairs and rechecks, `exhausted` and `unverified` file the fault and carry on, and each holds after the session restarts.
- [ ] Prove recovery once: kill the session on the disposable repo and see the watchdog resume it. Done when: the guard log shows the restart.
- [ ] Commit: `campaign: preflight and machine hold`

**Test checkpoint:** `pwsh scripts/campaign-preflight.ps1` exits 0 on this machine, and exits nonzero with `gh` signed out.

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
- [x] Run `scripts/run-guard.py watch` per Windows repo, with an argument list, the repo as working directory, no window and a 120 second limit. Done when: tests prove a fake guard ticks, a hung one is stopped, the next repo still ticks, and a spaced path survives. Cheaper substitute: a shell string.
- [x] Run a WSL repo through `wsl.exe -d <distro> --cd <path> -- python3 scripts/run-guard.py watch`, only when `wsl.exe --list --running` shows the distro. Done when: tests prove a stopped distro stays stopped and reads "WSL not running". Cheaper substitute: a tick that wakes the VM.
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

Filed from `D00 T03 §1`'s seventh cycle, under the owner's rule that leftovers are filed from a section's third cycle on. The gate names `gh workflow run <file> --ref <branch>` when nothing proves a commit, which runs whatever the branch holds then, and it prints this only after polling.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Name the commit in the recovery in `scripts/ci_status.py`. Done when: a moved branch is explained, never silently run.
2. Print the recovery at the first look. Done when: no sleep comes before it.

- [ ] Compare the branch's remote head with the checked commit through `gh api` in `scripts/ci_status.py`, offering the dispatch command only when they match, and otherwise saying the branch has moved on and its newer head is what to gate. Done when: `scripts/tests/test_ci_status.py` covers a match, a moved branch and an unreadable head.
- [ ] Print the recovery to stderr at the first look, before any sleep, keeping one JSON object on stdout. Done when: a test with a 30 minute wait reads it before the first sleep.
- [ ] Commit: `ci: recovery names its commit`

**Test checkpoint:** `python -m unittest scripts/tests/test_ci_status.py` exits 0, and fails if a moved branch is offered the dispatch command.

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

## Verification

- `pwsh scripts/campaign-preflight.ps1` exits 0.
- `python -m unittest discover -s scripts/tests` passes.
- `python scripts/todo-graph.py validate` and `python scripts/todo-graph.py plan --check` pass.
