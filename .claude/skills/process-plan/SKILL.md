---
name: process-plan
description: Front door for todo/implementation-plan.md. Audit the plan, then run process-phase on the first phase with a ready row, then the next ready phase after each closeout or park. Use when the user says process the plan. The argument --audit is audit only. A named phase is one phase and does not chain.
---

# Process the Implementation Plan

The only front door for `todo/implementation-plan.md` when the user did not name a single phase. The file is a generated projection of the 100% complete development plan (small, single-responsibility sections and granular five-part micro-steps): processing it is not "pick a row and improvise", and it is not "tick the boxes" (boxes flip only through `stamp`).

This skill does not ship a section and does not write a stamp. It audits, then chains `process-phase` runs until the plan is 100% complete or every leftover row is blocked here. Writes stay serial: the run this skill starts is the only writer on the tree.

## The loop

One continuous loop. You do not stop between sections or phases:

Before step 1, and again whenever a run is resumed, run `python scripts/todo-graph.py wake --apply`: it clears each Need whose check in `todo/wakes.json` passes now; commit what it changed (`todo: wake <ref>`). `query next` and `query disposition` also list close-out work once a phase's or a file's sections are all stamped: a `needs-verification` item is a file to close with `process-todo-file`, and a `needs-closeout` item is a phase whose closeout `process-phase` step 4 writes. When the run guard names a finishing step, take it: `needs-push` pushes the finished work so CI can prove it, and `needs-ci-repair` fixes a red head under `process-phase`'s red-first policy.

1. Audit (below), then start `process-phase` on the phase `python scripts/todo-graph.py query next` names, in this same turn.
2. After that phase's closeout or park, re-run `python scripts/todo-graph.py query next` and start the phase it names.
3. Stop only when `python scripts/todo-graph.py query next` prints null: every remaining `[ ]` row is blocked or waiting in this context, or the plan is done. That, and an operator stop, are the only halts. A wait is not a halt: when `scripts/review-dispatch.py` exits 3, the reviewer is unavailable and the run guard holds the run, so end the turn there, without building, retrying or stamping, and let the session's own heartbeat take the run up again when the wait passes.

A report to the operator is not a stop. An audit-only reply while a ready phase exists is a process defect: starting the phase is the next action, in the same turn. The self-correcting parts stay in the loop: drifted plan claims are corrected before building (inside `process-todo-section`), every gap and review leftover files through `add-todo`, and red gates are fixed, never bypassed (no `--no-verify`, no amend of a reviewed candidate, no force-push).

## The run guard holds this loop open

`scripts/run-guard.py` (hooks in `.claude/settings.json`, rules in `scripts/lib/run_guard.py`) enforces the loop above: the prompt `process plan` switches it on, the Stop hook sends a turn back while runnable rows remain, and a `CronCreate` heartbeat wakes an idle session. That keeps a live session working inside its open window, and nothing more: a run starts only when the operator starts it, and nothing restarts or resumes one (the owner's rule of 2026-11-12). When its session is gone (the window closed, a crash, a reboot) the run has ended, and `python scripts/run-guard.py status` records it as `ended`. Otherwise it ends only on an operator stop, no runnable rows, a stall (repeated attempts changing nothing) or a recorded halt: `python scripts/run-guard.py halt --ground <data-loss|credentials|shared-infrastructure|wrong-calculation|operator> --reason <why>`. A gate that cannot stamp a section is not a ground: record it, file the owner item and take the next row; a section whose completion review returned gaps has them fixed or filed and stamps in the same cycle, and a section is repaired only when its rounds ran out without a clean one, then parked when its repair cycles are used up. Check with `python scripts/run-guard.py status`.

## 0. Route

```powershell
git status -sb
python scripts/todo-graph.py validate
python scripts/todo-graph.py query next
```

| State | Do this |
| ----- | ------- |
| Another writer holds the tree | Stop and say so; never run two writers on one tree. |
| Argument is `--audit` | Run the audit below, then stop. Do not start a run. |
| Argument is a phase (`0`, `Phase 0`) | Run `process-phase` for that phase only; do not chain. |
| Empty argument, "the plan", or path | Audit below, then chain ready phases. |

## 1. Audit the whole plan

```powershell
python scripts/todo-graph.py validate
python scripts/todo-graph.py plan --check
python scripts/todo-graph.py query next
python scripts/todo-graph.py query blocked
python scripts/todo-graph.py query disposition
python scripts/todo-graph.py query stats
python scripts/run-tests.py
```

Fix every validator refusal before talking about shipping. If `plan --check` is stale, run `plan --sync` and re-check. The deep phase repair belongs to `process-phase` step 1; this audit is the commands above plus five recorded lines in the first phase's run file (`docs/phase-runs/<date>-phase-<N>.md`), not as the turn's last words:

1. Graph: valid section count, or the refusal.
2. Plan currency: the `--check` result.
3. Ready rows: count and first ref per phase.
4. Blocked rows: count and what blocks them (unmet dependencies, waiting Needs, unreachable hosts).
5. The phase being started, and why it is first.

Ready means runnable now in this context: `query ready` already excludes rows waiting on dependencies, inactive files and people Needs; `query disposition` gives each open row one state and reason for this session, so a row it doesn't call `ready` is a leftover here even when `query ready` lists it. A `needs-repair` or `needs-filing` row is work too, and it counts as a ready row when choosing a phase: `process-phase` repairs, parks, or files and stamps it. Otherwise `process-phase` re-reads it rather than trusting a previous list.

If `python scripts/todo-graph.py query next` prints null, report the leftovers with their blockers and stop. That is a genuine halt.

## 2. After a phase closeout or park

`process-phase` ends in exactly one of three ways: the phase table is all `[x]` with a written closeout; every leftover is blocked here and it parked (a `PARKED` line and park record in its run file); or the operator paused it. A parked phase is not complete and not a stall. Do not call it either.

Re-run `python scripts/todo-graph.py query next`. If it names a row, start `process-phase` on that row's phase in the same turn, same session, same rules. If it prints null, report which: plan done, or the leftovers and their blockers, with the run files named.

## Deny

- Do not invent a side loop that ships rows outside `process-todo-section` plus `review-todo-section`.
- Do not start a second run, and do not resume a paused run unless the operator said resume.
- Do not tick `implementation-plan.md` by hand, and do not edit it at all: it is generated.
- Do not claim a phase complete while its table has `[ ]` rows.
- Do not treat a named phase as the whole plan. Chaining is this skill's job, and only this skill's.
- Do not end the turn on the audit table.
