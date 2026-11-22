---
schema_version: 1
id: shell-follow-ups
domain: 02-shell
status: active
title: "TODO-05 -- Shell follow-ups"
---

# TODO-05 -- Shell follow-ups

> **Goal:** The panel's colours hold their contrast on every surface it really draws, its theme and icon builds say what they did and what failed, its icons read at every scale and to every eye, and its process runner holds no thread per idle call.

> **Current state:** Measured 2026-11-22 at `b4b2a9a` by `D00 T06 §2`. A data grid row now has a hover background (`B.Hover` on `IsMouseOver`, `src/Bosun.Panel/Theme/Controls.xaml`), but `ThemeContrast.Surfaces` in `tests/Bosun.Panel.Tests/ThemeContrast.cs` is still `Base`, `Surface` and `Raised`, and on `Hover` dark `TextMute` measured 4.33:1 and light `Accent` 4.27:1 (`D02 T01 §2`). `scripts/build-theme.py` prints nothing on success and stops at its first bad token; `scripts/build-icons.py` ends a missing master in a traceback; the tray's `needs` state is a plain disc; no icon is checked at scale or under colour-vision simulation; `ProcessRunner.RunAsync` in `src/Bosun.Core/ProcessRunner.cs` blocks three pool threads per call.

## Inputs

- `todo/findings.md`, base lines 46 and 64 to 75 except 72 and 73 (as of `b8c4e0b3`), struck with these sections' references by `D00 T06 §2`.
- `TODO-01-brand-and-theme.md`, which shipped the tokens, the theme build, the icons and the contrast test; `design/SPEC.md` and `design/tokens/colors.json`.

## Outcome

- `ThemeContrast` checks every text and background pair the panel draws, hover included, at 4.5:1 in both themes, and a failure names the token to change.
- `build-theme.py` and `build-icons.py` each end in one line naming what they wrote or what failed and what to do.
- The icons are compared at 100%, 125%, 150% and 200% on light and dark, and the runner badges are told apart under colour-vision simulation.

**Adjacency:** all=not-applicable (build tooling, tests and visual tokens: no user records)

**Adjacency rationale:** These sections refine the panel's look and its build tools; no record a user keeps is read or written.

## Implementation Order

| Order | Section | Deliverable | Depends On | Status |
| ----- | ------- | ----------- | ---------- | ------ |
| 1 | §1 | Contrast holds on hovered rows and every drawn pair, with the token named | -- | [ ] |
| 2 | §2 | The colour rule flagged in the editor as well as in the tests | -- | [ ] |
| 3 | §3 | Theme build says what it wrote and reports every bad token | -- | [ ] |
| 4 | §4 | Icon build failures in one line, and an attention cue on the tray | -- | [ ] |
| 5 | §5 | Icons checked at every scale and under colour-vision simulation | §4 | [ ] |
| 6 | §6 | Process runner measured against asynchronous pipes | -- | [ ] |

## 1. Contrast holds on hovered rows and every drawn pair, with the token named

Filed 2026-11-22 by `D00 T06 §2` from `todo/findings.md` (base lines 66, 68 and 69, measured in and filed by `D02 T01 §2`). Base line 66 is a defect now: a hovered data grid row draws caption text (`TextMute`) and accent text on `B.Hover`, which measured 4.33:1 (dark `TextMute`) and 4.27:1 (light `Accent`), under the 4.5:1 `design/SPEC.md` asks of text; `ThemeContrast.Surfaces` does not include `Hover`, so nothing catches it. A failure line names the colours and ratio but not the token to change.

**Phase:** 2
**Needs:** none
**Surface:** yes

**Build order:**

1. Catch it. Done when: the contrast test fails on today's tokens with `Hover` checked.
2. Fix it. Done when: every drawn pair passes at 4.5:1 in both themes, with the decided look kept.

- [ ] Add `Hover` to `ThemeContrast.Surfaces` in `tests/Bosun.Panel.Tests/ThemeContrast.cs`, and check the pairs each control template really draws (read from `src/Bosun.Panel/Theme/Controls.xaml`: hover, selected, disabled opacity and overlays), not only the fixed token grid (base lines 66 and 69). Done when: the test fails on today's `design/tokens/colors.json` naming dark `TextMute` and light `Accent` on `Hover`.
- [ ] Name the file and token path in each failure (`design/tokens/colors.json`, `dark.TextMute`) (base line 68). Done when: the failing run above prints both for each pair.
- [ ] Raise dark `TextMute` and light `Accent` in `design/tokens/colors.json` the least that passes 4.5:1 on every surface, rebuild with `python scripts/build-theme.py`, and capture the Queue and Memory pages in both themes before and after. Done when: the test passes, and the before and after renders are in `build/renders/` and attached to the review. Cheaper substitute: keeping caption and accent text off hovered rows, which changes what a row shows.
- [ ] Commit: `theme: contrast holds on hovered rows and every drawn pair (D02 T05 §1)`

**Test checkpoint:** `dotnet test tests/Bosun.Panel.Tests --filter ThemeContrast` exits 0, and fails with the two tokens restored.

**Fidelity:** `design/SPEC.md` (text contrast) and the panel's rendered Queue and Memory pages in both themes.
**Job:** the operator reads every caption and accent on a hovered row as easily as on a still one.
**Treatment:** the decided tokens moved the least that passes 4.5:1. Cheaper substitute that fails: hiding caption and accent text on hover.
**Chrome:** the generated theme from `design/tokens/`; no new control.

## 2. The colour rule flagged in the editor as well as in the tests

Filed 2026-11-22 by `D00 T06 §2` from `todo/findings.md` (base line 67, completion review of `D02 T01 §2`). `tests/Bosun.Panel.Tests/XamlRules.cs` refuses a raw colour in XAML only when the tests run.

**Phase:** 2
**Needs:** display-session
**Surface:** no

**Build order:**

1. Flag it while typing. Done when: a raw colour in a panel XAML file is flagged in the editor within a second.

- [ ] Write a Roslyn analyzer project, `src/Bosun.Analyzers`, that reads the panel's XAML as `AdditionalFiles` and reports the same rule `XamlRules.cs` holds, sharing its rule code so the two cannot disagree (base line 67). Done when: a test in `tests/Bosun.Panel.Tests` feeds it a XAML file with a raw `#RRGGBB` and reads one diagnostic at its line, and none on the panel's own files.
- [ ] Reference the analyzer from `src/Bosun.Panel/Bosun.Panel.csproj` so a build and the editor both report it. Done when: a build with a raw colour added to a XAML file fails with the diagnostic, and the time from typing to the squiggle in Visual Studio is recorded here. Cheaper substitute: a pre-commit grep.
- [ ] Commit: `panel: the colour rule as an analyzer, in the editor and the build (D02 T05 §2)`

**Test checkpoint:** `dotnet test tests/Bosun.Panel.Tests` exits 0, with the analyzer test failing when the rule is removed.

## 3. Theme build says what it wrote and reports every bad token

Filed 2026-11-22 by `D00 T06 §2` from `todo/findings.md` (base lines 64 and 65, completion review of `D02 T01 §1`). `scripts/build-theme.py` prints nothing on success (`main` ends with `return write_outputs(outputs)`) and raises at the first bad token in `validate`.

**Phase:** 2
**Needs:** none
**Surface:** no

**Build order:**

1. Say what was written. Done when: a success prints one line naming both files, and `--quiet` prints nothing.
2. Report every refusal. Done when: all independent bad tokens are reported together, each with its path, and nothing is written.

- [ ] Print one line naming both written files after `write_outputs` in `scripts/build-theme.py`, and add `--quiet` for automation, as Style Dictionary logs its outputs (base line 64). Done when: a test in `scripts/tests/` reads the line and its absence under `--quiet`.
- [ ] Collect every independent refusal in `validate` and report them together with each token's path, still writing nothing (base line 65). Done when: a test with three bad tokens reads all three and no output file. Cheaper substitute: reporting the first and a count.
- [ ] Commit: `theme: the build says what it wrote and reports every bad token (D02 T05 §3)`

**Test checkpoint:** `python scripts/run-tests.py test_build_theme` exits 0, with the three-token test failing against stop-at-first.

## 4. Icon build failures in one line, and an attention cue on the tray

Filed 2026-11-22 by `D00 T06 §2` from `todo/findings.md` (base lines 70 and 74, completion reviews of `D02 T01 §5` and `§3`). `main` in `scripts/build-icons.py` calls `build(args.masters)` outside any handler, so a missing master or a renderer error ends in a traceback; the tray's `needs` state (`TRAY_STATES`) is a plain blue disc that does not say "action needed" on its own, as OneDrive's attention mark does.

**Phase:** 2
**Needs:** none
**Surface:** yes

**Build order:**

1. Fail in one line. Done when: a missing master, a renderer error and a write failure each end in one line naming the path, the cause, that the old icon is kept, and what to do.
2. Add the cue. Done when: the `needs` icon carries an attention mark that reads at 16 and 20 px.

- [ ] Catch a missing master, a renderer error and a write failure in `main` of `scripts/build-icons.py`, each printing one line with the path, the cause, that the old icon is kept and the fix, with its own exit code (base line 70). Done when: a test in `scripts/tests/` drives all three and reads each line and the old icon unchanged.
- [ ] Draw an attention cue inside the `needs` disc (an exclamation mark, from `design/icon/`), keeping the decided disc, and render it at 16 and 20 px in both themes (base line 74). Done when: the frames are in `build/renders/`, the `needs` state is told apart from the others by the existing state test, and the cue is legible at 16 px in the render.
- [ ] Commit: `icons: build failures in one line, and an attention cue on the tray (D02 T05 §4)`

**Test checkpoint:** `python scripts/run-tests.py test_build_icons` exits 0, with the missing-master test failing against the unhandled `build` call.

**Fidelity:** `design/SPEC.md` (tray states) and the rendered tray icons at 16 and 20 px.
**Job:** the operator sees from the tray alone that the panel needs them, without hovering.
**Treatment:** the decided disc with an attention mark inside it. Cheaper substitute that fails: a different colour only.
**Chrome:** the tray icons from `scripts/build-icons.py`; no new tray control.

## 5. Icons checked at every scale and under colour-vision simulation

Filed 2026-11-22 by `D00 T06 §2` from `todo/findings.md` (base lines 46 and 71, completion reviews of `D00 T03 §20` and `D02 T01 §5`). `resources/bosun.ico` and the runner badges from `scripts/build-icons.py` are proved by their masters, not by how they look; nothing checks the badges are told apart at 100, 150 and 200% scaling, on light and dark backgrounds, or under colour-vision simulation.

**Phase:** 2
**Needs:** display-session
**Surface:** yes

**Build order:**

1. Capture. Done when: each icon is captured at 100%, 125%, 150% and 200% on light and dark backgrounds.
2. Compare. Done when: each capture is compared with an ImageMagick `icon:auto-resize` baseline and the badges pass a colour-vision check.

- [ ] Write `scripts/check-icons.py` to capture `resources/bosun.ico` and both runner badges at 100%, 125%, 150% and 200% scaling on a light and a dark background in a real display session, into `build/renders/icons/` (base line 71). Done when: the 16 captures are written with the commit they came from.
- [ ] Compare each capture with an ImageMagick `icon:auto-resize` baseline of the same master, and record the per-frame difference here (base line 71). Done when: each frame's difference is recorded, and a frame worse than the baseline is fixed or filed with its numbers.
- [ ] Simulate protanopia, deuteranopia and tritanopia on the badge captures and measure the colour difference between the two runners' badges (base line 46), keeping the decided design. Done when: the difference is recorded for each simulation and scale, and a pair below a ΔE of 20 is fixed or filed with its numbers.
- [ ] Commit: `icons: checked at every scale and under colour-vision simulation (D02 T05 §5)`

**Test checkpoint:** `python scripts/check-icons.py` exits 0 and writes its 16 captures and its table; it fails when a badge is swapped for the other runner's.

**Fidelity:** the rendered icons in `build/renders/icons/`.
**Job:** the operator tells the app and each runner's shortcut apart at any scale and with any colour vision.
**Treatment:** the decided icons, checked, not redrawn. Cheaper substitute that fails: checking the masters only.
**Chrome:** the icons from `scripts/build-icons.py`.

## 6. Process runner measured against asynchronous pipes

Filed 2026-11-22 by `D00 T06 §2` from `todo/findings.md` (base line 75, completion review of `D02 T02 §3`). `ProcessRunner.RunAsync` in `src/Bosun.Core/ProcessRunner.cs` starts three `Task.Run` blocking pipe tasks per call (`ReadCapped` for output and error, `WriteInput`), each holding a pool thread for the call's life.

**Phase:** 2
**Needs:** none
**Surface:** no

**Build order:**

1. Measure. Done when: concurrent reader calls are measured against CliWrap's asynchronous pipes.
2. Act on it. Done when: threads no longer grow with idle calls, or the measurement shows they do not matter.

- [ ] Benchmark 1, 10 and 50 concurrent `RunAsync` calls of an idle reader against CliWrap's asynchronous pipes: p95 time, worker threads, allocations and the panel dispatcher's responsiveness, and record the table here (base line 75). Done when: the table is written with its commit.
- [ ] When threads grow with idle calls, move `RunAsync` to overlapped (asynchronous) pipe reads and writes, keeping the job object containment and the output cap. Done when: the benchmark shows a flat worker-thread count, and the existing `ProcessRunner` tests pass, including the cap and containment ones. Cheaper substitute: raising the thread pool's minimum.
- [ ] Commit: `core: the process runner measured against asynchronous pipes (D02 T05 §6)`

**Test checkpoint:** `dotnet test tests/Bosun.Core.Tests --filter ProcessRunner` exits 0, and the benchmark table is in this section.

## Verification

- Every section's Test checkpoint passes on the stamped candidate, and `python scripts/todo-graph.py validate` passes.
- `dotnet test tests/Bosun.Panel.Tests --filter ThemeContrast` passes in both themes.
