<div align="center">

<img src="design/icon/bosun-256.svg" alt="Bosun icon" width="112">

# Bosun

**Run more repos on the same RAM. Heavy jobs take turns across Windows and WSL, and a logger records what each repo really uses.**

[![Build](https://github.com/rizonetech/Bosun/actions/workflows/build.yml/badge.svg?branch=main)](https://github.com/rizonetech/Bosun/actions/workflows/build.yml)
[![Plan](https://github.com/rizonetech/Bosun/actions/workflows/plan.yml/badge.svg?branch=main)](https://github.com/rizonetech/Bosun/actions/workflows/plan.yml)
[![License: GPL-3.0-or-later](https://img.shields.io/badge/license-GPL--3.0--or--later-35D6A6)](LICENSE)
![Platform: Windows 11 and WSL 2](https://img.shields.io/badge/platform-Windows%2011%20%7C%20WSL%202-0F1319)
![C++20](https://img.shields.io/badge/C%2B%2B-20-6EA8FF)
![Status: pre-release](https://img.shields.io/badge/status-pre--release-FFB547)

[Quick start](#how-do-i-build-and-install-it) · [Usage](#how-do-i-use-it) · [Panel preview](#what-is-being-built-next) · [Contributing](CONTRIBUTING.md) · [Security](SECURITY.md)

</div>

## Why does Bosun exist?

Several repos and agent sessions can share a machine until their builds overlap. Then Windows and the WSL VM compete for the same memory, and everything stalls. Bosun makes the heavy commands queue across both sides, without closing a session or an editor.

- **One queue for two operating systems:** `bosun run` works the same on Windows and in WSL, and both sides share the same slots.
- **Admission by real memory:** a second job starts only when there is room for it.
- **Crash safe:** slots are held by operating system objects, so a crashed job frees its slot at once.
- **Evidence before hardware:** the logger shows which repo used what, so a decision like buying more RAM rests on measured peaks.
- **Light and private:** two small native tools with no runtime to install, no admin rights and no network use.

## What works today?

| Part | State |
| ---- | ----- |
| `bosun run`, `status`, `report` and the login logger | Working on Windows 11 and WSL 2 |
| WSL shim and `bosun-log` user service | Working |
| One heartbeat for unattended runs, on both sides | Working |
| Bosun Panel, the desktop control panel | Designed and planned, not built yet |

There are no releases yet, so you build from source.

## How do I use it?

Put `bosun run --` in front of heavy commands, on Windows or in WSL:

```
bosun run -- npm test
bosun run -- msbuild App.sln /m:4
bosun status
bosun report --days 7
```

Sessions and editors stay open as normal. Only commands wrapped in `bosun run` queue. Do not wrap dev servers or watchers, because they would hold a slot for as long as they run.

## How does the queue decide?

- There are 2 slots by default, shared by Windows and WSL.
- The first slot always admits a job. The second needs 6 GB available on Windows, and in WSL also 3 GB available inside the VM.
- After 15 minutes a waiting job starts anyway, with a warning.
- A crashed job frees its slot immediately.

Change the defaults with `--slots`, `--min-avail-mb` and `--max-wait`, or `BOSUN_SLOTS`, `BOSUN_MIN_AVAIL_MB` and `BOSUN_MAX_WAIT`. In WSL, `--min-guest-mb` or `BOSUN_MIN_GUEST_MB` sets the VM threshold.

## What stops a job from filling the disk?

A job and everything it starts end together, and together they may write at most 100 GB. A job over that budget is stopped, children and all, and `bosun run` exits 74 and says why. A command that writes without end, such as a test whose fake child floods a log, would otherwise fill the drive, and a full system drive takes Windows and the WSL disk image on it down with it. Change the budget with `--max-write-gb` or `BOSUN_MAX_WRITE_GB`, and 0 turns it off.

- **Windows:** the job object that holds the command counts what every process in it writes, and closing it ends anything the command left behind.
- **WSL:** the command runs in its own systemd scope, and anything it leaves behind is stopped when it ends. systemd gives user jobs the `io` controller only through a root setting, so turn the budget on once with `sudo ~/.local/share/bosun/enable-write-budget.sh`, then `wsl --shutdown`. Until then the budget is off in WSL, and `bosun run` says so once per boot.

## How do unattended runs stay alive?

One heartbeat runs them all. A scheduled task ticks each registered repo's run guard every 5 minutes, resuming at most one repo per pass and holding while memory is short, for at most an hour. It never starts a stopped WSL distro.

```
bosun-heartbeat add D:\GitHub\MyRepo
bosun-heartbeat add ~/www/site --distro Ubuntu
bosun-heartbeat status
bosun-heartbeat remove D:\GitHub\MyRepo
```

A repo needs the run guard, `scripts/run-guard.py`, to join. Adding it removes that repo's own watchdog task, and removing it brings that back, so a repo still works without Bosun. Set `heartbeat_floor_mb` in `%LOCALAPPDATA%\Bosun\config.json` to change the memory floor from its default of 2048 MB.

## Where are the logs?

`%LOCALAPPDATA%\Bosun\logs`, one CSV per side per day, sampled every 30 seconds and kept for 14 days. A process is credited to the git repo holding its working directory, otherwise to its parent's repo.

## How do I build and install it?

Needs Visual Studio with the C++ tools and CMake 3.25 or later. The WSL side expects an `Ubuntu` distro, and `-Distro` picks another. No admin rights are needed.

```
cmake -S . -B build
cmake --build build --config Release
pwsh scripts/install.ps1
```

A Release build copies only `bosun.exe` and `bosun-logger.exe` to `bin/`, and everything else, including test binaries and PDBs, stays in `build/`. Both folders are ignored by Git.

The installer takes the tools from `bin/` and copies them to `%LOCALAPPDATA%\Bosun\bin`, adds that folder to your user PATH, starts the logger at login and sets up the `bosun-log` user service in WSL.

## How do I test it?

```
build\Release\test_core.exe
pwsh tests\queue_test.ps1
pwsh tests\stage_wsl_test.ps1
pwsh tests\bin_output_test.ps1
pwsh tests\heartbeat_test.ps1
wsl -- python3 -B tests/test_bosun_log.py
wsl -- bash wsl/queue_test.sh /mnt/d/GitHub/Bosun/build/Release/bosun.exe
```

The queue tests use a private slot namespace, so they do not affect real jobs. On a machine without WSL, `tests\queue_test.ps1 -SkipWsl` leaves out the WSL case, as CI does.

The WSL commands read the repo through `/mnt/d`, which WSL mounts when it starts. If it is missing after adding the drive, restart WSL or run `sudo mkdir -p /mnt/d && sudo mount -t drvfs D: /mnt/d`. `pwsh scripts/install.ps1` does not need it.

The design is in `docs/specs/2026-11-07-bosun-design.md`.

## What is being built next?

Bosun Panel, a .NET 11 WPF control panel for memory, dev instances, the queue, plans, reviews, unattended runs, agent updates, machine tuning and extra development on your own Anthropic and OpenAI API keys. These images come from the design [mockup](design/mockups/control-panel.html), with sample data and a simulated Mica backdrop. The panel itself is not built yet.

![Bosun Panel design preview: the Overview page](docs/images/panel-overview.png)

![Bosun Panel design preview: the Plan page with the repo selector](docs/images/panel-plan.png)

See the [design](docs/specs/2026-11-07-control-panel-design.md), the [interface spec](design/SPEC.md) and the [plan](todo/implementation-plan.md).

## How is the project run?

Work follows a validated TODO graph in [`todo/`](todo/TODO-00-INDEX.md), and changes are recorded in the [changelog](CHANGELOG.md). [CONTRIBUTING.md](CONTRIBUTING.md) explains how to take part, and [SECURITY.md](SECURITY.md) explains how to report a vulnerability privately.

## License

Copyright (C) 2026 Rizonetech (Pty) Ltd. Bosun is free software under the [GNU General Public License, version 3 or later](LICENSE). Third-party components are listed in [NOTICE.md](NOTICE.md).
