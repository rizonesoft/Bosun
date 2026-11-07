---
schema_version: 1
id: hooks-fleet
domain: 04-workflow
status: active
title: "TODO-04 -- Hooks, automations and fleet"
---

# TODO-04 -- Hooks, automations and fleet

> **Goal:** The operator can see which hooks really fire, the state of scheduled tasks, and which repo is on which kit generation.

> **Current state:** Measured 2026-11-07. Hooks are spread across `~/.claude/settings.json`, project and local settings. Automations write `build/automations/<name>/last.json`. Kit generations are only known by reading each repo.

## Inputs

- `design/SPEC.md` §9, §10.
- `.conclave/automations/run.py`, `docs/specs/2026-11-07-control-panel-design.md`.
- -> XREF: D04 T01 §1 -- consumed by this file
- -> XREF: D02 T02 §6 -- consumed by this file
- -> XREF: D07 T01 §4 -- consumes this file
- -> XREF: D02 T03 §4 -- consumed by this file
- -> XREF: D02 T03 §7 -- consumed by this file
- -> XREF: D02 T02 §11 -- consumed by this file

## Outcome

- Hooks are listed with their source and whether their script exists.
- Fleet shows each repo's generation from probes, read-only.

**Adjacency:** list=applicable; document=not-applicable (none); settings=not-applicable (settings files stay owned by Claude Code); reporting=applicable @ D04 T04 §3; notifications=applicable @ D04 T04 §2; permissions=not-applicable (per-user); audit=applicable @ D04 T04 §2; exchange=not-applicable (none); reverse=applicable @ D04 T04 §2

**Adjacency rationale:** The panel shows hooks but does not edit them. Fleet reports drift but never copies files, because the SDK's rule is that a project owns its copy.

## Implementation Order

| Order | Section | Deliverable | Depends On | Status |
| ----- | ------- | ----------- | ---------- | ------ |
| 1 | §1 | Hooks inspector | D04 T01 §1, D02 T02 §11, D02 T03 §4, D02 T03 §7 | [ ] |
| 2 | §2 | Automations panel | §1 | [ ] |
| 3 | §3 | Fleet page | §1, D02 T02 §6 | [ ] |

## 1. Hooks inspector

Hooks fail silently when a path moves. This merges all sources and proves each script is there.

**Phase:** 4
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §9.
**Job:** see what actually fires, and spot one that can't.
**Treatment:** a merged table with a Test action. Cheaper substitute that fails: opening three JSON files.
**Chrome:** the shell's page frame.

**Build order:**

1. Create `src/Bosun.Core/Workflow/HookInspector.cs`. Done when: user, project and local settings merge.
2. Add Test. Done when: a harmless payload runs against temporary state.

- [ ] Read each settings file, resolving `$CLAUDE_PROJECT_DIR`. Done when: a fixture with a missing script reads Script missing. Cheaper substitute: printing `env` values from settings.
- [ ] Add Test only for hooks known to isolate their state, starting with `run-guard.py hook` under `RUN_GUARD_STATE` pointed at a temporary file. Any other hook, including `campaign_guard.py`, shows Test not available with the reason. Done when: the live state file doesn't change, proven by a test, and a Recto fixture's Test is refused. Cheaper substitute: firing an unknown hook, whose side effects the panel can't know, against a live repo.
- [ ] Build the table per `design/SPEC.md` §9. Done when: each row names its source file.
- [ ] Commit: `hooks: inspector`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~HookInspector` exits 0, and fails if the change is reverted.

## 2. Automations panel

`last.json` has what's needed. A failed weekly task shouldn't wait for someone to look.

**Phase:** 4
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §9.
**Job:** see whether the scheduled checks ran, and run one now.
**Treatment:** a table with row actions. Cheaper substitute that fails: Task Scheduler's own window.
**Chrome:** a tab beside Hooks.

**Build order:**

1. Create `AutomationClient`. Done when: `tasks.json`, scheduler state and `last.json` merge.
2. Add actions. Done when: Run now, Open log, Install and Remove work.

- [ ] Read each task's state with `Get-ScheduledTask` output through `ProcessRunner`. Done when: a task never run reads Never, and a missing registration reads Not installed. Cheaper substitute: reading only `tasks.json`.
- [ ] Run `run.py <name>` for Run now. Done when: the result updates, and it runs through the task's own wrapper.
- [ ] Add `-Task <name>` to `.conclave/automations/install.ps1` and call it for Install and Remove. A repo whose installer lacks it shows those two actions unavailable, with the reason. Done when: one task appears and disappears, the others are untouched, and Activity logs both. Cheaper substitute: `-Uninstall`, which removes every task in the folder.
- [ ] Add failed automations to Needs you. Done when: exit 1 appears on Overview.
- [ ] Commit: `automations: panel`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~AutomationClient` exits 0, and fails if the change is reverted.

## 3. Fleet page

This is how "the latest and the best of all of them" stays true: drift becomes visible, and the operator decides when to port.

**Phase:** 4
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §10.
**Job:** see which repos are behind, and on what.
**Treatment:** one read-only table with links. Cheaper substitute that fails: a version number per repo, which the kit doesn't have.
**Chrome:** the shell's page frame.

**Build order:**

1. Extend `KitProbe` with component fingerprints. Done when: guard kind, dispatcher features, graph kind and hooks are read.
2. Create `Views/FleetView.xaml`. Done when: each repo has a row.

- [ ] Read each repo's generation from the files, recording features rather than hashes where the copies are adapted. Done when: fixtures for Steward, Recto, Isotone and this repo classify as expected. Cheaper substitute: byte comparison, which calls every adapted copy drifted.
- [ ] Build the read-only table per `design/SPEC.md` §10, linking each cell to its source file. Done when: no control writes into another repo, proven by a test of the view model's commands. Cheaper substitute: a one-click sync that overwrites a repo's owned copy.
- [ ] Commit: `fleet: page`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~Fleet` exits 0, and fails if the change is reverted.

## Verification

- `dotnet test Bosun.slnx` passes.
- The Fleet page's live values were spot-checked against two repos.
