# DevMode

DevMode keeps Windows and WSL repos from fighting over the same RAM. Heavy jobs take turns across both sides, and a logger records what each repo really uses.

## How do I use it?

Put `devmode run --` in front of heavy commands, on Windows or in WSL:

```
devmode run -- npm test
devmode run -- msbuild App.sln /m:4
devmode status
devmode report --days 7
```

Sessions and editors stay open as normal. Only commands wrapped in `devmode run` queue. Do not wrap dev servers or watchers, because they would hold a slot for as long as they run.

## How does the queue decide?

- There are 2 slots by default, shared by Windows and WSL.
- The first slot always admits a job. The second needs 6 GB available on Windows, and in WSL also 3 GB available inside the VM.
- After 15 minutes a waiting job starts anyway, with a warning.
- A crashed job frees its slot immediately.

Change the defaults with `--slots`, `--min-avail-mb` and `--max-wait`, or `DEVMODE_SLOTS`, `DEVMODE_MIN_AVAIL_MB` and `DEVMODE_MAX_WAIT`. In WSL, `--min-guest-mb` or `DEVMODE_MIN_GUEST_MB` sets the VM threshold.

## Where are the logs?

`%LOCALAPPDATA%\DevMode\logs`, one CSV per side per day, sampled every 30 seconds and kept for 14 days. A process is credited to the git repo holding its working directory, otherwise to its parent's repo.

## How do I build and install it?

Needs Visual Studio with the C++ tools. No admin rights are needed.

```
cmake -S . -B build
cmake --build build --config Release
pwsh scripts/install.ps1
```

This installs to `%LOCALAPPDATA%\DevMode\bin`, adds that folder to your user PATH, starts the logger at login and sets up the `devmode-log` user service in WSL.

## How do I test it?

```
build\Release\test_core.exe
pwsh tests\queue_test.ps1
wsl -- python3 -B tests/test_devmode_log.py
wsl -- bash wsl/queue_test.sh /mnt/r/GitHub/DevMode/build/Release/devmode.exe
```

The queue tests use a private slot namespace, so they do not affect real jobs.

The design is in `docs/specs/2026-11-07-devmode-design.md`.

## What is being built next?

DevMode Panel, a .NET 11 WPF control panel for memory, dev instances, the queue, plans, reviews, unattended runs, agent updates, machine tuning and extra development on the Claude API key. See the [design](docs/specs/2026-11-07-control-panel-design.md), the [interface spec](design/SPEC.md), the [mockup](design/mockups/control-panel.html) and the [plan](todo/implementation-plan.md).

