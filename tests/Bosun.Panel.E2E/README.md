# The end-to-end smoke

`tests/Bosun.Panel.E2E` drives the real `Bosun.Panel.exe` on the desktop: real windows, real keys and clicks (through `RealInput`, which sends nothing unless the panel is where it means to send it), the real notification area. It needs an unlocked desktop, and it runs with the build:

```powershell
bosun run -- dotnet test --project tests/Bosun.Panel.E2E
```

## Real input is opt-in

That command never takes your mouse or keyboard (D02 T02 §12, the operator's decision of 2026-11-18). The tests that press keys, click, drag, move the pointer or bring a panel to the front (the caption buttons, the splitter, the tray icon, the Tab and shortcut paths, the captures) carry `[RealInputFact]` and `[Trait("Input", "Real")]`, and they are skipped unless `BOSUN_SMOKE_INPUT=1`; `RealInput` and `PanelProcess.Front()` refuse in any other test, and a default test that leaves a panel it started in the foreground fails (`ForegroundKept`). They run through one script:

```powershell
pwsh scripts/smoke-input.ps1             # now: it takes the desk for a few minutes
pwsh scripts/smoke-input.ps1 -Install    # every night at 03:00, on an idle, unlocked desktop
pwsh scripts/smoke-input.ps1 -Status     # the last results, in words
pwsh scripts/smoke-input.ps1 -Uninstall  # no more nightly runs
```

The nightly run starts only on an unlocked desktop nobody has used for 15 minutes, and stops at the first sign of a person (input Windows did not mark as the smoke's own, or the desktop locked) or at 05:00, ending the class in flight. It never wakes the machine, so a night the machine slept or nobody was logged on has no result, which `-Status` says with its date. Every run is kept in `%LOCALAPPDATA%\Bosun\smoke\input-history.jsonl` with its own log.

## Which screen it runs on

Every panel the smoke starts opens on the test screen, never first on the main one, so a run stays off the screen you are working on (D02 T03 §12). The harness writes each panel's `panel.json` with a window place centred on that screen before the panel starts, and `PanelProcess` fails any test whose panel was first shown elsewhere.

The test screen is:

- the screen `BOSUN_TEST_SCREEN` names, as a device name Windows gives it, such as `\\.\DISPLAY3`; a run whose variable names no screen on the machine is refused with the screens there are;
- otherwise the largest screen that is not the main one (the leftmost of equals);
- otherwise, on a machine with one screen, the main one.

To see the device names: `Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.Screen]::AllScreens | Select-Object DeviceName, Primary, Bounds`.

Two things still reach the main screen, because Windows keeps them there: the notification area's icon and its menu (the tray tests), which Windows shows only on the main taskbar; and the keyboard focus and the pointer, which a test takes while it types or clicks into the panel and gives back after.

## One run on the desktop at a time

Inside a run the tests take turns (`TestAssembly.cs` turns parallel runs off). Across processes, a run owns the desktop through the mutex `Local\bosun-desktop` (D02 T02 §11): it takes it before its first test and releases it after its last, so a second smoke run, from another session of yours or another agent, waits for it rather than taking its windows, and so does `scripts/capture-window.ps1`, which takes the same lock for each capture. The lock is per logon session, as the desktop is.

A run that finds the lock taken says whom it waits for and waits up to 15 minutes, then fails before any test runs, naming the holder's pid, the label it gave (`capture-window.ps1`, `Bosun.Panel.E2E smoke`, never its command line) and its start time. The holder's record is `%LOCALAPPDATA%\Bosun\run\<session>\desktop\bosun-desktop.json`, `<session>` being the Windows session number; the mutex is what counts, and a record whose process has ended names no one.

- `BOSUN_DESKTOP_WAIT_S`: how long a second owner waits, a whole number of seconds from 0 to 86400 (0 refuses at once). Unset, 900. Anything else is refused.
- `BOSUN_DESKTOP_HELD`: set by a holder for what it starts (`<name>:<pid>:<process start>`), so the capture a test runs works under the run's hold rather than waiting on its own parent. It counts only for a descendant of that holder, while the lock's record names it.
- `BOSUN_DESKTOP_LOCK`: another lock name (lower-case letters, digits and `-`, at most 64), for tests of the lock itself, which never meet a real run.

The lock's one source is `scripts/lib/DesktopLock.cs`: the smoke compiles it, and `capture-window.ps1` loads it with `Add-Type`.
