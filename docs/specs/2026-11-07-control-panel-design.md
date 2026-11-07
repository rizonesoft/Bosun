# Bosun Panel: control panel design

Status: brainstorm result, 2026-11-07. Renamed from DevMode to Bosun the same day, and moved from `R:\GitHub` to `D:\GitHub`. Interface contract: [design/SPEC.md](../../design/SPEC.md). Mockup: [design/mockups/control-panel.html](../../design/mockups/control-panel.html). Execution plan: [todo/implementation-plan.md](../../todo/implementation-plan.md).

## What is it for?

Bosun Panel is one window that answers three questions for someone running 5 Windows and 5 WSL repos at once on 32 GB:

1. **Is it safe to start more work?** Live memory, what is using it, and the queue.
2. **What needs me?** Stalled runs, sections waiting for a decision, failed automations, agent updates.
3. **How do I keep the system improving?** The TODO graph, reviews, unattended runs and hooks, shown across every repo, plus machine tuning and extra development on the Claude API key.

## What did the sibling repos teach us?

Measured 2026-11-07 by reading each repo.

| Component | Base | Taken from elsewhere |
| --------- | ---- | -------------------- |
| Run guard, heartbeat, watchdog | Steward `run-guard.py` | Recto review-in-flight wait |
| Review dispatcher and pin | Steward | Recto settled items and timing |
| TODO graph and stamps | Steward `todo_graph.py` | Recto pre-commit hook |
| Dispatch hook | Recto `dispatch_hook.py` | Resolute pin and record guard |
| Scheduled automations | Recto, SDK kit | Bosun tasks and alert target |
| Change gates | Isotone | `weakening-scan`, `native-*-check` |
| WPF foundation | Steward.Console | Isotone token idea, Resolute capture |
| Icon pipeline | Office generator | Recto frames, Isotone small sizes |

Steward's generation is the base because it is the newest (guard dated 2026-11-07, dispatcher 2026-11-06) and Resolute's uncommitted work is migrating to the same files. Isotone's 12,000-line graph, `panel_slots` and `review_prompt` stay out: Resolute is deleting the same generation.

Review shape, kept the same as Steward: Claude Opus 5.5 writes, GPT-6 Astra reviews through the Codex CLI in its read-only sandbox, and Claude never reviews. Each section gets one medium plan review, one medium adversarial round, up to eight low rotation rounds that stop on clean, then one medium completion review last.

## Which technology?

- **.NET 11 and WPF:** `net11.0-windows`. RC2 is installed and carries a go-live licence; general availability is due 2026-11-10 at .NET Conf. .NET 11 is a Standard Term Support release, so the plan includes moving the pin to the final release (`D00 T02 §3`). Isotone already pins the same RC.
- **No MVVM toolkit:** Steward's `Observable` and `AsyncCommand` are small and proven, and every dependency in a memory tool costs memory.
- **Own theme, generated from tokens:** `design/tokens/colors.json` generates dark and light XAML with a drift check. Steward's hand-edited, dark-only theme is the thing to improve on. WPF-UI is rejected because Isotone is already removing it. WPF's built-in Fluent `ThemeMode` is rejected because the panel's templates are custom anyway.
- **The panel is not privileged:** `asInvoker`. Only an individual action that needs administrator rights asks for it, through UAC, for that one command.

## How is it built?

```text
Bosun.Panel (WPF, views and view models)
   |
Bosun.Core (net11.0-windows: readers, runners, ledger; no WPF)
   |
   +-- bosun.exe and the logger: status --json, report --json, current.json, jobs log
   +-- each repo's own scripts: todo-graph.py, review-dispatch.py, run-guard.py
   +-- each repo's files: reviews, .git/run-guard, build/automations, settings.json
   +-- agent CLIs: claude, codex, agy, gh
   +-- Anthropic C# SDK, official NuGet package
```

Rules that keep it honest:

- **The panel never re-implements a tool.** It runs the owning tool with an argument list and reads JSON. No Markdown or CSV parsing of what a command can already report. Missing JSON is added to the tool that owns the data, as `D01 T01` does for `bosun` and `D04 T03 §1` does for the guard.
- **Repos differ, and the panel says so.** A capability probe records what each repo's kit can report (for example, Isotone's `query ready` is plain text). An unsupported repo shows "not supported by this kit", never a guess.
- **The panel does not write plans or stamps.** `todo/`, receipts and findings stay owned by the skills and the reviewer. The panel reads them, and its write actions are limited to starting, stopping or halting a run, and to machine settings.
- **It must be cheap.** A memory tool that uses much memory defeats itself. Budget: under 150 MB working set and under 1% average CPU while idle in the tray, enforced by `D07 T01 §5`.
- **Status is never colour alone.** Every state has a word. Every icon-only control has an automation name.

## What is on each page?

The mockup shows all of them. Short version:

| Group | Page | Answers |
| ----- | ---- | ------- |
| Monitor | Overview | Safe to start more work? What needs me? |
| Monitor | Memory | Live headroom, per repo use, 24 h to 14 day history |
| Monitor | Instances | Every agent, editor and build on both sides |
| Monitor | Queue | Who holds a slot, who waits and why, the limits |
| Workflow | Plan | TODO graph per repo: board, graph, findings, deferred |
| Workflow | Reviews | Pin, rounds, review in flight, receipts |
| Workflow | Unattended run | Start, watch, stop, halt; each catch shown |
| Workflow | Hooks and automations | What actually fires, and scheduled tasks |
| Workflow | Fleet | Which kit generation each repo runs |
| Machine | Agents | Versions and updates, Windows and WSL |
| Machine | Optimise | Evidence, preview, apply, verify, undo |
| Claude API | Workbench | Overflow agents and single requests |
| Claude API | Usage | Ledger of everything the panel paid for |
| App | Settings, About | Every setting has a named consumer |

## How does the Claude API key fit in?

Extra development has two modes on either provider, Anthropic or OpenAI, and the second is the one that matters. The OpenAI provider was added on 2026-11-07 at the owner's request. On that key, Ask uses the official `OpenAI` package and an overflow agent runs Codex CLI the same way, with a local gateway enforcing the budget before each request:

1. **Ask:** one request through the official `Anthropic` NuGet package, for things like explaining a finding or summarising a memory report. No tools. Default model `claude-opus-5-5`, streaming on, with cheaper models one click away.
2. **Overflow agent:** runs Claude Code headless (`claude -p`, stream JSON) in a new git worktree, with the API key placed only in that child's environment. This gets the full harness, hooks, run guard and skills without re-implementing any of them, for when subscription limits are reached or more parallel work is wanted.

Rejected for now:

- **Managed Agents:** Anthropic hosts the sandbox, which cannot see local repos, WSL or the memory data the work is about.
- **Agent SDK:** it has no C# package, and the `claude` CLI is the same harness.
- **Tool Runner with our own tools:** it would mean rebuilding Claude Code's tools with less safety.
- **Batch API:** a 50% saving is only useful with a real bulk job, and none exists yet. Add it through `add-todo` when one does.
- **Claude as a reviewer through the API:** never. Reviews stay with the pinned reviewer of another model family.

Safety rules:

- Keys are entered in Settings and live in Windows Credential Manager, protected by DPAPI for the current user, outside Git. Each can also be imported once from 1Password when `op` is installed, and normal use makes no 1Password calls. Status shows only "stored" or "not stored". The key is never in arguments, logs or the parent environment.
- **No caps, no calls.** Daily and monthly caps have no defaults: until the operator sets them, the panel sends nothing. A request that would pass a cap is refused before it is sent.
- Cost comes from the panel's own ledger, using `usage` from each response and a dated price table (`design/pricing.json`). Anthropic's usage and cost reports need an Admin API key, so they are an optional later comparison, not the source.
- Overflow runs need a budget. The whole budget is reserved against the caps before the run starts and passed as `--max-budget-usd`. Watching reported cost is only a backstop.
- Overflow runs write only in their own worktree. Merging is the operator's step. The run guard works there because `D00 T01 §8` moves its state to the worktree's own git directory.

## What about the earlier phase 2 ideas?

The Bosun design left the pressure monitor, background memory priority and parking waiting for real logged peaks. The owner decided on 2026-11-07 to build both now, inert: the monitor with automatic action off until thresholds are set from `bosun report --days 7`, possible from 2026-11-14, and parking with an empty list. The earlier rejections stand: no working-set cleaners and no hard per-repo caps.

## How does the plan get tested while it is being built?

The plan builds the system with the system. `D00` finishes the kit in this repo, so every later section is reviewed and stamped the way Steward's are. The first unattended `process plan` here is the live test of the guard, heartbeat, hooks and reviewer, and the panel then displays that same run.

Surface sections must be able to run unattended, so they do not need a visible desktop. Pages render off screen through `RenderTargetBitmap` into PNGs a reviewer can read, and XAML is checked as XML for contrast and stray colours. Only sections proving real keyboard or UI Automation behaviour need a desktop, which the campaign keeps available, and the end-to-end UI Automation smoke test needs a desktop session (`D02 T02 §10`).

## How are repos found and runs opened?

The owner asked for both on 2026-11-07. Settings holds a list of project folders on either side. On this machine those are `D:\GitHub` on Windows and `~/www` and `~/projects` in WSL. Bosun lists new repos it finds there and adds them when told. These paths are the owner's settings, kept in `panel.json`, never in the code. The Unattended run page has a Commands card for each repo, with Copy and Open in terminal, using PowerShell on Windows and Bash in WSL.

## How does the unattended campaign run?

The owner set these on 2026-11-07. `D00 T03` builds them, and every other section waits for its preflight.

| Decision | Choice |
| -------- | ------ |
| Finish line | Implementation complete, then a short attended acceptance pass |
| Permissions | Bypass mode, plus deny rules for destructive and outbound actions |
| Usage limits | Subscription only, waiting and resuming; never paid API billing |
| Review dead end | Up to 33 repair cycles, then park and carry on; nothing ships unapproved |
| Completion gaps | Fixed or filed in the same cycle, never re-reviewed, as Steward and Recto run |
| Desktop | Logged in and unlocked, with sleep and update restarts held |
| .NET | Start on the RC; move to final when released; Release needs final |
| Machine changes | Disposable targets in the run; real ones in acceptance |
| API keys | Fake APIs in the run; live keys in acceptance, `D99 T01 §5` |
| Pressure monitor | Built, automatic action off until logs exist |
| Parking | Built, with an empty list |
| Signing | Unsigned; revisit if the repo goes public |
| Base kit | `R:\GitHub\Steward`, confirmed |
| Licence | `GPL-3.0-or-later`, confirmed |

A run that reaches the finish line writes `docs/acceptance/<date>.md`, listing every step left for the owner and any parked section.

## Who owns the heartbeat?

The owner decided on 2026-11-07 that Bosun runs one global heartbeat, from day one. Each repo keeps its run state, rules and hooks, because hooks fire inside its own session and a repo must still work without Bosun. One scheduled task, `Bosun heartbeat`, ticks every registered repo's guard every 5 minutes from the installed tool, so a campaign rebuilding Bosun can't disturb it, and Bosun itself runs on it while being built. `D00 T03 §11` builds it before launch, so other repos can be developed in parallel.

It can see what a repo can't: memory across all of them. So it resumes one repo per pass, and holds a pass while memory is short, for at most an hour, because memory pressure should delay a restart and never block it for good. A tick never starts a stopped WSL VM. A task was chosen over a loop in the logger because it keeps firing if a process crashes, and over a Windows service because a service can't easily start sessions in the owner's signed-in account. `Bosun`, `Steward` and `Resolute` move first. A repo with no run guard adds the kit first, and a global guard stays a later decision.

## How does Bosun use AI?

The owner asked for this on 2026-11-07, and `design/SPEC.md` §20 is the contract. Bosun already gathers what a model needs, so AI explains and never acts. The features are Explain this failure, Overnight briefing, Memory advisor, Finding patterns, Plan drafts and Second opinion. Each runs on a button press, inside the spend caps, with a preview of redacted text, a provider and model the operator chooses, and cited evidence.

Rejected: AI in queue admission, which must stay fast and predictable; automatic machine tuning; AI repairing stalled runs by itself, which would spend with nobody watching; and AI as a stand-in for the pinned reviewer. Ask Bosun, plain questions in the command palette, was offered and left out of this version.

## What did we decide by default?

Each of these is a default that is cheap to change, recorded so nobody has to rediscover it.

| Decision | Default | Cost of changing |
| -------- | ------- | ---------------- |
| Name | Bosun Panel | Rename a project and strings |
| Accent | Mint `#35D6A6`, amber second | Edit the token file |
| Icon | Prompt `>` plus two queue slots | Edit the SVG masters |
| Install | Per user, no administrator | Low |
| Runtime | Self-contained win-x64 | Publish flag |
| Review cap | 33 rounds a cycle, so a section isn't blocked by a low cap | One line in the pin |
| Python | Required, as the kit already needs it | n/a |
| Key store | Windows Credential Manager, entered in Settings | Swap `KeyStore` |
| Finish | Mica shell, Lucide glyphs, premium components | Drop `D02 T03` sections |
| WSL repos | First class, tools run inside the distro | Remove the WSL side |

The finish came from an owner request on 2026-11-07: they like the icon and interface, and asked for Lucide menu icons, button rounding, premium components and visualisation, and a Mica title bar, without yet knowing exactly what they want. The [mockup](../../design/mockups/control-panel.html) was rebuilt to show it, and the owner approved it on 2026-11-07 with one change, a repo selector for the Workflow pages, and expects to fine-tune the rest during the build. `design/SPEC.md` §19 is the contract and `todo/02-shell/TODO-03-premium-components.md` builds it. It costs little at idle because Windows composes Mica outside the panel's process, nothing inside the window blurs live, and animations stop when hidden and honour the system setting. If the system setting, Windows version or high contrast rules out Mica, the same layout falls back to solid `Base`.

The owner asked how WSL repos fit in. They stay in scope, because the problem Bosun solves spans both sides: memory, instances and the queue already cover WSL, and a WSL repo's plan, reviews and runs work the same way once its tools run inside the distro. The one rule is that polling never starts the VM, since a panel that wakes WSL to check on it would spend the memory it exists to protect.

## What needs the operator?

These are `Needs:` in the plan, so a run skips them instead of stalling:

- `D00 T01 §7`: create a private GitHub repo and CI. Publishing is outward-facing.
- `D00 T01 §9`: register the scheduled tasks on this machine.
- `D00 T02 §3`: move to the .NET 11 release when it ships, which installs an SDK.
- `D05 T02 §5`, `§6`: pressure thresholds, after 7 days of logs.
- `D06 T01 §2`: importing the API key from 1Password needs you present to unlock the vault.
- `D07 T01 §2`: whether to buy a code signing certificate.
- `D99 T01`: enabling XMP in the BIOS, the 64 GB decision, and the first attended walkthrough.
