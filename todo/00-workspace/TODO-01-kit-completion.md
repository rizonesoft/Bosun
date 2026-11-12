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

**Corrected 2026-11-10:** every sibling repo is under `R:\GitHub` again, since the `D:` volume was retired; paths in open sections are updated, and stamped sections keep theirs as history.

**Corrected 2026-11-12:** `§10` (kit updates from the three sample repos) and `§11` (removal of the samples folder) are withdrawn, unbuilt, on the owner's decision of 2026-11-12: Bosun and the Conclave-SDK kits stay separate, so Bosun imports nothing further from the SDK or its retired sibling repos. The owner removed `samples/` himself and it stays gone. The numbers 10 and 11 are not reused.

## Inputs

- `docs/specs/2026-11-07-control-panel-design.md`, the table of sources.
- `R:\GitHub\Recto\scripts\review-dispatch.py`, `scripts/campaign_guard.py:780`, `scripts/dispatch_hook.py`.
- `R:\GitHub\Isotone\scripts\weakening-scan.py`, `native-std-check.py`, `native-pe-check.py`.
- -> XREF: D00 T02 §2 -- consumes this file
- -> XREF: D00 T03 §10 -- consumed by this file
- -> XREF: D00 T02 §4 -- consumes this file

## Outcome

- A review running in the background cannot be pushed by the Stop hook.
- Settled findings are not reopened, and an unattended run starts from one prompt with the watchdog installed.

**Adjacency:** all=not-applicable (internal tooling and contracts with no records of their own)

**Adjacency rationale:** Everything here is tooling for the repo itself. Nothing in the product displays it: the panel stays out of each repo's workflow (owner decision, 2026-11-10).

## Implementation Order

| Order | Section | Deliverable | Depends On | Status |
| ----- | ------- | ----------- | ---------- | ------ |
| 1 | §1 | Review-in-flight marks for the guard | -- | [x] |
| 2 | §2 | Settled items in review prompts | §1 | [x] |
| 3 | §3 | Dispatch hook for background reviews | §1 | [x] |
| 4 | §4 | Pre-commit hook and clone setup | D00 T03 §10 | [x] |
| 5 | §5 | Scheduled automations for this repo | §4 | [ ] |
| 6 | §6 | Gates: native build checks and check-all | §4 | [x] |
| 7 | §7 | Private GitHub repo and CI | §5 | [ ] |
| 8 | §9 | Register scheduled tasks on this machine | §5 | [ ] |
| 9 | §12 | Build and dev tool stack measured and tuned | -- | [x] |
| 10 | §13 | Weakening scan over a diff | §6 | [ ] |

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

> **Started:** 2026-11-08T14:52:18Z

Without settled items, a reviewer reopens decisions the writer already resolved, and rounds burn on old ground. Recto carries them into every prompt.

**Corrected 2026-11-08:** Recto reads settled lines from every panel member's findings file; Bosun routes each writer to one reviewer, so the files are the `--record` file and the section's other cycle files (`D<NN>-T<NN>-s<N>.md` and `-cycle<n>.md`), which a repair keeps. Recto also reads lines inside a recorded review, so a reviewer quoting `- Settled:` would settle its own finding; here only the writer's lines outside recorded reviews count. The section said nothing on where writers learn the line forms, so the review skill now says so. A duration under one second rounds to 0 in Recto; here it rounds up, so a recorded duration is always positive.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Port `SETTLED`, `settled_items` and `settled_block` into `scripts/review-dispatch.py`. Done when: `- Filed:`, `- Not taken:` and `- Settled:` lines from the section's findings files appear in the next prompt.
2. Add `seconds` to the record and stdout JSON. Done when: `--list-rounds --json` reports it.

- [x] Port `SETTLED`, `settled_items` and `settled_block` from `D:\GitHub\Recto\scripts\review-dispatch.py:326` into `scripts/review-dispatch.py`, appending the block to every pass's prompt (plan, round and completion). Done when: a findings file with a `- Settled:` line puts that line in the prompt the fake reviewer receives. Cheaper substitute: pasting settled text by hand into prompts.
- [x] Read settled lines from the `--record` file and the section's other cycle files only outside recorded review bodies, in `settled_items` in `scripts/review-dispatch.py`. Done when: a `- Settled:` line inside a recorded review is not carried, and one in an earlier cycle file is. Cheaper substitute: scanning every line, as Recto does.
- [x] Refuse (exit 2, naming the file) before any reviewer runs when an existing cycle file of the section cannot be read or its review boundaries cannot be established, in `settled_items` in `scripts/review-dispatch.py`, where Recto skips it silently; an absent `--record` file is not an error. Done when: a test with an unreadable earlier cycle file runs no CLI and names the file, and a first pass on a new record still runs. Cheaper substitute: skipping the file and losing its settled items. (Plan review P2 1.) **Corrected 2026-11-08:** this covers the section's other cycle files; the `--record` file itself keeps `D06 T01 §8`'s handling, where a returned review is refused a place in an unreadable file (exit 2, stdout only), so no recorded pass runs without its settled items.
- [x] Write the prompt with its settled block to the pointer file an Antigravity reviewer reads, in `review` in `scripts/review-dispatch.py`, which today writes that file only when lenses are scheduled. Done when: fake Codex (stdin) and fake Antigravity (pointer file) each receive the settled lines on a plan, a round and a completion pass. (Plan review P2 3.)
- [x] Record `seconds`, the measured elapsed time rounded up so it is always positive, on every pass in `review` in `scripts/review-dispatch.py`, as a `- Seconds:` line in the findings file and `seconds` in stdout JSON, with `_read_findings` reading it as optional. Done when: a test with a stubbed clock gets 1 for 0.2 seconds and 3 for 2.4 seconds, in stdout, in the findings file and in `--list-rounds --json`, and records without the line still parse. Cheaper substitute: a constant 1. (Plan review P2 2.)
- [x] Add cases to `scripts/tests/test_review_dispatch.py`. Done when: settled lines across all three pass kinds and both prompt routes, the unreadable-file refusal and durations are asserted, a failed run records no approval, and the tests fail if the settled block or the `Seconds` line is dropped.
- [x] Keep a failed attempt's duration as `last_seconds` and the pass's running `seconds_spent` in `after_attempt` in `scripts/review-dispatch.py`, so a failure that waits (exit 3, no stdout) still keeps its time. Done when: a test reads both from `build/review-attempts.json` after a plain failure and after an auth wait. (Completion review gap 1.)
- [x] Hand a returned review back on stdout, exit 2, when the findings write itself fails with `OSError`, in `main` in `scripts/review-dispatch.py`, as an `Unrecorded` one already is. Done when: a test with a full disk on the findings write gets the review and its `seconds` on stdout, names the error, and finds the earlier file intact. (Completion review gap 2.)
- [x] Say in `.claude/skills/review-todo-section/SKILL.md` and its `.agents` and `.codex` mirrors that a `- Settled: <item>` or `- Not taken: <item> (<reason>)` line written under a review in the findings file is carried into every later prompt. Done when: a test reads the rule in all three mirrors.
- [x] Commit: `review: carry settled items and durations`

**Test checkpoint:** `python -m unittest discover -s scripts/tests -p test_review_dispatch.py` exits 0, and fails if the change is reverted.

> **Verified:** 2026-11-08 | §2 | db2f1bd816f86e1e079d6448089e2e6f5e992e63
> **Review:** `docs/reviews/d00-t01-s2.json`

## 3. Dispatch hook for background reviews

> **Started:** 2026-11-08T02:58:25Z

A review must run so the session hears when it ends, and a stamp must not sit on an edited pin. Recto's hook enforces the first and Resolute's the second.

**Corrected 2026-11-08:** both now live at `D:\GitHub\Recto\scripts\dispatch_hook.py` and `D:\GitHub\Resolute\scripts\dispatch_hook.py`. Resolute's hook checks only `.conclave/reviewers.toml`, though its docstring also names `docs/reviews/`; the port checks the pin only, since the dispatcher appends each round to a findings file and the writer adds its notes there before they are committed, so refusing on findings edits would stop the campaign's own steps. A review can outlast the shell tool's 10 minute limit, so `run_in_background` is right, and the review skills say so. Claude Code has no non-interactive hook listing, so registration is proven by `scripts/tests/test_hook_commands.py` and by running the registered command.

**Threat model 2026-11-08 (cycle 2):** the deny list and this hook guard against the campaign session's own mistakes in the spellings a session really writes. Cycle 1 spent its 33 rounds on ever rarer shell spellings (`$'...'` escapes, nested `env -C`, NUL bytes); each was fixed, but a session that obscures a command to get past its own guard already controls the tools, so such a spelling is `P3` under the round prompt's proportionality rule. The hard guarantees sit elsewhere: `D00 T01 §1`'s in-flight marks keep a review correct however it is launched, and the remote's branch protection, once `D00 T03 §17` has it on, is what makes a force push impossible.

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
- [x] State both hooks' threat model in `scripts/tool-guard.py` and `scripts/dispatch_hook.py`, and here: they catch the campaign session's own honest mistakes in the spellings a session writes, and a session that obscures a command to get past its own guard already controls the tools, which the round prompt's proportionality rule makes `P3`; what keeps reviews correct however they are launched is `D00 T01 §1`'s in-flight marks, and what would make a force push impossible is the remote's own branch protection, which `D00 T03 §17` turns on and reads back once the owner agrees. Done when: a test reads the statement in both hooks.
- [x] Copy `tool-guard.py` into the real-session test's repo, which the hook imports, and first prove the installed hook refuses a detached launch there; and have `scripts/dispatch_hook.py` report a parser it can't load rather than crash. Done when: the test fails if the hook can't start, and a hook without its parser says so and lets the call through.
- [x] Assert the verdict in the real-session test, with a fake reviewer that returns `approve`, one that returns `needs-attention` and one that fails, each reported as its exit status and its verdict line. Done when: a lost or misreported negative review fails the test.
- [x] Read Bash's `$'...'` as one quoted word in `split_chain` in `scripts/tool-guard.py` and in `parse` in `scripts/dispatch_hook.py`, where a backslash escapes its closing quote. Done when: a test refuses `git push origin $'a\';b' --force` and the like.
- [x] Commit: `hooks: refuse detached reviews and dirty pins`

**Test checkpoint:** `python -m unittest scripts/tests/test_dispatch_hook.py scripts/tests/test_tool_guard.py scripts/tests/test_hook_commands.py` exits 0, and fails if the change is reverted; `BOSUN_E2E=1 python -m unittest scripts/tests/test_guard_e2e.py` proves a background review and its verdict reach the session. **Corrected 2026-11-08:** the parser changes reach `scripts/tool-guard.py`, so its suite is in.

> **Repair cycle:** 1: 2026-11-08: its 33 review rounds ran out on ever rarer shell spellings; cycle 2 states the hooks' threat model (after: its 33 review rounds ran out with findings open)

> **Verified:** 2026-11-08 | §3 | fbd42c6418e8acd873f892b292956f7ecc17e0db
> **Review:** `docs/reviews/d00-t01-s3.json`

## 4. Pre-commit hook and clone setup

> **Started:** 2026-11-12T03:54:08Z

The plan must never be committed out of step with its sections. The hook that refuses such a commit is in the repo, but a clone has to opt in, and nothing tests the hook.

**Corrected 2026-11-12:** measured before the build. `scripts/hooks/pre-commit` is already adapted to this repo: it refuses a commit that stages `todo/` or `docs/reviews/` while the graph is invalid or the plan is stale, and one with unstaged or untracked files in those folders; this clone's `core.hooksPath` is `scripts/hooks`, and it has refused and passed commits throughout the 2026-11-12 run. So the first item is to prove the hook, not to adapt it. The clone setup no longer installs the watchdog by default: `AGENTS.md` says Bosun needs no unattended run, and a scheduled task is not something a contributor's clone should get unasked. `-Watchdog` installs it for a machine that runs the plan unattended, and leaves an installed one alone, so a setup run during a live run does not replace the task that guards it. Decision taken as the default an expert would defend; reversing it is deleting the switch.

**Corrected 2026-11-12 (plan review):** the plan review in `docs/reviews/00-workspace/D00-T01-s4.md` returned six gaps, all planned in below. Whether the watchdog is installed for this checkout is asked of the guard that owns it, which already checks the task's target, user and enabled state (`verify` in `scripts/lib/run_guard.py`) and now says which of four states the task is in, so the setup never reads its wording and never installs after an answer it could not read. The hook test covers every refusal the hook has, in a fixture that is shown to validate first. `-Repo` is defined and tested from elsewhere, and every change is previewed, read back and printed with the command that reverses it.

**Corrected 2026-11-12 (build):** one item changed and one was added. Removing the hook's `validate` call left every hook test passing, because `plan --check` loads the graph first and refuses an invalid one itself. The call only loaded the graph a second time, about 8 seconds on every commit touching the plan on this machine, so it is removed and the invalid-graph case stands against the one remaining call. The item that said the case fails without the `validate` call is reworded to what is true.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Prove `scripts/hooks/pre-commit` in a temporary repository. Done when: every refusal it has is exercised, and a synced plan lands.
2. Let the guard say what state its task is in. Done when: `status --verify --json` carries it.
3. Add `scripts/setup-clone.ps1`. Done when: it sets `core.hooksPath`, and a second run changes nothing.

- [x] Add `scripts/tests/test_pre_commit.py` with a self-contained temporary repository: a valid TODO tree with its `todo/plan.json` and generated plan, `scripts/todo-graph.py`, `scripts/review-dispatch.py`, `scripts/lib/todo_graph.py`, `scripts/lib/host_caps.py` and the real `scripts/hooks/pre-commit`, committed through `git` with `core.hooksPath` set. Done when: the fixture's own `validate` and `plan --check` exit 0 before any hook case runs, a commit staging a TODO edit with a stale plan exits nonzero and names `plan --sync`, and the same commit lands once the plan is synced and staged. Cheaper substitute: running the check only in CI, which lets the stale plan reach `main` first.
- [x] Cover what the hook guards beyond `todo/` in the same test: a commit touching only `docs/reviews/` is checked too, and a commit staging nothing under either folder lands though the plan is stale. Done when: both are asserted, and the first fails if `docs/reviews/` is taken out of the hook's first line.
- [x] Cover the working-tree refusals: an unstaged change, an untracked file and an ignored file, each under `todo/` and under `docs/reviews/`, are each refused naming the folder. Done when: the six cases are asserted and fail if the hook's loop over the two folders is removed.
- [x] Cover an invalid graph: a staged TODO file that fails `validate` is refused. Done when: the case fails if the hook's `plan --check` call, which validates the graph first, is removed.
- [x] Remove the separate `validate` call from `scripts/hooks/pre-commit`, which loaded the graph a second time and refused nothing `plan --check` does not. Done when: every case in `scripts/tests/test_pre_commit.py` still passes, the invalid-graph one included, and the hook's message says what to do for a refused graph as well as a stale plan.
- [x] Cover interpreter discovery with stand-in `python3` and `python` commands first on `PATH`: a `python3` that does not run falls back to `python`, and with neither running the commit is refused saying Python is needed. Done when: both are asserted on Windows and Linux.
- [x] Add `task` to what `verify` in `scripts/lib/run_guard.py` returns: `ok` (the task runs this checkout's guard, as this user, enabled), `missing`, `mismatched` (it exists but is another checkout's, another user's or disabled) or `unreadable`. Done when: `scripts/tests/test_run_guard.py` covers the four, and `python scripts/run-guard.py status --verify --json` prints it under `verify`. Cheaper substitute: the setup matching the words of `problems`, which change.
- [x] Create `scripts/setup-clone.ps1 [-Repo <folder>] [-Watchdog]`. `-Repo` defaults to the checkout the script is in, every `git` call names it with `-C`, and the setting is written with `--local`. It sets `core.hooksPath` to `scripts/hooks` when it is not that already, printing the old value and the command that puts it back, reads the setting back before saying it is set, and says nothing needed doing when it already was. Done when: `tests/setup_clone_test.ps1` shows a fresh clone gets the setting, a second run changes nothing, a run from another folder with `-Repo` naming a clone whose path has a space changes that clone and leaves a second clone untouched. Cheaper substitute: documenting the `git config` line without a script.
- [x] Refuse in `scripts/setup-clone.ps1` with exit 1 and the cause: the folder is not a git checkout, has no `scripts/hooks/pre-commit`, `git` is not on `PATH`, or the setting could not be written or does not read back. Done when: the test covers each, with a `config.lock` left in the clone's `.git` as the write that fails, and shows the previous value is still there.
- [x] With `-Watchdog`, ask `python scripts/run-guard.py status --verify --json` what state the task is in and act on `verify.task` alone: `ok` is left alone; `missing` prints the task's name, the checkout and what it will run, installs it through `python scripts/run-guard.py install`, asks again, and reports success only when it now reads `ok`; `mismatched` and `unreadable` exit 1 with the guard's problems, change nothing, and say how to recover (rename this folder when another checkout owns the task, else `python scripts/run-guard.py uninstall` and run again). A status that fails, is not JSON or has no known `verify.task`, and a missing `python`, exit 1 and install nothing. Done when: `tests/setup_clone_test.ps1`, with a stand-in `run-guard.py` in the temporary clone, covers each state, a failed install, an install that exits 0 and registers nothing, and that no `-Watchdog` never runs the guard. Cheaper substitute: always installing, which replaces the task guarding a live run.
- [x] Run `pwsh scripts/setup-clone.ps1` and `pwsh scripts/setup-clone.ps1 -Watchdog` in this clone. Done when: both report nothing to do, `git config --local core.hooksPath` prints `scripts/hooks` and `python scripts/run-guard.py status --verify --json` reports `verify.task` as `ok`.
- [x] Document it in `CONTRIBUTING.md` and the hooks line of `AGENTS.md`: what `pwsh scripts/setup-clone.ps1` does, `-Repo`, when to add `-Watchdog` and what it needs (Python, Windows Task Scheduler), and how to undo each (`git config --local --unset core.hooksPath`, `python scripts/run-guard.py uninstall`). Add `tests/setup_clone_test.ps1` to the Windows job in `.github/workflows/build.yml`. Done when: both documents carry those points and the step is listed.
- [x] Commit: `tooling: pre-commit hook and clone setup`

**Test checkpoint:** `python -m unittest discover -s scripts/tests -p test_pre_commit.py`, `python scripts/run-tests.py test_run_guard` and `pwsh tests/setup_clone_test.ps1` exit 0, and fail if the hook's plan check is removed, the setup stops setting `core.hooksPath`, or the setup installs over a task it could not verify.

> **Verified:** 2026-11-12 | §4 | ae4518ac1a120de811caf09411da90b0138aec25
> **Review:** `docs/reviews/d00-t01-s4.json`

## 5. Scheduled automations for this repo

Toolchain drift should arrive as a notice, not a surprise. The kit's runner is copied in but names no tasks, and its alert names another project.

**Corrected 2026-11-12 (held for the owner):** checked against the repo before building, and the section is held on a scope question only the owner can settle. What was found: (1) the Python and Visual Studio checks are about this machine, not this repo, and Recto's `recto-python-pin` and `recto-visual-studio` tasks already run them weekly on this machine and email the same address, so Bosun's copies would send the same news twice; (2) `check-dotnet-pin.py` does not exist in Isotone, Bosun has no `global.json` until `D00 T02 §1`, and the .NET pin already has its own section and wake check (`D00 T02 §3`, `scripts/dotnet-final-available.py`); (3) the sibling repos are under `D:\`, not `R:\GitHub`; (4) `.conclave/automations/alert.py` already sends to the operator's address, so only its failure-path test is missing; (5) the alert reads its key through `conclave-op`, which ties this repo's tooling to Conclave, against the 2026-11-10 rule that Bosun stays small and unconnected and the 2026-11-12 decision that its tooling is its own. The question: does Bosun still want its own scheduled checks? Recommended answer: no, withdraw this section and `§9`, and take the `bosun-pr-merge` task out of `§7`, since the only pin that is Bosun's alone is `python-version` in `.github/workflows/plan.yml`, which a reviewer sees on every Python release. The alternative is one Bosun check of that workflow pin, written here, with no email. Reversing a withdrawal costs re-filing three small sections. Nothing was built.

**Phase:** 0
**Needs:** owner-decision
**Surface:** no

**Build order:**

1. Fill `.conclave/automations/tasks.json`. Done when: `run.py` validates the tasks with `folder` set to `\Bosun\`.
2. Port the pin checks and repoint `alert.py`. Done when: a failed task writes `last.json` and the alert path is exercised in a test.

- [ ] Port `check-python-pin.py` and `check-visual-studio.py` from `R:\GitHub\Recto\scripts`, and add `check-dotnet-pin.py` from `R:\GitHub\Isotone\scripts`. Done when: each exits 0 on a current pin and nonzero on a stale fixture.
- [ ] Set `bosun-python-pin`, `bosun-visual-studio` and `bosun-dotnet-pin` in `.conclave/automations/tasks.json`. Done when: the validator in `run.py` accepts it. Cheaper substitute: a task whose name lacks the folder prefix.
- [ ] Point `.conclave/automations/alert.py` at the operator's address, with the key source unchanged. Done when: `scripts/tests/test_automations.py` covers the failure path with a stub sender, sending no real mail. Cheaper substitute: sending a real test email from a test.
- [ ] Commit: `automations: pin checks on a schedule`

**Test checkpoint:** `python -m unittest discover -s scripts/tests -p test_automations.py` exits 0, and fails if the change is reverted.

## 6. Gates: native build checks and check-all

> **Started:** 2026-11-12T04:47:02Z

One command should tell a contributor, and a run, whether the tree is fit to commit, and the native build should prove the flags it claims, not only compile.

**Corrected 2026-11-12:** rewritten before the build, for four reasons. The section bundled three things; the weakening scan is its own job with its own reader (the reviewer) and moved to `§13`, which adds it to the gate. The scripts are written here for this repo, not copied from Isotone: the owner decided on 2026-11-12 that Bosun's tooling is its own, and Isotone's checks pin another standard and another build. Measured the same day: every C++ compile in `build/compile_commands.json` carries `-std:c++20` (from `CMAKE_CXX_STANDARD 20`), and `CMakeLists.txt` sets no control-flow guard, so "the built `bosun.exe` passes" needs the build hardened first. And the executable's flags are read from the file itself, where Isotone reads `dumpbin` text, so the check runs in a plain shell and in CI. Also found: `tests/` holds 15 PowerShell tests and `.github/workflows/build.yml` runs 12, with nothing saying which are left out on purpose.

**Corrected 2026-11-12 (plan review):** the plan review in `docs/reviews/00-workspace/D00-T01-s6.md` returned five gaps, all planned in below. Every step runs in its own job object through `Measure-Job` (`scripts/lib/job-measure.ps1`, built in `§12`), so a step that fails, times out or is interrupted leaves nothing running, with or without `bosun`. `tests/queue_test.ps1` runs with `-SkipWsl` by default, as CI runs it, so only its WSL half is left out. The WSL side (`tests/test_bosun_log.py`, which needs Linux, the full queue test and the shell parse) runs with `-Wsl`, never by default, because a gate must not start a stopped WSL machine unasked. The standard check refuses input it cannot judge, and the runner's listing, order and missing-program paths are tested.

**Corrected 2026-11-12 (build):** the three PowerShell tests the workflow left out (`campaign_admin_test`, `campaign_hold_test`, `stage_wsl_test`) need no special host and pass here, so they run in the gate and in CI, and the gate's table of what a default run leaves out holds only the WSL side. The stale-plan case in `tests/check_all_test.ps1` uses a small TODO tree of its own, since the workflow's checkout is shallow and this repository's stamped plan needs its history.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Harden the native build and check it. Done when: `bosun.exe`, `bosun-logger.exe` and the test executables carry control-flow guard, and a check proves it from the files.
2. Add `scripts/check-all.ps1` as the one-command gate. Done when: it runs every gate in order, stops at the first failure and names it.

- [x] Add `/guard:cf` to the compile and link options and `/CETCOMPAT` to the link options in `CMakeLists.txt`, for every target. Done when: `pwsh scripts/build.ps1 -Fresh` builds with warnings as errors, `build/Release/test_core.exe` passes, and `bin/bosun.exe status` still runs. Cheaper substitute: the compile option alone, which emits the checks and leaves the image flag unset.
- [x] Write `scripts/native-pe-check.py <exe or folder>...`, reading each image's PE headers itself (no `dumpbin`): it requires control-flow guard, ASLR with high entropy and NX in the DLL characteristics, and the CET-compatible bit in the extended DLL characteristics debug entry of an x64 image. Exit 0 when every image holds, 1 naming each image and what it lacks, 2 when a path is missing, is not a PE image, or a folder holds no `.exe`. Done when: `scripts/tests/test_native_checks.py` builds minimal PE images in memory with and without each flag and gets the right verdict for each, and a truncated or non-PE file exits 2 without a traceback. Cheaper substitute: skipping an absent build without saying so.
- [x] Write `scripts/native-std-check.py [<build folder>]`, reading `compile_commands.json`: every C++ compile carries exactly one standard switch, in either spelling (`/std:` or `-std:`), and it is the one `CMAKE_CXX_STANDARD` in `CMakeLists.txt` gives (`c++20`), never `c++latest`. Exit 0, 1 naming the source file and its switches, or 2 with the reason when it cannot judge: the database is missing, is not JSON, is not a list of records with a command and a file, holds no C++ compile at all, or `CMakeLists.txt` names no single `CMAKE_CXX_STANDARD`. Done when: the test covers a good database, a doubled switch, a wrong one, a missing one and `c++latest`, reads the pinned standard from a fixture `CMakeLists.txt`, and gets exit 2 for an empty database, malformed JSON, a record with no command and a `CMakeLists.txt` with no standard or two. Cheaper substitute: passing an empty database, where every compile holds because there is none.
- [x] Create `scripts/check-all.ps1`, running in this order and stopping at the first failure with the step's name, its command and its exit code: the plan (`python scripts/todo-graph.py plan --check`, which validates the graph first), the agent tiers (`node scripts/verify-agent-tiers.mjs`), the native build (`pwsh scripts/build.ps1`), the standard check, the PE check on `build/Release`, the C++ tests (`build/Release/test_core.exe`), each PowerShell test in `tests/` that needs no special host (`tests/queue_test.ps1` with `-SkipWsl`), and the Python suite (`python scripts/run-tests.py`). The build and the Python suite go through `bosun run --` when `bosun` is on `PATH`. `-List` prints the steps and runs none, `-Only <name>...` runs the named ones, and `-Wsl` adds the WSL side: `python3 -B tests/test_bosun_log.py` in WSL, the queue test without `-SkipWsl`, and `bash -n` over the scripts in `wsl/`. Done when: it exits 0 on this tree and prints one line per step with its seconds. Cheaper substitute: running every step and burying the first failure under the rest.
- [x] Run every step of `scripts/check-all.ps1` through `Measure-Job` with a time limit per step (`-StepMinutes`, default 30), so the step and everything it started end when it fails, passes its limit, or the gate is interrupted. Done when: `tests/check_all_test.ps1` shows a stand-in step that starts a sleeping child and then fails, and one that starts a child and hangs past a short limit, each named with what happened, with the child gone (its process id read from a file it wrote) and no later step run. Cheaper substitute: `Start-Process -Wait`, which leaves a failed step's children running.
- [x] Name in `scripts/check-all.ps1` the tests it leaves out of the default run and why (they need an elevated shell or a signed-in agent, or belong to `-Wsl`), with the command that runs each, in one table the script reads and `-List` prints. Done when: `tests/check_all_test.ps1` fails when a file matching `tests/*_test.ps1` is neither run nor listed there with a reason, and when `.github/workflows/build.yml` runs fewer of them than the gate does without the same reason.
- [x] Add `tests/check_all_test.ps1`: with stand-in steps, a run where every step passes runs each registered step once, in the order `-List` prints, and exits 0; `-List` runs none of them; a failing step stops the run, is named with its command and exit code, and nothing after it runs; a step whose program is not on `PATH` is named as that and stops the run the same way; `-Only` runs just what it names and refuses a name that is not a step; and in a temporary copy of the plan tooling with a stale plan, `-Only plan` exits nonzero. Done when: it passes, fails if the gate carries on past a failure, and is in the Windows job of `.github/workflows/build.yml` with `scripts/tests/test_native_checks.py` covered by the Python suite.
- [x] Run the two native checks in the Windows job of `.github/workflows/build.yml` after the build. Done when: the steps are listed and the job is green.
- [x] Point `AGENTS.md` and `CONTRIBUTING.md` at `pwsh scripts/check-all.ps1` as the way to verify, replacing the list of separate commands. Done when: both say so, and `AGENTS.md` no longer says "once `D00 T01 §6` has landed".
- [x] Commit: `gates: native build checks and check-all`

**Test checkpoint:** `pwsh scripts/check-all.ps1` exits 0 on a clean tree and exits nonzero, naming the plan step, with a deliberately stale plan; `pwsh tests/check_all_test.ps1` and `python -m unittest discover -s scripts/tests -p test_native_checks.py` exit 0, and the second fails if the PE check stops requiring control-flow guard.

> **Verified:** 2026-11-12 | §6 | 43f64347cbf56d04bd4078139b19512bb3852b9b
> **Review:** `docs/reviews/d00-t01-s6.json`

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

## 12. Build and dev tool stack measured and tuned

> **Started:** 2026-11-11T23:45:36Z

Filed 2026-11-12 on the owner's request to optimise the build and dev tools for memory and speed. Measured state: `scripts/build.ps1` uses Ninja Multi-Config with MSVC (`CMakePresets.json`); on 2026-11-10 it was no faster than the Visual Studio generator for this tree (nothing to do 4 s, one changed file 11 s, full build 15 s), dominated by loading the compiler environment and compiling `src/app.cpp`. `sccache` (0.19.0) and `lld-link` (LLD 22.1.3, from the Visual Studio LLVM component) are installed and unused. The rule from `AGENTS.md` applies: measure first, change the owning script, prove the gain, and keep only what wins.

**Corrected 2026-11-12:** three claims changed before the build. Peak memory comes from a Windows job object the measuring script puts each command in (`PeakJobMemoryUsed`, the peak committed memory of the command and everything it starts): `bosun report` gives peaks per repo and minute, not per command. The .NET build defaults item moved to `D00 T02 §4`, since it cannot be measured before `D00 T02 §1` creates the solution and this section does not wait on that. A test of the measuring script was added, since the checkpoint as written could not fail on a wrong table.

**Corrected 2026-11-12 (plan review):** the plan review in `docs/reviews/00-workspace/D00-T01-s12.md` returned eight gaps, all planned in below: a controlled starting state and repeated samples, an `sccache` server the measurement owns, tests that prove child memory, time and exit codes, proof that each candidate was really in use and a fallback where its tool is missing, a safe contract for the cached developer environment, persistence failures and a render-only recovery, a timeout on every step, and queue admission through `bosun run --` outside the timed interval.

**Corrected 2026-11-12 (build):** what the build found, against the items as planned. `sccache` and `lld-link` were measured and dropped, so their switches, the `/Z7` setting and the missing-tool notices are not in `scripts/build.ps1`; the numbers and reasons are in `docs/build-performance.md`. The cached environment won and is what `scripts/build.ps1` now does, with no switch. Its key and compiler-folder cases are in `tests/dev_env_test.ps1`, and `tests/build_script_test.ps1` (new) runs the real script against the real Visual Studio, not `tests/build_cache_test.ps1` as first written. Two items were added: a configure that fails on an existing cache is tried fresh once (a plain build failed that way twice on the tree earlier sessions left, and `-Fresh` cured it), and `-DecideOnly` records a decision on a stored label once its numbers are compared. `lld-link` was also measured beside the kept cache (`env-cache-lld`), since its first run against the baseline alone was inside the spread between runs.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Build the measuring tool and its test. Done when: `pwsh tests/measure_build_test.ps1` exits 0.
2. Measure a baseline. Done when: the median wall time and peak memory of a no-op, one-file and clean build, and of `python scripts/run-tests.py`, are in `docs/build-performance.md` with the machine, tool versions and date.
3. Try each candidate against that baseline, then measure the kept ones together. Done when: each is recorded as kept or dropped with its numbers, and the label `final` holds the combination that stays.

- [x] Write `scripts/lib/job-measure.ps1` with `Measure-Job -FilePath -ArgumentList -WorkingDirectory -TimeoutSeconds`: start the command suspended, assign it to a new job object set to end every process in it when the job closes, resume it, and return its exit code, wall seconds, `PeakJobMemoryUsed` in MB and whether it timed out; at the timeout the job is terminated. Done when: `tests/measure_build_test.ps1` shows a command's own exit code 7 is returned and a 2 second sleep reports between 2 and 10 seconds. Cheaper substitute: `Measure-Command` and the parent's working set, which misses every child process.
- [x] Prove child memory in `tests/measure_build_test.ps1`: a small parent whose child alone allocates 300 MB reports at least 300 MB, and the same parent with no allocation reports under 150 MB. Done when: both bounds are asserted, so neither a parent-only reading nor a constant passes.
- [x] Prove cleanup in `tests/measure_build_test.ps1`: a timed-out command with a sleeping child, and a command whose parent exits while its child sleeps on, each leave no process from the job once `Measure-Job` returns. Done when: the child's process id is read from a file it wrote and `Get-Process -Id` finds nothing. Cheaper substitute: stopping only the direct child.
- [x] Fail loudly when the command cannot start or cannot be put in the job, in `Measure-Job`: throw naming the command and the Win32 error, ending anything already started. Done when: a test with a file that does not exist gets that error and no result.
- [x] Write `scripts/measure-build.ps1 -Label <name> -Decision <baseline|kept|dropped> [-Note <text>] [-Tests] [-Samples 3] [-TimeoutSeconds 1800]`: one untimed preparation build first, then `-Samples` timed runs each of the no-op build, the build after touching `src/app.cpp`, and the `-Fresh` build of `scripts/build.ps1` (and, with `-Tests`, one timed `python scripts/run-tests.py`), each through `Measure-Job` with the timeout. Done when: a run prints each sample and the median per step, and the no-op step is refused (exit 1) when its build compiled anything, read from the build output. Cheaper substitute: one sample with no preparation build, which times a cold configure as a no-op.
- [x] Queue the measurement in `scripts/measure-build.ps1`: when `bosun` is on `PATH` and the script is not already under it, re-run itself through `bosun run --` so the wait for a slot is outside every timed interval, and run directly when `bosun` is absent, saying so. Done when: `tests/measure_build_test.ps1` shows, with a fake `bosun` first on `PATH`, that the script is started through it once and not again inside it.
- [x] Store the result in `docs/build-performance.json` from `scripts/measure-build.ps1` (temporary file, then replace, then read back): under the label, its decision, note, UTC date, machine name, processor, memory, the versions of `cmake`, `ninja`, `cl` and of `sccache` and `lld-link` when used, the configuration in force (launcher, linker, environment cache on or off), every sample and the medians. Done when: one run on this machine stores the `baseline` label and the read-back equals what was written. Cheaper substitute: timing by hand and typing the numbers.
- [x] Refuse to store a failed or timed-out step, and refuse a `docs/build-performance.json` that does not parse or lacks its `schema_version`, in `scripts/measure-build.ps1`: exit 1 naming the step and its exit code, or the file and the parse error, writing nothing. Done when: `tests/measure_build_test.ps1` shows a failing step, a timed-out step and a malformed file each leave the file byte-identical. Cheaper substitute: recording the time of a failed build as a result.
- [x] Render `docs/build-performance.md` from `docs/build-performance.json` in `scripts/measure-build.ps1` (temporary file, then replace), one table per label with its decision, note, configuration and tool versions, wall time in seconds and peak memory in MB, never edited by hand; `-RenderOnly` renders without measuring, and a render that fails after the data was stored exits 1 naming `-RenderOnly` as the recovery. Done when: `tests/measure_build_test.ps1` renders a two-label fixture and finds each label's decision, note and exact numbers with their units, fails when a label or a row is left out, and shows `-RenderOnly` rebuilding a deleted document. Cheaper substitute: appending Markdown rows, which lets the document and the data disagree.
- [x] Add `tests/measure_build_test.ps1` to the Windows job in `.github/workflows/build.yml`. Done when: the step is listed beside `tests/build_cache_test.ps1`.
- [x] Try `sccache` in `scripts/build.ps1`, passed as `-DCMAKE_CXX_COMPILER_LAUNCHER` with `/Z7` debug info only when `sccache` is found, and a plain build with a one-line notice when it is not. The measurement gives it its own server (`SCCACHE_SERVER_PORT` on a free port and `SCCACHE_DIR` under `build/`), started inside the measured job so its compiler processes are counted and it ends with the job; no other server is stopped. Done when: a cache-warm `-Fresh` build is measured under the label `sccache`, `sccache --show-stats` read inside the job shows cache hits above zero for it, and it is kept only if its median is faster than the baseline's with `build/Release/test_core.exe` passing; a dropped candidate is reverted and recorded as dropped with its numbers. Cheaper substitute: the user's shared server, whose work the job does not count.
- [x] Try caching the Visual Studio developer environment in `scripts/lib/dev-env.ps1`, used by `scripts/build.ps1`: store only the variables `Enter-VsDevShell` added or changed, with `PATH` kept as the entries it prepended, in `build/dev-env.json` (temporary file, then replace), keyed on the `vswhere` installation path and version; replay sets those variables and prepends those entries to the current `PATH`. A missing, malformed or unwritable cache, or one whose key or compiler path no longer holds, falls back to loading the environment and rewrites the cache. Done when: `tests/dev_env_test.ps1` shows, each in a fresh `pwsh` process with a fake developer shell, reuse, a changed key, a malformed file and a stored compiler folder that no longer exists, and that a variable the shell did not set is not restored; the no-op build is measured under the label `env-cache`, and the faster way stays. Cheaper substitute: saving the whole environment, which replays stale and unrelated variables.
- [x] Try `lld-link` for the Release link, passed by `scripts/build.ps1` as `-DCMAKE_LINKER` only when `lld-link` is found, with a plain link and a one-line notice when it is not. Done when: it is measured under the label `lld-link`, `ninja -C build -f build-Release.ninja -t commands` names `lld-link` for the link steps, and it stays only if its median is faster than the baseline's with `build/Release/test_core.exe` passing; a dropped candidate is reverted and recorded with its numbers.
- [x] Measure the kept candidates together under the label `final`, and prove the fallback: add `tests/dev_env_test.ps1` to the Windows job in `.github/workflows/build.yml`, where the hosted runner builds without any tool this section did not install. Done when: `final` is stored with its configuration, and the workflow's build step needs no new install step.
- [x] Configure fresh once in `scripts/build.ps1` when configuring from the existing `build/CMakeCache.txt` fails, and fail as before when the fresh configure fails too. Done when: `tests/build_script_test.ps1`, with a fake `cmake`, shows a bad cache configured fresh once and built, a broken project failing after the one fresh try with nothing built, and a failing `-Fresh` not tried again. Cheaper substitute: always configuring fresh, which rebuilds everything on every build.
- [x] Add `-DecideOnly` to `scripts/measure-build.ps1`: record `-Decision` and `-Note` on a label already measured, keeping its numbers, and refuse a label never measured. Done when: `tests/measure_build_test.ps1` shows both. Cheaper substitute: measuring again to change a word.
- [x] Add `tests/build_script_test.ps1` to the Windows job in `.github/workflows/build.yml`. Done when: the step is listed beside `tests/dev_env_test.ps1`.
- [x] Commit: `build: measured tool stack, keeping what wins`

**Test checkpoint:** `pwsh tests/measure_build_test.ps1`, `pwsh tests/dev_env_test.ps1` and `pwsh tests/build_script_test.ps1` exit 0, and fail if `Measure-Job` stops counting child processes, a failed step is stored or the renderer drops a row; `pwsh scripts/measure-build.ps1 -RenderOnly` rewrites `docs/build-performance.md` from the stored data; and `pwsh scripts/build.ps1` followed by `build/Release/test_core.exe` passes with every kept setting.

> **Verified:** 2026-11-12 | §12 | 15dead954809d32f8f25caff791712b5077ef011
> **Review:** `docs/reviews/d00-t01-s12.json`

## 13. Weakening scan over a diff

Split out of `§6` on 2026-11-12. A passing suite cannot tell a correct change from one that deleted the test, skipped it or swallowed the error it checked. The scan reads a diff and names every hunk of those shapes, so the writer answers each one and the reviewer sees them. It is written here, for the kinds of test this repo has (Python `unittest`, PowerShell `Check` scripts, `tests/test_core.cpp` and the workflows), not copied from another repo.

**Corrected 2026-11-12 (plan review):** the plan review in `docs/reviews/00-workspace/D00-T01-s13.md` returned eight gaps, all planned in below. The scan gets a committed-range mode (`--head`), so a candidate is scanned by its own endpoints whatever the working tree holds. Each signal has an identity of its own (the case's name, or the line), so one answer covers one signal. What is committed is enforced and what is not yet committed is only reported, because an answer lives in a commit body and an uncommitted change has none. The report keeps every signal with its answer, answered or not. C++ cases are read as this repository registers them, in the `groups[]` table of `tests/test_core.cpp`. A moved test is matched one to one by its qualified name, and the assertions it carried are counted across the move. The git side is tested in disposable repositories.

**Phase:** 0
**Needs:** none
**Surface:** no

**Build order:**

1. Write the scan and its tests. Done when: a diff that deletes a test is flagged, a plain feature diff is not, and each signal can be answered on its own.
2. Put it where it is read. Done when: `scripts/check-all.ps1` stops on an unanswered signal in what is committed, and the review prompt carries the report.

- [ ] Write `scripts/weakening-scan.py` with two modes. `--base <rev> --head <rev>` scans exactly that committed range. Without `--head` it scans the commits from the base to `HEAD` and then, separately, what is not committed (staged, unstaged and untracked files). The base defaults to the merge base of `HEAD` and `origin/main`, or to `HEAD` when there is no such branch. Done when: `scripts/tests/test_weakening_scan.py`, in disposable repositories, shows a range scanned with `HEAD` ahead of it and a dirty working tree reports only the range's own signals, that staged, unstaged and untracked changes are each seen in the uncommitted part, and that the default base falls back to `HEAD`. Cheaper substitute: always diffing against the working tree, which mixes later commits and unrelated edits into a candidate's scan.
- [ ] Report each signal as `<kind> <path>:<id>`, where the id is the case's name when the signal is about a case and `L<line>` otherwise, for these kinds: `test-file-deleted`; `test-removed` (a Python `def test_` named with its class, a PowerShell `Check '...'` line named by its text, a row of the `groups[]` table in `tests/test_core.cpp` named by its group); `test-disabled` (a `groups[]` row whose function became `nullptr`); `assertion-removed` (more assertion lines removed from a file than added to it: `self.assert`, `CHECK(`, a `Check` line's condition); `skip-added` (`skipTest`, `unittest.skip`, a `-Skip` switch on a test in the gate or the workflow, `continue-on-error`); `ci-step-removed` (a `run:` line gone from a workflow); `warning-relaxed` (`CMAKE_COMPILE_WARNING_AS_ERROR`, `/W4` or `TreatWarningsAsErrors` removed, a `noqa` or `type: ignore` added); and `error-swallowed` (an `except` whose body is only `pass`, an empty `catch`). Done when: the test has a diff for each kind that is flagged with the right identity, and a plain feature diff that adds code and tests and is clean.
- [ ] Match a moved test one to one by its qualified name: a case removed in one place and added once elsewhere in the same diff is a move, not a removal, and the assertions removed with it are weighed against those added with it. Done when: a renamed test file and a case moved between files are clean; two classes with a method of the same name, one of them deleted, is flagged; and a move that drops an assertion is flagged as `assertion-removed`.
- [ ] Accept an answer per signal from the commit bodies of the scanned range, as `Weakening scan: <kind> <path>:<id> -- <why>`, and print every signal with its state: `answered` with the reason, or `unanswered`. Exit 0 when every committed signal is answered, 1 when one is not, and 2 with the reason when the range or a file in it cannot be read. Signals in what is not committed are printed under their own heading, as needing an answer in the commit body, and never change the exit code. Done when: the test covers one of two same-kind signals in one file answered and the other not, an answer with no reason (which does not count), a fully answered range whose report still lists each signal with its reason, an uncommitted signal that exits 0, an invalid revision that exits 2, and a diff git cannot produce that exits 2 and is never read as empty. Cheaper substitute: a flag that silences the scan.
- [ ] Add the scan to `scripts/check-all.ps1` after the plan step, and update the order `tests/check_all_test.ps1` asserts. Done when: that test, in a disposable repository holding the gate and the scan, shows `-Only weakening` exit 1 on a commit that removes a test with no answer and exit 0 once the commit body answers it. Cheaper substitute: checking only that `-List` names the step.
- [ ] Add a line to `docs/review-round-prompt.md` and `docs/completion-review-prompt.md` telling the writer to paste the scan's report for the candidate range (`python scripts/weakening-scan.py --base <BASE> --head <TIP>`). Done when: both templates carry it.
- [ ] Run it over this repository's history from `858a85d` to `HEAD`. Done when: every signal it reports is either a real removal, listed under this section with why it was right, or a false signal fixed in the scan with a test.
- [ ] Commit: `gates: weakening scan`

**Test checkpoint:** `python -m unittest discover -s scripts/tests -p test_weakening_scan.py` and `pwsh tests/check_all_test.ps1` exit 0, and the first fails if the rule for a removed test is taken out or an answer for one signal is allowed to cover another.

## Verification

- `pwsh scripts/check-all.ps1` passes.
- `python scripts/todo-graph.py validate` and `python scripts/todo-graph.py plan --check` pass.
