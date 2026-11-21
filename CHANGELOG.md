# Changelog

Notable changes to Bosun are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions will follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- `bosun run`: a build queue shared by Windows and WSL, with memory-gated slots.
- `bosun status`, and `bosun report` over the logged memory data.
- The login logger on Windows, and the `bosun-log` user service in WSL.
- `bosun run` write budget: a job and everything it starts may write at most 100 GB (`--max-write-gb`, `BOSUN_MAX_WRITE_GB`), and one over it is stopped with exit 74. In WSL a job runs in its own systemd scope, so what it leaves behind ends with it.
- `bin/`, holding only the shipped tools after a Release build.
- CI for the build, the tests and the TODO graph.
- The Bosun Panel design, interface spec, mockup and implementation plan.

### Changed

- The native tools build with Ninja through `pwsh scripts/build.ps1`, with warnings as errors everywhere and `compile_commands.json` for clangd. `app.cpp` is compiled once for both tools.
- `pwsh scripts/build.ps1` replays the Visual Studio compiler environment from `build/dev-env.json` instead of loading it on every build: a build with nothing to do went from 3.9 s to 1.1 s. It also configures fresh once when the existing CMake cache cannot be configured from.
- `pwsh scripts/measure-build.ps1` measures the build's wall time and peak memory per step and keeps the results in `docs/build-performance.md`. `sccache` and `lld-link` were measured and not kept.
- `python scripts/run-tests.py` runs the tooling's tests several classes at a time.
- `pwsh scripts/start-run.ps1` starts the unattended run with its goal, and `pwsh scripts/setup-lsp.ps1` sets up code intelligence for Claude Code.
- An unattended run starts only when its operator starts it and lives in its own window. Closing the window stops it and everything it started, a crash or a reboot ends it, and nothing restarts or resumes one. The run guard keeps a live session working (the Stop hook, the in-session heartbeat, the waits) and records a run whose session is gone as `ended`.
- `pwsh scripts/start-run.ps1 -InstallShortcut` writes two desktop shortcuts, "Bosun Claude campaign" and "Bosun Carbon campaign", each carrying the Bosun icon with its runner's badge. The Carbon one starts Antigravity on Gemini 4 Carbon.
- The unattended run starts on the newest Opus (`claude --model opus`). The start signs in with the long-lived token from the user's variables, which `pwsh scripts/store-claude-token.ps1 -FromVault` imports once from the 1Password item the private overlay names (`vault_item` in `.private/config.toml`, or `BOSUN_VAULT_ITEM`).
- Renamed the project from DevMode to Bosun.
- Bosun Panel's scope is the product's own data: memory, dev instances, the queue and machine tuning. The planned workflow pages (plan, reviews, unattended run, hooks, fleet), the agents page, the API workbench and Insights are dropped, with every control that acted on another repository.
- The campaign preflight checks free space on the drive the checkout is on, and the deny list's scratch folder comes from `BOSUN_SCRATCH`. No folder is named in code.

### Removed

- `bosun-heartbeat` and the `Bosun heartbeat` task. It took over each registered repo's own watchdog, so a repo that moved was left with none. `scripts/install.ps1` removes what the heartbeat left behind.
- The run watchdog: the scheduled task that resumed a dead session in a hidden window, `run-guard.py watch`, `install` and `status --verify`, the `-Watchdog` option of `scripts/setup-clone.ps1`, and the install step in `scripts/install.ps1`. `python scripts/run-guard.py uninstall` removes a task an older checkout left.
- The `Bosun attach D` startup task and `scripts/attach-vhdx.ps1`, with the volume they attached.

### Fixed

- The review dispatcher's tests stop the whole process tree on a timeout. Python's own timeout killed only the dispatcher, and a fake reviewer flooding stderr kept running until it filled the disk.

- The WSL side now installs from a folder WSL can read, even when it can't see the drive holding the repository.
