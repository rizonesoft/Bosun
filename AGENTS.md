# AGENTS.md

This repository follows the Conclave-SDK 3-agent conventions.

## Project

- Bosun keeps Windows and WSL repos from fighting over the same RAM: `bosun.exe` and the loggers (C++ in `src/`, WSL side in `wsl/`), and Bosun Panel, a .NET 11 WPF control panel over them and over each repo's agent workflow. See [README.md](./README.md), the [panel design](./docs/specs/2026-11-07-control-panel-design.md) and the [core design](./docs/specs/2026-11-07-bosun-design.md).
- `design/SPEC.md` is the interface contract and wins over the mockup. Colours, radii, elevation, motion, spacing and type come only from `design/tokens/colors.json`. The app and tray icons come only from `design/icon/`, and menu and control glyphs only from Lucide through the icon map in `D02 T03 §3`.
- The panel never re-implements a tool: it runs the owning tool with an argument list and reads JSON, and missing data is added to the tool that owns it. It never writes to `todo/`, receipts or findings, and it never writes run state.
- Verify with `pwsh scripts/check-all.ps1` once `D00 T01 §6` has landed. Until then: `python scripts/todo-graph.py validate`, `python -m unittest discover -s scripts/tests`, `node scripts/verify-agent-tiers.mjs`, and the commands in `README.md`. Run full builds and test suites through `bosun run --`.
- Plans live in `todo/` and are driven by the `todo` kit skills; see [TODO workflow](./docs/todo-workflow.md). Validate with `python scripts/todo-graph.py validate`. Domains: `00-workspace`, `01-core`, `02-shell`, `03-monitor`, `04-workflow`, `05-machine`, `06-claude-api`, `07-release`, `99-manual`. Numbers are stable addresses.
- Claude Opus 5.5 (`claude-opus-5-5`) is the writer. Independent review runs through `scripts/review-dispatch.py` and `.conclave/reviewers.toml`: Codex uses Argon; Claude, Gemini and Meta/Muse use Astra. Claude is never a reviewer, and no review runs through the panel's API keys. Each section runs one medium plan review, one medium adversarial review, up to `schedule.max_low_rounds` low reviews (32 here, so 33 rounds in all), stopping on clean, then one medium completion review last. Pass `--writer-model <exact-model-version>` and keep one findings file. Never resume an operator-paused section without explicit authorization.
- An unattended run starts from the prompt `process plan`. `scripts/run-guard.py` holds it open through hooks, a heartbeat and a watchdog task. Trunk-based `main`: commit completed, verified steps directly, and never amend a candidate under review, bypass hooks or force-push. Every clone runs `git config core.hooksPath scripts/hooks`.
- Never commit key material or live secrets. API keys live only in Windows Credential Manager, entered in Settings, and reach only the child process that needs them. No spend caps set means no API calls to any provider.

## Review failure modes

The generic review skills check the failure modes this file names for the area a change touches:

- A queue record treated as authority over the slot mutexes, or a queue change that lets a crashed job block others.
- The panel parsing human text, Markdown or CSV that a tool could report as JSON, or re-implementing a tool's rules.
- A path that sends an API request without passing `SpendGuard`, a key reaching a log, an argument or the panel's own environment, or an unknown model's cost counted as zero.
- A machine change with no preview, read-back or undo, or a whole-panel elevation where one command would do.
- A timer, poller or animation that keeps running for a hidden page, a live blur, motion that ignores the system setting, or anything that pushes the idle budget past 150 MB and 1% CPU.
- An Insight that sends text not shown in its preview or not redacted, that writes a file or changes the machine, or that reads as a review result.
- A heartbeat tick that starts a stopped WSL VM, that resumes more than one repo in a pass, or a memory hold with no time limit.
- A hex colour or font size outside `Theme/Generated`, status shown by colour alone, an icon-only control without an automation name, or a stubbed control with no owner section.
- A value that can't be read displayed as 0 instead of "Not available" with a reason.

## Working rules

- Verify files, routes, schemas, tools, and commands before relying on them. A path someone mentions is a hypothesis until it is on disk.
- Inspect existing patterns, imports, the package manager, the framework, and the test commands before editing. Do not infer architecture or tool schemas from memory when the repository can answer.
- Resolve a tool by capability before path. Prefer explicit config, then discovery, then a setup-required state. Store discovered paths only in ignored local config and never hardcode user-specific absolute paths.
- Prefer global or user-level tool installs. Add repo-scoped wrappers only when they encode project-specific behavior or reproducible commands.
- Real data only. Demo or seed data stays behind an explicit demo mode and is labeled as such.
- Run the relevant typecheck, lint, and tests after a change.
- Fix at the owning layer: a setting in its setting, behaviour in the code that produces it, data in the data. No output filters that hide a problem. Code changes end the report with a `Layer:` line.
- Markdown prose is never hard-wrapped: one line per paragraph and per list item. Every Markdown table row stays `<= 100` characters so raw `.md` files never wrap.
- Never use em dashes or en dashes anywhere: prose, code, comments, commit messages and generated files.
- Temporary files and full logs go to scratch, never the repository: `R:\scratch` on Windows, `/mnt/r/scratch` on WSL (the same folder), or the session scratchpad when one is given.

## Safety

- Local reversible work in the repository needs no confirmation.
- Ask the operator once before anything that sends, deletes, or changes something outside the repository: mail, production systems, cloud changes, SQL writes, dropping data, credentials.
- When a command needs sudo, give the operator the exact command to run with `! sudo ...`. Never store the sudo password.
- Never print, log, or commit a secret value.

## Skills

- Antigravity skills live in `.agents/skills/<name>/`, Claude skills in `.claude/skills/<name>/`, and Codex skills in `.codex/skills/<name>/`, all as real files, never symlinks. Change all three together.
- Follow [Agent Skills and Rules Structure](./docs/agent-skills-and-rules-structure.md) before adding or moving skills.
- Name a skill for its capability, not with a project prefix.
- Each agent reads and runs only its own directory (`.agents/` for Antigravity, `.claude/` for Claude Code, `.codex/` for Codex) and never edits another agent's skills unless asked.

## Subagents

- Delegate mechanical subtasks to subagents. Claude subagents pin an explicit `model:`: Sonnet by default, and Opus (with a `model_rationale:`) only where judgment quality needs it.
- Subagents read and run; they never write. Every Claude subagent declares an explicit `tools:` list without Edit, Write, MultiEdit, NotebookEdit or `*`. The main loop makes every change and verifies what subagents return.
- Haiku is only for near-mechanical work, at least 90% mechanical (read-only lookups, inventories, structural checks), whose output a script or the main loop verifies. Pin `claude-haiku-5-5` or later, declare `mechanical_share:` (90 to 100), point `haiku_replay:` at a record of a known-answer replay that showed no loss against Sonnet, and give it no edit tools. Never use Haiku for review, audit, research, security, debugging, diagnosis, planning or design.
- Run `node scripts/verify-agent-tiers.mjs` after adding or changing an agent definition; it enforces these rules.

## TODO workflow

Plan and build through the TODO system: [docs/todo-workflow.md](./docs/todo-workflow.md).
