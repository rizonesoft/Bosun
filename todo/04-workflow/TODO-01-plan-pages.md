---
schema_version: 1
id: plan-pages
domain: 04-workflow
status: active
title: "TODO-01 -- Plan pages"
---

# TODO-01 -- Plan pages

> **Goal:** The state of each repo's plan is readable in the panel through that repo's own tool, including this one.

> **Current state:** Measured 2026-11-07. `scripts/todo-graph.py` emits JSON for `query plan`, `query stats`, `query rounds` and `query deferred`, and `resolve` adds a section's body. Isotone's `query ready` is text.

## Inputs

- `design/SPEC.md` §6.
- `R:\GitHub\Conclave-SDK\extensions\vscode\src\core\checkPin.ts`, an existing consumer of these contracts.
- -> XREF: D02 T02 §6 -- consumed by this file
- -> XREF: D04 T02 §1 -- consumes this file
- -> XREF: D04 T03 §1 -- consumes this file
- -> XREF: D04 T04 §1 -- consumes this file
- -> XREF: D02 T03 §4 -- consumed by this file
- -> XREF: D02 T03 §7 -- consumed by this file
- -> XREF: D02 T02 §11 -- consumed by this file

## Outcome

- Each supported repo shows its plan, and an unsupported one says what the probe found.
- The panel never writes to `todo/`.

**Adjacency:** list=applicable; document=not-applicable (the Markdown is the document); settings=not-applicable (no settings); reporting=applicable @ D04 T01 §2; notifications=not-applicable (the run page owns them); permissions=not-applicable (read-only); audit=not-applicable (receipts are the audit); exchange=not-applicable (none); reverse=not-applicable (read-only)

**Adjacency rationale:** Deliberately read-only: stamps stay with the skills so a button can never bypass a review.

## Implementation Order

| Order | Section | Deliverable | Depends On | Status |
| ----- | ------- | ----------- | ---------- | ------ |
| 1 | §1 | Workflow client | D02 T02 §6 | [ ] |
| 2 | §2 | Plan board | §1, D02 T02 §9, D02 T02 §11, D02 T03 §4, D02 T03 §7 | [ ] |
| 3 | §3 | Section detail | §2 | [ ] |
| 4 | §4 | Dependency graph view | §3 | [ ] |
| 5 | §5 | Findings and deferred tabs | §3 | [ ] |

## 1. Workflow client

One typed client over each repo's tools. Its interpreter and working directory come from the repo, so each repo's own kit answers.

**Phase:** 4
**Needs:** none
**Surface:** no

**Build order:**

1. Create `src/Bosun.Core/Workflow/WorkflowClient.cs`. Done when: `query plan`, `stats`, `deferred` and `resolve` are typed.
2. Handle WSL repos. Done when: a WSL repo runs through `wsl.exe`.

- [ ] Run `python scripts/todo-graph.py` in the repo through `ProcessRunner`. Done when: fixtures captured from Steward and Recto parse, and exit 2 surfaces its stderr as the failed state. Cheaper substitute: reading `todo/` Markdown in C#.
- [ ] Convert a WSL repo's commands with an argument list, its distro, `--cd` and `python3`. Done when: a path with spaces survives. Cheaper substitute: a shell string.
- [ ] Return "WSL not running" from `WslState` before any command for a stopped distro in `WorkflowClient.cs`. Done when: a test proves no `wsl.exe` command runs, and the page shows its last data with its age. Cheaper substitute: letting the call start the VM.
- [ ] Respect `KitProbe` in every call. Done when: an unsupported repo never runs a query.
- [ ] Commit: `workflow: typed client`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~WorkflowClient` exits 0, and fails if the change is reverted.

## 2. Plan board

The everyday view of a plan: what's done, what can run and what's stuck.

**Phase:** 4
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §6.
**Job:** see what's left and what can run now.
**Treatment:** cards, then grouped rows with filter. Cheaper substitute that fails: a rendered copy of `implementation-plan.md`.
**Chrome:** the shell's page frame.

**Build order:**

1. Create `Views/PlanView.xaml`. Done when: sections group by phase with status words.
2. Add the filter, and read the repo from `SelectedRepo`. Done when: the filter works on a 400 row plan, and changing repo reloads the board.

- [ ] Build cards and the phase groups per `design/SPEC.md` §6. Done when: counts equal `query stats` in a test against the fixture.
- [ ] Virtualise the list. Done when: Recto's size stays responsive, with a measured render time. Cheaper substitute: loading every row into a stack panel.
- [ ] Show the unsupported state. Done when: it names what the probe found, and links to Fleet.
- [ ] Commit: `plan: board`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~PlanViewModel` exits 0, and fails if the change is reverted.

## 3. Section detail

A blocked row has to say what it's blocked on. Otherwise the operator is back in the terminal.

**Phase:** 4
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §6.
**Job:** understand why a section can't run and what it would do.
**Treatment:** master and detail. Cheaper substitute that fails: opening a text editor on the TODO file.
**Chrome:** a side pane in the Plan page.

**Build order:**

1. Add the detail pane from `resolve`. Done when: unmet dependencies are clickable.
2. Link evidence. Done when: receipt and findings file open.

- [ ] Show body, dependencies with state, and needs, from `resolve`. Done when: exit codes 0, 3 and 4 each draw correctly. Cheaper substitute: showing only the title.
- [ ] Link `docs/reviews/<ref>.json` and the findings file. Done when: a missing file reads as not yet reviewed.
- [ ] Commit: `plan: section detail`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~SectionDetail` exits 0, and fails if the change is reverted.

## 4. Dependency graph view

Order lives in the graph, so the graph should be visible. A layered drawing is enough, and it needs a keyboard path.

**Phase:** 4
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §6.
**Job:** see what unblocks the most work.
**Treatment:** layered nodes and edges within one phase at a time. Cheaper substitute that fails: an unreadable whole-plan hairball.
**Chrome:** a tab in the Plan page.

**Build order:**

1. Create `Controls/DependencyGraph.cs`. Done when: a phase's sections lay out with no crossings hidden.
2. Add keyboard navigation. Done when: arrows follow edges.

- [ ] Layer sections by dependency depth, with status word per node. Done when: a fixture lays out deterministically. Cheaper substitute: a static image from mermaid.
- [ ] Add arrow key navigation and the list as the equivalent. Done when: a test walks an edge by keyboard, and every node has an automation name.
- [ ] Commit: `plan: dependency graph`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~DependencyGraph` exits 0, and fails if the change is reverted.

## 5. Findings and deferred tabs

Filed defects and open deferrals are easy to forget. They belong beside the plan.

**Phase:** 4
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §6.
**Job:** see what was noticed but not yet scheduled.
**Treatment:** two tabs. Cheaper substitute that fails: leaving them to the file tree.
**Chrome:** tabs using `TabStrip`.

**Build order:**

1. Add Findings from `todo/findings.md`. Done when: entries show with their reference.
2. Add Deferred from `query deferred`. Done when: state and owner show.

- [ ] Display `todo/findings.md` as plain formatted text. Done when: an unscheduled finding feeds Needs you as a count.
- [ ] List `query deferred` with state word and owner reference. Done when: selecting a row opens the owner section.
- [ ] Commit: `plan: findings and deferred`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~PlanTabs` exits 0, and fails if the change is reverted.

## Verification

- `dotnet test Bosun.slnx` passes.
- Renders exist for each state, including the unsupported one.
