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
| 8 | §8 | Render catalog: a review report, readable companions, progress and an honest Shows | -- | [ ] |
| 9 | §9 | Controls measured, and motion through one helper | -- | [ ] |
| 10 | §10 | The crash notice copies a redacted report and names its buttons | -- | [ ] |
| 11 | §11 | Test host: a named crash, no error reports, kept evidence, a measured smoke | -- | [ ] |
| 12 | §12 | Async commands say they are running and can be cancelled | -- | [ ] |
| 13 | §13 | The panel starts fast, answers a hung first launch, and opens at a page | -- | [ ] |
| 14 | §14 | Refresh says when and how it went, and a failure can be copied | -- | [ ] |
| 15 | §15 | Settings preview a theme, save off the UI thread, and keep a snapshot | -- | [ ] |
| 16 | §16 | The tray shows headroom and opens its menu beside the icon | -- | [ ] |

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

**Test checkpoint:** `dotnet test tests/Bosun.Core.Tests --filter ProcessRunner` exits 0, and the benchmark table is in this section.

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

## 8. Render catalog: a review report, readable companions, progress and an honest Shows

Filed 2026-11-22 by `D00 T06 §3` from `todo/findings.md` (base lines 76 to 79, completion reviews of `D02 T02 §9`). `WriteAll` in `tests/Bosun.Panel.Tests/Render/RenderCatalog.cs` writes only the PNGs and their JSON index, keeps every case's decoded pixels for a theme and reports nothing until it ends; `Shows` in `tests/Bosun.Panel.Tests/Render/PageRenderer.cs` (`Texts`) counts text that is transparent, clipped away or covered.

**Phase:** 2
**Needs:** none
**Surface:** no

**Build order:**

1. Make a review readable. Done when: one command writes a report pairing each changed case's pictures, and each case has a text companion.
2. Make the run honest and light. Done when: `Shows` ignores hidden text, and a run reports its progress and keeps only the pixels it needs.

- [ ] Have one command write a report pairing each changed case's previous and current picture, with links, advisory and tolerant of font differences, as Verify with DiffEngine pairs them (base line 76). Done when: a test changes one case and reads the report naming that case alone with both pictures.
- [ ] Write a readable companion beside each PNG (visible text, then the automation names in their order) from the texts the renderer already collects, as Verify.Xaml keeps XML beside its pictures (base line 77). Done when: a test reads a case's companion and finds its texts and names in order, and a reordered control changes it.
- [ ] Have `Shows` refuse text that is fully transparent, fully clipped or fully covered (base line 78). Done when: three fixtures, one of each, fail `Shows`, and a visible one still passes.
- [ ] Report completed of total and each case's time as the catalog runs, keep a case's comparison pixels only while needed, and measure time to first picture and peak memory against an equal Verify run (base line 79). Done when: the run prints its progress, and the before and after numbers are recorded here.
- [ ] Commit: `render: a review report, readable companions, progress and an honest Shows (D02 T05 §8)`

**Test checkpoint:** `dotnet test tests/Bosun.Panel.Tests --filter Render` exits 0, with the three `Shows` fixtures failing against today's check.

## 9. Controls measured, and motion through one helper

Filed 2026-11-22 by `D00 T06 §3` from `todo/findings.md` (base lines 81 to 83, 107 and 108, completion reviews of `D02 T01 §4` and `D02 T03 §1`). `src/Bosun.Panel/Theme/Controls.xaml` has no measured responsiveness for `LogBox` and `DataGrid`, its `DataGridColumnHeader` sort and grips are proved on the style only, and its `ComboBox` drop-down does not fade with the other overlays; each consumer of `Changed` in `src/Bosun.Panel/Theme/Motion.cs` starts and cancels its own animation; and the animations of `todo/02-shell/TODO-03-premium-components.md` are not measured at 60 Hz.

**Phase:** 2
**Needs:** display-session
**Surface:** yes

**Build order:**

1. Measure. Done when: the log, the table and the animations have recorded numbers against their targets.
2. Unify. Done when: every animation starts and finishes through one helper, the drop-down included.

- [ ] Time a 500-line log and a large process table against Fluent's controls on one machine, p95 interaction under 100 ms with virtualization kept (base line 81). Done when: the numbers are recorded here with their commit.
- [ ] Sort a table by a header click twice and resize a column at 100%, 150% and 200% scaling, rendered in both themes (base line 82). Done when: a test drives both and the renders are in `build/renders/`.
- [ ] Give the `ComboBox` drop-down the gated fade of `Theme/Motion.cs`, matching the palette and toasts (base line 83). Done when: a test reads the fade with motion on and none with it off.
- [ ] Give `Motion.cs` one start-and-finish helper that every consumer uses, so motion turned off leaves no animation clock running and no consumer repeats the cancelling (base line 107). Done when: a test turns motion off mid-animation and finds no clock left, and no consumer calls `BeginAnimation` itself.
- [ ] Measure the finished animations and shadows of `todo/02-shell/TODO-03-premium-components.md` at 60 Hz: the 95th percentile frame interval at or under 16.7 ms, no animation work on a hidden page, the 150 MB and 1% CPU idle budget kept, beside WinUI Composition's off-thread animations (base line 108). Done when: the numbers are recorded here with their commit.
- [ ] Commit: `panel: controls measured, and motion through one helper (D02 T05 §9)`

**Test checkpoint:** `dotnet test tests/Bosun.Panel.Tests --filter "Motion|Controls"` exits 0, and the measurements are in this section.

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

- [ ] Add a Copy redacted report action needing one activation, uploading nothing, beside Open crash folder (base line 84). Done when: a test triggers it and reads the clipboard text with the user folder and machine name redacted.
- [ ] Ask with a task dialog whose buttons are Restart Bosun Panel and Close, each one keyboard activation, checked at 100%, 200% and in a contrast theme (base line 86). Done when: a test reads both buttons and their results, and the renders are in `build/renders/`.
- [ ] Commit: `panel: the crash notice copies a redacted report and names its buttons (D02 T05 §10)`

**Test checkpoint:** `dotnet test tests/Bosun.Panel.Tests --filter Crash` exits 0.

**Fidelity:** `design/SPEC.md` (the crash notice) and its renders.
**Job:** the operator who saw the panel fail can hand a report over in one step, and knows what each button does.
**Treatment:** a task dialog with named actions. Cheaper substitute that fails: Yes and No with a longer sentence.
**Chrome:** the system task dialog; no custom window.

## 11. Test host: a named crash, no error reports, kept evidence, a measured smoke

Filed 2026-11-22 by `D00 T06 §3` from `todo/findings.md` (base lines 89, 90, 104 and 105). One full test run in about 25 ended with a heap-corruption exit and no dump (`tests/Bosun.Panel.Tests`, 2026-11-15); each crash test leaves a Windows error report (`tests/Bosun.Panel.TestChild/Program.cs`, `crash-thread`); a failed smoke test deletes its own folder (`tests/Bosun.Panel.E2E/PanelProcess.cs`, `Dispose`); and the smoke's duration is unmeasured.

**Phase:** 2
**Needs:** none
**Surface:** no

**Build order:**

1. Keep the evidence. Done when: a crashing test host leaves a dump, and a failed smoke test keeps its folder.
2. Leave no noise and measure. Done when: the crash test leaves no error report, and the smoke's timings are recorded.

- [ ] Collect a dump when the test host crashes (`--blame-crash` with a dump type, or `DOTNET_DbgEnableMiniDump`), and run the panel's tests in full ten times recording any crash, its dump and the free memory then (base line 89). Done when: the runs are recorded here, and a crash, if any, has its dump and stack named or is filed with them.
- [ ] Have the test child opt out of Windows Error Reporting for itself (`SetErrorMode` with `SEM_NOGPFAULTERRORBOX`, or `WerSetFlags`) before it dies on purpose (base line 90). Done when: a crash test run leaves no new Application log error or report, checked before and after.
- [ ] Keep the folder of a failed smoke test and print its path with the last assertion's context, with a capture of the panel's window only where `RealInput`'s checks allow it (base line 104). Done when: a deliberately failed smoke test leaves its folder and prints its path; a passing one leaves nothing.
- [ ] Measure the smoke's cold and warm duration and its three slowest tests over five runs (base line 105). Done when: the numbers are recorded here with their commit.
- [ ] Commit: `tests: the panel's host names a crash, leaves no error report, keeps a failed smoke's evidence (D02 T05 §11)`

**Test checkpoint:** `dotnet test tests/Bosun.Panel.Tests --filter Crash` exits 0 with no new Application log error, and the runs and timings are in this section.

## 12. Async commands say they are running and can be cancelled

Filed 2026-11-22 by `D00 T06 §3` from `todo/findings.md` (base lines 87 and 88, completion review of `D02 T02 §2`). `IsRunning` in `src/Bosun.Panel/Mvvm/AsyncCommand.cs` raises no change notification, and the command has no shared cancellation.

**Phase:** 2
**Needs:** none
**Surface:** no

**Build order:**

1. Notify. Done when: `IsRunning` raises `PropertyChanged` at its start and end.
2. Cancel. Done when: a cancellable command has a token and a Cancel command.

- [ ] Raise `PropertyChanged` for `IsRunning` at the start and the end, failure included, keeping the 150 ms loading delay of `design/SPEC.md` §19, as CommunityToolkit's `AsyncRelayCommand` does (base line 87). Done when: a test binds to it and reads both changes, a failing action included.
- [ ] Give a cancellable command a token and a Cancel command: one activation, immediate feedback, no failure shown for a cancel the person asked for, and a clean next run (base line 88). Done when: a test cancels a running action and reads no failure and a clean second run.
- [ ] Commit: `panel: async commands say they are running and can be cancelled (D02 T05 §12)`

**Test checkpoint:** `dotnet test tests/Bosun.Panel.Tests --filter AsyncCommand` exits 0.

## 13. The panel starts fast, answers a hung first launch, and opens at a page

Filed 2026-11-22 by `D00 T06 §3` from `todo/findings.md` (base lines 85, 91 and 92, completion reviews of `D02 T02 §1` and `§3`). A second launch waits up to two five-second tries with nothing on screen when the first panel is hung (`src/Bosun.Panel/Startup/SingleInstance.cs`, `Start`); there is no way to open the panel at a page from outside (`src/Bosun.Panel/Startup/PanelStartup.cs`); and nothing measures start or navigation (`src/Bosun.Panel/Shell/ShellView.xaml`).

**Phase:** 2
**Needs:** display-session
**Surface:** yes

**Build order:**

1. Answer a hung launch. Done when: waiting feedback shows within 500 ms.
2. Open at a page. Done when: a page key on the command line or the pipe opens the panel there.
3. Measure. Done when: warm activation, cold start and page switch have recorded numbers.

- [ ] Show waiting feedback within 500 ms on the hung path and measure warm activation, target p95 under 250 ms against PowerToys on one machine, keeping the acknowledgement (base line 85). Done when: a test with a first panel that does not answer reads the feedback within 500 ms, and the warm numbers are recorded here.
- [ ] Take a page key on the command line and over the activation pipe (`activate queue`), opening or bringing the panel forward at that page through `ShellViewModel.GoToAsync`, as PowerToys' settings links do (base line 91). Done when: a test launches with `queue` and reads the Queue page shown, and a second launch with `memory` moves the running panel there.
- [ ] Measure a cold start to an interactive shell (target under 1 s at p95) and a page switch with 500 lines in the Activity pane (under 100 ms at p95) against PowerToys on one machine, within `D07 T01 §5`'s budget (base line 92). Done when: the numbers are recorded here with their commit.
- [ ] Commit: `panel: starts fast, answers a hung first launch, and opens at a page (D02 T05 §13)`

**Test checkpoint:** `dotnet test tests/Bosun.Panel.Tests --filter "SingleInstance|PanelStartup"` exits 0, and the measurements are in this section.

**Fidelity:** `design/SPEC.md` §1 (the shell) and the waiting feedback's render.
**Job:** the operator who clicks twice, or follows a link to a page, is never left with nothing on screen.
**Treatment:** the decided shell, with a waiting notice and page keys. Cheaper substitute that fails: a longer single-instance timeout.
**Chrome:** the shell's own toast and pages; no new window.

## 14. Refresh says when and how it went, and a failure can be copied

Filed 2026-11-22 by `D00 T06 §3` from `todo/findings.md` (base lines 93, 101, 102, 109 and 110, completion reviews of `D02 T02 §4`, `§8` and `D02 T03 §7`). A failure's message and a log line can't be copied (`src/Bosun.Panel/Shell/PageFrame.xaml`, the Activity pane); `src/Bosun.Panel/Shell/DispatcherRefreshDriver.cs` (`Every`) ticks once a second, adding up to a second; a failed page says when it tries again only in the first Activity line (`src/Bosun.Panel/Shell/PageBase.cs`, `RefreshEvery`); a Refresh the person asked for shows nothing until it ends (`PageFrame.xaml`); and a slow first load never says it has loaded (`src/Bosun.Panel/Controls/Skeleton.cs`).

**Phase:** 2
**Needs:** display-session
**Surface:** yes

**Build order:**

1. Time it right. Done when: a refresh runs within 100 ms of being due.
2. Say it. Done when: a page shows when it last refreshed and next tries, a manual Refresh says "Refreshing", and a slow load says it loaded.
3. Copy it. Done when: one activation copies a whole diagnostic.

- [ ] Add a Copy action, one activation and reachable by keyboard, to a failure's message and to an Activity line, copying the whole diagnostic and uploading nothing (base line 93). Done when: a test copies each and reads the clipboard.
- [ ] Set `DispatcherRefreshDriver`'s timer to the earliest due time instead of a one-second tick, for under 100 ms of added latency with no more wake-ups, keeping the intervals and no ticking while nothing is active (base line 101). Done when: a test reads a refresh within 100 ms of due, the same wake-up count, and no timer while nothing is active.
- [ ] Show on a failed page when it last refreshed and when the next try is, without announcing each change to a screen reader (base line 102). Done when: a test reads both times on the page and no live-region change per tick.
- [ ] After 150 ms of a Refresh the person asked for, say "Refreshing" beside the button without a modal, clear it at the end and announce the result once, never for a routine poll (base line 109). Done when: a test reads the word after 150 ms, gone after, one announcement, and none for a poll.
- [ ] Say once, politely and without moving focus, that a page has loaded, only for a load that showed the skeleton, checked with Narrator or NVDA (base line 110). Done when: a test reads one polite announcement after a slow load and none after a fast one, and the screen reader check is recorded here.
- [ ] Commit: `panel: refresh says when and how it went, and a failure can be copied (D02 T05 §14)`

**Test checkpoint:** `dotnet test tests/Bosun.Panel.Tests --filter "Refresh|PageFrame|Skeleton"` exits 0.

**Fidelity:** `design/SPEC.md` §19 (loading and refresh) and the page frame's renders.
**Job:** the operator knows a page is current, when it next tries, and can hand a failure over in one step.
**Treatment:** quiet status text and one polite announcement. Cheaper substitute that fails: a spinner with no words.
**Chrome:** `Shell/PageFrame.xaml` and the Activity pane; no new control.

## 15. Settings preview a theme, save off the UI thread, and keep a snapshot

Filed 2026-11-22 by `D00 T06 §3` from `todo/findings.md` (base lines 94 to 96 and 100, completion reviews of `D02 T02 §5` and `§7`). A damaged `panel.json` falls back to the defaults (`src/Bosun.Core/PanelSettingsStore.cs`); a theme is seen only after Save (`src/Bosun.Panel/Settings/SettingsPage.cs`, `SelectedTheme`); `SaveAsync` writes, flushes, reads back and calls the registry on the UI thread; and the tray's first-close flag is saved on it too (`src/Bosun.Panel/Tray/TrayController.cs`, `NoticeWasShown`).

**Phase:** 2
**Needs:** display-session
**Surface:** yes

**Build order:**

1. Keep a snapshot. Done when: a damaged file offers to restore the last good one.
2. Preview and save off the UI thread. Done when: a theme previews at once and saves never block the window.

- [ ] Keep one validated previous snapshot (`panel.json.last`) and offer to restore it in one action, saying exactly what was recovered, as PowerToys restores from a backup (base line 94). Done when: a test damages `panel.json` and restores from the snapshot, reading back each value.
- [ ] Preview a chosen theme at once (under 100 ms), Save committing it and Discard restoring the saved theme, keeping the segmented control of `D02 T03 §4` (base line 95). Done when: a test chooses a theme, reads it applied, discards, and reads the saved one back.
- [ ] Move `SaveAsync`'s write, flush, read-back and registry call off the UI thread, with feedback within 100 ms, a busy state after 150 ms, and the window answering through a one-second injected delay (base line 96). Done when: a test injects the delay and reads the window answering and the busy state.
- [ ] Save the tray's first-close flag off the UI thread with the Settings page's saves, and measure tray Open and the menu under 100 ms at p95 in `D07 T01`'s budget run (base line 100). Done when: a test reads the flag saved with the UI thread free, and the numbers are recorded here.
- [ ] Commit: `panel: settings preview a theme, save off the UI thread, and keep a snapshot (D02 T05 §15)`

**Test checkpoint:** `dotnet test tests/Bosun.Panel.Tests --filter "Settings|Tray"` and `dotnet test tests/Bosun.Core.Tests --filter PanelSettingsStore` exit 0.

**Fidelity:** `design/SPEC.md` (the Settings page) and its renders in both themes.
**Job:** the operator tries a theme without committing to it, and never loses settings to a damaged file.
**Treatment:** a live preview with Save and Discard. Cheaper substitute that fails: saving on every choice.
**Chrome:** the Settings page's own controls; no new dialog.

## 16. The tray shows headroom and opens its menu beside the icon

Filed 2026-11-22 by `D00 T06 §3` from `todo/findings.md` (base lines 97 and 99, completion review of `D02 T02 §7`). The healthy tray says only that it is normal (`src/Bosun.Panel/Tray/TrayViewModel.cs`, `Tooltip`), and a menu opened from the keyboard appears at the mouse pointer (`src/Bosun.Panel/Tray/TrayShell.cs`, `TrackMenu`).

**Phase:** 2
**Needs:** display-session
**Surface:** yes

**Build order:**

1. Show headroom. Done when: the tooltip gives memory free and the sample's age.
2. Anchor the menu. Done when: a keyboard-opened menu appears beside the icon.

- [ ] Add the memory free and the sample's age to the healthy tooltip, from the pressure sample `D03 T01 §4` already takes, with no second poller (base line 97). Done when: a test reads both in the tooltip and one poller.
- [ ] Anchor a menu opened from the keyboard beside the icon, from the x and y Windows sends in `wParam` at version 4, checked at 100, 150 and 200 percent scaling (base line 99). Done when: a test sends a keyboard open with a pointer on another monitor and reads the menu at the icon's position.
- [ ] Commit: `tray: headroom in the tooltip, and the menu beside the icon (D02 T05 §16)`

**Test checkpoint:** `dotnet test tests/Bosun.Panel.Tests --filter Tray` exits 0.

**Fidelity:** `design/SPEC.md` (the tray) and the menu's position at each scale.
**Job:** the operator sees memory headroom in one hover and finds the menu where the keyboard opened it.
**Treatment:** a fuller tooltip and an anchored menu. Cheaper substitute that fails: a tooltip that polls on its own.
**Chrome:** the tray's own menu and tooltip; no new window.

## Verification

- Every section's Test checkpoint passes on the stamped candidate, and `python scripts/todo-graph.py validate` passes.
- `dotnet test tests/Bosun.Panel.Tests --filter ThemeContrast` passes in both themes.
