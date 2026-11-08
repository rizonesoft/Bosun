---
name: process-phase
description: Attended runner in Antigravity that takes one phase of todo/implementation-plan.md to 100 percent. Repair the phase, gap-check it, then ship section after section via process-todo-section plus review-todo-section, parking only when every leftover row is blocked here. Use when the user says process, run, or finish a phase.
---

# Process Phase

One phase, start to 100%, or parked when the rest of it cannot run here. You do not stop in between.

Exactly three endings: zero open rows and a written closeout; every leftover row blocked or waiting in this context, so the phase is parked and `process-plan` moves to the next ready phase; or the operator's own pause. A parked phase is neither complete nor a stall.

The whole plan is `process-plan`, not this skill. When the session entered through `process-plan`, return to it after closeout or park; a session pinned to one phase ends here.

Attended means interruptible, not stoppable: answer a mid-run message briefly and continue the loop in the same turn. The one exception outranks everything: if the user tells you to stop or pause, obey immediately, record it in the run file, and wait.

Completion never buys completion with a bypass: no `--no-verify`, no amend of a candidate under review, no force-push. A red gate is work, not a skip.

## Step 0: open the run

Check that no other writer holds the tree (`git status`; ask about unfamiliar uncommitted work). Writes are serial: this run is the only writer. Open the run file `docs/phase-runs/<YYYY-MM-DD>-phase-<N>.md` (create `docs/phase-runs/` if absent) and append every finding the moment it is made: the file survives session death where chat scrollback does not.

```markdown
# Phase run: <heading>

## Phase repairs              (Step 1: what was wrong with the plan, what was corrected, where)
## Shipped-row verification   (Step 1b: each spine [x] row, stamp and re-run checkpoint result)
## Gap audit                  (Step 2: gaps found, where each was filed; the park record lands here)
## Sections                   (Step 3: one entry per section: ref, outcome, review verdict)
## Critical events            (every stop, pause, resume, session death)
## Lessons                    (what was learned this run, worth keeping)
```

Read the most recent prior run file for this phase, if one exists: anything unresolved there is this run's first input.

## Step 1: repair the phase before running it

1. `python scripts/todo-graph.py validate`: fix every refusal now.
2. `python scripts/todo-graph.py plan --check`: if stale, `plan --sync`.
3. For every open row in the phase: `python scripts/todo-graph.py resolve '<ref>'`. Record the exit code. Then read `python scripts/todo-graph.py query disposition`, which gives each open row one state and reason for this session. A row it doesn't call `ready` is a leftover, not a stall: leave the row, ship every `ready` row, re-reading the state after each stamp, and park when only leftovers remain. A row `resolve` passes but this calls `attended` or host-bound waits for its session. When `scripts/review-dispatch.py` exits 3 on any plan, round or completion pass, the reviewer is unavailable and the run waits: end the turn without building, retrying or stamping. A `needs-verification` item from `query disposition` is a file whose sections are all stamped: run `process-todo-file` on it. A `needs-closeout` item is this phase with every row stamped: write its closeout (Step 4). A `needs-filing` row is work too: fix each gap its completion review lists and stamp it with `--completion '<findings file>:completed'`, or file each through `add-todo`, list each under that review as `- Filed: DNN TNN §N`, commit, then stamp it with `--completion '<findings file>:filed'`. A `needs-repair` row is work: run `python scripts/todo-graph.py repair '<ref>' --reason '<why>'`, commit it, then run `process-todo-section` on it again, in the new cycle's findings file. When its reason says the repair cycles are used up, run `python scripts/todo-graph.py park '<ref>' --reason '<why>'` instead, commit it and carry on with the next row; a parked section and everything depending on it go in the acceptance list. Exit 1 or 2 means the plan cites a section that does not exist: repair the reference against the TODO files; a broken reference blocks a park.
4. Read each open section's TODO file top to bottom for phase-level staleness only (per-section validation happens again inside `process-todo-section`): work already shipped elsewhere, sections made moot by a later decision, Current state blocks contradicted by the tree. Correct with dated `**Corrected YYYY-MM-DD:**` notes.

### Step 1b: shipped rows are verified, not trusted

`[x]` rows are claims. Re-check the shipped rows an open row in this phase depends on (the spine): `resolve` exits 3, the receipt matches its stamp, and the section's Test checkpoint still passes when re-run. A checkpoint that no longer passes means the section regressed: treat it as this phase's work through `review-todo-section` in audit stance before building on it.

## Step 2: gap-audit the phase for 100% completeness

Read the phase as a user would use it, end to end, against its governing documents (the milestone "Done when" in the foundation plan, the SPEC sections the phase implements). Ask what is missing for a 100% complete phase: screens, dialogs, controls, settings consumers, refusal/error paths, undo/reverse paths, persistence read-backs, or handoffs between domains nobody specified. Split any coarse umbrella sections into small, single-responsibility sections and expand any summary checkboxes into concrete five-part micro-steps. File each gap with `add-todo` (evidence, owner, dependency) and sync the plan. Never cap planning or silently drop scope. A gap found is a gap filed the same turn.

## Step 3: ship the phase, one row at a time

In the generated plan's table order, for each open row: `process-todo-section` (whose one-pass plan review comes before the build), then `review-todo-section`, whose one-pass completion and excellence review comes last, after clean regular rounds and whose verdict every new stamp carries (`stamp --completion '<findings file>:complete'`, beside the plan review's `--plan` findings file). Every gap it files becomes a new section through `add-todo` in this phase or the one that owns it. Record each outcome in the run file, with the completion verdict and any sections filed. Commit the section work, then its stamp commit. Re-read `python scripts/todo-graph.py query disposition` before each row, because the graph and this session both move: run a row it calls `ready`, repair or park one it calls `needs-repair`, file and stamp one it calls `needs-filing`, and skip the rest. `resolve` alone doesn't decide this, because it passes rows this session can't run. When every remaining open row is a leftover, the phase parks: write a column-0 `PARKED <UTC stamp> <one-line reason>` line in the run file followed by the park record (each leftover, what blocks it, where the blocker lives), commit the run file, and return to `process-plan` (or end, if pinned).

### Red-first CI

After each stamped section, push, then run `python scripts/ci_status.py --gate --sha HEAD --wait-minutes 30`, and act on the one action it prints. It remembers the last proven commit, so a restart after a push loses nothing:

- `proceed`: start the next row.
- `wait`: CI hasn't answered, or `gh` is unavailable. If it prints a `gh workflow run` command, as after a fresh clone, run that first, because a real run is the way to find out. Run the same command again, and don't start the next row. This costs time, not repair attempts, and it ends in `unverified` after two hours on one commit.
- `repair`: fix the red run on `main` before the next row, commit, push and run the gate again. A repeat check of the same commit uses no attempt, and the count survives a restart.
- `unverified`: two hours went by with no answer, or the gate couldn't save its state. Do as for `exhausted`, naming the reason.
- `exhausted`: three repairs have failed. File the fault through `add-todo` with the failing workflow and its URL, then carry on with the next row on local gates, because the run must not block itself. The guard refuses `complete` while `main` is red, so the fault stays visible at the end.

## Step 4: closeout

When the phase table is all `[x]`: `process-todo-file` on any file the phase exhausted, re-run the full test suite once, confirm `query stats` and the regenerated plan show the phase complete, write the closeout under a `## Closeout` heading of its own in the run file (a new one each time: one written before a later stamp no longer counts) (what shipped, what was repaired, what was learned), commit, and report. A phase is complete when its table says so and the closeout is written: not before.

## Guardrails

- Do not invent a side loop that ships rows outside `process-todo-section` plus `review-todo-section`.
- Do not tick `implementation-plan.md` by hand. It is generated; sync it.
- Do not claim a phase complete while its table has `[ ]` rows, and do not call a parked phase complete or a stall.
- Do not end the turn on the audit. Ship, park, or close out.
