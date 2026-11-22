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
| 7 | §7 | Runner badges and the tray cue recognised by people at a glance | §4, §5 | [ ] |
| 8 | §8 | Render catalog: review report, readable companions, progress, honest Shows | -- | [ ] |
| 9 | §9 | Controls measured, and motion through one helper | -- | [ ] |
| 10 | §10 | The crash notice copies a redacted report and names its buttons | -- | [ ] |
| 11 | §11 | Test host: a named crash, no error reports, kept evidence, timed smoke | -- | [ ] |
| 12 | §12 | Async commands say they are running and can be cancelled | -- | [ ] |
| 13 | §13 | The panel starts fast, answers a hung first launch, and opens at a page | -- | [ ] |
| 14 | §14 | Refresh says when and how it went, and a failure can be copied | -- | [ ] |
| 15 | §15 | Settings preview a theme, save off the UI thread, and keep a snapshot | -- | [ ] |
| 16 | §16 | The tray shows headroom and opens its menu beside the icon | -- | [ ] |
| 17 | §17 | Keyboard reach: the palette, toasts, results, placeholders and the Activity list | -- | [ ] |
| 18 | §18 | The desktop lock: compiled once, and inspectable from another terminal | -- | [ ] |
| 19 | §19 | The nightly smoke looks at the desk first, and its status points at each report | -- | [ ] |

## 1. Contrast holds on hovered rows and every drawn pair, with the token named

Filed 2026-11-22 by `D00 T06 §2` from `todo/findings.md` (base lines 66, 68 and 69, measured in and filed by `D02 T01 §2`). Base line 66 is a defect now: a hovered data grid row draws caption text (`TextMute`) and accent text on `B.Hover`, which measured 4.33:1 (dark `TextMute`) and 4.27:1 (light `Accent`), under the 4.5:1 `design/SPEC.md` asks of text; `ThemeContrast.Surfaces` does not include `Hover`, so nothing catches it. A failure line names the colours and ratio but not the token to change. Extended 2026-11-22 by `D00 T06 §4` (base lines 112, 113, 114 and 143).

**Phase:** 2
**Needs:** display-session
**Surface:** yes

**Build order:**

1. Catch it. Done when: the contrast test fails on today's tokens with `Hover` checked.
2. Fix it. Done when: every drawn pair passes at 4.5:1 in both themes, with the decided look kept.

- [ ] Add `Hover` to `ThemeContrast.Surfaces` in `tests/Bosun.Panel.Tests/ThemeContrast.cs`, and check the pairs each control template really draws (read from `src/Bosun.Panel/Theme/Controls.xaml`: hover, selected, disabled opacity and overlays), not only the fixed token grid (base lines 66 and 69). Done when: the test fails on today's `design/tokens/colors.json` naming dark `TextMute` and light `Accent` on `Hover`.
- [ ] Name the file and token path in each failure (`design/tokens/colors.json`, `dark.TextMute`) (base line 68). Done when: the failing run above prints both for each pair.
- [ ] Raise dark `TextMute` and light `Accent` in `design/tokens/colors.json` the least that passes 4.5:1 on every surface, rebuild with `python scripts/build-theme.py`, and capture the Queue and Memory pages in both themes before and after. Done when: the test passes, and the before and after renders are in `build/renders/` and attached to the review. Cheaper substitute: keeping caption and accent text off hovered rows, which changes what a row shows.
- [ ] Prove the contrast palette from `SystemColors` (`src/Bosun.Panel/Theme/HighContrastTheme.cs`, applied to `src/Bosun.Panel/Theme/Controls.xaml` live by `App.xaml.cs`) in all four Windows contrast themes, Aquatic and Dusk beside the Night sky and Desert that `tests/Bosun.Panel.Tests/Helpers/ContrastPalettes.cs` holds, switching live into each and restoring the chosen theme on leaving contrast (base line 80; filed by `D00 T06 §3`'s repair cycle, since `D02 T03 §10` proved two of the four). Done when: a test switches into each of the four and back and reads the system colours applied, then the chosen theme restored. Cheaper substitute: testing only the two themes `ContrastPalettes.cs` holds, or reading resource keys without switching live, which the Done when refuses (**corrected 2026-11-22, the completion review of D00 T06 §3**).
- [ ] Give every interactive control a hover the person can see in a contrast theme, list rows (`DataGridRow`, `NavRow`) and fields (`Field`) among them, as buttons already take the highlight pair, since `Hover` resolves to the window colour and `LineStrong` to the text colour (base line 112). In `src/Bosun.Panel/Theme/HighContrastTheme.cs` and `src/Bosun.Panel/Theme/Controls.xaml`. Done when: a test hovers each interactive style of `Controls.xaml` in Night sky and Desert and reads a brush different from the unhovered one, and golden images of the hovered states in both are in `build/renders/`. Cheaper substitute: changing the buttons only, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §4`**).
- [ ] Draw a dialog's edge (`src/Bosun.Panel/Shell/LeaveDialog.xaml`) and a drop-down's (`Drop` in `src/Bosun.Panel/Theme/Controls.xaml`) 2 px wide in a contrast theme, as Microsoft's guidance has for transient surfaces, checked at 100 and 200 percent scaling (base line 113). Done when: a test reads both edges at 2 px in a contrast theme and 1 px otherwise, and the renders at both scales are in `build/renders/`. Cheaper substitute: widening the dialog only, or checking one scale, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §4`**).
- [ ] Measure from Windows' contrast change to the repaint in `src/Bosun.Panel/Theme/ThemeService.cs` (`Refresh`), under 100 ms at the 95th percentile with focus and an unsaved edit kept, and stop reloading the Dark dictionary and rebuilding the contrast colours when nothing they hold changed (base line 114). Done when: the numbers are recorded here with their commit, and a test switches contrast with a field focused and edited and reads both kept. Cheaper substitute: timing the rebuild alone, without the repaint, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §4`**).
- [ ] Repeat the shown-window focus and selection checks of `tests/Bosun.Panel.E2E/ContrastTests.cs` with Desert (`hcwhite.theme`) and at 200 percent scaling, with no clipped label and no lost focus indicator (base line 143). Done when: the tests pass in Night sky and Desert at 100 and 200 percent and fail on a label clipped on purpose. Cheaper substitute: running Desert at 100 percent only, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §4`**).
- [ ] Commit: `theme: contrast holds on hovered rows and every drawn pair (D02 T05 §1)`

**Test checkpoint:** `dotnet test --project tests/Bosun.Panel.Tests --filter-class "*ThemeContrast*"` exits 0, and fails with the two tokens restored. (**corrected 2026-11-22, round 2 of `D00 T06 §4`:** written as VSTest arguments, which `global.json`'s Microsoft.Testing.Platform runner refuses) The shown-window contrast tests of `tests/Bosun.Panel.E2E/ContrastTests.cs` are real-input tests this command can't select, so `pwsh scripts/smoke-input.ps1` also exits 0, its log in `%LOCALAPPDATA%\Bosun\smoke` naming each `ContrastTests` case run in Night sky and Desert at 100 and 200 percent (**corrected 2026-11-22, round 3 of `D00 T06 §4`**, which also made the section need a display session).

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

**Test checkpoint:** `dotnet test --project tests/Bosun.Panel.Tests` exits 0, with the analyzer test failing when the rule is removed. (**corrected 2026-11-22, round 2 of `D00 T06 §4`:** written as VSTest arguments, which `global.json`'s Microsoft.Testing.Platform runner refuses)

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
- [ ] Draw an attention cue inside the `needs` disc (an exclamation mark, from `design/icon/`), keeping the decided disc, and render it at 16 and 20 px in both themes (base line 74). Done when: the frames are in `build/renders/`, the `needs` state is told apart from the others by the existing state test, and the cue is legible at 16 px in the render. Whether a first-time user reads it without hovering is `§7`'s.
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
- [ ] Simulate protanopia, deuteranopia and tritanopia on the badge captures and measure the colour difference between the two runners' badges (base line 46), keeping the decided design. Done when: the difference is recorded for each simulation and scale, and a pair below a ΔE of 20 is fixed or filed with its numbers. Whether a person tells them apart within two seconds is `§7`'s.
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

**Test checkpoint:** `dotnet test --project tests/Bosun.Core.Tests --filter-class "*ProcessRunner*"` exits 0, and the benchmark table is in this section. (**corrected 2026-11-22, round 2 of `D00 T06 §4`:** written as VSTest arguments, which `global.json`'s Microsoft.Testing.Platform runner refuses)

## 7. Runner badges and the tray cue recognised by people at a glance

Filed 2026-11-22 by the completion review of `D00 T06 §2` from `todo/findings.md` (base lines 46 and 74): the two targets only people can judge. `§5` measures the colour difference between the runner badges from `scripts/build-icons.py`, and `§4` draws the tray's attention cue, but neither says whether a person tells the runners apart within two seconds, or whether a first-time user reads the cue as "action needed" without hovering.

**Phase:** 2
**Needs:** display-session,attended
**Surface:** yes

**Build order:**

1. Time the runner identification. Done when: viewers name each runner's badge within two seconds at every scale, background and simulation.
2. Ask first-time users about the cue. Done when: first-time users say what the `needs` icon means without hovering.

- [ ] Show each runner badge capture from `§5` (100%, 150% and 200%, light and dark, each colour-vision simulation) for two seconds, in random order, to at least three viewers, and record how many name the runner correctly (base line 46). Done when: the results table is in this section, and any capture under three correct of three is fixed in `scripts/build-icons.py` and shown again.
- [ ] Show the tray's `needs` icon from `§4` at 16 and 20 px, in both themes, to at least three people who have not used Bosun, and ask what it asks of them, without hovering (base line 74). Done when: their answers are recorded here, and the cue is redrawn and asked again if fewer than three say it needs their action.
- [ ] Commit: `icons: runner badges and the tray cue recognised at a glance (D02 T05 §7)`

**Test checkpoint:** the results tables in this section show three correct of three for every capture and every icon size, and `python scripts/check-icons.py` still exits 0 on the final icons.

**Fidelity:** the captures from `§5` and the tray icons from `§4`.
**Job:** the operator knows which runner a shortcut starts, and that the tray needs them, at a glance.
**Treatment:** a timed showing with real viewers. Cheaper substitute that fails: a colour-difference number standing in for recognition.
**Chrome:** the icons from `scripts/build-icons.py`; nothing new drawn for the test.

## 8. Render catalog: review report, readable companions, progress, honest Shows

Filed 2026-11-22 by `D00 T06 §3` from `todo/findings.md` (base lines 76 to 79, completion reviews of `D02 T02 §9`). `WriteAll` in `tests/Bosun.Panel.Tests/Render/RenderCatalog.cs` writes only the PNGs and their JSON index, keeps every case's decoded pixels for a theme and reports nothing until it ends; `Shows` in `tests/Bosun.Panel.Tests/Render/PageRenderer.cs` (`Texts`) counts text that is transparent, clipped away or covered.

**Phase:** 2
**Needs:** none
**Surface:** no

**Build order:**

1. Make a review readable. Done when: one command writes a report pairing each changed case's pictures, and each case has a text companion.
2. Make the run honest and light. Done when: `Shows` ignores hidden text, and a run reports its progress and keeps only the pixels it needs.

- [ ] Have one command write a report pairing each changed case's previous and current picture, with links, advisory and tolerant of font differences, as Verify with DiffEngine pairs them (base line 76). In `tests/Bosun.Panel.Tests/Render/RenderCatalog.cs`. Done when: a test changes one case and reads the report naming that case alone with both pictures. Cheaper substitute: a report listing changed case names without the two pictures side by side, which the Done when refuses (**corrected 2026-11-22, the completion review of D00 T06 §3**).
- [ ] Write a readable companion beside each PNG (visible text, then the automation names in their order) from the texts the renderer already collects, as Verify.Xaml keeps XML beside its pictures (base line 77). In `tests/Bosun.Panel.Tests/Render/RenderCatalog.cs`. Done when: a test reads a case's companion and finds its texts and names in order, and a reordered control changes it. Cheaper substitute: a companion holding the automation names without the visible text, which the Done when refuses (**corrected 2026-11-22, the completion review of D00 T06 §3**).
- [ ] Have `Shows` refuse text that is fully transparent, fully clipped or fully covered (base line 78). In `tests/Bosun.Panel.Tests/Render/RenderCatalog.cs`. Done when: three fixtures, one of each, fail `Shows`, and a visible one still passes. Cheaper substitute: checking opacity only, so clipped and covered text still passes, which the Done when refuses (**corrected 2026-11-22, the completion review of D00 T06 §3**).
- [ ] Report completed of total and each case's time as the catalog runs, keep a case's comparison pixels only while needed, and measure time to first picture and peak memory against an equal Verify run (base line 79). In `tests/Bosun.Panel.Tests/Render/RenderCatalog.cs`. Done when: the run prints its progress, and the before and after numbers are recorded here. Cheaper substitute: one progress line printed when the catalog ends, which the Done when refuses (**corrected 2026-11-22, the completion review of D00 T06 §3**).
- [ ] Commit: `render: a review report, readable companions, progress and an honest Shows (D02 T05 §8)`

**Test checkpoint:** `dotnet test --project tests/Bosun.Panel.Tests --filter-class "*Render*"` exits 0, with the three `Shows` fixtures failing against today's check. (**corrected 2026-11-22, round 2 of `D00 T06 §4`:** written as VSTest arguments, which `global.json`'s Microsoft.Testing.Platform runner refuses)

## 9. Controls measured, and motion through one helper

Filed 2026-11-22 by `D00 T06 §3` from `todo/findings.md` (base lines 81 to 83, 107 and 108, completion reviews of `D02 T01 §4` and `D02 T03 §1`). `src/Bosun.Panel/Theme/Controls.xaml` has no measured responsiveness for `LogBox` and `DataGrid`, its `DataGridColumnHeader` sort and grips are proved on the style only, and its `ComboBox` drop-down does not fade with the other overlays; each consumer of `Changed` in `src/Bosun.Panel/Theme/Motion.cs` starts and cancels its own animation; and the animations of `todo/02-shell/TODO-03-premium-components.md` are not measured at 60 Hz. Extended 2026-11-22 by `D00 T06 §4` (base lines 125, 126, 129 to 132 and 134).

**Phase:** 2
**Needs:** display-session
**Surface:** yes

**Build order:**

1. Measure. Done when: the log, the table and the animations have recorded numbers against their targets.
2. Unify. Done when: every animation starts and finishes through one helper, the drop-down included.

- [ ] Time a 500-line log and a large process table against Fluent's controls on one machine, p95 interaction under 100 ms with virtualization kept (base line 81). In `src/Bosun.Panel/Theme/Controls.xaml` (`LogBox`, the `DataGrid` styles). Done when: the numbers are recorded here with their commit. Cheaper substitute: timing a short log, or with virtualization off, which the Done when refuses (**corrected 2026-11-22, the completion review of D00 T06 §3**).
- [ ] Sort a table by a header click twice and resize a column at 100%, 150% and 200% scaling, rendered in both themes (base line 82). In `src/Bosun.Panel/Theme/Controls.xaml` (`DataGridColumnHeader`). Done when: a test drives both and the renders are in `build/renders/`. Cheaper substitute: checking one scale or one theme, which the Done when refuses (**corrected 2026-11-22, the completion review of D00 T06 §3**).
- [ ] Give the `ComboBox` drop-down the gated fade of `Theme/Motion.cs`, matching the palette and toasts (base line 83). In `src/Bosun.Panel/Theme/Controls.xaml` (the `ComboBox` popup). Done when: a test reads the fade with motion on and none with it off. Cheaper substitute: a fixed fade that motion turned off does not stop, which the Done when refuses (**corrected 2026-11-22, the completion review of D00 T06 §3**).
- [ ] Give `Motion.cs` one start-and-finish helper that every consumer uses, so motion turned off leaves no animation clock running and no consumer repeats the cancelling (base line 107). Done when: a test turns motion off mid-animation and finds no clock left, and no consumer calls `BeginAnimation` itself. Cheaper substitute: a helper some consumers bypass, leaving a clock running with motion off, which the Done when refuses (**corrected 2026-11-22, the completion review of D00 T06 §3**).
- [ ] Measure the finished animations and shadows of `todo/02-shell/TODO-03-premium-components.md` at 60 Hz: the 95th percentile frame interval at or under 16.7 ms, no animation work on a hidden page, the 150 MB and 1% CPU idle budget kept, beside WinUI Composition's off-thread animations (base line 108). In `src/Bosun.Panel/Theme/Motion.cs` and its consumers. Done when: the numbers are recorded here with their commit. Cheaper substitute: measuring one animation, or with motion off, which the Done when refuses (**corrected 2026-11-22, the completion review of D00 T06 §3**).
- [ ] Widen `CountBadge` (`src/Bosun.Panel/Theme/Controls.xaml`) smoothly within `Motion.Normal` when its count crosses a digit boundary (9 to 10, 99 to 100), at once with reduced motion, without moving the row's title or clipping (base line 125). Done when: a test reads the width animated with motion on, at once with it off, and the title's position unchanged. Cheaper substitute: a fixed width wide enough for three digits, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §4`**).
- [ ] Measure `RailMarker.OnLayoutUpdated` (`src/Bosun.Panel/Theme/RailMarker.cs`) on every layout pass with the rail shown, holding selection feedback to one frame, the slide to its 220 ms, and the panel to its 150 MB and 1% idle budget (base line 126). Done when: the numbers are recorded here with their commit. Cheaper substitute: measuring a slide with the layout pass excluded, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §4`**).
- [ ] Give the card consuming `Sparkline` (`src/Bosun.Panel/Controls/Sparkline.cs`, `Plot` and `Say`) the change and its duration beside the line, so a 0.1 GB wobble and a 10 GB change read differently with no axis (base line 129). Done when: a test renders a 0.1 GB wobble and a 10 GB change and reads each one's change and duration in the words beside it, with no axis drawn  Cheaper substitute: an axis added to the line, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §4`**).
- [ ] Have `RadialGauge.SpokenName` (`src/Bosun.Panel/Controls/RadialGauge.cs`) say the total once, "7.4 GB free of 32 GB, Safe", with a regression test (base line 130). Done when: a test reads that name from the gauge and fails without the total. Cheaper substitute: the total in a tooltip only, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §4`**).
- [ ] Measure `Sparkline.OnRender` (`src/Bosun.Panel/Controls/Sparkline.cs`) at the real consumer's largest sample window, under 16 ms at p95, and the 150 MB and 1% idle budget with all three controls shown (base line 131). Done when: the numbers are recorded here with their commit. Cheaper substitute: a short sample window, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §4`**).
- [ ] Show exact MB beside the one-decimal summary of `QueueWords.Gb` (`src/Bosun.Panel/Controls/QueueSlotTile.cs`), so 6143 MB and a 6144 MB floor read differently with no navigation (base line 132). Done when: a test renders both and reads "6143 MB" and "6144 MB" beside their summaries  Cheaper substitute: a second decimal, which still rounds, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §4`**).
- [ ] Measure `WaitingRow.Read` (`src/Bosun.Panel/Controls/WaitingRow.cs`) on a realistic busy queue, document-to-display under 100 ms within the 150 MB and 1% CPU budget (base line 134). Done when: the numbers are recorded here with their commit and the queue's size. Cheaper substitute: an empty or one-job queue, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §4`**).
- [ ] Commit: `panel: controls measured, and motion through one helper (D02 T05 §9)`

**Test checkpoint:** `dotnet test --project tests/Bosun.Panel.Tests --filter-class "*Motion*" --filter-class "*Controls*" --filter-class "*CountBadge*" --filter-class "*RailMarker*"` (the badge and rail marker owning base lines 125 and 126  exits 0, and the measurements are in this section. (**corrected 2026-11-22, round 2 of `D00 T06 §4`:** written as VSTest arguments, which `global.json`'s Microsoft.Testing.Platform runner refuses)

**Fidelity:** `design/SPEC.md` (motion and tables) and the rendered table in both themes.
**Job:** the operator's tables and lists answer at once and move the same way everywhere.
**Treatment:** the decided controls, measured and given one motion path. Cheaper substitute that fails: a fade on the drop-down alone.
**Chrome:** the generated theme and `Theme/Motion.cs`; no new control.

## 10. The crash notice copies a redacted report and names its buttons

Filed 2026-11-22 by `D00 T06 §3` from `todo/findings.md` (base lines 84 and 86, completion review of `D02 T02 §1`). The crash notice in `src/Bosun.Panel/App.xaml.cs` gives a path and asks with Yes and No (`Ask`, `MessageBox`).

**Phase:** 2
**Needs:** display-session
**Surface:** yes

**Build order:**

1. Copy a report. Done when: one activation copies a redacted report, uploading nothing.
2. Name the choices. Done when: the notice asks with Restart Bosun Panel and Close.

- [ ] Add a Copy redacted report action needing one activation, uploading nothing, beside Open crash folder (base line 84). In `src/Bosun.Panel/Startup/CrashReports.cs` and `src/Bosun.Panel/Startup/CrashHandlers.cs`. Done when: a test triggers it and reads the clipboard text with the user folder and machine name redacted. Cheaper substitute: copying the report unredacted, or only the folder path, which the Done when refuses (**corrected 2026-11-22, the completion review of D00 T06 §3**).
- [ ] Ask with a task dialog whose buttons are Restart Bosun Panel and Close, each one keyboard activation, checked at 100%, 200% and in a contrast theme (base line 86). In `src/Bosun.Panel/Startup/CrashHandlers.cs`. Done when: a test reads both buttons and their results, and the renders are in `build/renders/`. Cheaper substitute: a message box with Yes and No, which the Done when refuses (**corrected 2026-11-22, the completion review of D00 T06 §3**).
- [ ] Commit: `panel: the crash notice copies a redacted report and names its buttons (D02 T05 §10)`

**Test checkpoint:** `dotnet test --project tests/Bosun.Panel.Tests --filter-class "*Crash*"` exits 0. (**corrected 2026-11-22, round 2 of `D00 T06 §4`:** written as VSTest arguments, which `global.json`'s Microsoft.Testing.Platform runner refuses)

**Fidelity:** `design/SPEC.md` (the crash notice) and its renders.
**Job:** the operator who saw the panel fail can hand a report over in one step, and knows what each button does.
**Treatment:** a task dialog with named actions. Cheaper substitute that fails: Yes and No with a longer sentence.
**Chrome:** the system task dialog; no custom window.

## 11. Test host: a named crash, no error reports, kept evidence, timed smoke

Filed 2026-11-22 by `D00 T06 §3` from `todo/findings.md` (base lines 89, 90, 104 and 105). One full test run in about 25 ended with a heap-corruption exit and no dump (`tests/Bosun.Panel.Tests`, 2026-11-15); each crash test leaves a Windows error report (`tests/Bosun.Panel.TestChild/Program.cs`, `crash-thread`); a failed smoke test deletes its own folder (`tests/Bosun.Panel.E2E/PanelProcess.cs`, `Dispose`); and the smoke's duration is unmeasured.

**Phase:** 2
**Needs:** none
**Surface:** no

**Build order:**

1. Keep the evidence. Done when: a crashing test host leaves a dump, and a failed smoke test keeps its folder.
2. Leave no noise and measure. Done when: the crash test leaves no error report, and the smoke's timings are recorded.

- [ ] Add `Microsoft.Testing.Extensions.CrashDump` to the test projects and pass `--crashdump` in `scripts/check-all.ps1` and `.github/workflows/build.yml`, keeping a CI run's dump as an artefact, then run the panel's tests in full ten times recording any crash, its dump and the free memory then (base line 89). Done when: a deliberately crashing test host leaves a dump through the local gate and through CI, where it is kept as an artefact, and the ten runs are recorded here, a crash named by its dump and stack or filed with them. Cheaper substitute: the flag set locally only, or a CI dump with no artefact kept, which the Done when refuses (**corrected 2026-11-22, the completion review of D00 T06 §3**).
- [ ] Have the test child opt out of Windows Error Reporting for itself (`SetErrorMode` with `SEM_NOGPFAULTERRORBOX`, or `WerSetFlags`) before it dies on purpose (base line 90). In `tests/Bosun.Panel.E2E/PanelProcess.cs`. Done when: a crash test run leaves no new Application log error or report, checked before and after. Cheaper substitute: turning Windows Error Reporting off for the whole machine, which the Done when refuses (**corrected 2026-11-22, the completion review of D00 T06 §3**).
- [ ] Keep the folder of a failed smoke test and print its path with the last assertion's context, with a capture of the panel's window only where `RealInput`'s checks allow it (base line 104). In `tests/Bosun.Panel.E2E/PanelProcess.cs`. Done when: a deliberately failed smoke test leaves its folder and prints its path; a passing one leaves nothing. Cheaper substitute: keeping the folder without printing its path, which the Done when refuses (**corrected 2026-11-22, the completion review of D00 T06 §3**).
- [ ] Measure the smoke's cold and warm duration and its three slowest tests over five runs, keeping one panel per test as it runs now (base line 105). In `tests/Bosun.Panel.E2E/PanelProcess.cs`. Done when: the numbers are recorded here with their commit and that each test had a panel of its own. Cheaper substitute: sharing one panel across tests to make the numbers smaller, which the Done when refuses (**corrected 2026-11-22, the completion review of D00 T06 §3**).
- [ ] Commit: `tests: the panel's host names a crash, leaves no error report, keeps a failed smoke's evidence (D02 T05 §11)`

**Test checkpoint:** `dotnet test --project tests/Bosun.Panel.Tests --filter-class "*Crash*"` exits 0 with no new Application log error, a deliberate host crash leaves its dump locally and as a CI artefact, and the runs and timings are in this section. (**corrected 2026-11-22, round 2 of `D00 T06 §4`:** written as VSTest arguments, which `global.json`'s Microsoft.Testing.Platform runner refuses)

## 12. Async commands say they are running and can be cancelled

Filed 2026-11-22 by `D00 T06 §3` from `todo/findings.md` (base lines 87 and 88, completion review of `D02 T02 §2`). `IsRunning` in `src/Bosun.Panel/Mvvm/AsyncCommand.cs` raises no change notification, and the command has no shared cancellation.

**Phase:** 2
**Needs:** none
**Surface:** no

**Build order:**

1. Notify. Done when: `IsRunning` raises `PropertyChanged` at its start and end.
2. Cancel. Done when: a cancellable command has a token and a Cancel command.

- [ ] Raise `PropertyChanged` for `IsRunning` at the start and the end, failure included, keeping the 150 ms loading delay of `design/SPEC.md` §19, as CommunityToolkit's `AsyncRelayCommand` does (base line 87). In `src/Bosun.Panel/Mvvm/AsyncCommand.cs`. Done when: a test binds to it and reads both changes, a failing action included. Cheaper substitute: raising it at the start only, so a failure leaves the command busy, which the Done when refuses (**corrected 2026-11-22, the completion review of D00 T06 §3**).
- [ ] Give a cancellable command a token and a Cancel command: one activation, immediate feedback, no failure shown for a cancel the person asked for, and a clean next run (base line 88). In `src/Bosun.Panel/Mvvm/AsyncCommand.cs`. Done when: a test cancels a running action and reads no failure and a clean second run. Cheaper substitute: a Cancel that hides the busy state while the work runs on, which the Done when refuses (**corrected 2026-11-22, the completion review of D00 T06 §3**).
- [ ] Commit: `panel: async commands say they are running and can be cancelled (D02 T05 §12)`

**Test checkpoint:** `dotnet test --project tests/Bosun.Panel.Tests --filter-class "*AsyncCommand*"` exits 0. (**corrected 2026-11-22, round 2 of `D00 T06 §4`:** written as VSTest arguments, which `global.json`'s Microsoft.Testing.Platform runner refuses)

## 13. The panel starts fast, answers a hung first launch, and opens at a page

Filed 2026-11-22 by `D00 T06 §3` from `todo/findings.md` (base lines 85, 91 and 92, completion reviews of `D02 T02 §1` and `§3`). A second launch waits up to two five-second tries with nothing on screen when the first panel is hung (`src/Bosun.Panel/Startup/SingleInstance.cs`, `Start`); there is no way to open the panel at a page from outside (`src/Bosun.Panel/Startup/PanelStartup.cs`); and nothing measures start or navigation (`src/Bosun.Panel/Shell/ShellView.xaml`).

**Phase:** 2
**Needs:** display-session
**Surface:** yes

**Build order:**

1. Answer a hung launch. Done when: waiting feedback shows within 500 ms.
2. Open at a page. Done when: a page key on the command line or the pipe opens the panel there.
3. Measure. Done when: warm activation, cold start and page switch have recorded numbers.

- [ ] Show waiting feedback within 500 ms on the hung path and measure warm activation, target p95 under 250 ms against PowerToys on one machine, keeping the acknowledgement (base line 85). In `src/Bosun.Panel/Startup/SingleInstance.cs`. Done when: a test with a first panel that does not answer reads the feedback within 500 ms, and the warm numbers are recorded here. Cheaper substitute: feedback shown only once the window appears, which the Done when refuses (**corrected 2026-11-22, the completion review of D00 T06 §3**).
- [ ] Take a page key on the command line and over the activation pipe (`activate queue`), opening or bringing the panel forward at that page through `ShellViewModel.GoToAsync`, as PowerToys' settings links do (base line 91). In `src/Bosun.Panel/Startup/SingleInstance.cs`, `src/Bosun.Panel/Startup/PanelStartup.cs` and `src/Bosun.Panel/Shell/ShellViewModel.cs`. Done when: a test launches with `queue` and reads the Queue page shown, and a second launch with `memory` moves the running panel there. Cheaper substitute: accepting the key at first start only, not over the pipe, which the Done when refuses (**corrected 2026-11-22, the completion review of D00 T06 §3**).
- [ ] Measure a cold start to an interactive shell (target under 1 s at p95) and a page switch with 500 lines in the Activity pane (under 100 ms at p95) against PowerToys on one machine, within `D07 T01 §5`'s budget (base line 92). In `src/Bosun.Panel/Shell/ShellView.xaml`. Done when: the numbers are recorded here with their commit. Cheaper substitute: a warm start measured as the cold one, which the Done when refuses (**corrected 2026-11-22, the completion review of D00 T06 §3**).
- [ ] Commit: `panel: starts fast, answers a hung first launch, and opens at a page (D02 T05 §13)`

**Test checkpoint:** `dotnet test --project tests/Bosun.Panel.Tests --filter-class "*StartupTests*"` exits 0, and the measurements are in this section. (**corrected 2026-11-22, round 2 of `D00 T06 §4`:** written as VSTest arguments, which `global.json`'s Microsoft.Testing.Platform runner refuses)

**Fidelity:** `design/SPEC.md` §1 (the shell) and the waiting feedback's render.
**Job:** the operator who clicks twice, or follows a link to a page, is never left with nothing on screen.
**Treatment:** the decided shell, with a waiting notice and page keys. Cheaper substitute that fails: a longer single-instance timeout.
**Chrome:** the shell's own toast and pages; no new window.

## 14. Refresh says when and how it went, and a failure can be copied

Filed 2026-11-22 by `D00 T06 §3` from `todo/findings.md` (base lines 93, 101, 102, 109 and 110, completion reviews of `D02 T02 §4`, `§8` and `D02 T03 §7`). A failure's message and a log line can't be copied (`src/Bosun.Panel/Shell/PageFrame.xaml`, the Activity pane); `src/Bosun.Panel/Shell/DispatcherRefreshDriver.cs` (`Every`) ticks once a second, adding up to a second; a failed page says when it tries again only in the first Activity line (`src/Bosun.Panel/Shell/PageBase.cs`, `RefreshEvery`); a Refresh the person asked for shows nothing until it ends (`PageFrame.xaml`); and a slow first load never says it has loaded (`src/Bosun.Panel/Controls/Skeleton.cs`). Extended 2026-11-22 by `D00 T06 §4` (base line 133).

**Phase:** 2
**Needs:** display-session
**Surface:** yes

**Build order:**

1. Time it right. Done when: a refresh runs within 100 ms of being due.
2. Say it. Done when: a page shows when it last refreshed and next tries, a manual Refresh says "Refreshing", and a slow load says it loaded.
3. Copy it. Done when: one activation copies a whole diagnostic.

- [ ] Add a Copy action, one activation and reachable by keyboard, to a failure's message and to an Activity line, copying the whole diagnostic and uploading nothing (base line 93). In `src/Bosun.Panel/Shell/PageFrame.xaml` and `src/Bosun.Panel/Shell/ActivityLog.cs`. Done when: a test copies each and reads the clipboard. Cheaper substitute: copying only the visible first line, which the Done when refuses (**corrected 2026-11-22, the completion review of D00 T06 §3**).
- [ ] Set `DispatcherRefreshDriver`'s timer to the earliest due time instead of a one-second tick, for under 100 ms of added latency with no more wake-ups, keeping the intervals and no ticking while nothing is active (base line 101). In `src/Bosun.Panel/Shell/DispatcherRefreshDriver.cs`. Done when: a test reads a refresh within 100 ms of due, the same wake-up count, and no timer while nothing is active. Cheaper substitute: a shorter fixed tick, which wakes more often, which the Done when refuses (**corrected 2026-11-22, the completion review of D00 T06 §3**).
- [ ] Show on a failed page when it last refreshed and when the next try is, without announcing each change to a screen reader (base line 102). In `src/Bosun.Panel/Shell/PageFrame.xaml` and `src/Bosun.Panel/Shell/PageBase.cs`. Done when: a test reads both times on the page and no live-region change per tick. Cheaper substitute: a live region that announces each change, which the Done when refuses (**corrected 2026-11-22, the completion review of D00 T06 §3**).
- [ ] After 150 ms of a Refresh the person asked for, say "Refreshing" beside the button without a modal, clear it at the end and announce the result once, never for a routine poll (base line 109). In `src/Bosun.Panel/Shell/PageBase.cs`. Done when: a test reads the word after 150 ms, gone after, one announcement, and none for a poll. Cheaper substitute: a modal, or the word shown at once, which the Done when refuses (**corrected 2026-11-22, the completion review of D00 T06 §3**).
- [ ] Say once, politely and without moving focus, that a page has loaded, only for a load that showed the skeleton, checked with Narrator or NVDA (base line 110). In `src/Bosun.Panel/Shell/PageBase.cs`. Done when: a test reads one polite announcement after a slow load and none after a fast one, and the screen reader check is recorded here. Cheaper substitute: announcing every refresh, or moving focus, which the Done when refuses (**corrected 2026-11-22, the completion review of D00 T06 §3**).
- [ ] Give every queue job line one keyboard-reachable copy action that copies the whole command exactly: a waiting job (`WaitingLineView` in `src/Bosun.Panel/Theme/Controls.xaml`, filled by `src/Bosun.Panel/Controls/WaitingRow.cs`), a running job's slot tile (`src/Bosun.Panel/Controls/QueueSlotTile.cs`) and a finished one (the Command column of `src/Bosun.Panel/Queue/QueueView.xaml`) (base line 133). Done when: a test copies the command of a job while it waits, while it runs and after it finishes, each reached by keyboard on a command holding quotes and spaces, and reads the clipboard equal to the command each time. Cheaper substitute: copying from the finished jobs only, or the visible, trimmed text, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §4`**, its targets widened by round 5).
- [ ] Commit: `panel: refresh says when and how it went, and a failure can be copied (D02 T05 §14)`

**Test checkpoint:** `dotnet test --project tests/Bosun.Panel.Tests --filter-class "*Refresh*" --filter-class "*PageFrame*" --filter-class "*Skeleton*" --filter-class "*QueuePage*"` (the queue copy of base line 133  exits 0. (**corrected 2026-11-22, round 2 of `D00 T06 §4`:** written as VSTest arguments, which `global.json`'s Microsoft.Testing.Platform runner refuses)

**Fidelity:** `design/SPEC.md` §19 (loading and refresh) and the page frame's renders.
**Job:** the operator knows a page is current, when it next tries, and can hand a failure over in one step.
**Treatment:** quiet status text and one polite announcement. Cheaper substitute that fails: a spinner with no words.
**Chrome:** `Shell/PageFrame.xaml` and the Activity pane; no new control.

## 15. Settings preview a theme, save off the UI thread, and keep a snapshot

Filed 2026-11-22 by `D00 T06 §3` from `todo/findings.md` (base lines 94 to 96 and 100, completion reviews of `D02 T02 §5` and `§7`). A damaged `panel.json` falls back to the defaults (`src/Bosun.Core/PanelSettingsStore.cs`); a theme is seen only after Save (`src/Bosun.Panel/Settings/SettingsPage.cs`, `SelectedTheme`); `SaveAsync` writes, flushes, reads back and calls the registry on the UI thread; and the tray's first-close flag is saved on it too (`src/Bosun.Panel/Tray/TrayController.cs`, `NoticeWasShown`). Extended 2026-11-22 by `D00 T06 §4` (base lines 119, 127, 128 and 142). Extended 2026-11-22 by `D00 T06 §5` (base lines 162 and 163).

**Phase:** 2
**Needs:** display-session
**Surface:** yes

**Build order:**

1. Keep a snapshot. Done when: a damaged file offers to restore the last good one.
2. Preview and save off the UI thread. Done when: a theme previews at once and saves never block the window.

- [ ] Keep one validated previous snapshot (`panel.json.last`) and offer to restore it in one action, saying exactly what was recovered, as PowerToys restores from a backup (base line 94). In `src/Bosun.Core/PanelSettingsStore.cs`. Done when: a test damages `panel.json` and restores from the snapshot, reading back each value. Cheaper substitute: restoring the last file without validating it, which the Done when refuses (**corrected 2026-11-22, the completion review of D00 T06 §3**).
- [ ] Preview a chosen theme at once (under 100 ms), Save committing it and Discard restoring the saved theme, keeping the segmented control of `D02 T03 §4` (base line 95). In `src/Bosun.Panel/Settings/SettingsPage.cs`. Done when: a test chooses a theme, reads it applied, discards, and reads the saved one back. Cheaper substitute: applying only on Save, or Discard leaving the preview applied, which the Done when refuses (**corrected 2026-11-22, the completion review of D00 T06 §3**).
- [ ] Move `SaveAsync`'s write, flush, read-back and registry call off the UI thread, with feedback within 100 ms, a busy state after 150 ms, and the keyboard and repaint answering through a one-second injected delay keeping the atomic save and its read-back (base lines 96 and 127, which repeat each other). In `src/Bosun.Panel/Settings/SettingsPage.cs` and `src/Bosun.Core/PanelSettingsStore.cs`. Done when: a test injects the delay and reads the window answering and the busy state. Cheaper substitute: moving the write off the UI thread while the registry call stays on it, which the Done when refuses (**corrected 2026-11-22, the completion review of D00 T06 §3**).
- [ ] Save the tray's first-close flag off the UI thread with the Settings page's saves, and measure tray Open and the menu under 100 ms at p95 in `D07 T01`'s budget run (base line 100). In `src/Bosun.Panel/Tray/TrayController.cs`. Done when: a test reads the flag saved with the UI thread free, and the numbers are recorded here. Cheaper substitute: a save started and never awaited, losing its error, which the Done when refuses (**corrected 2026-11-22, the completion review of D00 T06 §3**).
- [ ] Say under the pressure threshold field of `src/Bosun.Panel/Settings/SettingsView.xaml`, not only in its tooltip, that it is Windows' available memory, how often it is checked (read from the interval `src/Bosun.Panel/Tray/PressureCheck.cs` uses, so the text stays true when base line 118's item brings detection to 2 s), with a 1 GB way back, checked at the window's least size and at 200 percent scaling (base line 119). Done when: a test reads the three facts in visible text under the field, changes the check's interval and reads the text follow it, and the renders at both sizes show it unclipped  Cheaper substitute: the text in the tooltip only, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §4`**).
- [ ] Give the theme choice in `src/Bosun.Panel/Settings/SettingsView.xaml` an access key, so one documented chord puts the keyboard on the chosen theme and one arrow moves it (base line 128). Done when: a test presses the chord, reads focus on the chosen option, presses an arrow and reads the next one chosen. Cheaper substitute: a chord that focuses the first option, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §4`**).
- [ ] Add an optional in-app contrast preview selector to `src/Bosun.Panel/Settings/SettingsView.xaml`, two actions to switch into a contrast theme and one to restore, leaving the desktop's theme untouched, beside the start-time `BOSUN_PANEL_CONTRAST` of `src/Bosun.Panel/Theme/ContrastPreview.cs` (base line 142). Done when: a test switches in two actions, reads the contrast colours applied, restores in one, and reads the Windows theme unchanged. Cheaper substitute: a setting that needs a restart, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §4`**).
- [ ] Step the pressure threshold of `src/Bosun.Panel/Settings/SettingsView.xaml` with Up and Down by a sensible increment within its 512 to 65536 MB range, as a NumberBox does, keeping explicit Save (base line 162). Done when: `ThresholdTests` presses Up and Down, reads the value stepped and held at both ends, and reads nothing saved before Save. Cheaper substitute: a step that saves at once, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §5`**).
- [ ] Check the whole Settings form of `src/Bosun.Panel/Settings/SettingsView.xaml` at the least window size (960 x 600) and at 200% scaling with a long refusal: nothing clipped, its keyboard order and heading navigation checked (base line 163). Done when: `SettingsLayoutTests` renders both with a long refusal and reads no clipped element, the tab order and the headings in order. Cheaper substitute: the heading ranks alone, as now, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §5`**).
- [ ] Commit: `panel: settings preview a theme, save off the UI thread, and keep a snapshot (D02 T05 §15)`

**Test checkpoint:** `dotnet test --project tests/Bosun.Panel.Tests --filter-class "*Settings*" --filter-class "*Tray*"` and `dotnet test --project tests/Bosun.Core.Tests --filter-class "*PanelSettingsStore*"` exit 0. (**corrected 2026-11-22, round 2 of `D00 T06 §4`:** written as VSTest arguments, which `global.json`'s Microsoft.Testing.Platform runner refuses)

**Fidelity:** `design/SPEC.md` (the Settings page) and its renders in both themes.
**Job:** the operator tries a theme without committing to it, and never loses settings to a damaged file.
**Treatment:** a live preview with Save and Discard. Cheaper substitute that fails: saving on every choice.
**Chrome:** the Settings page's own controls; no new dialog.

## 16. The tray shows headroom and opens its menu beside the icon

Filed 2026-11-22 by `D00 T06 §3` from `todo/findings.md` (base lines 97 and 99, completion review of `D02 T02 §7`). The healthy tray says only that it is normal (`src/Bosun.Panel/Tray/TrayViewModel.cs`, `Tooltip`), and a menu opened from the keyboard appears at the mouse pointer (`src/Bosun.Panel/Tray/TrayShell.cs`, `TrackMenu`). Extended 2026-11-22 by `D00 T06 §4` (base lines 118 and 120).

**Phase:** 2
**Needs:** display-session
**Surface:** yes

**Build order:**

1. Show headroom. Done when: the tooltip gives memory free and the sample's age.
2. Anchor the menu. Done when: a keyboard-opened menu appears beside the icon.

- [ ] Add the memory free and the sample's age to the healthy tooltip, from the pressure sample `D03 T01 §4` already takes, with no second poller (base line 97). In `src/Bosun.Panel/Tray/TrayViewModel.cs` and `src/Bosun.Panel/Tray/PressureCheck.cs`. Done when: a test reads both in the tooltip and one poller. Cheaper substitute: a second timer polling memory, which the Done when refuses (**corrected 2026-11-22, the completion review of D00 T06 §3**).
- [ ] Anchor a menu opened from the keyboard beside the icon, from the x and y Windows sends in `wParam` at version 4, checked at 100, 150 and 200 percent scaling (base line 99). In `src/Bosun.Panel/Tray/NativeTrayIcon.cs`. Done when: a test sends a keyboard open with a pointer on another monitor and reads the menu at the icon's position. Cheaper substitute: anchoring at the cursor, which the Done when refuses (**corrected 2026-11-22, the completion review of D00 T06 §3**).
- [ ] Measure from a sustained fall under the threshold to the notification in `src/Bosun.Panel/Tray/PressureCheck.cs` under a build workload, now up to 60 s (the logger's 30 s sample in `src/app.cpp` and `wsl/bosun_log.py`, then the check's own 30 s), and bring it to 2 s at most in the owning logger, keeping one notice per episode and the idle budget (base line 118). Done when: a test drops available memory just after a sample and reads the notification within 2 s of the crossing, one notice for the episode, and the measured crossing-to-notification under a build and the idle CPU are recorded here. Cheaper substitute: a test that starts from a low snapshot, skipping the wait for the next sample, or a timer polling every second that breaks the idle budget, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §4`**).
- [ ] Take a pressure notification's click to the Memory page in one step in `src/Bosun.Panel/Tray/TrayController.cs` (`Open`), keeping the unsaved-edits question, as `OpenQueueAsync` does for the Queue (base line 120). Done when: a test clicks the notification from another page and reads Memory shown, and with an unsaved edit reads the question first. Cheaper substitute: opening the panel without changing page, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §4`**).
- [ ] Commit: `tray: headroom in the tooltip, and the menu beside the icon (D02 T05 §16)`

**Test checkpoint:** `dotnet test --project tests/Bosun.Panel.Tests --filter-class "*Tray*"` exits 0. (**corrected 2026-11-22, round 2 of `D00 T06 §4`:** written as VSTest arguments, which `global.json`'s Microsoft.Testing.Platform runner refuses)

**Fidelity:** `design/SPEC.md` (the tray) and the menu's position at each scale.
**Job:** the operator sees memory headroom in one hover and finds the menu where the keyboard opened it.
**Treatment:** a fuller tooltip and an anchored menu. Cheaper substitute that fails: a tooltip that polls on its own.
**Chrome:** the tray's own menu and tooltip; no new window.

## 17. Keyboard reach: the palette, toasts, results, placeholders and the Activity list

Filed 2026-11-22 by `D00 T06 §4` from `todo/findings.md` (base lines 135 to 141, completion reviews of `D02 T03 §8` and `§9`). The palette matches a query as one substring (`src/Bosun.Core/CommandIndex.cs`, `MatchOf`), opens with no memory of the last action (`src/Bosun.Panel/Shell/CommandPalette.cs`, `Open`) and shows no keys (`PaletteRow` in `src/Bosun.Panel/Theme/Controls.xaml`); a toast can't be reached by a shortcut (`src/Bosun.Panel/Shell/ShellView.xaml.cs`, `OnShortcut`), links to no Activity line or Retry (`src/Bosun.Panel/Shell/ToastCard.cs`), and its overflow count clears on any dismiss (`src/Bosun.Panel/Shell/ToastHost.cs`, `Dismiss`). Extended 2026-11-22 by `D00 T06 §5` (base lines 167, 168, 171 and 172).

**Phase:** 2
**Needs:** display-session
**Surface:** yes

**Build order:**

1. Find by word. Done when: an ordered word-prefix query finds its command, ranked as now.
2. Repeat and show keys. Done when: the last action opens first and each row shows its key.
3. Reach toasts. Done when: a shortcut reaches the newest toast, and it leads to its result, its line and its Retry.

- [ ] Match each query word, in order, to a later word of the name in `src/Bosun.Core/CommandIndex.cs` (`MatchOf`), as that word's prefix or as an abbreviation of it (its letters in order, starting with its first: "sv" for "Save"), so "sv set" finds Save settings (`src/Bosun.Panel/Settings/SettingsPage.cs:234`); rank prefix, then word start, then a word-by-word match, then inside, under 50 ms at the panel's command count (base line 135)  Cheaper substitute: matching each word anywhere, which reorders the ranking, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §4`**).
- [ ] Put the last action run first when `CommandPalette.Open` (`src/Bosun.Panel/Shell/CommandPalette.cs`) opens, if it is still offered, so repeating it is Ctrl+K then Enter with its confirmation kept (base line 136). Done when: a test runs an action, reopens, presses Enter and reads it run again, its confirmation asked, and a withdrawn action not shown first. Cheaper substitute: remembering a name that is no longer offered, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §4`**).
- [ ] Show a command's own key beside it in `PaletteRow` (`src/Bosun.Panel/Theme/Controls.xaml`; F5 for Refresh) and a compact Enter and Esc hint, checked in all three themes (base line 137). Done when: a test reads F5 on the Refresh row and the hint, and the renders in Dark, Light and contrast show both. Cheaper substitute: a hint in one theme only, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §4`**).
- [ ] Add one shortcut in `src/Bosun.Panel/Shell/ShellView.xaml.cs` (`OnShortcut`) that puts the keyboard on the newest toast, one key that dismisses it, and the keyboard back where it was (base line 138). Done when: a test presses the shortcut, reads focus on the newest toast, dismisses it by key, and reads focus where it was. Cheaper substitute: a shortcut that focuses the toast area only, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §4`**).
- [ ] Give a toast in `src/Bosun.Panel/Shell/ToastCard.cs` one keyboard action that shows its whole result or selects its line in the Activity pane (base line 139). Done when: a test presses it on a cut toast and reads the whole text, and on a logged one reads its Activity line selected. Cheaper substitute: the Show more button reached by Tab, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §4`**).
- [ ] Keep the count of results not shown in `src/Bosun.Panel/Shell/ToastHost.cs` (`Dismiss`) until the Activity pane is visited, with one action to reach them, instead of clearing it on any dismiss (base line 140). Done when: a test overflows, dismisses one, reads the count kept, takes the action and reads the pane shown and the count cleared. Cheaper substitute: clearing the count when the window hides, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §4`**).
- [ ] Reach the owning page's safe Retry, or the setting at fault, from a failed toast in `src/Bosun.Panel/Shell/ToastCard.cs` in one press, its confirmation kept (base line 141). Done when: a test fails an action, presses the toast's action and reads the retry run with its confirmation asked, or the setting focused. Cheaper substitute: a Retry that skips the confirmation, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §4`**).
- [ ] Give a toast's whole message a reading surface in `src/Bosun.Panel/Shell/ToastMessage.cs` whose text is selectable and read by character and line, keeping the expansion hold, as VS Code's Accessible View does for notifications, verified with Narrator or NVDA to the last character (base line 167). Done when: a test reads the text selectable, with a caret moving by character and by line to the last character, and the screen reader check is recorded here. Cheaper substitute: a scroll area of plain text, as now, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §5`**).
- [ ] Add a notification-reading command in `src/Bosun.Panel/Shell/ShellView.xaml.cs` that reaches the latest full result from any page in at most two keys, focus returned on the way out (base line 168). Done when: a test on each page presses the keys, reads the latest whole result focused, leaves, and reads focus where it was. Cheaper substitute: the newest toast only, gone once dismissed, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §5`**).
- [ ] Name one built page in each placeholder of `src/Bosun.Panel/Shell/PlaceholderPage.cs` as a keyboard-reachable link, so the next step is one activation with no visit to another unfinished page (base line 171). Done when: `ShellTests` reads the link on the Optimise and About placeholders, activates it by keyboard, and reads a built page shown. Cheaper substitute: the fixed sentence, as now, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §5`**).
- [ ] Add one shortcut in `src/Bosun.Panel/Shell/ShellView.xaml.cs` that, from any page, puts the keyboard in the Activity list with its reading position kept, as VS Code's Ctrl+Shift+U does for Output (base line 172). Done when: `ShellTests` presses it on each page and reads focus in the Activity list at the kept anchor. Cheaper substitute: focus at the top of the list, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §5`**).
- [ ] Commit: `panel: the palette and toasts by keyboard, and a palette that finds by word (D02 T05 §17)`

**Test checkpoint:** `dotnet test --project tests/Bosun.Core.Tests --filter-class "*CommandIndex*"` and `dotnet test --project tests/Bosun.Panel.Tests --filter-class "*CommandPalette*" --filter-class "*Toast*"` exit 0. (**corrected 2026-11-22, round 2 of `D00 T06 §4`:** written as VSTest arguments, which `global.json`'s Microsoft.Testing.Platform runner refuses) The shell's tests are selected too: `dotnet test --project tests/Bosun.Panel.Tests --filter-class "*ShellTests*"` exits 0 (**corrected 2026-11-22 by `D00 T06 §5`**, for base lines 171 and 172).

**Fidelity:** `design/SPEC.md` (the command palette and the toasts) and their renders in the three themes.
**Job:** the operator repeats, finds and follows up an action without the mouse.
**Treatment:** word-prefix search, a remembered last action, visible keys and keyboard-reachable toasts. Cheaper substitute that fails: a toast reachable only by Tab.
**Chrome:** the palette and the toast host; no new window.
## 18. The desktop lock: compiled once, and inspectable from another terminal

Filed 2026-11-22 by `D00 T06 §5` from `todo/findings.md` (base lines 165 and 166, the completion review of `D02 T02 §11`). The desktop lock compiles its source on every capture (`scripts/capture-window.ps1`), and nothing reports who holds or waits for it (`scripts/lib/DesktopLock.cs`). Split 2026-11-22 from one section by round 2 of `D00 T06 §5`, one script each.

**Phase:** 2
**Needs:** none
**Surface:** no

**Build order:**

1. Cache the `Add-Type` of `scripts/capture-window.ps1`. Done when: `tests/capture_window_test.ps1` reads the compiled lock reused until its source changes, within 100 ms at p95. (**corrected 2026-11-22, the completion review of `D00 T06 §5`**)
2. Inspect `scripts/lib/DesktopLock.cs` with one read-only command. Done when: `DesktopLockTests` reads the holder and the waiters from it. (**corrected 2026-11-22, the completion review of `D00 T06 §5`**)

- [ ] Cache the desktop lock that `scripts/capture-window.ps1` compiles (its `Add-Type` of `scripts/lib/DesktopLock.cs`) as an assembly keyed by the source's hash, keeping one source for both callers, for under 100 ms of added lock overhead at p95 across 30 fresh uncontended captures (base line 165). Done when: `tests/capture_window_test.ps1` reads the cached assembly reused, and rebuilt when the source changes, and the p95 over 30 captures is recorded here under 100 ms. Cheaper substitute: a second copy of the source, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §5`**).
- [ ] Add one read-only command for `scripts/lib/DesktopLock.cs` that reports, from another terminal, the desktop's holder, how many wait and the oldest wait, without touching the desktop, as `bosun status` reports the queue's waiters (base line 166). Done when: `tests/Bosun.Panel.E2E/DesktopLockTests.cs` holds the lock with two waiting and reads the holder, the count 2 and the oldest wait from the command. Cheaper substitute: the holder record alone, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §5`**).
- [ ] Commit: `scripts: the desktop lock is compiled once and inspectable (D02 T05 §18)`

**Test checkpoint:** `pwsh tests/capture_window_test.ps1` exits 0, `dotnet test --project tests/Bosun.Panel.E2E --filter-class "*DesktopLock*"` exits 0, and the overhead is in this section.

## 19. The nightly smoke looks at the desk first, and its status points at each report

Filed 2026-11-22 by `D00 T06 §5` from `todo/findings.md` (base lines 173 and 174, the completion review of `D02 T02 §12`). The nightly smoke builds before it looks at the desk, and its status names a failure without its report (`scripts/smoke-input.ps1`). Split 2026-11-22 from one section by round 2 of `D00 T06 §5`, one script each.

**Phase:** 2
**Needs:** none
**Surface:** no

**Build order:**

1. Look first in the nightly admission of `scripts/smoke-input.ps1`. Done when: `tests/smoke_input_test.ps1` reads no build or discovery started on a locked or recently used desk. (**corrected 2026-11-22, the completion review of `D00 T06 §5`**)
2. Point at reports from `scripts/smoke-input.ps1 -Status`. Done when: `tests/smoke_input_test.ps1` reads each failed test's TRX and log paths. (**corrected 2026-11-22, the completion review of `D00 T06 §5`**)

- [ ] Look at the desk's lock and idle time in `scripts/smoke-input.ps1` before the build in the nightly admission, so a desk locked or used within 15 minutes makes zero build or discovery launches, keeping the guarded recheck after the watcher (base line 173). Done when: `tests/smoke_input_test.ps1` runs a scheduled admission on a locked desk and on a recently used one and reads no build or discovery started, and the recheck kept. Cheaper substitute: the single check after the build, as now, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §5`**).
- [ ] Have `scripts/smoke-input.ps1 -Status` name each failed real-input test and print the path to its retained TRX report and class log, in one call (base line 174). Done when: `tests/smoke_input_test.ps1` fails a test, calls `-Status`, and reads the test's name and both paths, each file present. Cheaper substitute: the names only, as now, which the Done when refuses (**filed 2026-11-22 by `D00 T06 §5`**).
- [ ] Commit: `scripts: the nightly smoke looks first and points at its reports (D02 T05 §19)`

**Test checkpoint:** `pwsh tests/smoke_input_test.ps1` exits 0.
## Verification

- Every section's Test checkpoint passes on the stamped candidate, and `python scripts/todo-graph.py validate` passes.
- `dotnet test --project tests/Bosun.Panel.Tests --filter-class "*ThemeContrast*"` passes in both themes (**corrected 2026-11-22, round 4 of `D00 T06 §4`:** written as VSTest arguments).
