---
schema_version: 1
id: kit-completion
domain: 00-workspace
status: active
title: "TODO-01 -- Finish the agent kit"
---

# TODO-01 -- Finish the agent kit

> **Goal:** This repo runs the newest review and run machinery from every sibling, tested here, so each later section is built and stamped through it.

> **Current state:** Measured 2026-11-07. Steward's guard, dispatcher, graph and prompts were copied in on 2026-11-07 and the SDK kit seeded the rest. Recto's review-in-flight marks, settled items and dispatch hook, and Isotone's gates, are not yet ported. No GitHub remote exists.

## Inputs

- `docs/specs/2026-11-07-control-panel-design.md`, the table of sources.
- `R:\GitHub\Recto\scripts\review-dispatch.py`, `scripts/campaign_guard.py:780`, `scripts/dispatch_hook.py`.
- `R:\GitHub\Isotone\scripts\weakening-scan.py`, `native-std-check.py`, `native-pe-check.py`.
- -> XREF: D00 T02 §2 -- consumes this file
- -> XREF: D04 T02 §1 -- consumes this file
- -> XREF: D06 T01 §7 -- consumes this file

## Outcome

- A review running in the background cannot be pushed by the Stop hook.
- Settled findings are not reopened, and an unattended run starts from one prompt with the watchdog installed.

**Adjacency:** all=not-applicable (internal tooling and contracts with no records of their own)

**Adjacency rationale:** Everything here is tooling for the repo itself. The surfaces that display it belong to `D04`.

## Implementation Order

| Order | Section | Deliverable | Depends On | Status |
| ----- | ------- | ----------- | ---------- | ------ |
| 1 | §1 | Review-in-flight marks for the guard | -- | [ ] |
| 2 | §2 | Settled items in review prompts | §1 | [ ] |
| 3 | §3 | Dispatch hook for background reviews | §1 | [ ] |
| 4 | §4 | Pre-commit hook and clone setup | -- | [ ] |
| 5 | §5 | Scheduled automations for this repo | §4 | [ ] |
| 6 | §6 | Gates: weakening scan and native checks | §4 | [ ] |
| 7 | §7 | Private GitHub repo and CI | §5 | [ ] |
| 8 | §8 | Run guard state in linked worktrees | §1 | [ ] |
| 9 | §9 | Register scheduled tasks on this machine | §5 | [ ] |

## 1. Review-in-flight marks for the guard

Steward's guard has no idea a review is running, so a Stop hook can push the session during the wait. Recto solved this with marks written by the dispatcher; this section ports them onto Steward's guard without changing its other rules.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Add the marks to `scripts/review-dispatch.py`, from Recto. Done when: a running dispatch leaves `build/review-in-flight/<pid>-<reviewer>.json` and removes it on exit.
2. Teach `scripts/lib/run_guard.py` to wait on a live mark. Done when: the Stop hook lets a turn end while a live mark exists and not after it expires.

- [ ] Write the mark `{pid, reviewer, started, deadline}` in `scripts/review-dispatch.py`, citing Recto's `in_flight`. Done when: the file exists during a dispatch and is gone after success, failure and timeout. Cheaper substitute: writing the mark only on the success path.
- [ ] Add `review_in_flight` to `on_stop` in `scripts/lib/run_guard.py`, citing `R:\GitHub\Recto\scripts\campaign_guard.py:780`. Done when: a stale mark from a dead process or past its deadline is ignored. Cheaper substitute: trusting the file's existence without checking the process.
- [ ] Add cases to `scripts/tests/test_run_guard.py`. Done when: live, dead and expired marks each produce the expected Stop decision.
- [ ] Commit: `guard: wait for a review in flight`

**Test checkpoint:** `python -m unittest discover -s scripts/tests -p test_run_guard.py` exits 0, and fails if the change is reverted.

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

A review must run so the session hears when it ends, and a stamp must not sit on an edited pin. Recto's hook enforces the first and Resolute's the second.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Create `scripts/dispatch_hook.py` from Recto's, adding Resolute's pin guard. Done when: a detached dispatch and an edited pin are each refused.
2. Wire it as PreToolUse on `Bash|PowerShell` in `.claude/settings.json`. Done when: a live session refuses `review-dispatch.py --prompt x &`.

- [ ] Create `scripts/dispatch_hook.py` from `R:\GitHub\Recto\scripts\dispatch_hook.py`. Done when: `&`, `nohup`, `Start-Job` and a missing `run_in_background` exit 2; `--check-pin` passes. Cheaper substitute: matching only the Bash spelling and missing PowerShell.
- [ ] Add the refusal while `.conclave/reviewers.toml` or `docs/reviews/` is modified, from `R:\GitHub\Resolute\scripts\dispatch_hook.py`. Done when: `review-dispatch.py` and `todo-graph.py stamp` exit 2 on a dirty pin. Cheaper substitute: a warning that lets the stamp through.
- [ ] Add `scripts/tests/test_dispatch_hook.py`. Done when: each refusal and each allowed form is asserted, and a malformed payload fails open with a message.
- [ ] Register the hook in `.claude/settings.json`. Done when: `python -c` JSON parse passes and the hook appears in `claude` hook listing.
- [ ] Commit: `hooks: refuse detached reviews and dirty pins`

**Test checkpoint:** `python -m unittest discover -s scripts/tests -p test_dispatch_hook.py` exits 0, and fails if the change is reverted.

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

1. Create the private repo and push `main`, once answered. Done when: `git remote -v` shows it.
2. Add `.github/workflows/build.yml` and `plan.yml`, and port `merge_prs.py`. Done when: `ci_status.py` reports green.

- [x] Create `rizonetech/Bosun` as private with `gh repo create`. Done when: `gh repo view --json visibility` says PRIVATE. Cheaper substitute: a public repo.
- [ ] Add `.github/workflows/plan.yml` and `build.yml` with pinned actions, Windows runner. Done when: both pass on `main`, and a stale plan turns `plan.yml` red.
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

Registering tasks changes this machine, outside the repo, so a run leaves it for the operator.

**Phase:** 0
**Needs:** attended
**Surface:** no

**Build order:**

1. Run the installer with the operator present. Done when: the tasks are registered.
2. Run one task. Done when: its `last.json` exists.

- [ ] Run `.conclave/automations/install.ps1`. Done when: `Get-ScheduledTask -TaskPath \Bosun\` lists the tasks, each running through `pythonw.exe`. Cheaper substitute: a task that opens a console window.
- [ ] Run `python .conclave/automations/run.py bosun-python-pin`. Done when: `build/automations/bosun-python-pin/last.json` records its exit.
- [ ] Commit: `automations: register on this machine`

**Test checkpoint:** `Get-ScheduledTask -TaskPath \Bosun\` lists all tasks, and `build/automations/bosun-python-pin/last.json` exists.

## Verification

- `pwsh scripts/check-all.ps1` passes.
- `python scripts/todo-graph.py validate` and `python scripts/todo-graph.py plan --check` pass.
