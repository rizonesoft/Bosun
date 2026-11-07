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
- -> XREF: D02 T03 §6 -- consumed by this file
- -> XREF: D02 T03 §7 -- consumed by this file
- -> XREF: D02 T02 §11 -- consumed by this file
- -> XREF: D02 T02 §4 -- consumed by this file
- -> XREF: D07 T01 §4 -- consumes this file
- -> XREF: D06 T02 §5 -- consumes this file
- -> XREF: D01 T01 §5 -- consumed by this file
- -> XREF: D02 T02 §6 -- consumed by this file
- -> XREF: D07 T01 §5 -- consumes this file

## Outcome

- The panel shows the same state as `run-guard.py status --json`.
- Start refuses beside a live session, and halting records a ground.

**Adjacency:** list=applicable @ D04 T03 §2; document=not-applicable (nothing carried); settings=not-applicable (none); reporting=applicable @ D04 T03 §2; notifications=applicable @ D04 T03 §5; permissions=applicable @ D04 T03 §3; audit=applicable @ D04 T03 §4; exchange=not-applicable (none); reverse=applicable @ D04 T03 §3

**Adjacency rationale:** Start's reverse is Stop. The guard stays the only owner of run state: the panel calls its commands and never writes `state.json`.

## Implementation Order

| Order | Section | Deliverable | Depends On | Status |
| ----- | ------- | ----------- | ---------- | ------ |
| 1 | §1 | status --json for the guard | D04 T01 §1 | [ ] |
| 2 | §2 | Run page: state and health | §1, D02 T02 §11, D02 T03 §6, D02 T03 §7, D03 T01 §5 | [ ] |
| 3 | §3 | Start, stop and halt | §2 | [ ] |
| 4 | §4 | Guard log and resumed session tail | §2 | [ ] |
| 5 | §5 | Run notifications | §2, D03 T01 §5 | [ ] |
| 6 | §6 | Panel view of the heartbeat | §1, D01 T01 §5, D02 T02 §6 | [ ] |
| 7 | §7 | Move repos to the supervisor | §6, §2 | [ ] |
| 8 | §8 | Commands card and terminal launch | §3 | [ ] |

## 1. status --json for the guard

Fix this at the guard, which owns the data, instead of parsing two formats. Resolute's `todo-graph.py` query has a `--json` form to borrow the shape from.

**Phase:** 4
**Needs:** none
**Surface:** no

**Build order:**

1. Add `--json` to `scripts/run-guard.py`. Done when: it prints one object with `schema_version`.
2. Add a typed reader in `WorkflowClient`. Done when: every status word parses, old and new.

- [ ] Emit status, session, counters and `health()` as one object, citing `health()` at `R:\GitHub\Resolute\scripts\lib\run_guard.py:470`. Done when: `scripts/tests/test_run_guard.py` asserts the shape, and plain output is unchanged. Cheaper substitute: parsing the two-line output in C#.
- [ ] Read it in `src/Bosun.Core/Workflow/RunClient.cs`. Done when: fixtures for active, stalled, expired, waiting, parked and complete parse with their reasons, and older kits' words still parse.
- [ ] Read `RunClient` for the selected repo, running inside its distro for a WSL repo through `WorkflowClient`. Done when: a WSL fixture returns data, and a stopped distro shows "WSL not running" with no `wsl.exe` command run. Cheaper substitute: Windows repos only.
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
- [ ] Implement `IRunsSource` and `IRunBadgeSource` from `RunClient` in `src/Bosun.Core/Workflow/RunSources.cs`. Done when: fixtures fill the Overview Runs table and the title bar badge.
- [ ] Commit: `run: page with state and health`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~RunViewModel` exits 0, and fails if the change is reverted.

## 3. Start, stop and halt

Starting has to happen in the same way a person would, so the guard's prompt detection and its session records all behave normally.

**Phase:** 4
**Needs:** display-session
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
- [ ] Start a WSL repo's run through `wt.exe wsl.exe -d <distro> --cd <path> claude`, in `src/Bosun.Panel/Views/RunActions.cs`. Done when: an argument test keeps a spaced path whole.
- [ ] Show a parked section's reason, repair cycles used and findings link in `RunView.xaml`, with Grant one more repair, which confirms and runs `todo-graph.py unpark`. Done when: a fixture shows them, and the button is the only route the panel offers. Cheaper substitute: telling the operator to find the Markdown file.
- [ ] Commit: `run: start, stop and halt`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~RunActions` exits 0, and fails if the change is reverted.

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
- [ ] Read the guard log tail for the selected repo, running inside its distro for a WSL repo through `WorkflowClient`. Done when: a WSL fixture returns data, and a stopped distro shows "WSL not running" with no `wsl.exe` command run. Cheaper substitute: Windows repos only.
- [ ] Commit: `run: log tail`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~RunLogTail` exits 0, and fails if the change is reverted.

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

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~RunWatch` exits 0, and fails if the change is reverted.

## 6. Panel view of the heartbeat

`D00 T03 §11` built the heartbeat, and it is already running by now. This section shows it in the panel, and has the panel's repo registry drive it through the tool that owns it. **Corrected 2026-11-07:** this built the supervisor inside the logger, until the owner asked for it from day one.

**Phase:** 4
**Needs:** none
**Surface:** no

**Build order:**

1. Create `src/Bosun.Core/Workflow/HeartbeatClient.cs`. Done when: `bosun heartbeat status --json` parses.
2. Include it in `bosun status --json`. Done when: pages read one source.

- [ ] Read `bosun heartbeat status --json` in `HeartbeatClient.cs`. Done when: fixtures for a tick, a hold, an error and "WSL not running" parse. Cheaper substitute: reading `repos.json` directly.
- [ ] Call `bosun heartbeat add` and `remove` from `HeartbeatClient.cs`, never writing `repos.json`. Done when: a test proves the panel passes an argument list, and shows a refusal in full.
- [ ] Report the heartbeat task's state in `HeartbeatClient.cs`, with "Not available" and a reason when it can't be read. Done when: a fixture with the task missing says so. Cheaper substitute: showing the last known state as current.
- [ ] Commit: `run: panel view of the heartbeat`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~HeartbeatClient` exits 0, and fails if the panel writes `repos.json`.

## 7. Move repos to the supervisor

A repo must still work without Bosun, so its own watchdog stays available. It stands aside when Bosun supervises the repo, and a double tick can't resume twice.

**Phase:** 4
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §8 and §10.
**Job:** see every repo's heartbeat in one place, and choose who supervises it.
**Treatment:** a Supervised by word per repo, and Supervise with Bosun or Use the repo's own watchdog. Cheaper substitute that fails: two watchdogs fighting.
**Chrome:** the Unattended run and Fleet pages.

**Build order:**

1. Add a tick lock to `scripts/lib/run_guard.py`. Done when: two ticks together resume once.
2. Add the switch in `src/Bosun.Panel/Views/RunView.xaml`. Done when: moving a repo removes its own task and restores it on the way back.

- [ ] Add a lock around `watch_tick` in `scripts/lib/run_guard.py`. Done when: `scripts/tests/test_run_guard.py` proves two concurrent ticks give one resume. Cheaper substitute: hoping the schedules don't overlap.
- [ ] Add Supervised by to `health()` in `scripts/lib/run_guard.py`, reading Bosun's status file when present. Done when: a test proves a repo with no Bosun reads as its own watchdog, unchanged.
- [ ] Add Supervise with Bosun in `RunView.xaml`, running `bosun heartbeat add` for one repo. Done when: a test with a fake repo moves it both ways. Cheaper substitute: removing tasks by hand.
- [ ] Show every repo's supervisor, last tick and heartbeat age in `src/Bosun.Panel/Views/FleetView.xaml`. Done when: a fixture shows Windows and WSL rows with words.
- [ ] Show "Add the run guard kit first" in `RunView.xaml` for a repo with no `scripts/run-guard.py`, from `KitProbe`, naming the command. Done when: a fixture repo shows it, and Supervise with Bosun is disabled with that reason.
- [ ] Commit: `supervisor: move repos to Bosun`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~Supervise` exits 0, and fails if a repo ends up with both watchdogs.

## 8. Commands card and terminal launch

The owner wants the commands kept in one place, with a button to open a run in a new terminal: PowerShell on Windows, Bash in WSL. Commands run in a terminal the operator can see, so the panel never hides what a command does.

**Phase:** 4
**Needs:** display-session
**Surface:** yes
**Fidelity:** `design/SPEC.md` §8, Commands card.
**Job:** start or inspect a run in one press, without remembering commands.
**Treatment:** a card of commands with Copy and Open in terminal. Cheaper substitute that fails: a text file of commands.
**Chrome:** the Unattended run page, with `Btn` and Lucide glyphs.

**Build order:**

1. Create `src/Bosun.Core/Workflow/TerminalLauncher.cs`. Done when: each side builds the right argument list.
2. Add the card to `src/Bosun.Panel/Views/RunView.xaml`. Done when: built-in and stored commands open and copy.

- [ ] Build `wt.exe -d <repo> pwsh -NoExit -Command <command>` for a Windows repo in `TerminalLauncher.cs`, as an argument list. Done when: `tests/Bosun.Core.Tests/TerminalLauncherTests.cs` keeps a spaced path and quotes whole. Cheaper substitute: one command string.
- [ ] Build `wt.exe wsl.exe -d <distro> --cd <path> -- bash -lc <command>; exec bash` for a WSL repo in `TerminalLauncher.cs`. Done when: the test keeps a spaced path whole, and the shell stays open after the command.
- [ ] Fall back to a console window with a notice when `wt.exe` is missing, in `TerminalLauncher.cs`. Done when: a test with no `wt.exe` opens `pwsh` or `wsl.exe` directly.
- [ ] Add the built-in commands from the repo's capabilities in `RunView.xaml`: start the campaign with bypass mode, guard status, plan status, and preflight where the repo has it, using `python3` for a WSL repo. Done when: a fixture repo with no preflight doesn't list it, and a WSL fixture shows `python3`. Cheaper substitute: the same list for every kit.
- [ ] Offer Start WSL before opening a terminal in a stopped distro. Done when: a test proves pressing Open in terminal is what starts it, and polling never does.
- [ ] Add stored commands per repo in `panel.json`, with Add, Edit and Remove. Done when: a test reads them back, and a command on one repo doesn't show on another. Cheaper substitute: one global list.
- [ ] Add Copy to each command. Done when: the clipboard holds the exact text in a test.
- [ ] Warn when a stored command looks like it holds a secret, and still let the operator save it. Done when: a test with a planted token shows the notice, without blocking the save.
- [ ] Log each launch to `ActivityLog.cs`, naming the repo and command. Done when: a test finds one line per press.
- [ ] Commit: `run: commands card and terminal launch`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~TerminalLauncher` exits 0, and fails if a spaced path splits.

## Verification

- `dotnet test Bosun.slnx` passes.
- An attended check: a real run started from the panel survives a killed session through the watchdog.
