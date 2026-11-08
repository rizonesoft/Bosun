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

**Corrected 2026-11-08:** Recto and Isotone moved to `D:\GitHub\Recto` and `D:\GitHub\Isotone`; their paths below are updated. Steward stays at `R:\GitHub\Steward`.

## Inputs

- `R:\GitHub\Steward\Directory.Build.props` and `D:\GitHub\Isotone\Directory.Packages.props`.
- `docs/specs/2026-11-07-control-panel-design.md`, the technology section.
- -> XREF: D00 T01 §6 -- consumed by this file
- -> XREF: D02 T01 §1 -- consumes this file
- -> XREF: D02 T02 §3 -- consumes this file
- -> XREF: D07 T01 §1 -- consumes this file
- -> XREF: D00 T03 §10 -- consumed by this file
- -> XREF: D00 T03 §6 -- consumed by this file

## Outcome

- `dotnet build Bosun.slnx` and `dotnet test Bosun.slnx` succeed with warnings as errors.
- The SDK pin moves to the final release when it ships, as one reviewed change.

**Adjacency:** all=not-applicable (internal tooling and contracts with no records of their own)

**Adjacency rationale:** Build configuration only.

## Implementation Order

| Order | Section | Deliverable | Depends On | Status |
| ----- | ------- | ----------- | ---------- | ------ |
| 1 | §1 | Solution, props and empty projects | D00 T03 §10 | [ ] |
| 2 | §2 | Add .NET to the gates | §1, D00 T01 §6 | [ ] |
| 3 | §3 | Move the SDK pin to the .NET 11 release | §2 | [ ] |

## 1. Solution, props and empty projects

Everything later hangs off this shape. The pin allows the RC now and is moved once in §3.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Create `global.json`, `Directory.Build.props`, `Directory.Packages.props` and `Bosun.slnx`. Done when: restore succeeds.
2. Create `src/Bosun.Core`, `src/Bosun.Panel`, `tests/Bosun.Core.Tests` and `tests/Bosun.Panel.Tests`. Done when: the solution builds and an empty test passes.

- [ ] Create `global.json` pinning `11.0.100-rc.2.26504.105` with `rollForward: latestFeature` and `allowPrerelease: true`. Done when: `dotnet --version` from the repo prints the RC. Cheaper substitute: no pin at all.
- [ ] Create `Directory.Build.props` with `TreatWarningsAsErrors`, `Nullable` and `ArtifactsPath` set to `build/dotnet`, so all .NET output sits under `build/` beside the C++ output, plus the `_wpftmp` fix from `D:\GitHub\Isotone\Directory.Build.props`. Done when: a deliberate warning fails the build, and a full build creates no `artifacts/` folder and no `bin/` or `obj/` folder under `src/` or `tests/`.
- [ ] Create `src/Bosun.Panel/Bosun.Panel.csproj` targeting `net11.0-windows`, with `UseWPF` and `InvariantGlobalization=false`, citing `R:\GitHub\Steward\src\Steward.Console\Steward.Console.csproj:25`. Done when: a templated binding runs in a test without throwing. Cheaper substitute: inheriting invariant globalization and crashing on the first binding.
- [ ] Create `src/Bosun.Core` and both test projects with xunit from `Directory.Packages.props`. Done when: `dotnet test` runs two passing tests.
- [ ] Commit: `dotnet: solution, props and empty projects`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~Foundation` exits 0, and fails if the change is reverted.

## 2. Add .NET to the gates

A section can only be held to tests that the one-command gate actually runs.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Extend `scripts/check-all.ps1`. Done when: it runs `dotnet test` through `bosun run`.
2. Extend `.gitignore`. Done when: `git status` is clean after a build.

- [ ] Add `dotnet build` and `dotnet test` to `scripts/check-all.ps1`. Done when: a failing test makes it exit nonzero. Cheaper substitute: running them outside the queue.
- [ ] Confirm `.gitignore` covers `build/`, `bin/`, `obj/`, `.vs/` and `*.user`, adding what is missing. Done when: `git status --short` is empty after a full build.
- [ ] Commit: `gates: run the .NET build and tests`

**Test checkpoint:** `pwsh scripts/check-all.ps1` exits 0, and exits nonzero with a failing test added.

## 3. Move the SDK pin to the .NET 11 release

.NET 11 is due on 2026-11-10. The RC's go-live licence covers development until then, and this section moves everything at once when the final release is out. The owner chose to start on the RC, so `D00 T03 §6` clears the Need when `D00 T03 §10` has installed the final SDK, and Release waits for it.

**Phase:** 0
**Needs:** third-party
**Surface:** no

**Build order:**

1. Install the final SDK and update `global.json` and package versions. Done when: no prerelease version remains in the repo.
2. Run the full gate. Done when: it passes on the final release.

- [ ] Set `global.json` to the final 11.0.1xx SDK with `allowPrerelease: false`. Done when: `dotnet --version` prints the final SDK. Cheaper substitute: leaving the RC pinned after release.
- [ ] Update prerelease versions in `Directory.Packages.props`. Done when: searching the repo for `rc.2` finds nothing outside dated notes.
- [ ] Confirm the release on the .NET download page. Done when: the page's latest version is recorded in the commit body.
- [ ] Commit: `dotnet: move to the .NET 11 release`

**Test checkpoint:** `pwsh scripts/check-all.ps1` exits 0 on the final SDK.

## Verification

- `dotnet test Bosun.slnx` passes.
- `python scripts/todo-graph.py validate` passes.
