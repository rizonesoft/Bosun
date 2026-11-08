---
schema_version: 1
id: kit-completion
domain: 00-workspace
status: active
title: "TODO-01 -- Finish the agent kit"
---

# TODO-01 -- Finish the agent kit

> **Goal:** This repo runs the newest review and run machinery from every sibling, tested here, so each later section is built and stamped through it.

> **Current state:** Measured 2026-11-07. Steward's guard, dispatcher, graph and prompts were copied in on 2026-11-07 and the SDK kit seeded the rest. Recto's review-in-flight marks, settled items and dispatch hook, and Isotone's gates, are not yet ported. The private repo `rizonetech/Bosun` exists, `origin` is set, and `build.yml`, `plan.yml` and Dependabot run on `main`.

## Inputs

- `docs/specs/2026-11-07-control-panel-design.md`, the table of sources.
- `R:\GitHub\Recto\scripts\review-dispatch.py`, `scripts/campaign_guard.py:780`, `scripts/dispatch_hook.py`.
- `R:\GitHub\Isotone\scripts\weakening-scan.py`, `native-std-check.py`, `native-pe-check.py`.
- -> XREF: D00 T02 §2 -- consumes this file
- -> XREF: D04 T02 §1 -- consumes this file
- -> XREF: D06 T01 §7 -- consumes this file
- -> XREF: D00 T03 §10 -- consumed by this file

## Outcome

- A review running in the background cannot be pushed by the Stop hook.
- Settled findings are not reopened, and an unattended run starts from one prompt with the watchdog installed.

**Adjacency:** all=not-applicable (internal tooling and contracts with no records of their own)

**Adjacency rationale:** Everything here is tooling for the repo itself. The surfaces that display it belong to `D04`.

## Implementation Order

| Order | Section | Deliverable | Depends On | Status |
| ----- | ------- | ----------- | ---------- | ------ |
| 1 | §1 | Review-in-flight marks for the guard | -- | [x] |
| 2 | §2 | Settled items in review prompts | §1 | [ ] |
| 3 | §3 | Dispatch hook for background reviews | §1 | [ ] |
| 4 | §4 | Pre-commit hook and clone setup | D00 T03 §10 | [ ] |
| 5 | §5 | Scheduled automations for this repo | §4 | [ ] |
| 6 | §6 | Gates: weakening scan and native checks | §4 | [ ] |
| 7 | §7 | Private GitHub repo and CI | §5 | [ ] |
| 8 | §8 | Run guard state in linked worktrees | §1 | [ ] |
| 9 | §9 | Register scheduled tasks on this machine | §5 | [ ] |

## 1. Review-in-flight marks for the guard

> **Started:** 2026-11-08T01:27:36Z

Steward's guard has no idea a review is running, so a Stop hook can push the session during the wait. Recto solved this with marks written by the dispatcher; this section ports them onto Steward's guard without changing its other rules.

**Corrected 2026-11-08:** Recto moved to `D:\GitHub\Recto`; its marks are the `in_flight` class in `scripts/review-dispatch.py:542` and the check is `review_in_flight` in `scripts/campaign_guard.py:780`. Bosun's `on_stop` already lets a turn end during a wait (`D00 T03 §4`); a live mark joins that, and neither counts the turn as idle.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Add the marks to `scripts/review-dispatch.py`, from Recto. Done when: a running dispatch leaves `build/review-in-flight/<pid>-<reviewer>.json` and removes it on exit.
2. Teach `scripts/lib/run_guard.py` to wait on a live mark. Done when: the Stop hook lets a turn end while a live mark exists and not after it expires.

- [x] Write the mark `{pid, reviewer, started, deadline}` in `scripts/review-dispatch.py`, citing Recto's `in_flight`. Done when: the file exists during a dispatch and is gone after success, failure and timeout. Cheaper substitute: writing the mark only on the success path.
- [x] Add `review_in_flight` to `on_stop` in `scripts/lib/run_guard.py`, citing `D:\GitHub\Recto\scripts\campaign_guard.py:780`. Done when: a stale mark from a dead process or past its deadline is ignored. Cheaper substitute: trusting the file's existence without checking the process.
- [x] Add cases to `scripts/tests/test_run_guard.py`. Done when: live, dead and expired marks each produce the expected Stop decision, and repeated Stops while a live mark stands leave `idle`, `yields` and the run active, with the turn sent back as usual once it is gone.
- [x] Refuse to launch a reviewer whose mark can't be written, in `scripts/review-dispatch.py` (where Recto only skips it), so a review never runs unprotected: exit 2 naming the folder. Done when: a test with an unwritable mark folder runs no CLI.
- [x] Prove the dispatcher's own marks in `scripts/tests/test_review_dispatch.py`: a fake reviewer sees its mark while it runs, the guard's `review_in_flight` reads it as live, and it is gone after success, failure and timeout. Done when: the tests fail if the dispatcher stops writing marks.
- [x] Commit: `guard: wait for a review in flight`

**Test checkpoint:** `python -m unittest scripts/tests/test_run_guard.py scripts/tests/test_review_dispatch.py` exits 0, and fails if either half is reverted.

> **Verified:** 2026-11-08 | §1 | fa91ebb4b747f44d274bf05a1d74355ef412dba8
> **Review:** `docs/reviews/d00-t01-s1.json`

## 2. Settled items in review prompts

Without settled items, a reviewer reopens decisions the writer already resolved, and rounds burn on old ground. Recto carries them into every prompt.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Port `SETTLED` and `settled_block` into `scripts/review-dispatch.py`. Done when: `- Filed:`, `- Not taken:` and `- Settled:` lines from the findings file appear in the next prompt.
2. Add `seconds` to the record and stdout JSON. Done when: `--list-rounds --json` reports it.

- [ ] Port `SETTLED` from `R:\GitHub\Recto\scripts\review-dispatch.py:326`. Done when: a findings file with a `- Settled:` line puts that line in the prompt. Cheaper substitute: pasting settled text by hand into prompts.
- [ ] Record `seconds` in `scripts/review-dispatch.py`. Done when: each record has a positive duration, and records without one still parse.
- [ ] Add cases to `scripts/tests/test_review_dispatch.py`. Done when: settled lines and durations are asserted, and a failed run records no approval.
- [ ] Commit: `review: carry settled items and durations`

**Test checkpoint:** `python -m unittest discover -s scripts/tests -p test_review_dispatch.py` exits 0, and fails if the change is reverted.

## 3. Dispatch hook for background reviews

> **Started:** 2026-11-08T01:44:56Z

A review must run so the session hears when it ends, and a stamp must not sit on an edited pin. Recto's hook enforces the first and Resolute's the second.

**Corrected 2026-11-08:** both now live at `D:\GitHub\Recto\scripts\dispatch_hook.py` and `D:\GitHub\Resolute\scripts\dispatch_hook.py`. Resolute's hook checks only `.conclave/reviewers.toml`, though its docstring also names `docs/reviews/`; the port checks the pin only, since the dispatcher appends each round to a findings file and the writer adds its notes there before they are committed, so refusing on findings edits would stop the campaign's own steps. A review can outlast the shell tool's 10 minute limit, so `run_in_background` is right, and the review skills say so. Claude Code has no non-interactive hook listing, so registration is proven by `scripts/tests/test_hook_commands.py` and by running the registered command.

**Design note 2026-11-08:** the hook reads shell text, so it is an early warning for the launch forms a session really uses, as its docstring says, not a proof that no spelling gets past it. What keeps the run correct however a review is launched is `D00 T01 §1`: the dispatcher's in-flight mark lets the turn end and holds `watch` while the review runs, and the run resumes when it ends. A detached review the hook misses therefore costs a later resume, not a lost review or a tree changed under the reviewer.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Create `scripts/dispatch_hook.py` from Recto's, adding Resolute's pin guard. Done when: a detached dispatch and an edited pin are each refused.
2. Wire it as PreToolUse on `Bash|PowerShell` in `.claude/settings.json`. Done when: a live session refuses `review-dispatch.py --prompt x &`.

- [x] Create `scripts/dispatch_hook.py` from `D:\GitHub\Recto\scripts\dispatch_hook.py`. Done when: `&`, `nohup`, `Start-Job` and a missing `run_in_background` exit 2; `--check-pin` passes. Cheaper substitute: matching only the Bash spelling and missing PowerShell.
- [x] Add the refusal while `.conclave/reviewers.toml` is modified or staged, from `D:\GitHub\Resolute\scripts\dispatch_hook.py`. Done when: `review-dispatch.py` and `todo-graph.py stamp` exit 2 on a dirty pin, and pass with a modified findings file. Cheaper substitute: a warning that lets the stamp through.
- [x] Add `scripts/tests/test_dispatch_hook.py`. Done when: each refusal and each allowed form is asserted, and a malformed payload fails open with a message.
- [x] Register the hook in `.claude/settings.json` on `Bash|PowerShell`. Done when: the settings parse, `scripts/tests/test_hook_commands.py` finds it and runs its command, and a payload through that command refuses a detached review.
- [x] Tell `review-todo-section/SKILL.md` and `process-todo-section/SKILL.md` how each agent runs a review and waits for it, since only Claude has this hook and `run_in_background`: Claude's mirror runs `review-dispatch.py` with `run_in_background` and acts on the completion notice; Codex's runs it in its shell, keeps the returned session and reads it with `write_stdin` until it exits; Antigravity's runs it in the foreground with a long enough timeout; each collects the final output and exit status before going on. Done when: a test reads each agent's own instruction in its mirror.
- [x] Prove the registered command lets a background review through, and a real session hears it end, in `scripts/tests/test_guard_e2e.py` under `BOSUN_E2E=1`: a session in a disposable repo starts a review against a fake reviewer with `run_in_background`, and reports its exit status and verdict, once for a review that returns and once for one that fails. Done when: the test passes on this machine, and the hook-registration tests are in the checkpoint.
- [ ] Commit: `hooks: refuse detached reviews and dirty pins`

**Test checkpoint:** `python -m unittest scripts/tests/test_dispatch_hook.py scripts/tests/test_hook_commands.py` exits 0, and fails if the change is reverted; `BOSUN_E2E=1 python -m unittest scripts/tests/test_guard_e2e.py` proves a background review reaches the session.

## 4. Pre-commit hook and clone setup

The plan must never be committed out of step with its sections. The kit carries the hook, but a clone has to opt in.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Adapt `scripts/hooks/pre-commit` to this repo. Done when: a staged TODO edit with a stale plan is refused.
2. Add `scripts/setup-clone.ps1`. Done when: it sets `core.hooksPath`, and a second run changes nothing.

- [ ] Adapt `scripts/hooks/pre-commit` from the kit. Done when: a commit staging `todo/` while `plan --check` fails exits nonzero. Cheaper substitute: running the check only in CI.
- [ ] Create `scripts/setup-clone.ps1` to set `core.hooksPath` and install the watchdog through `scripts/run-guard.py install`, which replaces any existing task of that name. This clone's watchdog was installed on 2026-11-07, so running it here changes nothing. Done when: `git config core.hooksPath` prints `scripts/hooks` and `run-guard.py status` reports `watchdog_installed` true. Cheaper substitute: documenting the steps without a script.
- [ ] Add a temporary-repo test in `scripts/tests/test_pre_commit.py`. Done when: a stale plan commit is refused, and a synced one lands.
- [ ] Commit: `tooling: pre-commit hook and clone setup`

**Test checkpoint:** `python -m unittest discover -s scripts/tests -p test_pre_commit.py` exits 0, and fails if the change is reverted.

## 5. Scheduled automations for this repo

Toolchain drift should arrive as a notice, not a surprise. The kit's runner is copied in but names no tasks, and its alert names another project.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Fill `.conclave/automations/tasks.json`. Done when: `run.py` validates the tasks with `folder` set to `\Bosun\`.
2. Port the pin checks and repoint `alert.py`. Done when: a failed task writes `last.json` and the alert path is exercised in a test.

- [ ] Port `check-python-pin.py` and `check-visual-studio.py` from `R:\GitHub\Recto\scripts`, and add `check-dotnet-pin.py` from `R:\GitHub\Isotone\scripts`. Done when: each exits 0 on a current pin and nonzero on a stale fixture.
- [ ] Set `bosun-python-pin`, `bosun-visual-studio` and `bosun-dotnet-pin` in `.conclave/automations/tasks.json`. Done when: the validator in `run.py` accepts it. Cheaper substitute: a task whose name lacks the folder prefix.
- [ ] Point `.conclave/automations/alert.py` at the operator's address, with the key source unchanged. Done when: `scripts/tests/test_automations.py` covers the failure path with a stub sender, sending no real mail. Cheaper substitute: sending a real test email from a test.
- [ ] Commit: `automations: pin checks on a schedule`

**Test checkpoint:** `python -m unittest discover -s scripts/tests -p test_automations.py` exits 0, and fails if the change is reverted.

## 6. Gates: weakening scan and native checks

A test deleted or a tolerance widened to get to green must be visible to the reviewer. Isotone's scan does this generically, and its native checks catch compiler flag drift.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Copy the three scripts from Isotone and adapt their paths. Done when: each runs on this repo.
2. Add `scripts/check-all.ps1` as the one-command gate. Done when: it runs every gate and reports the first failure.

- [ ] Port `scripts/weakening-scan.py`. Done when: a fixture diff deleting a test is flagged, and a plain feature diff is not.
- [ ] Port `scripts/native-std-check.py` and `scripts/native-pe-check.py` against `CMakeLists.txt`. Done when: the built `bosun.exe` passes, and a fixture without CFG fails. Cheaper substitute: skipping PE checks when the build is absent without saying so.
- [ ] Create `scripts/check-all.ps1` covering `validate`, `plan --check`, tier check, C++ tests, tooling tests and, once present, `dotnet test`. Done when: it exits nonzero when any step fails, and heavy steps go through `bosun run`.
- [ ] Commit: `gates: weakening scan, native checks, check-all`

**Test checkpoint:** `pwsh scripts/check-all.ps1` exits 0 on a clean tree, and exits nonzero with a deliberately stale plan.

## 7. Private GitHub repo and CI

The kit's CI gate and merge script need a remote. The owner asked for a private repo in the Rizonetech organization on 2026-11-07, with the finish of a public repo in case it is published later.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Create the private repo and push `main`. Done when: `git remote -v` shows it.
2. Add `.github/workflows/build.yml` and `plan.yml`, and port `merge_prs.py`. Done when: `ci_status.py` reports green.

- [x] Create `rizonetech/Bosun` as private with `gh repo create`. Done when: `gh repo view --json visibility` says PRIVATE. Cheaper substitute: a public repo.
- [ ] Add `.github/workflows/plan.yml` and `build.yml` with pinned actions, Windows runner. Done when: both pass on `main`, and a stale plan turns `plan.yml` red on a disposable pull request branch, closed afterwards. Actions stay on major tags, kept current by Dependabot.
- [ ] Port `scripts/merge_prs.py` from `R:\GitHub\Recto\scripts` and add `bosun-pr-merge` to `.conclave/automations/tasks.json`. Done when: a dry run lists only green Dependabot pull requests.
- [ ] Commit: `ci: private repo, workflows and merge task`

**Test checkpoint:** `python scripts/ci_status.py` exits 0 on `main`, and exits 1 after a deliberately red run.

## 8. Run guard state in linked worktrees

Overflow runs use linked worktrees, where `.git` is a file, so `scripts/lib/run_guard.py:32` can't create its state. The guard owns this, so the fix goes there, and sibling kits can port it later.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Resolve state through `git rev-parse --git-path run-guard`. Done when: a linked worktree gets its own state folder.
2. Keep the watchdog out of worktrees. Done when: `install` in a linked worktree refuses and says why.

- [ ] Replace the `.git` path in `scripts/lib/run_guard.py:32` with the `--git-path` result, keeping `RUN_GUARD_STATE` first. Done when: state in the main checkout stays at `.git/run-guard`, and a worktree's lives under its own git directory. Cheaper substitute: a special case that skips the guard in worktrees.
- [ ] Refuse `run-guard.py install` from a linked worktree, since overflow runs are bounded by a budget. Done when: a test in a temporary worktree gets a clear refusal and no scheduled task.
- [ ] Add linked-worktree cases to `scripts/tests/test_run_guard.py`. Done when: two checkouts of one repo hold separate states, and the Stop hook works in each.
- [ ] Commit: `guard: state in linked worktrees`

**Test checkpoint:** `python -m unittest discover -s scripts/tests -p test_run_guard.py` exits 0, and fails if the change is reverted.

## 9. Register scheduled tasks on this machine

Registering this repo's own per-user tasks changes this machine, outside the repo. The owner cleared every waiting step for the unattended run on 2026-11-07.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Run the installer. Done when: the tasks are registered.
2. Run one task. Done when: its `last.json` exists.

- [ ] Run `.conclave/automations/install.ps1`. Done when: `Get-ScheduledTask -TaskPath \Bosun\` lists the tasks, each running through `pythonw.exe`. Cheaper substitute: a task that opens a console window.
- [ ] Run `python .conclave/automations/run.py bosun-python-pin`. Done when: `build/automations/bosun-python-pin/last.json` records its exit.
- [ ] Commit: `automations: register on this machine`

**Test checkpoint:** `Get-ScheduledTask -TaskPath \Bosun\` lists all tasks, and `build/automations/bosun-python-pin/last.json` exists.

## Verification

- `pwsh scripts/check-all.ps1` passes.
- `python scripts/todo-graph.py validate` and `python scripts/todo-graph.py plan --check` pass.
