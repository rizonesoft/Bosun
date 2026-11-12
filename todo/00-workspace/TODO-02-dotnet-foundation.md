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

**Corrected 2026-11-10:** every sibling repo is under `R:\GitHub` again, since the `D:` volume was retired; paths in open sections are updated, and stamped sections keep theirs as history.

## Inputs

- `R:\GitHub\Steward\Directory.Build.props` and `R:\GitHub\Isotone\Directory.Packages.props`.
- `docs/specs/2026-11-07-control-panel-design.md`, the technology section.
- -> XREF: D00 T01 §6 -- consumed by this file
- -> XREF: D02 T01 §1 -- consumes this file
- -> XREF: D02 T02 §3 -- consumes this file
- -> XREF: D07 T01 §1 -- consumes this file
- -> XREF: D00 T03 §10 -- consumed by this file
- -> XREF: D00 T03 §6 -- consumed by this file
- -> XREF: D00 T01 §12 -- consumed by this file

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
| 4 | §4 | .NET build defaults measured for memory and speed | §1, D00 T01 §12 | [ ] |

## 1. Solution, props and empty projects

Everything later hangs off this shape: one solution beside the C++ tools, with central package versions, warnings as errors, all output under `build/`, and tests that run through the queue.

**Corrected 2026-11-12:** checked before the build. The final SDK `11.0.100` is installed beside the RC (`dotnet --list-sdks`), so this section pins the release from the start, and `§3` (moving the pin off the RC) has nothing left to move: it is corrected to the checks that remain. The fix for WPF's temporary project, which Isotone carries, is written here for this repo, since the owner ruled on 2026-11-12 that Bosun takes nothing from its siblings. MSBuild node reuse stays off from the start, because it was measured holding memory on this machine; `§4` measures the other settings against it.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Create `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `Directory.Build.rsp` and `Bosun.slnx`. Done when: `dotnet restore Bosun.slnx` succeeds.
2. Create `src/Bosun.Core`, `src/Bosun.Panel`, `tests/Bosun.Core.Tests` and `tests/Bosun.Panel.Tests`. Done when: the solution builds with warnings as errors and the foundation tests pass.

- [ ] Create `global.json` pinning SDK `11.0.100` with `rollForward: latestFeature` and `allowPrerelease: false`. Done when: `dotnet --version` run in the repo prints `11.0.100` or a later 11.0 feature band, and never the RC. Cheaper substitute: no pin at all, so the RC can be chosen.
- [ ] Create `Directory.Build.props` with `TreatWarningsAsErrors`, `Nullable` enabled, `LangVersion` latest, `ArtifactsPath` set to `build/dotnet` so all .NET output sits under `build/` beside the C++ output, and the settings WPF's temporary markup project needs to build under `ArtifactsPath` and central package management. Done when: a deliberate warning fails the build, and a full build leaves no `artifacts/` folder and no `bin/` or `obj/` folder under `src/` or `tests/`.
- [ ] Create `Directory.Packages.props` with `ManagePackageVersionsCentrally` and the test packages (xunit v3, its Visual Studio runner, `Microsoft.NET.Test.Sdk`), each at a released version. Done when: no project names a package version itself, and `dotnet restore` succeeds.
- [ ] Create `src/Bosun.Panel/Bosun.Panel.csproj` targeting `net11.0-windows`, with `UseWPF` and `InvariantGlobalization` false, and an empty `App`. Done when: a test in `tests/Bosun.Panel.Tests` creates a WPF `TextBlock`, binds its `Text` to a value with a format on an STA thread, and reads the formatted text back without throwing. Cheaper substitute: inheriting invariant globalization, which throws on the first culture-dependent binding.
- [ ] Create `src/Bosun.Core/Bosun.Core.csproj` targeting `net11.0`, and both test projects, each with one foundation test (`FullyQualifiedName~Foundation`). Done when: `dotnet test Bosun.slnx --filter FullyQualifiedName~Foundation` runs and passes them.
- [ ] Create `Directory.Build.rsp` with `-nodeReuse:false` and `-maxCpuCount:4`. Measured 2026-11-10: another repo's build left 11 idle nodes holding 1.4 GB on this 32 GB machine while this repo's build waited 15 minutes for a slot. Done when: `tests/dotnet_nodes_test.ps1` builds the solution and finds no MSBuild or `dotnet` build node it started still alive 5 seconds after `dotnet build` returns, and fails with the file removed. Cheaper substitute: an environment variable only this machine sets.
- [ ] Keep `build.ps1` and the C++ build unaffected. Done when: `pwsh scripts/check-all.ps1` still passes, and the new `.csproj` files are not picked up by CMake or by `scripts/native-std-check.py`.
- [ ] Commit: `dotnet: solution, props and empty projects`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~Foundation` exits 0 and fails if the change is reverted; `pwsh tests/dotnet_nodes_test.ps1` exits 0.

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

.NET 11 is due on 2026-11-10. The RC's go-live licence covers development until then, and this section moves everything at once when the final release is out. The owner chose to start on the RC, so `D00 T03 §6` clears the Need when `D00 T03 §10` has installed the final SDK, and Release waits for it. **Corrected 2026-11-12:** 11.0.100 shipped on 2026-11-11 (`latest-sdk` in the 11.0 `releases.json`). The `Bosun dotnet final` task's installs failed on the in-use `dotnet.exe` until `install-dotnet-final.ps1` passed `-SkipNonVersionedFiles` (`c14114b`); once the elevated re-apply copies that fix into `C:\ProgramData\Bosun\bin` and the task runs, `dotnet --list-sdks` shows 11.0.100 and the Need clears. No project or `global.json` exists yet, so nothing built here runs on the RC.

**Corrected 2026-11-12 (while correcting `§1`):** `§1` pins the release `11.0.100` from the start, since it is installed, so there is no RC pin to move. What is left is confirming the release everywhere the SDK is chosen and that nothing prerelease crept in.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Confirm the release is what every build uses. Done when: no prerelease version remains in the repo, and CI builds on the release.

- [ ] Confirm `global.json` names the release with `allowPrerelease: false`, as `§1` set it. Done when: `dotnet --version` in the repo prints the release, and the CI job that `§2` adds reports the same SDK in its log.
- [ ] Search the repo for prerelease versions in `Directory.Packages.props`, `global.json` and the workflows. Done when: searching for `rc.`, `preview.` and `-alpha` finds nothing outside dated notes.
- [ ] Confirm the release on the .NET download page. Done when: the page's latest 11.0 SDK is recorded in the commit body.
- [ ] Commit: `dotnet: confirm the .NET 11 release everywhere`

**Test checkpoint:** `pwsh scripts/check-all.ps1` exits 0 on the release SDK.

> **Woken:** 2026-11-12: its `third-party` check in todo/wakes.json passed

## 4. .NET build defaults measured for memory and speed

Moved here on 2026-11-12 from `D00 T01 §12`, which measures the native build: the .NET 11 panel will be the heavy build, and its defaults can only be measured once `§1` has created `Bosun.slnx`. The owner asked for the build tools to be tuned for memory and speed on this machine; the rule is the same as there: measure first, keep only what wins.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Measure the .NET build as it stands. Done when: `docs/build-performance.md` holds a `dotnet-baseline` label.
2. Try each setting and keep the winners in `Directory.Build.props`. Done when: each is recorded as kept or dropped with its numbers.

- [ ] Add a `-Dotnet` switch to `scripts/measure-build.ps1` that times `dotnet build Bosun.slnx` (no-op, one touched `.cs` file, after `dotnet clean`) through `Measure-Job`. Done when: a run stores the label `dotnet-baseline` with wall seconds and peak MB for the three steps, and `tests/measure_build_test.ps1` covers the switch with a fake `dotnet`.
- [ ] Try `UseSharedCompilation` off in `Directory.Build.props`. Done when: it is measured under its own label and kept only if peak memory falls without the one-file build slowing by more than 10%.
- [ ] Try MSBuild node reuse off (`-nodeReuse:false` through `Directory.Build.rsp`). Done when: it is measured under its own label, with the count of `dotnet` and `VBCSCompiler` processes left running 60 seconds after the build recorded in the note, and kept only if it wins on memory left held without slowing the one-file build by more than 10%.
- [ ] Try a `-maxcpucount` limit in `Directory.Build.rsp`. Done when: the limits tried are each measured under their own label and the kept value, or none, is recorded with its numbers.
- [ ] Commit: `build: .NET build defaults measured, keeping what wins`

**Test checkpoint:** `pwsh tests/measure_build_test.ps1` exits 0, `pwsh scripts/measure-build.ps1 -Dotnet -Label check -Decision kept` writes the .NET rows to `docs/build-performance.md`, and `dotnet build Bosun.slnx` succeeds with warnings as errors under every kept setting.

## Verification

- `dotnet test Bosun.slnx` passes.
- `python scripts/todo-graph.py validate` passes.
