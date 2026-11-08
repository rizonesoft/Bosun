# Changelog

Notable changes to Bosun are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions will follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- `bosun run`: a build queue shared by Windows and WSL, with memory-gated slots.
- `bosun status`, and `bosun report` over the logged memory data.
- The login logger on Windows, and the `bosun-log` user service in WSL.
- `bosun-heartbeat`: one scheduled heartbeat for every repo's unattended run.
- `bosun run` write budget: a job and everything it starts may write at most 100 GB (`--max-write-gb`, `BOSUN_MAX_WRITE_GB`), and one over it is stopped with exit 74. In WSL a job runs in its own systemd scope, so what it leaves behind ends with it.
- `bin/`, holding only the shipped tools after a Release build.
- CI for the build, the tests and the TODO graph.
- The Bosun Panel design, interface spec, mockup and implementation plan.

### Changed

- Renamed the project from DevMode to Bosun.

### Fixed

- The review dispatcher's tests stop the whole process tree on a timeout. Python's own timeout killed only the dispatcher, and a fake reviewer flooding stderr kept running until it filled the disk.

- The WSL side now installs from a folder WSL can read, even when it can't see the drive holding the repository.
