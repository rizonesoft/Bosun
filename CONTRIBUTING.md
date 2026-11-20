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

Nothing is scheduled. An unattended run starts only when you start it (`pwsh scripts/start-run.ps1`, or its desktop shortcuts), lives in its own window and ends with it; nothing restarts or resumes one. The watchdog task that used to resume a run was retired on 2026-11-12, with the `-Watchdog` option that installed it: a clone that still has the task removes it with `python scripts/run-guard.py uninstall`. Bosun itself needs no unattended run.

## What do I need?

- Windows 11 with Visual Studio's C++ tools and CMake 3.25 or later.
- WSL 2 with an `Ubuntu` distro for the WSL tests.
- Python 3.14 or later.
- Node.js, for `scripts/verify-agent-tiers.mjs`.

## Which checks must pass?

```
pwsh scripts/check-all.ps1
```

That one command runs every gate in order and stops at the first failure, naming the step, its command and its exit code: the plan, the agent tiers, the native build, the language standard of every compile, the hardening of every built image (control-flow guard, ASLR, NX and CET), the C++ tests, every PowerShell test in `tests/`, and the Python suite. `-List` prints the steps and what a default run leaves out, `-Only <name>` runs just the steps named, and `-Wsl` adds the WSL side, which a default run never starts. The build and the Python suite take a turn in the queue through `bosun run --` when `bosun` is installed, so they queue with everything else on your machine.

`todo/implementation-plan.md` is generated. Edit the TODO sections, then run `python scripts/todo-graph.py plan --sync`.

## What does a good change look like?

- It fixes the cause in the layer that owns the behaviour, not with a filter over the result.
- A bug fix has a regression test that fails before the fix and passes after it.
- Colours, radii, elevation, motion, spacing and type come from `design/tokens/colors.json`.
- A value that can't be read shows "Not available" with a reason, never 0, and status always has a word as well as a colour.
- It never adds a secret, an API key or a personal path.
- It uses no em dashes or en dashes, in prose, code or commit messages.

## How do I report something?

Use the [issue forms](https://github.com/rizonesoft/Bosun/issues/new/choose) for bugs and ideas. Security problems go through [SECURITY.md](SECURITY.md), never a public issue.

## Under what licence are contributions made?

By contributing, you agree that your contribution is licensed under the [GNU General Public License, version 3 or later](LICENSE), the same as the project.

Everyone taking part follows the [Code of Conduct](CODE_OF_CONDUCT.md).
