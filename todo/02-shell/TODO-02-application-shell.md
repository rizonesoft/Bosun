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
| 3 | §3 | Process runner | D00 T02 §1 | [x] |
| 4 | §4 | Window chrome, rail and activity pane | §2, §3, §9 | [ ] |
| 5 | §5 | Settings store and theme switching | §4 | [ ] |
| 6 | §7 | Tray icon and notifications | §5, D02 T01 §3 | [ ] |
| 7 | §8 | Pollers that stop when nothing is looking | §3 | [ ] |
| 8 | §9 | Off-screen page rendering for review | D02 T01 §1 | [x] |
| 9 | §10 | End-to-end smoke through UI Automation | §4, §9 | [ ] |

## 1. Startup, single instance and crash handling

Steward has neither single-instance nor crash handling, which the review of its console identified as the main thing not to copy.

**Phase:** 2
**Needs:** none
**Surface:** no

**Corrected 2026-11-15:** checked against the tree before the build. What the section said before is in its three items (a named mutex and activation pipe, the three handlers, the manifest); they are split and made exact below. The facts and the decisions, each with what reversing it costs:

- There is no window yet (`MainWindow` is `D02 T02 §4`'s), so "the first window comes to the front" can't be shown here: the first instance raises `SingleInstance.Activated` when a second launch asks, `App` answers it by showing, restoring and activating `MainWindow` when there is one, and the window's side (a minimised or hidden window comes back) is an item added to `D02 T02 §4` on 2026-11-15, with the two real processes in `D02 T02 §10`.
- One panel per user and logon session: the mutex is `Local\Bosun.Panel.<name>` and the pipe `Bosun.Panel.<name>.<session id>`, opened for the current user only (`PipeOptions.CurrentUserOnly`), where `<name>` is the user's SID. The environment variable `BOSUN_PANEL_INSTANCE`, when set, replaces the SID, so a test (or a second panel run from a checkout) never meets the operator's own running panel. Reversal cost: none.
- The first instance is whoever created the mutex, never whoever owns it: a panel that crashed holds no handle, so it never blocks the next one, and nothing waits on an abandoned mutex.
- The second launch sends the one word `activate` and exits 0. Before it sends, it lets the first take the foreground (`AllowSetForegroundWindow` with the pipe server's process id), since Windows refuses a background process that. If the first does not answer in 5 seconds (it is hung, or was just exiting), the second tries once more to be the first, and otherwise exits 3 having started nothing: two panels polling at once is the failure this section exists to prevent. Reversal cost: none.
- The pipe's reader is hostile-input code: it reads at most 64 bytes, for at most 2 seconds, acts only on `activate`, and a client that sends nothing, too much or anything else is dropped without the listener stopping.
- A crash report is text in the crash folder (`%LOCALAPPDATA%\Bosun\crash\`, or the folder the environment variable `BOSUN_PANEL_DATA` names, with `crash` under it, for tests): the time, the panel's version, the Windows and .NET versions, which handler caught it, and the exception with its inner exceptions and stacks. It never holds the environment, the command line or a user's name: the user's profile path, user name and the machine's name are replaced wherever they appear (a path in an exception message carries them). It is written temporary-file-then-replace, capped at 256 KB, and the folder keeps the newest 20 reports, so a crash loop can't fill the disk.
- After an exception on the UI thread the panel's state is unknown, and it can change the machine: it writes the report, says so in a message that names the report and offers to start the panel again, and exits with code 1. It does not carry on. An unobserved task exception is reported and observed, and the panel carries on (nothing on screen depends on a task nobody awaited). An exception on any other thread ends the process, as .NET does; the report is written first. Reversal cost: one handler.
- Steward's manifest asks for `requireAdministrator` (`app.manifest:12`), which the panel must not copy (`docs/specs/2026-11-07-control-panel-design.md:48`); its DPI lines are `:27-28`. The panel's also names Windows 10 and 11 as supported and the version 6 common controls, so the crash message is drawn as Windows 11 draws a message box.
- The checkpoint named `dotnet test Bosun.slnx --filter FullyQualifiedName~Startup`, which the test platform here does not take: it is `--solution Bosun.slnx --filter-class`.

**Build order:**

1. Create `src/Bosun.Panel/Startup/SingleInstance.cs` and call it from `src/Bosun.Panel/App.xaml.cs` (**Corrected 2026-11-13:** it exists, an empty `App` class from `D00 T02 §1`, beside `App.xaml`, `Foundation.xaml` and `FoundationText.cs`). Done when: a second launch activates the first and exits.
2. Create `src/Bosun.Panel/Startup/CrashReports.cs` and `CrashHandlers.cs`. Done when: a thrown exception writes a report and shows a message.
3. Create `src/Bosun.Panel/app.manifest`. Done when: the built `Bosun.Panel.exe` carries it.

- [ ] Create `src/Bosun.Panel/Startup/SingleInstance.cs` (`SingleInstance.Start(name)`), returning a typed result: `First` (it created the mutex and now listens), `Forwarded` (another instance holds it and took the request) or `NotAnswering` (another holds it and did not answer in the time given, tried twice). Done when: `tests/Bosun.Panel.Tests/StartupTests.cs` starts two with one name and reads `First` then `Forwarded`, with the first's `Activated` raised once; and after the first is disposed, a third is `First`. Cheaper substitute: two panels polling at once.
- [ ] Listen on the activation pipe in `SingleInstance.cs`, for the current user only, reading at most 64 bytes for at most 2 seconds and acting only on `activate`. Done when: a test client that connects and sends nothing, one that sends 1 MB, and one that sends another word each raise nothing, and an `activate` sent after each of them still raises `Activated`. Cheaper substitute: a reader that waits for ever on a silent client.
- [ ] Return `NotAnswering` when the holder of the mutex has no listener, in `SingleInstance.cs`. Done when: a test holds the mutex with no pipe and reads `NotAnswering` within the time it gave (300 ms, twice), with nothing left listening.
- [ ] Derive the instance name in `src/Bosun.Panel/Startup/InstanceName.cs`: the user's SID, or `BOSUN_PANEL_INSTANCE` when set, refusing a value that isn't 1 to 64 letters, digits, dots or hyphens (it becomes an object name). Done when: a test reads the SID with the variable unset, the variable's value when set, and the refusal for a value with a backslash.
- [ ] Start through `SingleInstance` in `src/Bosun.Panel/App.xaml.cs` (`OnStartup`): a second launch lets the first take the foreground, forwards and exits 0 before any window is made; `NotAnswering` exits 3; the first keeps the instance until `OnExit` disposes it and answers `Activated` on the UI thread by showing, restoring and activating `MainWindow` when there is one. Done when: with a `SingleInstance` held by the test under a name of its own, the real `Bosun.Panel.exe` started with `BOSUN_PANEL_INSTANCE` set to that name exits 0 within 10 seconds, the test's `Activated` is raised, and no window was made (the process had none when it exited); the test ends the process tree if it outlives the limit. Cheaper substitute: a second panel that exits without telling the first.
- [ ] Create `src/Bosun.Panel/Startup/CrashReports.cs` (`CrashReports.Write(folder, exception, source)`), returning the report's path or why it couldn't be written, never throwing: the fields above, written temporary-file-then-replace as `crash-<UTC yyyyMMdd-HHmmss>-<process id>-<n>.txt`. Done when: a test writes one for an exception with an inner exception and reads the time, the version, the source, both types, both messages and a stack frame back from the file, with no `.tmp` left; and a folder that can't be made (a file stands where it should be) returns the reason and throws nothing. Cheaper substitute: swallowing the exception and carrying on silently.
- [ ] Keep secrets out of a report in `CrashReports.cs`: the user's profile path, user name and the machine's name are replaced (`%USERPROFILE%`, `<user>`, `<machine>`), and the environment and command line are never written. Done when: a test writes a report for an exception whose message and stack hold the profile path, the user name and the machine's name, sets a marker environment variable, and finds none of the four in the file.
- [ ] Bound the reports in `CrashReports.cs`: one report is at most 256 KB, cut with a line saying so, and after a write the folder holds the newest 20 reports and nothing else of its own is removed. Done when: a test writes a report for an exception with a 1 MB message and reads a file of at most 256 KB ending in the cut line; and after 25 writes finds 20 reports, the newest, with a `notes.txt` beside them untouched.
- [ ] Create `src/Bosun.Panel/Startup/CrashHandlers.cs` (`CrashHandlers.Attach`), handling `Dispatcher.UnhandledException`, `AppDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException`: each writes a report; the dispatcher's marks the exception handled, tells the person through the `notify` it was given and then asks for exit code 1 through the `exit` it was given; the task's observes the exception and carries on; `Detach` removes all three. Done when: a test throws on a dispatcher of its own and reads a report with source `UI thread`, one notice naming that report's path and one exit of 1; lets a faulted task go unobserved through a collection and reads a report with source `Task` and no notice or exit; raises the domain's handler with an exception and reads a report with source `Thread`; and after `Detach` a throw on the dispatcher writes nothing. Cheaper substitute: swallowing the exception and carrying on silently.
- [ ] Attach the handlers in `App.xaml.cs` (the constructor, before anything else can throw), with the crash folder from `src/Bosun.Panel/Startup/PanelPaths.cs` (`%LOCALAPPDATA%\Bosun`, or `BOSUN_PANEL_DATA`), a message box as the notice (the report's path, and Yes to start the panel again, No to close) and `Shutdown(1)` as the exit, the instance released before a restart so the new panel is the first. Done when: a test reads `PanelPaths.Crash()` under `BOSUN_PANEL_DATA` when set and under the local application data folder when not, and the notice's words name the report's path. Cheaper substitute: a panel that carries on in an unknown state.
- [ ] Create `src/Bosun.Panel/app.manifest` with per-monitor v2 DPI awareness, `asInvoker`, Windows 10 and 11 as supported and the version 6 common controls, citing `D:\Steward\src\Steward.Console\app.manifest:27-28`, and name it as `ApplicationManifest` in `src/Bosun.Panel/Bosun.Panel.csproj`. Done when: a test reads `asInvoker` (and no `requireAdministrator`) and `permonitorv2` from the file, and reads the same two from the manifest resource inside the built `Bosun.Panel.exe`. Cheaper substitute: copying `requireAdministrator`.
- [ ] Commit: `shell: single instance and crash reports`

**Test checkpoint:** `bosun run -- dotnet test --solution Bosun.slnx --filter-class "*Startup*" --filter-class "*Crash*"` exits 0, and fails if the change is reverted.

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
- [x] Commit: `core: process runner`

**Test checkpoint:** `bosun run -- dotnet test --solution Bosun.slnx --filter-class "*ProcessRunner*"` exits 0, and fails if the change is reverted; `pwsh tests/queue_test.ps1 -SkipWsl` prints `all passed`.

> **Verified:** 2026-11-13 | §3 | 72c10ee1645bf422e233ad7ba360a6ddfb27fb77
> **Review:** `docs/reviews/d02-t02-s3.json`

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
- [ ] Add the rail row's data template for `Section` in `src/Bosun.Panel/Shell/MainWindow.xaml`, shown by the `NavList` style of `src/Bosun.Panel/Theme/Controls.xaml` (**Added 2026-11-15** from `D02 T01 §4`, which styles the row and shows its content, but has no `Section` to bind): the page's title, with the slot `D02 T03 §3` fills with the glyph and the count badge. Done when: the off-screen render of the rail shows each page's title, the selected one in `B.Accent` beside its marker bar. Cheaper substitute: rows drawn by `ToString`.
- [ ] Return `HTMAXBUTTON` from a `WM_NCHITTEST` hook. Done when: a test with synthetic coordinates gets the right code. Cheaper substitute: custom caption buttons without snap layouts.
- [ ] Add placeholder pages for every rail entry, each deferring to its owning section. Done when: each rail row opens a page that names its owner section, and none is a dead control.
- [ ] Create `src/Bosun.Panel/Shell/PageFrame.xaml` and `PageBase.cs`, holding the header and the content host. Done when: a page renders through them. Cheaper substitute: each view drawing its own header.
- [ ] Create `src/Bosun.Panel/Shell/ActivityLog.cs` over `UiLogSink`, with each line starting INFO, WARN or FAIL. Done when: a test with no prefix given refuses the line.
- [ ] Give the `LogBox` style of `src/Bosun.Panel/Theme/Controls.xaml` its entry template for `ActivityLog`'s entry type (**Added 2026-11-15** from `D02 T01 §4`, which ports the list and its row style but has no entry type to bind): the time in `B.TextMute`, the word INFO, WARN or FAIL (WARN in `B.Warn`, FAIL in `B.Danger`) and the message, each in `T.Mono`, after `D:\Steward\src\Steward.Console\Theme\Controls.xaml:113`. Done when: the off-screen render of the pane shows a WARN line with its word, in both themes. Cheaper substitute: the state told by colour alone, with no word.
- [ ] Log every panel action through `AsyncCommand` to `ActivityLog.cs`. Done when: a test runs a command and finds one line, and a view can't add a command without it. Cheaper substitute: each view logging by hand.
- [ ] Set the window's `FontFamily` to `F.Ui`, its `FontSize` to `T.Body` and its `Foreground` to `B.Text` in `MainWindow.xaml` (**Added 2026-11-15** from the plan review of `D02 T01 §4`, which ports no implicit `TextBlock` style, so that a control's own colour reaches its words): every text inherits them. Done when: a test reads all three on an unstyled `TextBlock` in a page, and the brush follows a theme swap. Cheaper substitute: a font and colour typed into each view.
- [ ] Bring the window forward when `SingleInstance.Activated` is raised, in `src/Bosun.Panel/App.xaml.cs` and `MainWindow.xaml.cs` (**Added 2026-11-15** from `D02 T02 §1`, which raises the event but has no window): a minimised window is restored, a hidden one shown, and it is activated. Done when: a test minimises the window, raises the event and reads the window's state as normal and visible. Cheaper substitute: a second launch that seems to do nothing.
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

> **Started:** 2026-11-13T21:07:33Z

A surface section has to be provable in an unattended run with no visible desktop, or surfaces would never get reviewed. This renders real views to PNG, in both themes.

**Corrected 2026-11-13:** checked against the tree before the build. The panel has no view yet: `src/Bosun.Panel` holds `App.xaml`, `Foundation.xaml` and the generated `Theme/Generated/Dark.xaml` and `Light.xaml` (`D02 T01 §1`, stamped), so the catalogue starts with one sample view of the tests' own and each surface section adds its views. `tests/Bosun.Panel.Tests` already runs WPF work on an STA thread in two places (`FoundationTests.cs`, `ThemeTests.cs`), each with its own copy of the helper: the renderer brings one shared helper and both use it. `D:\Resolute\scripts\capture-window.ps1` is 880 lines and already does what the port needs (it sends no input, refuses with exit 3 when the target doesn't own the foreground or is covered, stops only what it started, writes the PNG and its sidecar as one verified pair, and checks itself without a desktop through `-SelfTest`): the port keeps all of that and changes only what names Resolute. The checkpoint's filter is the Microsoft.Testing.Platform form the repo uses (`--filter-class`). Decided here (each a small change to reverse): a render is the view's own tree, measured, arranged and drawn at 96 DPI whatever the machine's scaling, so two machines give the same size; a `Window` can't be drawn without being shown, so a window registers its content and its chrome is proved by its own section's tests (`D02 T02 §4`) and by an attended capture; the renders are written by the test itself, since `build/` is already out of Git and the test is what proves them; and each run empties `build/renders/` of the PNGs and index it owns first, so a view that was removed leaves no stale picture for a reviewer to read.

**Corrected 2026-11-13 (plan review):** the plan review in `docs/reviews/02-shell/D02-T02-s9.md` returned six gaps (3 P1, 3 P2), planned in below. The theme's brush is `B.Surface` (`Surface` is the token's name in the JSON). A tree that hangs from nothing never gets `Loaded` and can't be told from one that is still loading, so the renderer gives each view a presentation source that is never shown (an `HwndSource` with no visible style) and a readiness contract with a time limit. Two states that draw the same picture prove nothing, so states must differ and each must show its own content. `scripts/render-pages.ps1` clears the old evidence before it builds and the index is written last, so a failed run leaves nothing that reads as a current one. The port corrects three things the source gets wrong about what it started (it picks new processes by name, learns what is its own only once a window appears, and stops one process, not its tree). And a real window capture needs a desktop, which this section does not have: that proof is owned by `D02 T02 §10`.

**Corrected 2026-11-13 (build):** found while building, and built as corrected. `PageRenderer.Render` takes the `RenderCase` (not an element), since the view has to be made on the thread that draws it. The picture is the view on the theme's `B.Base`, as a page sits in the window. The catalogue's `Clear` removes the index first, so a picture that can't be removed never leaves a folder that reads as a whole run. The port gained `-NoNewWindow` (a console program started in this console, which is how the self-test launches without opening a window), and its help says that `-LaunchArgs` starting with a dash are given as `-LaunchArgs:'...'`, which PowerShell would otherwise read as a parameter. A process list that can't be read stops the capture, since what the launch started would be unknown. An error or Ctrl+C after the launch stops what was launched too. The two new tests are in `.github/workflows/build.yml`, as `tests/check_all_test.ps1` requires of every PowerShell test.

**Corrected 2026-11-14 (completion review):** the completion review in `docs/reviews/02-shell/D02-T02-s9.md` returned three gaps in scope, each fixed. A view that blocks the thread it is drawn on stopped the readiness limit's own clock, so a render is also timed from outside that thread (`RenderCase.DrawLimit`, 30 s on top of `ReadyLimit`) and given up naming the view. A picture that is not a render sitting in the folder failed the run, and a run that failed after its index was written left that index: the run counts only its own files (`RenderCatalog.Owns`) and `scripts/render-pages.ps1` removes the index of a failed run. And the port no longer learns what it started from snapshots of parent process ids, which lose a descendant whose parent lived between two of them: the launch is made suspended inside a Windows job object, so everything it starts is in the job from its first instruction and is stopped with it (this replaces `taskkill /T /F` and the parent id and start time rule in the item below). Found on the way: the self-test's handle `no window has` was a fixed number that was a real window's on this day, so it now asks Windows for one.

**Phase:** 2
**Needs:** none
**Surface:** no

**Build order:**

1. Create `tests/Bosun.Panel.Tests/Render/PageRenderer.cs`. Done when: any WPF element renders to PNG through `RenderTargetBitmap` on a hidden STA thread.
2. Port `D:\Resolute\scripts\capture-window.ps1`. Done when: it captures a real window with its sidecar.

- [x] Share one STA helper, `tests/Bosun.Panel.Tests/Sta.cs` (`Sta.Run`), and use it in `FoundationTests.cs` and `ThemeTests.cs` in place of their own copies. Done when: both files' tests pass through it, and an exception thrown on the STA thread reaches the test with its own message.
- [x] Create `tests/Bosun.Panel.Tests/Render/PageRenderer.cs` (`PageRenderer.Render(element, theme, width, height)`): on the STA thread it builds the view from its factory (so the view is made on the thread that draws it), merges the named generated theme into its resources, roots it in an `HwndSource` that is never shown (no `WS_VISIBLE`, so `Loaded` fires and bindings run as they do in the app, and nothing appears on screen), measures and arranges it at the size given, waits until it is ready, and draws it with `RenderTargetBitmap` at 96 DPI into PNG bytes. Ready means: the dispatcher has run to idle, and, for a view that loads something, the task its `RenderCase` names (`Ready`) has completed; a `Ready` task that fails, or is not done within the case's limit (5 s unless the case says otherwise), fails the render naming the view. Done when: a test renders a sample element in `Dark` and in `Light` at 1360 x 860 and reads back a PNG of exactly that size, whose pixel inside a panel painted with `{DynamicResource B.Surface}` is that theme's `Surface` from `design/tokens/colors.json` and whose text pixels differ from it, and the two themes' images differ; a view whose text is set in its `Loaded` handler through a binding shows that text in the picture; a view that fills itself 200 ms after `Loaded` is drawn filled when its case names that task, a `Ready` task that throws fails the render with the view's name and the task's own exception, and one that never completes fails after its limit, saying so. Cheaper substitute: golden pixel comparisons that break on font updates.
- [x] Refuse what can't be a picture, by name: an element that renders with no size, a size that isn't positive, a theme that isn't `Dark` or `Light`, a `Window` (register its content), and an image that is one flat colour. Done when: tests read an exception naming the view and the reason for each, and a view whose constructor throws is reported with its name and the original exception.
- [x] Add the catalogue, `tests/Bosun.Panel.Tests/Render/RenderCatalog.cs` (`RenderCase`: view name, state name, a factory): one row per view and state, with the sample view registered as `sample` in states `normal` and `empty`, each state binding the view to data of its own (rows in `normal`, the words `Nothing here yet` in `empty`) and naming a text its picture must contain (`RenderCase.Shows`, looked for in the rendered tree). Done when: a test fails when two rows share a view and state, or a name holds anything but lower-case letters, digits and hyphens (the names become file names); each sample state's rendered tree holds its own text and not the other's; and a catalogue whose two states of one view draw the same picture, or whose state doesn't show the text it names, is refused naming the view and both states (tested with a catalogue that gives `empty` the `normal` factory).
- [x] Render every view registered with the renderer, in each state at 1360 x 860 and in both themes, to `build/renders/` as `<view>.<state>.<theme>.png`, each written to a temporary file and then moved into place, with `build/renders/index.json` listing every file with its view, state, theme, size and the commit and time of the run, written last and only when every render succeeded (the folder is `build/renders/` unless `BOSUN_RENDER_DIR` names another, which the tests use). Views register as their sections land. Done when: a test with a sample element asserts the image is not blank and has the expected size, the folder holds exactly the files the catalogue gives (a PNG left from an earlier run is gone, and a file that isn't a render is left alone), and the index names each of them; and when one render fails or one file can't be written (tested with a destination held open), the run fails naming it and leaves no `index.json`, the old one included. Cheaper substitute: golden pixel comparisons that break on font updates.
- [x] Add `scripts/render-pages.ps1` to write all renders: it first removes the renders and index a previous run left (so nothing old can be read as current), builds the test project and stops with the build's exit code when that fails, runs the render test, and prints the folder and the count of files written only when `index.json` is there, exiting with the test's code. Done when: `tests/render_pages_test.ps1` plants an old PNG and index, runs the script against a project that doesn't build, and reads a nonzero exit, no index and no old PNG; the review prompt can name the folder (`docs/review-round-prompt.md` and `docs/completion-review-prompt.md` say that a `Surface: yes` section's renders are in `build/renders/`, written by `pwsh scripts/render-pages.ps1`), renders stay out of Git (`git status --short build/renders` is empty after a run), and `tests/render_pages_test.ps1` runs the script and reads the four sample files and the index.
- [x] Port `scripts/capture-window.ps1` for attended checks, from `D:\Resolute\scripts\capture-window.ps1`: every rule kept (no input sent, exit 3 when the target isn't in the foreground or is covered, only what it started is stopped, the PNG and its sidecar written as one verified pair, `-Verify`, `-SelfTest`), with Resolute's section references, example paths and names replaced by Bosun's, and three corrections to what it started: its own processes are the one it launched and that one's descendants (by parent process id and start time), never a process that merely has the same name and appeared meanwhile; they are known from the moment of the launch, so a launch that never shows a window is stopped too; and they are stopped as a tree (`taskkill /T /F`). Done when: it fails closed when the target isn't in the foreground, with exit 3 (the self-test's covered-window rule; the capture of a real window is proved in a desktop session by `D02 T02 §10`); the self-test, with console children started without a window, launches a program that never shows a window and reads exit 1 with that program and a child it started both gone, while a process of the same name started before, and one started independently during the wait, are both still running; `pwsh scripts/capture-window.ps1 -SelfTest` prints `self-test: all pass`; `tests/capture_window_test.ps1` runs that self-test in the gate; and `git grep -n -i resolute scripts/capture-window.ps1` finds nothing.
- [x] Say in `AGENTS.md` how a surface is looked at: `pwsh scripts/render-pages.ps1` for the unattended renders, `pwsh scripts/capture-window.ps1` for an attended capture of the real window. Done when: both commands are there with one line each.
- [x] Commit: `tests: off-screen page renders`

> **Deferred:** capturing a real window (the PNG decodes at the window's size, `-Verify` accepts the pair, a covered or background target is refused with exit 3) needs a desktop session -> XREF: D02 T02 §10

**Test checkpoint:** `bosun run -- dotnet test --solution Bosun.slnx --filter-class "*PageRenderer*"` exits 0, and fails if the change is reverted; `pwsh tests/render_pages_test.ps1` and `pwsh tests/capture_window_test.ps1` each print `all passed`.

> **Verified:** 2026-11-14 | §9 | dc66a7839b9d43d10059a530816c128368c6274c
> **Review:** `docs/reviews/d02-t02-s9.json`

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
- [ ] Capture the running panel's main window with `scripts/capture-window.ps1` (handed over by `D02 T02 §9`, which can only self-test it without a desktop). Done when: the PNG decodes at the window's size, `pwsh scripts/capture-window.ps1 -Verify <png>` accepts the pair, and with another window over the panel, and with the panel in the background, the capture is refused with exit 3 and writes nothing.
- [ ] Tab through the shown window and open a combo box in the smoke (**Added 2026-11-15** from the plan review of `D02 T01 §4`: an off-screen test can neither move focus with a real key nor open a drop-down, which is a window of its own). Done when: after a Tab key the focused element reports keyboard focus and its focus ring is on screen, the Space key toggles a focused check box and the Down key selects a closed combo box's next item (**Added 2026-11-15:** an off-screen test can't prove either, since a button reads the real mouse's left button before Space clicks it and a combo box reads the real Alt key with Down), and a combo box expanded through UI Automation lists its items and collapses on Esc. Cheaper substitute: trusting the styles' setters.
- [ ] Start the panel twice in the smoke (**Added 2026-11-15** from `D02 T02 §1`, which proves the second launch against an instance the test holds, with no window to bring forward). Done when: with the first panel's window minimised, a second `Bosun.Panel.exe` exits 0 and the first's window is restored and in front, and only one panel process remains. Cheaper substitute: asserting the exit code alone.
- [ ] Commit: `tests: end-to-end smoke`

**Test checkpoint:** `bosun run -- dotnet test tests/Bosun.Panel.E2E` exits 0 in a desktop session.

## Verification

- The end-to-end smoke passes in a desktop session.
- `dotnet test Bosun.slnx` passes.
