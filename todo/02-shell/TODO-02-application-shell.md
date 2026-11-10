---
schema_version: 1
id: application-shell
domain: 02-shell
status: active
title: "TODO-02 -- The application shell"
---

# TODO-02 -- The application shell

> **Goal:** A themed, accessible, single-instance window with rail navigation, settings and a tray, whose every page can be rendered and reviewed unattended.

> **Current state:** Measured 2026-11-07. No application exists. Steward.Console is the proven model, with known gaps: no single instance, no crash handling, arguments split on spaces, administrator required.

## Inputs

- `design/SPEC.md` §1, §15, §17.
- `R:\GitHub\Steward\src\Steward.Console`.
- -> XREF: D02 T01 §4 -- consumes this file
- -> XREF: D02 T01 §4 -- consumed by this file
- -> XREF: D00 T02 §1 -- consumed by this file
- -> XREF: D03 T01 §1 -- consumes this file
- -> XREF: D03 T02 §1 -- consumes this file
- -> XREF: D05 T02 §1 -- consumes this file
- -> XREF: D07 T01 §1 -- consumes this file
- -> XREF: D02 T03 §2 -- consumes this file
- -> XREF: D03 T01 §5 -- consumes this file

## Outcome

- The panel starts once, survives a crash with a report, and shows every rail row.
- Every page renders to PNG with no visible desktop.

**Adjacency:** list=not-applicable (no records of its own); document=not-applicable (nothing carried); settings=applicable @ D02 T02 §5; reporting=not-applicable (later pages); notifications=applicable @ D02 T02 §7; permissions=not-applicable (per-user, no roles); audit=applicable @ D02 T02 §4; exchange=not-applicable (nothing imported); reverse=not-applicable (settings are re-editable)

**Adjacency rationale:** The Activity pane is the panel's audit trail. Every later page writes to it.

## Implementation Order

| Order | Section | Deliverable | Depends On | Status |
| ----- | ------- | ----------- | ---------- | ------ |
| 1 | §1 | Startup, single instance and crash handling | D02 T01 §4 | [ ] |
| 2 | §2 | View model base and accessibility helpers | §1 | [ ] |
| 3 | §3 | Process runner | D00 T02 §1 | [ ] |
| 4 | §4 | Window chrome, rail and activity pane | §2, §3, §9 | [ ] |
| 5 | §5 | Settings store and theme switching | §4 | [ ] |
| 6 | §7 | Tray icon and notifications | §5, D02 T01 §3 | [ ] |
| 7 | §8 | Pollers that stop when nothing is looking | §3 | [ ] |
| 8 | §9 | Off-screen page rendering for review | D02 T01 §1 | [ ] |
| 9 | §10 | End-to-end smoke through UI Automation | §4, §9 | [ ] |

## 1. Startup, single instance and crash handling

Steward has neither single-instance nor crash handling, which the review of its console identified as the main thing not to copy.

**Phase:** 2
**Needs:** none
**Surface:** no

**Build order:**

1. Create `src/Bosun.Panel/App.xaml.cs`. Done when: a second launch activates the first and exits.
2. Add the crash handlers. Done when: a thrown exception writes a report and shows a message.

- [ ] Add a named mutex and activation pipe in `App.xaml.cs`. Done when: a second process exits 0 and the first window comes to the front. Cheaper substitute: two panels polling at once.
- [ ] Handle `DispatcherUnhandledException`, `AppDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException`, writing to `%LOCALAPPDATA%\Bosun\crash\`. Done when: a test seam throwing on each path leaves a report without secrets. Cheaper substitute: swallowing the exception and carrying on silently.
- [ ] Create `src/Bosun.Panel/app.manifest` with per-monitor v2 and `asInvoker`, citing `R:\GitHub\Steward\src\Steward.Console\app.manifest:27-28`. Done when: a test reads the manifest and asserts both. Cheaper substitute: copying `requireAdministrator`.
- [ ] Commit: `shell: single instance and crash reports`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~Startup` exits 0, and fails if the change is reverted.

## 2. View model base and accessibility helpers

These are small, proven and worth reusing unchanged, with their tests.

**Phase:** 2
**Needs:** none
**Surface:** no

**Build order:**

1. Port `Observable`, `AsyncCommand`, `LiveRegion`, `SelectionGuard` and `UiLogSink`. Done when: each has its test passing here.
2. Add a Lucide kind test. Done when: each icon name resolves against the enum.

- [ ] Port `ViewModels\Observable.cs` and `Services\LiveRegion.cs` from `R:\GitHub\Steward\src\Steward.Console`. Done when: re-entry is refused and announcements fire in `tests/Bosun.Panel.Tests`.
- [ ] Port `SelectionGuard.cs` and `UiLogSink.cs`. Done when: a guarded rail refuses leaving a dirty page, and the sink caps at 500 entries. Cheaper substitute: undoing the selection afterwards, which does not hold in WPF.
- [ ] Add `MahApps.Metro.IconPacks.Lucide` and a test that every `Section.Icon` parses. Done when: a misspelled kind fails the test.
- [ ] Commit: `shell: view model base and helpers`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~Helpers` exits 0, and fails if the change is reverted.

## 3. Process runner

Every tool call goes through here, so it owns the rules: an argument list, no window, a timeout and bounded output. Steward's runner splits arguments on spaces, which this must not copy.

**Phase:** 2
**Needs:** none
**Surface:** no

**Build order:**

1. Create `src/Bosun.Core/ProcessRunner.cs`. Done when: quoted and spaced arguments round-trip.
2. Add timeout and cancel. Done when: a timed-out child and its tree stop.

- [ ] Use `ArgumentList`, `CreateNoWindow`, and redirected output capped at a configured size. Done when: spaces, quotes and Unicode survive, and huge output is cut with a marker. Cheaper substitute: a single command string.
- [ ] Add timeout and cancellation with tree kill. Done when: a sleeper child is gone within a second of cancel. Cheaper substitute: abandoning the process and returning.
- [ ] Add `RunJsonAsync<T>` refusing an unknown `schema_version`. Done when: a newer schema gives a typed `Unsupported` result, not an exception or defaults.
- [ ] Commit: `core: process runner`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~ProcessRunner` exits 0, and fails if the change is reverted.

## 4. Window chrome, rail and activity pane

This is the frame every page sits in. Snap layouts need real hit testing, which Steward's chrome lacks.

**Phase:** 2
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §1.
**Job:** the operator always knows where they are and what the panel just did.
**Treatment:** custom chrome, 208 px rail, resizable activity pane, four page states. Cheaper substitute that fails: a stock window with tabs.
**Chrome:** the generated theme with `Btn.Chrome`; no third-party window framework.

**Build order:**

1. Build `MainWindow.xaml` per `design/SPEC.md` §1. Done when: the off-screen render matches the mockup's layout in both themes.
2. Handle `WM_NCHITTEST`. Done when: hovering maximise reports `HTMAXBUTTON`.

- [ ] Build the title bar, rail and activity pane with the `Section` record from `R:\GitHub\Steward\src\Steward.Console\ViewModels\MainViewModel.cs:112`. Done when: adding a page is one row, and pages bind through implicit `DataTemplate`s. Cheaper substitute: a static screenshot-like layout with no navigation.
- [ ] Return `HTMAXBUTTON` from a `WM_NCHITTEST` hook. Done when: a test with synthetic coordinates gets the right code. Cheaper substitute: custom caption buttons without snap layouts.
- [ ] Add placeholder pages for every rail entry, each deferring to its owning section. Done when: each rail row opens a page that names its owner section, and none is a dead control.
- [ ] Create `src/Bosun.Panel/Shell/PageFrame.xaml` and `PageBase.cs`, holding the header and the content host. Done when: a page renders through them. Cheaper substitute: each view drawing its own header.
- [ ] Create `src/Bosun.Panel/Shell/ActivityLog.cs` over `UiLogSink`, with each line starting INFO, WARN or FAIL. Done when: a test with no prefix given refuses the line.
- [ ] Log every panel action through `AsyncCommand` to `ActivityLog.cs`. Done when: a test runs a command and finds one line, and a view can't add a command without it. Cheaper substitute: each view logging by hand.
- [ ] Set `MinWidth` 960 and `MinHeight` 600 in `MainWindow.xaml`. Done when: a test reads both.
- [ ] Add the Activity pane splitter in `MainWindow.xaml`, saved in `PanelSettings`. Done when: a drag resizes it within limits, and the size reads back after a restart. Cheaper substitute: a fixed height.
- [ ] Commit: `shell: chrome, rail and activity pane`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~Shell` exits 0, and fails if the change is reverted.

## 5. Settings store and theme switching

Settings live in one atomic file. Theme is the first consumer.

**Phase:** 2
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §15, General.
**Job:** the operator sets the panel up once and it stays that way.
**Treatment:** a plain form with Save, showing read-back. Cheaper substitute that fails: settings applied only in memory.
**Chrome:** the existing page frame and `Field` styles.

**Build order:**

1. Create `src/Bosun.Core/PanelSettings.cs`. Done when: a save writes atomically and reads back.
2. Switch merged dictionaries live. Done when: Windows' theme change applies without restart.

- [ ] Persist `%LOCALAPPDATA%\Bosun\panel.json` with `schema_version` and temp-then-rename. Done when: a corrupt file is kept as `.bad` and defaults load with a notice. Cheaper substitute: losing the corrupt file silently.
- [ ] Follow `SystemEvents.UserPreferenceChanged` and swap `Theme/Generated` dictionaries in `App.xaml.cs`. Done when: a test flips the preference and a brush resolves to the other theme.
- [ ] Build the General part of the Settings page per `design/SPEC.md` §15. Done when: theme and start-with-Windows save and read back. Cheaper substitute: a setting nothing reads.
- [ ] Write and remove the `Bosun Panel` Run value for Start with Windows in the tray, in `src/Bosun.Core/StartupEntry.cs`, behind a registry seam. Done when: a test proves on, off and read-back with the real key untouched. Cheaper substitute: a setting nothing reads.
- [ ] Commit: `shell: settings store and live theme`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~Settings` exits 0, and fails if the change is reverted.

## 7. Tray icon and notifications

The panel's most common state is closed. The tray is what makes it useful while working elsewhere.

**Phase:** 2
**Needs:** none
**Surface:** yes
**Fidelity:** `design/SPEC.md` §17.
**Job:** the operator hears about memory pressure without opening the window.
**Treatment:** tray icon, menu and toasts. Cheaper substitute that fails: a window that must stay open.
**Chrome:** standard Windows tray and toast surfaces.

**Build order:**

1. Add `H.NotifyIcon.Wpf` and `TrayViewModel`. Done when: state changes the icon and tooltip.
2. Add notifications with a pause. Done when: each type can be turned off.

- [ ] Show `resources/tray-*.ico` with a tooltip in words in `src/Bosun.Panel/TrayViewModel.cs`. Done when: a test maps each state to icon and text. Cheaper substitute: colour-only state.
- [ ] Add the menu per `design/SPEC.md` §17, and the first-close notice. Done when: Exit stops every poller, and closing the window keeps running after saying so once.
- [ ] Add toast notifications with per-type settings and a one-hour pause. Done when: a disabled type raises nothing, in a test with a fake sink.
- [ ] Add the Needs you tray state in `src/Bosun.Panel/Shell/TrayIcon.cs`, read from `INeedsYouSource`. Done when: a fake source with one row shows the overlay and tooltip words, and none shows Normal.
- [ ] Commit: `shell: tray and notifications`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~Tray` exits 0, and fails if the change is reverted.

## 8. Pollers that stop when nothing is looking

A monitor that burns CPU while hidden defeats itself. One scheduler owns all refresh, which also gives the performance budget one place to measure.

**Phase:** 2
**Needs:** none
**Surface:** no

**Build order:**

1. Create `src/Bosun.Core/RefreshScheduler.cs`. Done when: only visible pages poll.
2. Add backoff. Done when: a failing tool backs off and says so.

- [ ] Register a refresh per page with intervals per `design/SPEC.md` §1. Done when: hiding a page stops its calls within one interval, in a fake-clock test. Cheaper substitute: a timer per view model that is never stopped.
- [ ] Add failure backoff and overlap refusal. Done when: a slow tool never runs twice at once, and the page shows its failed state with the message.
- [ ] Add a Refresh button to `PageFrame.xaml`, calling the page's scheduled refresh once. Done when: a test proves one extra call and no overlap with a running poll. Cheaper substitute: waiting for the next tick.
- [ ] Commit: `core: refresh scheduler`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~RefreshScheduler` exits 0, and fails if the change is reverted.

## 9. Off-screen page rendering for review

A surface section has to be provable in an unattended run with no visible desktop, or surfaces would never get reviewed. This renders real views to PNG, in both themes.

**Phase:** 2
**Needs:** none
**Surface:** no

**Build order:**

1. Create `tests/Bosun.Panel.Tests/Render/PageRenderer.cs`. Done when: any WPF element renders to PNG through `RenderTargetBitmap` on a hidden STA thread.
2. Port `R:\GitHub\Resolute\scripts\capture-window.ps1`. Done when: it captures a real window with its sidecar.

- [ ] Render every view registered with the renderer, in each state at 1360 x 860 and in both themes, to `build/renders/`. Views register as their sections land. Done when: a test with a sample element asserts the image is not blank and has the expected size. Cheaper substitute: golden pixel comparisons that break on font updates.
- [ ] Add `scripts/render-pages.ps1` to write all renders. Done when: the review prompt can name the folder, and renders stay out of Git.
- [ ] Port `scripts/capture-window.ps1` for attended checks. Done when: it fails closed when the target isn't in the foreground, with exit 3.
- [ ] Commit: `tests: off-screen page renders`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~PageRenderer` exits 0, and fails if the change is reverted.

## 10. End-to-end smoke through UI Automation

One real launch proves the parts work together: the process starts, every page opens and every control has a name. This needs a desktop session, so a run leaves it for an attended pass.

**Phase:** 2
**Needs:** display-session
**Surface:** no

**Build order:**

1. Add `tests/Bosun.Panel.E2E` with FlaUI. Done when: it launches the panel and walks the rail.
2. Assert names. Done when: each interactive control has an automation name.

- [ ] Launch `Bosun.Panel.exe` with a temporary settings folder and walk every rail row. Done when: each page reaches data, empty or failed, and none crashes. Cheaper substitute: testing only view models.
- [ ] Walk the automation tree and fail on a nameless interactive control. Done when: removing one `AutomationProperties.Name` fails the test.
- [ ] Commit: `tests: end-to-end smoke`

**Test checkpoint:** `bosun run -- dotnet test tests/Bosun.Panel.E2E` exits 0 in a desktop session.

## Verification

- The end-to-end smoke passes in a desktop session.
- `dotnet test Bosun.slnx` passes.
