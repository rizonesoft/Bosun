---
schema_version: 1
id: dotnet-foundation
domain: 00-workspace
status: active
title: "TODO-02 -- The .NET foundation"
---

# TODO-02 -- The .NET foundation

> **Goal:** A .NET 11 solution builds warning-free beside the C++ tools, with central package versions and tests that run through the queue.

> **Current state:** Measured 2026-11-07. No .NET code exists. SDK `11.0.100-rc.2.26504.105` and the matching WPF runtime are installed. C++ sources sit directly in `src/`.

## Inputs

- `R:\GitHub\Steward\Directory.Build.props` and `R:\GitHub\Isotone\Directory.Packages.props`.
- `docs/specs/2026-11-07-control-panel-design.md`, the technology section.
- -> XREF: D00 T01 §6 -- consumed by this file
- -> XREF: D02 T01 §1 -- consumes this file
- -> XREF: D02 T02 §3 -- consumes this file
- -> XREF: D07 T01 §1 -- consumes this file

## Outcome

- `dotnet build DevMode.slnx` and `dotnet test DevMode.slnx` succeed with warnings as errors.
- The SDK pin moves to the final release when it ships, as one reviewed change.

**Adjacency:** all=not-applicable (internal tooling and contracts with no records of their own)

**Adjacency rationale:** Build configuration only.

## Implementation Order

| Order | Section | Deliverable | Depends On | Status |
| ----- | ------- | ----------- | ---------- | ------ |
| 1 | §1 | Solution, props and empty projects | -- | [ ] |
| 2 | §2 | Add .NET to the gates | §1, D00 T01 §6 | [ ] |
| 3 | §3 | Move the SDK pin to the .NET 11 release | §2 | [ ] |

## 1. Solution, props and empty projects

Everything later hangs off this shape. The pin allows the RC now and is moved once in §3.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Create `global.json`, `Directory.Build.props`, `Directory.Packages.props` and `DevMode.slnx`. Done when: restore succeeds.
2. Create `src/DevMode.Core`, `src/DevMode.Panel`, `tests/DevMode.Core.Tests` and `tests/DevMode.Panel.Tests`. Done when: the solution builds and an empty test passes.

- [ ] Create `global.json` pinning `11.0.100-rc.2.26504.105` with `rollForward: latestFeature` and `allowPrerelease: true`. Done when: `dotnet --version` from the repo prints the RC. Cheaper substitute: no pin at all.
- [ ] Create `Directory.Build.props` with `TreatWarningsAsErrors`, `Nullable` and artifacts output, plus the `_wpftmp` fix from `R:\GitHub\Isotone\Directory.Build.props`. Done when: a deliberate warning fails the build.
- [ ] Create `src/DevMode.Panel/DevMode.Panel.csproj` targeting `net11.0-windows`, with `UseWPF` and `InvariantGlobalization=false`, citing `R:\GitHub\Steward\src\Steward.Console\Steward.Console.csproj:25`. Done when: a templated binding runs in a test without throwing. Cheaper substitute: inheriting invariant globalization and crashing on the first binding.
- [ ] Create `src/DevMode.Core` and both test projects with xunit from `Directory.Packages.props`. Done when: `dotnet test` runs two passing tests.
- [ ] Commit: `dotnet: solution, props and empty projects`

**Test checkpoint:** `devmode run -- dotnet test DevMode.slnx --filter FullyQualifiedName~Foundation` exits 0, and fails if the change is reverted.

## 2. Add .NET to the gates

A section can only be held to tests that the one-command gate actually runs.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Extend `scripts/check-all.ps1`. Done when: it runs `dotnet test` through `devmode run`.
2. Extend `.gitignore`. Done when: `git status` is clean after a build.

- [ ] Add `dotnet build` and `dotnet test` to `scripts/check-all.ps1`. Done when: a failing test makes it exit nonzero. Cheaper substitute: running them outside the queue.
- [ ] Add `artifacts/`, `bin/`, `obj/`, `.vs/` and `*.user` to `.gitignore`. Done when: `git status --short` is empty after a full build.
- [ ] Commit: `gates: run the .NET build and tests`

**Test checkpoint:** `pwsh scripts/check-all.ps1` exits 0, and exits nonzero with a failing test added.

## 3. Move the SDK pin to the .NET 11 release

.NET 11 is due on 2026-11-10. The RC's go-live licence covers development until then, and this section moves everything at once when the final release is out.

**Phase:** 0
**Needs:** third-party,attended
**Surface:** no

**Build order:**

1. Install the final SDK and update `global.json` and package versions. Done when: no prerelease version remains in the repo.
2. Run the full gate. Done when: it passes on the final release.

- [ ] Set `global.json` to the final 11.0.1xx SDK with `allowPrerelease: false`. Done when: `dotnet --version` prints the final SDK. Cheaper substitute: leaving the RC pinned after release.
- [ ] Update prerelease versions in `Directory.Packages.props`. Done when: searching the repo for `rc.2` finds nothing outside dated notes.
- [ ] Remove the `third-party` Need from this section once the release is confirmed on the .NET download page. Done when: the page's latest version is recorded in the commit body.
- [ ] Commit: `dotnet: move to the .NET 11 release`

**Test checkpoint:** `pwsh scripts/check-all.ps1` exits 0 on the final SDK.

## Verification

- `dotnet test DevMode.slnx` passes.
- `python scripts/todo-graph.py validate` passes.
