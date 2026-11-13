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

> **Corrected 2026-11-13:** a WPF skeleton exists from `D00 T02 §1`: `src/Bosun.Panel` (`net11.0-windows`, `UseWPF`) with `App.xaml`, an empty `App.xaml.cs`, `Foundation.xaml` and `FoundationText.cs`, and `tests/Bosun.Panel.Tests` with only `FoundationTests.cs`; there is still no `Theme/`, `Shell/` or `Controls/` folder, no `MainWindow`, no `app.manifest`, and no MahApps, H.NotifyIcon or FlaUI package in `Directory.Packages.props`.

## Inputs

- `design/SPEC.md` §1, §15, §17.
- `D:\Steward\src\Steward.Console`.

**Corrected 2026-11-13:** the sibling repos are at `D:\<repo>` (`D:\Steward`, `D:\Office`, `D:\Recto`, `D:\Resolute`, `D:\Isotone`); `R:\GitHub` holds only CarbonCD and IsotoneStack, so every sibling-repo path this file named, given under `R:\GitHub`, now reads `D:\<repo>`.
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

1. Fill `src/Bosun.Panel/App.xaml.cs` (**Corrected 2026-11-13:** it exists, an empty `App` class from `D00 T02 §1`, beside `App.xaml`, `Foundation.xaml` and `FoundationText.cs`). Done when: a second launch activates the first and exits.
2. Add the crash handlers. Done when: a thrown exception writes a report and shows a message.

- [ ] Add a named mutex and activation pipe in `App.xaml.cs`. Done when: a second process exits 0 and the first window comes to the front. Cheaper substitute: two panels polling at once.
- [ ] Handle `DispatcherUnhandledException`, `AppDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException`, writing to `%LOCALAPPDATA%\Bosun\crash\`. Done when: a test seam throwing on each path leaves a report without secrets. Cheaper substitute: swallowing the exception and carrying on silently.
- [ ] Create `src/Bosun.Panel/app.manifest` with per-monitor v2 and `asInvoker`, citing `D:\Steward\src\Steward.Console\app.manifest:27-28`. Done when: a test reads the manifest and asserts both. Cheaper substitute: copying `requireAdministrator`.
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

- [ ] Port `ViewModels\Observable.cs` and `Services\LiveRegion.cs` from `D:\Steward\src\Steward.Console`. Done when: re-entry is refused and announcements fire in `tests/Bosun.Panel.Tests`.
- [ ] Port `SelectionGuard.cs` and `UiLogSink.cs`. Done when: a guarded rail refuses leaving a dirty page, and the sink caps at 500 entries. Cheaper substitute: undoing the selection afterwards, which does not hold in WPF.
- [ ] Add `MahApps.Metro.IconPacks.Lucide` and a test that every `Section.Icon` parses. Done when: a misspelled kind fails the test.
- [ ] Commit: `shell: view model base and helpers`

**Test checkpoint:** `bosun run -- dotnet test Bosun.slnx --filter FullyQualifiedName~Helpers` exits 0, and fails if the change is reverted.

## 3. Process runner

> **Started:** 2026-11-13T19:32:03Z

Every tool call goes through here, so it owns the rules: an argument list, no window, a timeout and bounded output. Steward's runner splits arguments on spaces, which this must not copy.

**Corrected 2026-11-13:** checked against the tree before the build. `src/Bosun.Core` (`net11.0`) holds only `Foundation.cs`, and `tests/Bosun.Core.Tests` only `FoundationTests.cs`; `Bosun.slnx` lists two source and two test projects. Steward's `Services\ScriptRunner.cs` (`D:\Steward\src\Steward.Console`) is the model for the rules, not the code: it splits a command string on spaces, has no timeout, awaits `WaitForExitAsync(ct)` (a cancel abandons the process), and reads output with no bound. The tools the runner calls now exist and say what it must read: `bosun status --json`, `bosun report --json` and `bosun config get --json` (`D01 T01`, stamped), each one JSON object with an integer `schema_version` on stdout, UTF-8, with diagnostics on stderr and exit 64 for a refused argument. The checkpoint's filter is the Microsoft.Testing.Platform form the repo uses (`--filter-class`). Decided here (industry defaults, each a small change to reverse): results are typed, never thrown, for everything a real tool call can do (a missing program, a timeout, a cancel, a nonzero exit, bad JSON), since `AGENTS.md` asks for typed errors; a child's whole tree ends with the call, through a Windows job object that is closed when the call returns, with `Process.Kill(entireProcessTree: true)` beside it for the moment before the child is in the job (the owner's rule: a child never outlives its caller, and a tree walk alone misses a grandchild whose parent has exited); output is read as bytes and decoded as UTF-8; JSON is read through a source-generated `JsonTypeInfo<T>`, so nothing needs reflection; and the tests' child is a small console project of its own, `tests/Bosun.TestChild`, because no shell on the machine hands its arguments on unchanged.

**Corrected 2026-11-13 (plan review):** the plan review in `docs/reviews/02-shell/D02-T02-s3.md` returned five gaps (3 P1, 2 P2), planned in below. The child is created inside its job, never put there afterwards: `Process.Start` can't do that, so the runner starts the child itself with `CreateProcessW`, naming the job in the start-up attributes (`PROC_THREAD_ATTRIBUTE_JOB_LIST`), so there is no moment in which the child runs outside it, and listing the three pipe handles as the only ones it inherits (this replaces the job-after-start and tree-walk design above). `bosun config set` and `unset --json` reply without a `schema_version`, so `RunJsonAsync` would call a saved limit a failure: the replies gain it in `src/app.cpp`, the layer that owns them. The tests' grandchild is bounded and ended by the test itself, and must be seen writing before its stopping is believed. Input is written beside the reads and can be cut short by the child without losing the result. And a successful JSON read keeps the process's stderr, where `bosun` says what it ignored.

**Corrected 2026-11-13 (build):** found while building, and built as corrected. The time-limit tests can't see the moment the child started, so they are measured with the start-up included: the child that never reads its input is ended within two seconds of its 300 ms limit, and the `tree` child's limit is 3 s, not 300 ms, so that its grandchild is seen to have run before it is ended (the cancel test waits for the grandchild, then measures from the cancel: under a second). `orphan` exits only once its grandchild has written, so the orphan is known to exist. The test child gained `cwd` and `env <name>`, and `print` takes an optional stderr text and exit code. A bare program name is looked for on `PATH` only, never in the current folder, and a `.cmd` or `.bat` is refused as `NotStarted`: `CreateProcessW` would hand it to `cmd.exe`, which reads arguments by other rules. Both are in `src/Bosun.Core/ProcessRunner.cs` (`Resolve`); the job, the pipes and the start are in `src/Bosun.Core/ChildProcess.cs`.

**Phase:** 2
**Needs:** none
**Surface:** no

**Build order:**

1. Create `src/Bosun.Core/ProcessRunner.cs`. Done when: quoted and spaced arguments round-trip.
2. Add timeout and cancel. Done when: a timed-out child and its tree stop.
3. Add `RunJsonAsync<T>`. Done when: each way a tool's JSON can be wrong is its own typed result.

- [x] Create the tests' child, `tests/Bosun.TestChild` (a `net11.0` console project in `Bosun.slnx`, built before `tests/Bosun.Core.Tests` through a `ProjectReference` that references no assembly): `args` prints its arguments as a JSON array, `out <bytes> <bytes>` writes that much to stdout and stderr, `sleep <ms>`, `exit <code>`, `stdin` echoes what it is given, `print <text>` writes it to stdout as UTF-8, `tree <file> <ms>` starts a grandchild (`beat <file>`) and then sleeps itself, `orphan <file>` starts the same grandchild and exits at once, and `beat <file>` writes its process id as the file's first line and then appends a line every 100 ms, at most 100 of them (ten seconds), and exits, so a grandchild nothing stops still ends. Each test that starts one ends it in its own teardown by that process id, whatever the runner did. Done when: the solution builds with it and `tests/Bosun.Core.Tests` finds the built `Bosun.TestChild.exe` beside its own output folder, failing with a message naming the path when it is missing.
- [x] Use `ArgumentList`, `CreateNoWindow`, and redirected output capped at a configured size, in `src/Bosun.Core/ProcessRunner.cs` (`ProcessRequest`, `ProcessResult`, `ProcessRunner.RunAsync`) (**Corrected 2026-11-13:** the runner starts the child itself, so these are its own equivalents: an argument list quoted by the rule `CommandLineToArgvW` reads back, `CREATE_NO_WINDOW`, and three pipes): no shell, stdin closed at once unless the request carries input, the working folder and extra environment variables from the request, stdout and stderr each read as bytes up to `MaxOutputBytes` (default 1 MiB), decoded as UTF-8 with a sequence cut at the cap dropped, the rest of the stream drained and discarded so the child never blocks on a full pipe, and `OutputTruncated` or `ErrorTruncated` set with the text ending in `[cut at <N> bytes]` on a line of its own. Done when: `tests/Bosun.Core.Tests/ProcessRunnerTests.cs` round-trips arguments holding spaces, quotes, backslashes before a quote, an empty argument, `&`, `%PATH%` and non-ASCII text; reads a 5 MiB flood on both streams at once without hanging, each cut at its cap with the marker; reads a multi-byte character straddling the cap as whole characters only; passes stdin, the working folder and an environment variable through; and returns the exit code. Cheaper substitute: a single command string.
- [x] Deliver input without waiting on it: the request's `StandardInput` is written on a task of its own beside the reads, stdin is closed once it is delivered, and a child that closes stdin early, exits mid-write or never reads ends the write quietly, with `InputDelivered` false in the result and the exit result, timeout and cancel all as they would be. Done when: tests give 4 MiB of input to a child that exits at once (`Exited`, its code, `InputDelivered` false, no hang), to a `stdin` child (all of it echoed back up to the cap, `InputDelivered` true), and to a `sleep` child that never reads, with a 300 ms timeout (`TimedOut` within a second).
- [x] Return a typed result, never an exception, for every real ending (`ProcessOutcome`: `Exited`, `TimedOut`, `Cancelled`, `NotStarted`): a program that isn't found or can't be started is `NotStarted` with the system's reason; a request with no program, a timeout that isn't positive or a negative cap is refused as `NotStarted` naming the field. Done when: tests read `NotStarted` for a missing program and for each bad request, with no child started, and `Exited` with its code for a child that exits 3.
- [x] Add timeout and cancellation with tree kill: the child is created in a job object (`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`) through `CreateProcessW`'s start-up attributes, so it and everything it starts are in the job from their first instruction; the request's `Timeout` (default 30 s) or the caller's token terminates the job, `RunAsync` returns `TimedOut` or `Cancelled` with what was read so far, and the job is closed when the call returns however it ends, so nothing the child started is left running. A job that can't be made, or a child that can't be created in it, is `NotStarted` with the system's reason, every handle closed and no process left. Done when: a `tree` child cancelled after 300 ms returns `Cancelled` within a second, its grandchild's file has its process id and at least one line (it ran) and then stops growing, and that process is gone; the same with a 300 ms timeout returns `TimedOut`; an `orphan` child, whose grandchild is started and orphaned at once, returns `Exited` and that grandchild is gone and its file no longer grows; and each grandchild is also ended by the test's own teardown. Cheaper substitute: abandoning the process and returning.
- [x] Give `bosun config set` and `unset --json`, and the refusals of `config get --json`, a `schema_version` in `src/app.cpp` (`config_reply`): `{"schema_version":1,"ok":...}`. Done when: `tests/queue_test.ps1` reads `schema_version` 1 on a saved value, a refused one and an unknown key.
- [x] Add `RunJsonAsync<T>` refusing an unknown `schema_version` (`JsonResult<T>`: `Ok` with the value, `Unsupported` with the version found and the one supported, `Invalid` with why, `Failed`, each carrying the `ProcessResult`, so the stderr of a call that succeeded is kept: `bosun` exits 0 while warning there that it ignored a damaged config file or a bad variable, and the caller shows that as a WARN line in the Activity pane of `D02 T02 §4`): the process must exit 0 with untruncated stdout that is one JSON object whose `schema_version` is the integer the caller supports, read through the caller's `JsonTypeInfo<T>`. Done when: a newer schema gives a typed `Unsupported` result, not an exception or defaults, and so does an older one; text that isn't JSON, a JSON array, an object with no `schema_version` or a string one, an object the type can't be read from, and truncated output are each `Invalid` with a reason; a nonzero exit, a timeout, a cancel and a missing program are each `Failed` carrying the result, stderr included; the supported version reads the value; a child that prints valid JSON and a warning on stderr is `Ok` with that warning in its `Process.StandardError`; and, against the real `build\Release\bosun.exe` with `LOCALAPPDATA` set by the request to a folder of the test's own, `config set slots 3 --json` is `Ok`, `config get --json` then reads `slots` saved as 3, and `config set slots 0 --json` is `Failed` with exit 64. Cheaper substitute: deserialising straight into the type, where a missing field becomes a default.
- [x] Prove the checks can fail: with the job left out of the start-up attributes, the `orphan` and `tree` tests fail (their grandchildren, bounded and ended by the tests, write on); with the cap removed, the flood test fails. Done when: both mutations are run and recorded in the commit body.
- [ ] Commit: `core: process runner`

**Test checkpoint:** `bosun run -- dotnet test --solution Bosun.slnx --filter-class "*ProcessRunner*"` exits 0, and fails if the change is reverted; `pwsh tests/queue_test.ps1 -SkipWsl` prints `all passed`.

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

1. Build `src/Bosun.Panel/Shell/MainWindow.xaml` per `design/SPEC.md` §1 (**Corrected 2026-11-13:** in `Shell/`, where `TODO-03` §2 and §9 extend it beside the other shell parts; this file had named it with no folder). Done when: the off-screen render matches the mockup's layout in both themes.
2. Handle `WM_NCHITTEST`. Done when: hovering maximise reports `HTMAXBUTTON`.

- [ ] Build the title bar, rail and activity pane with the `Section` record from `D:\Steward\src\Steward.Console\ViewModels\MainViewModel.cs:112`. Done when: adding a page is one row, and pages bind through implicit `DataTemplate`s. Cheaper substitute: a static screenshot-like layout with no navigation.
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
2. Port `D:\Resolute\scripts\capture-window.ps1`. Done when: it captures a real window with its sidecar.

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
