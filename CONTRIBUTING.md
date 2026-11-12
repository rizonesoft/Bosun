# Contributing to Bosun

Bosun is built section by section from a validated TODO graph in [`todo/`](todo/TODO-00-INDEX.md). A change should either ship a section or capture new work as a section. A small bug fix with a regression test is also welcome.

## How do I start?

1. Read [AGENTS.md](AGENTS.md) for the project rules. [`design/SPEC.md`](design/SPEC.md) is the interface contract.
2. Run `python scripts/todo-graph.py query ready` to see which work can start now.
3. Set up the clone, once:

```
pwsh scripts/setup-clone.ps1
```

It turns on the repository's git hooks by setting `core.hooksPath` to `scripts/hooks` in the clone's own config, so a commit that would leave the generated plan stale or the TODO graph invalid is refused before it lands. It is safe to run again, and `-Repo <folder>` sets up a clone other than the one the script is in. To undo it: `git config --local --unset core.hooksPath`.

Add `-Watchdog` only on a machine that runs the plan unattended: it installs the scheduled task that resumes a run, and needs Python and Windows Task Scheduler. It installs only when the task is missing, leaves one that already runs this clone alone, and reports one that belongs to another clone or is disabled without replacing it. To undo it: `python scripts/run-guard.py uninstall`. Bosun itself needs neither.

## What do I need?

- Windows 11 with Visual Studio's C++ tools and CMake 3.25 or later.
- WSL 2 with an `Ubuntu` distro for the WSL tests.
- Python 3.14 or later.
- Node.js, for `scripts/verify-agent-tiers.mjs`.

## Which checks must pass?

```
pwsh scripts/build.ps1
build\Release\test_core.exe
pwsh tests\queue_test.ps1
pwsh tests\bin_output_test.ps1
python scripts/todo-graph.py validate
python scripts/todo-graph.py plan --check
python scripts/run-tests.py
node scripts/verify-agent-tiers.mjs
```

The README lists the WSL tests. Run full builds and test suites through `bosun run --` so they queue with everything else on your machine.

`todo/implementation-plan.md` is generated. Edit the TODO sections, then run `python scripts/todo-graph.py plan --sync`.

## What does a good change look like?

- It fixes the cause in the layer that owns the behaviour, not with a filter over the result.
- A bug fix has a regression test that fails before the fix and passes after it.
- Colours, radii, elevation, motion, spacing and type come from `design/tokens/colors.json`.
- A value that can't be read shows "Not available" with a reason, never 0, and status always has a word as well as a colour.
- It never adds a secret, an API key or a personal path.
- It uses no em dashes or en dashes, in prose, code or commit messages.

## How do I report something?

Use the [issue forms](https://github.com/rizonetech/Bosun/issues/new/choose) for bugs and ideas. Security problems go through [SECURITY.md](SECURITY.md), never a public issue.

## Under what licence are contributions made?

By contributing, you agree that your contribution is licensed under the [GNU General Public License, version 3 or later](LICENSE), the same as the project.

Everyone taking part follows the [Code of Conduct](CODE_OF_CONDUCT.md).
