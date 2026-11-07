---
schema_version: 1
id: run-pages
domain: 04-workflow
status: active
title: "TODO-03 -- Unattended run page"
---

# TODO-03 -- Unattended run page

> **Goal:** An unattended run can be started, watched and stopped from the panel, with each of the three catches visibly working.

> **Current state:** Measured 2026-11-07. `scripts/run-guard.py status` prints a word then JSON, which is awkward to consume. Start, stop and halt commands exist. Nothing shows a run except the terminal.

## Inputs

- `design/SPEC.md` §8.
- `scripts/run-guard.py`, `scripts/lib/run_guard.py`, `.claude/skills/process-plan/SKILL.md`.
- -> XREF: D04 T01 §1 -- consumed by this file
- -> XREF: D02 T02 §9 -- consumed by this file
- -> XREF: D03 T01 §5 -- consumed by this file

## Outcome

- The panel shows the same state as `run-guard.py status --json`.
- Start refuses beside a live session, and halting records a ground.

**Adjacency:** list=applicable; document=not-applicable (nothing carried); settings=not-applicable (none); reporting=applicable @ D04 T03 §2; notifications=applicable @ D04 T03 §5; permissions=applicable @ D04 T03 §3; audit=applicable @ D04 T03 §4; exchange=not-applicable (none); reverse=applicable @ D04 T03 §3

**Adjacency rationale:** Start's reverse is Stop. The guard stays the only owner of run state: the panel calls its commands and never writes `state.json`.

## Implementation Order

| Order | Section | Deliverable | Depends On | Status |
| ----- | ------- | ----------- | ---------- | ------ |
| 1 | §1 | status --json for the guard | D04 T01 §1 | [ ] |
| 2 | §2 | Run page: state and health | §1, D02 T02 §9 | [ ] |
| 3 | §3 | Start, stop and halt | §2 | [ ] |
| 4 | §4 | Guard log and resumed session tail | §2 | [ ] |
| 5 | §5 | Run notifications | §2, D03 T01 §5 | [ ] |

## 1. status --json for the guard

Fix this at the guard, which owns the data, instead of parsing two formats. Resolute already has a `--json` form to borrow from.

**Phase:** 4
**Needs:** none
**Surface:** no

**Build order:**

1. Add `--json` to `scripts/run-guard.py`. Done when: it prints one object with `schema_version`.
2. Add a typed reader in `WorkflowClient`. Done when: all seven status words parse.

- [ ] Emit status, session, counters and `health()` as one object, citing `R:\GitHub\Resolute\scripts\lib\run_guard.py:341`. Done when: `scripts/tests/test_run_guard.py` asserts the shape, and plain output is unchanged. Cheaper substitute: parsing the two-line output in C#.
- [ ] Read it in `src/DevMode.Core/Workflow/RunClient.cs`. Done when: fixtures for active, stalled and expired parse.
- [ ] Commit: `guard: status --json`

**Test checkpoint:** `python -m unittest discover -s scripts/tests -p test_run_guard.py` exits 0, and fails if the change is reverted.

## 2. Run page: state and health

This shows the three catches separately (Stop hook, heartbeat, watchdog), because a run can look fine while one is broken.

**Phase:** 4
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §8.
**Job:** know an unattended run really is running, and would recover if it died.
**Treatment:** four cards with words and ages. Cheaper substitute that fails: a single status dot.
**Chrome:** the shell's page frame.

**Build order:**

1. Create `Views/RunView.xaml`. Done when: the four cards show live state.
2. Show health problems. Done when: an out-of-date heartbeat is called out.

- [ ] Build cards per `design/SPEC.md` §8. Done when: a stale tick reads as Watchdog not ticking, with its age. Cheaper substitute: one overall green light.
- [ ] Show the last API failure. Done when: a rate-limit record appears with its time.
- [ ] Feed Runs on Overview and the title bar badge. Done when: they match the page in a test.
- [ ] Commit: `run: page with state and health`

**Test checkpoint:** `devmode run -- dotnet test DevMode.slnx --filter FullyQualifiedName~RunViewModel` exits 0, and fails if the change is reverted.

## 3. Start, stop and halt

Starting has to happen in the same way a person would, so the guard's prompt detection and its session records all behave normally.

**Phase:** 4
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §8.
**Job:** start or end a run without remembering commands.
**Treatment:** one primary and two secondary actions, one danger with a form. Cheaper substitute that fails: printing the command to copy.
**Chrome:** `Btn.Primary` and `Btn.Danger`.

**Build order:**

1. Add Start. Done when: a Windows Terminal tab opens running `claude` in the repo with the `process plan` prompt.
2. Add Stop, Halt and watchdog actions. Done when: each runs the guard's command.

- [ ] Launch through `wt.exe -d <repo> claude "process plan"`, after checking for a live session. Done when: a live session refuses by name, and a missing `wt.exe` falls back to a console window with a notice. Cheaper substitute: spawning a hidden `claude -p`, which has no live view.
- [ ] Add Stop at end of turn, and Halt with ground and required reason via `run-guard.py halt`. Done when: an empty reason is refused, and the recorded ground shows on the page. Cheaper substitute: writing `state.json` directly.
- [ ] Add Install and Uninstall watchdog. Done when: state reads back from `health()`, and Activity logs each action.
- [ ] Commit: `run: start, stop and halt`

**Test checkpoint:** `devmode run -- dotnet test DevMode.slnx --filter FullyQualifiedName~RunActions` exits 0, and fails if the change is reverted.

## 4. Guard log and resumed session tail

When a run stalls, the first question is what it last did.

**Phase:** 4
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §8.
**Job:** see what the run did last.
**Treatment:** a monospace pane with pause. Cheaper substitute that fails: an Open log button alone.
**Chrome:** `LogBox` from the theme.

**Build order:**

1. Add the tail of `.git/run-guard/run-guard.log`. Done when: new lines appear.
2. Add `resumed-session.log`. Done when: both can be paused.

- [ ] Tail both files with a shared reader that handles rotation. Done when: a rotated file doesn't lose position or crash. Cheaper substitute: re-reading the whole file every refresh.
- [ ] Add auto-scroll with pause. Done when: scrolling up pauses, and resume returns to the end.
- [ ] Commit: `run: log tail`

**Test checkpoint:** `devmode run -- dotnet test DevMode.slnx --filter FullyQualifiedName~RunLogTail` exits 0, and fails if the change is reverted.

## 5. Run notifications

A stalled run at 3 a.m. should be waiting in the tray at breakfast, and the Overview should say so too.

**Phase:** 4
**Needs:** none
**Surface:** no

**Build order:**

1. Create `RunWatch`. Done when: a change to stalled, halted, expired or drained notifies once.
2. Feed Needs you. Done when: the row opens the run page.

- [ ] Watch registered repos at 60 second intervals while in the tray. Done when: a fake-clock test notifies once per transition. Cheaper substitute: polling every repo every 5 seconds.
- [ ] Implement `INeedsYouSource` for run state. Done when: a stalled run appears on Overview, and clears when resolved.
- [ ] Commit: `run: notifications`

**Test checkpoint:** `devmode run -- dotnet test DevMode.slnx --filter FullyQualifiedName~RunWatch` exits 0, and fails if the change is reverted.

## Verification

- `dotnet test DevMode.slnx` passes.
- An attended check: a real run started from the panel survives a killed session through the watchdog.
