# AGENTS.md

This repository follows the Conclave-SDK 3-agent conventions.

## Project

- Bosun keeps Windows and WSL repos from fighting over the same RAM: `bosun.exe` and the loggers (C++ in `src/`, WSL side in `wsl/`), and Bosun Panel, a .NET 11 WPF control panel over them: memory, dev instances, the queue and machine tuning. See [README.md](./README.md), the [panel design](./docs/specs/2026-11-07-control-panel-design.md) and the [core design](./docs/specs/2026-11-07-bosun-design.md).
- Bosun stays small and unconnected (owner decision, 2026-11-10). It needs no unattended run, no other repo and no agent workflow to work, and no other repo needs Bosun: nothing here writes into, reconfigures or schedules anything in another repository. The panel shows what `bosun.exe` and the loggers measure and never reads a repo's plan, reviews or run state. Never name a machine's folders in code: a path comes from the checkout, an environment variable or a setting.
- Bosun and the Conclave-SDK kits stay separate (owner decision, 2026-11-12). Bosun's rules, skills, hooks, review tooling and TODO tooling are its own from here on: import, merge or sync nothing further from Conclave-SDK or its retired sibling repos, and never clone or copy another repo into this checkout (the owner removed `samples/` on purpose, and it stays gone). Improve Bosun's tooling here, on its own terms.
- `design/SPEC.md` is the interface contract and wins over the mockup. Colours, radii, elevation, motion, spacing and type come only from `design/tokens/colors.json`. The app and tray icons come only from `design/icon/`, and menu and control glyphs only from Lucide through the icon map in `D02 T03 §3`.
- The panel never re-implements a tool: it runs the owning tool with an argument list and reads JSON, and missing data is added to the tool that owns it.
- Verify with `pwsh scripts/check-all.ps1` once `D00 T01 §6` has landed. Until then: `python scripts/todo-graph.py validate`, `python scripts/run-tests.py`, `node scripts/verify-agent-tiers.mjs`, and the commands in `README.md`. Build the native tools with `pwsh scripts/build.ps1` (Ninja, warnings as errors). Run full builds and test suites through `bosun run --`. `pwsh scripts/setup-lsp.ps1` sets up code intelligence (clangd, csharp-ls, pyright) on a new machine.
- Plans live in `todo/` and are driven by the `todo` kit skills; see [TODO workflow](./docs/todo-workflow.md). Validate with `python scripts/todo-graph.py validate`. Domains: `00-workspace`, `01-core`, `02-shell`, `03-monitor`, `05-machine`, `07-release`, `99-manual` (`04` and `06` were retired on 2026-11-10 and are never reused). Numbers are stable addresses.
- Claude Opus is the writer, always the newest one: the run starts with `claude --model opus`, an alias Claude Code resolves itself (`claude-opus-5-5` on 2026-11-10), so no script names a version. Independent review runs through `scripts/review-dispatch.py` and `.conclave/reviewers.toml`: Codex uses Carbon (Gemini 4 Carbon); Claude and Gemini use Astra. Claude is never a reviewer, and no review runs through the panel's API keys. Each section runs one medium plan review, one medium adversarial review, up to `schedule.max_low_rounds` low reviews (32 here, so 33 rounds in all), stopping on clean, then one medium completion review last. Pass `--writer-model <exact-model-version>` (the exact id of the model your own session is running, never the alias) and keep one findings file. Never resume an operator-paused section without explicit authorization.
- An unattended run starts from the prompt `process plan`. `scripts/run-guard.py` holds it open through hooks, an in-session heartbeat and this repo's own watchdog task (`python scripts/run-guard.py install`); `pwsh scripts/start-run.ps1` starts one (the desktop shortcut "Bosun unattended run" runs it; `-InstallShortcut` writes the shortcut): every permission prompt off, the newest Opus, the goal set, and signed in with the long-lived token in the user variable `CLAUDE_CODE_OAUTH_TOKEN`, which `pwsh scripts/store-claude-token.ps1 -FromVault` imports once from the 1Password Conclave vault. The guard keeps the run's model, so a resumed session runs the same one. When a start misbehaves, `pwsh scripts/campaign-preflight.ps1` names each fault with its fix. Trunk-based `main`: commit completed, verified steps directly, and never amend a candidate under review, bypass hooks or force-push. Every clone runs `git config core.hooksPath scripts/hooks`.
- Never commit key material or live secrets.

## Review failure modes

The generic review skills check the failure modes this file names for the area a change touches:

- A queue record treated as authority over the slot mutexes, or a queue change that lets a crashed job block others.
- The panel parsing human text, Markdown or CSV that a tool could report as JSON, or re-implementing a tool's rules.
- Anything that makes Bosun depend on another repo, an agent workflow or an open unattended run, that writes into or schedules something in another repository, or that names a machine's folder in code.
- A poll that starts a stopped WSL VM.
- A machine change with no preview, read-back or undo, or a whole-panel elevation where one command would do.
- A timer, poller or animation that keeps running for a hidden page, a live blur, motion that ignores the system setting, or anything that pushes the idle budget past 150 MB and 1% CPU.
- A hex colour or font size outside `Theme/Generated`, status shown by colour alone, an icon-only control without an automation name, or a stubbed control with no owner section.
- A value that can't be read displayed as 0 instead of "Not available" with a reason.

## Autonomy and ambition

The owner set this on 2026-11-10, and it governs every session, attended or not.

- **Decide, then act.** When you know the answer, take it: pick the option an expert in the field would defend, do the work, and record the decision and what reversing it would cost. Do not stop to ask a question you can answer from the code, the documents or good engineering judgment. The only stops are the halting grounds in the run guard (data loss, credentials, shared infrastructure, a calculation that looks wrong) and the operator's own word.
- **Aim past the competition.** The bar is the best tool in its class, measured against what Microsoft, Apple, Google or Adobe would ship: faster, lighter, clearer, more reliable, and a pleasure to use. "Good enough" and "that can't be done" are claims to test, not reasons to stop. Excellence comes from doing fewer things superbly, so it never licenses scope the product rule above forbids.
- **Self-heal.** A failing gate, a flaky test, a broken tool or a stale claim in the plan is work, fixed at the layer that owns it, with a test that would have caught it. Never route around it.
- **Self-improve.** When the repo, its runners, its tools or its methods slow the work down or let a defect through, improve them: measure first, change the owning script or skill, prove the gain, and write the lesson where the next session will read it (the phase run file's Lessons, this file, or the skill). Leave every tool you touch better than you found it.

## Working rules

- Verify files, routes, schemas, tools, and commands before relying on them. A path someone mentions is a hypothesis until it is on disk.
- Inspect existing patterns, imports, the package manager, the framework, and the test commands before editing. Do not infer architecture or tool schemas from memory when the repository can answer.
- Resolve a tool by capability before path. Prefer explicit config, then discovery, then a setup-required state. Store discovered paths only in ignored local config and never hardcode user-specific absolute paths.
- Prefer global or user-level tool installs. Add repo-scoped wrappers only when they encode project-specific behavior or reproducible commands.
- Real data only. Demo or seed data stays behind an explicit demo mode and is labeled as such.
- Develop on `main`, always: no feature branches and no long-lived worktrees. A branch or worktree is only for a throwaway (a disposable pull request, an isolated experiment), and whoever makes one removes it when done. When you find a stale one, check `git worktree list` and `git branch -a`, then remove any whose work is merged or abandoned (`git worktree remove`, `git branch -d`, `git push origin --delete`); ask the owner first when it holds unmerged commits or uncommitted files.
- Run the relevant typecheck, lint, and tests after a change.
- Fix at the owning layer: a setting in its setting, behaviour in the code that produces it, data in the data. No output filters that hide a problem. Code changes end the report with a `Layer:` line.
- Markdown prose is never hard-wrapped: one line per paragraph and per list item. Every Markdown table row stays `<= 100` characters so raw `.md` files never wrap.
- Never use em dashes or en dashes anywhere: prose, code, comments, commit messages and generated files.
- Temporary files and full logs go to scratch, never the repository: the folder the `BOSUN_SCRATCH` variable names (else the system temp folder), or the session scratchpad when one is given.

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

- Delegate mechanical subtasks to the fleet in `.claude/agents/`: `code-explorer` (find and map code), `test-runner` (run gates and report failures), `git-runner` (status, diffs, history), `debug-evidence` (gather facts for a failure) and `mutation-tester` (prove a checkpoint can fail). The process skills name where each is used. Claude subagents pin an explicit `model:`: Sonnet by default, and Opus (with a `model_rationale:`) only where judgment quality needs it.
- Subagents read and run; they never write. Every Claude subagent declares an explicit `tools:` list without Edit, Write, MultiEdit, NotebookEdit or `*`. The main loop makes every change and verifies what subagents return.
- Haiku is only for near-mechanical work, at least 90% mechanical (read-only lookups, inventories, structural checks), whose output a script or the main loop verifies. Pin `claude-haiku-5-5` or later, declare `mechanical_share:` (90 to 100), point `haiku_replay:` at a record of a known-answer replay that showed no loss against Sonnet, and give it no edit tools. Never use Haiku for review, audit, research, security, debugging, diagnosis, planning or design.
- Quality comes before delegation. Check what a subagent returns against the code or the command output before relying on it. When one gets its job wrong (a missed failure, an invented result, a wrong summary), do that work in the main loop, and remove the subagent: delete its file in `.claude/agents/`, take its name out of this section and the skills that use it, and note why in the commit. A removed subagent comes back only with a replay that shows the fault is gone.
- Run `node scripts/verify-agent-tiers.mjs` after adding or changing an agent definition; it enforces these rules.

## TODO workflow

Plan and build through the TODO system: [docs/todo-workflow.md](./docs/todo-workflow.md).
