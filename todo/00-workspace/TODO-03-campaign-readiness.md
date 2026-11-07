---
schema_version: 1
id: campaign-readiness
domain: 00-workspace
status: active
title: "TODO-03 -- Campaign readiness"
---

# TODO-03 -- Campaign readiness

> **Goal:** An unattended `process plan` runs to "implementation complete" with no prompt, no permanent dead end, no unlimited retry, no spend on paid APIs and no false green, and hands the owner one acceptance list.

> **Current state:** Measured 2026-11-07. An independent review and the code agree on these faults. `scripts/review-dispatch.py:241` refuses after a completion review with gaps, and `scripts/lib/todo_graph.py:1222` only unstamps stamped sections, so such a section can never stamp. `scripts/review-dispatch.py:248` needs operator recovery on exhausted rounds, and `scripts/lib/run_guard.py:325` still offers that row. `scripts/lib/run_guard.py:47` always excludes `display-session`. `scripts/lib/run_guard.py:205` ends a run for good when no row is runnable. `scripts/ci_status.py:120` exits 0 with no successful run. `tests/test_core.cpp:22` ignores its arguments. `cmake` is not on PATH.

## Inputs

- The owner's campaign decisions in `docs/specs/2026-11-07-control-panel-design.md`, "How does the unattended campaign run?".
- `scripts/lib/run_guard.py`, `scripts/run-guard.py`, `scripts/review-dispatch.py`, `scripts/lib/todo_graph.py`, `scripts/ci_status.py`, `.conclave/reviewers.toml`.
- `.claude/skills/process-plan`, `process-phase`, `process-todo-section` and `review-todo-section`, mirrored in `.agents/skills` and `.codex/skills`.
- -> XREF: D00 T01 §3 -- consumed by this file
- -> XREF: D00 T01 §4 -- consumes this file
- -> XREF: D00 T02 §1 -- consumes this file
- -> XREF: D00 T02 §3 -- consumes this file
- -> XREF: D01 T01 §1 -- consumes this file

## Outcome

- `python scripts/run-guard.py status --json` reports one of complete, waiting, parked or stopped, with what each waits for.
- `pwsh scripts/campaign-preflight.ps1` exits 0 only when every check below has passed on this machine, through the exact resume command.

**Adjacency:** all=not-applicable (internal tooling and contracts with no records of their own)

**Adjacency rationale:** This is the machinery that builds the product. The operator sees its results in the Unattended run page, owned by `D04 T03`.

## Implementation Order

| Order | Section | Deliverable | Depends On | Status |
| ----- | ------- | ----------- | ---------- | ------ |
| 1 | §1 | CI status that proves the commit | -- | [ ] |
| 2 | §2 | One section disposition across graph, guard and skills | -- | [ ] |
| 3 | §3 | Review repair cycles and parking | -- | [ ] |
| 4 | §4 | Reviewer attempt limits and diagnostics | §2 | [ ] |
| 5 | §5 | Usage limits wait and resume | §2 | [ ] |
| 6 | §6 | Run end states and wake checks | §3, §5 | [ ] |
| 7 | §7 | Owned session recovery | §5 | [ ] |
| 8 | §8 | Campaign permissions | -- | [ ] |
| 9 | §9 | Named core tests and CMake resolution | -- | [ ] |
| 10 | §10 | Preflight and machine hold | §1, §4, §6, §7, §8, §9, §11, D00 T01 §3 | [ ] |
| 11 | §11 | One global heartbeat | -- | [ ] |

## 1. CI status that proves the commit

> **Started:** 2026-11-07T18:00:05Z

Exit 0 means only that no red run was seen today, so a commit with no successful run reads as passing. A gate has to say which commit passed, and what the run does next. **Corrected 2026-11-07** after the plan review: the push range, a gate action and the exit table were added, and the item naming the wrong file was fixed.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Extend `scripts/ci_status.py`. Done when: `pending`, `none` and unavailable are separate from `green`, and a proven skip is separate from missing evidence.
2. Add `--sha`, `--base` and `--gate`. Done when: a green run of an older commit can't satisfy a newer one, and `--gate` prints one action.

- [x] Report a verdict of `green`, `red`, `pending`, `none` or `skipped` per workflow in `scripts/ci_status.py`. Done when: a new `scripts/tests/test_ci_status.py` covers an empty run list, all runs pending and an older green with a newer run pending, through both text and JSON output. Cheaper substitute: treating no red as green.
- [x] Add `--sha <commit>` in `scripts/ci_status.py`, reading runs with `gh run list --commit <sha>` so the lookup can't miss it, and judging by the newest attempt of that commit. Done when: tests prove an exact green passes, a green older commit doesn't, a commit outside the latest ten branch runs is still found, and a newer pending or failed run of the same commit decides.
- [x] Add `--base <commit>` in `scripts/ci_status.py`, reading the workflow's `paths` and `paths-ignore` at `--sha` and comparing every file changed in `base..sha`, to report `skipped` with the reason. Done when: tests prove a Markdown-only push is `skipped`, a code commit followed by a Markdown stamp in one push is not, and a missing base, an unreadable workflow or a failed diff reads `none`, never `skipped`. Cheaper substitute: checking only the last commit.
- [x] Return one overall exit in `scripts/ci_status.py`: red first as 1, then unavailable as 3, pending as 4, none as 5, and 0 only when every workflow is `green` or `skipped`. Done when: a table test covers mixed results, and the module docstring states this order.
- [x] Add `--gate` in `scripts/ci_status.py`, printing one action for a pushed commit: `proceed` on exit 0, `wait` on 3, 4 or 5, `repair` on red with attempts left, and `exhausted` after three. Done when: scenario tests walk each transition, including a repair that goes green. Cheaper substitute: rules only in skill text.
- [x] Hold the repair count in `build/ci-repairs.json` with an atomic replace, keyed by the first red commit, reset by a green. Done when: a test proves the count survives a new process, and a corrupt file reads as 0 attempts used with one warning line.
- [x] Bound `wait` in `--gate` with `--wait-minutes` in `scripts/ci_status.py`, polling every 60 seconds. Done when: a fake clock shows pending for the whole time ends in `wait`, and pending turning green ends in `proceed`.
- [x] Add the red-first policy to `.claude/skills/process-phase/SKILL.md` and its two mirrors: after each stamped section, push, run `--gate` with the pushed commit and the previous remote head, and act on its one action. `exhausted` files the fault through `add-todo` and carries on with local gates, since the owner said the run must not block itself, and `D00 T03 §6` refuses `complete` while `main` is red. Done when: a test proves the three mirrors hold the same text and name each action.
- [ ] Commit: `ci: status proves the commit`

**Test checkpoint:** `python -m unittest discover -s scripts/tests -p test_ci_status.py` exits 0, and fails if an empty run list exits 0 or a code commit before a Markdown stamp reads `skipped`.

## 2. One section disposition across graph, guard and skills

> **Started:** 2026-11-07T18:21:16Z

The graph, the guard and the skills each decide differently what can run. One function decides, so a row can't be offered by one and refused by another. **Corrected 2026-11-07:** it also holds the dead-end check the guard kept as `stranded`, and an active remote session counts as a desktop, where this said console only.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Add `section_disposition` in `scripts/lib/todo_graph.py`. Done when: each open row has one state and reason.
2. Use it in `scripts/lib/run_guard.py`. Done when: `HOST_NEEDS` is gone.

- [x] Create `scripts/lib/host_caps.py` reporting `windows-host`, `arm64-host`, `office`, `libreoffice` and `display-session`. Done when: `scripts/tests/test_host_caps.py` proves each from faked probes. Cheaper substitute: a hardcoded list of what this machine has.
- [x] Report `display-session` in `scripts/lib/host_caps.py` only for an active, unlocked session of this process, through `WTSQuerySessionInformationW` and `OpenInputDesktop` showing the `Default` desktop. Done when: faked locked and disconnected sessions read false, a failed probe reads false, a non-Windows host reads false, and this machine reads true while unlocked. Cheaper substitute: assuming a logged-in user has a desktop.
- [x] Add `section_disposition(row, rows, caps)` in `scripts/lib/todo_graph.py`, keeping the existing `disposition(row, rows)` checks for dependencies and suspect ancestors, and returning `ready`, `waiting-host`, `waiting-person`, `waiting-dependency`, `needs-repair`, `parked` or `verified` with a reason. `needs-repair` covers both dead ends: a latest cycle whose completion returned gaps, and a latest cycle with its returned rounds at the pin's cap and no clean last round, without being confused with a clean last round awaiting completion. `parked` reads a `> **Parked:**` line, which `D00 T03 §3` adds. Done when: `scripts/tests/test_todo_graph.py` covers every Need in the closed list, including `store-account` and `attended`, and `resolve`, `classify` and plan generation keep their documented exit codes.
- [x] Replace `HOST_NEEDS` and `stranded` in `scripts/lib/run_guard.py` with `query disposition`, offering only `ready` rows and keeping `real_rows` returning None when it can't tell. Done when: a test proves a `display-session` row runs with a desktop and waits without one, and an unreadable answer never ends a run. Cheaper substitute: adding `display-session` to an exclusion list.
- [x] Add `query disposition` to `scripts/todo-graph.py`, printing JSON with each open row's ref, state and reason, leaving `query ready`, `resolve` and `classify` unchanged. Done when: a test proves the guard and skills read the same output, and those three keep their documented results.
- [x] Update `.claude/skills/process-plan/SKILL.md` and `process-phase/SKILL.md` and their mirrors to use `query disposition`. Done when: no skill names its own list of host Needs.
- [ ] Commit: `graph: one section disposition`

**Test checkpoint:** `python -m unittest scripts/tests/test_todo_graph.py scripts/tests/test_host_caps.py scripts/tests/test_run_guard.py` exits 0, and fails if `section_disposition` returns ready for an `attended` row, a locked session reads as a desktop, or the guard offers a `waiting-host` row.

## 3. Review repair cycles and parking

A section that fails review must either get a real chance to repair or step aside, never hold up the run in silence. The owner set the cap at three repair cycles. No section ever ships unapproved. **Corrected 2026-11-07:** this depended on §2, but §2 and every other section can only recover from a completion gap through this section, so it can't wait for them. It uses `section_disposition` from the §2 code in the tree.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Add `schedule.max_repair_cycles` to `.conclave/reviewers.toml`. Done when: the pin reads 3.
2. Add `repair` and `park` to `scripts/todo-graph.py`. Done when: a dead-end section starts a new cycle, or parks at the cap.

- [ ] Set `max_repair_cycles = 3` in `.conclave/reviewers.toml` and report it from `--check-pin --json` in `scripts/review-dispatch.py`. Done when: a test reads it back, and a value over 3 is refused. Cheaper substitute: a constant in the skill text.
- [ ] Add `python scripts/todo-graph.py repair '<ref>' --reason '<text>'` for an unstamped section whose completion returned gaps or whose rounds ran out, in `scripts/lib/todo_graph.py`. Done when: a test proves a new cycle file `D<NN>-T<NN>-s<N>-cycle<K>.md` starts with the old findings kept. Cheaper substitute: deleting the findings file.
- [ ] Offer `needs-repair` rows as work in `scripts/lib/run_guard.py`, and route them to `repair` in `.claude/skills/process-phase/SKILL.md` and its mirrors, since `D00 T03 §2` offers only `ready` rows until this lands. Done when: a test restarts a run with only repairable work left, and it starts the repair rather than ending.
- [ ] Refuse `repair` on a stamped section, on a section with no dead end, and past the cap with a message naming `park`, in `scripts/lib/todo_graph.py`. Done when: all three are tested, and the old `unstamp` route is unchanged.
- [ ] Add a `> **Parked:** <date>: <reason>` line and `python scripts/todo-graph.py park '<ref>' --reason '<text>'`. Done when: `section_disposition` returns `parked`, its dependants return `waiting-dependency`, and `stamp` refuses a parked section until the line is removed by the operator.
- [ ] Refuse `--record` in `scripts/review-dispatch.py` on a findings file older than the section's newest cycle file. `check_sequence` already reads one file per cycle, so a new file allows plan, rounds and completion again with no change there. **Corrected 2026-11-07:** this said to change `check_sequence`. Done when: a test runs a whole second cycle and refuses mixing findings across cycles.
- [ ] Teach `.claude/skills/review-todo-section/SKILL.md` and its mirrors: repair at a dead end, park at the cap, then carry on. Done when: a test reads that rule from all three.
- [ ] Persist the repair cycle in a `> **Repair cycle:** <K>: <date>: <reason>` line, and count those lines with `Unstamped:` notes through a new `reopenings` in `section_findings`, `build_start` and `review_dead_end` in `scripts/lib/todo_graph.py`, while `suspects` and `settled` keep counting `Unstamped:` notes only, since a section never stamped has no consumers to suspect. Done when: a test goes completion gaps, repair, clean reviews, then a successful `stamp` and `validate`, with the earlier findings kept. Cheaper substitute: changing only the dispatcher.
- [ ] Write a fresh `> **Started:**` line after the repair plan review in `.claude/skills/process-todo-section/SKILL.md` and its mirrors. Done when: a test proves the new plan review precedes the new build start.
- [ ] Commit: `review: repair cycles and parking`

**Test checkpoint:** `python -m unittest discover -s scripts/tests -p test_review_dispatch.py` exits 0, and fails if a fourth repair cycle is allowed.

## 4. Reviewer attempt limits and diagnostics

Failed attempts don't count as reviews, and each one changes the tree, so a broken reviewer looks like progress and retries forever. Only Astra reviews Claude's work, and there is no fallback, so an outage must wait.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Classify failures in `scripts/review-dispatch.py`. Done when: each failure has a class and a retry rule.
2. Keep attempts out of the progress signal. Done when: a failed attempt doesn't count as a change.

- [ ] Classify a failed attempt as `auth`, `model`, `rate-limit`, `timeout`, `invalid-verdict` or `other`, with any retry-after time, in `scripts/review-dispatch.py`. Done when: `scripts/tests/test_review_dispatch.py` covers each from captured CLI output. Cheaper substitute: reducing every failure to an exit number.
- [ ] Keep up to 4 KB of redacted stderr in `build/review-failures/` from `scripts/review-dispatch.py`. Done when: a planted key in stderr never reaches the file. Cheaper substitute: discarding stderr.
- [ ] Cap attempts per pass at 5 with exponential backoff, recording each in `build/review-attempts.json` rather than the findings file. Done when: a fake failing CLI stops at 5, and the tree's signature is unchanged.
- [ ] Exclude `build/` state from `signature` in `scripts/lib/run_guard.py`. Done when: a test proves an attempt record does not reset `idle` or `restarts`.
- [ ] Mark the run `waiting` with reason `reviewer-unavailable` after the attempt cap, for `auth`, `model` and repeated `rate-limit` failures. Done when: the guard rechecks every 30 minutes, and a recovered reviewer resumes the run. Cheaper substitute: switching to a different reviewer.
- [ ] Commit: `review: attempt limits and failure classes`

**Test checkpoint:** `python -m unittest discover -s scripts/tests -p test_review_dispatch.py` exits 0, and fails if a sixth attempt runs.

## 5. Usage limits wait and resume

The owner chose subscription only, so hitting a limit costs time and never money. Today three restarts with nothing changed call the run stalled, which can happen long before a limit resets.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Add wait state to `scripts/lib/run_guard.py`. Done when: a usage limit records when to retry.
2. Keep paid keys away from the run. Done when: a resumed session can't see one.

- [ ] Classify `StopFailure` data as `usage-limit`, `auth`, `overloaded` or `other` in `scripts/lib/run_guard.py`, storing `wait_until` from any reset time, or 30 minutes after the last failure when none is given. Done when: `scripts/tests/test_run_guard.py` covers each case. Cheaper substitute: only recording the failure text.
- [ ] Leave `restarts` and `MAX_QUIET_HOURS` unchanged while `wait_until` is in the future. Done when: a fake clock across a 5 hour limit ends in a resume, not `stalled`.
- [ ] Remove `ANTHROPIC_API_KEY`, `ANTHROPIC_AUTH_TOKEN`, `OPENAI_API_KEY` and `CLAUDE_CODE_USE_BEDROCK` from the environment passed by `resume_command`'s launch in `scripts/lib/run_guard.py`. Done when: a test with all of them set reads a clean child environment. Cheaper substitute: relying on them being unset.
- [ ] Write one guard log line in `scripts/lib/run_guard.py` when one of those variables was removed, naming the variable and no value. Done when: a test sees the line, and the run carries on.
- [ ] Treat `auth` as `waiting` with reason `sign-in-needed`, and write one line to the guard log. Done when: a test proves it never spins and resumes after sign-in returns.
- [ ] Commit: `guard: wait out usage limits without paid keys`

**Test checkpoint:** `python -m unittest discover -s scripts/tests -p test_run_guard.py` exits 0, and fails if `restarts` grows during a wait.

## 6. Run end states and wake checks

"No runnable rows" is not one state. A run waiting for the .NET release is not finished, and a finished build must not end before its closeouts are written. The owner's finish line is implementation complete, then an acceptance pass.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Split `drained` in `scripts/lib/run_guard.py`. Done when: complete, waiting, parked and stopped are distinct.
2. Add `todo/wakes.json`. Done when: a temporary Need clears by itself when its check passes.

- [ ] Define `complete` in `scripts/lib/run_guard.py` as every section outside phase 99 stamped, with the Verification of each of those files run and each of those phases closed out in `docs/phase-runs/`. Done when: a test with a stamped section but no closeout does not read complete, and phase 99 rows do not prevent it. Cheaper substitute: treating an empty ready list as done.
- [ ] Refuse `complete` in `scripts/lib/run_guard.py` unless `scripts/ci_status.py --sha` on the pushed head exits 0. Done when: a test with a red head does not read complete, and its acceptance file names the failing workflow.
- [ ] Add pending file verification and phase closeouts to `query disposition` in `scripts/lib/todo_graph.py`. Done when: a crash after the last stamp leaves work the watchdog resumes.
- [ ] Keep a run with only `waiting-host` or `waiting-person` rows active in state `waiting`, with `watch` rechecking each tick. Done when: a test proves a row becoming ready resumes the run with no prompt.
- [ ] End a run whose remaining work outside phase 99 is only parked sections and their dependants as `parked`, never as complete, and write `docs/acceptance/<date>.md` in both `parked` and `complete`, listing every parked section, each blocked dependant with its root blocker, and every phase 99 row with how to run it. Done when: fixtures for both states produce the file, and `parked` is never reported as implementation complete.
- [ ] Create `todo/wakes.json`, mapping a section and Need to a check command, and clear that Need from the section through `scripts/todo-graph.py`. Done when: a test proves a passing check removes the Need in a commit, and a failing one leaves it. Cheaper substitute: an operator editing the section.
- [ ] Add the first wake in `todo/wakes.json` for `D00 T02 §3` `third-party`, using `scripts/dotnet-final-available.py`. Done when: a faked `dotnet --list-sdks` with a final 11.0.1xx SDK passes, and with only the RC fails.
- [ ] Extend `.claude/skills/process-plan/SKILL.md` and `process-phase/SKILL.md` and their mirrors to dispatch work items from `query disposition`: section execution, file verification and phase closeout. Done when: a test simulating a crash after the final stamp resumes only the verification and closeout, then reads complete. Cheaper substitute: only listing the pending work.
- [ ] Commit: `guard: end states and wake checks`

**Test checkpoint:** `python -m unittest discover -s scripts/tests -p test_run_guard.py` exits 0, and fails if an empty ready list reads complete.

## 7. Owned session recovery

A hung resumed session is only reported today, and any Claude session in the checkout blocks recovery. Over days, sessions will end, hang and lose their transcripts.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Track the run's own session in `scripts/lib/run_guard.py`. Done when: an unrelated session does not count as the run.
2. Recover it. Done when: a hung child and a missing session both resume.

- [ ] Hold the run's session and child identity in `scripts/lib/run_guard.py`, and treat only those as live. Done when: `scripts/tests/test_run_guard.py` proves an idle unrelated session does not block `watch`, and that a second writer is still refused while its session is holding the tree.
- [ ] Stop an owned child and its process tree after `CHILD_HANG_HOURS` with no change, unless `bosun status --json` shows it holding or waiting for a slot. Done when: a test stops a hung child and leaves one waiting in the queue. Cheaper substitute: reporting the hang and leaving it running.
- [ ] Start a new session with `claude -p "process plan"` when `--resume` fails because the transcript is gone. Done when: a fake CLI returning that error leads to one fresh start, recorded in the guard log.
- [ ] Add `--verify` to `scripts/run-guard.py status`, checking the task's action, principal, state and a tick within two intervals, for the repo's own task or for `Bosun heartbeat` when `bosun heartbeat list` holds the repo. Done when: a wrong action path reads unhealthy, and a repo on the heartbeat reads healthy with no task of its own. Cheaper substitute: checking that the task exists.
- [ ] Record a failed hook in `build/guard-health.json` from the fail-open path in `scripts/run-guard.py`. Done when: a corrupt state file shows up in `status`, and the session still is not blocked.
- [ ] Commit: `guard: owned session recovery`

**Test checkpoint:** `python -m unittest discover -s scripts/tests -p test_run_guard.py` exits 0, and fails if an unrelated session blocks `watch`.

## 8. Campaign permissions

A hidden session can't answer a prompt, so any prompt stalls the run. The owner chose bypass mode with a deny list, and told us on 2026-11-07 not to let caution block the run. So the list covers only what can't be undone, and a refusal returns its reason so the session carries on another way.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Add the deny rules to `.claude/settings.json`. Done when: each is refused in a real session.
2. Fix `resume_command`. Done when: a resumed session runs in the approved mode.

- [ ] Add deny rules in `.claude/settings.json` for force-push, `git push --delete`, `gh repo delete` and changing the repo's visibility, and for recursive deletes outside the repo, scratch, temp, and `build/`, through a `PreToolUse` hook. Done when: `scripts/tests/test_hook_commands.py` proves each is refused with a reason, while commits, pushes, branch work and cleaning scratch all pass. Cheaper substitute: an instruction asking the session not to.
- [ ] Refuse mail and chat sends in the same hook: the send tools of Gmail, Outlook and Teams connectors. Done when: a test sees those tool names refused, and web lookups and documentation tools pass.
- [ ] Set `permission_mode` to `bypassPermissions` for a run in `scripts/lib/run_guard.py`, validating it against `claude --help`. Done when: a test with an unknown mode refuses to start and names it. Cheaper substitute: forwarding any saved string.
- [ ] Prove a write, a queued build and a commit in a disposable repo through the exact `resume_command`, in `scripts/tests/test_guard_e2e.py`. Done when: the test passes on this machine with no prompt.
- [ ] Commit: `guard: campaign permissions`

**Test checkpoint:** `python -m unittest discover -s scripts/tests -p test_hook_commands.py` exits 0, and fails if force-push is allowed.

## 9. Named core tests and CMake resolution

Five test checkpoints pass a name to a test program that ignores it, so they can't fail for the reason they state, and the build command is not on PATH.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Add a name filter to `tests/test_core.cpp`. Done when: a name runs only its tests.
2. Resolve CMake. Done when: `cmake --version` works in a fresh session.

- [ ] Group the cases in `tests/test_core.cpp` by name and accept one name as an argument. Done when: an unknown name exits nonzero, and with no argument every implemented group runs. Cheaper substitute: ignoring the argument.
- [ ] Reserve the names `records`, `status_json`, `report_json`, `config` and `pressure` in `tests/test_core.cpp`, failing when selected and left out of the default run until implemented. Done when: selecting each exits nonzero, so a section can't pass without its tests, and an implemented group joins the default run.
- [ ] Add a CMake check to `scripts/campaign-preflight.ps1`, finding it through `vswhere` and requiring it on PATH. Done when: a PATH without CMake fails with the fix named. Cheaper substitute: a hardcoded Visual Studio path in a section.
- [ ] Add the Visual Studio CMake folder to the user PATH on this machine. Done when: a new shell and the watchdog's environment both run `cmake --version`.
- [ ] Commit: `tests: named core tests; preflight finds CMake`

**Test checkpoint:** `build\Release\test_core.exe no-such-test` and `build\Release\test_core.exe records` exit nonzero, and `build\Release\test_core.exe` exits 0.

## 10. Preflight and machine hold

One command proves the run can finish before anyone walks away. It holds the owner's decisions: logged in and unlocked, no sleep or update restarts, and the final .NET SDK installed when it ships. Every other section waits for this one. It is attended on purpose: it runs once with the owner present, by naming it to `process-todo-section`, and the unattended campaign starts only after it is stamped.

**Phase:** 0
**Needs:** attended
**Surface:** no

**Build order:**

1. Create `scripts/campaign-preflight.ps1`. Done when: each check prints pass or fail with its fix.
2. Create `scripts/campaign-hold.ps1` and `scripts/campaign-admin-setup.ps1`. Done when: holds apply and restore.

- [ ] Check in `scripts/campaign-preflight.ps1`: `bosun`, CMake, `dotnet`, `gh` auth, Codex sign-in and a real `gpt-6-astra` reply, WSL `Ubuntu`, `/mnt/d`, D: free space, a clean pushed tree, green CI on HEAD and `run-guard.py status --verify`. Done when: `tests/campaign_preflight_test.ps1` proves each fails with a faked environment, and each failure prints the command that fixes it. Cheaper substitute: a checklist in a document.
- [ ] Create `scripts/campaign-hold.ps1 -Apply` for sleep, display timeout and lock, saving prior values, with `-Restore`. Done when: settings read back and restore byte for byte. Cheaper substitute: asking the operator to remember.
- [ ] Create `scripts/campaign-admin-setup.ps1`, run once elevated, to pause Windows Update restarts and register a SYSTEM task that installs the final .NET 11 SDK side by side with `dotnet-install.ps1 -Channel 11.0 -Quality GA -InstallDir`, given the folder of the `dotnet.exe` that the watchdog's launch environment resolves, retrying daily. Done when: the task exists, before release its run changes nothing, and it removes itself only after that environment's `dotnet --list-sdks` shows a final 11.0.1xx SDK. Cheaper substitute: the script's default folder, which belongs to the SYSTEM account.
- [ ] Add `-Restore` to `scripts/campaign-admin-setup.ps1` for update settings. Done when: `docs/acceptance` lists this as a step.
- [ ] Run the preflight on this machine. Done when: it exits 0 and its output is quoted here.
- [ ] Prove recovery once: kill the session on the disposable repo and see the watchdog resume it. Done when: the guard log shows the restart.
- [ ] Commit: `campaign: preflight and machine hold`

**Test checkpoint:** `pwsh scripts/campaign-preflight.ps1` exits 0 on this machine, and exits nonzero with `gh` signed out.

## 11. One global heartbeat

> **Started:** 2026-11-07T18:49:50Z

The owner decided on 2026-11-07 that one heartbeat runs every repo from day one, including Bosun while it is being built, so other repos can be developed in parallel. Each repo keeps its run state, rules and hooks. One scheduled task runs the schedule, from an installed copy, so a campaign rebuilding Bosun can't disturb it. A task is used rather than a loop in the logger because it keeps firing if a process crashes. The panel's view of this comes later, in `D04 T03 §6`. **Corrected 2026-11-07** after the plan review: this was C++ in `src/app.cpp`, which has no JSON or crash-safe state, and it is now Python like the guard it drives, with the handoff, lock, durable state, WSL and config contracts added.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Create `scripts/heartbeat.py`. Done when: `tick` runs each registered repo's guard in turn, and `add`, `remove`, `list` and `status` work.
2. Install it from `scripts/install.ps1` with the `Bosun heartbeat` task. Done when: a real scheduled run updates the status.

- [x] Hold repos in `%LOCALAPPDATA%\Bosun\heartbeat\repos.json` with an atomic write, each with a path, a distro for WSL and a state of `adding`, `active` or `removing`. Done when: `scripts/tests/test_heartbeat.py` proves a missing file means no repos and a corrupt one ticks nothing and reports it. Cheaper substitute: a repo list in code.
- [x] Add `add <path> [--distro <name>]` in `scripts/heartbeat.py`, refusing a repo with no `scripts/run-guard.py`, naming what to copy, and refusing a duplicate. Done when: tests prove both refusals and that `list` reads the repo back.
- [x] Make the handoff crash safe in `scripts/heartbeat.py`: record `adding`, remove the repo's own task, then record `active`; record `removing`, restore its own task, then delete the entry. Done when: tests crash after each step and the next `tick` finishes the handoff, so a repo never ends with no watchdog. Cheaper substitute: running the two steps and hoping.
- [x] Treat an absent own task as already removed in `scripts/heartbeat.py`, checking with `schtasks /Query` first. Done when: tests prove a repo with no task adds cleanly, and a real permission error refuses and keeps the old state.
- [x] Remove a repo's own task again in each `tick` if it reappears, for example because an older kit's prompt told a session to install it. Done when: a test sees one removal with a note in the status, and `Steward` and `Resolute` need no change.
- [x] Update `real_task_installed` in `scripts/lib/run_guard.py` to count a repo listed in `repos.json` as installed, with the prompt hook naming `heartbeat.py add`. Done when: `scripts/tests/test_run_guard.py` proves a listed repo never recommends a second watchdog, and one with neither still warns.
- [x] Take a lock around `tick`, `add` and `remove` in `scripts/heartbeat.py` that the operating system releases on a crash. Done when: tests prove a second process's `tick` reports busy and changes nothing, and `add` waits, then refuses. Cheaper substitute: relying on Task Scheduler's instance policy.
- [x] Run `scripts/run-guard.py watch` per Windows repo, with an argument list, the repo as working directory, no window and a 120 second limit. Done when: tests prove a fake guard ticks, a hung one is stopped, the next repo still ticks, and a spaced path survives. Cheaper substitute: a shell string.
- [x] Run a WSL repo through `wsl.exe -d <distro> --cd <path> -- python3 scripts/run-guard.py watch`, only when `wsl.exe --list --running` shows the distro. Done when: tests prove a stopped distro stays stopped and reads "WSL not running". Cheaper substitute: a tick that wakes the VM.
- [x] Support WSL in `add` and `remove` with no task handoff, since a WSL repo's guard has no Windows task, refusing while the distro is stopped. Done when: `tests/heartbeat_test.ps1` adds, ticks and removes a temporary repo in the real distro with a fake guard.
- [x] Stop a pass after a guard prints exactly `resumed, attempt <N>`, not `resumed session still running`, saving where to carry on. Done when: tests in separate processes prove three dead repos resume across three passes, and a live child doesn't use the pass. Cheaper substitute: matching the word `resumed`.
- [x] Hold a pass while available memory is below `heartbeat_floor_mb` in `%LOCALAPPDATA%\Bosun\config.json`, default 2048, saving when the hold began, for at most 60 minutes, then tick anyway. Done when: tests across separate processes prove the hold, its reason in the status, the tick after the limit, a non-default value changing the result, and a malformed file using the default with a warning. `D01 T01 §5` later adds `config get` and `set` for this key.
- [x] Add `status` and `status --json` with `schema_version`: each repo's state, last tick, result and error, any hold, whether the last pass is stale, and the task's existence, enabled state and action. Done when: tests cover empty, never ticked, unreadable, stale, and a missing or disabled task. Cheaper substitute: printing a saved file.
- [x] Install `scripts/heartbeat.py` to `%LOCALAPPDATA%\Bosun\bin` with a `bosun-heartbeat.cmd` shim in `scripts/install.ps1`, and create the `Bosun heartbeat` task every 5 minutes through `pythonw.exe`, per user. Done when: `schtasks` shows that action, reinstalling keeps `repos.json`, and `schtasks /Run` updates the status with no window.
- [x] Move this machine to the heartbeat: add `D:\GitHub\Bosun`, `R:\GitHub\Steward` and `R:\GitHub\Resolute`, and delete the leftover `guard-e2e run watchdog` task. Done when: `bosun-heartbeat status` shows a tick for all three, with the task healthy, and `schtasks` shows no repo watchdog for them.
- [ ] Commit: `heartbeat: one global heartbeat`

**Test checkpoint:** `python -m unittest discover -s scripts/tests -p test_heartbeat.py` and `pwsh tests/heartbeat_test.ps1` exit 0, and fail if a stopped distro is started, a live child uses the pass, or a crash mid-handoff leaves no watchdog.

## Verification

- `pwsh scripts/campaign-preflight.ps1` exits 0.
- `python -m unittest discover -s scripts/tests` passes.
- `python scripts/todo-graph.py validate` and `python scripts/todo-graph.py plan --check` pass.
